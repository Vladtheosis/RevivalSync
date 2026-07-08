using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace RevivalSync.Patches
{
    /// <summary>
    /// Every eligible physics object starts being shadowed locally as soon as it spawns —
    /// there is no puppet-to-simulation handoff later, which is where desync came from.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabObject), "Start")]
    internal static class PhysGrabObjectStartPatch
    {
        private static void Postfix(PhysGrabObject __instance)
        {
            if (!SimManager.Ready) return;
            SimManager.TryRegister(__instance);
        }
    }

    /// <summary>
    /// Captures the host's object states from the raw serialization events — the same
    /// low-level capture the original NetworkingReworked used. Immune to observed-component
    /// order, other mods' patches on PhotonTransformView, and per-component read failures.
    /// </summary>
    [HarmonyPatch]
    internal static class SerializeReadCapturePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PhotonNetwork), "OnSerializeRead");
        }

        private static void Prefix(object[] data, Player sender, int networkTime)
        {
            if (!SimManager.Ready || data == null || data.Length < 4) return;
            if (PhotonNetwork.IsMasterClient) return;
            if (sender == null || sender != PhotonNetwork.MasterClient) return;
            if (!(data[0] is int viewId)) return;

            SimManager.SimState st = SimManager.GetByViewId(viewId);
            if (st == null) return;
            try
            {
                SimManager.CacheHostState(st, data, networkTime);
            }
            catch (Exception e)
            {
                if (Plugin.VerboseLogging.Value) Plugin.Log.LogWarning($"Capture failed for view {viewId}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// When the local player starts grabbing, register the grab immediately instead of
    /// waiting for the host round trip, and take local physics authority over the object.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabObject), "GrabStarted")]
    internal static class GrabStartedPatch
    {
        private static void Postfix(PhysGrabObject __instance, PhysGrabber player)
        {
            if (!SimManager.Ready || !SimManager.IsClientInLobby()) return;
            if (player == null || !player.isLocal) return;

            // called every frame while holding — keep the hot path cheap
            if (SimManager.IsLocalGrab(__instance))
            {
                if (!__instance.playerGrabbing.Contains(player))
                {
                    __instance.playerGrabbing.Add(player);
                }
                return;
            }

            // we gave this object back to the host mid-hold; stay vanilla until re-grabbed
            if (SimManager.IsHandedBack(__instance)) return;

            if (!SimManager.CanSimulate(__instance)) return;

            if (!__instance.playerGrabbing.Contains(player))
            {
                __instance.playerGrabbing.Add(player);
            }
            SimManager.StartLocalGrab(__instance);
        }
    }

    /// <summary>
    /// When the local player releases, unregister immediately, throw locally for instant
    /// feedback, and let the passive blend settle the object onto the host's trajectory.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabObject), "GrabEnded")]
    internal static class GrabEndedPatch
    {
        private static void Postfix(PhysGrabObject __instance, PhysGrabber player)
        {
            if (!SimManager.Ready || !SimManager.IsClientInLobby()) return;
            if (player == null || !player.isLocal) return;
            SimManager.ClearHandback(__instance);
            if (!SimManager.IsLocalGrab(__instance)) return;

            __instance.playerGrabbing.Remove(player);
            SimManager.LocalThrow(__instance, player);
            SimManager.EndLocalGrab(__instance);
        }
    }

    /// <summary>
    /// The host's grab broadcast arrives after we already added ourselves locally —
    /// skip it so the grabber list never holds duplicates (duplicate = doubled forces).
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabObject), "GrabPlayerAddRPC")]
    internal static class GrabPlayerAddDedupePatch
    {
        private static bool Prefix(PhysGrabObject __instance, int photonViewID)
        {
            if (!SimManager.Ready || !SimManager.IsClientInLobby()) return true;

            PhotonView pv = PhotonView.Find(photonViewID);
            PhysGrabber grabber = pv != null ? pv.GetComponent<PhysGrabber>() : null;
            if (grabber != null && __instance.playerGrabbing.Contains(grabber))
            {
                if (Plugin.VerboseLogging.Value)
                {
                    Plugin.Log.LogInfo($"[dedupe] host grab broadcast for {__instance.name} (already registered locally)");
                }
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// While we simulate an object, the transform view must not puppet it back
    /// to the host's (laggy) position every frame.
    /// </summary>
    [HarmonyPatch(typeof(PhotonTransformView), "Update")]
    internal static class TransformViewUpdatePatch
    {
        private static bool Prefix(PhotonTransformView __instance)
        {
            if (!SimManager.Ready || !SimManager.IsSuppressed(__instance)) return true;
            SimManager.TickKinematicTimer(__instance);
            return false;
        }
    }

    /// <summary>
    /// While simulating, the vanilla read must not apply host state (host state comes from
    /// our low-level capture instead). The stream is still consumed to stay aligned for any
    /// component that reads after the transform view.
    /// </summary>
    [HarmonyPatch(typeof(PhotonTransformView), "OnPhotonSerializeView")]
    internal static class TransformViewSerializePatch
    {
        private static bool Prefix(PhotonTransformView __instance, PhotonStream stream, PhotonMessageInfo info, bool __runOriginal)
        {
            // another mod's prefix already skipped the original — the stream data is
            // theirs to consume, reading it again would corrupt the packet
            if (!__runOriginal) return false;
            if (!SimManager.Ready || stream.IsWriting) return true;
            if (!SimManager.IsRegistered(__instance)) return true;

            // vanilla ignores non-host senders without reading; mirror that
            if (info.Sender != PhotonNetwork.MasterClient) return false;

            try
            {
                // consume-and-discard, must match PhotonTransformView's write order exactly
                stream.ReceiveNext(); // isSleeping
                stream.ReceiveNext(); // teleport
                stream.ReceiveNext(); // isKinematic
                stream.ReceiveNext(); // velocity
                stream.ReceiveNext(); // angularVelocity
                stream.ReceiveNext(); // position
                stream.ReceiveNext(); // direction
                stream.ReceiveNext(); // rotation
            }
            catch (Exception e)
            {
                if (Plugin.VerboseLogging.Value)
                {
                    Plugin.Log.LogWarning($"Stream discard failed on {__instance.name}: {e.Message}");
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Carts run their steering/stabilization only on the host. Reroute those
    /// authority checks so a locally-simulated cart also runs them for us.
    /// Matches calls by method identity, not IL patterns, so it survives game updates
    /// as long as the check methods themselves exist.
    /// </summary>
    [HarmonyPatch]
    internal static class CartAuthorityPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new List<MethodBase>();
            void Add(Type type, string name)
            {
                MethodInfo m = AccessTools.Method(type, name);
                if (m != null) targets.Add(m);
                else Plugin.Log.LogWarning($"CartAuthorityPatch: {type.Name}.{name} not found, skipping.");
            }

            Add(typeof(PhysGrabObject), "FixedUpdate");
            Add(typeof(PhysGrabCart), "FixedUpdate");
            Add(typeof(PhysGrabCart), "CartSteer");
            if (Plugin.SimulateHinges.Value)
            {
                // runs the game's own door logic (closing torque, latching, bounce,
                // hinge-point stabilization) locally for simulated doors
                Add(typeof(PhysGrabHinge), "FixedUpdate");
            }
            if (Plugin.InstantCartHandle.Value)
            {
                Add(typeof(PhysGrabObjectGrabArea), "Update");
            }
            return targets;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo masterOrSingle = AccessTools.Method(typeof(SemiFunc), "IsMasterClientOrSingleplayer");
            MethodInfo punIsMaster = AccessTools.PropertyGetter(typeof(PhotonNetwork), nameof(PhotonNetwork.IsMasterClient));
            MethodInfo replacement = AccessTools.Method(typeof(CartAuthorityPatch), nameof(MasterOrLocallySimulated));

            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                bool isAuthorityCheck =
                    (masterOrSingle != null && ins.Calls(masterOrSingle)) ||
                    (punIsMaster != null && ins.Calls(punIsMaster));

                if (isAuthorityCheck)
                {
                    var loadThis = new CodeInstruction(OpCodes.Ldarg_0);
                    loadThis.labels.AddRange(ins.labels);
                    loadThis.blocks.AddRange(ins.blocks);
                    yield return loadThis;
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    replaced++;
                }
                else
                {
                    yield return ins;
                }
            }

            if (replaced == 0)
            {
                Plugin.Log.LogWarning($"CartAuthorityPatch: no authority checks found in {original.DeclaringType?.Name}.{original.Name} — game code may have changed.");
            }
        }

        public static bool MasterOrLocallySimulated(Component self)
        {
            if (!GameManager.Multiplayer()) return true;
            if (PhotonNetwork.IsMasterClient) return true;
            if (self == null) return false;

            PhysGrabObject pgo = self as PhysGrabObject;
            if (pgo == null) pgo = self.GetComponent<PhysGrabObject>();
            if (pgo == null) pgo = self.GetComponentInParent<PhysGrabObject>();
            return pgo != null && SimManager.HasPhysicsAuthority(pgo);
        }
    }

    /// <summary>
    /// Vanilla clients DESTROY the hinge joint on doors/cabinets (the host simulates them
    /// remotely). Keep the joint so the locally-running hinge logic has something to swing.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabHinge), "Awake")]
    internal static class HingeJointKeeperPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo notMaster = AccessTools.Method(typeof(SemiFunc), "IsNotMasterClient");
            MethodInfo replacement = AccessTools.Method(typeof(HingeJointKeeperPatch), nameof(NotMasterAndHingeSimOff));
            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (notMaster != null && ins.Calls(notMaster))
                {
                    ins.operand = replacement;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
            {
                Plugin.Log.LogWarning("HingeJointKeeperPatch: PhysGrabHinge.Awake changed — doors stay host-driven.");
            }
        }

        public static bool NotMasterAndHingeSimOff()
        {
            return SemiFunc.IsNotMasterClient() && !Plugin.SimulateHinges.Value;
        }
    }

    /// <summary>
    /// When the local player (or their cart/held object) physically pushes a door, tell
    /// the simulation so it stops syncing the door toward the host's still-closed copy.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabHinge), "OnCollisionStay")]
    internal static class HingePushPatch
    {
        private static void Postfix(PhysGrabHinge __instance, Collision other)
        {
            if (!SimManager.Ready || !SimManager.IsClientInLobby()) return;
            SimManager.NotifyHingePushed(__instance, other);
        }
    }

    /// <summary>
    /// The BepInEx plugin component's Update/FixedUpdate never run in this game, so the
    /// driver loops ride on game code that provably updates: GameDirector (every frame)
    /// and PlayerController (every physics step, exists whenever gameplay exists).
    /// </summary>
    [HarmonyPatch]
    internal static class DriverHooksPatch
    {
        [HarmonyPatch(typeof(GameDirector), "Update")]
        [HarmonyPostfix]
        private static void OnFrame()
        {
            SimDriver.FrameUpdate();
        }

        [HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
        [HarmonyPostfix]
        private static void OnPhysicsTick()
        {
            SimManager.Tick();
        }
    }

    /// <summary>
    /// Fires when the game (or Photon itself) creates its network dispatcher —
    /// the earliest safe moment to touch PhotonNetwork.
    /// </summary>
    [HarmonyPatch(typeof(PhotonHandler), "Awake")]
    internal static class PhotonHandlerAwakePatch
    {
        private static void Postfix()
        {
            bool firstTime = !Plugin.PhotonReady;
            Plugin.PhotonReady = true;
            Plugin.ApplyPhotonSettings();
            if (firstTime)
            {
                Plugin.Log.LogInfo("Photon is up — timeout and LateUpdate dispatch settings applied.");
            }
        }
    }
}
