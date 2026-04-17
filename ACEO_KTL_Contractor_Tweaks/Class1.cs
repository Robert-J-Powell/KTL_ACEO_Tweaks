using System.Collections.Generic;
using System.Reflection;
using AirportCEOModLoader.WatermarkUtils;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

// References required:
//   Unity.TextMeshPro.dll
//   AirportCEOModLoader.dll (D:\SteamLibrary\steamapps\workshop\content\673610\3109136766\plugins\AirportCEOModLoader.dll)

namespace KTL_Contract_Tweaks
{
    // =========================================================================
    // KTL_Contract_Tweaks  v1.0.0
    //
    // Part of KTL Contract Tweaks — configurable multipliers for non-airline
    // business contracts, plus optional airline negotiation suppression.
    //
    // Contractors (section 1):
    //   - NumberOfContractors  — scales the deployable contractor cap per firm
    //   - ContractorHourlyCost — scales the hourly cost per deployed contractor
    //
    // Aviation Fuel Supplier (section 2):
    //   - FuelAvgas100LLCost   — scales the purchase cost per litre of Avgas 100LL
    //   - FuelJetA1Cost        — scales the purchase cost per litre of Jet A-1
    //
    // Catering Food Supplier (section 3):
    //   - CateringFoodCost     — scales the purchase cost per food unit
    //
    // De-icing Fluid Supplier (section 4):
    //   - DeicingFluidCost     — scales the purchase cost per litre of de-icing fluid
    //
    // Airlines (section 5):
    //   - EnableAirlineNegotiation — suppresses annual airline contract renegotiation
    //
    // UI surfaces patched:
    //   - ConstructionPanelUI.UpdatePanel                  — contractor max / available count
    //   - SelectedContractUI.SetPanelValues                — contractor contract text
    //   - ContractorModel.GetNegotiationValues             — contractor renegotiation preview
    //   - AviationFuelSupplierModel.get_CostPerLiterAvgas100LL — Avgas cost at runtime
    //   - AviationFuelSupplierModel.get_CostPerLiterJetA1      — Jet A-1 cost at runtime
    //   - AviationFuelSupplierModel.GetNegotiationValues        — fuel renegotiation preview
    //   - CateringFoodSupplierModel.get_CostPerFood        — catering cost at runtime
    //   - CateringFoodSupplierModel.GetNegotiationValues   — catering renegotiation preview
    //   - DeicingFluidSupplierModel.get_CostPerLiter       — de-icing cost at runtime
    //   - DeicingFluidSupplierModel.GetNegotiationValues   — de-icing renegotiation preview
    //   - EmailController.GenerateNegotiationEmail         — airline email suppression
    //   - ContractPanelUI.UpdatePanel                      — airline tile colour suppression
    //   - SelectedContractUI.SetContractPanelValues        — airline negotiate button suppression
    //
    // Excluded by design:
    //   - Contractor count in negotiation panel (vanilla provides no such slider)
    //   - Staff wages / vehicles / fines / tax  (covered by KTL Expenses mod)
    //   - Franchise and bank contracts          (out of scope)
    //
    // Notes on airline negotiation suppression:
    //   When EnableAirlineNegotiation is false, three patches work together:
    //   1. AirlineNegotiationEmailPatch — filters airlines from the CFO email.
    //   2. AirlineContractTilesPatch — Prefix on ContractPanelUI.UpdatePanel.
    //      Sweeps negotiationActive = false on all airline contracts by walking
    //      BusinessController.allBusinessArray directly via reflection — this
    //      accesses the raw live backing array rather than a filtered copy,
    //      ensuring the flags are cleared before tile colours are rendered.
    //   3. AirlineNegotiationPanelPatch — Prefix on SetContractPanelValues.
    //      Belt-and-braces reset for the individual contract panel view.
    //
    // =========================================================================
    // PATCH HISTORY
    // =========================================================================
    //
    // v1.0.0 — Version bump to stable. Namespace renamed to KTL_Contract_Tweaks.
    //   Known limitation acknowledged: on saves with a pending contract negotiation
    //   at load time, the affected airline tile may still show orange on first open
    //   of the contract panel.  This is a load-order foible — the game has already
    //   baked negotiationActive = true into the save before the sweep patch runs.
    //   The panel patch corrects the flag when the contract is clicked, so dismissing
    //   and reopening the panel resolves it.  Does not affect saves where suppression
    //   was active before any renewal date was reached.  No code changes.
    //
    //   Post-release cosmetic fix (no version bump — not released at time of change):
    //   Config key names updated to a consistent numeric dot prefix convention across
    //   all sections.  Ensures BepInEx config manager displays keys in logical order
    //   (enable toggle always first) without relying on alphabetical coincidence or
    //   the A_/B_ workaround previously used in sections 3 and 4.
    //   New key names:
    //     1. Contractors : EnableContractors      → 1.EnableContractors
    //                      NumberOfContractors    → 2.NumberOfContractors
    //                      ContractorHourlyCost   → 3.ContractorHourlyCost
    //     2. Fuel        : EnableFuel             → 1.EnableFuel
    //                      FuelAvgas100LLCost     → 2.FuelAvgas100LLCost
    //                      FuelJetA1Cost          → 3.FuelJetA1Cost
    //     3. Catering    : A_EnableCatering       → 1.EnableCatering
    //                      B_CateringFoodCost     → 2.CateringFoodCost
    //     4. De-icing    : A_EnableDeicing        → 1.EnableDeicing
    //                      B_DeicingFluidCost     → 2.DeicingFluidCost
    //     5. Airlines    : EnableAirlineNegotiation — unchanged (solo toggle).
    //   Breaking change for existing cfg files: values will reset to defaults on
    //   first load — users must re-apply their settings.
    //
    // v0.5.2 — Orange tile fix + de-icing/catering config key ordering fix
    //   Fixed: Airline contract tiles still showing orange with suppression on.
    //   Previous AirlineContractTilesPatch used GetListOfActiveBusinessesByType
    //   which returns a filtered copy — flags were reset on the copy, not the
    //   live models before tile rendering.  New approach: walk allBusinessArray
    //   directly via reflection to access the raw backing array.
    //   Fixed: De-icing and Catering toggles now appear above their sliders in
    //   the config manager.  A_/B_ prefixes added to sections 3 and 4 only
    //   (D and C both sort before E alphabetically).
    //   Note: cfg file resets to defaults on first load after 0.5.1 (key name
    //   changes) — re-apply your values after updating.
    //
    // v0.5.1 — Airline panel fix + config key cleanup
    //   Fixed: AirlineNegotiationPanelPatch Postfix → Prefix on
    //   SetContractPanelValues so panel renders correctly on first open.
    //   Changed: Numeric prefixes removed from individual config key names.
    //   Breaking change: cfg file resets to defaults on first load.
    //
    // v0.5.0 — Airline negotiation suppression
    //   Added section 5: Airlines.  EnableAirlineNegotiation (default true).
    //
    // v0.4.0 — De-icing fluid supplier.
    // v0.3.0 — Catering food supplier.
    // v0.2.0 — Aviation fuel supplier.
    // v0.1.0 — Contractor baseline (KTL Contract Tweaks rebrand).
    //
    // =========================================================================

    [BepInPlugin("com.ktl.aceo.tweaks.contracts", "KTL Contract Tweaks", "1.0.0")]
    public class ContractorTweaksPlugin : BaseUnityPlugin
    {
        // =====================================================================
        // Config entries — 1. Contractors
        // =====================================================================

        public static ConfigEntry<bool> EnableContractors;
        public static ConfigEntry<float> NumberOfContractors;
        public static ConfigEntry<float> ContractorHourlyCost;

        // =====================================================================
        // Config entries — 2. Aviation Fuel Supplier
        // =====================================================================

        public static ConfigEntry<bool> EnableFuel;
        public static ConfigEntry<float> FuelAvgas100LLCost;
        public static ConfigEntry<float> FuelJetA1Cost;

        // =====================================================================
        // Config entries — 3. Catering Food Supplier
        // =====================================================================

        public static ConfigEntry<bool> EnableCatering;
        public static ConfigEntry<float> CateringFoodCost;

        // =====================================================================
        // Config entries — 4. De-icing Fluid Supplier
        // =====================================================================

        public static ConfigEntry<bool> EnableDeicing;
        public static ConfigEntry<float> DeicingFluidCost;

        // =====================================================================
        // Config entries — 5. Airlines
        // =====================================================================

        public static ConfigEntry<bool> EnableAirlineNegotiation;

        internal static new ManualLogSource Logger;
        private bool _isNormalizingConfig;

        // Cached reflection field for BusinessController.allBusinessArray.
        // Populated once on Awake — avoids per-frame reflection cost.
        private static FieldInfo _allBusinessArrayField;

        // =====================================================================
        // Lifecycle
        // =====================================================================

        void Awake()
        {
            Logger = base.Logger;
            BindConfig();
            NormalizeAndSaveConfigValues();
            RegisterLiveConfigHandlers();
            CacheReflectionFields();
            RegisterPatches();
        }

        void Start()
        {
            WatermarkUtils.Register(new WatermarkInfo("KTL-CT", "1.0.0", true));
            Logger.LogInfo("KTL Contract Tweaks watermark registered.");
        }

        private static void CacheReflectionFields()
        {
            _allBusinessArrayField = AccessTools.Field(typeof(BusinessController), "allBusinessArray");
            if (_allBusinessArrayField == null)
                Logger.LogWarning("Could not cache allBusinessArray field — AirlineContractTilesPatch may not function.");
            else
                Logger.LogInfo("allBusinessArray field cached.");
        }

        /// <summary>
        /// Sweeps negotiationActive = false on all AirlineModel contracts by
        /// walking the raw allBusinessArray backing array.  Called from
        /// AirlineContractTilesPatch before tile colours are rendered.
        /// </summary>
        public static void SweepAirlineNegotiationFlags()
        {
            if (_allBusinessArrayField == null) return;

            var controller = SingletonNonDestroy<BusinessController>.Instance;
            if (controller == null) return;

            // allBusinessArray is a DynamicArray<BusinessModel>.
            // We access .array (the raw BusinessModel[]) and .Length directly.
            var dynamicArray = _allBusinessArrayField.GetValue(controller);
            if (dynamicArray == null) return;

            var arrayField = AccessTools.Field(dynamicArray.GetType(), "array");
            var lengthField = AccessTools.Field(dynamicArray.GetType(), "Length");
            if (arrayField == null || lengthField == null) return;

            var rawArray = arrayField.GetValue(dynamicArray) as BusinessModel[];
            int length = (int)lengthField.GetValue(dynamicArray);
            if (rawArray == null) return;

            for (int i = 0; i < length; i++)
            {
                BusinessModel bm = rawArray[i];
                if (bm == null) continue;
                if (bm is AirlineModel && bm.contract != null && bm.contract.negotiationActive)
                    bm.contract.negotiationActive = false;
            }
        }

        // =====================================================================
        // Config binding
        // =====================================================================

        private void BindConfig()
        {
            // --- 1. Contractors ----------------------------------------------
            // Numeric dot prefixes ensure BepInEx displays keys in logical order.
            EnableContractors = Config.Bind(
                "1. Contractors", "1.EnableContractors", true,
                "Master toggle. False restores vanilla contractor behaviour entirely.");

            NumberOfContractors = Config.Bind(
                "1. Contractors", "2.NumberOfContractors", 1.0f,
                new ConfigDescription(
                    "Deployable contractor count multiplier. 1.0 = vanilla. " +
                    "Low: 0.75 (fewer contractors). High: 3.0 (triple vanilla cap). " +
                    "Steps: 5% from 0.75–1.5, then 10% from 1.5–3.0. " +
                    "Clamped to 25–750 regardless of multiplier. " +
                    "Balance with ContractorHourlyCost to control total hourly spend.",
                    new AcceptableValueRange<float>(0.75f, 3.0f)));

            ContractorHourlyCost = Config.Bind(
                "1. Contractors", "3.ContractorHourlyCost", 1.0f,
                new ConfigDescription(
                    "Hourly cost per contractor multiplier. 1.0 = vanilla. " +
                    "Low: 0.5 (half cost). High: 1.75 (75% premium). Step: 0.05. " +
                    "Balance with NumberOfContractors — increasing both simultaneously " +
                    "multiplies total construction expense.",
                    new AcceptableValueRange<float>(0.5f, 1.75f)));

            // --- 2. Aviation Fuel Supplier ------------------------------------
            // Numeric dot prefixes ensure BepInEx displays keys in logical order.
            EnableFuel = Config.Bind(
                "2. Aviation Fuel Supplier", "1.EnableFuel", true,
                "Master toggle. False restores vanilla fuel purchase costs entirely.");

            FuelAvgas100LLCost = Config.Bind(
                "2. Aviation Fuel Supplier", "2.FuelAvgas100LLCost", 1.0f,
                new ConfigDescription(
                    "Avgas 100LL purchase cost multiplier. 1.0 = vanilla. " +
                    "Low: 0.5 (half cost). High: 1.25 (25% premium). Step: 0.05. " +
                    "Capped at 1.25 to preserve margin at vanilla sale prices. " +
                    "Scales what the airport pays the supplier per litre on delivery. " +
                    "Sale price to airlines is controlled by KTL Economy Tweaks.",
                    new AcceptableValueRange<float>(0.5f, 1.25f)));

            FuelJetA1Cost = Config.Bind(
                "2. Aviation Fuel Supplier", "3.FuelJetA1Cost", 1.0f,
                new ConfigDescription(
                    "Jet A-1 purchase cost multiplier. 1.0 = vanilla. " +
                    "Low: 0.5 (half cost). High: 1.25 (25% premium). Step: 0.05. " +
                    "Capped at 1.25 to preserve margin at vanilla sale prices. " +
                    "Scales what the airport pays the supplier per litre on delivery. " +
                    "Sale price to airlines is controlled by KTL Economy Tweaks.",
                    new AcceptableValueRange<float>(0.5f, 1.25f)));

            // --- 3. Catering Food Supplier ------------------------------------
            // Numeric dot prefixes ensure BepInEx displays keys in logical order.
            EnableCatering = Config.Bind(
                "3. Catering Food Supplier", "1.EnableCatering", true,
                "Master toggle. False restores vanilla catering purchase costs entirely.");

            CateringFoodCost = Config.Bind(
                "3. Catering Food Supplier", "2.CateringFoodCost", 1.0f,
                new ConfigDescription(
                    "Catering food purchase cost per unit multiplier. 1.0 = vanilla. " +
                    "Low: 0.5 (half cost). High: 1.25 (25% premium). Step: 0.05. " +
                    "Capped at 1.25 to preserve margin at vanilla catering sale prices. " +
                    "Scales what the airport pays the supplier per food unit on delivery. " +
                    "Sale price to airlines is controlled by KTL Economy Tweaks.",
                    new AcceptableValueRange<float>(0.5f, 1.25f)));

            // --- 4. De-icing Fluid Supplier -----------------------------------
            // Numeric dot prefixes ensure BepInEx displays keys in logical order.
            EnableDeicing = Config.Bind(
                "4. De-icing Fluid Supplier", "1.EnableDeicing", true,
                "Master toggle. False restores vanilla de-icing purchase costs entirely.");

            DeicingFluidCost = Config.Bind(
                "4. De-icing Fluid Supplier", "2.DeicingFluidCost", 1.0f,
                new ConfigDescription(
                    "De-icing fluid purchase cost per litre multiplier. 1.0 = vanilla. " +
                    "Low: 0.5 (half cost). High: 1.5 (50% premium). Step: 0.05. " +
                    "De-icing is a pure expense — no income-side ceiling applies. " +
                    "Scales what the airport pays the supplier per litre on delivery.",
                    new AcceptableValueRange<float>(0.5f, 1.5f)));

            // --- 5. Airlines -------------------------------------------------
            EnableAirlineNegotiation = Config.Bind(
                "5. Airlines", "EnableAirlineNegotiation", true,
                "True (default) = vanilla behaviour. Airlines renegotiate each year — " +
                "the CFO sends the negotiation email and terms can change. " +
                "False = airline renegotiation suppressed. Terms roll over at current " +
                "values each year, no Negotiate button appears, no orange tile, and " +
                "airlines are omitted from the CFO negotiation email (email suppressed " +
                "entirely if no other contracts are due that year).");
        }

        // =====================================================================
        // Patch registration
        // =====================================================================

        private void RegisterPatches()
        {
            var harmony = new Harmony("com.ktl.aceo.tweaks.contracts");

            TryPatch(harmony, typeof(ContractorCostPatch), "ContractorCostPatch");
            TryPatch(harmony, typeof(ContractorCountPatch), "ContractorCountPatch");
            TryPatch(harmony, typeof(ConstructionPanelDisplayPatch), "ConstructionPanelDisplayPatch");
            TryPatch(harmony, typeof(ContractorDisplayPatch), "ContractorDisplayPatch");
            TryPatch(harmony, typeof(ContractorNegotiationPanelPatch), "ContractorNegotiationPanelPatch");
            TryPatch(harmony, typeof(FuelCostPatch_Avgas), "FuelCostPatch_Avgas");
            TryPatch(harmony, typeof(FuelCostPatch_JetA1), "FuelCostPatch_JetA1");
            TryPatch(harmony, typeof(FuelNegotiationPanelPatch), "FuelNegotiationPanelPatch");
            TryPatch(harmony, typeof(CateringCostPatch), "CateringCostPatch");
            TryPatch(harmony, typeof(CateringNegotiationPanelPatch), "CateringNegotiationPanelPatch");
            TryPatch(harmony, typeof(DeicingCostPatch), "DeicingCostPatch");
            TryPatch(harmony, typeof(DeicingNegotiationPanelPatch), "DeicingNegotiationPanelPatch");
            TryPatch(harmony, typeof(AirlineNegotiationEmailPatch), "AirlineNegotiationEmailPatch");
            TryPatch(harmony, typeof(AirlineContractTilesPatch), "AirlineContractTilesPatch");
            TryPatch(harmony, typeof(AirlineNegotiationPanelPatch), "AirlineNegotiationPanelPatch");

            Logger.LogInfo("KTL Contract Tweaks 1.0.0 loaded.");
        }

        private static void TryPatch(Harmony harmony, System.Type patchType, string patchName)
        {
            try
            {
                harmony.PatchAll(patchType);
                Logger.LogInfo($"Patch registered: {patchName}");
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning($"Patch failed to register: {patchName} — {ex.Message}");
            }
        }

        // =====================================================================
        // Step snapping
        // =====================================================================

        public static float SnapToStep(float value, float min, float max, float step)
        {
            float clamped = Mathf.Clamp(value, min, max);
            float snapped = Mathf.Round((clamped - min) / step) * step + min;
            return Mathf.Clamp((float)System.Math.Round(snapped, 3), min, max);
        }

        public static float SnapContractorCount(float value)
        {
            float clamped = Mathf.Clamp(value, 0.75f, 3f);
            if (clamped <= 1.5f)
                return Mathf.Clamp((float)System.Math.Round(Mathf.Round((clamped - 0.75f) / 0.05f) * 0.05f + 0.75f, 3), 0.75f, 1.5f);
            return Mathf.Clamp((float)System.Math.Round(Mathf.Round((clamped - 1.5f) / 0.1f) * 0.1f + 1.5f, 3), 1.5f, 3f);
        }

        public static float GetContractorCostValue() =>
            SnapToStep(ContractorHourlyCost.Value, 0.5f, 1.75f, 0.05f);

        public static int GetScaledMaxContractors(int vanillaMax)
        {
            float multiplier = SnapContractorCount(NumberOfContractors.Value);
            return Mathf.Clamp(Mathf.RoundToInt(vanillaMax * multiplier), 25, 750);
        }

        public static float GetFuelAvgasValue() =>
            SnapToStep(FuelAvgas100LLCost.Value, 0.5f, 1.25f, 0.05f);

        public static float GetFuelJetA1Value() =>
            SnapToStep(FuelJetA1Cost.Value, 0.5f, 1.25f, 0.05f);

        public static float GetCateringFoodValue() =>
            SnapToStep(CateringFoodCost.Value, 0.5f, 1.25f, 0.05f);

        public static float GetDeicingFluidValue() =>
            SnapToStep(DeicingFluidCost.Value, 0.5f, 1.5f, 0.05f);

        // =====================================================================
        // Config normalisation
        // =====================================================================

        private void NormalizeAndSaveConfigValues()
        {
            bool changed = false;
            changed |= SetIfDifferent(ContractorHourlyCost, GetContractorCostValue());
            changed |= SetIfDifferent(NumberOfContractors, SnapContractorCount(NumberOfContractors.Value));
            changed |= SetIfDifferent(FuelAvgas100LLCost, GetFuelAvgasValue());
            changed |= SetIfDifferent(FuelJetA1Cost, GetFuelJetA1Value());
            changed |= SetIfDifferent(CateringFoodCost, GetCateringFoodValue());
            changed |= SetIfDifferent(DeicingFluidCost, GetDeicingFluidValue());
            if (changed) { Config.Save(); Logger.LogInfo("Contract config values normalised and saved."); }
        }

        private void RegisterLiveConfigHandlers()
        {
            RegisterSnapHandler(ContractorHourlyCost, _ => GetContractorCostValue());
            RegisterSnapHandler(NumberOfContractors, e => SnapContractorCount(e.Value));
            RegisterSnapHandler(FuelAvgas100LLCost, _ => GetFuelAvgasValue());
            RegisterSnapHandler(FuelJetA1Cost, _ => GetFuelJetA1Value());
            RegisterSnapHandler(CateringFoodCost, _ => GetCateringFoodValue());
            RegisterSnapHandler(DeicingFluidCost, _ => GetDeicingFluidValue());
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
    // Harmony patches — 1. Contractors
    // =========================================================================

    [HarmonyPatch(typeof(ContractorModel), "get_HourlyCostPerContractor")]
    public static class ContractorCostPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref float __result)
        {
            if (!ContractorTweaksPlugin.EnableContractors.Value) return;
            __result = Mathf.Clamp(__result * ContractorTweaksPlugin.GetContractorCostValue(), 1.5f, 15f);
        }
    }

    [HarmonyPatch(typeof(ContractorModel), "get_NbrOfDeployableContractors")]
    public static class ContractorCountPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ContractorModel __instance, ref int __result)
        {
            if (!ContractorTweaksPlugin.EnableContractors.Value) return;
            int scaledMax = ContractorTweaksPlugin.GetScaledMaxContractors(__instance.maxContractors);
            int deployed = Singleton<ConstructionController>.Instance.constructionData.nbrOfDeployedContractors;
            __result = Mathf.Max(0, scaledMax - deployed);
        }
    }

    [HarmonyPatch(typeof(ConstructionPanelUI), "UpdatePanel")]
    public static class ConstructionPanelDisplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ConstructionPanelUI __instance)
        {
            if (!ContractorTweaksPlugin.EnableContractors.Value) return;
            ContractorModel contractor = SingletonNonDestroy<BusinessController>.Instance.CurrentContractor;
            if (contractor == null) return;

            int scaledMax = ContractorTweaksPlugin.GetScaledMaxContractors(contractor.maxContractors);
            int deployed = Singleton<ConstructionController>.Instance.constructionData.nbrOfDeployedContractors;
            int available = Mathf.Max(0, scaledMax - deployed);

            __instance.maxDeployableContractosText.text =
                LocalizationManager.GetLocalizedValue("ConstructionPanelUI.cs.key.11") + scaledMax.ToString().ToBold();
            __instance.deployableContractorsText.text = available.ToString();
        }
    }

    [HarmonyPatch(typeof(SelectedContractUI), "SetPanelValues")]
    public static class ContractorDisplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(SelectedContractUI __instance, BusinessModel business)
        {
            if (!ContractorTweaksPlugin.EnableContractors.Value) return;
            ContractorModel contractorModel = business as ContractorModel;
            if (contractorModel == null) return;

            int scaledMax = ContractorTweaksPlugin.GetScaledMaxContractors(contractorModel.maxContractors);
            int vanillaMax = contractorModel.maxContractors;
            if (scaledMax == vanillaMax) return;

            FieldInfo textField = AccessTools.Field(typeof(SelectedContractUI), "textAreaText");
            if (textField == null) return;
            TextMeshProUGUI textArea = textField.GetValue(__instance) as TextMeshProUGUI;
            if (textArea == null) return;

            textArea.text = textArea.text.Replace(vanillaMax.ToString().ToBold(), scaledMax.ToString().ToBold());
        }
    }

    [HarmonyPatch(typeof(ContractorModel), "GetNegotiationValues")]
    public static class ContractorNegotiationPanelPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ContractorModel __instance, ref NegotiationValue[] __result)
        {
            if (!ContractorTweaksPlugin.EnableContractors.Value) return;
            if (__result == null || __result.Length == 0) return;
            float costMultiplier = ContractorTweaksPlugin.GetContractorCostValue();
            for (int i = 0; i < __result.Length; i++)
            {
                float baseHourlyCost = __instance.hourlyCostPerContractor;
                float multiplier = costMultiplier;
                __result[i].GetValue = (modifierValue) => modifierValue * baseHourlyCost * multiplier;
            }
        }
    }

    // =========================================================================
    // Harmony patches — 2. Aviation Fuel Supplier
    // =========================================================================

    [HarmonyPatch(typeof(AviationFuelSupplierModel), "get_CostPerLiterAvgas100LL")]
    public static class FuelCostPatch_Avgas
    {
        [HarmonyPostfix]
        public static void Postfix(ref float __result)
        {
            if (!ContractorTweaksPlugin.EnableFuel.Value) return;
            __result = Mathf.Clamp(__result * ContractorTweaksPlugin.GetFuelAvgasValue(), 0.01f, 4.0f);
        }
    }

    [HarmonyPatch(typeof(AviationFuelSupplierModel), "get_CostPerLiterJetA1")]
    public static class FuelCostPatch_JetA1
    {
        [HarmonyPostfix]
        public static void Postfix(ref float __result)
        {
            if (!ContractorTweaksPlugin.EnableFuel.Value) return;
            __result = Mathf.Clamp(__result * ContractorTweaksPlugin.GetFuelJetA1Value(), 0.01f, 1.0f);
        }
    }

    [HarmonyPatch(typeof(AviationFuelSupplierModel), "GetNegotiationValues")]
    public static class FuelNegotiationPanelPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AviationFuelSupplierModel __instance, ref NegotiationValue[] __result)
        {
            if (!ContractorTweaksPlugin.EnableFuel.Value) return;
            if (__result == null || __result.Length == 0) return;
            float baseAvgas = __instance.costPerLiterAvgas100LL;
            float baseJetA1 = __instance.costPerLiterJetA1;
            float multAvgas = ContractorTweaksPlugin.GetFuelAvgasValue();
            float multJetA1 = ContractorTweaksPlugin.GetFuelJetA1Value();
            if (__result.Length > 0) __result[0].GetValue = (m) => m * baseAvgas * multAvgas;
            if (__result.Length > 1) __result[1].GetValue = (m) => m * baseJetA1 * multJetA1;
        }
    }

    // =========================================================================
    // Harmony patches — 3. Catering Food Supplier
    // =========================================================================

    [HarmonyPatch(typeof(CateringFoodSupplierModel), "get_CostPerFood")]
    public static class CateringCostPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref float __result)
        {
            if (!ContractorTweaksPlugin.EnableCatering.Value) return;
            __result = Mathf.Clamp(__result * ContractorTweaksPlugin.GetCateringFoodValue(), 0.01f, 2.0f);
        }
    }

    [HarmonyPatch(typeof(CateringFoodSupplierModel), "GetNegotiationValues")]
    public static class CateringNegotiationPanelPatch
    {
        [HarmonyPostfix]
        public static void Postfix(CateringFoodSupplierModel __instance, ref NegotiationValue[] __result)
        {
            if (!ContractorTweaksPlugin.EnableCatering.Value) return;
            if (__result == null || __result.Length == 0) return;
            float baseCostPerFood = __instance.averageCostPerFood;
            float multiplier = ContractorTweaksPlugin.GetCateringFoodValue();
            __result[0].GetValue = (m) => m * baseCostPerFood * multiplier;
        }
    }

    // =========================================================================
    // Harmony patches — 4. De-icing Fluid Supplier
    // =========================================================================

    [HarmonyPatch(typeof(DeicingFluidSupplierModel), "get_CostPerLiter")]
    public static class DeicingCostPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref float __result)
        {
            if (!ContractorTweaksPlugin.EnableDeicing.Value) return;
            __result = Mathf.Clamp(__result * ContractorTweaksPlugin.GetDeicingFluidValue(), 0.01f, 12.0f);
        }
    }

    [HarmonyPatch(typeof(DeicingFluidSupplierModel), "GetNegotiationValues")]
    public static class DeicingNegotiationPanelPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DeicingFluidSupplierModel __instance, ref NegotiationValue[] __result)
        {
            if (!ContractorTweaksPlugin.EnableDeicing.Value) return;
            if (__result == null || __result.Length == 0) return;
            float baseCostPerLiter = __instance.costPerLiterDeicingFluid;
            float multiplier = ContractorTweaksPlugin.GetDeicingFluidValue();
            __result[0].GetValue = (m) => m * baseCostPerLiter * multiplier;
        }
    }

    // =========================================================================
    // Harmony patches — 5. Airlines
    // =========================================================================

    /// <summary>
    /// Filters airlines from the CFO negotiation email.
    /// Removes AirlineModel entries from the shared list in-place — also
    /// prevents auto-negotiation acting on them in the same cycle.
    /// Suppresses the email entirely if no non-airline businesses remain.
    /// </summary>
    [HarmonyPatch(typeof(EmailController), "GenerateNegotiationEmail")]
    public static class AirlineNegotiationEmailPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(List<BusinessModel> negotiableBusinesses)
        {
            if (ContractorTweaksPlugin.EnableAirlineNegotiation.Value) return true;
            for (int i = negotiableBusinesses.Count - 1; i >= 0; i--)
            {
                if (negotiableBusinesses[i] is AirlineModel)
                    negotiableBusinesses.RemoveAt(i);
            }
            return negotiableBusinesses.Count > 0;
        }
    }

    /// <summary>
    /// Sweeps negotiationActive = false on all airline contracts before
    /// ContractPanelUI.UpdatePanel renders the tile list.
    ///
    /// Walks BusinessController.allBusinessArray directly via reflection
    /// rather than using GetListOfActiveBusinessesByType — the latter returns
    /// a filtered copy and any flag resets would not affect the live models.
    /// The raw array contains the live BusinessModel instances; resetting flags
    /// here persists to every subsequent read in the same frame.
    /// </summary>
    [HarmonyPatch(typeof(ContractPanelUI), "UpdatePanel")]
    public static class AirlineContractTilesPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            if (ContractorTweaksPlugin.EnableAirlineNegotiation.Value) return;
            ContractorTweaksPlugin.SweepAirlineNegotiationFlags();
        }
    }

    /// <summary>
    /// Belt-and-braces Prefix on SetContractPanelValues.
    /// Resets negotiationActive = false on the specific airline contract before
    /// vanilla renders the panel, ensuring correct state on first open even if
    /// the tile sweep hasn't run yet (e.g. panel opened before economy panel).
    /// </summary>
    [HarmonyPatch(typeof(SelectedContractUI), "SetContractPanelValues")]
    public static class AirlineNegotiationPanelPatch
    {
        [HarmonyPrefix]
        public static void Prefix(BusinessModel business)
        {
            if (ContractorTweaksPlugin.EnableAirlineNegotiation.Value) return;
            if (!(business is AirlineModel)) return;
            if (business.contract != null && business.contract.negotiationActive)
                business.contract.negotiationActive = false;
        }
    }
}
