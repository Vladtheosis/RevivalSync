using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace RevivalSync
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.Revival.revivalsync";
        public const string PluginName = "RevivalSync";
        public const string PluginVersion = "1.1.3";

        internal static ManualLogSource Log;

        // simulation
        internal static ConfigEntry<bool> SimulateCarts;
        internal static ConfigEntry<bool> SimulateHinges;
        internal static ConfigEntry<bool> SimulateItems;
        internal static ConfigEntry<bool> InstantCartHandle;
        internal static ConfigEntry<float> PassiveSyncStrength;
        internal static ConfigEntry<float> PostThrowGrace;
        internal static ConfigEntry<float> SnapDistance;
        internal static ConfigEntry<float> HeldDriftCorrectAt;
        internal static ConfigEntry<float> HeldDriftHandbackAt;
        // network tweaks
        internal static ConfigEntry<bool> DisableTimeout;
        internal static ConfigEntry<bool> PhotonLateUpdate;
        internal static ConfigEntry<bool> SmoothSync;
        internal static ConfigEntry<bool> Extrapolate;
        internal static ConfigEntry<float> Future;
        internal static ConfigEntry<float> RateSmoothing;
        internal static ConfigEntry<float> TimingThreshold;
        // debugging
        internal static ConfigEntry<bool> VerboseLogging;

        /// <summary>
        /// False until the game initializes Photon itself. Touching PhotonNetwork before
        /// that (including PATCHING its methods!) forces Photon's dispatcher object into
        /// existence during plugin loading, which silently kills all connectivity.
        /// </summary>
        internal static bool PhotonReady;

        private static Harmony harmony;
        private static bool capturePatched;

        /// <summary>Applies the PhotonNetwork.OnSerializeRead capture patch once Photon is
        /// up — never earlier (see PhotonReady).</summary>
        internal static void EnsureCapturePatch()
        {
            if (capturePatched || !PhotonReady || harmony == null) return;
            capturePatched = true;
            try
            {
                harmony.PatchAll(typeof(Patches.SerializeReadCapturePatch));
                Log.LogInfo("Host-state capture hooked (deferred until Photon start).");
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to hook host-state capture: {e}");
            }
        }

        private void Awake()
        {
            Log = Logger;

            SimulateCarts = Config.Bind("Simulation", "SimulateCarts", true,
                "Simulate carts locally (removes cart input lag).");
            SimulateHinges = Config.Bind("Simulation", "SimulateHinges", true,
                "Simulate doors/cabinets locally: the game's own hinge logic (closing, latching, bounce) " +
                "runs on your machine so they respond instantly. Turn off to make them host-driven.");
            SimulateItems = Config.Bind("Simulation", "SimulateItems", true,
                "Simulate shop items (weapons, grenades, energy crystals, tools...) locally while held — " +
                "instant hand feel instead of host-driven with interpolation delay. Item effects " +
                "(damage, explosions, batteries, breaking) remain host-decided exactly like vanilla.");
            InstantCartHandle = Config.Bind("Simulation", "InstantCartHandle", true,
                "Register cart handle grabs locally right away instead of waiting for the host.");
            PassiveSyncStrength = Config.Bind("Simulation", "PassiveSyncStrength", 0.075f,
                new ConfigDescription("How strongly objects you are NOT holding blend toward the host's state each physics tick. " +
                    "This constant correction is what keeps both players seeing the same world.",
                    new AcceptableValueRange<float>(0.01f, 0.5f)));
            PostThrowGrace = Config.Bind("Simulation", "PostThrowGrace", 0.6f,
                new ConfigDescription("Seconds after you release/throw an object during which the correction is extra gentle, " +
                    "so your throw flies the way you saw it leave your hand.",
                    new AcceptableValueRange<float>(0f, 3f)));
            SnapDistance = Config.Bind("Simulation", "SnapDistance", 6f,
                new ConfigDescription("If an object you are not holding is further than this (meters) from the host's copy, " +
                    "snap it instead of blending.", new AcceptableValueRange<float>(2f, 30f)));
            HeldDriftCorrectAt = Config.Bind("Simulation", "HeldDriftCorrectAt", 1.5f,
                new ConfigDescription("While you hold an object: if your copy drifts further than this (meters) beyond the " +
                    "normal ping-trail from the host's copy, gently pull it back.",
                    new AcceptableValueRange<float>(0.5f, 10f)));
            HeldDriftHandbackAt = Config.Bind("Simulation", "HeldDriftHandbackAt", 4f,
                new ConfigDescription("While you hold an object: if your copy stays further than this (meters) beyond the normal " +
                    "ping-trail (e.g. the host's version got stuck), give control back to the host until you re-grab.",
                    new AcceptableValueRange<float>(1f, 20f)));

            DisableTimeout = Config.Bind("NetworkTweaks", "DisableTimeout", true,
                "Remove Photon's client-sided timeout that randomly kicks you from lobbies on brief lag spikes.");
            PhotonLateUpdate = Config.Bind("NetworkTweaks", "PhotonLateUpdate", true,
                "Process network packets every rendered frame instead of on the physics tick. Lower perceived latency.");
            SmoothSync = Config.Bind("SmoothSync", "Enabled", true,
                "Hermite interpolation for host-driven objects the simulation doesn't own (enemies, doors...).");
            Extrapolate = Config.Bind("SmoothSync", "Extrapolate", true,
                "Keep projecting movement briefly when an update is late or lost.");
            Future = Config.Bind("SmoothSync", "Future", 1f,
                new ConfigDescription("How far to project received data into the future, in update intervals.",
                    new AcceptableValueRange<float>(0f, 3f)));
            RateSmoothing = Config.Bind("SmoothSync", "RateSmoothing", 0.1f,
                new ConfigDescription("How quickly the measured update rate adapts.", new AcceptableValueRange<float>(0f, 1f)));
            TimingThreshold = Config.Bind("SmoothSync", "TimingThreshold", 1f,
                new ConfigDescription("Seconds between updates after which data is considered discontinuous.",
                    new AcceptableValueRange<float>(0.2f, 5f)));

            VerboseLogging = Config.Bind("Advanced", "VerboseLogging", false,
                "Log everything the sync system does: registrations, grabs/releases, snaps, handbacks, " +
                "host packet flow, stale-data warnings and periodic stats. Turn ON and attach " +
                "LogOutput.log when reporting bugs.");

            if (!SimManager.InitAccessors())
            {
                Log.LogError("RevivalSync could not find expected game internals (the game probably updated). " +
                             "The mod has disabled itself to avoid breaking your game.");
                return;
            }

            harmony = new Harmony(PluginGuid);
            int patched = 0, failed = 0;
            // NOTE: SerializeReadCapturePatch is deliberately NOT in this list. Patching a
            // PhotonNetwork method here would force Photon's static initialization during
            // plugin loading, which silently kills all connectivity ("no internet" bug).
            // It is applied lazily by EnsureCapturePatch() once the game starts Photon.
            Type[] patchTypes =
            {
                typeof(Patches.DriverHooksPatch),
                typeof(Patches.PhotonHandlerAwakePatch),
                typeof(Patches.HingeJointKeeperPatch),
                typeof(Patches.HingePushPatch),
                typeof(Patches.PhysGrabObjectStartPatch),
                typeof(Patches.GrabStartedPatch),
                typeof(Patches.GrabEndedPatch),
                typeof(Patches.GrabPlayerAddDedupePatch),
                typeof(Patches.TransformViewUpdatePatch),
                typeof(Patches.TransformViewSerializePatch),
                typeof(Patches.CartAuthorityPatch),
            };
            foreach (Type t in patchTypes)
            {
                try
                {
                    harmony.PatchAll(t);
                    patched++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.LogError($"Failed to apply patch {t.Name}: {e}");
                }
            }

            if (SmoothSync.Value && Smoothing.InitAccessors())
            {
                try
                {
                    harmony.PatchAll(typeof(Smoothing.SerializePatch));
                    harmony.PatchAll(typeof(Smoothing.UpdatePatch));
                    harmony.PatchAll(typeof(Smoothing.TeleportPatch));
                    harmony.PatchAll(typeof(Smoothing.EnablePatch));
                    Smoothing.Active = true;
                }
                catch (Exception e)
                {
                    Log.LogError($"Failed to apply SmoothSync patches, interpolation disabled: {e}");
                }
            }

            if (patched == 0)
            {
                Log.LogError("No patches could be applied — RevivalSync is inactive.");
                return;
            }

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Patches: {patched} ok, {failed} failed. " +
                        $"SmoothSync: {Smoothing.Active}. Prediction activates when you join someone else's lobby; " +
                        "the mod is inert while hosting.");
        }

        // fallback drivers: the plugin component's loops don't run in this game
        // (verified), so the real drivers are Harmony hooks (Patches.DriverHooksPatch) —
        // these stay as a harmless safety net, guarded against double-running
        private void Update()
        {
            SimDriver.FrameUpdate();
        }

        private void FixedUpdate()
        {
            SimManager.Tick();
        }

        private void OnDestroy()
        {
            SimManager.RestoreAll();
        }

        /// <summary>
        /// Plain Photon settings — nothing is patched for these, so they cannot conflict
        /// with other mods. Must only run after the game has initialized Photon.
        /// </summary>
        internal static void ApplyPhotonSettings()
        {
            if (!PhotonReady) return;
            try
            {
                if (PhotonLateUpdate.Value)
                {
                    PhotonNetwork.MinimalTimeScaleToDispatchInFixedUpdate = float.PositiveInfinity;
                }

                if (DisableTimeout.Value)
                {
                    var peer = PhotonNetwork.NetworkingClient?.LoadBalancingPeer;
                    if (peer != null)
                    {
                        // one hour instead of int.MaxValue: Photon adds this to its clock,
                        // so a huge value would overflow and instantly time out
                        peer.DisconnectTimeout = 3600000;
                        peer.SentCountAllowance = 10000;
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not apply Photon settings: {e.Message}");
            }
        }
    }
}
