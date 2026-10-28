using System;
#if !PREVIEW_CHECK
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
#endif

namespace MP3Together {
    internal static class PreviewData {
        public static byte[] Prepare(string ext, byte[] prefix) {
            if (ext == ".ogg") return TrimToOggPage(prefix);
            if (ext == ".wav") return FixWavHeader(prefix);
            if (ext == ".mp3") return StripVbrHeader(prefix);
            return prefix;
        }
        public static bool IsAllowedExtension(string ext) {
            return ext == ".mp3" || ext == ".wav" || ext == ".ogg";
        }
        public static int AudioStart(string ext, byte[] data, int available) {
            if (ext != ".mp3" || available < 10) return 0;
            if (data[0] != 0x49 || data[1] != 0x44 || data[2] != 0x33) return 0;
            int size = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
            int footer = (data[5] & 0x10) != 0 ? 10 : 0;
            return 10 + size + footer;
        }
        public static byte[] StripVbrHeader(byte[] data) {
            int start = AudioStart(".mp3", data, data.Length);
            int limit = Math.Min(data.Length - 4, start + 4096);
            for (int i = start; i < limit; i++) {
                if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0) continue;
                int end = Math.Min(data.Length - 4, i + 4 + 40);
                for (int j = i + 4; j < end; j++) {
                    bool xing = data[j] == 0x58 && data[j + 1] == 0x69 && data[j + 2] == 0x6E && data[j + 3] == 0x67;
                    bool info = data[j] == 0x49 && data[j + 1] == 0x6E && data[j + 2] == 0x66 && data[j + 3] == 0x6F;
                    bool vbri = data[j] == 0x56 && data[j + 1] == 0x42 && data[j + 2] == 0x52 && data[j + 3] == 0x49;
                    if (xing || info || vbri) {
                        data[j] = 0;
                        data[j + 1] = 0;
                        data[j + 2] = 0;
                        data[j + 3] = 0;
                        return data;
                    }
                }
                return data;
            }
            return data;
        }
        public static byte[] TrimToOggPage(byte[] data) {
            for (int i = data.Length - 4; i > 0; i--) {
                if (data[i] == 0x4F && data[i + 1] == 0x67 && data[i + 2] == 0x67 && data[i + 3] == 0x53) {
                    var trimmed = new byte[i];
                    Array.Copy(data, trimmed, i);
                    return trimmed;
                }
            }
            return data;
        }
        public static byte[] FixWavHeader(byte[] data) {
            if (data.Length < 44) return data;
            int dataChunk = -1;
            int pos = 12;
            while (pos + 8 <= data.Length) {
                int size = BitConverter.ToInt32(data, pos + 4);
                if (data[pos] == 0x64 && data[pos + 1] == 0x61 && data[pos + 2] == 0x74 && data[pos + 3] == 0x61) {
                    dataChunk = pos;
                    break;
                }
                if (size < 0) return data;
                pos += 8 + size + (size & 1);
            }
            if (dataChunk < 0) return data;
            int blockAlign = Math.Max((int)BitConverter.ToInt16(data, 32), 1);
            int available = data.Length - (dataChunk + 8);
            available -= available % blockAlign;
            var fixedData = new byte[dataChunk + 8 + available];
            Array.Copy(data, fixedData, fixedData.Length);
            Array.Copy(BitConverter.GetBytes(available), 0, fixedData, dataChunk + 4, 4);
            Array.Copy(BitConverter.GetBytes(fixedData.Length - 8), 0, fixedData, 4, 4);
            return fixedData;
        }
    }
#if !PREVIEW_CHECK
    internal static class DecoderHandler {
        private static readonly Dictionary<ushort, Coroutine> decoding = new Dictionary<ushort, Coroutine>();
        private static readonly Dictionary<ushort, string> decodePaths = new Dictionary<ushort, string>();
        private static readonly Dictionary<ushort, int> latestSeq = new Dictionary<ushort, int>();
        private static readonly HashSet<ushort> keptFiles = new HashSet<ushort>();
        private static int tempSeq;
        private static int seqCounter;
        internal static void ResetForNewSession() {
            latestSeq.Clear();
            SweepTempFiles(false);
        }
        internal static int NextSeq(ushort syncId) {
            int seq = ++seqCounter;
            latestSeq[syncId] = seq;
            return seq;
        }
        internal static void Invalidate(ushort syncId) {
            latestSeq[syncId] = ++seqCounter;
        }
        internal static bool Cancel(ushort syncId) {
            bool had = false;
            Coroutine co;
            if (decoding.TryGetValue(syncId, out co)) {
                if (co != null) MP3TogetherPlugin.Instance.StopCoroutine(co);
                decoding.Remove(syncId);
                had = true;
            }
            string oldPath;
            if (decodePaths.TryGetValue(syncId, out oldPath)) {
                decodePaths.Remove(syncId);
                if (!keptFiles.Remove(syncId)) {
                    try { File.Delete(oldPath); } catch { }
                }
                had = true;
            }
            return had;
        }
        internal static void Start(ushort syncId, int seq, string ext, byte[] bytes, bool isPreview, Func<ushort, int, bool> isLive) {
            Cancel(syncId);
            string tempPath = Path.Combine(Path.GetTempPath(), "mp3together_" + syncId + "_" + (++tempSeq) + ext);
            File.WriteAllBytes(tempPath, bytes);
            decodePaths[syncId] = tempPath;
            var co = MP3TogetherPlugin.Instance.StartCoroutine(DecodeAndPlay(tempPath, ext, syncId, seq, isPreview, bytes.Length, isLive, true));
            decoding[syncId] = co;
        }
        internal static void StartFromFile(ushort syncId, int seq, string ext, string path) {
            Cancel(syncId);
            long length = 0;
            try { length = new FileInfo(path).Length; } catch { }
            decodePaths[syncId] = path;
            keptFiles.Add(syncId);
            var co = MP3TogetherPlugin.Instance.StartCoroutine(DecodeAndPlay(path, ext, syncId, seq, false, (int)length, null, false));
            decoding[syncId] = co;
        }
        internal static string CopyFolder {
            get { return Path.Combine(Path.Combine(BepInEx.Paths.PluginPath, UpdaterConfig.FOLDER_NAME), Protocol.COPY_FOLDER_NAME); }
        }
        internal static string SafeName(string raw, string ext) {
            if (string.IsNullOrEmpty(raw)) return null;
            string name = raw.Trim();
            if (name.Length == 0 || name.Length > 150 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            if (Path.GetFileNameWithoutExtension(name).Length == 0) return null;
            if (!string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase)) return null;
            return name;
        }
        internal static string FindCopy(string name, long size) {
            try {
                string path = Path.Combine(CopyFolder, name);
                if (!File.Exists(path)) return null;
                return new FileInfo(path).Length == size ? path : null;
            } catch (Exception) {
                return null;
            }
        }
        internal static string CopyLocal(string name, byte[] data) {
            string temp = null;
            try {
                string folder = CopyFolder;
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name);
                temp = path + ".part";
                File.WriteAllBytes(temp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                MP3TogetherPlugin.Debug.CopySaved(name, data.Length);
                return path;
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: couldn't keep a copy of " + name + ": " + e.Message);
                MP3TogetherPlugin.Debug.CopyFailed(name, e.Message);
                if (temp != null) {
                    try { File.Delete(temp); } catch { }
                }
                return null;
            }
        }
        internal static IEnumerator LoadClip(string path, string ext, Action<AudioClip> done, Action<string> failed) {
            AudioType type = AudioType.MPEG;
            if (ext == ".wav") type = AudioType.WAV;
            else if (ext == ".ogg") type = AudioType.OGGVORBIS;
            string url;
            try {
                url = new Uri(path).AbsoluteUri;
            } catch (Exception e) {
                failed(e.Message);
                yield break;
            }
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, type)) {
                yield return www.SendWebRequest();
                if (!string.IsNullOrEmpty(www.error)) {
                    failed(www.error);
                    yield break;
                }
                AudioClip clip = null;
                string error = null;
                try {
                    clip = DownloadHandlerAudioClip.GetContent(www);
                } catch (Exception e) {
                    error = e.Message;
                }
                if (clip == null) {
                    failed(error ?? "no audio data");
                    yield break;
                }
                done(clip);
            }
        }
        internal static int SweepTempFiles(bool startup) {
            int removed = 0;
            long bytes = 0;
            try {
                var keep = new HashSet<string>(decodePaths.Values);
                foreach (string file in Directory.GetFiles(Path.GetTempPath(), "mp3together_*")) {
                    if (keep.Contains(file)) continue;
                    try {
                        if (!startup && (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalSeconds < Protocol.TEMP_FILE_MAX_AGE_SECONDS) continue;
                        long length = new FileInfo(file).Length;
                        File.Delete(file);
                        removed++;
                        bytes += length;
                    } catch (Exception) { }
                }
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: temp file sweep failed: " + e.Message);
            }
            if (startup) {
                try {
                    string folder = CopyFolder;
                    if (Directory.Exists(folder)) {
                        foreach (string part in Directory.GetFiles(folder, "*.part")) {
                            try { File.Delete(part); } catch { }
                        }
                    }
                } catch (Exception) { }
            }
            if (removed > 0) MP3TogetherPlugin.Debug.TempSwept(removed, bytes);
            return removed;
        }
        private static IEnumerator DecodeAndPlay(string path, string ext, ushort syncId, int seq, bool isPreview, int sourceBytes, Func<ushort, int, bool> isLive, bool deleteAfter) {
            float startTime = Time.unscaledTime;
            try {
                AudioType type = AudioType.MPEG;
                if (ext == ".wav") type = AudioType.WAV;
                else if (ext == ".ogg") type = AudioType.OGGVORBIS;
                string url;
                try {
                    url = new Uri(path).AbsoluteUri;
                } catch (Exception e) {
                    MP3TogetherPlugin.Log.LogError("mp3together: decode failed, bad path: " + e.Message);
                    MP3TogetherPlugin.Debug.FullDecodeFailed(syncId, e.Message);
                    if (deleteAfter) {
                        try { File.Delete(path); } catch { }
                    }
                    yield break;
                }
                using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, type)) {
                    yield return www.SendWebRequest();
                    if (!string.IsNullOrEmpty(www.error)) {
                        if (isPreview) {
                            MP3TogetherPlugin.Log.LogWarning("mp3together: preview decode failed, waiting for the full file: " + www.error);
                            MP3TogetherPlugin.Debug.PreviewFailed(syncId, www.error);
                        } else {
                            MP3TogetherPlugin.Log.LogError("mp3together: decode failed: " + www.error);
                            MP3TogetherPlugin.Debug.FullDecodeFailed(syncId, www.error);
                        }
                        if (deleteAfter) {
                            try { File.Delete(path); } catch { }
                        }
                        yield break;
                    }
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    if (deleteAfter) {
                        try { File.Delete(path); } catch { }
                    }
                    int currentSeq;
                    if (latestSeq.TryGetValue(syncId, out currentSeq) && currentSeq != seq) {
                        MP3TogetherPlugin.Debug.DecodeDropped(syncId);
                        UnityEngine.Object.Destroy(clip);
                        yield break;
                    }
                    if (isPreview) PlayPreviewClip(syncId, seq, clip, startTime, isLive);
                    else PlayFullClip(syncId, seq, clip, startTime, sourceBytes);
                }
            } finally {
                string current;
                if (decodePaths.TryGetValue(syncId, out current) && current == path) {
                    decodePaths.Remove(syncId);
                    decoding.Remove(syncId);
                    keptFiles.Remove(syncId);
                }
            }
        }
        private static void PlayPreviewClip(ushort syncId, int seq, AudioClip clip, float startTime, Func<ushort, int, bool> isLive) {
            if (!isLive(syncId, seq)) {
                MP3TogetherPlugin.Debug.PreviewDropped(syncId, "full file already arrived or transfer is gone");
                UnityEngine.Object.Destroy(clip);
                return;
            }
            if (HostPlayback.PlayPreview(syncId, seq, clip)) {
                MP3TogetherPlugin.Debug.PreviewPlaying(syncId, clip.length, Time.unscaledTime - startTime);
            } else {
                MP3TogetherPlugin.Log.LogWarning("mp3together: preview decoded syncId=" + syncId + " but its mp3 player is no longer registered - preview dropped.");
                UnityEngine.Object.Destroy(clip);
            }
        }
        private static void PlayFullClip(ushort syncId, int seq, AudioClip clip, float startTime, int sourceBytes) {
            var result = HostPlayback.SwapToFull(syncId, seq, clip);
            if (result == HostPlayback.SwapResult.Replaced) {
                MP3TogetherPlugin.Debug.SwapSkipped(syncId, "another song started on this player after the preview, full song not played");
                UnityEngine.Object.Destroy(clip);
                return;
            }
            if (result == HostPlayback.SwapResult.NoPreview && !HostPlayback.PlayFull(syncId, clip)) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: decoded syncId=" + syncId + " but its mp3 player is no longer registered - song dropped.");
                UnityEngine.Object.Destroy(clip);
                return;
            }
            MP3TogetherPlugin.Debug.RecvDone(syncId, sourceBytes, Time.unscaledTime - startTime);
        }
    }
#endif
}