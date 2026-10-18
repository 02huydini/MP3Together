using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Together;

namespace MP3Client {
    internal static class HostPlayback {
        internal enum SwapResult { NoPreview, Swapped, Replaced }
        private class Track {
            public int PreviewSeq;
            public AudioClip PreviewClip;
            public bool Paused;
            public float PausedAt;
            public AudioClip PausedClip;
            public AudioClip Owned;
        }
        private static readonly Dictionary<ushort, Track> tracks = new Dictionary<ushort, Track>();
        private static readonly FieldInfo sourceField = AccessTools.Field(typeof(MP3PlayerServerAudioStreamer), "musicplayersource");
        private static readonly FieldInfo queueField = AccessTools.Field(typeof(MP3PlayerServerAudioStreamer), "queued_resampled_samples");
        private static readonly PropertyInfo itemProp = typeof(SyncInfo).GetProperty("item", BindingFlags.Public | BindingFlags.Instance);
        private static readonly MethodInfo playMethod = AccessTools.Method(typeof(MP3Menu_Play_MultiplayerPatch), "Server_PlayThisSongOnThisMp3Player");
        internal static int OwnedClipCount {
            get {
                int n = 0;
                foreach (var t in tracks.Values) {
                    if (t.Owned != null) n++;
                }
                return n;
            }
        }
        internal static void Reset() {
            foreach (var t in tracks.Values) FreeOwned(t, "session reset", 0);
            tracks.Clear();
        }
        private static void FreeOwned(Track t, string why, ushort syncId) {
            if (t.Owned == null) return;
            UnityEngine.Object.Destroy(t.Owned);
            t.Owned = null;
            MP3TogetherDebug.Debug.ClipFreed(syncId, why);
        }
        private static void KeepOwned(Track t, AudioClip clip, ushort syncId) {
            if (t.Owned != null && t.Owned != clip) FreeOwned(t, "replaced by a newer clip", syncId);
            t.Owned = clip;
        }
        internal static void Sweep() {
            List<ushort> gone = null;
            foreach (var kv in tracks) {
                var t = kv.Value;
                if (t.Owned == null) continue;
                var src = GetSource(GetStreamer(kv.Key));
                if (src == null) {
                    FreeOwned(t, "mp3 player gone", kv.Key);
                    if (gone == null) gone = new List<ushort>();
                    gone.Add(kv.Key);
                } else if (src.clip != t.Owned) {
                    FreeOwned(t, "no longer on the player", kv.Key);
                }
            }
            if (gone != null) {
                foreach (var id in gone) tracks.Remove(id);
            }
        }
        private static Track GetTrack(ushort syncId) {
            Track t;
            if (!tracks.TryGetValue(syncId, out t)) {
                t = new Track();
                tracks[syncId] = t;
            }
            return t;
        }
        private static object GetItemObject(ushort syncId) {
            SyncInfo si;
            if (!NetObjectRegistry.TryGetSyncInfo((knetid)syncId, out si)) return null;
            return itemProp.GetValue(si);
        }
        private static MP3PlayerServerAudioStreamer GetStreamer(ushort syncId) {
            var comp = GetItemObject(syncId) as Component;
            if (comp == null) return null;
            return comp.GetComponent<MP3PlayerServerAudioStreamer>();
        }
        private static AudioSource GetSource(MP3PlayerServerAudioStreamer streamer) {
            return streamer != null ? sourceField.GetValue(streamer) as AudioSource : null;
        }
        private static void ClearQueues(MP3PlayerServerAudioStreamer streamer) {
            streamer.pcm_buffer_from_the_filter_thing.Clear();
            var q = queueField.GetValue(streamer) as Queue<float>;
            if (q != null) q.Clear();
        }
        private static bool Play(ushort syncId, AudioClip clip) {
            object item = GetItemObject(syncId);
            if (item == null) return false;
            playMethod.Invoke(null, new object[] { clip, item });
            return true;
        }
        internal static bool PlayFull(ushort syncId, AudioClip clip) {
            if (!Play(syncId, clip)) return false;
            var t = GetTrack(syncId);
            t.PreviewSeq = 0;
            t.PreviewClip = null;
            t.Paused = false;
            KeepOwned(t, clip, syncId);
            return true;
        }
        internal static bool PlayPreview(ushort syncId, int seq, AudioClip clip) {
            if (!Play(syncId, clip)) return false;
            var t = GetTrack(syncId);
            t.PreviewSeq = seq;
            t.PreviewClip = clip;
            t.Paused = false;
            KeepOwned(t, clip, syncId);
            return true;
        }
        internal static SwapResult SwapToFull(ushort syncId, int seq, AudioClip full) {
            Track t;
            if (!tracks.TryGetValue(syncId, out t) || t.PreviewSeq != seq || t.PreviewClip == null) return SwapResult.NoPreview;
            var streamer = GetStreamer(syncId);
            var src = GetSource(streamer);
            if (src == null) {
                t.PreviewSeq = 0;
                t.PreviewClip = null;
                return SwapResult.NoPreview;
            }
            if (src.clip != t.PreviewClip) {
                t.PreviewSeq = 0;
                t.PreviewClip = null;
                return SwapResult.Replaced;
            }
            float resumeAt;
            if (t.Paused) resumeAt = t.PausedAt;
            else if (src.isPlaying) resumeAt = src.time;
            else resumeAt = t.PreviewClip.length;
            resumeAt = Mathf.Clamp(resumeAt, 0f, Mathf.Max(0f, full.length - 0.05f));
            streamer.CheckRequiredProcessing(full);
            src.Stop();
            src.clip = full;
            ClearQueues(streamer);
            t.PreviewSeq = 0;
            t.PreviewClip = null;
            KeepOwned(t, full, syncId);
            if (t.Paused) {
                t.PausedAt = resumeAt;
                t.PausedClip = full;
                MP3TogetherDebug.Debug.SwapDone(syncId, resumeAt, true);
                return SwapResult.Swapped;
            }
            src.time = resumeAt;
            src.Play();
            MP3TogetherDebug.Debug.SwapDone(syncId, resumeAt, false);
            return SwapResult.Swapped;
        }
        internal static bool IsPreviewOf(ushort syncId, int seq) {
            Track t;
            return tracks.TryGetValue(syncId, out t) && t.PreviewSeq == seq && t.PreviewClip != null;
        }
        internal static void StopIfPreviewOf(ushort syncId, int seq) {
            if (IsPreviewOf(syncId, seq)) StopAudio(syncId);
        }
        internal static void StopAudio(ushort syncId) {
            Track t;
            if (tracks.TryGetValue(syncId, out t)) {
                t.PreviewSeq = 0;
                t.PreviewClip = null;
                t.Paused = false;
            }
            var src = GetSource(GetStreamer(syncId));
            if (src == null) return;
            src.Stop();
            if (tracks.TryGetValue(syncId, out t) && t.Owned != null && src.clip == t.Owned) {
                src.clip = null;
                FreeOwned(t, "stopped", syncId);
            }
            MP3TogetherDebug.Debug.Trace("HostPlayback.StopAudio", "stopped syncId=" + syncId);
        }
        internal static void TogglePause(ushort syncId) {
            var streamer = GetStreamer(syncId);
            var src = GetSource(streamer);
            if (src == null || src.clip == null) return;
            var t = GetTrack(syncId);
            if (t.Paused && (src.isPlaying || src.clip != t.PausedClip)) t.Paused = false;
            if (t.Paused) {
                t.Paused = false;
                ClearQueues(streamer);
                src.time = Mathf.Clamp(t.PausedAt, 0f, Mathf.Max(0f, src.clip.length - 0.05f));
                src.Play();
                MP3TogetherDebug.Debug.Trace("HostPlayback.TogglePause", "resumed syncId=" + syncId + " from " + t.PausedAt.ToString("F2") + "s");
            } else if (src.isPlaying) {
                t.PausedAt = src.time;
                t.PausedClip = src.clip;
                t.Paused = true;
                src.Stop();
                MP3TogetherDebug.Debug.Trace("HostPlayback.TogglePause", "paused syncId=" + syncId + " at " + t.PausedAt.ToString("F2") + "s");
            }
        }
    }
}