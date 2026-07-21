using System;
using System.IO;
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
        public const string PluginVersion = "1.2.18";

        internal static ManualLogSource Log;

        // simulation
        internal static ConfigEntry<bool> SimulateCarts;
        internal static ConfigEntry<bool> SimulateHinges;
        internal static ConfigEntry<bool> SimulateItems;
        internal static ConfigEntry<bool> InstantCartHandle;
        internal static ConfigEntry<float> PassiveSyncStrength;
        internal static ConfigEntry<float> PostThrowGrace;
        internal static ConfigEntry<float> SnapDistance;
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
        internal static ConfigEntry<KeyCode> ResyncKey;
        internal static ConfigEntry<float> AutoResyncSeconds;

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

            // ---- 1. Main: the on/off switches. Everything here is safe to flip. ----
            SimulateCarts = Config.Bind("1. Main", "Instant Carts", true,
                "Carts respond the moment you grab and push them, no waiting on the host. " +
                "Only turn this off if carts misbehave for you. Changes apply from the " +
                "next level.");
            SimulateHinges = Config.Bind("1. Main", "Instant Doors", true,
                "Doors and cabinets swing the moment you touch them, no waiting on the host. " +
                "Only turn this off if doors misbehave for you. Takes full effect on the " +
                "next game launch.");
            SimulateItems = Config.Bind("1. Main", "Instant Items", true,
                "Weapons, grenades and gadgets follow your hand instantly, aim with your " +
                "camera and fly the way you throw them. Damage and explosions are still " +
                "decided by the host, like normal. Vehicles, drones and the duck always " +
                "use the game's normal sync (they move themselves). Only turn this off " +
                "if an item misbehaves for you. Changes apply from the next level.");
            DisableTimeout = Config.Bind("1. Main", "No Timeout Kicks", true,
                "Stops the game from kicking you out of the lobby during short lag spikes.");
            ResyncKey = Config.Bind("1. Main", "Resync Loot Key", KeyCode.F8,
                "Emergency desync fix: press this key to instantly teleport every synced " +
                "object to exactly where the host sees it.");
            AutoResyncSeconds = Config.Bind("1. Main", "Auto Resync Seconds", 0f,
                new ConfigDescription(
                    "0 = off. If set, automatically performs the Resync Loot teleport every " +
                    "this many seconds, so you never have to press the key yourself.",
                    new AcceptableValueRange<float>(0f, 120f)));
            SmoothSync = Config.Bind("1. Main", "Smooth Enemies", true,
                "Enemies and other host-controlled things move smoothly instead of stuttering. " +
                "Takes effect on the next game launch.");

            // ---- 2. Fine-Tuning: the defaults are good. Touch only if asked to. ----
            const string tuning = "2. Fine-Tuning (defaults are good)";
            PassiveSyncStrength = Config.Bind(tuning, "World Sync Strength", 0.075f,
                new ConfigDescription(
                    "How firmly the world is pulled toward what the host sees. Higher = tighter sync " +
                    "but harsher corrections, lower = gentler but objects drift longer.",
                    new AcceptableValueRange<float>(0.01f, 0.5f)));
            PostThrowGrace = Config.Bind(tuning, "Throw Freedom Seconds", 0.6f,
                new ConfigDescription(
                    "How long a thrown object flies purely on your screen before syncing takes over again.",
                    new AcceptableValueRange<float>(0f, 3f)));
            SnapDistance = Config.Bind(tuning, "Teleport Distance", 6f,
                new ConfigDescription(
                    "If an object ends up further than this many meters from where the host has it, " +
                    "it teleports there instead of gliding.",
                    new AcceptableValueRange<float>(2f, 30f)));
            HeldDriftHandbackAt = Config.Bind(tuning, "Held Object Give Up At", 4f,
                new ConfigDescription(
                    "While you hold something: if it stays further off than this many meters (e.g. the " +
                    "host's copy got stuck), control returns to the host until you grab it again.",
                    new AcceptableValueRange<float>(1f, 20f)));
            InstantCartHandle = Config.Bind(tuning, "Instant Cart Handle", true,
                "Cart handle grabs register on your machine right away instead of waiting for the host.");
            PhotonLateUpdate = Config.Bind(tuning, "Process Packets Every Frame", true,
                "Handles network data every rendered frame instead of every physics tick. Lower felt latency.");
            Extrapolate = Config.Bind(tuning, "Smooth Enemies - Extrapolate", true,
                "Keep enemies moving on a predicted path for a moment when an update arrives late.");
            Future = Config.Bind(tuning, "Smooth Enemies - Look Ahead", 1f,
                new ConfigDescription(
                    "How far ahead of the received data enemies are projected, in update intervals.",
                    new AcceptableValueRange<float>(0f, 3f)));
            RateSmoothing = Config.Bind(tuning, "Smooth Enemies - Rate Adapt", 0.1f,
                new ConfigDescription(
                    "How quickly the measured update rate adapts. Leave at 0.1 — high values make " +
                    "enemies jitter with every network wobble.",
                    new AcceptableValueRange<float>(0f, 1f)));
            TimingThreshold = Config.Bind(tuning, "Smooth Enemies - Gap Seconds", 1f,
                new ConfigDescription(
                    "A silence longer than this many seconds counts as a gap instead of movement to smooth over.",
                    new AcceptableValueRange<float>(0.2f, 5f)));

            // ---- 3. Debug ----
            VerboseLogging = Config.Bind("3. Debug", "Verbose Logging", false,
                "Records everything the mod does into BepInEx/LogOutput.log. Turn this ON before " +
                "reporting a bug and include that file with the report.");

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
                typeof(Patches.ToggleClaimLogPatch),
                typeof(Patches.UpgradeCreditLogPatch),
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
            ArchiveSessionLog(true);
        }

        private static string sessionLogPath;
        private static float nextLogArchive;

        /// <summary>
        /// BepInEx overwrites LogOutput.log on every launch, which has already destroyed
        /// bug-session evidence once. Keep a copy of each session's log in
        /// BepInEx/RevivalSync-logs (newest 10 sessions), refreshed every minute so even
        /// a crash or hard kill preserves most of it.
        /// </summary>
        internal static void ArchiveSessionLog(bool force = false)
        {
            try
            {
                if (!force && Time.unscaledTime < nextLogArchive) return;
                nextLogArchive = Time.unscaledTime + 60f;

                string src = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                if (!File.Exists(src)) return;

                if (sessionLogPath == null)
                {
                    string dir = Path.Combine(Paths.BepInExRootPath, "RevivalSync-logs");
                    Directory.CreateDirectory(dir);
                    sessionLogPath = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    FileInfo[] files = new DirectoryInfo(dir).GetFiles("session-*.log");
                    Array.Sort(files, (a, b) => string.CompareOrdinal(b.Name, a.Name));
                    for (int i = 9; i < files.Length; i++) files[i].Delete();
                }
                File.Copy(src, sessionLogPath, true);
            }
            catch
            {
                // archiving must never break the game or shutdown
            }
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
