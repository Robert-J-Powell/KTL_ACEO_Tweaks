using AirportCEOModLoader.WatermarkUtils;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// References required:
//   AirportCEOModLoader.dll (D:\SteamLibrary\steamapps\workshop\content\673610\3109136766\plugins\AirportCEOModLoader.dll)
// Revert to vanilla

namespace ACEO_KTL_Disaster_Tweaks
{
    [BepInPlugin("com.ktl.aceo.tweaks.disasters", "KTL Disaster Tweaks", "1.0.2")]
    public class DisasterTweaksPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> EnableDisasters;
        public static ConfigEntry<float> EmergencyFrequency;
        public static ConfigEntry<bool> EnableAircraftEmergencies;
        public static ConfigEntry<bool> EnableWorldEvents;
        public static ConfigEntry<bool> EnableStaffStrikes;

        internal static new ManualLogSource Logger;

        private bool _isNormalizingConfig;

        void Awake()
        {
            Logger = base.Logger;

            EnableDisasters = Config.Bind(
                "1. Disasters",
                "0_EnableDisasters",
                true,
                new ConfigDescription(
                    "Master toggle. When false all disaster and emergency changes are disabled and vanilla behaviour is restored. " +
                    "Note: has no effect if Sandbox mode has emergencies disabled."
                )
            );

            EmergencyFrequency = Config.Bind(
                "1. Disasters",
                "1_EmergencyFrequency",
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
                "2_EnableAircraftEmergencies",
                true,
                new ConfigDescription(
                    "When false, suppresses all aircraft emergency landings — engine failures, equipment failures, fuel warnings, " +
                    "inflight medical, inflight security, bird strikes, wheel punctures, and air ambulance flights. " +
                    "EmergencyFrequency must be above 0.0 for these to occur."
                )
            );

            EnableWorldEvents = Config.Bind(
                "1. Disasters",
                "3_EnableWorldEvents",
                true,
                new ConfigDescription(
                    "When false, suppresses all world event disasters — power surges, railway signal failures, plumbing failures, " +
                    "volcano eruptions, global economy crashes, pandemics, and oil crises. " +
                    "EmergencyFrequency must be above 0.0 for these to occur."
                )
            );

            EnableStaffStrikes = Config.Bind(
                "1. Disasters",
                "4_EnableStaffStrikes",
                true,
                new ConfigDescription(
                    "When false, suppresses staff strikes. Note: staff strikes are triggered by low employee happiness and bypass " +
                    "EmergencyFrequency entirely — they will occur regardless of that setting unless toggled off here."
                )
            );

            NormalizeAndSaveConfigValues();
            RegisterLiveConfigHandlers();

            var harmony = new Harmony("com.ktl.aceo.tweaks.disasters");
            harmony.PatchAll(typeof(EmergencyFrequencyPatch));
            harmony.PatchAll(typeof(AircraftEmergencyPatch));
            harmony.PatchAll(typeof(WorldEventPatch));
            harmony.PatchAll(typeof(StaffStrikePatch));

            Logger.LogInfo("KTL Disaster Tweaks 1.0.2 loaded.");
        }

        void Start()
        {
            WatermarkUtils.Register(new WatermarkInfo("KTL-DT", "1.0.2", true));
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

    [HarmonyPatch(typeof(IncidentController), "TryPromptAircraftIncident")]
    public static class AircraftEmergencyPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!DisasterTweaksPlugin.EnableDisasters.Value) return true;
            return DisasterTweaksPlugin.EnableAircraftEmergencies.Value;
        }
    }

    [HarmonyPatch(typeof(IncidentController), "TrySpawnIncident")]
    public static class WorldEventPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!DisasterTweaksPlugin.EnableDisasters.Value) return true;
            return DisasterTweaksPlugin.EnableWorldEvents.Value;
        }
    }

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
