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
    /// Host heartbeat: every serialization event the host sends, for ANY object, proves
    /// it is alive. Until v0.5.0 this was also where host states were scraped out of the
    /// raw payload (NetworkingReworked's capture), but the payload is now one packed
    /// byte[] per object — states are read from the game's own decode instead
    /// (TransformViewSerializePatch).
    /// </summary>
    [HarmonyPatch]
    internal static class SerializeReadCapturePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PhotonNetwork), "OnSerializeRead");
        }

        private static void Prefix(Player sender)
        {
            if (!SimManager.Ready || PhotonNetwork.IsMasterClient) return;
            if (sender != null && sender == PhotonNetwork.MasterClient) SimManager.NoteHostTraffic();
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
            // ALWAYS drop our instant-grab entry, even when the sim already self-healed
            // (an unseen release cleared localGrab before this ran) or the object was
            // handed back mid-hold. A leftover entry keeps heldByLocalPlayer true, and
            // ItemToggle.Update then fires on OUR E presses with OUR photon id from
            // anywhere on the map — other players' upgrade orbs popped credited to us.
            __instance.playerGrabbing.Remove(player);
            if (!SimManager.IsLocalGrab(__instance)) return;

            SimManager.LocalThrow(__instance, player);
            SimManager.EndLocalGrab(__instance);
        }
    }

    /// <summary>
    /// The host's grab broadcast arrives after we already added ourselves locally, and the
    /// RPC adds unconditionally — duplicate entry = doubled grab forces. Since v0.5.0 the
    /// RPC also does real work (throttle reset, event hooks), so instead of skipping it we
    /// take our early entry out and let the host's genuine broadcast add the one copy.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabObject), "GrabPlayerAddRPC")]
    internal static class GrabPlayerAddDedupePatch
    {
        private static void Prefix(PhysGrabObject __instance, int photonViewID, PhotonMessageInfo _info)
        {
            if (!SimManager.Ready || !SimManager.IsClientInLobby()) return;
            // only a real host broadcast will re-add; a rejected one must not cost our entry
            if (_info.Sender != PhotonNetwork.MasterClient) return;

            PhotonView pv = PhotonView.Find(photonViewID);
            PhysGrabber grabber = pv != null ? pv.GetComponent<PhysGrabber>() : null;
            if (grabber != null && __instance.playerGrabbing.Remove(grabber))
            {
                if (Plugin.VerboseLogging.Value)
                {
                    Plugin.Log.LogInfo($"[dedupe] host grab broadcast for {__instance.name} (already registered locally)");
                }
            }
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
    /// Host state for simulated objects comes from the game's OWN decode of the packet
    /// (v0.5.0 packs it into a byte[] we would otherwise have to re-implement). The
    /// original always runs; the postfix copies the decoded host state into the simulation.
    /// The decode also writes to the rigidbody — isKinematic (the host flags every HELD
    /// object kinematic for clients, every packet), sleep/wake with zeroed velocity, and a
    /// position snap on the first packet. The old stream-discard never let any of that touch
    /// an object we simulate, so the transpiler routes those writes through filters that
    /// drop them for the object being decoded.
    /// </summary>
    [HarmonyPatch(typeof(PhotonTransformView), "OnPhotonSerializeView")]
    internal static class TransformViewSerializePatch
    {
        private static void Prefix(PhotonTransformView __instance, PhotonStream stream, PhotonMessageInfo info,
            bool __runOriginal, out SimManager.HostRead __state)
        {
            __state = default;
            SimManager.activeRead = null;
            // another mod's prefix skipped the original: nothing gets decoded to copy
            if (!__runOriginal || !SimManager.Ready || stream.IsWriting) return;
            // vanilla ignores non-host senders; so do we
            if (info.Sender != PhotonNetwork.MasterClient) return;
            SimManager.BeginHostRead(__instance, info.SentServerTimestamp, out __state);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var filters = new Dictionary<MethodInfo, MethodInfo>();
            void Map(MethodInfo original, string filter)
            {
                if (original != null) filters[original] = AccessTools.Method(typeof(TransformViewSerializePatch), filter);
            }
            Map(AccessTools.PropertySetter(typeof(Rigidbody), "isKinematic"), nameof(SetKinematic));
            Map(AccessTools.PropertySetter(typeof(Rigidbody), "linearVelocity"), nameof(SetVelocity));
            Map(AccessTools.PropertySetter(typeof(Rigidbody), "velocity"), nameof(SetVelocity));
            Map(AccessTools.PropertySetter(typeof(Rigidbody), "angularVelocity"), nameof(SetAngularVelocity));
            Map(AccessTools.Method(typeof(Rigidbody), "Sleep"), nameof(Sleep));
            Map(AccessTools.Method(typeof(Rigidbody), "WakeUp"), nameof(WakeUp));
            Map(AccessTools.PropertySetter(typeof(Rigidbody), "position"), nameof(SetBodyPosition));
            Map(AccessTools.PropertySetter(typeof(Rigidbody), "rotation"), nameof(SetBodyRotation));
            Map(AccessTools.PropertySetter(typeof(Transform), "position"), nameof(SetTransformPosition));
            Map(AccessTools.PropertySetter(typeof(Transform), "rotation"), nameof(SetTransformRotation));

            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                    && ins.operand is MethodInfo m && filters.TryGetValue(m, out MethodInfo filter))
                {
                    // instance call (target, args...) -> static filter(target, args...): same stack
                    ins.opcode = OpCodes.Call;
                    ins.operand = filter;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
            {
                Plugin.Log.LogWarning("TransformViewSerializePatch: no rigidbody writes found in " +
                    "PhotonTransformView.OnPhotonSerializeView — game code may have changed.");
            }
        }

        // the decode may only touch a simulated body's physics state if we are not simulating it
        private static bool Owned(Rigidbody rb)
        {
            SimManager.SimState st = SimManager.activeRead;
            return st != null && st.rb == rb;
        }

        // the first-packet placement is vanilla behaviour we keep — except for an object in
        // our hand, which must not be yanked away
        private static bool Held(Component c)
        {
            SimManager.SimState st = SimManager.activeRead;
            return st != null && st.localGrab && st.rb != null && (c == st.rb || c == st.rb.transform);
        }

        private static void SetKinematic(Rigidbody rb, bool value) { if (!Owned(rb)) rb.isKinematic = value; }
        private static void SetVelocity(Rigidbody rb, Vector3 value) { if (!Owned(rb)) rb.linearVelocity = value; }
        private static void SetAngularVelocity(Rigidbody rb, Vector3 value) { if (!Owned(rb)) rb.angularVelocity = value; }
        private static void Sleep(Rigidbody rb) { if (!Owned(rb)) rb.Sleep(); }
        private static void WakeUp(Rigidbody rb) { if (!Owned(rb)) rb.WakeUp(); }
        private static void SetBodyPosition(Rigidbody rb, Vector3 value) { if (!Held(rb)) rb.position = value; }
        private static void SetBodyRotation(Rigidbody rb, Quaternion value) { if (!Held(rb)) rb.rotation = value; }
        private static void SetTransformPosition(Transform tf, Vector3 value) { if (!Held(tf)) tf.position = value; }
        private static void SetTransformRotation(Transform tf, Quaternion value) { if (!Held(tf)) tf.rotation = value; }

        private static void Finalizer()
        {
            SimManager.activeRead = null;
        }

        private static void Postfix(PhotonTransformView __instance, SimManager.HostRead __state)
        {
            if (__state.st == null) return;
            try
            {
                SimManager.EndHostRead(__instance, __state);
            }
            catch (Exception e)
            {
                if (Plugin.VerboseLogging.Value)
                {
                    Plugin.Log.LogWarning($"Host state read failed on {__instance.name}: {e.Message}");
                }
            }
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
            // the override system (mass/drag/torque values set by item code, our feather
            // replication, hinge drag...) is master-gated INSIDE this method — without
            // this swap, every Override* call on a client is a silent no-op. The original
            // NetworkingReworked transpiled exactly this method for the same reason.
            Add(typeof(PhysGrabObject), "OverrideTimersTick");
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
            // NOTE: do NOT transpile item orientation methods (ItemGun.UpdateMaster,
            // ItemMelee.FixedUpdate...) — tried in 1.1.0: running the game's weapon
            // orientation locally fights the client-side grab physics and the weapon
            // buzzes violently. Held items mirror the host's rotation instead.
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
            if (pgo == null) return false;

            // cart-specific game logic (steering, stabilization, cart state) may only run
            // locally while WE hold the cart. Running it for a shadowed cart makes its
            // local stabilization fight the passive sync's rotation — which is how carts
            // ended up synced in position but stuck 90 degrees off in rotation.
            if (self is PhysGrabCart && !SimManager.IsLocalGrab(pgo)) return false;

            return SimManager.HasPhysicsAuthority(pgo);
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
    /// The BepInEx plugin component's Update/FixedUpdate never ran before game v0.5.0 (they
    /// do now), so the driver loops ride on game code that provably updates: GameDirector
    /// (every frame) and PlayerController (every physics step, exists whenever gameplay
    /// exists). Both drivers are guarded to run once per frame/step, whichever fires.
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
    /// Upgrade attribution is decided entirely on the client: REPO credits the upgrade to
    /// whoever's ItemToggle fired, and it fires on any client whose own copy of the item
    /// reads heldByLocalPlayer when Interact is pressed. The player id rides the RPC
    /// unchallenged — the host never verifies it. These two logs record the decision from
    /// both ends (did WE claim it, and who did the credit land on), so a session log can
    /// prove whether a mis-credited upgrade came from this client or from vanilla.
    /// </summary>
    [HarmonyPatch(typeof(ItemToggle), "ToggleItem")]
    internal static class ToggleClaimLogPatch
    {
        private static void Prefix(ItemToggle __instance, bool toggle, int player)
        {
            if (!Plugin.VerboseLogging.Value || !SimManager.Ready || __instance == null) return;
            // a diagnostic must never be able to throw into game code
            try
            {
                if (__instance.GetComponent<ItemUpgrade>() == null) return;
                PhysGrabObject pgo = __instance.GetComponent<PhysGrabObject>();
                Plugin.Log.LogInfo(
                    $"[upgrade] THIS CLIENT is firing toggle={toggle} on {__instance.name}, " +
                    $"crediting photon id {player} — {SimManager.DescribeGrabState(pgo)}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ItemUpgrade), "PlayerUpgrade")]
    internal static class UpgradeCreditLogPatch
    {
        private static void Prefix(ItemUpgrade __instance)
        {
            if (!Plugin.VerboseLogging.Value || !SimManager.Ready || __instance == null) return;
            try
            {
                ItemToggle toggle = __instance.GetComponent<ItemToggle>();
                if (toggle == null || SimManager.togglePlayerId == null) return;

                int credited = SimManager.togglePlayerId(toggle);
                // PlayerAvatar.instance is null outside gameplay — never let that escape
                int local = PlayerAvatar.instance != null ? SemiFunc.PhotonViewIDPlayerAvatarLocal() : -1;
                string who = credited == local ? "THIS PLAYER (us)" : "another player";
                Plugin.Log.LogInfo(
                    $"[upgrade] {__instance.name} applying to {who} (credited id {credited}, we are {local})");
            }
            catch { }
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
