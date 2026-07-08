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
            public int viewId;

            public bool localGrab;          // held by the local player right now
            public float postThrowTimer;    // > 0 shortly after release: blend extra softly
            public float desyncTimer;       // how long we've been far from the host's copy while holding
            public float stuckTimer;        // how long we've failed to converge (object wedged in geometry)
            public float debugTimer;        // rate limit for verbose diagnostics
            public int ridingTick;          // == current tick when sitting inside a locally-held cart

            public bool hasHostState;
            public float staleWarnTimer;    // rate limit for no-packets warnings
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

        // ---- reflection accessors into game internals ----
        private static AccessTools.FieldRef<PhysGrabObject, bool> pgoIsMaster;
        private static AccessTools.FieldRef<PhysGrabObject, bool> pgoIsActive;
        private static AccessTools.FieldRef<PhysGrabber, PhysGrabObject> grabberObject;
        private static AccessTools.FieldRef<PhysGrabCart, List<PhysGrabObject>> cartItems;
        private static AccessTools.FieldRef<PhysGrabCart, Transform> cartInCart;
        private static AccessTools.FieldRef<PlayerAvatar, bool> avatarSprinting;
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
                avatarSprinting = AccessTools.FieldRefAccess<PlayerAvatar, bool>("isSprinting");
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
            if (o.GetComponentInParent<PhysGrabHinge>() != null) return false;
            if (o.GetComponentInParent<ItemAttributes>() != null) return false;
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
                    float lag = Mathf.Abs((float)(PhotonNetwork.Time - networkTime / 1000.0));
                    st.hostPos = pos + dir * lag;
                    st.hostRot = rot;
                    st.hasHostState = true;
                    st.lastPacketTime = Time.unscaledTime;
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
                viewId = view.ViewID,
            };

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
            st.postThrowTimer = st.cart != null
                ? Plugin.PostThrowGrace.Value * 2.5f
                : Plugin.PostThrowGrace.Value;
            pgoIsMaster(pgo) = false;

            if (Plugin.VerboseLogging.Value)
            {
                Plugin.Log.LogInfo($"Released grab authority: {pgo.name}");
            }
        }

        internal static void LocalThrow(PhysGrabObject pgo, PhysGrabber player)
        {
            if (pgoThrow == null || pgo == null || player == null) return;
            if (!states.TryGetValue(pgo, out SimState st) || st.rb == null || st.rb.isKinematic) return;
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

            // The host's copy ALWAYS trails us by (speed x lag) while we drag — that trail is
            // normal and must not be "corrected", or the correction acts as a permanent brake.
            // Allow a velocity-proportional trail and compare against a velocity-led host
            // position; only true divergence gets corrected.
            float speed = st.rb.velocity.magnitude;
            float lagAllowance = speed * 0.4f;
            Vector3 ledHostPos = st.hostPos + st.hostVel * 0.15f;

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
                st.rb.position = Vector3.Lerp(st.rb.position, ledHostPos, 0.05f);
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

            // an awake, non-sleeping object should be receiving host packets — silence
            // here means the capture is failing, which is exactly what we need to know
            if (Plugin.VerboseLogging.Value && !st.hostSleeping && !st.rb.IsSleeping())
            {
                float age = Time.unscaledTime - st.lastPacketTime;
                st.staleWarnTimer -= Time.fixedDeltaTime;
                if (age > 3f && st.staleWarnTimer <= 0f)
                {
                    st.staleWarnTimer = 15f;
                    Plugin.Log.LogWarning($"[stale] {st.pgo.name}: awake but no host packets for {age:F0}s");
                }
            }

            if (st.postThrowTimer > 0f)
            {
                st.postThrowTimer -= Time.fixedDeltaTime;
            }

            if (st.hostTeleport)
            {
                st.hostTeleport = false;
                Snap(st, "host teleported it");
                return;
            }

            if (st.hostKinematic)
            {
                if (!st.rb.isKinematic) st.rb.isKinematic = true;
                st.rb.MovePosition(st.hostPos);
                st.rb.MoveRotation(st.hostRot);
                return;
            }
            if (st.rb.isKinematic) st.rb.isKinematic = false;

            if (st.hostSleeping)
            {
                if (st.rb.IsSleeping()) return; // both at rest — leave it alone (cheap)
                if ((st.rb.position - st.hostPos).sqrMagnitude < 0.0025f
                    && Quaternion.Angle(st.rb.rotation, st.hostRot) < 3f)
                {
                    st.rb.velocity = Vector3.zero;
                    st.rb.angularVelocity = Vector3.zero;
                    st.rb.position = st.hostPos;
                    st.rb.rotation = st.hostRot;
                    st.rb.Sleep();
                    return;
                }
                // far from the host's rest pose: fall through and blend toward it
            }

            float dist = Vector3.Distance(st.rb.position, st.hostPos);
            if (dist > Plugin.SnapDistance.Value)
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
            if (st.postThrowTimer > 0f)
            {
                a *= 0.3f; // let our locally-predicted throw fly; correct gently at first
            }

            st.rb.position = Vector3.Lerp(st.rb.position, st.hostPos, a);
            st.rb.rotation = Quaternion.Slerp(st.rb.rotation, st.hostRot, a);
            st.rb.velocity = Vector3.Lerp(st.rb.velocity, st.hostVel, a);
            st.rb.angularVelocity = Vector3.Lerp(st.rb.angularVelocity, st.hostAngVel, a);
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
            Smoothing.Sweep();
        }
    }
}
