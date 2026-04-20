using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using AirportCEOModLoader.WatermarkUtils;

// References required:
//   AirportCEOModLoader.dll (D:\SteamLibrary\steamapps\workshop\content\673610\3109136766\plugins\AirportCEOModLoader.dll)

namespace ACEO_KTL_Passenger_Activity_Tweaks
{
    // =========================================================================
    // ACEO_KTL_Passenger_Activity_Tweaks  v1.0.0
    //
    // Five configurable tweaks affecting passenger load, shopping behaviour,
    // purchase abandonment, marketing visitor counts, and venue opening hours:
    //
    //   1. Near-Maximum Passenger Load
    //      When enabled, raises the random lower bound for both
    //      totalNbrOfArrivingPassengers and totalNbrOfDepartingPassengers
    //      from 50% of MaxPax to 95% of MaxPax in the CommercialFlightModel
    //      constructor.  Upper bound remains MaxPax.
    //
    //   2. Increased Shopping Likelihood
    //      Applies a configurable multiplier to the hardcoded 0.25f chance
    //      used in PassengerController.WantsShop(bool allowChance).
    //      Result clamped to 0.50f.
    //
    //   3. Purchase Abandonment Reduction
    //      Scales the 0.2f bias term in the vanilla abandonment formula.
    //      At 1.0 vanilla behaviour is preserved exactly.
    //
    //   4. Increased Marketing Visitor Count
    //      Multiplies MaxVisitors cap and per-tick spawn batch by a
    //      configurable float.
    //
    //   5. Extended Night Opening Hours
    //      Shifts venue opening hours from 04:00–00:00 to 02:00–00:00
    //      for both shops and dining under Night Flights research.
    //
    //      Implementation: BusinessOpenHourNightFlight is the private constant
    //      (vanilla = 4) from which every relevant vanilla mechanism derives:
    //
    //        IsPreBusinessWorkingHours: hour > BusinessOpenHourNightFlight - 2
    //        IsBusinessWorkingHours:    hour > BusinessOpenHourNightFlight - 1
    //        IsBusinessOpenHours:       hour >= BusinessOpenHourNightFlight
    //        CurrentBusinessOpenHour:   returns BusinessOpenHourNightFlight
    //        Idle scheduler target:     (CurrentBusinessOpenHour - 1):30
    //
    //      Patching this single constant to return 2 shifts all mechanisms
    //      simultaneously.  No downstream patches are needed.
    //
    //      Passenger last-entry cutoff — 22:30:
    //        WantsShopPatch and WantsEatPatch block new entries after 22:30,
    //        providing a 90-minute drain window before midnight close.
    //
    //      Save reload note:
    //        On the first reload of a pre-mod save, TimeToRegenerateJobTasks
    //        may hold the old vanilla value of 03:30, causing staff to spawn
    //        ~2 hours late on that one cycle.  This resolves automatically
    //        after the first midnight cycle with the mod active.
    //
    // =========================================================================
    // PATCH HISTORY
    // =========================================================================
    //
    // v0.0.1–v0.3.1  Alpha/Beta iterations. See earlier release notes.
    //
    // v1.0.0  Initial Steam release.
    //
    // v0.9.0–v0.9.3  Bug fix series — staff scheduling issues from patching
    //   individual downstream consumers of BusinessOpenHourNightFlight rather
    //   than the constant itself.  Resulted in over-spawning, 1-hour shift
    //   loops, and stale scheduler corrections that re-introduced bugs.
    //
    // v1.0.0  Section 5 rewrite — single-patch approach.
    //   All previous Section 5 staff scheduling patches removed:
    //     CurrentBusinessOpenHourPatch, IsBusinessOpenHoursPatch,
    //     UpdateAirportStatusPatch, IsPreBusinessWorkingHoursPatch,
    //     StaleSchedulerCorrectionPatch.
    //   Replaced with BusinessOpenHourNightFlightPatch — Prefix on
    //     BusinessController.get_BusinessOpenHourNightFlight, returns 2.
    //   BusinessRoomUIPatch and passenger entry window patches retained.
    //
    // =========================================================================

    // =========================================================================
    // GrandMasterPlugin
    // =========================================================================
    [BepInPlugin("com.ktl.aceo.tweaks.passengeractivitytweaks", "KTL Passenger Activity Tweaks", "1.0.0")]
    public class GrandMasterPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> EnableMaxLoad;
        public static ConfigEntry<bool> EnableShoppingMultiplier;
        public static ConfigEntry<float> ShoppingChanceMultiplier;
        public static ConfigEntry<bool> EnableAbandonmentReduction;
        public static ConfigEntry<float> AbandonmentMultiplier;
        public static ConfigEntry<bool> EnableVisitorMultiplier;
        public static ConfigEntry<float> VisitorCountMultiplier;
        public static ConfigEntry<bool> EnableExtendedNightHours;

        internal static new ManualLogSource Logger;
        private bool _isNormalizingConfig;

        void Awake()
        {
            Logger = base.Logger;

            BindConfig();
            NormalizeAndSaveConfigValues();
            RegisterLiveConfigHandlers();

            var harmony = new Harmony("com.ktl.aceo.tweaks.passengeractivitytweaks");
            harmony.PatchAll(typeof(CommercialFlightConstructorPatch));
            harmony.PatchAll(typeof(WantsShopPatch));
            harmony.PatchAll(typeof(WantsEatPatch));
            harmony.PatchAll(typeof(SpawnVisitorsPatch));
            harmony.PatchAll(typeof(ShopAbandonmentPatch));
            harmony.PatchAll(typeof(BusinessOpenHourNightFlightPatch));
            harmony.PatchAll(typeof(BusinessRoomUIPatch));

            Logger.LogInfo("KTL Passenger Activity Tweaks 1.0.0 loaded.");
        }

        void Start()
        {
            WatermarkUtils.Register(new WatermarkInfo("KTL-PST", "1.0.0", true));
        }

        // =========================================================================
        // Config binding
        // =========================================================================
        private void BindConfig()
        {
            EnableMaxLoad = BindBool(
                "1. Passenger Load", "1.1 EnableMaxLoad", false,
                "When enabled, passenger counts per flight are set to 95%–100% of aircraft MaxPax instead of vanilla 50%–100%. Toggle only.");

            EnableShoppingMultiplier = BindBool(
                "2. Shopping", "2.1 EnableShoppingMultiplier", false,
                "Master toggle for the shopping chance multiplier. When off, vanilla 0.25 chance is used.");

            ShoppingChanceMultiplier = BindFloat(
                "2. Shopping", "2.2 ShoppingChanceMultiplier",
                1.0f, 1.0f, 1.33f,
                "Multiplier applied to the vanilla 0.25 random shop chance. 1.0 = vanilla (0.25). 1.33 = ~33% increase (0.33). Result clamped to 0.50. Step: ~5%. Requires 2.1 EnableShoppingMultiplier.");

            EnableAbandonmentReduction = BindBool(
                "3. Purchase Abandonment", "3.1 EnableAbandonmentReduction", false,
                "Master toggle for purchase abandonment reduction. When off, vanilla abandonment behaviour applies.");

            AbandonmentMultiplier = BindFloat(
                "3. Purchase Abandonment", "3.2 AbandonmentMultiplier",
                1.0f, 0.25f, 1.0f,
                "Scales the purchase-abandonment bias at high star ratings. 1.0 = vanilla. 0.25 = 75% less likely to abandon. Step: 0.05. Requires 3.1 EnableAbandonmentReduction.");

            EnableVisitorMultiplier = BindBool(
                "4. Marketing Visitors", "4.1 EnableVisitorMultiplier", false,
                "Master toggle for the marketing visitor count multiplier. When off, vanilla visitor counts apply.");

            VisitorCountMultiplier = BindFloat(
                "4. Marketing Visitors", "4.2 VisitorCountMultiplier",
                1.0f, 1.0f, 3.0f,
                "Multiplier applied to both MaxVisitors cap and per-tick spawn batch. 1.0 = vanilla. 3.0 = triple visitors. Only active when marketing modifier > 1. Step: 10%.");

            EnableExtendedNightHours = BindBool(
                "5. Opening Hours", "5.1 EnableExtendedNightHours", false,
                "Shifts Night Flights venue hours to 02:00–00:00 for both shops and dining. Requires Night Flights research. Without it, vanilla hours apply. Passenger last entry is 22:30 to allow a 90-minute drain before midnight close. Toggle only.");
        }

        private ConfigEntry<bool> BindBool(string section, string key, bool def, string desc) =>
            Config.Bind(section, key, def, new ConfigDescription(desc));

        private ConfigEntry<float> BindFloat(string section, string key, float def, float min, float max, string desc) =>
            Config.Bind(section, key, def, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));

        // =========================================================================
        // Step-snapping helpers
        // =========================================================================
        public static float SnapToStep(float value, float min, float max, float step)
        {
            float clamped = Mathf.Clamp(value, min, max);
            float snapped = Mathf.Round((clamped - min) / step) * step + min;
            return Mathf.Clamp((float)System.Math.Round(snapped, 3), min, max);
        }

        public static float GetShoppingMultiplierValue(ConfigEntry<float> e) => SnapToStep(e.Value, 1.0f, 1.33f, 0.05f);
        public static float GetVisitorMultiplierValue(ConfigEntry<float> e) => SnapToStep(e.Value, 1.0f, 3.0f, 0.10f);
        public static float GetAbandonmentMultiplierValue(ConfigEntry<float> e) => SnapToStep(e.Value, 0.25f, 1.0f, 0.05f);

        // =========================================================================
        // Time / research helpers
        // =========================================================================
        public static int GetCurrentGameHour() => Singleton<TimeController>.Instance.GetCurrentContinuousTime().Hour;
        public static int GetCurrentGameMinute() => Singleton<TimeController>.Instance.GetCurrentContinuousTime().Minute;

        public static bool GetHasResearchedNightFlights() =>
            Singleton<ProgressionManager>.Instance.GetProjectCompletionStatus(Enums.SpecificProjectType.NightFlights);

        // Returns true during 02:00–22:29.
        public static bool IsWithinPassengerEntryWindow()
        {
            int h = GetCurrentGameHour();
            int m = GetCurrentGameMinute();
            if (h < 2) return false;
            if (h < 22) return true;
            if (h == 22 && m < 30) return true;
            return false;
        }

        // =========================================================================
        // Config normalisation
        // =========================================================================
        public void NormalizeAndSaveConfigValues()
        {
            bool changed = false;
            changed |= SetIfDifferent(ShoppingChanceMultiplier, GetShoppingMultiplierValue(ShoppingChanceMultiplier));
            changed |= SetIfDifferent(VisitorCountMultiplier, GetVisitorMultiplierValue(VisitorCountMultiplier));
            changed |= SetIfDifferent(AbandonmentMultiplier, GetAbandonmentMultiplierValue(AbandonmentMultiplier));
            if (changed) { Config.Save(); Logger.LogInfo("Config values normalised to valid steps and saved."); }
        }

        public void RegisterLiveConfigHandlers()
        {
            RegisterSnapHandler(ShoppingChanceMultiplier, e => GetShoppingMultiplierValue(e));
            RegisterSnapHandler(VisitorCountMultiplier, e => GetVisitorMultiplierValue(e));
            RegisterSnapHandler(AbandonmentMultiplier, e => GetAbandonmentMultiplierValue(e));
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
    // Harmony patches
    // =========================================================================

    // -------------------------------------------------------------------------
    // CommercialFlightConstructorPatch
    // Target: CommercialFlightModel..ctor(string, bool, string, Route, Route)
    // Type:   Transpiler
    // Replaces the two ldc.r4 0.5f lower-bound operands passed to RandomRangeI
    // with GetMaxLoadLowerBound(), returning 0.95f when EnableMaxLoad is true.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(CommercialFlightModel), MethodType.Constructor,
        new System.Type[] { typeof(string), typeof(bool), typeof(string), typeof(Route), typeof(Route) })]
    public static class CommercialFlightConstructorPatch
    {
        public static float GetMaxLoadLowerBound() =>
            GrandMasterPlugin.EnableMaxLoad.Value ? 0.95f : 0.5f;

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getLowerBound = AccessTools.Method(typeof(CommercialFlightConstructorPatch), "GetMaxLoadLowerBound");
            int replacements = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Ldc_R4 &&
                    codes[i].operand is float f && Mathf.Approximately(f, 0.5f) &&
                    replacements < 2)
                {
                    bool confirmed = false;
                    for (int j = i + 1; j < System.Math.Min(i + 7, codes.Count); j++)
                    {
                        if (codes[j].opcode == OpCodes.Call &&
                            codes[j].operand is MethodInfo mi &&
                            mi.Name == "RandomRangeI" &&
                            mi.DeclaringType?.Name == "Utils")
                        { confirmed = true; break; }
                    }
                    if (confirmed) { codes[i] = new CodeInstruction(OpCodes.Call, getLowerBound); replacements++; }
                }
            }

            if (replacements < 2)
                GrandMasterPlugin.Logger.LogWarning(
                    $"CommercialFlightConstructorPatch: expected 2 replacements, made {replacements}. " +
                    "Game IL may have changed — EnableMaxLoad will have no effect.");

            return codes;
        }
    }

    // -------------------------------------------------------------------------
    // WantsShopPatch
    // Target: PassengerController.WantsShop(bool allowChance)
    // Type:   Prefix
    // When EnableExtendedNightHours + Night Flights: gates on
    // IsWithinPassengerEntryWindow() (02:00–22:29) instead of isBusinessOpenHours.
    // Also applies shopping chance multiplier when enabled.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(PassengerController), "WantsShop")]
    public static class WantsShopPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(PassengerController __instance, bool allowChance, ref bool __result)
        {
            bool multiplierActive = GrandMasterPlugin.EnableShoppingMultiplier.Value && allowChance;
            bool hoursActive = GrandMasterPlugin.EnableExtendedNightHours.Value;

            if (!multiplierActive && !hoursActive) return true;

            float chance = multiplierActive
                ? Mathf.Clamp(0.25f * GrandMasterPlugin.GetShoppingMultiplierValue(GrandMasterPlugin.ShoppingChanceMultiplier), 0f, 0.50f)
                : 0.25f;

            bool hoursOpen = (hoursActive && GrandMasterPlugin.GetHasResearchedNightFlights())
                ? GrandMasterPlugin.IsWithinPassengerEntryWindow()
                : AirportController.isBusinessOpenHours;

            __result =
                !__instance.PassengerModel.shouldLeaveAirport &&
                ((__instance.PassengerModel.needs.IsRested && __instance.PassengerModel.needs.IsBored) ||
                 (allowChance && Utils.ChanceOccured(chance))) &&
                __instance.ActivityNotRecentlyFailed(Enums.PassengerActivity.Shopping) &&
                AirportController.hasShopUpgrade &&
                hoursOpen;

            return false;
        }
    }

    // -------------------------------------------------------------------------
    // WantsEatPatch
    // Target: PassengerController.get_WantsEat
    // Type:   Prefix
    // When EnableExtendedNightHours + Night Flights: gates on
    // IsWithinPassengerEntryWindow() instead of isBusinessOpenHours.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(PassengerController), "get_WantsEat")]
    public static class WantsEatPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(PassengerController __instance, ref bool __result)
        {
            if (!GrandMasterPlugin.EnableExtendedNightHours.Value) return true;

            bool diningOpen = GrandMasterPlugin.GetHasResearchedNightFlights()
                ? GrandMasterPlugin.IsWithinPassengerEntryWindow()
                : AirportController.isBusinessOpenHours;

            __result =
                !__instance.PassengerModel.shouldLeaveAirport &&
                __instance.PassengerModel.needs.IsHungry &&
                __instance.ActivityNotRecentlyFailed(Enums.PassengerActivity.Eating) &&
                (AirportController.hasFoodUpgrade ||
                 (__instance.PassengerModel.isInAirlineLounge &&
                  Singleton<ProgressionManager>.Instance.GetProjectCompletionStatus(Enums.SpecificProjectType.UltimateCommercialLicense))) &&
                diningOpen;

            return false;
        }
    }

    // -------------------------------------------------------------------------
    // SpawnVisitorsPatch
    // Target: AirportController.SpawnVisitors()
    // Type:   Prefix
    // Scales MaxVisitors cap and per-tick batch by VisitorCountMultiplier.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(AirportController), "SpawnVisitors")]
    public static class SpawnVisitorsPatch
    {
        private static FieldInfo _currentVisitorsField;

        [HarmonyPrefix]
        public static bool Prefix(AirportController __instance)
        {
            if (!GrandMasterPlugin.EnableVisitorMultiplier.Value) return true;

            if (_currentVisitorsField == null)
                _currentVisitorsField = AccessTools.Field(typeof(AirportController), "currentVisitors");

            float multiplier = GrandMasterPlugin.GetVisitorMultiplierValue(GrandMasterPlugin.VisitorCountMultiplier);

            if (Singleton<TimeController>.Instance.GetCurrentTimeState() == Enums.TimeState.Pause) return false;

            int pax = __instance.GetNbrOfPassengersAtAirport(Enums.TravelDirection.Unspecified, 0);
            if (pax >= 1000) { AirportController.visitorsShouldLeave = true; return false; }
            if (pax >= 750) { AirportController.visitorsShouldLeave = false; return false; }
            AirportController.visitorsShouldLeave = false;

            float visitorEfficiency = ((Singleton<ProgressionManager>.Instance.GetStaticModifier(Enums.SpecificProjectType.Marketing) - 1f) * 10f).ClampMax(1f);
            int scaledMaxVisitors = (int)(25f * visitorEfficiency * multiplier);
            int currentVisitors = (int)_currentVisitorsField.GetValue(__instance);

            if (!__instance.HasAvailableTransitStructure || currentVisitors >= scaledMaxVisitors) return false;

            if (Utils.ChanceOccured(visitorEfficiency))
            {
                int num = Utils.RandomRangeI(1f, 5f * multiplier);
                for (int i = 0; i < num; i++)
                {
                    PassengerController passenger = Singleton<ObjectPoolController>.Instance.GetPassenger();
                    passenger.InitializePerson();
                    PassengerModel pm = passenger.PassengerModel;
                    pm.travelDirection = Enums.TravelDirection.Unspecified;
                    pm.wealthClass = Utils.ChanceOccured(0.2f) ? Enums.PersonWealthClass.High : Enums.PersonWealthClass.Low;
                    if (pm.wealthClass == Enums.PersonWealthClass.High && Utils.ChanceOccured(0.9f))
                        pm.personApperance.personClothingColorVector = Color.white;
                    pm.currentGenericZoneType = Enums.GenericZoneType.OpenZone;
                    pm.currentSpecificZoneType = Enums.SpecificZoneType.None;
                    pm.permissions.AddToAllowedGenericZoneType(Enums.GenericZoneType.OpenZone);
                    pm.isVisitor = true;
                    pm.isAtAirport = false;
                    pm.needs.ResetVisitor();
                    __instance.AddPersonToList(passenger);
                    __instance.IncreaseVisitorCount();
                }
            }
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // ShopAbandonmentPatch
    // Target: PassengerController.<Shop>d__72::MoveNext()
    // Type:   Transpiler
    // Replaces ldc.r4 0.2f bias term with GetAbandonmentBias().
    // -------------------------------------------------------------------------
    [HarmonyPatch]
    public static class ShopAbandonmentPatch
    {
        public static float GetAbandonmentBias() =>
            GrandMasterPlugin.EnableAbandonmentReduction.Value
                ? 0.2f * GrandMasterPlugin.GetAbandonmentMultiplierValue(GrandMasterPlugin.AbandonmentMultiplier)
                : 0.2f;

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var sm = typeof(PassengerController).GetNestedType("<Shop>d__72", BindingFlags.NonPublic | BindingFlags.Public);
            if (sm == null)
            {
                GrandMasterPlugin.Logger.LogWarning("ShopAbandonmentPatch: <Shop>d__72 not found — patch not applied.");
                return null;
            }
            var mn = sm.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (mn == null)
                GrandMasterPlugin.Logger.LogWarning("ShopAbandonmentPatch: MoveNext not found — patch not applied.");
            return mn;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getBias = AccessTools.Method(typeof(ShopAbandonmentPatch), "GetAbandonmentBias");
            int replacements = 0;

            for (int i = 1; i < codes.Count - 1; i++)
            {
                if (codes[i].opcode == OpCodes.Ldc_R4 &&
                    codes[i].operand is float f && Mathf.Approximately(f, 0.2f) &&
                    codes[i - 1].opcode == OpCodes.Sub &&
                    codes[i + 1].opcode == OpCodes.Add)
                {
                    bool confirmed = false;
                    for (int j = i + 2; j < System.Math.Min(i + 4, codes.Count); j++)
                    {
                        if (codes[j].opcode == OpCodes.Call &&
                            codes[j].operand is MethodInfo mi &&
                            mi.Name == "ChanceOccured" &&
                            mi.DeclaringType?.Name == "Utils")
                        { confirmed = true; break; }
                    }
                    if (confirmed) { codes[i] = new CodeInstruction(OpCodes.Call, getBias); replacements++; }
                }
            }

            if (replacements == 0)
                GrandMasterPlugin.Logger.LogWarning(
                    "ShopAbandonmentPatch: expected 1 bias replacement, made 0. " +
                    "Game IL may have changed — AbandonmentMultiplier will have no effect.");

            return codes;
        }
    }

    // -------------------------------------------------------------------------
    // BusinessOpenHourNightFlightPatch
    //
    // Target: BusinessController.get_BusinessOpenHourNightFlight (private)
    // Type:   Prefix
    //
    // Vanilla: returns 4.  Every mechanism controlling staff scheduling, room
    //   open/closed status, and passenger leisure eligibility reads from this
    //   constant directly or through CurrentBusinessOpenHour:
    //
    //     IsPreBusinessWorkingHours  →  hour > BusinessOpenHourNightFlight - 2
    //     IsBusinessWorkingHours     →  hour > BusinessOpenHourNightFlight - 1
    //     IsBusinessOpenHours        →  hour >= BusinessOpenHourNightFlight
    //     CurrentBusinessOpenHour    →  returns BusinessOpenHourNightFlight
    //     Idle scheduler target      →  (CurrentBusinessOpenHour - 1):30
    //
    // Mod: returns 2.  All downstream mechanisms shift simultaneously:
    //
    //     IsPreBusinessWorkingHours  →  hour > 0  (true from 01:00)
    //     IsBusinessOpenHours        →  hour >= 2 (true from 02:00)
    //     Idle scheduler target      →  01:30     (staff arrive by 02:00)
    //
    //   No additional patches are needed.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(BusinessController), "get_BusinessOpenHourNightFlight")]
    public static class BusinessOpenHourNightFlightPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref int __result)
        {
            if (!GrandMasterPlugin.EnableExtendedNightHours.Value ||
                !GrandMasterPlugin.GetHasResearchedNightFlights())
                return true;

            __result = 2;
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // BusinessRoomUIPatch
    //
    // Target: BusinessRoomUI.UpdatePanel()
    // Type:   Postfix
    //
    // Overwrites openHoursText for ShopRoom and FoodRoom to display 02:00–00:00
    // when EnableExtendedNightHours is active.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(BusinessRoomUI), "UpdatePanel")]
    public static class BusinessRoomUIPatch
    {
        private static FieldInfo _openHoursTextField;

        [HarmonyPostfix]
        public static void Postfix(BusinessRoomUI __instance)
        {
            if (!GrandMasterPlugin.EnableExtendedNightHours.Value) return;

            if (_openHoursTextField == null)
                _openHoursTextField = AccessTools.Field(typeof(BusinessRoomUI), "openHoursText");

            var room = __instance.getRoom();
            if (room == null || room.CurrentBusiness == null) return;
            if (room.roomType != Enums.RoomType.ShopRoom && room.roomType != Enums.RoomType.FoodRoom) return;

            System.DateTime open = new System.DateTime(1, 1, 1, 2, 0, 0);
            System.DateTime close = new System.DateTime(1, 1, 1, 0, 0, 0);

            string text =
                Utils.GetTimeBasedOnUnitSystem(open, GameSettingManager.AmPM, false) + "-" +
                Utils.GetTimeBasedOnUnitSystem(close, GameSettingManager.AmPM, false);

            var field = (TMPro.TextMeshProUGUI)_openHoursTextField.GetValue(__instance);
            field.text = text;
        }
    }
}