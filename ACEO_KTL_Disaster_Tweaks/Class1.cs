using System.Collections.Generic;
using AirportCEOModLoader.WatermarkUtils;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// References required:
//   AirportCEOModLoader.dll (D:\SteamLibrary\steamapps\workshop\content\673610\3109136766\plugins\AirportCEOModLoader.dll)

namespace ACEO_KTL_Disaster_Tweaks
{
    // =========================================================================
    // ACEO_KTL_Disaster_Tweaks  v2.0.0
    //
    // Provides a three-tier control hierarchy over airport disasters and
    // emergencies:
    //
    //   Tier 1 — Master toggle (section 1, 0.EnableDisasters)
    //     When false, all mod behaviour is disabled and vanilla is fully
    //     restored.  Has no effect if Sandbox mode has emergencies disabled.
    //
    //   Tier 2 — Category toggles (section 1)
    //     1.EmergencyFrequency   — global hourly chance of any emergency
    //     2.EnableAircraftEmergencies — suppresses the entire aircraft
    //                                   emergency category
    //     3.EnableWorldEvents         — suppresses the entire world event
    //                                   category
    //     4.EnableStaffStrikes        — suppresses staff strikes
    //
    //   Tier 3 — Individual incident toggles (sections 2 and 3)
    //     Per-type bool toggles within each category.  Only active when the
    //     corresponding category toggle (Tier 2) is also enabled.
    //
    //     Section 2 — Aircraft Emergencies (sourced from
    //       IncidentController.aircraftEmergencyTypes):
    //       EngineFailure, EquipmentFailure, WeatherWarning, FuelWarning,
    //       InflightMedical, InflightSecurity, AirAmbulanceFlight
    //
    //     Section 3 — World Events (sourced from
    //       IncidentController.emergencyTypes):
    //       PowerSurge, RailwaySignalFailure, PlumbingFailure,
    //       VolcanoEruption, GlobalEconomyCrash, Pandemic, OilCrisis
    //
    // Distribution behaviour:
    //   Type selection uses Utils.RandomItemInCollection, which applies a
    //   uniform distribution over the filtered array of enabled types.
    //   Disabling an individual type does not reduce overall emergency
    //   frequency — it redistributes its probability equally across the
    //   remaining enabled types in the same category.  To reduce frequency,
    //   use EmergencyFrequency (Tier 2).
    //
    // Patch strategy:
    //   AircraftEmergencyPatch and WorldEventPatch are Harmony Prefixes that
    //   return false, replacing the original method entirely.  This is
    //   necessary because the original methods contain the type-selection
    //   logic we must intercept; a Postfix cannot undo a selection already
    //   made.  Both patches replicate the vanilla ChanceOccured gate
    //   (via GameDataController.GetEmergencyChance(), which is already
    //   intercepted by EmergencyFrequencyPatch) before selecting from the
    //   filtered type array.
    //
    //   EmergencyFrequencyPatch is a Postfix on GameDataController.
    //   GetEmergencyChance() — safe and non-destructive; it only overrides
    //   the return value.
    //
    //   StaffStrikePatch is a Prefix on TrySpawnCausalityIncident that
    //   returns the category toggle value directly.
    //
    // =========================================================================
    // PATCH HISTORY
    // =========================================================================
    //
    // v1.0.0 — Initial internal release
    //   Master toggle (EnableDisasters) and emergency frequency override
    //   (EmergencyFrequency) implemented.
    //
    // v1.0.1 — Category toggles added
    //   EnableAircraftEmergencies, EnableWorldEvents, and EnableStaffStrikes
    //   added as Prefix patches on TryPromptAircraftIncident,
    //   TrySpawnIncident, and TrySpawnCausalityIncident respectively.
    //
    // v1.0.2 — Config normalisation and live snap handler
    //   EmergencyFrequency now snaps to 0.01 steps on load and on live
    //   config change.  NormalizeAndSaveConfigValues() and
    //   RegisterLiveConfigHandlers() introduced.
    //
    // v2.0.0 — Per-incident type toggles (sections 2 and 3)
    //   Seven individual bool toggles added for each of the two emergency
    //   categories (aircraft emergencies and world events).  AircraftEmergency
    //   Patch and WorldEventPatch rewritten as full-replacement Prefixes that
    //   build a filtered type array from enabled toggles, replicate the vanilla
    //   ChanceOccured gate, and call PromptIncident / SpawnIncident directly.
    //   Disabling a type redistributes its probability across remaining enabled
    //   types; overall frequency is unchanged.  All config keys migrated from
    //   '_' to '.' separator for uniformity.
    //
    // =========================================================================

    [BepInPlugin("com.ktl.aceo.tweaks.disasters", "KTL Disaster Tweaks", "2.0.0")]
    public class DisasterTweaksPlugin : BaseUnityPlugin
    {
        // ---------------------------------------------------------------------
        // Section 1 — Disasters (master + category toggles)
        // ---------------------------------------------------------------------
        public static ConfigEntry<bool> EnableDisasters;
        public static ConfigEntry<float> EmergencyFrequency;
        public static ConfigEntry<bool> EnableAircraftEmergencies;
        public static ConfigEntry<bool> EnableWorldEvents;
        public static ConfigEntry<bool> EnableStaffStrikes;

        // ---------------------------------------------------------------------
        // Section 2 — Aircraft Emergencies (individual type toggles)
        // ---------------------------------------------------------------------
        public static ConfigEntry<bool> EnableEngineFailure;
        public static ConfigEntry<bool> EnableEquipmentFailure;
        public static ConfigEntry<bool> EnableWeatherWarning;
        public static ConfigEntry<bool> EnableFuelWarning;
        public static ConfigEntry<bool> EnableInflightMedical;
        public static ConfigEntry<bool> EnableInflightSecurity;
        public static ConfigEntry<bool> EnableAirAmbulanceFlight;

        // ---------------------------------------------------------------------
        // Section 3 — World Events (individual type toggles)
        // ---------------------------------------------------------------------
        public static ConfigEntry<bool> EnablePowerSurge;
        public static ConfigEntry<bool> EnableRailwaySignalFailure;
        public static ConfigEntry<bool> EnablePlumbingFailure;
        public static ConfigEntry<bool> EnableVolcanoEruption;
        public static ConfigEntry<bool> EnableGlobalEconomyCrash;
        public static ConfigEntry<bool> EnablePandemic;
        public static ConfigEntry<bool> EnableOilCrisis;

        internal static new ManualLogSource Logger;

        private bool _isNormalizingConfig;

        void Awake()
        {
            Logger = base.Logger;

            // -----------------------------------------------------------------
            // Section 1 — Disasters
            // -----------------------------------------------------------------
            EnableDisasters = Config.Bind(
                "1. Disasters",
                "0.EnableDisasters",
                true,
                new ConfigDescription(
                    "Master toggle. When false all disaster and emergency changes are disabled and vanilla behaviour is restored. " +
                    "Note: has no effect if Sandbox mode has emergencies disabled."
                )
            );

            EmergencyFrequency = Config.Bind(
                "1. Disasters",
                "1.EmergencyFrequency",
                0.0f,
                new ConfigDescription(
                    "Controls the global chance of an emergency occurring each in-game hour. 0.0 disables random emergencies entirely. " +
                    "For reference, vanilla values are: Easy = 0.001, Moderate = 0.05, Difficult = 0.1, Extreme = 0.2. " +
                    "Range: 0.0 to 0.3 in 1% steps.",
                    new AcceptableValueRange<float>(0.0f, 0.3f)
                )
            );

            EnableAircraftEmergencies = Config.Bind(
                "1. Disasters",
                "2.EnableAircraftEmergencies",
                true,
                new ConfigDescription(
                    "When false, suppresses all aircraft emergency landings — engine failures, equipment failures, fuel warnings, " +
                    "inflight medical, inflight security, bird strikes, wheel punctures, and air ambulance flights. " +
                    "EmergencyFrequency must be above 0.0 for these to occur."
                )
            );

            EnableWorldEvents = Config.Bind(
                "1. Disasters",
                "3.EnableWorldEvents",
                true,
                new ConfigDescription(
                    "When false, suppresses all world event disasters — power surges, railway signal failures, plumbing failures, " +
                    "volcano eruptions, global economy crashes, pandemics, and oil crises. " +
                    "EmergencyFrequency must be above 0.0 for these to occur."
                )
            );

            EnableStaffStrikes = Config.Bind(
                "1. Disasters",
                "4.EnableStaffStrikes",
                true,
                new ConfigDescription(
                    "When false, suppresses staff strikes. Note: staff strikes are triggered by low employee happiness and bypass " +
                    "EmergencyFrequency entirely — they will occur regardless of that setting unless toggled off here."
                )
            );

            // -----------------------------------------------------------------
            // Section 2 — Aircraft Emergencies
            // -----------------------------------------------------------------
            const string aircraftNote =
                " Disabling this type redistributes its probability equally across remaining enabled types in this category. " +
                "2.EnableAircraftEmergencies (section 1) must also be enabled for individual toggles to have any effect.";

            EnableEngineFailure = Config.Bind(
                "2. Aircraft Emergencies",
                "1.EngineFailure",
                true,
                new ConfigDescription("When false, engine failure emergencies will not occur." + aircraftNote)
            );

            EnableEquipmentFailure = Config.Bind(
                "2. Aircraft Emergencies",
                "2.EquipmentFailure",
                true,
                new ConfigDescription("When false, equipment failure emergencies will not occur." + aircraftNote)
            );

            EnableWeatherWarning = Config.Bind(
                "2. Aircraft Emergencies",
                "3.WeatherWarning",
                true,
                new ConfigDescription("When false, weather warning emergencies will not occur." + aircraftNote)
            );

            EnableFuelWarning = Config.Bind(
                "2. Aircraft Emergencies",
                "4.FuelWarning",
                true,
                new ConfigDescription("When false, fuel warning emergencies will not occur." + aircraftNote)
            );

            EnableInflightMedical = Config.Bind(
                "2. Aircraft Emergencies",
                "5.InflightMedical",
                true,
                new ConfigDescription("When false, inflight medical emergencies will not occur." + aircraftNote)
            );

            EnableInflightSecurity = Config.Bind(
                "2. Aircraft Emergencies",
                "6.InflightSecurity",
                true,
                new ConfigDescription("When false, inflight security emergencies will not occur." + aircraftNote)
            );

            EnableAirAmbulanceFlight = Config.Bind(
                "2. Aircraft Emergencies",
                "7.AirAmbulanceFlight",
                true,
                new ConfigDescription("When false, air ambulance flight emergencies will not occur." + aircraftNote)
            );

            // -----------------------------------------------------------------
            // Section 3 — World Events
            // -----------------------------------------------------------------
            const string worldNote =
                " Disabling this type redistributes its probability equally across remaining enabled types in this category. " +
                "3.EnableWorldEvents (section 1) must also be enabled for individual toggles to have any effect.";

            EnablePowerSurge = Config.Bind(
                "3. World Events",
                "1.PowerSurge",
                true,
                new ConfigDescription("When false, power surge events will not occur." + worldNote)
            );

            EnableRailwaySignalFailure = Config.Bind(
                "3. World Events",
                "2.RailwaySignalFailure",
                true,
                new ConfigDescription("When false, railway signal failure events will not occur." + worldNote)
            );

            EnablePlumbingFailure = Config.Bind(
                "3. World Events",
                "3.PlumbingFailure",
                true,
                new ConfigDescription("When false, plumbing failure events will not occur." + worldNote)
            );

            EnableVolcanoEruption = Config.Bind(
                "3. World Events",
                "4.VolcanoEruption",
                true,
                new ConfigDescription("When false, volcano eruption events will not occur." + worldNote)
            );

            EnableGlobalEconomyCrash = Config.Bind(
                "3. World Events",
                "5.GlobalEconomyCrash",
                true,
                new ConfigDescription("When false, global economy crash events will not occur." + worldNote)
            );

            EnablePandemic = Config.Bind(
                "3. World Events",
                "6.Pandemic",
                true,
                new ConfigDescription("When false, pandemic events will not occur." + worldNote)
            );

            EnableOilCrisis = Config.Bind(
                "3. World Events",
                "7.OilCrisis",
                true,
                new ConfigDescription("When false, oil crisis events will not occur." + worldNote)
            );

            NormalizeAndSaveConfigValues();
            RegisterLiveConfigHandlers();

            var harmony = new Harmony("com.ktl.aceo.tweaks.disasters");
            harmony.PatchAll(typeof(EmergencyFrequencyPatch));
            harmony.PatchAll(typeof(AircraftEmergencyPatch));
            harmony.PatchAll(typeof(WorldEventPatch));
            harmony.PatchAll(typeof(StaffStrikePatch));

            Logger.LogInfo("KTL Disaster Tweaks 2.0.0 loaded.");
        }

        void Start()
        {
            WatermarkUtils.Register(new WatermarkInfo("KTL-DT", "2.0.0", true));
            Logger.LogInfo("KTL Disaster Tweaks watermark registered.");
        }

        // =========================================================================
        // Step snapping
        // =========================================================================
        public static float SnapToStep(float value, float min, float max, float step)
        {
            float clamped = Mathf.Clamp(value, min, max);
            float snapped = Mathf.Round((clamped - min) / step) * step + min;
            return Mathf.Clamp((float)System.Math.Round(snapped, 3), min, max);
        }

        public static float GetEmergencyFrequency() =>
            SnapToStep(EmergencyFrequency.Value, 0f, 0.3f, 0.01f);

        // =========================================================================
        // Config normalisation
        // =========================================================================
        private void NormalizeAndSaveConfigValues()
        {
            if (SetIfDifferent(EmergencyFrequency, GetEmergencyFrequency()))
            {
                Config.Save();
                Logger.LogInfo("Disaster config values normalised and saved.");
            }
        }

        private void RegisterLiveConfigHandlers()
        {
            RegisterSnapHandler(EmergencyFrequency, _ => GetEmergencyFrequency());
        }

        private void RegisterSnapHandler(ConfigEntry<float> entry, System.Func<ConfigEntry<float>, float> snapFunc)
        {
            entry.SettingChanged += (_, __) =>
            {
                if (_isNormalizingConfig) return;
                _isNormalizingConfig = true;
                try
                {
                    if (SetIfDifferent(entry, snapFunc(entry)))
                        Config.Save();
                }
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
    // EmergencyFrequencyPatch
    //
    // Postfix on GameDataController.GetEmergencyChance().
    // Overrides the return value with the snapped config frequency when the
    // master toggle is active.  Postfix is sufficient — no original logic need
    // be skipped, only the return value replaced.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(GameDataController), "GetEmergencyChance")]
    public static class EmergencyFrequencyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref float __result)
        {
            if (!DisasterTweaksPlugin.EnableDisasters.Value) return;
            __result = Mathf.Clamp(DisasterTweaksPlugin.GetEmergencyFrequency(), 0f, 0.3f);
        }
    }

    // -------------------------------------------------------------------------
    // AircraftEmergencyPatch
    //
    // Full-replacement Prefix on IncidentController.TryPromptAircraftIncident().
    // Returns false in all branches to skip the original entirely.
    //
    // Replicates the vanilla ChanceOccured gate via GameDataController.
    // GetEmergencyChance() (already intercepted by EmergencyFrequencyPatch).
    // Builds a filtered list of enabled aircraft emergency types from the
    // individual section 2 toggles, then calls PromptIncident directly on the
    // filtered list.  Disabling a type redistributes its probability equally
    // across remaining enabled types — overall frequency is unchanged.
    //
    // Prefix chosen because the original contains the type-selection call that
    // must be intercepted; a Postfix cannot undo a selection already made.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(IncidentController), "TryPromptAircraftIncident")]
    public static class AircraftEmergencyPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!DisasterTweaksPlugin.EnableDisasters.Value) return false;
            if (!DisasterTweaksPlugin.EnableAircraftEmergencies.Value) return false;
            if (!Utils.ChanceOccured(GameDataController.GetEmergencyChance())) return false;

            var filtered = new List<Enums.IncidentType>();
            if (DisasterTweaksPlugin.EnableEngineFailure.Value) filtered.Add(Enums.IncidentType.EngineFailure);
            if (DisasterTweaksPlugin.EnableEquipmentFailure.Value) filtered.Add(Enums.IncidentType.EquipmentFailure);
            if (DisasterTweaksPlugin.EnableWeatherWarning.Value) filtered.Add(Enums.IncidentType.WeatherWarning);
            if (DisasterTweaksPlugin.EnableFuelWarning.Value) filtered.Add(Enums.IncidentType.FuelWarning);
            if (DisasterTweaksPlugin.EnableInflightMedical.Value) filtered.Add(Enums.IncidentType.InflightMedical);
            if (DisasterTweaksPlugin.EnableInflightSecurity.Value) filtered.Add(Enums.IncidentType.InflightSecurity);
            if (DisasterTweaksPlugin.EnableAirAmbulanceFlight.Value) filtered.Add(Enums.IncidentType.AirAmbulanceFlight);

            if (filtered.Count == 0) return false;

            Singleton<IncidentController>.Instance.PromptIncident(
                Utils.RandomItemInCollection<Enums.IncidentType>(filtered), null);

            return false;
        }
    }

    // -------------------------------------------------------------------------
    // WorldEventPatch
    //
    // Full-replacement Prefix on IncidentController.TrySpawnIncident().
    // Returns false in all branches to skip the original entirely.
    //
    // Mirrors AircraftEmergencyPatch: replicates the vanilla ChanceOccured gate,
    // builds a filtered list from the section 3 individual toggles, and calls
    // SpawnIncident directly.  Disabling a type redistributes its probability
    // equally across remaining enabled types — overall frequency is unchanged.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(IncidentController), "TrySpawnIncident")]
    public static class WorldEventPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!DisasterTweaksPlugin.EnableDisasters.Value) return false;
            if (!DisasterTweaksPlugin.EnableWorldEvents.Value) return false;
            if (!Utils.ChanceOccured(GameDataController.GetEmergencyChance())) return false;

            var filtered = new List<Enums.IncidentType>();
            if (DisasterTweaksPlugin.EnablePowerSurge.Value) filtered.Add(Enums.IncidentType.PowerSurge);
            if (DisasterTweaksPlugin.EnableRailwaySignalFailure.Value) filtered.Add(Enums.IncidentType.RailwaySignalFailure);
            if (DisasterTweaksPlugin.EnablePlumbingFailure.Value) filtered.Add(Enums.IncidentType.PlumbingFailure);
            if (DisasterTweaksPlugin.EnableVolcanoEruption.Value) filtered.Add(Enums.IncidentType.VolcanoEruption);
            if (DisasterTweaksPlugin.EnableGlobalEconomyCrash.Value) filtered.Add(Enums.IncidentType.GlobalEconomyCrash);
            if (DisasterTweaksPlugin.EnablePandemic.Value) filtered.Add(Enums.IncidentType.Pandemic);
            if (DisasterTweaksPlugin.EnableOilCrisis.Value) filtered.Add(Enums.IncidentType.OilCrisis);

            if (filtered.Count == 0) return false;

            Singleton<IncidentController>.Instance.SpawnIncident(
                Utils.RandomItemInCollection<Enums.IncidentType>(filtered), default(System.TimeSpan));

            return false;
        }
    }

    // -------------------------------------------------------------------------
    // StaffStrikePatch
    //
    // Prefix on IncidentController.TrySpawnCausalityIncident().
    // Returns the category toggle value directly — true passes through to the
    // original (vanilla strike logic runs), false suppresses it entirely.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(IncidentController), "TrySpawnCausalityIncident")]
    public static class StaffStrikePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!DisasterTweaksPlugin.EnableDisasters.Value) return true;
            return DisasterTweaksPlugin.EnableStaffStrikes.Value;
        }
    }
}
