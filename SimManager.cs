using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace RevivalSync
{
    /// <summary>
    /// Every eligible PhysGrabObject on a client is simulated locally from the moment it
    /// spawns. While nobody local is holding it, its rigidbody is continuously and gently
    /// blended toward the host's streamed state ("passive shadowing" — the architecture of
    /// the original NetworkingReworked). While the local player holds it, the blend pauses
    /// and grab forces run locally for instant response.
    ///
    /// Host state is captured at the PhotonNetwork.OnSerializeRead level (raw event data,
    /// like the original mod's PhotonStreamCache) — immune to observed-component order,
    /// other mods' patches and per-component read failures.
    ///
    /// Hinged objects (doors, cabinets) are NOT simulated — the host owns them, exactly
    /// like the original mod did. Their joints/springs are host-managed and locally
    /// unreproducible without desync.
    /// </summary>
    internal static class SimManager
    {
        internal class SimState
        {
            public PhysGrabObject pgo;
            public PhotonTransformView ptv;
            public Rigidbody rb;
            public PhysGrabCart cart;       // set when this object IS a cart
            public bool isHinge;            // doors/cabinets: local joint + the game's own hinge logic
            public PhysGrabHinge hinge;
            public HingeJoint joint;        // kept alive locally; unbreakable (host decides breaks)
            public int viewId;

            public bool localGrab;          // held by the local player right now
            public float localPushTimer;    // hinges: local player/held object is pushing — go fully local
            public float postThrowTimer;    // > 0 shortly after release: blend extra softly
            public float postThrowRamp;     // after the grace: fade corrections back in (no brake)
            public float desyncTimer;       // how long we've been far from the host's copy while holding
            public float stuckTimer;        // how long we've failed to converge (object wedged in geometry)
            public float debugTimer;        // rate limit for verbose diagnostics
            public int ridingTick;          // == current tick when sitting inside a locally-held cart
            public bool mirrorHeldRot;      // item without local orientation logic: copy host rotation while held
            public ItemGun gun;             // weapons compute their hold orientation locally
            public ItemMelee melee;         // (from their own tuning fields — no network in the loop)

            public bool hasHostState;
            public float lastPacketTime = -999f;
            public Vector3 hostPos;
            public Quaternion hostRot = Quaternion.identity;
            public Vector3 hostVel;
            public Vector3 hostAngVel;
            public bool hostKinematic;
            public bool hostSleeping;
            public bool hostTeleport;
        }

        private static readonly Dictionary<PhysGrabObject, SimState> states = new Dictionary<PhysGrabObject, SimState>();
        private static readonly Dictionary<PhotonTransformView, SimState> byPtv = new Dictionary<PhotonTransformView, SimState>();
        private static readonly Dictionary<int, SimState> byViewId = new Dictionary<int, SimState>();
        private static readonly List<SimState> tickBuffer = new List<SimState>(256);

        // objects we handed back to the host mid-hold; no local grab authority until re-grabbed
        private static readonly HashSet<PhysGrabObject> handedBack = new HashSet<PhysGrabObject>();
        private static readonly List<PhysGrabObject> handedBackSweep = new List<PhysGrabObject>();

        internal static bool Ready { get; private set; }
        internal static int PacketsCaptured; // diagnostics
        private static float lastCaptureTime = -999f;

        /// <summary>True when the host has gone silent for EVERY object at once — game hung,
        /// player leaving, or dead connection. A single quiet object just means its host copy
        /// is at rest; world-wide silence means there is nothing live to sync toward.</summary>
        internal static bool HostStalled => Time.unscaledTime - lastCaptureTime > 2f;

        // ---- reflection accessors into game internals ----
        private static AccessTools.FieldRef<PhysGrabObject, bool> pgoIsMaster;
        private static AccessTools.FieldRef<ItemMelee, Quaternion> meleeYRot;
        private static AccessTools.FieldRef<PhysGrabObject, bool> pgoIsActive;
        private static AccessTools.FieldRef<PhysGrabber, PhysGrabObject> grabberObject;
        private static AccessTools.FieldRef<PhysGrabCart, List<PhysGrabObject>> cartItems;
        private static AccessTools.FieldRef<PhysGrabCart, Transform> cartInCart;
        private static AccessTools.FieldRef<PhysGrabCart, PhysGrabObjectGrabArea> cartGrabArea;
        private static AccessTools.FieldRef<PlayerAvatar, bool> avatarSprinting;
        private static AccessTools.FieldRef<PhysGrabHinge, bool> hingeBroken;
        private static MethodInfo pgoThrow;

        private static AccessTools.FieldRef<PhotonTransformView, Vector3> ptvNetPos;
        private static AccessTools.FieldRef<PhotonTransformView, Quaternion> ptvNetRot;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> ptvStoredPos;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> ptvSmoothedPos;
        private static AccessTools.FieldRef<PhotonTransformView, Quaternion> ptvSmoothedRot;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> ptvRecvPos;
        private static AccessTools.FieldRef<PhotonTransformView, Quaternion> ptvRecvRot;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> ptvPrevPos;
        private static AccessTools.FieldRef<PhotonTransformView, Quaternion> ptvPrevRot;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> ptvRecvVel;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> ptvRecvAngVel;
        private static AccessTools.FieldRef<PhotonTransformView, float> ptvDistance;
        private static AccessTools.FieldRef<PhotonTransformView, float> ptvAngle;
        private static AccessTools.FieldRef<PhotonTransformView, bool> ptvFirstTake;
        private static AccessTools.FieldRef<PhotonTransformView, bool> ptvTeleport;
        private static AccessTools.FieldRef<PhotonTransformView, bool> ptvIsSleeping;
        private static AccessTools.FieldRef<PhotonTransformView, bool> ptvKinForced;
        private static AccessTools.FieldRef<PhotonTransformView, float> ptvKinForcedTimer;

        internal static bool InitAccessors()
        {
            try
            {
                pgoIsMaster = AccessTools.FieldRefAccess<PhysGrabObject, bool>("isMaster");
                pgoIsActive = AccessTools.FieldRefAccess<PhysGrabObject, bool>("isActive");
                grabberObject = AccessTools.FieldRefAccess<PhysGrabber, PhysGrabObject>("grabbedPhysGrabObject");
                cartItems = AccessTools.FieldRefAccess<PhysGrabCart, List<PhysGrabObject>>("itemsInCart");
                cartInCart = AccessTools.FieldRefAccess<PhysGrabCart, Transform>("inCart");
                cartGrabArea = AccessTools.FieldRefAccess<PhysGrabCart, PhysGrabObjectGrabArea>("physGrabObjectGrabArea");
                avatarSprinting = AccessTools.FieldRefAccess<PlayerAvatar, bool>("isSprinting");
                hingeBroken = AccessTools.FieldRefAccess<PhysGrabHinge, bool>("broken");
                meleeYRot = AccessTools.FieldRefAccess<ItemMelee, Quaternion>("currentYRotation");
                pgoThrow = AccessTools.Method(typeof(PhysGrabObject), "Throw");

                ptvNetPos = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("m_NetworkPosition");
                ptvNetRot = AccessTools.FieldRefAccess<PhotonTransformView, Quaternion>("m_NetworkRotation");
                ptvStoredPos = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("m_StoredPosition");
                ptvSmoothedPos = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("smoothedPosition");
                ptvSmoothedRot = AccessTools.FieldRefAccess<PhotonTransformView, Quaternion>("smoothedRotation");
                ptvRecvPos = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("receivedPosition");
                ptvRecvRot = AccessTools.FieldRefAccess<PhotonTransformView, Quaternion>("receivedRotation");
                ptvPrevPos = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("prevPosition");
                ptvPrevRot = AccessTools.FieldRefAccess<PhotonTransformView, Quaternion>("prevRotation");
                ptvRecvVel = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("receivedVelocity");
                ptvRecvAngVel = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("receivedAngularVelocity");
                ptvDistance = AccessTools.FieldRefAccess<PhotonTransformView, float>("m_Distance");
                ptvAngle = AccessTools.FieldRefAccess<PhotonTransformView, float>("m_Angle");
                ptvFirstTake = AccessTools.FieldRefAccess<PhotonTransformView, bool>("m_firstTake");
                ptvTeleport = AccessTools.FieldRefAccess<PhotonTransformView, bool>("teleport");
                ptvIsSleeping = AccessTools.FieldRefAccess<PhotonTransformView, bool>("isSleeping");
                ptvKinForced = AccessTools.FieldRefAccess<PhotonTransformView, bool>("kinematicClientForced");
                ptvKinForcedTimer = AccessTools.FieldRefAccess<PhotonTransformView, float>("kinematicClientForcedTimer");

                if (pgoThrow == null)
                {
                    Plugin.Log.LogWarning("PhysGrabObject.Throw not found — throws will rely on host only.");
                }

                Ready = true;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Accessor init failed: {e}");
                Ready = false;
                return false;
            }
        }

        // ---- queries ----

        internal static bool IsClientInLobby()
        {
            return GameManager.Multiplayer() && PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient;
        }

        internal static bool IsSuppressed(PhotonTransformView ptv)
        {
            return ptv != null && byPtv.ContainsKey(ptv);
        }

        internal static bool IsRegistered(PhotonTransformView ptv)
        {
            return ptv != null && byPtv.ContainsKey(ptv);
        }

        internal static bool IsLocalGrab(PhysGrabObject pgo)
        {
            return pgo != null && states.TryGetValue(pgo, out SimState st) && st.localGrab;
        }

        /// <summary>True for every object we shadow locally — used to open the game's
        /// master-only physics paths (cart stabilization, velocity clamps) on the client.</summary>
        internal static bool HasPhysicsAuthority(PhysGrabObject pgo)
        {
            return pgo != null && states.ContainsKey(pgo);
        }

        /// <summary>True after a mid-hold handback: don't re-take grab authority until re-grabbed.</summary>
        internal static bool IsHandedBack(PhysGrabObject pgo)
        {
            return pgo != null && handedBack.Contains(pgo);
        }

        internal static void ClearHandback(PhysGrabObject pgo)
        {
            if (pgo != null) handedBack.Remove(pgo);
        }

        /// <summary>
        /// Objects whose behavior is owned by host-side systems stay vanilla: enemies,
        /// tumbled players, hinged objects (doors/cabinets — host-managed joints and springs),
        /// and shop/inventory items. Valuables, carts and plain physics props are simulated.
        /// </summary>
        internal static bool CanSimulate(PhysGrabObject o)
        {
            if (o == null || o.rb == null) return false;
            if (o.GetComponent<PhotonView>() == null) return false;
            if (o.GetComponent<PhotonTransformView>() == null) return false;

            if (o.GetComponent<PhysGrabCart>() != null) return Plugin.SimulateCarts.Value;

            if (o.GetComponent<PlayerTumble>() != null) return false;
            if (o.GetComponentInParent<Enemy>() != null) return false;
            if (o.GetComponentInParent<EnemyRigidbody>() != null) return false;
            if (o.GetComponentInParent<PhysGrabHinge>() != null) return Plugin.SimulateHinges.Value;
            // Item policy: the client controls what it touches. Weapons use locally
            // computed orientation (their own tuning fields — no network in the rotation
            // loop); gadgets mirror the host gently. Only genuinely AUTONOMOUS objects
            // stay vanilla — things that move themselves rather than being moved:
            // drivable vehicles, flying drones, and the self-propelled duck.
            // (The old NetworkingReworked blocked all powered items instead — stable,
            // but every tool felt host-laggy in hand. We keep its blocklist only for
            // the autonomous class, where it is unambiguously right.)
            if (o.GetComponentInParent<ItemVehicle>() != null) return false;
            if (o.GetComponentInParent<ItemAttributes>() != null)
            {
                foreach (Component c in o.GetComponentsInParent<Component>(true))
                {
                    if (c == null) continue;
                    string n = c.GetType().Name;
                    if (n.StartsWith("ItemDrone") || n.StartsWith("ItemRubberDuck"))
                    {
                        return false;
                    }
                }
                return Plugin.SimulateItems.Value;
            }
            return true;
        }

        // ---- host state capture (fed by the OnSerializeRead patch) ----

        internal static SimState GetByViewId(int viewId)
        {
            return byViewId.TryGetValue(viewId, out SimState st) ? st : null;
        }

        /// <summary>
        /// Scans a raw serialization event payload for the transform view's data pattern
        /// (isSleeping, teleport, isKinematic, velocity, angularVelocity, position, direction,
        /// rotation) and caches it. Component data starts at index 3; the exact offset within
        /// the array depends on the prefab's observed-component order, so we scan for it.
        /// Delta-compressed packets with unchanged (null) slots simply don't match — which is
        /// fine, unchanged means our cache is already correct.
        /// </summary>
        internal static void CacheHostState(SimState st, object[] data, int networkTime)
        {
            for (int i = 3; i + 7 < data.Length; i++)
            {
                if (data[i] is bool sleeping && data[i + 1] is bool teleport && data[i + 2] is bool kinematic
                    && data[i + 3] is Vector3 vel && data[i + 4] is Vector3 angVel
                    && data[i + 5] is Vector3 pos && data[i + 6] is Vector3 dir
                    && data[i + 7] is Quaternion rot)
                {
                    st.hostSleeping = sleeping;
                    st.hostTeleport |= teleport;
                    st.hostKinematic = kinematic;
                    st.hostVel = vel;
                    st.hostAngVel = angVel;
                    // clamped: Photon clock noise must not kick host positions around
                    float lag = Mathf.Min(Mathf.Abs((float)(PhotonNetwork.Time - networkTime / 1000.0)), 0.3f);
                    st.hostPos = pos + dir * lag;
                    st.hostRot = rot;
                    st.hasHostState = true;
                    st.lastPacketTime = Time.unscaledTime;
                    lastCaptureTime = Time.unscaledTime;
                    PacketsCaptured++;
                    return;
                }
            }
        }

        // ---- registration ----

        /// <summary>Called for every PhysGrabObject when it awakes (and on grab as a fallback).</summary>
        internal static SimState TryRegister(PhysGrabObject pgo)
        {
            if (!Ready || pgo == null) return null;
            if (states.TryGetValue(pgo, out SimState existing)) return existing;
            if (!IsClientInLobby()) return null;
            if (!CanSimulate(pgo)) return null;

            var ptv = pgo.GetComponent<PhotonTransformView>();
            var view = pgo.GetComponent<PhotonView>();
            if (ptv == null || view == null || pgo.rb == null) return null;

            var st = new SimState
            {
                pgo = pgo,
                ptv = ptv,
                rb = pgo.rb,
                cart = pgo.GetComponent<PhysGrabCart>(),
                hinge = pgo.GetComponent<PhysGrabHinge>(),
                viewId = view.ViewID,
            };
            st.isHinge = st.hinge != null;
            if (st.isHinge)
            {
                // the game's own hinge logic runs locally for these (FixedUpdate authority
                // patch) — the joint must survive but never break on its own; the host
                // decides breaks and we mirror them
                st.joint = pgo.GetComponent<HingeJoint>();
                if (st.joint != null)
                {
                    st.joint.breakForce = float.PositiveInfinity;
                    st.joint.breakTorque = float.PositiveInfinity;
                }
                // hinge logic and hinge-point setup are gated on spawned, which vanilla
                // never sets on clients
                pgo.spawned = true;
            }
            if (!st.isHinge && st.cart == null && pgo.GetComponentInParent<ItemAttributes>() != null)
            {
                // weapons compute their hold orientation LOCALLY from their own tuning
                // fields (aim offset, tilt) — mirroring the host's rotation at full
                // strength telegraphs the 10Hz ping-late packet steps ("glitchy").
                // Gadgets without known orientation fields mirror the host gently.
                st.gun = pgo.GetComponentInChildren<ItemGun>(true);
                st.melee = pgo.GetComponentInChildren<ItemMelee>(true);
                st.mirrorHeldRot = st.gun == null && st.melee == null;
            }

            // seed from whatever the transform view last received, so we have a sane
            // target even before our own capture sees its first packet
            if (!ptvFirstTake(ptv))
            {
                st.hasHostState = true;
                st.hostPos = ptvNetPos(ptv);
                st.hostRot = ptvNetRot(ptv);
                st.hostVel = ptvRecvVel(ptv);
                st.hostAngVel = ptvRecvAngVel(ptv);
                st.hostKinematic = st.rb.isKinematic;
                st.hostSleeping = ptvIsSleeping(ptv);
            }

            states[pgo] = st;
            byPtv[ptv] = st;
            if (st.viewId != 0) byViewId[st.viewId] = st;
            if (Plugin.VerboseLogging.Value)
            {
                Plugin.Log.LogInfo($"[reg] {pgo.name} (view {st.viewId}, cart={st.cart != null}, seeded={st.hasHostState})");
            }
            return st;
        }

        // ---- state transitions ----

        internal static void StartLocalGrab(PhysGrabObject pgo)
        {
            if (!Ready || pgo == null) return;

            SimState st = TryRegister(pgo);
            if (st == null) return;

            if (!st.localGrab && Plugin.VerboseLogging.Value)
            {
                Plugin.Log.LogInfo($"Local grab authority: {pgo.name}");
            }

            st.localGrab = true;
            st.postThrowTimer = 0f;
            st.desyncTimer = 0f;
            st.stuckTimer = 0f;
            pgoIsMaster(pgo) = true;
            EnsureDynamic(st);
        }

        internal static void EndLocalGrab(PhysGrabObject pgo)
        {
            if (!Ready || pgo == null || !states.TryGetValue(pgo, out SimState st) || !st.localGrab) return;

            st.localGrab = false;
            st.desyncTimer = 0f;
            st.stuckTimer = 0f;
            // carts get extra settling time: a rapid release mid-drag otherwise blends the
            // cart back toward the host's still-trailing copy (visible rollback)
            // doors get NO throw treatment: throw grace and host-cache seeding both mask
            // the host's true door rotation (a locked shop door "released" by a player
            // would reconcile toward our own lie instead of the host's closed state)
            st.postThrowTimer = st.isHinge ? 0f
                : (st.cart != null ? Plugin.PostThrowGrace.Value * 2.5f : Plugin.PostThrowGrace.Value);
            pgoIsMaster(pgo) = false;

            // NetworkingReworked's release trick (its SyncAfterRelease/OverwriteStoredNetworkData):
            // overwrite the cached host state with our own release state. The host's
            // "still in your hand" data is exactly the stale reference that made fresh
            // throws stop mid-air once corrections resumed — from here on we correct
            // against our own throw until real packets (which include it) arrive.
            if (!st.isHinge && st.hasHostState && st.rb != null)
            {
                st.hostPos = st.rb.position;
                st.hostRot = st.rb.rotation;
                st.hostVel = st.rb.velocity;
                st.hostAngVel = st.rb.angularVelocity;
                st.hostSleeping = false;
                st.hostKinematic = false;
                st.hostTeleport = false;
                st.lastPacketTime = Time.unscaledTime;
            }

            if (Plugin.VerboseLogging.Value)
            {
                Plugin.Log.LogInfo($"Released grab authority: {pgo.name}");
            }
        }

        internal static void LocalThrow(PhysGrabObject pgo, PhysGrabber player)
        {
            if (pgoThrow == null || pgo == null || player == null) return;
            if (!states.TryGetValue(pgo, out SimState st) || st.rb == null || st.rb.isKinematic) return;
            if (st.isHinge) return; // you don't throw a door
            try
            {
                pgoThrow.Invoke(pgo, new object[] { player });
                if (Plugin.VerboseLogging.Value)
                {
                    Plugin.Log.LogInfo($"[throw] {pgo.name} vel={st.rb.velocity.magnitude:F1}");
                }
            }
            catch (Exception e)
            {
                if (Plugin.VerboseLogging.Value) Plugin.Log.LogWarning($"Local throw failed: {e.Message}");
            }
        }

        /// <summary>Points the grab controller's orientation target at the host's
        /// (already straightened) rotation — the vectors are camera-relative, exactly
        /// how PhysGrabObject.TurnXYZ writes them for item scripts.</summary>
        private static void ApplyHeldOrientation(SimState st, PhysGrabber grabber)
        {
            if (st.gun != null)
            {
                // ItemGun.UpdateMaster's orientation core, computed locally: camera-
                // forward aim with the gun's own vertical offset, held at the gun's own
                // torque/damping values. No network data in the loop = no glitch.
                st.pgo.TurnXYZ(Quaternion.Euler(st.gun.aimVerticalOffset, 0f, 0f),
                    Quaternion.identity, Quaternion.identity);
                st.pgo.OverrideTorqueStrength(2f);
                st.pgo.OverrideAngularDrag(20f);
                return;
            }
            if (st.melee != null)
            {
                // ItemMelee.TurnXYZLogic computed locally, with melee's softer hold
                if (!st.melee.usesForceRotation) return;
                Quaternion turnX = st.melee.turnWeapon
                    ? Quaternion.Euler(st.melee.forwardTilt, 0f, 0f)
                    : Quaternion.Euler(st.melee.forwardTilt + st.melee.orientationOffset.x,
                        st.melee.orientationOffset.y, st.melee.orientationOffset.z);
                st.pgo.TurnXYZ(turnX, meleeYRot(st.melee), Quaternion.identity);
                st.pgo.OverrideTorqueStrength(st.melee.customTorqueStrength ? st.melee.torqueStrength : 0.4f);
                st.pgo.OverrideAngularDrag(5f);
                return;
            }
            if (!st.mirrorHeldRot || !st.hasHostState) return;
            // gadgets without known orientation fields: gentle host mirror — soft enough
            // that the 10Hz ping-late target can't telegraph as glitching
            if (grabber.playerAvatar == null || grabber.playerAvatar.localCamera == null) return;
            Transform cam = grabber.playerAvatar.localCamera.GetOverrideTransform();
            if (cam == null) return;
            grabber.cameraRelativeGrabbedForward = cam.InverseTransformDirection(st.hostRot * Vector3.forward);
            grabber.cameraRelativeGrabbedUp = cam.InverseTransformDirection(st.hostRot * Vector3.up);
        }

        /// <summary>Frame-rate refresh of held-tool orientation targets: the grabber's
        /// beam update re-captures the target from the current rotation each frame, so a
        /// physics-tick-only write loses whole frames to the re-capture. Item scripts
        /// win this race by writing every Update — so do we.</summary>
        internal static void MirrorHeldOrientationTargets()
        {
            if (states.Count == 0) return;
            foreach (SimState st in states.Values)
            {
                if (!st.localGrab) continue;
                if (st.gun == null && st.melee == null && !st.mirrorHeldRot) continue;
                PhysGrabber grabber = GetLocalGrabber(st);
                if (grabber == null || grabber.isRotating) continue;
                ApplyHeldOrientation(st, grabber);
            }
        }

        private static void EnsureDynamic(SimState st)
        {
            if (st.rb == null) return;
            if (st.rb.isKinematic) st.rb.isKinematic = false;
            if (st.rb.IsSleeping()) st.rb.WakeUp();
        }

        internal static void TickKinematicTimer(PhotonTransformView ptv)
        {
            // replicate the tail of PhotonTransformView.Update that we skip while suppressed
            if (ptvKinForcedTimer(ptv) > 0f)
            {
                ptvKinForcedTimer(ptv) -= Time.deltaTime;
                if (ptvKinForcedTimer(ptv) <= 0f)
                {
                    ptvKinForced(ptv) = false;
                }
            }
        }

        // ---- per-physics-tick driver ----

        private static int tickCounter;
        private static float oneWayPing;
        private static bool wasClientInLobby;
        private static float lastTickFixedTime = -1f;
        private static bool tickMarker;

        internal static void Tick()
        {
            if (!Ready) return;
            // driven from both a Harmony hook and the plugin component (whichever works
            // in this setup) — run at most once per physics step
            if (Time.fixedTime == lastTickFixedTime) return;
            lastTickFixedTime = Time.fixedTime;
            if (!tickMarker)
            {
                tickMarker = true;
                Plugin.Log.LogInfo("[driver] physics tick loop running");
            }
            SweepHandedBack();

            bool clientInLobby = IsClientInLobby();
            if (clientInLobby != wasClientInLobby)
            {
                wasClientInLobby = clientInLobby;
                if (Plugin.VerboseLogging.Value)
                {
                    Plugin.Log.LogInfo(clientInLobby
                        ? "[mode] CLIENT — simulation active (we are not the host)"
                        : "[mode] simulation idle (hosting, singleplayer or no lobby)");
                }
            }

            if (states.Count == 0) return;
            tickCounter++;
            oneWayPing = PhotonNetwork.GetPing() * 0.0005f; // RTT ms → one-way seconds

            tickBuffer.Clear();
            tickBuffer.AddRange(states.Values);

            // cargo inside a cart the local player is dragging must ride the cart in pure
            // local physics — blending it toward the host's (lagging) cargo positions drags
            // the cart backwards like an anchor and slams loot through the cart walls
            foreach (SimState st in tickBuffer)
            {
                if (!st.localGrab || st.cart == null) continue;
                List<PhysGrabObject> items = cartItems(st.cart);
                if (items == null) continue;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] != null && states.TryGetValue(items[i], out SimState rider))
                    {
                        rider.ridingTick = tickCounter;
                    }
                }
            }

            foreach (SimState st in tickBuffer)
            {
                if (st.pgo == null || st.rb == null || st.ptv == null)
                {
                    HardRemove(st);
                    continue;
                }

                if (st.viewId == 0)
                {
                    var view = st.pgo.GetComponent<PhotonView>();
                    if (view != null && view.ViewID != 0)
                    {
                        st.viewId = view.ViewID;
                        byViewId[st.viewId] = st;
                    }
                }

                // left the lobby / became host / went singleplayer: give everything back
                if (!clientInLobby)
                {
                    Restore(st);
                    continue;
                }

                if (st.localGrab)
                {
                    TickHeld(st);
                }
                else
                {
                    TickShadow(st);
                }
            }
        }

        /// <summary>Local player is holding this object: full local authority, but keep both
        /// players' views bounded — the host's copy can snag on geometry and diverge.</summary>
        private static void TickHeld(SimState st)
        {
            PhysGrabber grabber = GetLocalGrabber(st);
            if (!pgoIsActive(st.pgo) || grabber == null)
            {
                // release we did not see through GrabEnded (forced drop, tumble, death, extraction)
                EndLocalGrab(st.pgo);
                return;
            }

            pgoIsMaster(st.pgo) = true; // keep asserting; game code can rewrite it
            EnsureDynamic(st);

            if (st.isHinge)
            {
                CheckHingeBroken(st);
                return; // joint-anchored: grab forces + the game's own hinge logic handle it
            }

            if (st.cart != null)
            {
                DriveHeldCart(st, grabber);
            }

            // NOTE: hostKinematic is deliberately ignored while held — the game force-flags
            // grabbed/hinged objects "kinematic" for clients (KinematicClientForce), which
            // says nothing about the host rigidbody's real state.
            if (!st.hasHostState)
            {
                DebugHeld(st, grabber, -1f, 0f);
                return;
            }
            st.hostTeleport = false; // we own it; ignore teleports until release

            // A host that has gone silent everywhere cannot acknowledge our motion —
            // correcting toward its frozen last-known position just pins the object in
            // place (the "everything was stuck" freeze when the host hangs or quits).
            if (HostStalled)
            {
                st.desyncTimer = 0f;
                st.stuckTimer = 0f;
                DebugHeld(st, grabber, -1f, st.rb.velocity.magnitude);
                return;
            }

            // The host's copy ALWAYS trails us by (speed x lag) while we drag — that trail is
            // normal and must not be "corrected", or the correction acts as a permanent brake.
            // Allow a velocity-proportional trail and compare against a velocity-led host
            // position; only true divergence gets corrected. Stale packets mean the host's
            // copy is at rest — never lead by stale velocities.
            float speed = st.rb.velocity.magnitude;
            float lagAllowance = speed * 0.4f;
            bool heldHostIdle = Time.unscaledTime - st.lastPacketTime > 0.35f;
            Vector3 ledHostPos = heldHostIdle ? st.hostPos : st.hostPos + st.hostVel * 0.15f;

            float correctAt = (st.cart != null
                ? Plugin.HeldDriftCorrectAt.Value * 0.4f
                : Plugin.HeldDriftCorrectAt.Value) + lagAllowance;

            float drift = Vector3.Distance(st.rb.position, ledHostPos);
            if (drift > Plugin.HeldDriftHandbackAt.Value + lagAllowance)
            {
                st.desyncTimer += Time.fixedDeltaTime;
                if (st.desyncTimer > 0.6f)
                {
                    ForceHandback(st);
                    return;
                }
            }
            else
            {
                st.desyncTimer = 0f;
            }

            // wedged on geometry while the host's copy (which follows our hand) is elsewhere:
            // lerping just grinds against the wall, so snap it free to where the host has it
            if (drift > 1.5f && speed < 1f)
            {
                st.stuckTimer += Time.fixedDeltaTime;
                if (st.stuckTimer > 0.8f)
                {
                    st.stuckTimer = 0f;
                    Snap(st, "held object wedged in geometry, freeing to host position");
                    return;
                }
            }
            else
            {
                st.stuckTimer = 0f;
            }

            if (drift > correctAt)
            {
                // acceleration-style nudge instead of a raw position write: it composes
                // with the cart drive and renders smoothly through rigidbody interpolation
                st.rb.velocity += Vector3.ClampMagnitude(ledHostPos - st.rb.position, 3f)
                                  * (2.5f * Time.fixedDeltaTime);
            }

            // gadgets without local orientation logic: the host runs their straightening
            // scripts on our behalf. To make the tool STAY straight we do what the game's
            // own item scripts do while imposing orientation (see ItemGun.UpdateMaster):
            // neutralize the default grab torque — whose different target orientation is
            // what shoved the tool off-straight — heavily damp rotation, then steer
            // angular velocity toward the host's rotation, unopposed. Manual rotation
            // (rotate key) gets priority, exactly like the game's gun code does it.
            // Retarget the game's own grab-orientation controller instead of steering the
            // rigidbody: the grab code re-captures its target from the object's CURRENT
            // rotation every frame (PhysGrabber beam update) and overwrites outside
            // angular writes — which is why three rigidbody-steering attempts all felt
            // "weak". Writing the camera-relative target vectors (the same channel item
            // scripts use via TurnXYZ) makes the game's own tuned torque do the
            // straightening at native strength. Also written per-frame from SimDriver.
            if (!grabber.isRotating)
            {
                ApplyHeldOrientation(st, grabber);
            }

            DebugHeld(st, grabber, drift, speed);
        }

        private static void DebugHeld(SimState st, PhysGrabber grabber, float drift, float speed)
        {
            if (!Plugin.VerboseLogging.Value) return;
            st.debugTimer -= Time.fixedDeltaTime;
            if (st.debugTimer > 0f) return;
            st.debugTimer = 1f;
            float packetAge = st.hasHostState ? Time.unscaledTime - st.lastPacketTime : -1f;
            Plugin.Log.LogInfo(
                $"[held] {st.pgo.name}: speed={speed:F1} drift={drift:F1} hostState={st.hasHostState} " +
                $"packetAge={packetAge:F1}s hostKin={st.hostKinematic} hostSleep={st.hostSleeping} " +
                $"steered={grabber.physGrabForcesDisabledTimer > 0f}");
        }

        /// <summary>
        /// The game moves handle-held carts by directly steering their velocity toward a
        /// follow point behind the player — but only under host-side conditions (cartActive,
        /// grab-area lists) that are unreliable on clients. If the game's own steering didn't
        /// run this tick (its marker is the disabled-grab-forces timer), run the same drive
        /// ourselves so the cart always follows the local player at full speed.
        /// </summary>
        private static void DriveHeldCart(SimState st, PhysGrabber grabber)
        {
            if (grabber.physGrabForcesDisabledTimer > 0f) return; // vanilla steer is active

            // the game only steers HANDLE grabs — for body grabs ("weak" drag mode) the
            // host uses plain grab forces, and steering locally would desync us hard
            PhysGrabObjectGrabArea area = cartGrabArea(st.cart);
            if (area == null || !area.listOfAllGrabbers.Contains(grabber)) return;

            PlayerAvatar avatar = grabber.playerAvatar;
            if (avatar == null || PlayerController.instance == null) return;

            // no steering while the player stands inside the cart basket (vanilla rule)
            Transform inCart = cartInCart(st.cart);
            BoxCollider basket = inCart != null ? inCart.GetComponent<BoxCollider>() : null;
            Vector3 playerPos = avatar.transform.position + Vector3.up * 0.25f;
            if (basket != null && Vector3.Distance(basket.ClosestPoint(playerPos), playerPos) < 0.01f) return;

            grabber.OverridePhysGrabForcesDisable(0.1f);

            bool sprinting = avatarSprinting(avatar);
            float followMin = st.cart.isSmallCart ? 1.5f : 2f;
            float followMax = st.cart.isSmallCart ? 2f : 2.5f;
            float speedCap = sprinting ? 7f : 5f;
            bool movingForward = Vector3.Dot(PlayerController.instance.rb.velocity, st.pgo.transform.forward) > 0f;
            if (sprinting && movingForward)
            {
                followMin = 3f;
                followMax = 4f;
            }

            float t = Mathf.Clamp01(Vector3.Dot(st.rb.velocity, grabber.transform.forward) / speedCap);
            float followDist = Mathf.Lerp(followMin, followMax, t);
            Vector3 behind = grabber.transform.rotation * Vector3.back;
            Vector3 followPos = avatar.transform.position - behind * followDist;

            float pull = Mathf.Clamp01(Vector3.Distance(st.pgo.transform.position, followPos));
            Vector3 targetVel = Vector3.ClampMagnitude((followPos - st.pgo.transform.position).normalized * 5f * pull, 5f);
            float keepY = st.rb.velocity.y;
            Vector3 vel = Vector3.MoveTowards(st.rb.velocity, targetVel, pull * 2f);
            vel.y = keepY;
            st.rb.velocity = Vector3.ClampMagnitude(vel, 5f);

            // turn the cart to face away from the player, like the game's steering does
            Quaternion faceAway = Quaternion.Euler(0f, Quaternion.LookRotation(grabber.transform.position - st.pgo.transform.position, Vector3.up).eulerAngles.y + 180f, 0f);
            Quaternion flatRot = Quaternion.Euler(0f, st.rb.rotation.eulerAngles.y, 0f);
            (faceAway * Quaternion.Inverse(flatRot)).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            float turnRate = Mathf.Clamp(Mathf.Clamp(Mathf.Abs(angle) / 180f, 0.2f, 1f) * 20f, 0f, 4f);
            Vector3 targetAngVel = Vector3.ClampMagnitude(Mathf.Deg2Rad * angle * axis.normalized * turnRate, 4f);
            st.rb.angularVelocity = Vector3.ClampMagnitude(
                Vector3.MoveTowards(st.rb.angularVelocity, targetAngVel, turnRate), 4f);
        }

        /// <summary>Nobody local holds this object: run it in local physics, but blend it
        /// continuously toward the host's streamed state so desync can never accumulate.</summary>
        private static void TickShadow(SimState st)
        {
            pgoIsMaster(st.pgo) = false;

            // deactivated objects sit kinematic in limbo (extracted/destroyed) — hands off
            if (!pgoIsActive(st.pgo)) return;

            // cargo riding a cart the local player drags: pure local physics, no blending
            if (st.ridingTick == tickCounter)
            {
                if (st.rb.isKinematic) st.rb.isKinematic = false;
                st.hostTeleport = false;
                return;
            }

            if (!st.hasHostState) return;

            // world-wide packet silence (host stalled/leaving): blending toward the frozen
            // last-known state pins every object the player bumps — pure local physics
            // until data flows again
            if (HostStalled) return;

            if (st.postThrowTimer > 0f)
            {
                st.postThrowTimer -= Time.fixedDeltaTime;
                if (st.postThrowTimer <= 0f)
                {
                    st.postThrowRamp = 0.4f; // hand control back gradually, not with a brake
                }
            }
            else if (st.postThrowRamp > 0f)
            {
                st.postThrowRamp -= Time.fixedDeltaTime;
            }

            if (st.hostTeleport)
            {
                st.hostTeleport = false;
                Snap(st, "host teleported it");
                return;
            }

            // A fresh local throw: the host still thinks the object is in our hand, so
            // ANY correction drags the flight backward toward stale data ("moves so
            // weirdly"). Pure local physics for the grace period (config PostThrowGrace);
            // the regular glide reconciles whatever small difference remains afterwards.
            if (st.postThrowTimer > 0f) return;

            // Photon only sends packets when an object's data CHANGES. Silence therefore
            // means "the host's copy is exactly at hostPos, at rest" — treating stale
            // velocities as live is what made shadowed objects vibrate and drift.
            float packetAge = Time.unscaledTime - st.lastPacketTime;
            bool hostIdle = packetAge > 0.35f;
            // while packets flow, lead the target by the data's age so movement is
            // continuous between packets instead of sawtoothing toward stale positions
            // lead by data age PLUS one-way ping: the host's copy systematically trails
            // a moving object by the ping, and following the un-led position is what
            // braked fast throws mid-air ("catching its breath")
            Vector3 targetPos = hostIdle
                ? st.hostPos
                : st.hostPos + st.hostVel * Mathf.Min(packetAge + oneWayPing, 0.35f);
            Vector3 targetVel = hostIdle ? Vector3.zero : st.hostVel;
            Vector3 targetAngVel = hostIdle ? Vector3.zero : st.hostAngVel;

            if (st.hostKinematic && !st.isHinge)
            {
                if (!st.rb.isKinematic) st.rb.isKinematic = true;
                st.rb.MovePosition(targetPos);
                st.rb.MoveRotation(st.hostRot);
                return;
            }
            if (st.rb.isKinematic) st.rb.isKinematic = false;

            // doors/cabinets: anchored by their local joint and driven by the game's own
            // (locally running) hinge logic — we only nudge the swing angle toward the host
            if (st.isHinge)
            {
                CheckHingeBroken(st);
                // while the local player (or something they hold/push) is shoving this door,
                // the host's copy is still closed — syncing toward it fights the push
                // ("delayed and buggy, eventually opens"); go fully local briefly instead
                if (st.localPushTimer > 0f)
                {
                    st.localPushTimer -= Time.fixedDeltaTime;
                    return;
                }
                // NetworkingReworked's door model: hinges run the game's own logic fully
                // locally, and continuous rotation sync only FIGHTS it ("opening stuff is
                // delayed"). A door in local motion belongs to local physics; we only
                // follow the host when ITS copy is moving while ours rests (another player
                // using the door), and gently reconcile long-idle disagreement.
                if (st.rb.angularVelocity.sqrMagnitude > 0.25f) return;
                float hingeAngle = Quaternion.Angle(st.rb.rotation, st.hostRot);
                if (!hostIdle)
                {
                    if (hingeAngle > 0.5f)
                    {
                        float ha = Mathf.Clamp01(Plugin.PassiveSyncStrength.Value);
                        st.rb.MoveRotation(Quaternion.Slerp(st.rb.rotation, st.hostRot, ha));
                        st.rb.angularVelocity = Vector3.Lerp(st.rb.angularVelocity, targetAngVel, ha);
                    }
                }
                else if (hingeAngle > 10f)
                {
                    st.rb.MoveRotation(Quaternion.Slerp(st.rb.rotation, st.hostRot, 0.05f));
                }
                return;
            }

            if (st.hostSleeping || hostIdle)
            {
                if (st.rb.IsSleeping()) return; // both at rest — leave it alone (cheap)
                if ((st.rb.position - st.hostPos).sqrMagnitude < 0.0025f
                    && Quaternion.Angle(st.rb.rotation, st.hostRot) < 3f)
                {
                    st.rb.velocity = Vector3.zero;
                    st.rb.angularVelocity = Vector3.zero;
                    st.rb.position = st.hostPos;
                    st.rb.rotation = st.hostRot;
                    if (st.hostSleeping) st.rb.Sleep();
                    return;
                }
                // far from the host's rest pose: fall through and blend toward it
            }

            float dist = Vector3.Distance(st.rb.position, targetPos);
            if (dist > Plugin.SnapDistance.Value && st.postThrowRamp <= 0f)
            {
                Snap(st, "beyond snap distance");
                return;
            }

            // wedged on geometry: blending grinds against the wall forever, snap it free
            if (dist > 1.5f && st.rb.velocity.sqrMagnitude < 1f && st.postThrowTimer <= 0f)
            {
                st.stuckTimer += Time.fixedDeltaTime;
                if (st.stuckTimer > 1.2f)
                {
                    st.stuckTimer = 0f;
                    Snap(st, "wedged in geometry, freeing to host position");
                    return;
                }
            }
            else
            {
                st.stuckTimer = 0f;
            }

            float a = Mathf.Clamp01(Plugin.PassiveSyncStrength.Value);
            const float velBlend = 0.5f;

            // Correct through VELOCITY, not per-tick position writes: MovePosition on a
            // dynamic body fights its own physics integration 50x/s and renders as
            // vibration ("phone on max"). Folding the position error into the velocity
            // target lets the body glide to the host state and keeps rigidbody
            // interpolation smooth.
            Vector3 posErr = targetPos - st.rb.position;
            float errMag = posErr.magnitude;
            if (errMag > 0.02f) // deadband: don't chase packet noise at equilibrium
            {
                // error-scaled pull: gentle when nearly in place, firm when far — the
                // object glides to the host's state quickly but is never position-forced
                // (position forcing was the "jiggles its way over" look; slow flat gain
                // was the "and it's kinda far off" trail).
                // After a local throw the correction fades in from zero: the object first
                // FOLLOWS the host's velocity, then converges — no mid-air brake.
                float corrScale = 1f - Mathf.Clamp01(st.postThrowRamp / 0.4f);
                float gain = a * 25f * (1f + errMag * 2f) * corrScale;
                Vector3 desiredVel = targetVel + Vector3.ClampMagnitude(posErr * gain, 10f);
                st.rb.velocity = Vector3.Lerp(st.rb.velocity, desiredVel, velBlend);
            }
            else
            {
                st.rb.velocity = Vector3.Lerp(st.rb.velocity, targetVel, velBlend);
            }

            // rotation gets a small deadband too, so settled objects never shimmer
            if (Quaternion.Angle(st.rb.rotation, st.hostRot) > 1f)
            {
                st.rb.MoveRotation(Quaternion.Slerp(st.rb.rotation, st.hostRot, a));
                st.rb.angularVelocity = Vector3.Lerp(st.rb.angularVelocity, targetAngVel, a);
            }
        }

        /// <summary>Called from the hinge collision hook: if the local player or something
        /// they control is physically pushing this door, let it go fully local briefly.</summary>
        internal static void NotifyHingePushed(PhysGrabHinge hinge, Collision collision)
        {
            if (hinge == null || collision == null) return;
            PhysGrabObject pgo = hinge.GetComponent<PhysGrabObject>();
            if (pgo == null || !states.TryGetValue(pgo, out SimState st) || !st.isHinge) return;

            bool localPush = false;
            if (collision.gameObject.CompareTag("Player") && PlayerController.instance != null
                && Vector3.Distance(collision.transform.position, PlayerController.instance.transform.position) < 3f)
            {
                localPush = true; // the local player's body (remote avatars are further away)
            }
            else if (collision.rigidbody != null)
            {
                PhysGrabObject otherPgo = collision.rigidbody.GetComponent<PhysGrabObject>();
                if (otherPgo != null && states.TryGetValue(otherPgo, out SimState os)
                    && (os.localGrab || os.ridingTick >= tickCounter - 2))
                {
                    localPush = true; // a locally-held object or cargo of a locally-held cart
                }
            }
            if (localPush)
            {
                st.localPushTimer = 0.75f;
            }
        }

        /// <summary>The host decided a hinge broke — mirror it by dropping our local joint.</summary>
        private static void CheckHingeBroken(SimState st)
        {
            if (!st.isHinge || st.hinge == null) return;
            if (!hingeBroken(st.hinge)) return;
            if (st.joint != null)
            {
                UnityEngine.Object.Destroy(st.joint);
                st.joint = null;
            }
            st.isHinge = false; // free object from now on: normal shadowing rules apply
            if (Plugin.VerboseLogging.Value)
            {
                Plugin.Log.LogInfo($"[hinge] {st.pgo.name} broke on the host — local joint dropped");
            }
        }

        /// <summary>
        /// The host's copy is stuck or far away — stop pretending, give the object back to the
        /// host mid-hold. The player keeps holding it vanilla-style (host authoritative) and
        /// gets local grab authority again on their next grab.
        /// </summary>
        private static void ForceHandback(SimState st)
        {
            st.localGrab = false;
            st.desyncTimer = 0f;
            st.stuckTimer = 0f;
            st.postThrowTimer = 0f;
            pgoIsMaster(st.pgo) = false;
            handedBack.Add(st.pgo);

            if (Plugin.VerboseLogging.Value)
            {
                Plugin.Log.LogInfo($"Handed {st.pgo.name} back to the host (drifted too far from its copy).");
            }
        }

        private static void SweepHandedBack()
        {
            if (handedBack.Count == 0) return;
            handedBackSweep.Clear();
            foreach (PhysGrabObject pgo in handedBack)
            {
                if (pgo == null)
                {
                    handedBackSweep.Add(pgo);
                    continue;
                }
                bool stillHeldLocally = false;
                List<PhysGrabber> grabbing = pgo.playerGrabbing;
                for (int i = 0; i < grabbing.Count; i++)
                {
                    PhysGrabber g = grabbing[i];
                    if (g != null && g.isLocal && g.grabbed)
                    {
                        stillHeldLocally = true;
                        break;
                    }
                }
                if (!stillHeldLocally) handedBackSweep.Add(pgo);
            }
            foreach (PhysGrabObject pgo in handedBackSweep) handedBack.Remove(pgo);
        }

        private static PhysGrabber GetLocalGrabber(SimState st)
        {
            List<PhysGrabber> grabbing = st.pgo.playerGrabbing;
            for (int i = 0; i < grabbing.Count; i++)
            {
                PhysGrabber g = grabbing[i];
                if (g != null && g.isLocal && g.grabbed && grabberObject(g) == st.pgo)
                {
                    return g;
                }
            }
            return null;
        }

        private static void Snap(SimState st, string reason)
        {
            if (Plugin.VerboseLogging.Value)
            {
                float dist = Vector3.Distance(st.rb.position, st.hostPos);
                Plugin.Log.LogInfo($"[snap] {st.pgo.name}: {reason} (dist={dist:F1}m)");
            }
            st.rb.position = st.hostPos;
            st.rb.rotation = st.hostRot;
            if (!st.rb.isKinematic)
            {
                st.rb.velocity = st.hostVel;
                st.rb.angularVelocity = st.hostAngVel;
            }
        }

        /// <summary>Hand control back to PhotonTransformView without a visible snap.</summary>
        private static void Restore(SimState st)
        {
            try
            {
                if (st.pgo != null)
                {
                    pgoIsMaster(st.pgo) = false;
                }
                if (st.ptv != null && st.rb != null)
                {
                    Vector3 targetPos = st.hasHostState ? st.hostPos : st.rb.position;
                    Quaternion targetRot = st.hasHostState ? st.hostRot : st.rb.rotation;

                    ptvNetPos(st.ptv) = targetPos;
                    ptvNetRot(st.ptv) = targetRot;
                    ptvStoredPos(st.ptv) = targetPos;
                    ptvRecvPos(st.ptv) = targetPos;
                    ptvRecvRot(st.ptv) = targetRot;
                    ptvPrevPos(st.ptv) = targetPos;
                    ptvPrevRot(st.ptv) = targetRot;
                    ptvSmoothedPos(st.ptv) = st.rb.position;
                    ptvSmoothedRot(st.ptv) = st.rb.rotation;
                    ptvDistance(st.ptv) = Vector3.Distance(st.rb.position, targetPos);
                    ptvAngle(st.ptv) = Quaternion.Angle(st.rb.rotation, targetRot);
                    ptvRecvVel(st.ptv) = st.hasHostState ? st.hostVel : st.rb.velocity;
                    ptvRecvAngVel(st.ptv) = st.hasHostState ? st.hostAngVel : st.rb.angularVelocity;
                    ptvFirstTake(st.ptv) = false;
                    ptvTeleport(st.ptv) = false;
                    ptvIsSleeping(st.ptv) = st.hasHostState && st.hostSleeping;

                    if (st.hasHostState)
                    {
                        st.rb.isKinematic = st.hostKinematic;
                    }
                }
            }
            catch (Exception e)
            {
                if (Plugin.VerboseLogging.Value) Plugin.Log.LogWarning($"Restore failed: {e.Message}");
            }
            finally
            {
                HardRemove(st);
            }
        }

        private static void HardRemove(SimState st)
        {
            if (st.pgo != null) states.Remove(st.pgo);
            if (st.ptv != null) byPtv.Remove(st.ptv);
            if (st.viewId != 0) byViewId.Remove(st.viewId);

            // dictionaries can hold destroyed-object keys; sweep those out
            if (st.pgo == null || st.ptv == null)
            {
                SweepDead();
            }
        }

        private static readonly List<PhysGrabObject> deadPgos = new List<PhysGrabObject>();
        private static readonly List<PhotonTransformView> deadPtvs = new List<PhotonTransformView>();
        private static readonly List<int> deadIds = new List<int>();

        private static void SweepDead()
        {
            deadPgos.Clear();
            deadPtvs.Clear();
            deadIds.Clear();
            foreach (KeyValuePair<PhysGrabObject, SimState> kv in states)
            {
                if (kv.Key == null) deadPgos.Add(kv.Key);
            }
            foreach (KeyValuePair<PhotonTransformView, SimState> kv in byPtv)
            {
                if (kv.Key == null) deadPtvs.Add(kv.Key);
            }
            foreach (KeyValuePair<int, SimState> kv in byViewId)
            {
                if (kv.Value.pgo == null) deadIds.Add(kv.Key);
            }
            foreach (PhysGrabObject k in deadPgos) states.Remove(k);
            foreach (PhotonTransformView k in deadPtvs) byPtv.Remove(k);
            foreach (int k in deadIds) byViewId.Remove(k);
        }

        internal static void RestoreAll()
        {
            if (Plugin.VerboseLogging.Value && states.Count > 0)
            {
                Plugin.Log.LogInfo($"[mode] restoring {states.Count} objects to vanilla sync");
            }
            tickBuffer.Clear();
            tickBuffer.AddRange(states.Values);
            foreach (SimState st in tickBuffer)
            {
                Restore(st);
            }
            states.Clear();
            byPtv.Clear();
            byViewId.Clear();
            handedBack.Clear();
        }

        internal static int RegisteredCount => states.Count;

        internal static int HeldCount
        {
            get
            {
                int n = 0;
                foreach (SimState st in states.Values)
                {
                    if (st.localGrab) n++;
                }
                return n;
            }
        }
    }

    /// <summary>
    /// Per-frame upkeep, driven by the Plugin component itself (BepInEx guarantees it
    /// persists — a separate DontDestroyOnLoad GameObject created during chainloading
    /// silently dies with the first scene load and its Update never runs).
    /// </summary>
    internal static class SimDriver
    {
        private static bool announced;
        private static bool conflictsChecked;
        private static float nextRegisterSweep;
        private static float nextStatsLog;
        private static int lastPacketCount;
        private static int lastFrame = -1;

        internal static void FrameUpdate()
        {
            // driven from both a Harmony hook and the plugin component — once per frame
            if (Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount;

            if (!announced)
            {
                announced = true;
                Plugin.Log.LogInfo("[driver] alive — update loop running");
            }

            Plugin.EnsureCapturePatch();

            if (!conflictsChecked && Time.unscaledTime > 10f)
            {
                conflictsChecked = true;
                string[] conflicts =
                {
                    "net.ovchinikov.nwrework", "BlueAmulet.REPONetworkTweaks",
                    "com.Revival.networkingrevived", "com.Revival.networktweaksrevived",
                };
                foreach (string guid in conflicts)
                {
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(guid))
                    {
                        Plugin.Log.LogWarning($"Conflicting mod '{guid}' is still installed! RevivalSync replaces it — " +
                                              "please uninstall/disable the old mod.");
                    }
                }
            }

            // catches objects that existed before we joined (late join, level already loaded)
            if (Time.unscaledTime >= nextRegisterSweep)
            {
                nextRegisterSweep = Time.unscaledTime + 5f;
                if (SimManager.Ready && SimManager.IsClientInLobby())
                {
                    foreach (PhysGrabObject pgo in UnityEngine.Object.FindObjectsOfType<PhysGrabObject>())
                    {
                        SimManager.TryRegister(pgo);
                    }
                }
            }

            if (Plugin.VerboseLogging.Value && Time.unscaledTime >= nextStatsLog)
            {
                nextStatsLog = Time.unscaledTime + 10f;
                if (SimManager.IsClientInLobby())
                {
                    int packets = SimManager.PacketsCaptured;
                    Plugin.Log.LogInfo($"[stats] registered={SimManager.RegisteredCount} held={SimManager.HeldCount} " +
                                       $"packets={packets} (+{packets - lastPacketCount} in 10s)");
                    lastPacketCount = packets;
                }
            }

            Plugin.ApplyPhotonSettings();
            SimManager.MirrorHeldOrientationTargets();
            Smoothing.Sweep();
        }
    }
}
