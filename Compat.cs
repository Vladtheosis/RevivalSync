using System;
using System.Collections;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using Platforms;
using UnityEngine;

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
            try { FixSelfHostedPhoton(); }
            catch (Exception e) { Plugin.Log.LogError($"[compat] Self_Hosted_Photon handling failed: {e}"); }
        }

        // ---- Self_Hosted_Photon (zyxterus) 0.1.0 on game v0.5.0 ----

        private static ConfigEntry<bool> selfHostEnabled;
        private static AccessTools.FieldRef<MenuPageRegions, bool> regionsConnected;
        private static AccessTools.FieldRef<MenuElementRegion, string> regionCode;

        /// <summary>
        /// Self_Hosted_Photon replaces the region page with a single "Custom Host" entry. Its
        /// coroutine calls SteamManager.SendSteamAuthTicket(), which v0.5.0 replaced with
        /// PlatformManager.SetPhotonAuthTicket() — so with it enabled the region page (the way
        /// to host) throws MissingMethodException and never loads. Same page, v0.5.0 call.
        /// Applies only while that old method is really gone; an updated mod is left alone.
        /// </summary>
        private static void FixSelfHostedPhoton()
        {
            Type patchType = AccessTools.TypeByName("Self_Hosted_Photon.Patches.MenuPageRegionsPatch");
            MethodInfo startPrefix = patchType != null ? AccessTools.Method(patchType, "StartPrefix") : null;
            if (startPrefix == null) return;
            if (AccessTools.Method(typeof(SteamManager), "SendSteamAuthTicket") != null) return;

            Type cfg = AccessTools.TypeByName("Self_Hosted_Photon.ConfigManager");
            selfHostEnabled = cfg != null ? AccessTools.Property(cfg, "Enabled")?.GetValue(null) as ConfigEntry<bool> : null;
            regionsConnected = AccessTools.FieldRefAccess<MenuPageRegions, bool>("connectedToMaster");
            regionCode = AccessTools.FieldRefAccess<MenuElementRegion, string>("regionCode");

            new Harmony(Plugin.PluginGuid).Patch(startPrefix,
                prefix: new HarmonyMethod(typeof(Compat), nameof(SelfHostStartPrefix)));
            Plugin.Log.LogInfo("[compat] Self_Hosted_Photon: region page updated for game v0.5.0 (new Steam auth call).");
        }

        private static bool SelfHostStartPrefix(MenuPageRegions __instance, ref bool __result)
        {
            // disabled in its own config: its prefix lets the game's page run; so do we
            if (selfHostEnabled != null && !selfHostEnabled.Value) return true;
            __instance.StartCoroutine(CustomHostRegionPage(__instance));
            __result = false; // skip the game's own region page, like the mod intends
            return false;
        }

        /// <summary>Self_Hosted_Photon 0.1.0's region page, with v0.5.0's connect sequence
        /// (taken from the game's own MenuPageRegions.GetRegions).</summary>
        private static IEnumerator CustomHostRegionPage(MenuPageRegions page)
        {
            PhotonNetwork.Disconnect();
            while (PhotonNetwork.NetworkingClient.State != ClientState.Disconnected
                   && PhotonNetwork.NetworkingClient.State != ClientState.PeerCreated)
            {
                yield return null;
            }
            DataDirector.instance.PhotonSetVersion();
            DataDirector.instance.PhotonSetAppId();
            PlatformManager.instance.SetPhotonAuthTicket();
            PhotonNetwork.PhotonServerSettings.AppSettings.FixedRegion = "";
            ServerSettings.ResetBestRegionCodeInPreferences();
            PhotonNetwork.ConnectUsingSettings();
            while (!regionsConnected(page))
            {
                yield return null;
            }
            page.loadingGraphics.SetDone();
            yield return new WaitForSecondsRealtime(0.3f);

            MenuElementRegion entry = UnityEngine.Object.Instantiate(page.regionPrefab,
                page.transformStartPosition.position, Quaternion.identity,
                page.transformStartPosition.parent).GetComponent<MenuElementRegion>();
            entry.textName.text = "Custom Host";
            entry.textName.color = Color.white;
            entry.textPing.text = "";
            entry.parentPage = page;
            regionCode(entry) = "custom";
            PhotonNetwork.Disconnect();
            page.menuScrollBox.enabled = true;

            while (page.scrollCanvasGroup.alpha < 0.99f)
            {
                page.scrollCanvasGroup.alpha += Time.deltaTime * 5f;
                yield return null;
            }
            page.scrollCanvasGroup.alpha = 1f;
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
