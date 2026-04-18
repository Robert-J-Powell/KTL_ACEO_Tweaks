using AirportCEOModLoader.SaveLoadUtils;
using AirportCEOModLoader.WatermarkUtils;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// References required:
//   Unity.TextMeshPro.dll  (for ValueSliderContainerUI — resolved transitively)
//   AirportCEOModLoader.dll (D:\SteamLibrary\steamapps\workshop\content\673610\3109136766\plugins\AirportCEOModLoader.dll)

namespace ACEO_KTL_Economy_Tweaks
{
    // =========================================================================
    // ACEO_KTL_Economy_Tweaks  v1.1.0
    //
    // Applies configurable multipliers to sixteen airport income fee categories:
    //   2. Runways        — small, medium, and large aircraft runway usage fees
    //   3. Stands         — small/GA, medium, and large stand parking fees
    //   4. Aircraft Svc   — hangar repair, cabin cleaning, de-icing, catering
    //   5. Handling       — baggage handling and passenger handling fees
    //   6. Parking        — short-term and long-term car parking fees
    //   7. Fuel           — Avgas 100LL and Jet A1 fuel charges
    //
    // Excluded by design:
    //   - Bathroom entrance fee   (vanilla default is £0; scaling zero is useless)
    //   - Vending machine markup  (minor passenger-facing charge; not airline-linked)
    //   - Staff wages             (separate concern)
    //   - Contractors             (covered by KTL Contractor Tweaks)
    //   - Supply purchases        (contract-driven procurement costs)
    //   - Loans                   (contractually fixed obligations)
    //
    // Architecture:
    //   All vanilla fee defaults equal FeeMax / 2 (confirmed from EconomyData.cs).
    //   A base fee cache is seeded once per session from EconomyData at load or
    //   new game.  Multipliers are always applied as: scaledFee = base × multiplier.
    //   The base cache is never written to by sliders or save/load — it is the
    //   permanent vanilla reference for the session.
    //
    //   Rating evaluation curves are rebuilt after every multiplier application so
    //   that the green/orange/red bands in both the Fees panel and the report bars
    //   always reflect the scaled fee range correctly.
    //
    //   The in-game Fees panel sliders are extended to span the full scaled range:
    //   slider MaxValue = vanillaMax × multiplier, MinValue = vanillaMin × multiplier,
    //   step = vanillaStep × multiplier.  Slider values are written directly to the
    //   inner Unity Slider component via reflection to bypass the vanilla
    //   OnValueChanged listener chain, preventing vanilla from writing back to
    //   EconomyData during programmatic updates.
    //
    // =========================================================================
    // PATCH HISTORY
    // =========================================================================
    //
    // v1.0.6 — Initial public release
    //   Fee multipliers applied via EconomyData field writes on
    //   UpdateHourlyBalanceUI, FeesPanelUI.InitializePanel, and
    //   FeesPanelUI.LoadPanel.
    //   Base fee cache seeded once per session from vanilla defaults.
    //   Rating evaluation curves refreshed after each multiplier application
    //   so the fees panel green/orange/red colour bands scale correctly with
    //   the active multiplier.
    //   Known issue: Report bar satisfaction ratings (GA, airline, passenger)
    //   show orange/red even at default multiplier settings.  Root cause:
    //   BuildCurve() pivoted around the scaled live fee and used fixed ratios
    //   (1.25× / 1.75×) that are incompatible with the vanilla curve's
    //   coordinate space.  The report bar evaluates the raw live fee value
    //   against the curve; with the curve anchored incorrectly, any multiplied
    //   fee reads as mid-to-high dissatisfaction.
    //
    // v1.0.7 — Report bar rating curve fix
    //   Fixed: BuildCurve() now replicates the vanilla AirportRatingManager
    //   InitializeEvaluationCurve() formula exactly, scaled by the active
    //   multiplier.  All vanilla fee defaults equal FeeMax / 2, so
    //   scaledFeeMax = baseFee * 2 * multiplier reconstructs the vanilla
    //   FeeMax scaled to the current slider setting.
    //   The curve is then built as EaseInOut(scaledFeeMax * 0.4f, 1f,
    //   scaledFeeMax, 0f) — identical to vanilla, but shifted proportionally
    //   with the multiplier.  At the default multiplier of 1.0 the curve is
    //   bit-identical to what vanilla initialises.  At higher multipliers the
    //   green zone scales up commensurately, so a 2× runway fee reads green
    //   in the report bar, not orange/red.
    //   BuildCurve() now takes (baseFee, multiplier) instead of (neutralFee).
    //   All RefreshAirportRatingEvaluationCurves() call sites updated to pass
    //   the corresponding base cache value and snapped multiplier.
    //
    // v1.0.8 — Slider drag no longer snaps back to multiplier midpoint
    //   Fixed: ApplyB2FeesPanelOverrides() called ApplyEconomyFeeMultipliers()
    //   unconditionally at its end.  When triggered by a player slider drag, this
    //   immediately overwrote the player's chosen value with base × multiplier
    //   (i.e. FeeMax / 2 at the default 1.0 multiplier), making all sliders snap
    //   back to the middle regardless of where they were dragged.
    //   ApplyEconomyFeeMultipliers() sets the baseline fee from config and must
    //   only run at session start and on the hourly tick — not after every drag.
    //   Removed the erroneous call from ApplyB2FeesPanelOverrides().
    //
    // v1.0.9 — Diagnostic logging (testing only; reduced in v1.1.0)
    //   Added verbose BepInEx logging to all fee-write paths to assist diagnosis
    //   of slider snap-back issues.  Logging scope reduced in v1.1.0 now that the
    //   root cause has been identified and fixed.
    //
    // v1.1.0 — Slider event chain rewrite + MinValue + input snapping (bug fix)
    //   Root cause identified: ValueSliderContainerUI.Awake() registers its own
    //   internal OnValueChanged listener that calls RoundValueChanged() then
    //   UpdateContainer() synchronously on every slider.Value assignment.  Vanilla's
    //   FeesPanelUI.InitializePanel() registers a second listener that writes the
    //   slider value back to EconomyData.  Neither listener checks
    //   IsSyncingFeesPanelDisplay, so every programmatic write inside
    //   ApplyB2FeesPanelOverrides() triggered both, causing vanilla to overwrite
    //   EconomyData with its own snapped value immediately after the mod set it.
    //   This produced the observed snap-back on first drag and after every panel open.
    //
    //   Fix 1 — Direct inner Slider write.  ApplyB2FeesPanelOverrides() now
    //   reflects to the private Slider field inside ValueSliderContainerUI and
    //   writes slider.value, slider.minValue, and slider.maxValue directly,
    //   bypassing the ValueSliderContainerUI.Value property setter and its event
    //   chain entirely.  UpdateContainer() is still called afterwards to refresh
    //   the displayed text and colour.
    //
    //   Fix 2 — Vanilla listener suppression.  Before the write loop,
    //   ApplyB2FeesPanelOverrides() removes all listeners from each slider's
    //   onValueChanged event, writes the values directly, then restores all
    //   listeners.  This prevents vanilla's InitializePanel listener from writing
    //   back to EconomyData during the programmatic update.
    //
    //   Fix 3 — Input-side snapping.  AttachB2FeesPanelListeners() now snaps the
    //   raw drag value to the nearest displayStep multiple before writing to
    //   EconomyData.  This ensures the value written is always on the mod's step
    //   grid, so the subsequent ApplyB2FeesPanelOverrides() call writes the same
    //   value back to the slider and vanilla's round step has nothing to re-snap.
    //
    //   Fix 4 — FeesPanelInitPatch now calls ApplyB2FeesPanelOverrides() after
    //   AttachB2FeesPanelListeners().  Previously the slider ranges were not set
    //   until LoadPanel, so a player drag before the first panel open would fire
    //   with the slider at its uninitialised Unity default position (minValue = 1),
    //   writing £1 to EconomyData.  The subsequent hourly tick then reset the field
    //   to base × multiplier.
    //
    //   SliderDef gains VanillaMin to support scaled minValue.  The inner Slider
    //   field is cached once per panel instance via GetInnerSlider().
    //
    //   Diagnostic logging reduced to entry/exit lines and change-only multiplier
    //   writes.  Per-slider dump removed.
    //
    // =========================================================================

    // -------------------------------------------------------------------------
    // SliderDef
    //
    // Describes one entry in the in-game Fees panel slider table.
    //
    // FieldName     — private field name on FeesPanelUI that holds the
    //                 ValueSliderContainerUI instance for this fee.  Resolved
    //                 once per panel instance via AccessTools reflection.
    // VanillaMin    — the vanilla slider minimum for this fee.  Used to compute
    //                 the scaled slider MinValue: displayMin = VanillaMin × multiplier.
    // VanillaMax    — the vanilla FeeMax constant for this fee (from EconomyData).
    //                 Used to compute the scaled slider MaxValue at runtime:
    //                 displayMax = VanillaMax × multiplier.
    // DisplayStep   — the vanilla slider snap step.  Scaled the same way:
    //                 displayStep = DisplayStep × multiplier, so the step
    //                 stays proportional to the range at any multiplier.
    // GetMultiplier — delegate returning the current snapped multiplier value
    //                 for this fee.  Called each time the panel is refreshed.
    // Slider        — resolved reference to the live UI slider wrapper.  Null
    //                 until ResolveSlider() is called against a FeesPanelUI instance.
    // -------------------------------------------------------------------------
    internal sealed class SliderDef
    {
        public string FieldName { get; }
        public float VanillaMin { get; }
        public float VanillaMax { get; }
        public float DisplayStep { get; }
        public System.Func<float> GetMultiplier { get; }
        public ValueSliderContainerUI Slider { get; set; }

        // Cached reference to the private Unity Slider inside the wrapper.
        // Populated by GetInnerSlider() on first access.
        private Slider _innerSlider;

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
    [BepInPlugin("com.ktl.aceo.tweaks.economy", "KTL Economy Tweaks", "1.1.0")]
    public class GrandMasterPlugin : BaseUnityPlugin
    {
        // -------------------------------------------------------------------------
        // BepInEx config entries — one per fee category multiplier.
        // All bound in BindConfig() with explicit AcceptableValueRange so the
        // BepInEx config manager enforces min/max without additional clamping.
        // -------------------------------------------------------------------------

        public static ConfigEntry<bool> EnableEconomy;

        // Runway fees — charged to airlines per landing/takeoff.
        // Vanilla defaults: Small £500, Medium £1,500, Large £2,500.
        public static ConfigEntry<float> SmallRunwayIncome;
        public static ConfigEntry<float> MediumRunwayIncome;
        public static ConfigEntry<float> LargeRunwayIncome;

        // Stand parking fees — charged to airlines per stand occupancy hour.
        // Vanilla defaults: Small £400, Medium £500, Large £600.
        // Note: only smallStandParkingSlider exists in FeesPanelUI.  Medium and
        // large stand fees are applied via ApplyEconomyFeeMultipliers() directly;
        // they have no corresponding in-game slider and cannot be dragged manually.
        public static ConfigEntry<float> SmallStandIncome;
        public static ConfigEntry<float> MediumStandIncome;
        public static ConfigEntry<float> LargeStandIncome;

        // Aircraft service fees — charged to airlines per aircraft serviced.
        // Vanilla defaults: Hangar Repair £600, Cabin Cleaning £2.50/aircraft,
        //   De-icing Fluid £10/litre, Catering Meal £5/meal.
        public static ConfigEntry<float> HangarRepairIncome;
        public static ConfigEntry<float> CabinCleaningIncome;
        public static ConfigEntry<float> DeicingFluidIncome;
        public static ConfigEntry<float> CateringMealIncome;

        // Handling fees — charged to airlines per passenger or bag processed.
        // Vanilla defaults: Baggage Handling £10, Passenger Handling £15.
        public static ConfigEntry<float> BaggageHandlingIncome;
        public static ConfigEntry<float> PassengerHandlingIncome;

        // Car parking fees — charged directly to passengers.
        // Vanilla defaults: Short-term £10, Long-term £200.
        // Short-term is per-hour; long-term is per-day (divided by 24 in-game).
        public static ConfigEntry<float> ShortTermParkingIncome;
        public static ConfigEntry<float> LongTermParkingIncome;

        // Aviation fuel prices — charged to airlines per litre dispensed.
        // Vanilla defaults: Avgas 100LL £2.00/litre, Jet A1 £0.50/litre.
        public static ConfigEntry<float> Avgas100LLIncome;
        public static ConfigEntry<float> JetA1Income;

        // -------------------------------------------------------------------------
        // Runtime state flags.
        //
        // BaseFeeCacheInitialized — set true after EnsureBaseFeeCacheInitialized()
        //   runs successfully.  Guards all multiplier application calls so they
        //   never execute before vanilla values have been captured.
        //
        // IsSyncingFeesPanelDisplay — set true inside ApplyB2FeesPanelOverrides()
        //   while slider values are being written programmatically.  Acts as a
        //   secondary guard for our own OnValueChanged listeners in addition to
        //   the direct-write strategy.
        // -------------------------------------------------------------------------
        public static bool BaseFeeCacheInitialized;
        public static bool IsSyncingFeesPanelDisplay;

        // -------------------------------------------------------------------------
        // Base fee cache.
        //
        // Seeded once per session from EconomyData vanilla defaults at load or new
        // game.  These values are the permanent vanilla reference — they are never
        // written to by slider interaction, save/load, or multiplier application.
        //
        // All vanilla fee defaults equal EconomyData.FeeMax / 2 (confirmed from
        // decompiled EconomyData.cs).  The cache stores the default, not the max,
        // because ApplyEconomyFeeMultipliers() computes:
        //   scaledFee = baseFee × multiplier
        // At multiplier 1.0 this reproduces exactly the vanilla default.
        // -------------------------------------------------------------------------
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

        // _isNormalizingConfig — re-entrancy guard for NormalizeAndSaveConfigValues()
        // and the live SettingChanged handlers.  Prevents a config write triggered
        // by snapping from re-entering the snap logic recursively.
        private bool _isNormalizingConfig;

        // HookedFeesPanelInstanceIds — tracks which FeesPanelUI instances have
        // already had our OnValueChanged listeners attached.  FeesPanelUI can be
        // instantiated more than once in a session (e.g. after scene reload), so
        // we check the Unity instance ID before attaching to avoid duplicate hooks.
        private static readonly HashSet<int> HookedFeesPanelInstanceIds = new HashSet<int>();

        // -------------------------------------------------------------------------
        // Slider table.
        //
        // Each SliderDef maps one in-game Fees panel slider to its vanilla min/max,
        // display step, and the multiplier getter for its fee category.
        //
        // VanillaMin / VanillaMax values from decompiled FeesPanelUI.cs and
        // EconomyData.cs (FeeMin / FeeMax constants):
        //   Small Runway    £100 / £1,000   Medium Runway    £500 / £3,000
        //   Large Runway  £1,000 / £5,000   Small Stand       £50 /   £800
        //   Hangar Repair   £100 / £1,200
        //   Avgas 100LL   £0.50 /    £4/L   Jet A1          £0.10 /    £1/L
        //   Catering Meal  £1.0 /   £10/m   Cabin Cleaning  £0.50 /    £5/ac
        //   De-icing       £2.0 /   £20/L
        //   Passenger Hdl    £5 /     £30   Baggage Hdl        £3 /     £20
        //   Short Parking    £2 /     £20   Long Parking      £50 /    £400
        //
        // Medium and large stand sliders do not exist in vanilla FeesPanelUI and
        // are therefore absent from this table.  Their fees are still scaled by
        // ApplyEconomyFeeMultipliers() — they just cannot be dragged manually.
        // -------------------------------------------------------------------------
        private static SliderDef[] _sliderTable;

        private static void BuildSliderTable()
        {
            _sliderTable = new[]
            {
                //                                                  min     max     step
                new SliderDef("smallAircraftRunwaySlider",          100f,  1000f,   50f,   () => GetRunwayStandValue(SmallRunwayIncome)),
                new SliderDef("mediumAircraftRunwaySlider",         500f,  3000f,   50f,   () => GetRunwayStandValue(MediumRunwayIncome)),
                new SliderDef("largeAircraftRunwaySlider",         1000f,  5000f,  100f,   () => GetRunwayStandValue(LargeRunwayIncome)),
                new SliderDef("smallStandParkingSlider",             50f,   800f,   10f,   () => GetRunwayStandValue(SmallStandIncome)),
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

        // -------------------------------------------------------------------------
        // Awake
        //
        // Standard BepInEx entry point.  Config is bound before the slider table is
        // built because SliderDef delegates capture the config entries by reference.
        // NormalizeAndSaveConfigValues() snaps any out-of-step values that may have
        // been hand-edited in the .cfg file before the game reads them.
        // Each harmony.PatchAll() call is intentionally separate so that a single
        // patch failure does not silently prevent the remaining patches from loading.
        // -------------------------------------------------------------------------
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

            Logger.LogInfo("KTL Economy Tweaks 1.1.0 loaded.");
        }

        // -------------------------------------------------------------------------
        // Start
        //
        // Watermark registration runs in Start rather than Awake because
        // WatermarkUtils requires other singletons to be present, which are not
        // guaranteed during Awake.
        //
        // EventDispatcher hooks reset the base fee cache on load and new game so
        // vanilla defaults are always re-read cleanly at the start of each session.
        // This is necessary because EconomyData is re-created from saved JSON on
        // load — any previously applied multipliers embedded in the save would be
        // re-read as the new "base", compounding on subsequent multiplier writes.
        // Resetting the cache and re-seeding from vanilla prevents this.
        // -------------------------------------------------------------------------
        void Start()
        {
            WatermarkUtils.Register(new WatermarkInfo("KTL-ET", "1.1.0", true));

            EventDispatcher.EndOfLoad += OnSaveLoaded;
            EventDispatcher.NewGameStarted += OnNewGame;
        }

        // =========================================================================
        // Session lifecycle
        //
        // Both handlers follow the same pattern:
        //   1. Invalidate the base cache so EnsureBaseFeeCacheInitialized() will
        //      re-seed from the freshly loaded EconomyData.
        //   2. Clear the hooked panel set so listeners are re-attached to new
        //      FeesPanelUI instances created for the new session.
        //   3. Seed the cache and apply multipliers immediately so fee values are
        //      correct before the first hourly tick fires.
        // =========================================================================
        private static void OnSaveLoaded(SaveLoadGameDataController _)
        {
            BaseFeeCacheInitialized = false;
            HookedFeesPanelInstanceIds.Clear();

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

            EconomyData data = GetEconomyData();
            if (data != null)
            {
                EnsureBaseFeeCacheInitialized(data);
                ApplyEconomyFeeMultipliers(data, "OnNewGame");
                Logger.LogInfo("New game — base fees seeded from vanilla, multipliers applied.");
            }
        }

        // =========================================================================
        // Base fee cache
        //
        // Seeds the static Base* fields from EconomyData vanilla Default properties.
        // Called once per session — the BaseFeeCacheInitialized guard prevents any
        // subsequent call (e.g. from a Fees panel open) from overwriting the clean
        // vanilla values with already-multiplied ones.
        //
        // All vanilla defaults are FeeMax / 2 (confirmed from EconomyData.cs).
        // Seeding from the Default property rather than the live field ensures we
        // always capture the true vanilla value regardless of what has already been
        // written to EconomyData by the time this is called.
        // =========================================================================
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

        // =========================================================================
        // Config binding
        //
        // All entries use AcceptableValueRange so BepInEx enforces bounds both in
        // the config manager UI and when loading hand-edited .cfg files.
        // NormalizeAndSaveConfigValues() additionally snaps values to the defined
        // step grid, which AcceptableValueRange alone does not enforce.
        //
        // Section numbers match the in-game Fees panel layout order so the
        // BepInEx config manager presents categories in a familiar sequence.
        // =========================================================================
        private void BindConfig()
        {
            EnableEconomy = BindBool("1. Enable", "EnableEconomy", true, "Master toggle for all economy fee multipliers.");

            SmallRunwayIncome = BindFloat("2. Runways", "SmallRunwayIncome", 1f, 0.5f, 2.5f, "Small aircraft runway fee multiplier. 1.0 = vanilla. Low: 0.5 (£50 min, £500 max). High: 2.5 (£250 min, £2,500 max). Step: 10%.");
            MediumRunwayIncome = BindFloat("2. Runways", "MediumRunwayIncome", 1f, 0.5f, 2.5f, "Medium aircraft runway fee multiplier. 1.0 = vanilla. Low: 0.5 (£250 min, £1,500 max). High: 2.5 (£1,250 min, £7,500 max). Step: 10%.");
            LargeRunwayIncome = BindFloat("2. Runways", "LargeRunwayIncome", 1f, 0.5f, 2.5f, "Large aircraft runway fee multiplier. 1.0 = vanilla. Low: 0.5 (£500 min, £2,500 max). High: 2.5 (£2,500 min, £12,500 max). Step: 10%.");

            SmallStandIncome = BindFloat("3. Stands", "SmallStandIncome", 1f, 0.5f, 2.5f, "Small/GA stand parking fee multiplier. 1.0 = vanilla. Low: 0.5 (£25 min, £400 max). High: 2.5 (£125 min, £2,000 max). Step: 10%.");
            MediumStandIncome = BindFloat("3. Stands", "MediumStandIncome", 1f, 0.5f, 2.5f, "Medium stand parking fee multiplier. 1.0 = vanilla. No slider — applied directly. Step: 10%.");
            LargeStandIncome = BindFloat("3. Stands", "LargeStandIncome", 1f, 0.5f, 2.5f, "Large stand parking fee multiplier. 1.0 = vanilla. No slider — applied directly. Step: 10%.");

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

        // =========================================================================
        // Step-snapping helpers
        //
        // BepInEx AcceptableValueRange enforces min/max but does not snap values to
        // a step grid.  These helpers clamp and snap to the defined step so that:
        //   - Hand-edited .cfg values are rounded to the nearest valid step on load.
        //   - Live slider drags in the config manager snap cleanly without drift.
        //
        // Each fee category has its own helper because they have different ranges
        // and steps.  The helpers are public so patch classes can call them directly
        // without needing to re-implement the clamping logic.
        //
        // Vanilla fee ranges and steps (for reference):
        //   Runways / Stands   : 0.5–2.5,  10% steps  — broad range, primary income
        //   Aircraft Services  : 0.5–1.5,  10% steps  — per-aircraft, tighter band
        //   Handling           : 0.5–1.75,  5% steps  — per-passenger, fine control
        //   Short-term Parking : 0.5–1.5,  10% steps  — passenger price-sensitive
        //   Long-term Parking  : 0.5–2.0,  10% steps  — wider ceiling, less elastic
        //   Avgas 100LL        : 0.5–2.5,   5% steps  — GA fuel, niche revenue
        //   Jet A1             : 0.5–2.0,   5% steps  — airline fuel, high elasticity
        // =========================================================================
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

        // =========================================================================
        // Live config change handlers
        //
        // Registered via SettingChanged so that adjusting a multiplier in the
        // BepInEx config manager mid-game snaps the new value to the step grid and
        // saves immediately, without requiring a game restart or manual .cfg edit.
        //
        // _isNormalizingConfig guards against re-entrancy: writing entry.Value
        // inside the handler fires SettingChanged again, which would loop.  The
        // flag is set before the write and cleared in the finally block.
        // =========================================================================
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

        // =========================================================================
        // Fee application
        //
        // Target: EconomyData fields (written directly, not via patch).
        //
        // Vanilla: EconomyData fields are written once at construction from their
        //   Default properties (FeeMax / 2), and subsequently updated when the
        //   player drags a Fees panel slider.  No multiplier exists in vanilla.
        //
        // Mod: Each field is overwritten with base × snapped multiplier, rounded
        //   to an appropriate step to avoid floating-point values that look odd in
        //   the UI.  Rounding steps match vanilla slider snap increments:
        //     Runway fees          — nearest £100
        //     Stand parking fees   — nearest £50
        //     Hangar repair        — nearest £50
        //     Cabin cleaning       — nearest £0.50
        //     De-icing fluid       — nearest £0.50
        //     Catering meal        — nearest £0.10
        //     Handling fees        — nearest £1
        //     Car parking fees     — Short: nearest £1, Long: nearest £50
        //     Fuel prices          — Avgas: nearest £0.10, Jet A1: nearest £0.05
        //
        // The base cache is never written to here.  Only the live EconomyData
        // fields are modified.  This means the base remains available for future
        // multiplier recalculations regardless of how many times this runs.
        //
        // RefreshAirportRatingEvaluationCurves() is called at the end of every
        // application so rating curves always reflect the current live fee values.
        // =========================================================================
        public static void ApplyEconomyFeeMultipliers(EconomyData data, string callerContext = "unknown")
        {
            if (!EnableEconomy.Value || data == null) return;
            if (!BaseFeeCacheInitialized) return;

            Logger.LogDebug($"[ApplyMultipliers] Called from: {callerContext}");

            float prev; float next;

            prev = data.smallAircraftRunwayFee;
            data.smallAircraftRunwayFee = Round(BaseSmallAircraftRunwayFee * GetRunwayStandValue(SmallRunwayIncome), 100f);
            next = data.smallAircraftRunwayFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] smallAircraftRunwayFee: {prev:F2} -> {next:F2}");

            prev = data.mediumAircraftRunwayFee;
            data.mediumAircraftRunwayFee = Round(BaseMediumAircraftRunwayFee * GetRunwayStandValue(MediumRunwayIncome), 100f);
            next = data.mediumAircraftRunwayFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] mediumAircraftRunwayFee: {prev:F2} -> {next:F2}");

            prev = data.largeAircraftRunwayFee;
            data.largeAircraftRunwayFee = Round(BaseLargeAircraftRunwayFee * GetRunwayStandValue(LargeRunwayIncome), 100f);
            next = data.largeAircraftRunwayFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] largeAircraftRunwayFee: {prev:F2} -> {next:F2}");

            prev = data.smallStandParkingFee;
            data.smallStandParkingFee = Round(BaseSmallStandParkingFee * GetRunwayStandValue(SmallStandIncome), 50f);
            next = data.smallStandParkingFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] smallStandParkingFee: {prev:F2} -> {next:F2}");

            prev = data.mediumStandParkingFee;
            data.mediumStandParkingFee = Round(BaseMediumStandParkingFee * GetRunwayStandValue(MediumStandIncome), 50f);
            next = data.mediumStandParkingFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] mediumStandParkingFee: {prev:F2} -> {next:F2}");

            prev = data.largeStandParkingFee;
            data.largeStandParkingFee = Round(BaseLargeStandParkingFee * GetRunwayStandValue(LargeStandIncome), 50f);
            next = data.largeStandParkingFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] largeStandParkingFee: {prev:F2} -> {next:F2}");

            prev = data.hangarAircraftRepairFee;
            data.hangarAircraftRepairFee = Round(BaseHangarAircraftRepairFee * GetServiceValue(HangarRepairIncome), 50f);
            next = data.hangarAircraftRepairFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] hangarAircraftRepairFee: {prev:F2} -> {next:F2}");

            prev = data.aircraftCabinCleaningCost;
            data.aircraftCabinCleaningCost = Round(BaseAircraftCabinCleaningCost * GetServiceValue(CabinCleaningIncome), 0.5f);
            next = data.aircraftCabinCleaningCost;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] aircraftCabinCleaningCost: {prev:F3} -> {next:F3}");

            prev = data.deicingFluidCost;
            data.deicingFluidCost = Round(BaseDeicingFluidCost * GetServiceValue(DeicingFluidIncome), 0.5f);
            next = data.deicingFluidCost;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] deicingFluidCost: {prev:F3} -> {next:F3}");

            prev = data.cateringMealCost;
            data.cateringMealCost = Round(BaseCateringMealCost * GetServiceValue(CateringMealIncome), 0.1f);
            next = data.cateringMealCost;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] cateringMealCost: {prev:F3} -> {next:F3}");

            prev = data.baggageHandlingFee;
            data.baggageHandlingFee = Round(BaseBaggageHandlingFee * GetHandlingValue(BaggageHandlingIncome), 1f);
            next = data.baggageHandlingFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] baggageHandlingFee: {prev:F2} -> {next:F2}");

            prev = data.passengerHandlingFee;
            data.passengerHandlingFee = Round(BasePassengerHandlingFee * GetHandlingValue(PassengerHandlingIncome), 1f);
            next = data.passengerHandlingFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] passengerHandlingFee: {prev:F2} -> {next:F2}");

            prev = data.shortTermParkingFee;
            data.shortTermParkingFee = Round(BaseShortTermParkingFee * GetShortTermParkingValue(ShortTermParkingIncome), 1f);
            next = data.shortTermParkingFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] shortTermParkingFee: {prev:F2} -> {next:F2}");

            prev = data.longTermParkingFee;
            data.longTermParkingFee = Round(BaseLongTermParkingFee * GetLongTermParkingValue(LongTermParkingIncome), 50f);
            next = data.longTermParkingFee;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] longTermParkingFee: {prev:F2} -> {next:F2}");

            prev = data.avgas100LLFuelCost;
            data.avgas100LLFuelCost = Round(BaseAvgas100LLFuelCost * GetAvgasValue(Avgas100LLIncome), 0.1f);
            next = data.avgas100LLFuelCost;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] avgas100LLFuelCost: {prev:F3} -> {next:F3}");

            prev = data.jetA1FuelCost;
            data.jetA1FuelCost = Round(BaseJetA1FuelCost * GetJetA1Value(JetA1Income), 0.05f);
            next = data.jetA1FuelCost;
            if (!Mathf.Approximately(prev, next)) Logger.LogDebug($"[ApplyMultipliers] jetA1FuelCost: {prev:F3} -> {next:F3}");

            RefreshAirportRatingEvaluationCurves();
        }

        // =========================================================================
        // Rating curve refresh
        //
        // Target: AirportRatingManager private AnimationCurve fields (written via
        //   AccessTools reflection — same approach used by vanilla's own
        //   InitializeEvaluationCurves() which is private and inaccessible directly).
        //
        // Vanilla: AirportRatingManager.InitializeEvaluationCurves() builds each
        //   curve once at startup using the hardcoded FeeMax constant as the x-axis
        //   anchor:  EaseInOut(FeeMax × 0.4, 1f,  FeeMax × 1.0, 0f)
        //   meaning: at 40% of FeeMax the rating is 1 (fully happy/green);
        //            at 100% of FeeMax the rating is 0 (fully unhappy/red).
        //   Since all vanilla fee defaults are FeeMax / 2, the default fee sits at
        //   the midpoint of the green zone — correct and intentional vanilla design.
        //
        // Problem without this mod: applying a 2× multiplier doubles the live fee
        //   but leaves the curve anchored at the original FeeMax.  The doubled fee
        //   now evaluates to a point well into the orange/red zone even though from
        //   the player's perspective it is the new "normal" price.
        //
        // Mod fix: rebuild each curve using BuildCurve(baseFee, multiplier), which
        //   reconstructs a scaled FeeMax as baseFee × 2 × multiplier and applies
        //   the same 0.4×/1.0× thresholds vanilla uses.  At multiplier 1.0 the
        //   result is bit-identical to what vanilla initialises.  At any other
        //   multiplier the green zone shifts proportionally so the default position
        //   of the scaled fee always reads green in both the Fees panel and the
        //   lower-UI report bars.
        //
        // Called after every ApplyEconomyFeeMultipliers() and after every in-game
        // slider drag so curves stay in sync with live fee values at all times.
        // Also patched onto AirportRatingManager.InitializeEvaluationCurves() via
        // AirportRatingManagerInitCurvesPatch so our curves replace the vanilla ones
        // at scene load before any rating evaluations run.
        // =========================================================================
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

        // =========================================================================
        // Fees panel integration
        //
        // Vanilla FeesPanelUI manages its own slider display independently of the
        // EconomyData multiplier system.  Three points of intervention are needed:
        //
        //   1. InitializePanel (FeesPanelInitPatch) — called once when the panel
        //      is first created.  We seed the base cache, apply multipliers to
        //      EconomyData, attach our OnValueChanged listeners, then immediately
        //      call ApplyB2FeesPanelOverrides() so slider ranges are correct before
        //      the player can interact with them.
        //
        //   2. LoadPanel (FeesPanelDisplayPatch) — called each time the player
        //      opens the Fees UI.  Vanilla reads slider values from EconomyData
        //      and sets MaxValue from the hardcoded FeeMax constants, which are
        //      unaware of our multiplier.  ApplyB2FeesPanelOverrides() corrects
        //      the slider min, max, step and value after vanilla's load runs.
        //
        //   3. OnValueChanged listeners (attached in AttachB2FeesPanelListeners)
        //      — fire when the player drags a slider.  The raw value is snapped to
        //      the mod's displayStep grid before being written to EconomyData, so
        //      the value is always on a clean boundary that vanilla's internal snap
        //      cannot re-snap to a different position.
        //
        // Note: medium and large stand sliders do not exist in vanilla FeesPanelUI
        // so they cannot be dragged manually.  Their fees are still scaled by
        // ApplyEconomyFeeMultipliers() on every hourly tick.
        // =========================================================================
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

                    // Snap the raw drag value to the nearest displayStep multiple
                    // before writing to EconomyData.  This ensures the value is on
                    // the mod's step grid so the subsequent ApplyB2FeesPanelOverrides()
                    // write lands on the same value and vanilla's internal round step
                    // has nothing to re-snap to a different position.
                    float multiplier = capturedDef.GetMultiplier();
                    float step = capturedDef.DisplayStep * multiplier;
                    float snapped = step > 0f ? Mathf.Round(value / step) * step : value;

                    Logger.LogDebug($"[SliderDrag] {capturedDef.FieldName}: raw = {value:F3}, snapped = {snapped:F3}");

                    SetLiveEconomyValue(capturedDef.FieldName, snapped, data);
                    RefreshAirportRatingEvaluationCurves();
                    ApplyB2FeesPanelOverrides(feesPanel, "SliderDrag");
                });
            }
        }

        // Maps a slider field name to its corresponding EconomyData field and writes
        // the given value.  Only fields that have an in-game slider are listed here;
        // medium and large stand fees are absent because no slider exists for them.
        private static void SetLiveEconomyValue(string fieldName, float value, EconomyData data)
        {
            switch (fieldName)
            {
                case "smallAircraftRunwaySlider": data.smallAircraftRunwayFee = value; break;
                case "mediumAircraftRunwaySlider": data.mediumAircraftRunwayFee = value; break;
                case "largeAircraftRunwaySlider": data.largeAircraftRunwayFee = value; break;
                case "smallStandParkingSlider": data.smallStandParkingFee = value; break;
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

        // =========================================================================
        // ApplyB2FeesPanelOverrides
        //
        // Sets each slider's min, max, step, and value to reflect the current
        // multiplier-scaled range and live EconomyData value.
        //
        // Write strategy (v1.1.0):
        //   1. IsSyncingFeesPanelDisplay is set to suppress our own listeners.
        //   2. All listeners are removed from each slider's onValueChanged event
        //      to prevent vanilla's InitializePanel listener from writing back to
        //      EconomyData during the programmatic update.
        //   3. Values are written directly to the inner Unity Slider via reflection,
        //      bypassing the ValueSliderContainerUI.Value property setter and the
        //      internal Awake listener (RoundValueChanged + UpdateContainer).
        //   4. UpdateContainer() is called on the wrapper to refresh the display text
        //      and colour band without triggering the event chain.
        //   5. All listeners are restored and IsSyncingFeesPanelDisplay is cleared.
        //
        // ApplyEconomyFeeMultipliers() is intentionally NOT called here — it sets the
        // config-driven baseline and must not overwrite a value the player just chose.
        // =========================================================================
        public static void ApplyB2FeesPanelOverrides(FeesPanelUI feesPanel, string callerContext = "unknown")
        {
            if (!EnableEconomy.Value || feesPanel == null) return;
            if (!BaseFeeCacheInitialized) return;

            EconomyData data = GetEconomyData();
            if (data == null) return;

            Logger.LogDebug($"[ApplyOverrides] Called from: {callerContext}");

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
                    var listeners = inner.onValueChanged;
                    listeners.RemoveAllListeners();
                    Logger.LogDebug($"[ApplyOverrides] {def.FieldName}: listeners removed, writing min={displayMin:F3} max={displayMax:F3} val={liveValue:F3}");

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

                    Logger.LogDebug($"[ApplyOverrides] {def.FieldName}: listeners restored.");
                }
            }
            finally { IsSyncingFeesPanelDisplay = false; }
        }

        // Returns the current live value of a fee from EconomyData by slider field
        // name.  Used by ApplyB2FeesPanelOverrides() to populate slider display
        // values without needing direct EconomyData field references per entry.
        private static float GetLiveEconomyValue(string fieldName, EconomyData data)
        {
            switch (fieldName)
            {
                case "smallAircraftRunwaySlider": return data.smallAircraftRunwayFee;
                case "mediumAircraftRunwaySlider": return data.mediumAircraftRunwayFee;
                case "largeAircraftRunwaySlider": return data.largeAircraftRunwayFee;
                case "smallStandParkingSlider": return data.smallStandParkingFee;
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

        // =========================================================================
        // Shared utilities
        // =========================================================================

        // Rounds a float to the nearest multiple of step, then trims floating-point
        // noise to 3 decimal places.  Used to keep fee values clean in EconomyData
        // and avoid values like £1,499.9999 appearing in the UI.
        private static float Round(float value, float step)
        {
            if (step <= 0f) return value;
            return (float)System.Math.Round(Mathf.Round(value / step) * step, 3);
        }

        // Writes newValue to entry only if it differs meaningfully (beyond floating-
        // point noise at 3dp).  Returns true if a write occurred so callers can
        // batch-detect whether a Config.Save() is needed.
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

    // -------------------------------------------------------------------------
    // EconomyFeeRefreshPatch
    //
    // Target: EconomyController.UpdateHourlyBalanceUI()
    // Type:   Prefix
    //
    // Vanilla: UpdateHourlyBalanceUI() reads current EconomyData fee fields to
    //   populate the economy summary panel.  It does not modify fee values.
    //
    // Mod: A Prefix runs ApplyEconomyFeeMultipliers() before the vanilla method
    //   reads the fields.  This ensures that on every hourly tick the fee values
    //   in EconomyData are up to date with the current multiplier settings before
    //   the UI reads them — catching any case where EconomyData was loaded or
    //   modified without going through our patched Fees panel path.
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // FeesPanelInitPatch
    //
    // Target: FeesPanelUI.InitializePanel()
    // Type:   Postfix
    //
    // Vanilla: InitializePanel() locates and caches private slider references,
    //   registers OnValueChanged listeners that write slider values back to
    //   EconomyData, and sets up the panel layout.  Called once when the panel
    //   GameObject is first initialised.
    //
    // Mod: After vanilla initialisation completes, we seed the base fee cache
    //   (if not already seeded), apply multipliers so EconomyData is correct,
    //   attach our listeners, then immediately call ApplyB2FeesPanelOverrides()
    //   so slider ranges are set before the player can interact with them.
    //   Without this call a drag before the first LoadPanel would fire with the
    //   slider at its uninitialised Unity default position (minValue = 1),
    //   writing £1 to EconomyData.
    // -------------------------------------------------------------------------
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
            GrandMasterPlugin.AttachB2FeesPanelListeners(__instance);
            GrandMasterPlugin.ApplyB2FeesPanelOverrides(__instance, "FeesPanelInitPatch");
        }
    }

    // -------------------------------------------------------------------------
    // FeesPanelDisplayPatch
    //
    // Target: FeesPanelUI.LoadPanel()
    // Type:   Postfix
    //
    // Vanilla: LoadPanel() is called each time the player opens the Fees UI.
    //   It sets each slider's MaxValue from the hardcoded EconomyData.FeeMax
    //   constants and its Value from the current EconomyData fee field.  These
    //   FeeMax constants are fixed in the game binary and do not account for
    //   our multiplier — at 2× the slider would be pinned at the top of its
    //   vanilla range even though the actual fee is double the vanilla max.
    //
    // Mod: After vanilla LoadPanel() runs, ApplyB2FeesPanelOverrides() rewrites
    //   each slider's min, max, step and value to the scaled range.  The direct
    //   inner-Slider write strategy ensures vanilla's listeners cannot re-snap
    //   the values during this correction.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(FeesPanelUI), "LoadPanel")]
    public static class FeesPanelDisplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(FeesPanelUI __instance)
        {
            if (__instance == null || !GrandMasterPlugin.EnableEconomy.Value) return;
            GrandMasterPlugin.ApplyB2FeesPanelOverrides(__instance, "FeesPanelDisplayPatch");
        }
    }

    // -------------------------------------------------------------------------
    // AirportRatingManagerInitCurvesPatch
    //
    // Target: AirportRatingManager.InitializeEvaluationCurves()
    // Type:   Postfix
    //
    // Vanilla: InitializeEvaluationCurves() is called once at scene load and
    //   builds all rating AnimationCurve fields using the hardcoded FeeMax
    //   constants as x-axis anchors.  These curves are used by every subsequent
    //   call to EvaluateRating() to convert a live fee value into a satisfaction
    //   score for the GA, airline, and passenger report bars.
    //
    // Mod: After vanilla builds its curves, RefreshAirportRatingEvaluationCurves()
    //   immediately replaces them with multiplier-scaled equivalents via
    //   AccessTools reflection.  This ensures that from the very first rating
    //   evaluation after load, the curves reflect our multiplier settings rather
    //   than the vanilla FeeMax anchors.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(AirportRatingManager), "InitializeEvaluationCurves")]
    public static class AirportRatingManagerInitCurvesPatch
    {
        [HarmonyPostfix]
        public static void Postfix() => GrandMasterPlugin.RefreshAirportRatingEvaluationCurves();
    }
}
