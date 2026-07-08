using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace RevivalSync
{
    /// <summary>
    /// Snapshot (Hermite) interpolation for host-synced objects the simulation does NOT own
    /// (enemies, doors, players' tumbles...), based on the technique from BlueAmulet's
    /// REPONetworkTweaks — but additive: the vanilla PhotonTransformView still runs untouched,
    /// and we only re-position the object afterwards with a smoother estimate.
    /// </summary>
    internal static class Smoothing
    {
        internal static bool Active;

        private class Snapshot
        {
            internal Vector3 position;
            internal Quaternion rotation;
            internal Vector3 velocity;
            internal Vector3 angularVelocity;
        }

        private class InterpState
        {
            internal Rigidbody rb;
            internal Transform tf;
            internal PhysGrabHinge hinge;
            internal readonly LinkedList<Snapshot> snapshots = new LinkedList<Snapshot>();
            internal Snapshot prev;
            internal float interpStartTime = -1f;
            internal float smoothFreq = 0.1f;
            internal int lastTimestamp;
            internal bool haveFirst;
            internal Vector3 interpVel;
            internal Vector3 interpAngVel;

            internal void Clear()
            {
                snapshots.Clear();
                prev = null;
                interpStartTime = -1f;
            }
        }

        private static readonly Dictionary<PhotonTransformView, InterpState> states =
            new Dictionary<PhotonTransformView, InterpState>();

        private static AccessTools.FieldRef<PhotonTransformView, Vector3> recvPos;
        private static AccessTools.FieldRef<PhotonTransformView, Quaternion> recvRot;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> recvVel;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> recvAngVel;
        private static AccessTools.FieldRef<PhotonTransformView, Vector3> direction;
        private static AccessTools.FieldRef<PhotonTransformView, bool> teleportField;
        private static AccessTools.FieldRef<PhotonTransformView, bool> sleepingField;
        private static AccessTools.FieldRef<PhysGrabHinge, bool> hingeBroken;

        internal static bool InitAccessors()
        {
            try
            {
                recvPos = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("receivedPosition");
                recvRot = AccessTools.FieldRefAccess<PhotonTransformView, Quaternion>("receivedRotation");
                recvVel = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("receivedVelocity");
                recvAngVel = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("receivedAngularVelocity");
                direction = AccessTools.FieldRefAccess<PhotonTransformView, Vector3>("m_Direction");
                teleportField = AccessTools.FieldRefAccess<PhotonTransformView, bool>("teleport");
                sleepingField = AccessTools.FieldRefAccess<PhotonTransformView, bool>("isSleeping");
                hingeBroken = AccessTools.FieldRefAccess<PhysGrabHinge, bool>("broken");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"SmoothSync accessor init failed: {e}");
                return false;
            }
        }

        private static float BaseFrequency()
        {
            return 1f / Mathf.Max(1, PhotonNetwork.SerializationRate);
        }

        private static bool ShouldSmooth(PhotonTransformView ptv)
        {
            if (!Active || PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom) return false;
            if (SimManager.IsSuppressed(ptv))
            {
                // the simulation owns this object; drop our history so we rebuild fresh
                // if it is ever handed back (stale history = objects teleporting backwards)
                states.Remove(ptv);
                return false;
            }
            return true;
        }

        private static InterpState GetOrCreate(PhotonTransformView ptv)
        {
            if (!states.TryGetValue(ptv, out InterpState st))
            {
                st = new InterpState
                {
                    rb = ptv.GetComponent<Rigidbody>(),
                    tf = ptv.transform,
                    hinge = ptv.GetComponent<PhysGrabHinge>(),
                    smoothFreq = BaseFrequency(),
                };
                states[ptv] = st;
            }
            return st;
        }

        private static float nextSweep;

        internal static void Sweep()
        {
            if (Time.unscaledTime < nextSweep || states.Count == 0) return;
            nextSweep = Time.unscaledTime + 10f;
            if (!PhotonNetwork.InRoom)
            {
                states.Clear();
                return;
            }
            List<PhotonTransformView> dead = null;
            foreach (KeyValuePair<PhotonTransformView, InterpState> kv in states)
            {
                if (kv.Key == null)
                {
                    (dead ?? (dead = new List<PhotonTransformView>())).Add(kv.Key);
                }
            }
            if (dead != null)
            {
                foreach (PhotonTransformView k in dead) states.Remove(k);
            }
        }

        // ---- record incoming host state (after vanilla consumed the stream) ----

        [HarmonyPatch(typeof(PhotonTransformView), "OnPhotonSerializeView")]
        internal static class SerializePatch
        {
            private static void Postfix(PhotonTransformView __instance, PhotonStream stream, PhotonMessageInfo info, bool __runOriginal)
            {
                // if another patch skipped the original, the fields are stale — record nothing
                if (!__runOriginal || stream.IsWriting) return;
                if (!ShouldSmooth(__instance)) return;
                if (info.Sender != PhotonNetwork.MasterClient) return;

                InterpState st = GetOrCreate(__instance);
                int timestamp = info.SentServerTimestamp;
                if (st.haveFirst && timestamp == st.lastTimestamp) return;

                try
                {
                    Record(__instance, st, timestamp);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"SmoothSync record failed on {__instance.name}: {e.Message}");
                    st.Clear();
                }
            }
        }

        private static void Record(PhotonTransformView ptv, InterpState st, int timestamp)
        {
            bool teleported = teleportField(ptv);
            bool sleeping = sleepingField(ptv);

            if (teleported || sleeping)
            {
                // vanilla snaps/pins in these cases; restart interpolation cleanly after
                st.Clear();
                st.lastTimestamp = timestamp;
                st.haveFirst = true;
                return;
            }

            Vector3 vel = recvVel(ptv);
            var snap = new Snapshot
            {
                position = recvPos(ptv),
                rotation = recvRot(ptv),
                velocity = vel,
                angularVelocity = recvAngVel(ptv),
            };

            // doors: rigidbody velocity is useless around a hinge, derive it from movement
            if (st.hinge != null && !hingeBroken(st.hinge))
            {
                Vector3 dir = direction(ptv);
                snap.velocity = st.haveFirst
                    ? dir / Mathf.Max(1, timestamp - st.lastTimestamp) * 1000f
                    : dir / st.smoothFreq;
            }

            // project into the future to hide latency
            float future = Mathf.Max(Plugin.Future.Value * st.smoothFreq, 0f);
            if (st.snapshots.Count > 0 && (timestamp - st.lastTimestamp) / 1000f < Plugin.TimingThreshold.Value)
            {
                Snapshot last = st.snapshots.Last.Value;
                Vector3 accel = (snap.velocity - last.velocity) / st.smoothFreq;
                snap.position += snap.velocity * future + 0.5f * accel * future * future;
                Vector3 angAccel = (snap.angularVelocity - last.angularVelocity) / st.smoothFreq;
                snap.rotation *= Quaternion.Euler((snap.angularVelocity + angAccel * future) * future);
            }
            else
            {
                snap.position += snap.velocity * future;
                snap.rotation *= Quaternion.Euler(snap.angularVelocity * future);
            }

            st.snapshots.AddLast(snap);
            if (st.snapshots.Count == 2)
            {
                st.interpStartTime = Time.timeSinceLevelLoad;
            }
            else if (st.snapshots.Count >= 4)
            {
                // falling behind: drop the oldest and rebase to where we currently are
                st.snapshots.RemoveFirst();
                Snapshot first = st.snapshots.First.Value;
                first.position = st.tf.position;
                first.rotation = st.tf.rotation;
                first.velocity = st.interpVel;
                first.angularVelocity = st.interpAngVel;
                st.interpStartTime = Time.timeSinceLevelLoad;
            }

            // adapt to the host's real send rate
            if (st.haveFirst)
            {
                float measured = (timestamp - st.lastTimestamp) / 1000f;
                st.smoothFreq = measured >= Plugin.TimingThreshold.Value
                    ? BaseFrequency()
                    : Mathf.Lerp(st.smoothFreq, measured, Plugin.RateSmoothing.Value);
            }
            else
            {
                st.haveFirst = true;
            }
            st.lastTimestamp = timestamp;
        }

        // ---- apply smoothed movement (after vanilla did its coarse move) ----

        [HarmonyPatch(typeof(PhotonTransformView), "Update")]
        internal static class UpdatePatch
        {
            private static void Postfix(PhotonTransformView __instance)
            {
                if (!ShouldSmooth(__instance)) return;
                if (!states.TryGetValue(__instance, out InterpState st) || st.snapshots.Count == 0) return;
                if (teleportField(__instance) || sleepingField(__instance)) return;

                Apply(st);
            }
        }

        private static void Apply(InterpState st)
        {
            float factor = InterpFactor(st);
            while (factor >= 1f && st.snapshots.Count > 1)
            {
                st.prev = st.snapshots.First.Value;
                st.snapshots.RemoveFirst();
                st.interpStartTime += st.smoothFreq;
                factor = InterpFactor(st);
            }

            Vector3 pos;
            Quaternion rot;
            if (st.snapshots.Count == 1)
            {
                Snapshot only = st.snapshots.First.Value;
                if (st.prev != null && Plugin.Extrapolate.Value)
                {
                    float n = 2f - Mathf.Pow((float)Math.E, -factor);
                    pos = only.position + Vector3.SlerpUnclamped(st.prev.velocity, only.velocity, n) * ((n - 1f) * st.smoothFreq);
                    rot = Quaternion.SlerpUnclamped(st.prev.rotation, only.rotation, n);
                }
                else
                {
                    pos = only.position;
                    rot = only.rotation;
                }
                st.interpVel = only.velocity;
                st.interpAngVel = only.angularVelocity;
            }
            else
            {
                Snapshot a = st.snapshots.First.Value;
                Snapshot b = st.snapshots.First.Next.Value;
                pos = HermitePosition(a.position, a.velocity, b.position, b.velocity, factor, st.smoothFreq);
                rot = HermiteRotation(a.rotation, a.angularVelocity, b.rotation, b.angularVelocity, factor, st.smoothFreq);
                st.interpVel = Vector3.Slerp(a.velocity, b.velocity, factor);
                st.interpAngVel = Vector3.Lerp(a.angularVelocity, b.angularVelocity, factor);
            }

            if (st.rb != null)
            {
                st.rb.MovePosition(pos);
                st.rb.MoveRotation(rot);
            }
            else if (st.tf != null)
            {
                st.tf.position = pos;
                st.tf.rotation = rot;
            }
        }

        private static float InterpFactor(InterpState st)
        {
            if (st.smoothFreq <= 0f || st.interpStartTime < 0f) return 1f;
            return (Time.timeSinceLevelLoad - st.interpStartTime) / st.smoothFreq;
        }

        private static Vector3 HermitePosition(Vector3 startPos, Vector3 startVel, Vector3 endPos, Vector3 endVel, float t, float freq)
        {
            Vector3 a = startPos + startVel * (freq * t) / 3f;
            Vector3 b = endPos - endVel * (freq * (1f - t)) / 3f;
            return Vector3.Lerp(a, b, t);
        }

        private static Quaternion HermiteRotation(Quaternion startRot, Vector3 startSpin, Quaternion endRot, Vector3 endSpin, float t, float freq)
        {
            Quaternion a = startRot * Quaternion.Euler(startSpin * (freq * t) / 3f);
            Quaternion b = endRot * Quaternion.Euler(endSpin * (-1f * freq * (1f - t)) / 3f);
            return Quaternion.Slerp(a, b, t);
        }

        // ---- keep the buffer honest across teleports and re-enables ----

        [HarmonyPatch(typeof(PhotonTransformView), "Teleport")]
        internal static class TeleportPatch
        {
            private static void Postfix(PhotonTransformView __instance)
            {
                if (states.TryGetValue(__instance, out InterpState st)) st.Clear();
            }
        }

        [HarmonyPatch(typeof(PhotonTransformView), "OnEnable")]
        internal static class EnablePatch
        {
            private static void Postfix(PhotonTransformView __instance)
            {
                if (states.TryGetValue(__instance, out InterpState st))
                {
                    st.Clear();
                    st.haveFirst = false;
                }
            }
        }
    }
}
