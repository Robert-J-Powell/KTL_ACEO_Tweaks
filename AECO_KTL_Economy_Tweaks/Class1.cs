using AirportCEOModLoader.SaveLoadUtils;
using AirportCEOModLoader.WatermarkUtils;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// References required:
//   Unity.TextMeshPro.dll  (direct reference required; used by InjectOneStandSlider)
//   AirportCEOModLoader.dll (D:\SteamLibrary\steamapps\workshop\content\673610\3109136766\plugins\AirportCEOModLoader.dll)

namespace ACEO_KTL_Economy_Tweaks
{
    // =========================================================================
    // ACEO_KTL_Economy_Tweaks  v1.2.4
    //
    // Configurable multipliers for sixteen airport income fee categories:
    //   2. Runways      — small, medium, large runway usage fees
    //   3. Stands       — small/GA, medium, large stand parking fees
    //   4. Aircraft Svc — hangar repair, cabin cleaning, de-icing, catering
    //   5. Handling     — baggage and passenger handling fees
    //   6. Parking      — short-term and long-term car parking fees
    //   7. Fuel         — Avgas 100LL and Jet A1 fuel charges
    //
    // Excluded: bathroom fee (vanilla £0), vending markup, staff wages,
    //   contractors (KTL Contractor Tweaks), supply purchases, loans.
    //
    // Language: English only. Injected stand slider labels are not localised;
    //   the vanilla LocalizedTextMeshPro component is disabled on cloned rows.
    //
    // Architecture:
    //   Base fee cache seeded once from EconomyData vanilla defaults (= FeeMax/2).
    //   scaledFee = base × multiplier; cache never mutated by sliders or save/load.
    //   Rating curves rebuilt after each application so panel colour bands scale correctly.
    //   Sliders written via inner Unity Slider reflection to bypass OnValueChanged chain.
    //   Per-slider LastAppliedMultiplier guards player-dragged values from hourly resets.
    //
    // =========================================================================
    // PATCH HISTORY
    // =========================================================================
    //
    // v1.2.4 — [DIAG] LogStandSliderChildren added to InjectStandSliders. Fires once on
    //           SmallStandParkingFee source row to identify tooltip child name and component
    //           type. No version bump — diagnostic only.
    //         — Label fix: LocalizedTextMeshPro component disabled on cloned stand slider
    //           rows and source Small/GA row before setting label text. This component was
    //           overwriting our text with the vanilla localised string after every write.
    //           Diagnostic (LogSliderDescriptionComponents) removed; cause confirmed.
    //           English-only language note added to mod header.
    //
    // v1.2.3 — ForceMeshUpdate() added to label writes. Diagnostic added to identify
    //           localisation component overwriting labels (confirmed: LocalizedTextMeshPro).
    //
    // v1.2.2 — SliderDef.IsInjected flag; ResolveSlider skips reflection for injected
    //           defs. Label rewrite tries TextMeshProUGUI then TextMeshPro as fallback.
    //
    // v1.2.1 — Injected sliders seeded from EconomyData. Small/GA label corrected.
    //           Behaviour logging added to InjectOneStandSlider.
    //
    // v1.2.0 — Medium/large stand fees promoted to in-game sliders. Cloned from
    //           SmallStandParkingFee at runtime. Removed from "always apply" block.
    //
    // v1.1.x — Slider event chain rewrite; player drag persistence; LoadPanel guard.
    //
    // v1.0.6 — Initial public release.
    //
    // =========================================================================

    // SliderDef — one entry in the Fees panel slider table.
    //   FieldName/VanillaMin/VanillaMax/DisplayStep — resolved via reflection at init.
    //   GetMultiplier — delegate returning the current snapped multiplier for this fee.
    //   LastAppliedMultiplier — guards player-dragged values from hourly resets (-1f = force write).
    //   IsInjected — marks runtime-injected sliders; ResolveSlider skips reflection for these
    //                to preserve the manually-assigned Slider reference set by InjectOneStandSlider.
    internal sealed class SliderDef
    {
        public string FieldName { get; }
        public float VanillaMin { get; }
        public float VanillaMax { get; }
        public float DisplayStep { get; }
        public System.Func<float> GetMultiplier { get; }
        public ValueSliderContainerUI Slider { get; set; }
        public bool IsInjected { get; set; }

        private Slider _innerSlider;

        public float LastAppliedMultiplier = -1f;

        public SliderDef(string fieldName, float vanillaMin, float vanillaMax, float displayStep, System.Func<float> getMultiplier)
        {
            FieldName = fieldName;
            VanillaMin = vanillaMin;
            VanillaMax = vanillaMax;
            DisplayStep = displayStep;
            GetMultiplier = getMultiplier;
        }

        public void ResolveSlider(FeesPanelUI feesPanel)
        {
            // Injected sliders are assigned directly in InjectOneStandSlider; reflection
            // cannot find them on FeesPanelUI and would null out the reference if attempted.
            if (IsInjected) return;
            FieldInfo field = AccessTools.Field(typeof(FeesPanelUI), FieldName);
            Slider = field?.GetValue(feesPanel) as ValueSliderContainerUI;
            _innerSlider = null; // invalidate cache when panel instance changes
        }

        // Returns the inner Unity Slider, caching it after first resolution.
        // Returns null if the wrapper or its inner field cannot be found.
        public Slider GetInnerSlider()
        {
            if (_innerSlider != null) return _innerSlider;
            if (Slider == null) return null;
            FieldInfo f = AccessTools.Field(typeof(ValueSliderContainerUI), "slider");
            _innerSlider = f?.GetValue(Slider) as Slider;
            return _innerSlider;
        }
    }

    // =========================================================================
    // GrandMasterPlugin
    // =========================================================================
    [BepInPlugin("com.ktl.aceo.tweaks.economy", "KTL Economy Tweaks", "1.2.4")]
    public class GrandMasterPlugin : BaseUnityPlugin
    {
        // Config entries — one per fee category multiplier. Bound with AcceptableValueRange.
        public static ConfigEntry<bool> EnableEconomy;

        // Runway fees. Vanilla: Small £500, Medium £1,500, Large £2,500.
        public static ConfigEntry<float> SmallRunwayIncome;
        public static ConfigEntry<float> MediumRunwayIncome;
        public static ConfigEntry<float> LargeRunwayIncome;

        // Stand parking fees. Vanilla: Small/GA £400, Medium £500, Large £600.
        // All three have in-game sliders; medium and large are injected at runtime.
        public static ConfigEntry<float> SmallStandIncome;
        public static ConfigEntry<float> MediumStandIncome;
        public static ConfigEntry<float> LargeStandIncome;

        // Aircraft service fees. Vanilla: Hangar Repair £600, Cabin Cleaning £2.50, De-icing £10/L, Catering £5/meal.
        public static ConfigEntry<float> HangarRepairIncome;
        public static ConfigEntry<float> CabinCleaningIncome;
        public static ConfigEntry<float> DeicingFluidIncome;
        public static ConfigEntry<float> CateringMealIncome;

        // Handling fees. Vanilla: Baggage £10, Passenger £15.
        public static ConfigEntry<float> BaggageHandlingIncome;
        public static ConfigEntry<float> PassengerHandlingIncome;

        // Car parking fees. Vanilla: Short-term £10/hr, Long-term £200/day.
        public static ConfigEntry<float> ShortTermParkingIncome;
        public static ConfigEntry<float> LongTermParkingIncome;

        // Aviation fuel prices. Vanilla: Avgas 100LL £2.00/L, Jet A1 £0.50/L.
        public static ConfigEntry<float> Avgas100LLIncome;
        public static ConfigEntry<float> JetA1Income;

        // BaseFeeCacheInitialized — guards multiplier application until vanilla values are captured.
        // IsSyncingFeesPanelDisplay — suppresses our own OnValueChanged listeners during programmatic writes.
        public static bool BaseFeeCacheInitialized;
        public static bool IsSyncingFeesPanelDisplay;

        // Base fee cache — vanilla defaults (FeeMax/2) seeded once per session.
        // Never modified by sliders, save/load, or multiplier writes.
        public static float BaseSmallAircraftRunwayFee;
        public static float BaseMediumAircraftRunwayFee;
        public static float BaseLargeAircraftRunwayFee;
        public static float BaseSmallStandParkingFee;
        public static float BaseMediumStandParkingFee;
        public static float BaseLargeStandParkingFee;
        public static float BaseHangarAircraftRepairFee;
        public static float BaseAircraftCabinCleaningCost;
        public static float BaseDeicingFluidCost;
        public static float BaseCateringMealCost;
        public static float BaseBaggageHandlingFee;
        public static float BasePassengerHandlingFee;
        public static float BaseShortTermParkingFee;
        public static float BaseLongTermParkingFee;
        public static float BaseAvgas100LLFuelCost;
        public static float BaseJetA1FuelCost;

        internal static new ManualLogSource Logger;

        private bool _isNormalizingConfig; // re-entrancy guard for config snap handlers
        private static readonly HashSet<int> HookedFeesPanelInstanceIds = new HashSet<int>(); // prevents duplicate listener attachment on scene reload
        public static readonly HashSet<int> InjectedFeesPanelInstanceIds = new HashSet<int>(); // prevents duplicate stand slider injection per panel instance

        // Slider table — maps each Fees panel slider to vanilla min/max, step, and multiplier getter.
        // VanillaMin/Max from FeesPanelUI.cs and EconomyData.cs (FeeMin/FeeMax constants):
        //   Small Runway £100/£1,000  Medium Runway £500/£3,000  Large Runway £1,000/£5,000
        //   Small/GA Stand £50/£800  Medium Stand £100/£1,000  Large Stand £100/£1,200
        //   Hangar Repair £100/£1,200
        //   Avgas 100LL £0.50/£4/L  Jet A1 £0.10/£1/L  Catering £1/£10/m  Cabin Cleaning £0.50/£5
        //   De-icing £2/£20/L  Passenger Hdl £5/£30  Baggage Hdl £3/£20
        //   Short Parking £2/£20  Long Parking £50/£400
        // Medium/large stand sliders are injected at runtime; their GameObjects are clones
        // of SmallStandParkingFee parented to AircraftStandUsage.
        private static SliderDef[] _sliderTable;

        // Tracks injected stand slider GameObjects so they can be destroyed on session reset.
        private static readonly List<GameObject> _injectedStandSliders = new List<GameObject>();

        private static void BuildSliderTable()
        {
            _sliderTable = new[]
            {
                //                                                  min     max     step
                new SliderDef("smallAircraftRunwaySlider",          100f,  1000f,   50f,   () => GetRunwayStandValue(SmallRunwayIncome)),
                new SliderDef("mediumAircraftRunwaySlider",         500f,  3000f,   50f,   () => GetRunwayStandValue(MediumRunwayIncome)),
                new SliderDef("largeAircraftRunwaySlider",         1000f,  5000f,  100f,   () => GetRunwayStandValue(LargeRunwayIncome)),
                new SliderDef("smallStandParkingSlider",             50f,   800f,   10f,   () => GetRunwayStandValue(SmallStandIncome)),
                new SliderDef("mediumStandParkingSlider",           100f,  1000f,   50f,   () => GetRunwayStandValue(MediumStandIncome)),
                new SliderDef("largeStandParkingSlider",            100f,  1200f,   50f,   () => GetRunwayStandValue(LargeStandIncome)),
                new SliderDef("hangarAircraftRepairSlider",         100f,  1200f,   20f,   () => GetServiceValue(HangarRepairIncome)),
                new SliderDef("aviationFuelAvgas100LLSlider",       0.5f,    4f,   0.1f,  () => GetAvgasValue(Avgas100LLIncome)),
                new SliderDef("aviationFuelJetA1Slider",            0.1f,    1f,   0.1f,  () => GetJetA1Value(JetA1Income)),
                new SliderDef("cateringMealSlider",                  1.0f,  10f,   0.1f,  () => GetServiceValue(CateringMealIncome)),
                new SliderDef("aircraftCabinCleaningSlider",         0.5f,   5f,   0.1f,  () => GetServiceValue(CabinCleaningIncome)),
                new SliderDef("deicingFluidSlider",                  2.0f,  20f,   0.1f,  () => GetServiceValue(DeicingFluidIncome)),
                new SliderDef("passengerHandlingSlider",              5f,   30f,    1f,   () => GetHandlingValue(PassengerHandlingIncome)),
                new SliderDef("baggageHandlingSlider",                3f,   20f,    1f,   () => GetHandlingValue(BaggageHandlingIncome)),
                new SliderDef("shortTermParkingSlider",               2f,   20f,    1f,   () => GetShortTermParkingValue(ShortTermParkingIncome)),
                new SliderDef("longTermParkingSlider",               50f,  400f,    1f,   () => GetLongTermParkingValue(LongTermParkingIncome)),
            };
        }

        void Awake()
        {
            Logger = base.Logger;

            BindConfig();
            BuildSliderTable();
            NormalizeAndSaveConfigValues();
            RegisterLiveConfigHandlers();

            var harmony = new Harmony("com.ktl.aceo.tweaks.economy");
            harmony.PatchAll(typeof(EconomyFeeRefreshPatch));
            harmony.PatchAll(typeof(FeesPanelInitPatch));
            harmony.PatchAll(typeof(FeesPanelDisplayPatch));
            harmony.PatchAll(typeof(AirportRatingManagerInitCurvesPatch));

            Logger.LogInfo("KTL Economy Tweaks 1.2.4 loaded.");
        }

        // Start: registers watermark and event hooks for save load / new game
        // to re-seed the base cache from fresh EconomyData each session.
        void Start()
        {
            WatermarkUtils.Register(new WatermarkInfo("KTL-ET", "1.2.4", true));

            EventDispatcher.EndOfLoad += OnSaveLoaded;
            EventDispatcher.NewGameStarted += OnNewGame;
        }

        // Session lifecycle: invalidate cache, clear hooked panel set, destroy any
        // injected stand slider GameObjects, reset slider multiplier cache, then
        // re-seed and apply multipliers immediately.
        private static void OnSaveLoaded(SaveLoadGameDataController _)
        {
            BaseFeeCacheInitialized = false;
            HookedFeesPanelInstanceIds.Clear();
            InjectedFeesPanelInstanceIds.Clear();
            DestroyInjectedSliders();
            ResetSliderMultiplierCache();

            EconomyData data = GetEconomyData();
            if (data != null)
            {
                EnsureBaseFeeCacheInitialized(data);
                ApplyEconomyFeeMultipliers(data, "OnSaveLoaded");
            }
        }

        private static void OnNewGame(SaveLoadGameDataController _)
        {
            BaseFeeCacheInitialized = false;
            HookedFeesPanelInstanceIds.Clear();
            InjectedFeesPanelInstanceIds.Clear();
            DestroyInjectedSliders();
            ResetSliderMultiplierCache();

            EconomyData data = GetEconomyData();
            if (data != null)
            {
                EnsureBaseFeeCacheInitialized(data);
                ApplyEconomyFeeMultipliers(data, "OnNewGame");
                Logger.LogInfo("New game — base fees seeded from vanilla, multipliers applied.");
            }
        }

        // Destroys any previously injected stand slider GameObjects and clears the
        // SliderDef.Slider references for the injected entries so they are re-injected
        // fresh on the next panel open.
        private static void DestroyInjectedSliders()
        {
            foreach (var go in _injectedStandSliders)
                if (go != null) UnityEngine.Object.Destroy(go);
            _injectedStandSliders.Clear();

            // Null out cached Slider refs for the injected SliderDefs so ResolveSlider
            // doesn't find stale references after the GameObjects are destroyed.
            if (_sliderTable == null) return;
            foreach (var def in _sliderTable)
            {
                if (def.FieldName == "mediumStandParkingSlider" || def.FieldName == "largeStandParkingSlider")
                    def.Slider = null;
            }
        }

        // InjectStandSliders — clones SmallStandParkingFee twice into AircraftStandUsage,
        // renames the SliderDescription label child, seeds the initial value from live
        // EconomyData, and stores the ValueSliderContainerUI reference directly onto the
        // matching SliderDef entries in _sliderTable.
        // Called once per panel instance from FeesPanelInitPatch via the hooked instance guard.
        // Injected GameObjects are tracked in _injectedStandSliders for session-reset cleanup.
        public static void InjectStandSliders(FeesPanelUI feesPanel)
        {
            Transform standUsage = feesPanel.transform.Find("AircraftStandUsage");
            Transform sourceRow = feesPanel.transform.Find("AircraftStandUsage/SmallStandParkingFee");
            if (standUsage == null || sourceRow == null)
            {
                Logger.LogWarning("[v1.2.4] AircraftStandUsage or SmallStandParkingFee not found — stand sliders not injected.");
                return;
            }

            // DIAGNOSTIC — logs all direct children of SmallStandParkingFee and their
            // component type names to identify the tooltip child and component type.
            // To be removed once tooltip child/component are confirmed.
            LogStandSliderChildren(sourceRow);

            // Correct the source slider's label to "Small / GA".
            // LocalizedTextMeshPro is disabled first to prevent it overwriting our text.
            // This mod is English-only; localisation is not supported for injected labels.
            Transform sourceDesc = sourceRow.Find("SliderDescription");
            if (sourceDesc != null)
            {
                DisableLocalizedTextMeshPro(sourceDesc);

                var srcUGUI = sourceDesc.GetComponent<TextMeshProUGUI>();
                if (srcUGUI != null)
                {
                    srcUGUI.text = "Small / GA";
                    srcUGUI.ForceMeshUpdate();
                }
                else
                {
                    var srcTmp = sourceDesc.GetComponent<TextMeshPro>();
                    if (srcTmp != null)
                    {
                        srcTmp.text = "Small / GA";
                        srcTmp.ForceMeshUpdate();
                    }
                }
            }

            EconomyData data = GetEconomyData();
            InjectOneStandSlider(standUsage, sourceRow, "MediumStandParkingFee", "Medium aircraft", "mediumStandParkingSlider", data?.mediumStandParkingFee ?? 0f);
            InjectOneStandSlider(standUsage, sourceRow, "LargeStandParkingFee", "Large aircraft", "largeStandParkingSlider", data?.largeStandParkingFee ?? 0f);
        }

        private static void InjectOneStandSlider(Transform parent, Transform source, string goName, string label, string sliderDefName, float seedValue)
        {
            // Clone source row, rename, and parent to AircraftStandUsage.
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, parent);
            clone.name = goName;
            _injectedStandSliders.Add(clone);

            // Rewrite the label text via the SliderDescription child.
            // LocalizedTextMeshPro is disabled first to prevent it overwriting our text.
            // This mod is English-only; localisation is not supported for injected labels.
            // The component may be TextMeshProUGUI (canvas) or TextMeshPro (world); try both.
            Transform desc = clone.transform.Find("SliderDescription");
            if (desc != null)
            {
                DisableLocalizedTextMeshPro(desc);

                var tmpUGUI = desc.GetComponent<TextMeshProUGUI>();
                if (tmpUGUI != null)
                {
                    tmpUGUI.text = label;
                    tmpUGUI.ForceMeshUpdate();
                }
                else
                {
                    var tmp = desc.GetComponent<TextMeshPro>();
                    if (tmp != null)
                    {
                        tmp.text = label;
                        tmp.ForceMeshUpdate();
                    }
                    else Logger.LogWarning($"[v1.2.4] No TMP component found on SliderDescription of cloned {goName}.");
                }
            }
            else
            {
                Logger.LogWarning($"[v1.2.4] SliderDescription not found on cloned {goName}.");
            }

            // Store the ValueSliderContainerUI reference directly on the matching SliderDef.
            // SliderDef.ResolveSlider() uses reflection on FeesPanelUI fields, which won't
            // find injected GOs — so we assign the component reference manually here instead.
            var vsc = clone.GetComponent<ValueSliderContainerUI>();
            if (vsc == null)
            {
                Logger.LogWarning($"[v1.2.4] ValueSliderContainerUI not found on cloned {goName}.");
                return;
            }

            // Find the matching SliderDef, assign the wrapper reference, and mark as injected.
            // IsInjected prevents ResolveSlider from later nulling this reference via reflection.
            SliderDef matchedDef = null;
            foreach (var def in _sliderTable)
            {
                if (def.FieldName == sliderDefName)
                {
                    def.Slider = vsc;
                    def.IsInjected = true;
                    matchedDef = def;
                    break;
                }
            }

            if (matchedDef == null) return;

            // Seed the inner Unity Slider with the correct min/max/value from EconomyData
            // so it opens at the right position rather than inheriting the clone source value.
            // ApplyB2FeesPanelOverrides will re-apply these after injection, but seeding here
            // ensures the slider is correct even before that call completes.
            FieldInfo innerField = AccessTools.Field(typeof(ValueSliderContainerUI), "slider");
            var inner = innerField?.GetValue(vsc) as Slider;
            if (inner != null)
            {
                float multiplier = matchedDef.GetMultiplier();
                float displayMin = matchedDef.VanillaMin * multiplier;
                float displayMax = Mathf.Max(displayMin + 0.001f, matchedDef.VanillaMax * multiplier);
                float clampedSeed = Mathf.Clamp(seedValue, displayMin, displayMax);

                inner.minValue = displayMin;
                inner.maxValue = displayMax;
                inner.value = clampedSeed;
                vsc.roundValue = matchedDef.DisplayStep * multiplier;
                vsc.UpdateContainer();

                Logger.LogInfo($"[v1.2.4] Injected {goName}: label='{label}' min={displayMin} max={displayMax} value={clampedSeed}");
            }
            else
            {
                Logger.LogWarning($"[v1.2.4] Inner Slider not found on cloned {goName} — value not seeded.");
            }
        }

        // Disables the LocalizedTextMeshPro component on a SliderDescription transform if present.
        // That component overwrites TMP text with the vanilla localised string after every write.
        // Injected stand slider labels are English-only and do not use localisation.
        private static void DisableLocalizedTextMeshPro(Transform descTransform)
        {
            foreach (var c in descTransform.GetComponents<Behaviour>())
            {
                if (c.GetType().Name == "LocalizedTextMeshPro")
                {
                    c.enabled = false;
                    return;
                }
            }
        }

        // DIAGNOSTIC — logs all direct children of SmallStandParkingFee and their
        // component type names. Fires once at injection time to identify the tooltip
        // child name and component type. Remove once confirmed.
        private static void LogStandSliderChildren(Transform sourceRow)
        {
            Logger.LogInfo($"[DIAG] SmallStandParkingFee children ({sourceRow.childCount}):");
            for (int i = 0; i < sourceRow.childCount; i++)
            {
                Transform child = sourceRow.GetChild(i);
                var componentNames = new System.Text.StringBuilder();
                foreach (var c in child.GetComponents<Component>())
                    componentNames.Append(c.GetType().Name).Append(", ");
                Logger.LogInfo($"[DIAG]   [{i}] '{child.name}' — {componentNames}");
            }
        }

        // Seeds Base* fields from EconomyData Default properties. Called once per session;
        // guard prevents overwriting clean vanilla values with already-multiplied ones.
        public static void EnsureBaseFeeCacheInitialized(EconomyData data)
        {
            if (BaseFeeCacheInitialized || data == null) return;

            BaseSmallAircraftRunwayFee = data.SmallAircraftRunwayFeeDefault;
            BaseMediumAircraftRunwayFee = data.MediumAircraftRunwayFeeDefault;
            BaseLargeAircraftRunwayFee = data.LargeAircraftRunwayFeeDefault;
            BaseSmallStandParkingFee = data.SmallStandParkingFeeDefault;
            BaseMediumStandParkingFee = data.MediumStandParkingFeeDefault;
            BaseLargeStandParkingFee = data.LargeStandParkingFeeDefault;
            BaseHangarAircraftRepairFee = data.HangarAircraftRepairFeeDefault;
            BaseAvgas100LLFuelCost = data.Avgas100LLFuelCostDefault;
            BaseJetA1FuelCost = data.JetA1FuelCostDefault;
            BaseCateringMealCost = data.CateringMealCostDefault;
            BaseAircraftCabinCleaningCost = data.AircraftCabinCleaningCostDefault;
            BaseDeicingFluidCost = data.DeicingFluidCostDefault;
            BasePassengerHandlingFee = data.PassengerHandlingFeeDefault;
            BaseBaggageHandlingFee = data.BaggageHandlingFeeDefault;
            BaseShortTermParkingFee = data.ShortTermParkingFeeDefault;
            BaseLongTermParkingFee = data.LongTermParkingFeeDefault;

            BaseFeeCacheInitialized = true;
            Logger.LogInfo("Base fee cache initialised from vanilla defaults.");
        }

        // Config binding — AcceptableValueRange enforces bounds; NormalizeAndSaveConfigValues
        // additionally snaps to step grid. Section numbers match the Fees panel layout order.
        private void BindConfig()
        {
            EnableEconomy = BindBool("1. Enable", "EnableEconomy", true, "Master toggle for all economy fee multipliers.");

            SmallRunwayIncome = BindFloat("2. Runways", "SmallRunwayIncome", 1f, 0.5f, 2.5f, "Small aircraft runway fee multiplier. 1.0 = vanilla. Low: 0.5 (£50 min, £500 max). High: 2.5 (£250 min, £2,500 max). Step: 10%.");
            MediumRunwayIncome = BindFloat("2. Runways", "MediumRunwayIncome", 1f, 0.5f, 2.5f, "Medium aircraft runway fee multiplier. 1.0 = vanilla. Low: 0.5 (£250 min, £1,500 max). High: 2.5 (£1,250 min, £7,500 max). Step: 10%.");
            LargeRunwayIncome = BindFloat("2. Runways", "LargeRunwayIncome", 1f, 0.5f, 2.5f, "Large aircraft runway fee multiplier. 1.0 = vanilla. Low: 0.5 (£500 min, £2,500 max). High: 2.5 (£2,500 min, £12,500 max). Step: 10%.");

            SmallStandIncome = BindFloat("3. Stands", "SmallStandIncome", 1f, 0.5f, 2.5f, "Small/GA stand parking fee multiplier. 1.0 = vanilla. Low: 0.5 (£25 min, £400 max). High: 2.5 (£125 min, £2,000 max). Step: 10%.");
            MediumStandIncome = BindFloat("3. Stands", "MediumStandIncome", 1f, 0.5f, 2.5f, "Medium stand parking fee multiplier. 1.0 = vanilla. Low: 0.5 (£50 min, £500 max). High: 2.5 (£250 min, £2,500 max). Step: 10%.");
            LargeStandIncome = BindFloat("3. Stands", "LargeStandIncome", 1f, 0.5f, 2.5f, "Large stand parking fee multiplier. 1.0 = vanilla. Low: 0.5 (£50 min, £600 max). High: 2.5 (£250 min, £3,000 max). Step: 10%.");

            HangarRepairIncome = BindFloat("4. Aircraft Services", "HangarRepairIncome", 1f, 0.5f, 1.5f, "Hangar repair fee multiplier. 1.0 = vanilla. Low: 0.5 (£50 min, £600 max). High: 1.5 (£150 min, £1,800 max). Step: 10%.");
            CabinCleaningIncome = BindFloat("4. Aircraft Services", "CabinCleaningIncome", 1f, 0.5f, 1.5f, "Cabin cleaning fee multiplier. 1.0 = vanilla. Low: 0.5 (£0.25 min, £2.50 max). High: 1.5 (£0.75 min, £7.50 max). Step: 10%.");
            DeicingFluidIncome = BindFloat("4. Aircraft Services", "DeicingFluidIncome", 1f, 0.5f, 1.5f, "De-icing fluid charge multiplier. 1.0 = vanilla. Low: 0.5 (£1 min, £10 max). High: 1.5 (£3 min, £30 max). Step: 10%.");
            CateringMealIncome = BindFloat("4. Aircraft Services", "CateringMealIncome", 1f, 0.5f, 1.5f, "Catering meal charge multiplier. 1.0 = vanilla. Low: 0.5 (£0.50 min, £5 max). High: 1.5 (£1.50 min, £15 max). Step: 10%.");

            BaggageHandlingIncome = BindFloat("5. Handling", "BaggageHandlingIncome", 1f, 0.5f, 1.75f, "Baggage handling fee multiplier. 1.0 = vanilla. Low: 0.5 (£1.50 min, £10 max). High: 1.75 (£5.25 min, £35 max). Step: 5%.");
            PassengerHandlingIncome = BindFloat("5. Handling", "PassengerHandlingIncome", 1f, 0.5f, 1.75f, "Passenger handling fee multiplier. 1.0 = vanilla. Low: 0.5 (£2.50 min, £15 max). High: 1.75 (£8.75 min, £52.50 max). Step: 5%.");

            ShortTermParkingIncome = BindFloat("6. Parking", "ShortTermParkingIncome", 1f, 0.5f, 1.5f, "Short-term parking fee multiplier. 1.0 = vanilla. Low: 0.5 (£1 min, £10 max). High: 1.5 (£3 min, £30 max). Step: 10%.");
            LongTermParkingIncome = BindFloat("6. Parking", "LongTermParkingIncome", 1f, 0.5f, 2.0f, "Long-term parking fee multiplier. 1.0 = vanilla. Low: 0.5 (£25 min, £200 max). High: 2.0 (£100 min, £800 max). Step: 10%.");

            Avgas100LLIncome = BindFloat("7. Fuel", "Avgas100LLIncome", 1f, 0.5f, 2.5f, "Avgas 100LL fuel charge multiplier. 1.0 = vanilla. Low: 0.5 (£0.25 min, £2 max). High: 2.5 (£1.25 min, £10 max). Step: 5%.");
            JetA1Income = BindFloat("7. Fuel", "JetA1Income", 1f, 0.5f, 2.0f, "Jet A1 fuel charge multiplier. 1.0 = vanilla. Low: 0.5 (£0.05 min, £0.50 max). High: 2.0 (£0.20 min, £2 max). Step: 5%.");
        }

        private ConfigEntry<bool> BindBool(string section, string key, bool def, string desc) =>
            Config.Bind(section, key, def, new ConfigDescription(desc));

        private ConfigEntry<float> BindFloat(string section, string key, float def, float min, float max, string desc) =>
            Config.Bind(section, key, def, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));

        // Step-snapping helpers — clamp and round config values to valid step grids.
        // Ranges: Runways/Stands 0.5–2.5 (10%), Services 0.5–1.5 (10%),
        //   Handling 0.5–1.75 (5%), Short Parking 0.5–1.5 (10%),
        //   Long Parking 0.5–2.0 (10%), Avgas 0.5–2.5 (5%), Jet A1 0.5–2.0 (5%).
        public static float SnapToStep(float value, float min, float max, float step)
        {
            float clamped = Mathf.Clamp(value, min, max);
            float snapped = Mathf.Round((clamped - min) / step) * step + min;
            return Mathf.Clamp((float)System.Math.Round(snapped, 3), min, max);
        }

        public static float GetRunwayStandValue(ConfigEntry<float> e) => SnapToStep(e.Value, 0.5f, 2.5f, 0.1f);
        public static float GetServiceValue(ConfigEntry<float> e) => SnapToStep(e.Value, 0.5f, 1.5f, 0.1f);
        public static float GetHandlingValue(ConfigEntry<float> e) => SnapToStep(e.Value, 0.5f, 1.75f, 0.05f);
        public static float GetShortTermParkingValue(ConfigEntry<float> e) => SnapToStep(e.Value, 0.5f, 1.5f, 0.1f);
        public static float GetLongTermParkingValue(ConfigEntry<float> e) => SnapToStep(e.Value, 0.5f, 2.0f, 0.1f);
        public static float GetAvgasValue(ConfigEntry<float> e) => SnapToStep(e.Value, 0.5f, 2.5f, 0.05f);
        public static float GetJetA1Value(ConfigEntry<float> e) => SnapToStep(e.Value, 0.5f, 2.0f, 0.05f);

        // =========================================================================
        // Config normalisation
        //
        // Called once at startup after BindConfig().  Reads each config entry,
        // snaps it to the correct step grid, and saves the file only if at least
        // one value changed.  This corrects hand-edited .cfg values silently on
        // first load without requiring user intervention.
        // =========================================================================
        public void NormalizeAndSaveConfigValues()
        {
            bool changed = false;
            changed |= SetIfDifferent(SmallRunwayIncome, GetRunwayStandValue(SmallRunwayIncome));
            changed |= SetIfDifferent(MediumRunwayIncome, GetRunwayStandValue(MediumRunwayIncome));
            changed |= SetIfDifferent(LargeRunwayIncome, GetRunwayStandValue(LargeRunwayIncome));
            changed |= SetIfDifferent(SmallStandIncome, GetRunwayStandValue(SmallStandIncome));
            changed |= SetIfDifferent(MediumStandIncome, GetRunwayStandValue(MediumStandIncome));
            changed |= SetIfDifferent(LargeStandIncome, GetRunwayStandValue(LargeStandIncome));
            changed |= SetIfDifferent(HangarRepairIncome, GetServiceValue(HangarRepairIncome));
            changed |= SetIfDifferent(CabinCleaningIncome, GetServiceValue(CabinCleaningIncome));
            changed |= SetIfDifferent(DeicingFluidIncome, GetServiceValue(DeicingFluidIncome));
            changed |= SetIfDifferent(CateringMealIncome, GetServiceValue(CateringMealIncome));
            changed |= SetIfDifferent(BaggageHandlingIncome, GetHandlingValue(BaggageHandlingIncome));
            changed |= SetIfDifferent(PassengerHandlingIncome, GetHandlingValue(PassengerHandlingIncome));
            changed |= SetIfDifferent(ShortTermParkingIncome, GetShortTermParkingValue(ShortTermParkingIncome));
            changed |= SetIfDifferent(LongTermParkingIncome, GetLongTermParkingValue(LongTermParkingIncome));
            changed |= SetIfDifferent(Avgas100LLIncome, GetAvgasValue(Avgas100LLIncome));
            changed |= SetIfDifferent(JetA1Income, GetJetA1Value(JetA1Income));

            if (changed)
            {
                Config.Save();
                Logger.LogInfo("Config values normalised to valid slider steps and saved.");
            }
        }

        public void RegisterLiveConfigHandlers()
        {
            RegisterSnapHandler(SmallRunwayIncome, e => GetRunwayStandValue(e));
            RegisterSnapHandler(MediumRunwayIncome, e => GetRunwayStandValue(e));
            RegisterSnapHandler(LargeRunwayIncome, e => GetRunwayStandValue(e));
            RegisterSnapHandler(SmallStandIncome, e => GetRunwayStandValue(e));
            RegisterSnapHandler(MediumStandIncome, e => GetRunwayStandValue(e));
            RegisterSnapHandler(LargeStandIncome, e => GetRunwayStandValue(e));
            RegisterSnapHandler(HangarRepairIncome, e => GetServiceValue(e));
            RegisterSnapHandler(CabinCleaningIncome, e => GetServiceValue(e));
            RegisterSnapHandler(DeicingFluidIncome, e => GetServiceValue(e));
            RegisterSnapHandler(CateringMealIncome, e => GetServiceValue(e));
            RegisterSnapHandler(BaggageHandlingIncome, e => GetHandlingValue(e));
            RegisterSnapHandler(PassengerHandlingIncome, e => GetHandlingValue(e));
            RegisterSnapHandler(ShortTermParkingIncome, e => GetShortTermParkingValue(e));
            RegisterSnapHandler(LongTermParkingIncome, e => GetLongTermParkingValue(e));
            RegisterSnapHandler(Avgas100LLIncome, e => GetAvgasValue(e));
            RegisterSnapHandler(JetA1Income, e => GetJetA1Value(e));
        }

        private void RegisterSnapHandler(ConfigEntry<float> entry, System.Func<ConfigEntry<float>, float> snapFunc)
        {
            entry.SettingChanged += (_, __) =>
            {
                if (_isNormalizingConfig) return;
                _isNormalizingConfig = true;
                try { if (SetIfDifferent(entry, snapFunc(entry))) Config.Save(); }
                finally { _isNormalizingConfig = false; }
            };
        }

        // ApplyEconomyFeeMultipliers — writes scaledFee = base × multiplier to EconomyData.
        // All fees are now slider-mapped; only rewrite when multiplier has changed since last write.
        public static void ApplyEconomyFeeMultipliers(EconomyData data, string callerContext = "unknown")
        {
            if (!EnableEconomy.Value || data == null) return;
            if (!BaseFeeCacheInitialized) return;

            // All stand fees now have sliders — handled by ApplySliderFeeIfMultiplierChanged.
            ApplySliderFeeIfMultiplierChanged(data);

            RefreshAirportRatingEvaluationCurves();
        }

        // Writes base × multiplier per slider only when the multiplier has changed;
        // player-dragged EconomyData values are preserved when it has not.
        private static void ApplySliderFeeIfMultiplierChanged(EconomyData data)
        {
            foreach (var def in _sliderTable)
            {
                float multiplier = def.GetMultiplier();
                if (Mathf.Approximately(multiplier, def.LastAppliedMultiplier)) continue;

                float baseVal = GetBaseValueForSlider(def.FieldName);
                float step = GetRoundStepForSlider(def.FieldName);
                float newValue = Round(baseVal * multiplier, step);

                SetLiveEconomyValue(def.FieldName, newValue, data);
                def.LastAppliedMultiplier = multiplier;
            }
        }

        // Returns the base fee cache value for a slider by field name.
        private static float GetBaseValueForSlider(string fieldName)
        {
            switch (fieldName)
            {
                case "smallAircraftRunwaySlider": return BaseSmallAircraftRunwayFee;
                case "mediumAircraftRunwaySlider": return BaseMediumAircraftRunwayFee;
                case "largeAircraftRunwaySlider": return BaseLargeAircraftRunwayFee;
                case "smallStandParkingSlider": return BaseSmallStandParkingFee;
                case "mediumStandParkingSlider": return BaseMediumStandParkingFee;
                case "largeStandParkingSlider": return BaseLargeStandParkingFee;
                case "hangarAircraftRepairSlider": return BaseHangarAircraftRepairFee;
                case "aviationFuelAvgas100LLSlider": return BaseAvgas100LLFuelCost;
                case "aviationFuelJetA1Slider": return BaseJetA1FuelCost;
                case "cateringMealSlider": return BaseCateringMealCost;
                case "aircraftCabinCleaningSlider": return BaseAircraftCabinCleaningCost;
                case "deicingFluidSlider": return BaseDeicingFluidCost;
                case "passengerHandlingSlider": return BasePassengerHandlingFee;
                case "baggageHandlingSlider": return BaseBaggageHandlingFee;
                case "shortTermParkingSlider": return BaseShortTermParkingFee;
                case "longTermParkingSlider": return BaseLongTermParkingFee;
                default: return 0f;
            }
        }

        // Returns the rounding step for a slider by field name (matches vanilla snap increments).
        private static float GetRoundStepForSlider(string fieldName)
        {
            switch (fieldName)
            {
                case "smallAircraftRunwaySlider":
                case "mediumAircraftRunwaySlider":
                case "largeAircraftRunwaySlider": return 100f;
                case "smallStandParkingSlider":
                case "mediumStandParkingSlider":
                case "largeStandParkingSlider":
                case "hangarAircraftRepairSlider": return 50f;
                case "passengerHandlingSlider":
                case "baggageHandlingSlider":
                case "shortTermParkingSlider": return 1f;
                case "longTermParkingSlider": return 50f;
                case "aviationFuelAvgas100LLSlider": return 0.1f;
                case "aviationFuelJetA1Slider": return 0.05f;
                case "cateringMealSlider": return 0.1f;
                case "aircraftCabinCleaningSlider": return 0.5f;
                case "deicingFluidSlider": return 0.5f;
                default: return 1f;
            }
        }

        // Resets each slider's LastAppliedMultiplier to -1f on session boundaries
        // so the next apply call always re-establishes from the fresh base cache.
        private static void ResetSliderMultiplierCache()
        {
            if (_sliderTable == null) return;
            foreach (var def in _sliderTable)
                def.LastAppliedMultiplier = -1f;
        }

        // Rebuilds all rating AnimationCurve fields on AirportRatingManager via reflection,
        // scaling each curve's x-axis anchor by the active multiplier so that green/orange/red
        // bands in the Fees panel and report bars always reflect the current scaled fee range.
        // Called after every fee application and slider drag; also patched onto
        // InitializeEvaluationCurves() so our curves replace vanilla ones at scene load.
        public static void RefreshAirportRatingEvaluationCurves()
        {
            var rm = Singleton<AirportRatingManager>.Instance;
            var ec = Singleton<EconomyController>.Instance;
            if (rm == null || ec?.economyData == null) return;

            SetRatingCurve(rm, "smallRunwayUsageCostEvaluationCurve", BuildCurve(BaseSmallAircraftRunwayFee, GetRunwayStandValue(SmallRunwayIncome)));
            SetRatingCurve(rm, "mediumRunwayUsageCostEvaluationCurve", BuildCurve(BaseMediumAircraftRunwayFee, GetRunwayStandValue(MediumRunwayIncome)));
            SetRatingCurve(rm, "largeRunwayUsageCostEvaluationCurve", BuildCurve(BaseLargeAircraftRunwayFee, GetRunwayStandValue(LargeRunwayIncome)));
            SetRatingCurve(rm, "smallStandParkingCostEvaluationCurve", BuildCurve(BaseSmallStandParkingFee, GetRunwayStandValue(SmallStandIncome)));
            SetRatingCurve(rm, "mediumStandParkingCostEvaluationCurve", BuildCurve(BaseMediumStandParkingFee, GetRunwayStandValue(MediumStandIncome)));
            SetRatingCurve(rm, "largeStandParkingCostEvaluationCurve", BuildCurve(BaseLargeStandParkingFee, GetRunwayStandValue(LargeStandIncome)));
            SetRatingCurve(rm, "hangarAircraftRepairFeeCurve", BuildCurve(BaseHangarAircraftRepairFee, GetServiceValue(HangarRepairIncome)));
            SetRatingCurve(rm, "cateringMealCostEvaluationCurve", BuildCurve(BaseCateringMealCost, GetServiceValue(CateringMealIncome)));
            SetRatingCurve(rm, "aircraftCabinCleaningCostEvaluationCurve", BuildCurve(BaseAircraftCabinCleaningCost, GetServiceValue(CabinCleaningIncome)));
            SetRatingCurve(rm, "deicingFluidLitreCostEvaluationCurve", BuildCurve(BaseDeicingFluidCost, GetServiceValue(DeicingFluidIncome)));
            SetRatingCurve(rm, "passengerHandlingCostEvaluationCurve", BuildCurve(BasePassengerHandlingFee, GetHandlingValue(PassengerHandlingIncome)));
            SetRatingCurve(rm, "baggageHandlingCostEvaluationCurve", BuildCurve(BaseBaggageHandlingFee, GetHandlingValue(BaggageHandlingIncome)));
            SetRatingCurve(rm, "avgas100LLLitreCostEvaluationCurve", BuildCurve(BaseAvgas100LLFuelCost, GetAvgasValue(Avgas100LLIncome)));
            SetRatingCurve(rm, "jetA1LitreCostEvaluationCurve", BuildCurve(BaseJetA1FuelCost, GetJetA1Value(JetA1Income)));
            SetRatingCurve(rm, "shortTermParkingCostEvaluationCurve", BuildCurve(BaseShortTermParkingFee, GetShortTermParkingValue(ShortTermParkingIncome)));
            SetRatingCurve(rm, "longTermParkingCostEvaluationCurve", BuildCurve(BaseLongTermParkingFee, GetLongTermParkingValue(LongTermParkingIncome)));
        }

        // Writes a curve to a named private field on AirportRatingManager.
        // Exits silently if the field is not found (e.g. after a game update
        // renames it) rather than throwing, to preserve mod resilience.
        private static void SetRatingCurve(AirportRatingManager rm, string fieldName, AnimationCurve curve)
        {
            AccessTools.Field(typeof(AirportRatingManager), fieldName)?.SetValue(rm, curve);
        }

        // -------------------------------------------------------------------------
        // BuildCurve
        //
        // Constructs a rating evaluation curve matching vanilla's formula but scaled
        // by the active multiplier.
        //
        // Vanilla formula (from AirportRatingManager.InitializeEvaluationCurves):
        //   EaseInOut(FeeMax * 0.4f, 1f,  FeeMax, 0f)
        //   x-axis = fee value,  y-axis = satisfaction (1 = happy, 0 = unhappy)
        //   At FeeMax × 0.4 → satisfaction 1 (fully green)
        //   At FeeMax × 1.0 → satisfaction 0 (fully red)
        //
        // Since all vanilla defaults = FeeMax / 2:
        //   scaledFeeMax = baseFee × 2 × multiplier
        //
        // At multiplier 1.0, scaledFeeMax = FeeMax → identical to vanilla.
        // At multiplier 2.0, the entire curve shifts right proportionally, so a
        // doubled fee evaluates to the same satisfaction as the vanilla default fee
        // did against the vanilla curve.
        // -------------------------------------------------------------------------
        private static AnimationCurve BuildCurve(float baseFee, float multiplier)
        {
            float safeBase = Mathf.Max(0.001f, baseFee);
            float scaledFeeMax = safeBase * 2f * Mathf.Max(0.001f, multiplier);
            // 1 = happy (green), 0 = unhappy (red) — matches vanilla orientation.
            return AnimationCurve.EaseInOut(scaledFeeMax * 0.4f, 1f, scaledFeeMax, 0f);
        }

        // Fees panel integration — three patch points:
        //   InitializePanel (FeesPanelInitPatch): seed cache, apply multipliers,
        //     attach listeners, call ApplyB2FeesPanelOverrides so ranges are set
        //     before the player can interact.
        //   LoadPanel (FeesPanelDisplayPatch): correct slider min/max/step/value
        //     after vanilla sets them from hardcoded FeeMax constants. Guarded on
        //     BaseFeeCacheInitialized.
        //   OnValueChanged listeners (AttachB2FeesPanelListeners): snap raw drag
        //     to mod's displayStep grid before writing to EconomyData.
        public static EconomyData GetEconomyData() =>
            Singleton<EconomyController>.Instance?.economyData;

        public static void AttachB2FeesPanelListeners(FeesPanelUI feesPanel)
        {
            if (feesPanel == null) return;
            // Guard: only attach once per panel instance to avoid stacking duplicate
            // listeners if InitializePanel is called more than once on the same object.
            if (!HookedFeesPanelInstanceIds.Add(feesPanel.GetInstanceID())) return;

            foreach (var def in _sliderTable)
            {
                def.ResolveSlider(feesPanel);
                if (def.Slider == null) continue;

                var capturedDef = def;
                def.Slider.OnValueChanged.AddListener(value =>
                {
                    if (!EnableEconomy.Value || IsSyncingFeesPanelDisplay) return;
                    if (!BaseFeeCacheInitialized) return;

                    EconomyData data = GetEconomyData();
                    if (data == null) return;

                    // Snap drag to displayStep grid so it lands cleanly and vanilla can't re-snap it.
                    float multiplier = capturedDef.GetMultiplier();
                    float step = capturedDef.DisplayStep * multiplier;
                    float snapped = step > 0f ? Mathf.Round(value / step) * step : value;

                    SetLiveEconomyValue(capturedDef.FieldName, snapped, data);
                    RefreshAirportRatingEvaluationCurves();
                    ApplyB2FeesPanelOverrides(feesPanel, "SliderDrag");
                });
            }
        }

        // Maps a slider field name to its corresponding EconomyData field and writes the given value.
        private static void SetLiveEconomyValue(string fieldName, float value, EconomyData data)
        {
            switch (fieldName)
            {
                case "smallAircraftRunwaySlider": data.smallAircraftRunwayFee = value; break;
                case "mediumAircraftRunwaySlider": data.mediumAircraftRunwayFee = value; break;
                case "largeAircraftRunwaySlider": data.largeAircraftRunwayFee = value; break;
                case "smallStandParkingSlider": data.smallStandParkingFee = value; break;
                case "mediumStandParkingSlider": data.mediumStandParkingFee = value; break;
                case "largeStandParkingSlider": data.largeStandParkingFee = value; break;
                case "hangarAircraftRepairSlider": data.hangarAircraftRepairFee = value; break;
                case "aviationFuelAvgas100LLSlider": data.avgas100LLFuelCost = value; break;
                case "aviationFuelJetA1Slider": data.jetA1FuelCost = value; break;
                case "cateringMealSlider": data.cateringMealCost = value; break;
                case "aircraftCabinCleaningSlider": data.aircraftCabinCleaningCost = value; break;
                case "deicingFluidSlider": data.deicingFluidCost = value; break;
                case "passengerHandlingSlider": data.passengerHandlingFee = value; break;
                case "baggageHandlingSlider": data.baggageHandlingFee = value; break;
                case "shortTermParkingSlider": data.shortTermParkingFee = value; break;
                case "longTermParkingSlider": data.longTermParkingFee = value; break;
            }
        }

        // ApplyB2FeesPanelOverrides — sets each slider's min/max/step/value to the
        // multiplier-scaled range and live EconomyData value. Writes directly to the
        // inner Unity Slider via reflection, stripping and restoring all listeners to
        // prevent vanilla writing back to EconomyData during the programmatic update.
        // Does NOT call ApplyEconomyFeeMultipliers — must not overwrite a player drag.
        public static void ApplyB2FeesPanelOverrides(FeesPanelUI feesPanel, string callerContext = "unknown")
        {
            if (!EnableEconomy.Value || feesPanel == null) return;
            if (!BaseFeeCacheInitialized) return;

            EconomyData data = GetEconomyData();
            if (data == null) return;

            IsSyncingFeesPanelDisplay = true;
            try
            {
                foreach (var def in _sliderTable)
                {
                    if (def.Slider == null) def.ResolveSlider(feesPanel);
                    if (def.Slider == null) continue;

                    Slider inner = def.GetInnerSlider();
                    if (inner == null) continue;

                    float multiplier = def.GetMultiplier();
                    float liveValue = GetLiveEconomyValue(def.FieldName, data);
                    float displayMin = def.VanillaMin * multiplier;
                    float displayMax = Mathf.Max(displayMin + 0.001f, def.VanillaMax * multiplier);
                    float displayStep = def.DisplayStep * multiplier;

                    // Remove all listeners to prevent vanilla's InitializePanel
                    // listener writing liveValue back to EconomyData during our write.
                    inner.onValueChanged.RemoveAllListeners();

                    // Write directly to the inner Slider, bypassing the wrapper's
                    // Value property setter and its Awake-registered event chain.
                    inner.minValue = displayMin;
                    inner.maxValue = displayMax;
                    inner.value = Mathf.Clamp(liveValue, displayMin, displayMax);

                    // Restore the wrapper's round step and refresh display text/colour.
                    def.Slider.roundValue = displayStep;
                    def.Slider.Interactable = true;
                    def.Slider.UpdateContainer();

                    // Re-register the wrapper's own internal listener (from Awake)
                    // and vanilla's InitializePanel listener by re-running their
                    // registration logic via the wrapper's event.
                    // The wrapper's Awake listener calls RoundValueChanged + UpdateContainer.
                    // Vanilla's listener writes slider.Value back to EconomyData.
                    // Both are restored by re-adding them through the wrapper's public event.
                    inner.onValueChanged.AddListener(v =>
                    {
                        def.Slider.roundValue = displayStep; // keep step current
                        if (def.Slider.roundValue > 0f)
                            inner.value = Utils.RoundToNearestGivenNumber(v, def.Slider.roundValue);
                        def.Slider.UpdateContainer();
                    });

                    // Re-add vanilla's EconomyData write-back listener.
                    // This mirrors what FeesPanelUI.InitializePanel() registers for each slider.
                    var capturedDef = def;
                    inner.onValueChanged.AddListener(v =>
                    {
                        if (IsSyncingFeesPanelDisplay) return;
                        EconomyData d = GetEconomyData();
                        if (d != null) SetLiveEconomyValue(capturedDef.FieldName, inner.value, d);
                    });
                }
            }
            finally { IsSyncingFeesPanelDisplay = false; }
        }

        // Returns the current live value of a fee from EconomyData by slider field name.
        // Used by ApplyB2FeesPanelOverrides() to populate slider display values.
        private static float GetLiveEconomyValue(string fieldName, EconomyData data)
        {
            switch (fieldName)
            {
                case "smallAircraftRunwaySlider": return data.smallAircraftRunwayFee;
                case "mediumAircraftRunwaySlider": return data.mediumAircraftRunwayFee;
                case "largeAircraftRunwaySlider": return data.largeAircraftRunwayFee;
                case "smallStandParkingSlider": return data.smallStandParkingFee;
                case "mediumStandParkingSlider": return data.mediumStandParkingFee;
                case "largeStandParkingSlider": return data.largeStandParkingFee;
                case "hangarAircraftRepairSlider": return data.hangarAircraftRepairFee;
                case "aviationFuelAvgas100LLSlider": return data.avgas100LLFuelCost;
                case "aviationFuelJetA1Slider": return data.jetA1FuelCost;
                case "cateringMealSlider": return data.cateringMealCost;
                case "aircraftCabinCleaningSlider": return data.aircraftCabinCleaningCost;
                case "deicingFluidSlider": return data.deicingFluidCost;
                case "passengerHandlingSlider": return data.passengerHandlingFee;
                case "baggageHandlingSlider": return data.baggageHandlingFee;
                case "shortTermParkingSlider": return data.shortTermParkingFee;
                case "longTermParkingSlider": return data.longTermParkingFee;
                default: return 0f;
            }
        }

        // Round to nearest step multiple, trimmed to 3dp to avoid float noise in the UI.
        private static float Round(float value, float step)
        {
            if (step <= 0f) return value;
            return (float)System.Math.Round(Mathf.Round(value / step) * step, 3);
        }

        // Writes newValue to entry only if meaningfully different (3dp); returns true if written.
        private static bool SetIfDifferent(ConfigEntry<float> entry, float newValue)
        {
            float cur = (float)System.Math.Round(entry.Value, 3);
            float nxt = (float)System.Math.Round(newValue, 3);
            if (Mathf.Approximately(cur, nxt)) return false;
            entry.Value = nxt;
            return true;
        }
    }

    // =========================================================================
    // Harmony patches
    // =========================================================================

    // EconomyFeeRefreshPatch — Prefix on EconomyController.UpdateHourlyBalanceUI().
    // Runs ApplyEconomyFeeMultipliers() before vanilla reads EconomyData fee fields.
    // Player-dragged slider values are preserved unless the multiplier has changed.
    [HarmonyPatch(typeof(EconomyController), "UpdateHourlyBalanceUI")]
    public static class EconomyFeeRefreshPatch
    {
        [HarmonyPrefix]
        public static void Prefix(EconomyController __instance)
        {
            if (__instance?.economyData != null)
                GrandMasterPlugin.ApplyEconomyFeeMultipliers(__instance.economyData, "HourlyTick");
        }
    }

    // FeesPanelInitPatch — Postfix on FeesPanelUI.InitializePanel().
    // Seeds base cache, applies multipliers, injects stand sliders (once per instance),
    // attaches listeners, then calls ApplyB2FeesPanelOverrides so slider ranges are
    // correct before first interaction.
    [HarmonyPatch(typeof(FeesPanelUI), "InitializePanel")]
    public static class FeesPanelInitPatch
    {
        [HarmonyPostfix]
        public static void Postfix(FeesPanelUI __instance)
        {
            if (__instance == null) return;
            EconomyData data = GrandMasterPlugin.GetEconomyData();
            if (data == null) return;

            GrandMasterPlugin.EnsureBaseFeeCacheInitialized(data);
            GrandMasterPlugin.ApplyEconomyFeeMultipliers(data, "FeesPanelInitPatch");

            // Inject medium/large stand sliders once per panel instance, before
            // listener attachment so the cloned wrappers are present when listeners
            // are added and when ApplyB2FeesPanelOverrides resolves SliderDefs.
            if (GrandMasterPlugin.InjectedFeesPanelInstanceIds.Add(__instance.GetInstanceID()))
                GrandMasterPlugin.InjectStandSliders(__instance);

            GrandMasterPlugin.AttachB2FeesPanelListeners(__instance);
            GrandMasterPlugin.ApplyB2FeesPanelOverrides(__instance, "FeesPanelInitPatch");
        }
    }

    // FeesPanelDisplayPatch — Postfix on FeesPanelUI.LoadPanel().
    // Corrects slider min/max/step/value after vanilla sets them from hardcoded FeeMax.
    // Guarded on BaseFeeCacheInitialized; early LoadPanel calls during session load are skipped.
    [HarmonyPatch(typeof(FeesPanelUI), "LoadPanel")]
    public static class FeesPanelDisplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(FeesPanelUI __instance)
        {
            if (__instance == null || !GrandMasterPlugin.EnableEconomy.Value) return;
            if (!GrandMasterPlugin.BaseFeeCacheInitialized) return;
            GrandMasterPlugin.ApplyB2FeesPanelOverrides(__instance, "FeesPanelDisplayPatch");
        }
    }

    // AirportRatingManagerInitCurvesPatch — Postfix on InitializeEvaluationCurves().
    // Replaces vanilla FeeMax-anchored curves with multiplier-scaled equivalents
    // so satisfaction ratings read correctly in the Fees panel and report bars.
    [HarmonyPatch(typeof(AirportRatingManager), "InitializeEvaluationCurves")]
    public static class AirportRatingManagerInitCurvesPatch
    {
        [HarmonyPostfix]
        public static void Postfix() => GrandMasterPlugin.RefreshAirportRatingEvaluationCurves();
    }
}
