using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace RevivalSync
{
    /// <summary>
    /// Host Assist (config, ON by default since 1.6.0 = automatic): when BOTH the host and a
    /// client run RevivalSync, the client streams the state of what it holds to the host and
    /// the host's copy follows it, instead of re-simulating the grab one upload-lag late. On
    /// release the host takes the client's release velocity, so both fly the same arc.
    ///
    /// Everything else in the mod works around a host that does not know we exist; this is
    /// the one path that removes the client+host round trip instead of hiding it. A vanilla
    /// host ignores the event, and a client only sends once the host advertises support
    /// (player property "rsync"), so mixed lobbies behave exactly as before.
    ///
    /// The host never trusts blindly: the sender must be grabbing that object on the host,
    /// and the reported state must be near the host's own copy and at a sane speed.
    /// </summary>
    internal static class HostAssist
    {
        private const byte EventCode = 187;
        private const string Prop = "rsync";
        // Value of the player property. 1 = 1.5.x (opt-in, items steered or not depending on
        // the build); 2 = 1.6.0: on by default, weapons streamed, scripted objects not. A
        // client only streams to a host on its own protocol or newer.
        private const int Protocol = 2;

        private static int ProtocolOf(Player p)
        {
            return p != null && p.CustomProperties.TryGetValue(Prop, out object v) && v is int i ? i : 0;
        }
        private const float MaxGap = 4f;     // metres between host copy and reported state
        private const float MaxSpeed = 30f;
        private const float Follow = 10f;    // same gain the game uses for its own velocity follow

        private class Entry
        {
            public PhysGrabObject pgo;
            public int actor;
            public Vector3 pos, vel, angVel;
            public Quaternion rot;
            public float time;
            public float releasedAt = -999f; // holder let go: keep following its stream briefly
            public bool hostDriven;          // scripted object streamed by an older client: never steered
        }

        /// <summary>How long after letting go the holder keeps streaming and the host keeps
        /// following — the release then needs no jump on the host and no settle on the client.</summary>
        internal const float Tail = 0.4f;

        /// <summary>Client: the host is following what we hold, so WE are the reference —
        /// nothing on our side should steer a just-released object toward the host.</summary>
        internal static bool Active => On && hostSupports;

        /// <summary>Client: is THIS object streamed to the host? Never for a host-driven one
        /// (see <see cref="HostDrivenReason"/>) — those keep the ordinary non-assist behaviour.</summary>
        internal static bool For(SimManager.SimState st) => Active && st != null && !st.hostDriven;

        // components every valuable carries (or level/UI helpers) that are not behaviour scripts
        private static readonly HashSet<string> plainTypes = new HashSet<string>
        {
            "ValuableObject", "NotValuableObject", "ValuablePropSwitch", "ValuableExtracting",
            "ValuableDiscover", "ValuableDiscoverCustom", "ValuableDiscoverGraphic",
            "ValuableVolume", "ValuableVolumeDebugDisplay",
        };

        /// <summary>
        /// Why the host has to keep driving this object itself, or null if it is plain physics
        /// (carts, ordinary loot, props). The host's Steer replaces EVERYTHING the host does
        /// to a held object: it switches off the host's grab forces (which also carry the
        /// TurnXYZ straightening item scripts ask for) and overwrites both velocities every
        /// tick. Fine for plain objects. For a scripted one it erases, for every player:
        ///  - items: recoil, melee swing forces and the hold orientation — and a gadget whose
        ///    rotation we mirror FROM the host would chase its own echo;
        ///  - trap/scripted valuables: the host-only jolts, shakes and pulls (scream doll,
        ///    phone, ice saw, baby head...) that are the whole point of the trap.
        /// Those stay on the non-assist path, which is how they behave on a vanilla host.
        /// Errs toward "host-driven": a wrong yes only costs the assist on that object.
        /// </summary>
        internal static string HostDrivenReason(PhysGrabObject pgo, bool isCart)
        {
            if (pgo == null || isCart) return null;
            // A weapon is only ever streamed by a client running the game's weapon logic for
            // it (Instant Weapons): its copy is the complete one, recoil and swings included.
            if (pgo.GetComponentInChildren<ItemGun>(true) != null || pgo.GetComponentInChildren<ItemMelee>(true) != null)
            {
                return null;
            }
            if (pgo.GetComponentInParent<ItemAttributes>() != null) return "item";
            foreach (MonoBehaviour c in pgo.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (c is Trap) return n;
                if (plainTypes.Contains(n)) continue;
                if (n.Contains("Valuable") || n.StartsWith("MuseumProp")) return n;
            }
            return null;
        }

        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private static readonly List<int> dead = new List<int>();
        private static readonly RaiseEventOptions toHost = new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient };
        private static bool hooked;
        private static float nextPropCheck;
        private static bool hostSupports;
        private static bool announcedHost, announcedClient;

        private static bool On => Plugin.HostAssistEnabled != null && Plugin.HostAssistEnabled.Value;

        /// <summary>Per frame, once Photon is up: hook the event, advertise, read the host's flag.</summary>
        internal static void FrameUpdate()
        {
            if (!Plugin.PhotonReady) return;
            if (!hooked)
            {
                hooked = true;
                PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
            }
            if (Time.unscaledTime < nextPropCheck) return;
            nextPropCheck = Time.unscaledTime + 2f;
            hostSupports = false;
            if (!PhotonNetwork.InRoom) return;
            // Advertise what we speak — and 0 when switched off, so nobody keeps treating a
            // host that turned it off as one that follows them.
            Player me = PhotonNetwork.LocalPlayer;
            int mine = On ? Protocol : 0;
            if (me != null && ProtocolOf(me) != mine && (mine != 0 || me.CustomProperties.ContainsKey(Prop)))
            {
                me.SetCustomProperties(new Hashtable { { Prop, mine } });
            }
            if (!On) return;
            Player host = PhotonNetwork.MasterClient;
            hostSupports = host != null && !host.IsLocal && ProtocolOf(host) >= Protocol;
            if (hostSupports && !announcedClient)
            {
                announcedClient = true;
                Plugin.Log.LogInfo("[assist] the host has RevivalSync — Host Assist is on: what you hold is streamed to it.");
            }
        }

        // ---- client: send what we hold ----

        internal static void ClientSend(SimManager.SimState st, bool release)
        {
            if (!On || !hostSupports || st == null || st.rb == null || st.viewId == 0 || st.isHinge) return;
            if (st.hostDriven) return; // the host runs logic on it that we do not: it must not follow us
            object[] data = { st.viewId, st.rb.position, st.rb.rotation, st.rb.velocity, st.rb.angularVelocity, release };
            // the release must arrive (it carries the throw); the stream may drop packets
            PhotonNetwork.RaiseEvent(EventCode, data, toHost, release ? SendOptions.SendReliable : SendOptions.SendUnreliable);
        }

        // ---- host: receive and follow ----

        private static void OnEvent(EventData ev)
        {
            if (ev.Code != EventCode || !On || !PhotonNetwork.IsMasterClient) return;
            try
            {
                if (!(ev.CustomData is object[] d) || d.Length < 6) return;
                int viewId = (int)d[0];
                Vector3 pos = (Vector3)d[1];
                Quaternion rot = (Quaternion)d[2];
                Vector3 vel = Vector3.ClampMagnitude((Vector3)d[3], MaxSpeed);
                Vector3 angVel = Vector3.ClampMagnitude((Vector3)d[4], MaxSpeed);
                bool release = (bool)d[5];

                if (release)
                {
                    // Arrives right after that player's GrabEnded. No teleport (1.5.0 wrote the
                    // position: a cart jumped a metre on the host, through its own cargo): just
                    // mark it, and keep following the holder's stream for the tail. That also
                    // replaces the throw the host computed from ITS late view of the hand.
                    if (!entries.TryGetValue(viewId, out Entry held) || held.actor != ev.Sender) return;
                    held.releasedAt = Time.unscaledTime;
                    held.pos = pos;
                    held.rot = rot;
                    held.vel = vel;
                    held.angVel = angVel;
                    held.time = Time.unscaledTime;
                    return;
                }

                if (!entries.TryGetValue(viewId, out Entry e))
                {
                    PhotonView view = PhotonView.Find(viewId);
                    PhysGrabObject pgo = view != null ? view.GetComponent<PhysGrabObject>() : null;
                    if (pgo == null || pgo.rb == null) return;
                    // a 1.5.3+ client never streams these; this covers an older one
                    bool isCart = pgo.GetComponent<PhysGrabCart>() != null;
                    e = new Entry { pgo = pgo, hostDriven = HostDrivenReason(pgo, isCart) != null };
                    entries[viewId] = e;
                    if (!announcedHost)
                    {
                        announcedHost = true;
                        Plugin.Log.LogInfo("[assist] hosting: a client is streaming held objects — following them.");
                    }
                }
                e.actor = ev.Sender;
                e.pos = pos;
                e.rot = rot;
                e.vel = vel;
                e.angVel = angVel;
                e.time = Time.unscaledTime;
            }
            catch (Exception ex)
            {
                if (Plugin.VerboseLogging.Value) Plugin.Log.LogWarning($"[assist] bad event: {ex.Message}");
            }
        }

        private static PhysGrabber GrabberOf(PhysGrabObject pgo, int actor)
        {
            List<PhysGrabber> grabbing = pgo.playerGrabbing;
            for (int i = 0; i < grabbing.Count; i++)
            {
                PhysGrabber g = grabbing[i];
                if (g != null && g.photonView != null && g.photonView.OwnerActorNr == actor) return g;
            }
            return null;
        }

        /// <summary>Host, every physics tick: steer each streamed object onto its holder's copy.</summary>
        internal static void HostTick()
        {
            if (entries.Count == 0) return;
            if (!On || !PhotonNetwork.IsMasterClient)
            {
                entries.Clear();
                return;
            }
            dead.Clear();
            foreach (KeyValuePair<int, Entry> kv in entries)
            {
                Entry e = kv.Value;
                if (e.pgo == null || e.pgo.rb == null || Time.unscaledTime - e.time > 1f)
                {
                    dead.Add(kv.Key);
                    continue;
                }
                Steer(e);
            }
            foreach (int id in dead) entries.Remove(id);
        }

        /// <summary>The host's CartSteer rewrites a handle-held cart's velocity every tick
        /// toward a point behind where the HOST sees the player (always late) — it simply
        /// overwrote the assist in 1.5.0 (held carts still trailed ~0.24s x speed). Called
        /// right after it, so the holder's own state has the last word.</summary>
        internal static void AfterCartSteer(PhysGrabCart cart)
        {
            if (entries.Count == 0 || cart == null || !On || !PhotonNetwork.IsMasterClient) return;
            foreach (Entry e in entries.Values)
            {
                if (e.pgo != null && e.pgo.gameObject == cart.gameObject)
                {
                    Steer(e);
                    return;
                }
            }
        }

        private static void Steer(Entry e)
        {
            float now = Time.unscaledTime;
            if (e.hostDriven) return;                            // scripted: the host's own logic drives it
            if (now - e.time > 0.25f) return;                    // stream paused: host physics alone
            Rigidbody rb = e.pgo.rb;
            if (rb == null || rb.isKinematic) return;
            PhysGrabber grabber = GrabberOf(e.pgo, e.actor);
            if (grabber == null)
            {
                // only the holder may steer it — or, for a moment after letting go, the player
                // who just held it, as long as nobody else has picked it up
                if (now - e.releasedAt > Tail + 0.2f || e.pgo.playerGrabbing.Count > 0) return;
            }
            Vector3 gap = e.pos - rb.position;
            if (gap.sqrMagnitude > MaxGap * MaxGap) return;      // snagged or bogus: host physics wins

            // our steering replaces the host's lagged copy of that player's grab pull
            if (grabber != null) grabber.OverridePhysGrabForcesDisable(0.1f);
            rb.velocity = Vector3.ClampMagnitude(e.vel + gap * Follow, MaxSpeed);
            (e.rot * Quaternion.Inverse(rb.rotation)).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            Vector3 turn = Vector3.zero;
            if (Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
            {
                turn = axis * (angle * Mathf.Deg2Rad * Follow);
            }
            rb.angularVelocity = Vector3.ClampMagnitude(e.angVel + turn, MaxSpeed);
        }
    }
}
