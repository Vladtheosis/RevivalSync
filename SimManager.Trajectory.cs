using Photon.Pun;
using UnityEngine;

namespace RevivalSync
{
    /// <summary>
    /// Trajectory matching (config "Trajectory Matching", experimental): a thrown object, or a
    /// cart you are pushing, is steered onto the HOST's path while it moves — instead of flying
    /// purely locally for the throw grace and being reconciled once it lands.
    ///
    /// The trick is WHAT we compare. A host packet describes the host's copy a moment ago, and
    /// the host's copy itself runs about one upload-latency behind ours (it only learns about a
    /// throw when our data reaches it). So the packet is compared with where OUR copy was at
    /// the matching moment — looked up in a short history of our own path — never with where
    /// it is now. Comparing against "now" is what made both earlier attempts fail: following
    /// the host's present position pulled fresh throws backwards (it was still in our hand),
    /// and extrapolating the host forward moved the target between packets (1.2.13's rule).
    /// Here the correction changes only when a packet arrives, so the target is still between
    /// packets and the rule holds.
    ///
    /// Corrections we apply are recorded alongside the history, so a packet that was already
    /// in flight when we corrected is not counted twice. Tracking continues through bounces
    /// and slides (time alignment lines the two bounces up); a big disagreement (2.5m), both
    /// copies coming to rest, or 6 seconds ends it and hands the object back to the normal
    /// sync, eased in.
    /// </summary>
    internal static partial class SimManager
    {
        internal sealed class Trajectory
        {
            internal struct Sample
            {
                public int t;          // server clock, ms
                public Vector3 pos;
                public Vector3 vel;
                public Vector3 corr;   // total position correction applied before this sample
                public Vector3 corrV;  // total velocity correction applied before this sample
            }

            internal const int Capacity = 64; // ~1.3s at 50 ticks/s
            internal readonly Sample[] buf = new Sample[Capacity];
            internal int head;   // next write slot
            internal int count;

            internal bool flying;        // released and being steered onto the host's path
            internal float flightAge;
            internal int ticksSinceRelease;
            internal int stillTicks;
            internal int contacts;
            internal Vector3 corr, corrV;           // cumulative corrections applied
            internal Vector3 remaining, remainingV; // corrections still to apply
            internal Vector3 lastVel;
            internal bool hasLastVel;

            internal bool packetFresh;
            internal int packetStamp;

            // diagnostics for the end-of-flight log line
            internal int packets;
            internal float errSum, errMax;

            internal void Reset()
            {
                head = count = 0;
                flying = false;
                flightAge = 0f;
                ticksSinceRelease = 0;
                stillTicks = 0;
                contacts = 0;
                corr = corrV = remaining = remainingV = Vector3.zero;
                hasLastVel = false;
                packetFresh = false;
                packets = 0;
                errSum = errMax = 0f;
            }

            internal void Record(Rigidbody rb)
            {
                buf[head] = new Sample
                {
                    t = PhotonNetwork.ServerTimestamp,
                    pos = rb.position,
                    vel = rb.velocity,
                    corr = corr,
                    corrV = corrV,
                };
                head = (head + 1) % Capacity;
                if (count < Capacity) count++;
            }

            private ref Sample At(int i) // 0 = oldest
            {
                return ref buf[(head - count + i + Capacity) % Capacity];
            }

            /// <summary>Our state at server time t (interpolated), with every correction made
            /// since then added back — "where that moment of our path is now".</summary>
            internal bool StateAt(int t, out Vector3 pos, out Vector3 vel)
            {
                pos = vel = Vector3.zero;
                if (count < 2) return false;
                ref Sample first = ref At(0);
                if (unchecked(t - first.t) < 0) return false; // older than our history
                for (int i = 1; i < count; i++)
                {
                    ref Sample b = ref At(i);
                    if (unchecked(t - b.t) > 0) continue;
                    ref Sample a = ref At(i - 1);
                    int span = unchecked(b.t - a.t);
                    float k = span > 0 ? Mathf.Clamp01(unchecked(t - a.t) / (float)span) : 1f;
                    pos = Vector3.Lerp(a.pos + (corr - a.corr), b.pos + (corr - b.corr), k);
                    vel = Vector3.Lerp(a.vel + (corrV - a.corrV), b.vel + (corrV - b.corrV), k);
                    return true;
                }
                return false; // newer than our newest sample: nothing to compare yet
            }

            /// <summary>The upload lag that best lines our path up with the host's position:
            /// the history sample (corrected) closest to it. Seconds, or -1 if unclear.</summary>
            internal float MatchLag(int stamp, Vector3 hostPos)
            {
                float best = float.MaxValue;
                int bestT = 0;
                for (int i = 0; i < count; i++)
                {
                    ref Sample s = ref At(i);
                    int age = unchecked(stamp - s.t);
                    if (age < 0 || age > 800) continue;
                    float d = ((s.pos + (corr - s.corr)) - hostPos).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        bestT = s.t;
                    }
                }
                if (best > 0.75f * 0.75f) return -1f;
                return unchecked(stamp - bestT) / 1000f;
            }
        }

        // upload lag from us to the host's simulation (seconds) — learned from real flights,
        // starting from our ping as a guess (the host's own ping is usually similar)
        private static float trajLag = -1f;

        private static float TrajLag()
        {
            if (trajLag < 0f) trajLag = Mathf.Clamp(PhotonNetwork.GetPing() / 1000f + 0.03f, 0.05f, 0.6f);
            return trajLag;
        }

        private static bool TrajOn(SimState st)
        {
            return Plugin.TrajectoryMatching.Value && !st.isHinge;
        }

        /// <summary>A fresh host packet was decoded for this object (EndHostRead).</summary>
        private static void TrajOnPacket(SimState st, int sentStamp)
        {
            if (st.traj == null) return;
            st.traj.packetFresh = true;
            st.traj.packetStamp = sentStamp;
        }

        /// <summary>While held: keep a short history of our path (a throw's first host packets
        /// still describe the object in our hand, so the history must start before release).</summary>
        private static void TrajRecordHeld(SimState st)
        {
            if (!TrajOn(st)) return;
            if (st.traj == null) st.traj = new Trajectory();
            st.traj.flying = false;
            st.traj.Record(st.rb);
            st.traj.lastVel = st.rb.velocity;
            st.traj.hasLastVel = true;
        }

        private static void TrajBeginFlight(SimState st)
        {
            if (!TrajOn(st) || st.traj == null || st.traj.count < 2) return;
            Trajectory tr = st.traj;
            tr.flying = true;
            tr.flightAge = 0f;
            tr.ticksSinceRelease = 0;
            tr.stillTicks = 0;
            tr.contacts = 0;
            tr.remaining = tr.remainingV = Vector3.zero;
            tr.packets = 0;
            tr.errSum = tr.errMax = 0f;
            // tracking replaces the blind throw grace
            st.postThrowTimer = 0f;
            st.postThrowRamp = 0f;
        }

        /// <summary>One physics tick of a tracked flight. Returns true while tracking owns the
        /// object (the normal passive sync must not also run).</summary>
        private static bool TrajStep(SimState st)
        {
            Trajectory tr = st.traj;
            if (tr == null || !tr.flying) return false;
            if (!Plugin.TrajectoryMatching.Value) { TrajEnd(st, "switched off"); return false; }

            float dt = Time.fixedDeltaTime;
            tr.flightAge += dt;
            tr.ticksSinceRelease++;

            Vector3 vel = st.rb.velocity;
            // Contacts do NOT end tracking (1.4.0 did, and its first session showed most throws
            // handed back after 0.3s — straight into the old sync, which drags a still-moving
            // object toward where the host had it ~140ms ago: the very settle this replaces).
            // Comparing against our own past lines the two bounces up in time, so the same
            // bounce costs nothing and a different bounce is steered onto the host's. Counted
            // for the log only. Expected change: gravity plus last tick's velocity correction.
            if (tr.hasLastVel && tr.ticksSinceRelease > 3)
            {
                Vector3 expected = tr.lastVel + (st.rb.useGravity ? Physics.gravity * dt : Vector3.zero);
                if ((vel - expected).sqrMagnitude > 2f * 2f) tr.contacts++;
            }
            if (tr.flightAge > 6f) { TrajEnd(st, "timeout"); return false; }
            if (st.hostKinematic) { TrajEnd(st, "taken on host"); return false; }

            tr.Record(st.rb);

            if (tr.packetFresh)
            {
                tr.packetFresh = false;
                // learn the lag while the object moves fast enough for positions to tell times apart
                if (vel.sqrMagnitude > 4f)
                {
                    float m = tr.MatchLag(tr.packetStamp, st.hostPos);
                    if (m > 0f) trajLag = Mathf.Lerp(TrajLag(), Mathf.Clamp(m, 0.03f, 0.6f), 0.15f);
                }
                int ourMoment = unchecked(tr.packetStamp - Mathf.RoundToInt(TrajLag() * 1000f));
                if (tr.StateAt(ourMoment, out Vector3 ourPos, out Vector3 ourVel))
                {
                    Vector3 err = st.hostPos - ourPos;
                    float e = err.magnitude;
                    tr.packets++;
                    tr.errSum += e;
                    if (e > tr.errMax) tr.errMax = e;
                    if (e > 2.5f) { TrajEnd(st, $"diverged {e:F1}m"); return false; }
                    // replace, never add: this packet already reflects everything before it
                    tr.remaining = err;
                    tr.remainingV = st.hostVel - ourVel;
                }
            }

            // ease the open correction in: ~65% within 0.15s, so a new packet's change of mind
            // reads as a curve, not a kink
            const float k = 0.15f;
            Vector3 dp = tr.remaining * k;
            Vector3 dv = tr.remainingV * k;
            tr.remaining -= dp;
            tr.remainingV -= dv;
            tr.corr += dp;
            tr.corrV += dv;
            st.rb.position += dp;
            st.rb.velocity = vel + dv;
            tr.lastVel = st.rb.velocity;
            tr.hasLastVel = true;

            // landed and the host agrees it stopped. Held for 8 ticks: the top of a straight-up
            // throw is also "nearly still", but only for a few ticks
            bool still = st.rb.velocity.sqrMagnitude < 0.25f && st.hostVel.sqrMagnitude < 0.25f;
            tr.stillTicks = still ? tr.stillTicks + 1 : 0;
            if (tr.flightAge > 0.3f && tr.stillTicks >= 8)
            {
                TrajEnd(st, "landed");
                return false;
            }
            return true;
        }

        /// <summary>Held cart: the time-aligned comparison separates the normal ping trail
        /// (which the old raw-drift view could not tell apart from a real problem) from a real
        /// mismatch, e.g. the host's cart snagged on a doorframe. Only the real part is nudged
        /// out, gently, and the cargo rides along with every nudge.</summary>
        private static void TrajHeldCart(SimState st)
        {
            Trajectory tr = st.traj;
            if (tr == null || st.cart == null || !TrajOn(st)) return;
            if (tr.packetFresh)
            {
                tr.packetFresh = false;
                int ourMoment = unchecked(tr.packetStamp - Mathf.RoundToInt(TrajLag() * 1000f));
                if (tr.StateAt(ourMoment, out Vector3 ourPos, out _))
                {
                    Vector3 err = st.hostPos - ourPos;
                    err.y = 0f; // the floor decides height
                    const float deadband = 0.25f; // ordinary disagreement: leave it alone
                    float e = err.magnitude;
                    tr.remaining = e > deadband ? err * ((e - deadband) / e) : Vector3.zero;
                }
            }
            if (tr.remaining.sqrMagnitude < 1e-6f) return;
            Vector3 dp = tr.remaining * 0.06f;
            tr.remaining -= dp;
            tr.corr += dp;
            Vector3 before = st.rb.position;
            Quaternion rotBefore = st.rb.rotation;
            st.rb.position = before + dp;
            CarryCargo(st, before, rotBefore);
        }

        private static void TrajEnd(SimState st, string why)
        {
            Trajectory tr = st.traj;
            if (tr == null || !tr.flying) return;
            tr.flying = false;
            tr.remaining = tr.remainingV = Vector3.zero;
            // the normal sync takes over, eased in rather than grabbing hold
            st.postThrowTimer = 0f;
            st.postThrowRamp = 0.4f;
            if (Plugin.VerboseLogging.Value)
            {
                float avg = tr.packets > 0 ? tr.errSum / tr.packets : 0f;
                Plugin.Log.LogInfo($"[traj] {st.pgo.name}: {why} after {tr.flightAge:F2}s, " +
                    $"{tr.packets} packets, {tr.contacts} contacts, lag {TrajLag() * 1000f:F0}ms, " +
                    $"error avg {avg:F2}m max {tr.errMax:F2}m");
            }
        }
    }
}
