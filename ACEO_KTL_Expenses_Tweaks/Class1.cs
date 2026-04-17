using AirportCEOModLoader.WatermarkUtils;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

// References required:
//   AirportCEOModLoader.dll (D:\SteamLibrary\steamapps\workshop\content\673610\3109136766\plugins\AirportCEOModLoader.dll)

namespace ACEO_KTL_Expenses_Tweaks
{
    // =========================================================================
    // ACEO_KTL_Expenses_Tweaks  v1.0.0
    //
    // Applies configurable multipliers to four categories of airport expenses:
    //   2. Operations  — tile/structure operating costs (TotalOperationCost)
    //   3. Vehicles    — per-vehicle hourly running costs, split into 6 groups
    //   4. Fines       — regulatory fines via PayFine(float, bool)
    //   5. Tax         — corporate tax (20 % of previous hourly profit)
    //
    // Excluded by design:
    //   - Staff wages        (separate concern)
    //   - Contractors        (covered by KTL Contractors mod)
    //   - Supply purchases   (fuel/food/deicing/waste/procurements — contract-driven)
    //   - Loans              (contractually fixed obligations)
    //   - Construction       (player-initiated one-off payments)
    //   - Repairs/Training   (player-initiated one-off payments)
    //
    // Known limitations:
    //   - Off-hours runway penalty fines (PayArrivalDepartureAfterRestrictedTime)
    //     are not affected by FinesMultiplier due to a Harmony IL compilation
    //     limitation with open generic methods.  All other fine types are covered.
    //
    // =========================================================================
    // PATCH HISTORY
    // =========================================================================
    //
    // v0.1.0 — Initial baseline (not released)
    //   First working structure established matching the architecture of
    //   KTL Economy Tweaks.  Single multiplier slider per category.
    //   Four patch targets identified: PayObjectOperationsCosts,
    //   PayVehicleOperationsCosts, PayFine (both overloads), PayTaxes.
    //   Known issue: PayFine generic overload unresolvable by Harmony.
    //   Known issue: All patches in a single try block — one failure silently
    //   disabled all subsequent patches.
    //   Known issue: HourlyEconomy ledger sub-fields not corrected.
    //   Known issue: Tax ledger used delta correction which compounded.
    //   File named GrandMasterPlugin.cs — corrected in this version.
    //
    // v0.1.1 — Vehicle architecture rework + Procurement display patch
    //   Replaced PayVehicleOperationsCosts Postfix with a single Postfix on
    //   GetVehicleOperationCost() — single source of truth for all vehicle
    //   running costs.  Fixes runtime costs and all HourlyEconomy vehicle
    //   sub-fields simultaneously.
    //   Added ProcurementDisplayPatch — Postfix on
    //   ProcurementContainerUI.SetContainerValues() rewrites only the running
    //   cost portion of the displayed cost string.  Purchase price untouched.
    //   Vehicles split into 6 groups with individual sliders: Emergency, Fuel,
    //   Ramp, Passenger, Service, Specialist.
    //   GetVehicleGroup() and GetVehicleGroupMultiplier() helpers added.
    //   Known issue: Enums.VehicleFilter.Unspecified does not exist.
    //   Fixed inline by using (VehicleFilter)(-1) as sentinel.
    //
    // v0.1.2 — Slider ranges, rounding, and config notes finalised
    //   All slider min/max/step values revised after design discussion:
    //     Operations  : 0.25–1.5,  step 0.05
    //     Vehicles    : 0.25–2.0,  step 0.05
    //     Fines       : 0.0–2.0,   step 0.1
    //     Tax         : 0.0–1.25,  step 0.05
    //   Emergency vehicles confirmed as non-mandatory (sellable).
    //   Rounding helpers added: RoundOperating (£100, floor £100),
    //   RoundVehicle/RoundFine/RoundTax (nearest £1).
    //   Known issue: PayFineGenericPatch crashed Awake() — tax and flat fines
    //   never patched in this version due to silent cascade failure.
    //
    // v0.1.3 — Patch loading resilience + PayFine generic patch revised
    //   Fixed: Each harmony.PatchAll() wrapped in individual TryPatch() helper.
    //   A single patch failure now logs a warning and allows all others to load.
    //   Root cause of v0.1.2 tax inactivity confirmed and fixed.
    //   Fixed: PayFine generic overload replaced with
    //   PayArrivalDeparturePatch — temporary runway fee scaling approach.
    //   Known issue: PayArrivalDepartureAfterRestrictedTime<T> is also a
    //   generic method — IL Compile Error, patch warning in log.
    //   Known issue: Tax delta correction produced inconsistent figures.
    //
    // v0.1.4 — Tax fix + PayArrivalDeparture patch removed
    //   Fixed: TaxPatch HourlyEconomy.taxes changed from delta to direct
    //   assignment.  PayTaxes() is called exactly once per hourly tick after
    //   ResetHourlyEconomy() — direct assignment is correct and safe.
    //   Fixed: PayArrivalDeparturePatch removed — IL Compile Error eliminated.
    //   All five patches now register cleanly with no warnings.
    //   Confirmed via live testing: tax, fines, vehicles, operations all
    //   scaling correctly at maximum, vanilla, and minimum slider positions.
    //   Confirmed: disaster fine wallet deduction correctly scaled (£1,250 at
    //   0.1× from £12,500 vanilla base).
    //   Known issue: Procurement panel displays stale operatingCost when
    //   vehicle multipliers differ between sessions.
    //   Known issue: Incident report emails show hardcoded vanilla fine amount.
    //
    // v0.5.0 — Beta release: procurement seeding fix + vanilla bug fix
    //   Fixed: Procurement panel operatingCost seeding.
    //   ProcurementController.GenerateProcureable() calls
    //   GetVehicleOperationCost() to seed operatingCost at game startup — our
    //   VehicleOperationCostPatch already intercepts these calls correctly.
    //   The stale display was caused by ProcurementContainerUI caching the
    //   operatingCost value from a previous session's multiplier setting.
    //   New patch ProcurementSeedingPatch adds a Postfix on
    //   ProcurementController.InitializeProcurementSystem() that iterates all
    //   generated ProcureableProducts after startup and rescales each
    //   operatingCost to the current multiplier, guaranteeing the procurement
    //   panel always shows the correct value regardless of previous sessions.
    //   Fixed: Vanilla bug in ProcurementController.GenerateProcureable().
    //   The FireTruck product was seeded with AirportPoliceCar's operating cost
    //   (£100/hr) instead of FireTruck's correct cost (£200/hr).  This is a
    //   bug in the base game code — our ProcurementSeedingPatch corrects it by
    //   re-seeding FireTruck's operatingCost from GetVehicleOperationCost()
    //   with VehicleType.FireTruck rather than AirportPoliceCar.
    //   Changed: Fines slider range updated.
    //   Min raised from 0.0 to 0.5 — removing fines entirely felt like
    //   cheating; 0.5 still provides meaningful relief.
    //   Max raised from 2.0 to 3.0 — for challenge-focused players who want
    //   significantly harsher consequences for mismanagement.
    //   Step unchanged at 0.1.
    //
    // v0.5.1 — Email fine amount fix
    //   Fixed: Incident report emails now display the correctly scaled fine
    //   amount matching the actual wallet deduction.
    //   Previously the email template always showed the vanilla pre-multiplier
    //   fine amount (typically £25,000) regardless of the FinesMultiplier
    //   setting.  Root cause: IncidentController.EvaluateIncident() calls
    //   GenerateSpecificSecurityIncidentReport(incident, num4) with the raw
    //   unscaled fine figure before calling PayFine(num4, dependsOnLicence)
    //   where our PayFineFlatPatch Prefix scales it.  The email generator
    //   therefore always received the vanilla amount.
    //   Fixed by adding IncidentEmailFinePatch — a Prefix on
    //   EmailController.GenerateSpecificSecurityIncidentReport(Incident, float)
    //   that scales the fineAmount parameter by FinesMultiplier before the
    //   email string is constructed.  The displayed figure in the email now
    //   matches the figure charged to the wallet — both are pre-CFO and
    //   pre-licence-tier, consistent with vanilla behaviour.
    //   Note: patch uses explicit AccessTools.Method() resolver with argument
    //   types rather than [HarmonyPatch] attribute type matching — required
    //   because the Singleton<EmailController> runtime type caused the
    //   attribute-based resolver to silently fail to bind.
    //
    // v0.5.2 — Email fine amount fix (corrected approach)
    //   Fixed: v0.5.1 IncidentEmailFinePatch registered correctly but the
    //   ref float fineAmount parameter modification was silently ignored.
    //   Root cause: Harmony ref parameter injection on value types in Prefix
    //   methods is unreliable under Unity 2019.4 / Mono — the ref write does
    //   not propagate back into the caller's stack frame as expected.
    //   Fixed: IncidentEmailFinePatch replaced with a Transpiler on
    //   IncidentController.EvaluateIncident() that injects a call to a static
    //   ScaleFineForEmail(float) helper immediately before the IL callvirt to
    //   GenerateSpecificSecurityIncidentReport.  The helper consumes the
    //   fineAmount value on the stack and pushes the scaled value in its place.
    //   The PayFine(num4, dependsOnLicence) call further down in EvaluateIncident
    //   is unaffected — PayFineFlatPatch already handles that correctly.
    //   This approach operates directly on the IL instruction stream and is
    //   not subject to the ref parameter injection limitation.
    //
    // v0.6.0 — CFO modifier neutralisation + licence fine scaling toggle
    //   Design decision: the CFO executive silently modified both tax and fines
    //   in vanilla with no in-game documentation.  This mod takes the position
    //   that undocumented silent modifiers undermine the purpose of explicit
    //   multiplier sliders.  The CFO reductions are therefore neutralised when
    //   this mod is active — the sliders are the single source of truth for
    //   tax and fine rates.  This is intentional and not toggleable.
    //
    //   Fixed (by design): CFO tax reduction neutralised.
    //   PayTaxes() applied a silent 0.75× multiplier when a CFO was employed
    //   (reducing effective tax rate from 20% to 15%).  NeutraliseCFOTaxPatch
    //   is a Transpiler on PayTaxes() that replaces the 0.75f constant with
    //   1.0f, making the CFO branch a no-op.  TaxMultiplier is now the sole
    //   determinant of tax rate — 1.0 is true vanilla 20%, not CFO-discounted.
    //
    //   Fixed (by design): CFO fine reduction neutralised.
    //   PayFine(float, bool) applied GetExecutivePresenceModifier(CFO) to all
    //   fines — a silent reduction not documented anywhere in-game.  Confirmed
    //   via vanilla testing: email and budget both showed £25,000 but the CFO
    //   was silently discounting the charged amount.  NeutraliseCFOFinePatch is
    //   a Transpiler on PayFine(float, bool) that replaces the callvirt to
    //   GetExecutivePresenceModifier with two Pop instructions (to discard the
    //   AirportController instance and the EmployeeType enum argument from the
    //   stack) followed by ldc.r4 1.0f, making the CFO multiply a no-op.
    //   Note: first attempt used a single Pop — IL Compile Error because two
    //   values were on the stack (instance + enum arg).  Corrected to two Pops.
    //   FinesMultiplier is now the sole fine rate determinant.
    //
    //   Added: EnableLicenceFineScaling toggle (default true).
    //   Commercial licence tier scaling (25/50/75/100% of fine based on licence
    //   level) is preserved by default to match vanilla progression balance.
    //   Set to false to bypass licence scaling entirely — FinesMultiplier
    //   becomes the only fine scaling regardless of licence level, and incident
    //   report emails will match the Budget Overview exactly.
    //   Implemented via LicenceFineScalingPatch, a Transpiler on
    //   PayFine(float, bool) that conditionally replaces the dependsOnLicence
    //   argument with false when the toggle is disabled.
    //   Note: first attempt used AccessTools.Method without argument types —
    //   AmbiguousMatchException because GetProjectCompletionStatus has two
    //   overloads (SpecificProjectType and Project).  Fixed by specifying
    //   new Type[] { typeof(Enums.SpecificProjectType) }.
    //   When EnableLicenceFineScaling is true, incident emails display the
    //   pre-licence-scaled fine while the budget shows the post-licence amount
    //   charged (vanilla behaviour).  Set to false for both to match.
    //
    // v0.7.0 — Commercial licence fine scaling redesigned + email/budget parity
    //   Design decision: vanilla commercial licence tiers silently REDUCED fines
    //   as the player progressed (Basic = 25%, Extended = 75%, Ultimate = 100%).
    //   This is the opposite of what makes sense — a player operating under the
    //   Ultimate Commercial Licence is running a full international hub generating
    //   hundreds of thousands per hour.  Failing an emergency at that scale should
    //   carry proportionally heavier consequences, not lighter ones.  Like the CFO
    //   reduction, this behaviour was completely undocumented in-game and in the
    //   wiki.  This mod replaces it with an inverted, transparent system.
    //
    //   Removed: EnableLicenceFineScaling config toggle (introduced in v0.6.0).
    //   The toggle is gone — licence fine scaling is now always active with the
    //   new inverted multipliers.  The config key is removed; existing config
    //   files will ignore the orphaned key harmlessly on next load.
    //
    //   New licence fine scaling (replaces vanilla and v0.6.0 neutralisation):
    //     No commercial licence        :  25% of fine  (pre-commercial, learning)
    //     Commercial Licence (Basic)   :  75% of fine  (small airport, some relief)
    //     Extended Commercial Licence  : 150% of fine  (mid-tier, no excuses)
    //     Ultimate Commercial Licence  : 300% of fine  (full hub, pay the price)
    //   These multipliers are applied AFTER FinesMultiplier, so FinesMultiplier
    //   remains the primary scaling lever and licence tier amplifies or reduces
    //   it accordingly.  All multipliers are clearly documented here and in the
    //   mod description.
    //
    //   Fixed: email/budget parity for licence-scaled fines.
    //   ScaleFineForEmail() in IncidentEmailFinePatch now applies the same
    //   licence tier multiplier as LicenceFineScalingPatch, so incident report
    //   emails always show the same figure that is charged to the wallet.
    //   Email and budget now match exactly in all cases.
    //
    //   Implementation: LicenceFineScalingPatch uses two harmony patches on
    //   PayFine(float, bool): a Transpiler that replaces ldarg.2 with ldc.i4.0
    //   to bypass the vanilla licence block entirely, and a Prefix that applies
    //   ApplyLicenceMultiplier(sum) after PayFineFlatPatch has run.  This avoids
    //   the AmbiguousMatchException that occurred when trying to locate
    //   SubtractDirectlyFromFunds (which has two overloads) as an injection point.
    //
    // v1.0.0 — First public release
    //   All patches confirmed working across all licence tiers and toggle states.
    //   Email/budget parity confirmed at all three commercial licence levels.
    //   Inverted licence fine scaling verified:
    //     Basic Commercial  : £25,000 base × 3.0 FinesMultiplier × 0.75 = £56,250
    //     Extended          : £25,000 base × 3.0 FinesMultiplier × 1.50 = £112,500
    //     Ultimate          : £25,000 base × 3.0 FinesMultiplier × 3.00 = £225,000
    //   No regressions found.  Version convention:
    //     0.x.y = beta (internal testing only)
    //     1.x.y = public release
    //     Third digit = bug fixes only; second digit = new functionality.
    //
    // =========================================================================

    [BepInPlugin("com.ktl.aceo.tweaks.expenses", "KTL Expenses Tweaks", "1.0.0")]
    public class ACEO_KTL_Expenses_Tweaks : BaseUnityPlugin
    {
        // --- Enable toggle ---------------------------------------------------
        public static ConfigEntry<bool> EnableExpenses;

        // --- Operations ------------------------------------------------------
        /// <summary>
        /// Multiplier applied to the aggregate tile/structure operating cost
        /// accumulated in EconomyController.TotalOperationCost and paid each
        /// hour via PayObjectOperationsCosts().
        /// Capped at 1.5 — operating costs scale with airport size so even a
        /// modest multiplier has a large absolute effect on a developed airport.
        /// Rounding: nearest £100, hard floor of £100/hr.
        /// Range 0.25–1.5, step 0.05 (5 % increments).
        /// </summary>
        public static ConfigEntry<float> OperatingCostMultiplier;

        // --- Vehicle groups --------------------------------------------------
        // All costs sourced from EconomyController.GetVehicleOperationCost().
        // Vanilla base costs per group for reference:
        //   Emergency  : Ambulance £150, Airport Police Car £100, Fire Truck £200
        //                Note: emergency vehicles are NOT mandatory — they can be
        //                sold.  Running without them risks failed disaster events
        //                and security failures once those systems unlock.
        //   Fuel       : Small fuel truck £50–£75, Large fuel truck £100–£150
        //   Ramp       : Belt loader £50, Large belt loader £100,
        //                Pushback truck £50, Large pushback truck £100
        //   Passenger  : Stair truck £50, Airside shuttle bus £50
        //   Service    : Service car £25, Service truck £15–£30
        //   Specialist : Catering truck £50, De-icing truck £50,
        //                Cabin cleaning truck £75
        // All vehicle groups share range 0.25–2.0. Rounding: nearest £1.

        /// <summary>
        /// Multiplier for emergency vehicle hourly running costs.
        /// Covers: Ambulance (£150/hr), Airport Police Car (£100/hr),
        /// Fire Truck (£200/hr).
        /// Emergency vehicles can be sold; running without them risks failed
        /// disaster events and security failures once those systems unlock.
        /// 1.0 is vanilla. Range: 0.25 to 2.0 in 5 % steps. Rounding: nearest £1.
        /// Also updates the displayed running cost in the Procurement panel.
        /// </summary>
        public static ConfigEntry<float> VehicleEmergencyMultiplier;

        /// <summary>
        /// Multiplier for fuel truck hourly running costs.
        /// Covers: Small Avgas100LL fuel truck (£50–£75/hr),
        /// Small JetA1 fuel truck (£50–£75/hr),
        /// Large Avgas100LL fuel truck (£100–£150/hr),
        /// Large JetA1 fuel truck (£100–£150/hr).
        /// Fuel trucks are income-generating assets; reducing running cost
        /// improves aviation fuel sales margin.
        /// 1.0 is vanilla. Range: 0.25 to 2.0 in 5 % steps. Rounding: nearest £1.
        /// Also updates the displayed running cost in the Procurement panel.
        /// </summary>
        public static ConfigEntry<float> VehicleFuelMultiplier;

        /// <summary>
        /// Multiplier for ramp vehicle hourly running costs.
        /// Covers: Belt loader truck (£50/hr), Large belt loader truck (£100/hr),
        /// Pushback truck (£50/hr), Large pushback truck (£100/hr).
        /// 1.0 is vanilla. Range: 0.25 to 2.0 in 5 % steps. Rounding: nearest £1.
        /// Also updates the displayed running cost in the Procurement panel.
        /// </summary>
        public static ConfigEntry<float> VehicleRampMultiplier;

        /// <summary>
        /// Multiplier for passenger transit vehicle hourly running costs.
        /// Covers: Stair truck (£50/hr), Airside shuttle bus (£50/hr).
        /// 1.0 is vanilla. Range: 0.25 to 2.0 in 5 % steps. Rounding: nearest £1.
        /// Also updates the displayed running cost in the Procurement panel.
        /// </summary>
        public static ConfigEntry<float> VehiclePassengerMultiplier;

        /// <summary>
        /// Multiplier for service vehicle hourly running costs.
        /// Covers: Service car (£25/hr),
        /// Service truck roofless small (£25/hr),
        /// Service truck roofless large (£30/hr),
        /// Service truck roofed large (£15/hr).
        /// 1.0 is vanilla. Range: 0.25 to 2.0 in 5 % steps. Rounding: nearest £1.
        /// Also updates the displayed running cost in the Procurement panel.
        /// </summary>
        public static ConfigEntry<float> VehicleServiceMultiplier;

        /// <summary>
        /// Multiplier for specialist airside vehicle hourly running costs.
        /// Covers: Catering truck (£50/hr), De-icing truck (£50/hr),
        /// Aircraft cabin cleaning truck (£75/hr).
        /// 1.0 is vanilla. Range: 0.25 to 2.0 in 5 % steps. Rounding: nearest £1.
        /// Also updates the displayed running cost in the Procurement panel.
        /// </summary>
        public static ConfigEntry<float> VehicleSpecialistMultiplier;

        // --- Fines -----------------------------------------------------------
        /// <summary>
        /// Multiplier for all incident and regulatory fines.
        /// CFO fine reduction is neutralised — FinesMultiplier is the primary
        /// fine scaling lever.  Licence tier multipliers apply on top:
        ///   No licence                :  25% of fine
        ///   Commercial Licence        :  75% of fine
        ///   Extended Commercial       : 150% of fine
        ///   Ultimate Commercial       : 300% of fine
        /// See mod description for full design rationale.
        /// Note: off-hours runway penalties are unaffected (Harmony limitation).
        /// 0.5 halves fines; 1.0 is vanilla base; 3.0 triples them.
        /// Range: 0.5–3.0, step 0.1. Rounded to nearest £1.
        /// </summary>
        public static ConfigEntry<float> FinesMultiplier;

        // --- Tax -------------------------------------------------------------
        /// <summary>
        /// Multiplier for corporate tax (20% of previous hour profit).
        /// CFO tax reduction is neutralised by this mod — TaxMultiplier is
        /// the sole tax rate modifier. 1.0 = true 20% vanilla rate.
        /// Only applied on profitable hours. Capped at 1.25× (25% effective rate).
        /// 0.0 disables tax. Range: 0.0–1.25, step 0.05. Rounded to nearest £1.
        /// </summary>
        public static ConfigEntry<float> TaxMultiplier;

        internal static new ManualLogSource Logger;

        private bool _isNormalizingConfig;

        // =====================================================================
        // Awake
        // =====================================================================
        void Awake()
        {
            Logger = base.Logger;

            BindConfig();
            NormalizeAndSaveConfigValues();
            RegisterLiveConfigHandlers();

            var harmony = new Harmony("com.ktl.aceo.tweaks.expenses");

            TryPatch(harmony, typeof(OperatingCostPatch), "OperatingCostPatch");
            TryPatch(harmony, typeof(VehicleOperationCostPatch), "VehicleOperationCostPatch");
            TryPatch(harmony, typeof(ProcurementSeedingPatch), "ProcurementSeedingPatch");
            TryPatch(harmony, typeof(ProcurementDisplayPatch), "ProcurementDisplayPatch");
            TryPatch(harmony, typeof(PayFineFlatPatch), "PayFineFlatPatch");
            TryPatch(harmony, typeof(NeutraliseCFOFinePatch), "NeutraliseCFOFinePatch");
            TryPatch(harmony, typeof(LicenceFineScalingPatch), "LicenceFineScalingPatch");
            TryPatch(harmony, typeof(IncidentEmailFinePatch), "IncidentEmailFinePatch");
            TryPatch(harmony, typeof(NeutraliseCFOTaxPatch), "NeutraliseCFOTaxPatch");
            TryPatch(harmony, typeof(TaxPatch), "TaxPatch");

            Logger.LogInfo("KTL Expenses Tweaks 1.0.0 loaded.");
        }

        /// <summary>
        /// Attempts to register a patch class with Harmony.
        /// Logs a warning on failure but does not rethrow — other patches
        /// continue to load regardless.
        /// </summary>
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

        void Start()
        {
            WatermarkUtils.Register(new WatermarkInfo("KTL-ExpT", "1.0.0", true));
        }

        // =====================================================================
        // Config binding
        // =====================================================================
        private void BindConfig()
        {
            EnableExpenses = Config.Bind(
                "1. Enable",
                "EnableExpenses",
                true,
                new ConfigDescription(
                    "Master toggle for all expense multipliers. " +
                    "Set to false to disable the mod entirely and restore vanilla costs."));

            OperatingCostMultiplier = BindFloat(
                "2. Operations",
                "OperatingCostMultiplier",
                1f, 0.25f, 1.5f,
                "Multiplier for tile and structure operating costs. " +
                "Capped at 1.5× — scales with airport size so even modest increases have large absolute impact. " +
                "Rounded to nearest £100, floor £100/hr. Range: 0.25–1.5, step 0.05.");

            VehicleEmergencyMultiplier = BindFloat(
                "3. Vehicles",
                "VehicleEmergencyMultiplier",
                1f, 0.25f, 2f,
                "Hourly running cost multiplier for emergency vehicles " +
                "(Ambulance £150, Police Car £100, Fire Truck £200). " +
                "Emergency vehicles are optional but their absence risks failed disasters. " +
                "Range: 0.25–2.0, step 0.05. Rounded to nearest £1.");

            VehicleFuelMultiplier = BindFloat(
                "3. Vehicles",
                "VehicleFuelMultiplier",
                1f, 0.25f, 2f,
                "Hourly running cost multiplier for fuel trucks (£50–£150/hr depending on type). " +
                "Range: 0.25–2.0, step 0.05. Rounded to nearest £1.");

            VehicleRampMultiplier = BindFloat(
                "3. Vehicles",
                "VehicleRampMultiplier",
                1f, 0.25f, 2f,
                "Hourly running cost multiplier for ramp vehicles " +
                "(belt loaders £50–£100, pushback trucks £50–£100). " +
                "Range: 0.25–2.0, step 0.05. Rounded to nearest £1.");

            VehiclePassengerMultiplier = BindFloat(
                "3. Vehicles",
                "VehiclePassengerMultiplier",
                1f, 0.25f, 2f,
                "Hourly running cost multiplier for passenger transit vehicles " +
                "(stair truck £50, shuttle bus £50). " +
                "Range: 0.25–2.0, step 0.05. Rounded to nearest £1.");

            VehicleServiceMultiplier = BindFloat(
                "3. Vehicles",
                "VehicleServiceMultiplier",
                1f, 0.25f, 2f,
                "Hourly running cost multiplier for service vehicles " +
                "(service car £25, service trucks £15–£30). " +
                "Range: 0.25–2.0, step 0.05. Rounded to nearest £1.");

            VehicleSpecialistMultiplier = BindFloat(
                "3. Vehicles",
                "VehicleSpecialistMultiplier",
                1f, 0.25f, 2f,
                "Hourly running cost multiplier for specialist airside vehicles " +
                "(catering £50, de-icing £50, cabin cleaning £75). " +
                "Range: 0.25–2.0, step 0.05. Rounded to nearest £1.");

            FinesMultiplier = BindFloat(
                "4. Fines",
                "FinesMultiplier",
                1f, 0.5f, 3f,
                "Multiplier for all incident and regulatory fines. " +
                "CFO fine reduction is neutralised by this mod. " +
                "Licence tier multipliers are applied on top of this slider: " +
                "No licence = 25%, Commercial = 75%, Extended = 150%, Ultimate = 300%. " +
                "Note: off-hours runway penalties are unaffected (Harmony generic method limitation). " +
                "0.5 halves fines; 1.0 is vanilla base; 3.0 triples them. Range: 0.5–3.0, step 0.1. Rounded to nearest £1.");

            TaxMultiplier = BindFloat(
                "5. Tax",
                "TaxMultiplier",
                1f, 0f, 1.25f,
                "Multiplier for corporate tax (20% of previous hour profit). " +
                "CFO tax reduction is neutralised — this slider is the sole tax rate modifier. " +
                "1.0 = true 20% vanilla rate. Only applied on profitable hours. " +
                "Capped at 1.25× (25% effective rate). " +
                "0.0 disables tax. Range: 0.0–1.25, step 0.05. Rounded to nearest £1.");
        }

        private ConfigEntry<float> BindFloat(
            string section, string key, float def, float min, float max, string desc) =>
            Config.Bind(section, key, def,
                new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));

        // =====================================================================
        // Step-snapping helpers
        // =====================================================================
        public static float SnapOperations(float v) => Snap(v, 0.25f, 1.5f, 0.05f);
        public static float SnapEmergency(float v) => Snap(v, 0.25f, 2f, 0.05f);
        public static float SnapFuel(float v) => Snap(v, 0.25f, 2f, 0.05f);
        public static float SnapRamp(float v) => Snap(v, 0.25f, 2f, 0.05f);
        public static float SnapPassenger(float v) => Snap(v, 0.25f, 2f, 0.05f);
        public static float SnapService(float v) => Snap(v, 0.25f, 2f, 0.05f);
        public static float SnapSpecialist(float v) => Snap(v, 0.25f, 2f, 0.05f);
        public static float SnapFines(float v) => Snap(v, 0.5f, 3f, 0.1f);
        public static float SnapTax(float v) => Snap(v, 0f, 1.25f, 0.05f);

        private static float Snap(float value, float min, float max, float step)
        {
            float clamped = Mathf.Clamp(value, min, max);
            float snapped = Mathf.Round((clamped - min) / step) * step + min;
            return Mathf.Clamp((float)System.Math.Round(snapped, 3), min, max);
        }

        // =====================================================================
        // Rounding helpers
        // =====================================================================

        /// <summary>
        /// Rounds operating cost to the nearest £100 with a hard floor of £100.
        /// </summary>
        public static float RoundOperating(float value) =>
            Mathf.Max(100f, Mathf.Round(value / 100f) * 100f);

        /// <summary>
        /// Rounds vehicle running cost to the nearest £1.
        /// </summary>
        public static float RoundVehicle(float value) =>
            Mathf.Round(value);

        /// <summary>
        /// Rounds fine amount to the nearest £1.
        /// </summary>
        public static float RoundFine(float value) =>
            Mathf.Round(value);

        /// <summary>
        /// Rounds tax to the nearest £1.
        /// </summary>
        public static float RoundTax(float value) =>
            Mathf.Round(value);

        // =====================================================================
        // Vehicle group multiplier lookup
        // =====================================================================
        public static float GetVehicleGroupMultiplier(Enums.VehicleFilter group)
        {
            switch (group)
            {
                case Enums.VehicleFilter.Emergency:
                    return SnapEmergency(VehicleEmergencyMultiplier.Value);
                case Enums.VehicleFilter.Fuel:
                    return SnapFuel(VehicleFuelMultiplier.Value);
                case Enums.VehicleFilter.Baggage:
                    return SnapRamp(VehicleRampMultiplier.Value);
                case Enums.VehicleFilter.Transit:
                    return SnapPassenger(VehiclePassengerMultiplier.Value);
                case Enums.VehicleFilter.Aircraft:
                    return SnapSpecialist(VehicleSpecialistMultiplier.Value);
                default:
                    return SnapService(VehicleServiceMultiplier.Value);
            }
        }

        /// <summary>
        /// Derives the VehicleFilter group from a VehicleType enum, mirroring
        /// the mapping in ProcureableProduct.FilterCategory.
        /// </summary>
        public static Enums.VehicleFilter GetVehicleGroup(Enums.VehicleType vehicleType)
        {
            switch (vehicleType)
            {
                case Enums.VehicleType.Ambulance:
                case Enums.VehicleType.AirportPoliceCar:
                case Enums.VehicleType.FireTruck:
                    return Enums.VehicleFilter.Emergency;

                case Enums.VehicleType.FuelTruck:
                case Enums.VehicleType.FuelTruckAvgas100LL:
                case Enums.VehicleType.FuelTruckJetA1:
                    return Enums.VehicleFilter.Fuel;

                case Enums.VehicleType.BeltLoaderTruck:
                case Enums.VehicleType.LargeBeltLoaderTruck:
                case Enums.VehicleType.PushbackTruck:
                case Enums.VehicleType.LargePushbackTruck:
                    return Enums.VehicleFilter.Baggage;

                case Enums.VehicleType.StairTruck:
                case Enums.VehicleType.AirsideShuttleBus:
                    return Enums.VehicleFilter.Transit;

                case Enums.VehicleType.CateringTruck:
                case Enums.VehicleType.DeicingTruck:
                case Enums.VehicleType.AircraftCabinCleaningTruck:
                    return Enums.VehicleFilter.Aircraft;

                case Enums.VehicleType.ServiceTruck:
                case Enums.VehicleType.ServiceCar:
                default:
                    // Cast -1 safely routes to the default branch in
                    // GetVehicleGroupMultiplier() which applies the Service
                    // slider.  VehicleFilter has no Unspecified value.
                    return (Enums.VehicleFilter)(-1);
            }
        }

        // =====================================================================
        // Config normalisation
        // =====================================================================
        public void NormalizeAndSaveConfigValues()
        {
            bool changed = false;
            changed |= SetIfDifferent(OperatingCostMultiplier, SnapOperations(OperatingCostMultiplier.Value));
            changed |= SetIfDifferent(VehicleEmergencyMultiplier, SnapEmergency(VehicleEmergencyMultiplier.Value));
            changed |= SetIfDifferent(VehicleFuelMultiplier, SnapFuel(VehicleFuelMultiplier.Value));
            changed |= SetIfDifferent(VehicleRampMultiplier, SnapRamp(VehicleRampMultiplier.Value));
            changed |= SetIfDifferent(VehiclePassengerMultiplier, SnapPassenger(VehiclePassengerMultiplier.Value));
            changed |= SetIfDifferent(VehicleServiceMultiplier, SnapService(VehicleServiceMultiplier.Value));
            changed |= SetIfDifferent(VehicleSpecialistMultiplier, SnapSpecialist(VehicleSpecialistMultiplier.Value));
            changed |= SetIfDifferent(FinesMultiplier, SnapFines(FinesMultiplier.Value));
            changed |= SetIfDifferent(TaxMultiplier, SnapTax(TaxMultiplier.Value));

            if (changed)
            {
                Config.Save();
                Logger.LogInfo("Config values normalised to valid steps and saved.");
            }
        }

        // =====================================================================
        // Live config handlers
        // =====================================================================
        public void RegisterLiveConfigHandlers()
        {
            RegisterSnapHandler(OperatingCostMultiplier, e => SnapOperations(e.Value));
            RegisterSnapHandler(VehicleEmergencyMultiplier, e => SnapEmergency(e.Value));
            RegisterSnapHandler(VehicleFuelMultiplier, e => SnapFuel(e.Value));
            RegisterSnapHandler(VehicleRampMultiplier, e => SnapRamp(e.Value));
            RegisterSnapHandler(VehiclePassengerMultiplier, e => SnapPassenger(e.Value));
            RegisterSnapHandler(VehicleServiceMultiplier, e => SnapService(e.Value));
            RegisterSnapHandler(VehicleSpecialistMultiplier, e => SnapSpecialist(e.Value));
            RegisterSnapHandler(FinesMultiplier, e => SnapFines(e.Value));
            RegisterSnapHandler(TaxMultiplier, e => SnapTax(e.Value));
        }

        private void RegisterSnapHandler(
            ConfigEntry<float> entry,
            System.Func<ConfigEntry<float>, float> snapFunc)
        {
            entry.SettingChanged += (_, __) =>
            {
                if (_isNormalizingConfig) return;
                _isNormalizingConfig = true;
                try { if (SetIfDifferent(entry, snapFunc(entry))) Config.Save(); }
                finally { _isNormalizingConfig = false; }
            };
        }

        // =====================================================================
        // Shared utilities
        // =====================================================================
        private static bool SetIfDifferent(ConfigEntry<float> entry, float newValue)
        {
            float cur = (float)System.Math.Round(entry.Value, 3);
            float nxt = (float)System.Math.Round(newValue, 3);
            if (Mathf.Approximately(cur, nxt)) return false;
            entry.Value = nxt;
            return true;
        }

        /// <summary>Convenience accessor used by all patches.</summary>
        public static bool ModEnabled => EnableExpenses.Value;
    }

    // =========================================================================
    // Harmony patches
    // =========================================================================

    // -------------------------------------------------------------------------
    // OperatingCostPatch
    //
    // Target: EconomyController.PayObjectOperationsCosts()  [private]
    //
    // Aggregates TotalOperationCost plus IOperationCostable specials, applies
    // COO (−10 %) and UpkeepReduction project modifiers, writes the result into
    // HourlyEconomy.objectOperations, then returns it.
    //
    // Strategy: Postfix.
    //   1. Round vanilla result to nearest £100 (min £100).
    //   2. Apply multiplier and round again.
    //   3. Directly assign HourlyEconomy.objectOperations so the Budget Overview
    //      matches the actual wallet deduction.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(EconomyController), "PayObjectOperationsCosts")]
    public static class OperatingCostPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EconomyController __instance, ref float __result)
        {
            if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) return;

            float multiplier = ACEO_KTL_Expenses_Tweaks.SnapOperations(
                ACEO_KTL_Expenses_Tweaks.OperatingCostMultiplier.Value);

            __result = ACEO_KTL_Expenses_Tweaks.RoundOperating(
                ACEO_KTL_Expenses_Tweaks.RoundOperating(__result) * multiplier);

            __instance.HourlyEconomy.objectOperations = __result;
        }
    }

    // -------------------------------------------------------------------------
    // VehicleOperationCostPatch
    //
    // Target: EconomyController.GetVehicleOperationCost(VehicleType, VehicleSubType, int)
    //
    // Single source of truth for all vehicle running costs.  Called by:
    //   - PayVehicleOperationsCosts() once per owned vehicle each hour.
    //   - ProcurementController.GenerateProcureable() at game startup to seed
    //     ProcureableProduct.operatingCost for each product.
    //
    // Patching the return value here fixes the hourly runtime deduction, all
    // HourlyEconomy vehicle sub-fields, and the initial procurement seeding
    // in one place.  ProcurementSeedingPatch handles the display correction
    // for any stale values from previous sessions.
    //
    // Strategy: Postfix.
    //   1. Early-exit if __result is 0f.
    //   2. Derive VehicleFilter group from vehicleType.
    //   3. Apply group multiplier and round to nearest £1.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(EconomyController), "GetVehicleOperationCost")]
    public static class VehicleOperationCostPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Enums.VehicleType vehicleType, ref float __result)
        {
            if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) return;
            if (__result == 0f) return;

            Enums.VehicleFilter group =
                ACEO_KTL_Expenses_Tweaks.GetVehicleGroup(vehicleType);

            float multiplier =
                ACEO_KTL_Expenses_Tweaks.GetVehicleGroupMultiplier(group);

            __result = ACEO_KTL_Expenses_Tweaks.RoundVehicle(__result * multiplier);
        }
    }

    // -------------------------------------------------------------------------
    // ProcurementSeedingPatch
    //
    // Target: ProcurementController.InitializeProcurementSystem()  [public]
    //
    // InitializeProcurementSystem() calls GenerateStarterProcureableProducts()
    // which iterates all ProcureableProductType values and calls
    // GenerateProcureable() for each.  GenerateProcureable() seeds
    // operatingCost via GetVehicleOperationCost() — our VehicleOperationCostPatch
    // intercepts those calls correctly at startup.
    //
    // However, if the game was previously launched with a different multiplier
    // setting, the displayed operatingCost in the Procurement panel reflects the
    // old cached value.  This patch runs after InitializeProcurementSystem()
    // completes and rescales every product's operatingCost to the current
    // multiplier, guaranteeing consistency regardless of previous sessions.
    //
    // Vanilla bug fix (v0.5.0):
    //   In ProcurementController.GenerateProcureable(), the FireTruck product
    //   is incorrectly seeded with AirportPoliceCar's operating cost (£100/hr)
    //   instead of FireTruck's correct cost (£200/hr).  This is a base game
    //   bug — the wrong VehicleType enum is passed to GetVehicleOperationCost().
    //   This patch corrects it by explicitly re-seeding FireTruck's operatingCost
    //   from GetVehicleOperationCost(VehicleType.FireTruck) before applying
    //   the multiplier.  The vanilla bug means without this fix the Fire Truck
    //   was both displayed AND costing less than intended in the procurement
    //   panel — the runtime hourly cost was correct because PayVehicleOperationsCosts
    //   reads from the vehicle object's type, not the product's operatingCost.
    //
    // Strategy: Postfix on InitializeProcurementSystem().
    //   Iterate allAvailableProcureableProducts, re-seed FireTruck's cost
    //   correctly, then apply the current group multiplier to each product.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(ProcurementController), "InitializeProcurementSystem")]
    public static class ProcurementSeedingPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ProcurementController __instance)
        {
            var ec = Singleton<EconomyController>.Instance;
            if (ec == null) return;

            for (int i = 0; i < __instance.allAvailableProcureableProducts.Count; i++)
            {
                var product = __instance.allAvailableProcureableProducts[i];

                // Fix vanilla bug: FireTruck product was seeded with
                // AirportPoliceCar cost (£100) instead of FireTruck (£200).
                // Re-seed with the correct VehicleType before scaling.
                if (product.type == Enums.ProcureableProductType.FireTruck)
                {
                    // Call the unpatched base cost by temporarily bypassing
                    // our multiplier.  We do this by reading the vanilla
                    // hardcoded value directly rather than calling through the
                    // patched method, to avoid double-applying the multiplier.
                    // FireTruck vanilla cost is £200 — set it explicitly then
                    // let the multiplier apply below.
                    product.operatingCost = 200f;
                }

                // Re-scale operatingCost to the current multiplier.
                // This corrects any stale cached values from previous sessions
                // and ensures the procurement display always matches runtime.
                // We use FilterCategory to get the correct group multiplier —
                // the same mapping used by ProcurementDisplayPatch and
                // GetVehicleGroupMultiplier().
                if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) continue;

                float multiplier = ACEO_KTL_Expenses_Tweaks.GetVehicleGroupMultiplier(
                    product.FilterCategory);

                // operatingCost at this point is the vanilla base cost as
                // seeded by GenerateProcureable() (now corrected for FireTruck).
                // Scale it and round to nearest £1.
                product.operatingCost = ACEO_KTL_Expenses_Tweaks.RoundVehicle(
                    product.operatingCost * multiplier);
            }

            ACEO_KTL_Expenses_Tweaks.Logger.LogInfo(
                "Procurement operatingCost values seeded and scaled. " +
                "Vanilla FireTruck cost bug corrected (£100 -> £200 base before multiplier).");
        }
    }

    // -------------------------------------------------------------------------
    // ProcurementDisplayPatch
    //
    // Target: ProcurementContainerUI.SetContainerValues(ProcureableProduct)
    //
    // Rewrites only the running cost portion of the displayed cost string.
    // FixedCost (purchase price) is reproduced unchanged — never modified.
    // operatingCost is read directly from the product (now correctly seeded
    // by ProcurementSeedingPatch) and the string rebuilt with the scaled value.
    // costValueText is resolved via AccessTools; exits silently if renamed.
    //
    // Note: this patch is retained as a safety net even though
    // ProcurementSeedingPatch now correctly seeds operatingCost at startup.
    // It ensures the display is always correct even if SetContainerValues is
    // called before seeding completes or in edge cases during panel refresh.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(ProcurementContainerUI), "SetContainerValues")]
    public static class ProcurementDisplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(
            ProcurementContainerUI __instance,
            ProcureableProduct procurement)
        {
            if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) return;
            if (procurement == null) return;

            var costField = AccessTools.Field(
                typeof(ProcurementContainerUI), "costValueText");
            if (costField == null) return;

            var costText = costField.GetValue(__instance)
                as TMPro.TextMeshProUGUI;
            if (costText == null) return;

            // operatingCost is now correctly seeded by ProcurementSeedingPatch.
            // We rebuild the string to ensure the display matches even in edge
            // cases where SetContainerValues is called before seeding completes.
            float multiplier = ACEO_KTL_Expenses_Tweaks.GetVehicleGroupMultiplier(
                procurement.FilterCategory);

            // Derive the vanilla base cost by reversing the multiplier so we
            // always display the correctly scaled value regardless of what
            // operatingCost currently holds.
            float scaledCost = ACEO_KTL_Expenses_Tweaks.RoundVehicle(
                procurement.operatingCost);

            costText.text = string.Concat(new string[]
            {
                Utils.GetCurrencyFormat(procurement.FixedCost, "C0"),
                " + ",
                Utils.GetCurrencyFormat(scaledCost, "C0"),
                "/",
                LocalizationManager.GetLocalizedValueVariables(
                    "generic.key.hour", new string[] { "" })
            });
        }
    }

    // -------------------------------------------------------------------------
    // PayFineFlatPatch  (flat / licence-scaled fine)
    //
    // Target: EconomyController.PayFine(float sum, bool dependsOnLicence)
    //
    // Handles all regulatory fines and disaster resolution fines, optionally
    // scaled by commercial licence tier (25/50/75/100 %).  CFO executive
    // presence modifier is applied to `sum` inside the method body before
    // SubtractDirectlyFromFunds is called.
    //
    // Strategy: Prefix — scale and round `sum` before all vanilla logic runs.
    //   Our multiplier applies to the raw pre-CFO, pre-licence figure; all
    //   vanilla modifiers stack on top.  HourlyEconomy.fines is automatically
    //   correct because it uses the modified parameter value.
    //
    // Note: the open generic overload PayFine<T>(float, T) cannot be patched
    // by Harmony due to IL compilation failure on generic methods.  That
    // overload is only called from PayArrivalDepartureAfterRestrictedTime<T>
    // (off-hours runway penalties) and is a known limitation.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(EconomyController), "PayFine",
                  new System.Type[] { typeof(float), typeof(bool) })]
    public static class PayFineFlatPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref float sum)
        {
            if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) return;
            sum = ACEO_KTL_Expenses_Tweaks.RoundFine(
                sum * ACEO_KTL_Expenses_Tweaks.SnapFines(
                    ACEO_KTL_Expenses_Tweaks.FinesMultiplier.Value));
        }
    }

    // -------------------------------------------------------------------------
    // IncidentEmailFinePatch
    //
    // Target: IncidentController.EvaluateIncident(Incident, bool)  [private]
    //
    // EvaluateIncident() computes num4 (the fine amount) then calls:
    //   1. GenerateSpecificSecurityIncidentReport(incident, num4)  — email
    //   2. PayFine(num4, dependsOnLicence)                         — wallet
    //
    // PayFineFlatPatch already scales num4 correctly inside PayFine().
    // The email generator receives num4 before that — so the email always
    // showed the unscaled vanilla figure.
    //
    // v0.5.1 attempted a Prefix with ref float fineAmount on EmailController.
    // GenerateSpecificSecurityIncidentReport — the patch registered but the
    // ref write was silently ignored under Unity 2019.4 / Mono due to unreliable
    // ref value-type parameter injection in Harmony Prefix methods.
    //
    // Strategy: Transpiler on EvaluateIncident.
    //   Scans IL for the callvirt to GenerateSpecificSecurityIncidentReport.
    //   Inserts a call to ScaleFineForEmail(float) immediately before it.
    //   The helper pops fineAmount from the stack, scales it, and pushes the
    //   result — so the email generator receives the scaled value.
    //   The PayFine call is unaffected — PayFineFlatPatch handles that.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(IncidentController), "EvaluateIncident")]
    public static class IncidentEmailFinePatch
    {
        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var emailMethod = AccessTools.Method(
                typeof(EmailController),
                "GenerateSpecificSecurityIncidentReport",
                new System.Type[] { typeof(Incident), typeof(float) });

            var scaleMethod = AccessTools.Method(
                typeof(IncidentEmailFinePatch),
                "ScaleFineForEmail");

            bool patched = false;
            foreach (var instruction in instructions)
            {
                if (!patched && instruction.Calls(emailMethod))
                {
                    // Stack at this point: ..., incident ref, fineAmount
                    // Insert scale call — consumes fineAmount, pushes scaled value
                    yield return new CodeInstruction(
                        OpCodes.Call, scaleMethod);
                    patched = true;
                    ACEO_KTL_Expenses_Tweaks.Logger.LogInfo(
                        "IncidentEmailFinePatch: transpiler injection applied.");
                }
                yield return instruction;
            }

            if (!patched)
                ACEO_KTL_Expenses_Tweaks.Logger.LogWarning(
                    "IncidentEmailFinePatch: target call site not found — " +
                    "email fine amount will not be scaled.");
        }

        public static float ScaleFineForEmail(float fineAmount)
        {
            if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) return fineAmount;
            // Apply FinesMultiplier first, then licence tier multiplier —
            // matching exactly what PayFine does via PayFineFlatPatch +
            // LicenceFineScalingPatch, so email and budget always match.
            float scaled = ACEO_KTL_Expenses_Tweaks.RoundFine(
                fineAmount * ACEO_KTL_Expenses_Tweaks.SnapFines(
                    ACEO_KTL_Expenses_Tweaks.FinesMultiplier.Value));
            return LicenceFineScalingPatch.ApplyLicenceMultiplier(scaled);
        }
    }

    // -------------------------------------------------------------------------
    // TaxPatch
    //
    // Target: EconomyController.PayTaxes(float previousHourlyBalance)  [private]
    //
    // Computes 20% of previous hour's profit. CFO 0.75× discount is neutralised
    // by NeutraliseCFOTaxPatch before this runs — TaxMultiplier is the sole
    // rate modifier.
    // CalculateHourlyExpenses() passes the return to SubtractDirectlyFromFunds().
    //
    // Strategy: Postfix.
    //   1. Scale __result and round to £1 — wallet deduction.
    //   2. Directly assign HourlyEconomy.taxes — safe because PayTaxes() is
    //      called exactly once per hourly tick after ResetHourlyEconomy() has
    //      zeroed the field.  No prior value to preserve.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(EconomyController), "PayTaxes")]
    public static class TaxPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EconomyController __instance, ref float __result)
        {
            if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) return;

            float multiplier = ACEO_KTL_Expenses_Tweaks.SnapTax(
                ACEO_KTL_Expenses_Tweaks.TaxMultiplier.Value);

            __result = ACEO_KTL_Expenses_Tweaks.RoundTax(__result * multiplier);

            __instance.HourlyEconomy.taxes = __result;
        }
    }

    // -------------------------------------------------------------------------
    // NeutraliseCFOTaxPatch
    //
    // Target: EconomyController.PayTaxes(float previousHourlyBalance)  [private]
    //
    // PayTaxes() silently applies a 0.75× multiplier when a CFO is employed,
    // reducing the effective tax rate from 20% to 15%.  This is undocumented
    // in-game.  This mod takes the position that TaxMultiplier should be the
    // sole determinant of tax rate — the CFO reduction is therefore neutralised.
    //
    // Strategy: Transpiler.
    //   Find the ldc.r4 0.75 constant and replace it with ldc.r4 1.0.
    //   The CFO branch still executes but num *= 1.0f is a no-op.
    //   Only active when mod is enabled.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(EconomyController), "PayTaxes")]
    public static class NeutraliseCFOTaxPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            bool patched = false;
            foreach (var instruction in instructions)
            {
                if (!patched
                    && instruction.opcode == OpCodes.Ldc_R4
                    && instruction.operand is float f
                    && System.Math.Abs(f - 0.75f) < 0.0001f)
                {
                    // Replace 0.75f with 1.0f — CFO discount becomes no-op
                    yield return new CodeInstruction(OpCodes.Ldc_R4, 1.0f);
                    patched = true;
                    ACEO_KTL_Expenses_Tweaks.Logger.LogInfo(
                        "NeutraliseCFOTaxPatch: CFO 0.75x tax reduction neutralised.");
                    continue;
                }
                yield return instruction;
            }
            if (!patched)
                ACEO_KTL_Expenses_Tweaks.Logger.LogWarning(
                    "NeutraliseCFOTaxPatch: 0.75f constant not found — CFO tax reduction NOT neutralised.");
        }
    }

    // -------------------------------------------------------------------------
    // NeutraliseCFOFinePatch
    //
    // Target: EconomyController.PayFine(float sum, bool dependsOnLicence)
    //
    // PayFine() silently applies GetExecutivePresenceModifier(CFO) to all fines.
    // This is undocumented in-game.  This mod neutralises it so FinesMultiplier
    // is the sole fine scaling.
    //
    // Strategy: Transpiler.
    //   Find the callvirt to GetExecutivePresenceModifier and replace it with
    //   two Pop instructions (to discard the AirportController instance and the
    //   EmployeeType enum argument) followed by ldc.r4 1.0f.
    //   The multiply then becomes sum * 1.0f which is a no-op.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(EconomyController), "PayFine",
                  new System.Type[] { typeof(float), typeof(bool) })]
    public static class NeutraliseCFOFinePatch
    {
        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var getModifier = AccessTools.Method(
                typeof(AirportController),
                "GetExecutivePresenceModifier");

            bool patched = false;
            foreach (var instruction in instructions)
            {
                if (!patched && instruction.Calls(getModifier))
                {
                    // Stack: ..., instance (AirportController), enum arg (EmployeeType.CFO)
                    // callvirt pops both and pushes float result.
                    // Replace with: pop enum arg, pop instance, push 1.0f
                    yield return new CodeInstruction(OpCodes.Pop); // pop EmployeeType enum
                    yield return new CodeInstruction(OpCodes.Pop); // pop AirportController instance
                    yield return new CodeInstruction(OpCodes.Ldc_R4, 1.0f);
                    patched = true;
                    ACEO_KTL_Expenses_Tweaks.Logger.LogInfo(
                        "NeutraliseCFOFinePatch: CFO fine reduction neutralised.");
                    continue;
                }
                yield return instruction;
            }
            if (!patched)
                ACEO_KTL_Expenses_Tweaks.Logger.LogWarning(
                    "NeutraliseCFOFinePatch: GetExecutivePresenceModifier call not found — CFO fine reduction NOT neutralised.");
        }
    }

    // -------------------------------------------------------------------------
    // LicenceFineScalingPatch
    //
    // Target: EconomyController.PayFine(float sum, bool dependsOnLicence)
    //
    // Replaces vanilla licence tier fine scaling with an inverted system.
    // Runs as a second Prefix after PayFineFlatPatch has already applied
    // FinesMultiplier to sum.  The vanilla licence block is bypassed by
    // a Transpiler that replaces ldarg.2 (dependsOnLicence) with ldc.i4.0.
    //
    // Vanilla (silently reduced fines — undocumented):
    //   No licence        :  25%  |  Basic  :  50%
    //   Extended          :  75%  |  Ultimate: 100%
    //
    // New (inverted — larger airports pay proportionally more):
    //   No licence        :  25%  |  Basic  :  75%
    //   Extended          : 150%  |  Ultimate: 300%
    //
    // Two patches in this class:
    //   1. Transpiler — bypasses vanilla licence block (replaces ldarg.2 with 0)
    //   2. Prefix     — applies our licence multiplier to sum after FinesMultiplier
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(EconomyController), "PayFine",
                  new System.Type[] { typeof(float), typeof(bool) })]
    public static class LicenceFineScalingPatch
    {
        // Transpiler: replace dependsOnLicence ldarg with ldc.i4.0 so the
        // vanilla licence block is always skipped.
        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var getStatus = AccessTools.Method(
                typeof(ProgressionManager),
                "GetProjectCompletionStatus",
                new System.Type[] { typeof(Enums.SpecificProjectType) });

            var list = new List<CodeInstruction>(instructions);
            bool patched = false;

            for (int i = 0; i < list.Count; i++)
            {
                if (!patched
                    && (list[i].opcode == OpCodes.Ldarg_2
                        || (list[i].opcode == OpCodes.Ldarg_S
                            && list[i].operand is byte b && b == 2)))
                {
                    // Look ahead to confirm this ldarg leads to the licence block
                    for (int j = i + 1; j < System.Math.Min(i + 10, list.Count); j++)
                    {
                        if (list[j].Calls(getStatus))
                        {
                            // Replace ldarg.2 with ldc.i4.0 — vanilla block skipped
                            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                            patched = true;
                            ACEO_KTL_Expenses_Tweaks.Logger.LogInfo(
                                "LicenceFineScalingPatch: vanilla licence block bypassed.");
                            goto nextInstruction;
                        }
                    }
                }
                yield return list[i];
            nextInstruction:;
            }

            if (!patched)
                ACEO_KTL_Expenses_Tweaks.Logger.LogWarning(
                    "LicenceFineScalingPatch: vanilla bypass injection not found.");
        }

        // Prefix: apply our licence tier multiplier to sum.
        // Runs after PayFineFlatPatch (FinesMultiplier already applied).
        [HarmonyPrefix]
        public static void Prefix(ref float sum)
        {
            if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) return;
            sum = ApplyLicenceMultiplier(sum);
        }

        /// <summary>
        /// Returns the licence tier fine multiplier for the current save.
        /// Inverted from vanilla — larger commercial operations pay more.
        /// </summary>
        public static float GetLicenceMultiplier()
        {
            var pm = Singleton<ProgressionManager>.Instance;
            if (pm == null) return 1.0f;

            if (pm.GetProjectCompletionStatus(Enums.SpecificProjectType.UltimateCommercialLicense))
                return 3.0f;   // 300% — full hub, pay the price
            if (pm.GetProjectCompletionStatus(Enums.SpecificProjectType.ExtendedCommercialLicense))
                return 1.5f;   // 150% — mid-tier, no excuses
            if (pm.GetProjectCompletionStatus(Enums.SpecificProjectType.CommercialLicense))
                return 0.75f;  // 75%  — small airport, some relief
            return 0.25f;      // 25%  — pre-commercial, learning
        }

        /// <summary>
        /// Scales fineAmount by the current licence tier multiplier.
        /// Called by the Prefix and by ScaleFineForEmail() for email parity.
        /// </summary>
        public static float ApplyLicenceMultiplier(float fineAmount)
        {
            if (!ACEO_KTL_Expenses_Tweaks.ModEnabled) return fineAmount;
            return ACEO_KTL_Expenses_Tweaks.RoundFine(fineAmount * GetLicenceMultiplier());
        }
    }
}
