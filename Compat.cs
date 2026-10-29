using System;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace RevivalSync
{
    /// <summary>
    /// Other mods that patch the same code RevivalSync relies on. Runs once, on the first
    /// frame — every plugin has finished loading by then, whatever the load order was.
    /// </summary>
    internal static class Compat
    {
        private static bool done;

        internal static void Apply()
        {
            if (done) return;
            done = true;
            try { DisableCartSpeedSync(); }
            catch (Exception e) { Plugin.Log.LogError($"[compat] CartSpeedSync handling failed: {e}"); }
            try { FixRepoFidelity(); }
            catch (Exception e) { Plugin.Log.LogError($"[compat] REPO Fidelity handling failed: {e}"); }
        }

        /// <summary>
        /// CartSpeedSync postfixes PhysGrabCart.CartSteer to push the cart along with a
        /// sprinting player. Vanilla only runs CartSteer on the host, but RevivalSync runs it
        /// locally for the cart you hold — so it boosted OUR copy past the host's and the gap
        /// grew (measured: average drift of a held cart 0.37m without it, 0.78m with it).
        /// Not supported: all of its patches are removed.
        /// </summary>
        private static void DisableCartSpeedSync()
        {
            const string id = "discjenny.CartSpeedSync";
            if (!Chainloader.PluginInfos.ContainsKey(id)) return;
            Harmony.UnpatchID(id);
            Plugin.Log.LogWarning("[compat] CartSpeedSync is not supported and has been switched off: it speeds up " +
                "your copy of a cart past the host's, which makes held carts drift and fling loot. RevivalSync " +
                "already makes carts instant. You can uninstall CartSpeedSync.");
        }

        /// <summary>
        /// REPO Fidelity's GrabAreaIdleSkipPatch skips PhysGrabObjectGrabArea.Update outright on
        /// clients — a free saving in vanilla, where that method does nothing on a client. But
        /// Instant Cart Handle works by running exactly that method locally, so with Fidelity
        /// installed cart handle grabs silently went back to waiting on the host. Only that one
        /// prefix is removed; the rest of REPO Fidelity is untouched.
        /// </summary>
        private static void FixRepoFidelity()
        {
            if (!Plugin.InstantCartHandle.Value) return;
            var target = AccessTools.Method(typeof(PhysGrabObjectGrabArea), "Update");
            // fully qualified: RevivalSync.Patches is our own namespace
            HarmonyLib.Patches info = target != null ? Harmony.GetPatchInfo(target) : null;
            if (info == null) return;
            foreach (Patch p in info.Prefixes)
            {
                if (p.PatchMethod?.DeclaringType?.Name != "GrabAreaIdleSkipPatch") continue;
                new Harmony(Plugin.PluginGuid).Unpatch(target, p.PatchMethod);
                Plugin.Log.LogInfo($"[compat] REPO Fidelity: removed its cart-handle skip ({p.owner}) so Instant " +
                                   "Cart Handle works. Everything else in REPO Fidelity is unchanged.");
            }
        }
    }
}
