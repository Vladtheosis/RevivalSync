using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace RevivalSync
{
    /// <summary>
    /// Host Assist (config, experimental, off by default): when BOTH the host and a client run
    /// RevivalSync with it on, the client streams the state of what it holds to the host and
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
            if (!On || !PhotonNetwork.InRoom) return;
            Player me = PhotonNetwork.LocalPlayer;
            if (me != null && !me.CustomProperties.ContainsKey(Prop))
            {
                me.SetCustomProperties(new Hashtable { { Prop, 1 } });
            }
            Player host = PhotonNetwork.MasterClient;
            hostSupports = host != null && !host.IsLocal && host.CustomProperties.ContainsKey(Prop);
            if (hostSupports && !announcedClient)
            {
                announcedClient = true;
                Plugin.Log.LogInfo("[assist] the host runs RevivalSync with Host Assist — what you hold is streamed to it.");
            }
        }

        // ---- client: send what we hold ----

        internal static void ClientSend(SimManager.SimState st, bool release)
        {
            if (!On || !hostSupports || st == null || st.rb == null || st.viewId == 0 || st.isHinge) return;
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
                    // arrives right after that player's GrabEnded: replace the throw the host
                    // just computed from ITS view of the hand with the one the player made
                    if (entries.TryGetValue(viewId, out Entry held) && held.actor == ev.Sender
                        && Time.unscaledTime - held.time < 1f && held.pgo != null && held.pgo.rb != null)
                    {
                        Rigidbody rb = held.pgo.rb;
                        if (!rb.isKinematic && (pos - rb.position).sqrMagnitude < MaxGap * MaxGap)
                        {
                            rb.position = pos;
                            rb.rotation = rot;
                            rb.velocity = vel;
                            rb.angularVelocity = angVel;
                        }
                    }
                    entries.Remove(viewId);
                    return;
                }

                if (!entries.TryGetValue(viewId, out Entry e))
                {
                    PhotonView view = PhotonView.Find(viewId);
                    PhysGrabObject pgo = view != null ? view.GetComponent<PhysGrabObject>() : null;
                    if (pgo == null || pgo.rb == null) return;
                    e = new Entry { pgo = pgo };
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
                if (Time.unscaledTime - e.time > 0.25f) continue; // stream paused: host physics alone
                PhysGrabber grabber = GrabberOf(e.pgo, e.actor);
                if (grabber == null) continue;                      // only the player holding it may steer it
                Rigidbody rb = e.pgo.rb;
                if (rb.isKinematic) continue;
                Vector3 gap = e.pos - rb.position;
                if (gap.sqrMagnitude > MaxGap * MaxGap) continue;   // snagged or bogus: host physics wins

                // our steering replaces the host's lagged copy of that player's grab pull
                grabber.OverridePhysGrabForcesDisable(0.1f);
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
            foreach (int id in dead) entries.Remove(id);
        }
    }
}
