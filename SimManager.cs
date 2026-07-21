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
            public float noSnapTimer;       // recently thrown: landings glide to the host spot, never teleport
            public float hingeSyncPause;    // door we just pushed/released: settle window before host truth resumes
            public bool droneExempt;        // magnet drone drives this object: vanilla sync until released
            public float farTimer;          // shadowed object stuck far from host: convergence backstop
            public ItemDrone droneSelf;     // this object IS a drone: host-owned only while flying
            public ItemToggle droneToggle;  // (toggled on = deployed = it moves itself)
            public float desyncTimer;       // how long we've been far from the host's copy while holding
            public float stuckTimer;        // how long we've failed to converge (object wedged in geometry)
            public float debugTimer;        // rate limit for verbose diagnostics
            public int ridingTick;          // == current tick when sitting inside a locally-held cart
            public SimState ridingCart;     // the cart it rides
            public bool wasRiding;          // rode a cart last tick: schedule a gentle staged return
            public float rideHoldTimer;     // carts: keep marking riders briefly after use ends
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

        private static Vector3 disabledPos = new Vector3(0f, 3000f, 0f);
        private static bool disabledPosRead;

        /// <summary>Where the game parks deactivated/pooled objects (far above the map).
        /// The host streams that position like any other, and blending toward it launches
        /// our copy off the level — hence "objects aren't where they should be".</summary>
        private static Vector3 DisabledPosition()
        {
            if (!disabledPosRead)
            {
                try
                {
                    if (AssetManager.instance != null)
                    {
                        var f = AccessTools.Field(typeof(AssetManager), "physDisabledPosition");
                        if (f != null)
                        {
                            disabledPos = (Vector3)f.GetValue(AssetManager.instance);
                            disabledPosRead = true;
                        }
                    }
                }
                catch { disabledPosRead = true; } // keep the known default
            }
            return disabledPos;
        }

        // ---- reflection accessors into game internals ----
        private static AccessTools.FieldRef<PhysGrabObject, bool> pgoIsMaster;
        private static AccessTools.FieldRef<ItemMelee, Quaternion> meleeYRot;
        private static AccessTools.FieldRef<PhysGrabObject, bool> pgoIsActive;
        internal static AccessTools.FieldRef<PhysGrabObject, bool> pgoHeldByLocal;
        internal static AccessTools.FieldRef<ItemToggle, int> togglePlayerId;
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
                pgoHeldByLocal = AccessTools.FieldRefAccess<PhysGrabObject, bool>("heldByLocalPlayer");
                togglePlayerId = AccessTools.FieldRefAccess<ItemToggle, int>("playerTogglePhotonID");
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
            return ptv != null && byPtv.TryGetValue(ptv, out SimState st) && !st.droneExempt;
        }

        internal static bool IsRegistered(PhotonTransformView ptv)
        {
            return ptv != null && byPtv.TryGetValue(ptv, out SimState st) && !st.droneExempt;
        }

        internal static bool IsLocalGrab(PhysGrabObject pgo)
        {
            return pgo != null && states.TryGetValue(pgo, out SimState st) && st.localGrab;
        }

        /// <summary>True for every object we shadow locally — used to open the game's
        /// master-only physics paths (cart stabilization, velocity clamps) on the client.</summary>
        internal static bool HasPhysicsAuthority(PhysGrabObject pgo)
        {
            return pgo != null && states.TryGetValue(pgo, out SimState st) && !st.droneExempt;
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
            // Upgrade orbs are never simulated, no matter what. REPO decides who PERMANENTLY
            // receives an upgrade purely client-side: ItemToggle fires on whichever client's
            // own copy reads heldByLocalPlayer when Interact is pressed, and that client's
            // player id rides the RPC unchallenged. Our instant grab makes that flag true
            // locally with no host round trip, so on a contested orb (two players grabbing
            // at it — exactly what passing one over looks like) the modded client wins the
            // race and takes the upgrade. Nothing about holding an orb needs instant
            // physics, so we stay out of it entirely and let vanilla decide.
            if (o.GetComponentInParent<ItemUpgrade>() != null) return false;
            if (o.GetComponentInParent<ItemAttributes>() != null)
            {
                foreach (Component c in o.GetComponentsInParent<Component>(true))
                {
                    if (c == null) continue;
                    // drones are only host-owned while FLYING (dynamic exemption in
                    // Tick) — a carried drone is just an item and deserves instant feel
                    if (c.GetType().Name.StartsWith("ItemRubberDuck"))
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
                    // NR stored the streamed position as-is (it read the direction slot and
                    // ignored it). Extrapolating by clock lag makes the blend target jump
                    // around between packets, which every "vibrating / jittery" report has
                    // traced back to. Keep it exactly where the host said it was.
                    st.hostPos = pos;
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
            // client-side simulation moves objects with real velocities — continuous
            // collision detection keeps fast corrections and hard throws from tunneling
            // through floors (the host never sees it because its copy runs vanilla)
            if (st.rb.collisionDetectionMode == CollisionDetectionMode.Discrete)
            {
                st.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
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
                st.droneSelf = pgo.GetComponentInChildren<ItemDrone>(true);
                if (st.droneSelf != null)
                {
                    st.droneToggle = pgo.GetComponentInChildren<ItemToggle>(true);
                }
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
            if (!st.isHinge)
            {
                st.noSnapTimer = 3f; // bounces diverge between machines: glide, don't teleport
            }
            else
            {
                st.hingeSyncPause = 1.5f; // let the door you just swung finish before host truth resumes
            }
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

            // covers the unseen-release path (forced drop, knocked out of hand by hitting
            // a player): GrabEnded never ran, so the entry our grab patch added is still
            // in playerGrabbing and this object still reads "held by local player"
            ScrubStaleLocalGrabber(pgo);

            if (Plugin.VerboseLogging.Value)
            {
                Plugin.Log.LogInfo($"Released grab authority: {pgo.name}");
            }
        }

        /// <summary>The upgrade-theft guard. A stale LOCAL entry in playerGrabbing makes
        /// heldByLocalPlayer stick, and ItemToggle.Update then toggles that item on every
        /// local E press with OUR photon id — upgrades popped in other players' hands and
        /// the host credited us. The vanilla janitor (PhysGrabObject.Update) only removes
        /// entries whose grabber's GLOBAL grabbed flag is false, so the stale entry
        /// survives as long as we are holding anything else. We put the entry there
        /// (instant grab), so we take it out.</summary>
        private static void ScrubStaleLocalGrabber(PhysGrabObject pgo)
        {
            if (pgo == null) return;
            List<PhysGrabber> grabbing = pgo.playerGrabbing;
            for (int i = grabbing.Count - 1; i >= 0; i--)
            {
                PhysGrabber g = grabbing[i];
                if (g == null || !g.isLocal) continue;
                if (g.grabbed && grabberObject(g) == pgo) continue;
                grabbing.RemoveAt(i);
                if (Plugin.VerboseLogging.Value)
                {
                    Plugin.Log.LogInfo($"[stale-grab] {pgo.name}: removed stale local grabber entry (object read as held-by-us)");
                }
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
        private static int cargoStagger;

        // ---- magnet drones: the drone provides the motion, so the host owns the target ----
        private static AccessTools.FieldRef<ItemDrone, bool> droneMagnetActive;
        private static AccessTools.FieldRef<ItemDrone, PhysGrabObject> droneMagnetTarget;
        private static bool droneAccessorsTried;
        internal static ItemDrone[] cachedDrones;
        private static readonly Dictionary<PhysGrabObject, ItemDrone> droneTargetMap =
            new Dictionary<PhysGrabObject, ItemDrone>();

        internal static void EnsureDroneAccessors()
        {
            if (droneAccessorsTried) return;
            droneAccessorsTried = true;
            try
            {
                droneMagnetActive = AccessTools.FieldRefAccess<ItemDrone, bool>("magnetActive");
                droneMagnetTarget = AccessTools.FieldRefAccess<ItemDrone, PhysGrabObject>("magnetTargetPhysGrabObject");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Drone handoff disabled (game internals changed): {e.Message}");
            }
        }

        private static void RebuildDroneTargets()
        {
            droneTargetMap.Clear();
            if (droneMagnetActive == null || cachedDrones == null) return;
            foreach (ItemDrone d in cachedDrones)
            {
                if (d == null || !droneMagnetActive(d)) continue;
                PhysGrabObject t = droneMagnetTarget(d);
                if (t != null) droneTargetMap[t] = d;
            }
        }
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
            RebuildDroneTargets();

            tickBuffer.Clear();
            tickBuffer.AddRange(states.Values);

            // cargo inside a cart the local player is dragging must ride the cart in pure
            // local physics — blending it toward the host's (lagging) cargo positions drags
            // the cart backwards like an anchor and slams loot through the cart walls
            foreach (SimState st in tickBuffer)
            {
                if (st.cart == null || st.pgo == null || st.rb == null) continue;
                // NetworkingReworked's cargo rule (broader than ours was): cargo rides in
                // pure local physics whenever the cart is in use by ANYONE or still
                // rolling — blending cargo toward 10Hz host positions inside a smoothly
                // moving cart is the "loot vibrates in the cart" rattle
                bool cartInUse = st.localGrab
                    || st.pgo.playerGrabbing.Count > 0
                    || st.rb.velocity.sqrMagnitude > 0.25f;
                // hysteresis: riding must not flap with stop-start pushing — every
                // riding<->synced transition jostles the load ("loot slides off")
                if (cartInUse)
                {
                    st.rideHoldTimer = 1.5f;
                }
                else if (st.rideHoldTimer > 0f)
                {
                    st.rideHoldTimer -= Time.fixedDeltaTime;
                }
                else
                {
                    continue;
                }
                List<PhysGrabObject> items = cartItems(st.cart);
                if (items == null) continue;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] != null && states.TryGetValue(items[i], out SimState rider))
                    {
                        rider.ridingTick = tickCounter;
                        rider.ridingCart = st;
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

                // "ownership follows whoever provides the motion": a magnet drone drives
                // this object, so the host owns it — UNLESS the local player is also
                // holding it (that is the feather drone's whole purpose: helping a player
                // carry heavy loot). The player's grab always wins; 1.2.7 exempted held
                // objects too and the fight caused an infinite exempt/re-register flap.
                bool droneOnly = !st.localGrab
                    && ((droneTargetMap.Count > 0 && droneTargetMap.ContainsKey(st.pgo))
                        || (st.droneSelf != null && st.droneToggle != null && st.droneToggle.toggleState));
                if (droneOnly != st.droneExempt)
                {
                    st.droneExempt = droneOnly;
                    if (droneOnly)
                    {
                        pgoIsMaster(st.pgo) = false;
                        // hand to vanilla WITHOUT unregistering (Restore() fully removes
                        // the state — that removal was the 969-cycle flap in one session)
                        SeedPtvFields(st);
                        if (Plugin.VerboseLogging.Value)
                        {
                            Plugin.Log.LogInfo($"[drone] {st.pgo.name}: magnet-grabbed — host drives it until released");
                        }
                    }
                }
                if (st.droneExempt) continue;

                if (st.localGrab)
                {
                    TickHeld(st);
                }
                else
                {
                    // safety net for any leftover path that strands our instant-grab
                    // entry — a genuine local grab always has localGrab set by the
                    // GrabStarted postfix before this tick can run
                    ScrubStaleLocalGrabber(st.pgo);
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

            // a magnet drone is helping carry this held object: replicate the feather
            // physics the host applies to its copy (ItemDroneFeather's target values) —
            // otherwise we simulate it at full weight and it feels immovable
            if (droneTargetMap.Count > 0 && droneTargetMap.TryGetValue(st.pgo, out ItemDrone helper)
                && helper != null && helper.GetComponent<ItemDroneFeather>() != null)
            {
                st.pgo.OverrideMass(0.5f);
                st.pgo.OverrideDrag(1f);
                st.pgo.OverrideAngularDrag(5f);
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

            // HELD OBJECTS — NetworkingReworked's rule: while you are holding something it
            // is yours completely, and the network does not touch it. Its passive sync
            // simply skipped anything locally grabbed. Everything we added on top of that
            // misfired in real lobbies: the drift nudge fought the natural ping-trail and
            // became a brake (the "ultra slow cart"), and the wedge-snap mistook a paused
            // cart's normal trail for a stuck object and teleported it out of your hands.
            // Both are gone. What remains is one genuine last resort — if the host's copy
            // is hopelessly far away for over a second, it is snagged on something and
            // holding on achieves nothing, so give it back.
            float speed = st.rb.velocity.magnitude;
            float drift = Vector3.Distance(st.rb.position, st.hostPos);
            float handbackAt = Plugin.HeldDriftHandbackAt.Value + speed * 0.4f;
            if (st.cart != null) handbackAt *= 1.5f;
            if (drift > handbackAt)
            {
                st.desyncTimer += Time.fixedDeltaTime;
                if (st.desyncTimer > (st.cart != null ? 1.2f : 0.6f))
                {
                    ForceHandback(st);
                    return;
                }
            }
            else
            {
                st.desyncTimer = 0f;
            }
            st.stuckTimer = 0f;

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

            // CART CARGO — the original NetworkingReworked's model (credit: readthisifbad).
            // While the cart is in use, cargo gets NO sync of any kind: it simply rides in
            // local physics, in a local basket, with local collisions. Every attempt to
            // correct it mid-haul has failed differently — blending rattles it, world-space
            // pulls shove it out (the host's cart trails by the ping), frame-relative pulls
            // fight the basket's own collisions. The basket already holds the loot; the
            // network does not need to. Divergence is reconciled once the haul ends.
            if (st.ridingTick == tickCounter)
            {
                if (st.rb.isKinematic) st.rb.isKinematic = false;
                st.hostTeleport = false;
                st.wasRiding = true;
                return;
            }
            if (st.wasRiding)
            {
                // haul over: NR re-synced the cart first, then its cargo staggered a few
                // hundredths apart, so the load settles item by item instead of the whole
                // pile jerking at once. Our correction ramp does the same job smoothly.
                st.wasRiding = false;
                st.ridingCart = null;
                st.postThrowRamp = 0.5f + (cargoStagger++ & 7) * 0.05f;
            }

            if (!st.hasHostState) return;

            // world-wide packet silence (host stalled/leaving): blending toward the frozen
            // last-known state pins every object the player bumps — pure local physics
            // until data flows again
            if (HostStalled) return;

            // the host has this object pooled/deactivated (parked off-map): syncing to it
            // would fling our copy into the sky and then snap it back, over and over
            if ((st.hostPos - DisabledPosition()).sqrMagnitude < 25f) return;

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
            if (st.noSnapTimer > 0f)
            {
                st.noSnapTimer -= Time.fixedDeltaTime;
            }

            if (st.hostTeleport)
            {
                st.hostTeleport = false;
                // Some objects (death heads especially) get the teleport flag set by the
                // host constantly. When our copy is already there it is a pointless
                // physics write and thousands of log lines — one session logged 3710 of
                // them for a single corpse head. Only act on real teleports.
                if ((st.rb.position - st.hostPos).sqrMagnitude > 0.01f)
                {
                    Snap(st, "host teleported it");
                    return;
                }
            }

            // A fresh local throw: the host still thinks the object is in our hand, so
            // ANY correction drags the flight backward toward stale data ("moves so
            // weirdly"). Pure local physics for the grace period (config PostThrowGrace);
            // the regular glide reconciles whatever small difference remains afterwards.
            if (st.postThrowTimer > 0f) return;

            // the host holds it kinematic (another player carrying it, a system owning it):
            // follow its state exactly, no physics of our own
            if (st.hostKinematic && !st.isHinge)
            {
                if (!st.rb.isKinematic) st.rb.isKinematic = true;
                st.rb.MovePosition(st.hostPos);
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
                    st.hingeSyncPause = 1f; // and let it settle after the push ends
                    return;
                }
                if (st.hingeSyncPause > 0f)
                {
                    st.hingeSyncPause -= Time.fixedDeltaTime;
                    // still swinging from OUR interaction: local physics finishes the swing
                    if (st.rb.angularVelocity.sqrMagnitude > 0.25f) return;
                }
                // otherwise the host's door angle is continuously authoritative. The
                // game's own auto-close logic runs locally too — an unsynced "local
                // motion owns the door" rule let that spring quietly fight host truth
                // (open cupboards self-closing = desync). Only OUR interactions above
                // may interrupt the sync, never the door's own spring.
                // joint-friendly host authority: steer ANGULAR VELOCITY to close the
                // angle gap. MoveRotation forcing fought the hinge joint solver and the
                // local closing spring 50x/s — that was the door jitter. The deadband
                // keeps agreement calm; the joint itself keeps the swing on its arc.
                (st.hostRot * Quaternion.Inverse(st.rb.rotation)).ToAngleAxis(out float hAng, out Vector3 hAxis);
                if (hAng > 180f) hAng -= 360f;
                if (Mathf.Abs(hAng) > 1.5f && !float.IsInfinity(hAxis.x))
                {
                    Vector3 targetW = Vector3.ClampMagnitude(Mathf.Deg2Rad * hAng * hAxis.normalized * 4f, 6f);
                    st.rb.angularVelocity = Vector3.Lerp(st.rb.angularVelocity, targetW, 0.35f);
                }
                return;
            }

            // both copies at rest and already agreeing: leave it alone so it can sleep
            if (st.hostSleeping
                && (st.rb.position - st.hostPos).sqrMagnitude < 0.0025f
                && Quaternion.Angle(st.rb.rotation, st.hostRot) < 3f)
            {
                if (!st.rb.IsSleeping())
                {
                    st.rb.velocity = Vector3.zero;
                    st.rb.angularVelocity = Vector3.zero;
                    st.rb.Sleep();
                }
                return;
            }
            // asleep somewhere the host does not have it: it would rest there forever,
            // immune to the blend below — wake it so it can be brought home
            if (st.rb.IsSleeping()) st.rb.WakeUp();

            float dist = Vector3.Distance(st.rb.position, st.hostPos);
            if (dist > Plugin.SnapDistance.Value && st.postThrowRamp <= 0f && st.noSnapTimer <= 0f)
            {
                Snap(st, "beyond snap distance");
                return;
            }

            // convergence backstop: if the blend hasn't brought an object home after five
            // seconds (wedged behind geometry, bad luck), teleport it. The promise is that
            // loot always ends up where the host sees it — and "home" includes ROTATION:
            // a cart in the right spot at the wrong angle is still desynced ("synced but
            // 90 degrees a different way").
            float rotErr = Quaternion.Angle(st.rb.rotation, st.hostRot);
            if (dist > 1.5f || rotErr > 30f)
            {
                st.farTimer += Time.fixedDeltaTime;
                if (st.farTimer > 5f && st.noSnapTimer <= 0f && st.postThrowRamp <= 0f)
                {
                    st.farTimer = 0f;
                    Snap(st, "stuck away from host position too long");
                    return;
                }
            }
            else if (dist < 0.75f && rotErr < 10f)
            {
                st.farTimer = 0f;
            }

            // ---- passive sync: the original NetworkingReworked's, kept as it was ----
            // Position, rotation, velocity and angular velocity are each eased toward the
            // host's last streamed state every physics tick. That is the whole algorithm.
            // Everything we layered on top of it through 1.2.x — extrapolating the target
            // by ping, idle detection, deadbands, velocity steering — each fixed one
            // symptom and caused another, because they all made the target MOVE between
            // packets. A still target converges smoothly and cannot fight the physics.
            float a = Mathf.Clamp01(Plugin.PassiveSyncStrength.Value);
            if (st.postThrowRamp > 0f)
            {
                // ease back in after a throw or a cart unload instead of grabbing hold
                a *= 1f - Mathf.Clamp01(st.postThrowRamp / 0.5f);
            }
            st.rb.position = Vector3.Lerp(st.rb.position, st.hostPos, a);
            st.rb.rotation = Quaternion.Slerp(st.rb.rotation, st.hostRot, a);
            st.rb.velocity = Vector3.Lerp(st.rb.velocity, st.hostVel, a);
            st.rb.angularVelocity = Vector3.Lerp(st.rb.angularVelocity, st.hostAngVel, a);
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

        /// <summary>Diagnostic: exactly what our copy believes about who holds an object.
        /// Upgrade attribution hangs on heldByLocalPlayer, so this line in a session log
        /// settles who claimed an upgrade and why.</summary>
        internal static string DescribeGrabState(PhysGrabObject pgo)
        {
            if (pgo == null) return "(no PhysGrabObject)";
            var sb = new System.Text.StringBuilder();
            sb.Append("heldByLocalPlayer=");
            sb.Append(pgoHeldByLocal != null ? pgoHeldByLocal(pgo).ToString() : "?");
            sb.Append(" simulated=").Append(states.ContainsKey(pgo));
            sb.Append(" localGrab=").Append(IsLocalGrab(pgo));
            sb.Append(" grabbers=[");
            List<PhysGrabber> grabbing = pgo.playerGrabbing;
            for (int i = 0; i < grabbing.Count; i++)
            {
                PhysGrabber g = grabbing[i];
                if (i > 0) sb.Append(", ");
                if (g == null) { sb.Append("null"); continue; }
                sb.Append(g.isLocal ? "LOCAL" : "remote");
                sb.Append(grabberObject(g) == pgo ? "(holding)" : "(stale!)");
            }
            sb.Append(']');
            return sb.ToString();
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
            SeedPtvFields(st);
            HardRemove(st);
        }

        /// <summary>Writes the vanilla transform view's internal state to match ours so a
        /// handback never visibly snaps — does NOT unregister (Restore does that).</summary>
        private static void SeedPtvFields(SimState st)
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

        /// <summary>NetworkingReworked's HardSync applied to everything (credit:
        /// readthisifbad): teleport every shadowed object to the host's exact state.
        /// The player-facing emergency desync fix, bound to a key.</summary>
        internal static void ResyncAll()
        {
            int n = 0;
            foreach (SimState st in states.Values)
            {
                if (!st.hasHostState || st.localGrab || st.droneExempt) continue;
                if (st.pgo == null || st.rb == null || !pgoIsActive(st.pgo)) continue;
                st.rb.position = st.hostPos;
                st.rb.rotation = st.hostRot;
                if (!st.rb.isKinematic)
                {
                    st.rb.velocity = st.hostVel;
                    st.rb.angularVelocity = st.hostAngVel;
                }
                st.farTimer = 0f;
                n++;
            }
            Plugin.Log.LogInfo($"[resync] manual hard sync: {n} objects teleported to the host's state");
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
        private static float nextAutoResync;

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
                    SimManager.EnsureDroneAccessors();
                    SimManager.cachedDrones = UnityEngine.Object.FindObjectsOfType<ItemDrone>();
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

            if (SimManager.Ready && SimManager.IsClientInLobby())
            {
                if (Plugin.ResyncKeyCode != KeyCode.None && UnityEngine.Input.GetKeyDown(Plugin.ResyncKeyCode))
                {
                    SimManager.ResyncAll();
                }
                if (Plugin.AutoResyncSeconds.Value > 0f && Time.unscaledTime >= nextAutoResync)
                {
                    nextAutoResync = Time.unscaledTime + Mathf.Max(5f, Plugin.AutoResyncSeconds.Value);
                    SimManager.ResyncAll();
                }
            }

            Plugin.ApplyPhotonSettings();
            Plugin.ArchiveSessionLog();
            SimManager.MirrorHeldOrientationTargets();
            Smoothing.Sweep();
        }
    }
}
