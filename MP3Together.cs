using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using Together;

namespace MP3Client {
    internal class MP3Together : MP3ClientLog {
        public override void SendStart(ushort syncId, string fileName, int totalBytes) {
            Totals.Add("send.started");
            Info("send start syncId=" + syncId + " file=\"" + fileName + "\" bytes=" + totalBytes);
        }
        public override void SendChunk(ushort syncId, int chunkIndex, int offset, int length, float elapsedSeconds, float sinceLastSeconds) {
            Info("send chunk syncId=" + syncId + " #" + chunkIndex + " offset=" + offset + " len=" + length + " t=" + elapsedSeconds.ToString("F2") + "s (+" + sinceLastSeconds.ToString("F2") + "s)");
        }
        public override void SendProgress(ushort syncId, int sentBytes, int totalBytes, float elapsedSeconds) {
            float rateKBs = elapsedSeconds > 0f ? (sentBytes / 1024f) / elapsedSeconds : 0f;
            Info("send progress syncId=" + syncId + " " + sentBytes + "/" + totalBytes + " bytes, " + elapsedSeconds.ToString("F1") + "s elapsed, " + rateKBs.ToString("F1") + " KB/s avg");
        }
        public override void SendDone(ushort syncId, int totalBytes, float elapsedSeconds) {
            Totals.Add("send.completed");
            Totals.Add("send.bytes", totalBytes);
            Totals.Add("send.ms", (long)(elapsedSeconds * 1000f));
            float rateKBs = elapsedSeconds > 0f ? (totalBytes / 1024f) / elapsedSeconds : 0f;
            Info("send done syncId=" + syncId + " " + totalBytes + " bytes in " + elapsedSeconds.ToString("F1") + "s, " + rateKBs.ToString("F1") + " KB/s avg");
        }
        public override void SendAckStalled(ushort syncId) {
            Totals.Add("send.stalls");
            Warn("send window full and no ack from host for " + Protocol.ACK_STALL_SECONDS.ToString("F0") + "s syncId=" + syncId + " - holding the window, not sending past it (aborts after " + Protocol.SEND_ABORT_SECONDS.ToString("F0") + "s without progress)");
        }
        public override void SendCoroutineExit(ushort syncId, string reason) {
            Info("send coroutine exit syncId=" + syncId + " reason=" + reason);
        }
        public override void RecvBegin(ushort syncId, int totalBytes, string ext) {
            Totals.Add("recv.begun");
            Info("recv begin syncId=" + syncId + " ext=" + ext + " bytes=" + totalBytes);
        }
        public override void RecvProgress(ushort syncId, int receivedChunks, int totalChunks, float elapsedSeconds) {
            Info("recv progress syncId=" + syncId + " chunks " + receivedChunks + "/" + totalChunks + ", " + elapsedSeconds.ToString("F1") + "s elapsed");
        }
        public override void RecvDone(ushort syncId, int totalBytes, float elapsedSeconds) {
            Totals.Add("recv.played");
            Totals.Add("recv.bytes", totalBytes);
            float rateKBs = elapsedSeconds > 0f ? (totalBytes / 1024f) / elapsedSeconds : 0f;
            Info("recv done syncId=" + syncId + " " + totalBytes + " bytes in " + elapsedSeconds.ToString("F1") + "s, " + rateKBs.ToString("F1") + " KB/s avg");
        }
        public override void RecvCancelled(ushort syncId) {
            Totals.Add("recv.cancelled");
            Info("recv cancelled syncId=" + syncId);
        }
        public override void DecodeReplaced(ushort syncId) {
            Totals.Add("decode.replaced");
            Info("decode replaced syncId=" + syncId + " - cancelled the in-flight decode and deleted its temp file before starting the new one");
        }
        public override void DecodeDropped(ushort syncId) {
            Totals.Add("decode.dropped");
            Info("decode dropped syncId=" + syncId + " - a newer transfer or cancel superseded it, not played");
        }
        public override void PreviewStarted(ushort syncId, int prefixBytes, int totalBytes) {
            Totals.Add("preview.started");
            Info("preview start syncId=" + syncId + " prefix=" + prefixBytes + "/" + totalBytes + " bytes (" + (prefixBytes * 100L / Math.Max(totalBytes, 1)) + "%)");
        }
        public override void PreviewPlaying(ushort syncId, float clipSeconds, float decodeSeconds) {
            Totals.Add("preview.playing");
            Info("preview playing syncId=" + syncId + " clip=" + clipSeconds.ToString("F1") + "s decode=" + decodeSeconds.ToString("F2") + "s");
        }
        public override void PreviewDropped(ushort syncId, string reason) {
            Totals.Add("preview.dropped");
            Info("preview dropped syncId=" + syncId + " - " + reason);
        }
        public override void SwapDone(ushort syncId, float resumeAt, bool paused) {
            Totals.Add("swap.done");
            Info("swap done syncId=" + syncId + " full song resumes at " + resumeAt.ToString("F2") + "s" + (paused ? " (kept paused)" : ""));
        }
        public override void SwapSkipped(ushort syncId, string reason) {
            Totals.Add("swap.skipped");
            Info("swap skipped syncId=" + syncId + " - " + reason);
        }
        public override void SendChunkFailed(ushort syncId, int chunkIndex, int offset, Exception e) {
            Totals.Add("send.chunkFailed");
            base.SendChunkFailed(syncId, chunkIndex, offset, e);
        }
        public override void RecvStalled(ushort syncId, int resendFromOffset) {
            Totals.Add("recv.resendRequests");
            base.RecvStalled(syncId, resendFromOffset);
        }
        public override void RecvDeadlineHit(ushort syncId) {
            Totals.Add("recv.deadlineHits");
            base.RecvDeadlineHit(syncId);
        }
        public override void TransferFrom(ushort sender, ushort syncId, int totalBytes, string ext) {
            Totals.AddSender(sender, totalBytes, false);
            Info("transfer from sender=" + sender + " syncId=" + syncId + " ext=" + ext + " bytes=" + totalBytes);
        }
        public override void TransferRejected(ushort sender, ushort syncId, string reason, int totalBytes) {
            Totals.Add("reject." + reason);
            Totals.AddSender(sender, 0, true);
            Warn("transfer rejected sender=" + sender + " syncId=" + syncId + " reason=" + reason + " bytes=" + totalBytes);
        }
        public override void TransferSuperseded(ushort sender, ushort syncId, string reason) {
            Totals.Add("recv.superseded");
            Info("transfer superseded sender=" + sender + " syncId=" + syncId + " - " + reason);
        }
        public override void ActiveTransfers(int count, long bufferedBytes) {
            Totals.Peak("peak.transfers", count);
            Totals.Peak("peak.bufferedBytes", bufferedBytes);
        }
        public override void FullDecodeFailed(ushort syncId, string error) {
            Totals.Add("decode.fullFailed");
            Warn("full decode failed syncId=" + syncId + " : " + error);
        }
        public override void PreviewFailed(ushort syncId, string error) {
            Totals.Add("preview.failed");
            Warn("preview decode failed syncId=" + syncId + " : " + error);
        }
        public override void ClipFreed(ushort syncId, string why) {
            Totals.Add("clips.freed");
            Info("clip freed syncId=" + syncId + " - " + why);
        }
        public override void TempSwept(int count, long bytes) {
            Totals.Add("temp.files", count);
            Totals.Add("temp.bytes", bytes);
            Info("temp sweep removed " + count + " file(s), " + (bytes / 1024) + " KB");
        }
        public override void OutdatedClient(ushort sender, byte protocol) {
            Totals.Add("alerts.outdatedClients");
            Info("outdated client sender=" + sender + " protocol=" + protocol);
        }
        public override void OpusFailed(string stage, string detail) {
            Totals.Add("alerts.opusFailures");
            Warn("opus failure [" + stage + "] " + detail);
        }
        public override void SendRejected(ushort syncId, byte reason, int maxBytes) {
            Totals.Add("send.rejectedByHost");
            Warn("host rejected our transfer syncId=" + syncId + " reason=" + reason + " hostMaxBytes=" + maxBytes);
        }
        public override void SendBlockedBySize(ushort syncId, long length, long limit) {
            Totals.Add("send.blockedBySize");
            Warn("send blocked by size syncId=" + syncId + " file=" + length + " limit=" + limit);
        }
        public override void SendAborted(ushort syncId, string reason) {
            Totals.Add("send.aborted");
            Warn("send aborted syncId=" + syncId + " - " + reason);
        }
        public override void SendThrottled(ushort syncId, string reason) {
            Totals.Add("send.throttled." + reason);
        }
        public override void SessionStarted() {
            Totals.ResetSession();
            Info("session started");
        }
        public override void SessionEnded(string reason) {
            Totals.Dump(Info, "session ended (" + reason + ")", true);
        }
        public override void TotalsSummary(string reason) {
            Totals.Dump(Info, reason, true);
        }
        public override void Trace(string stage, string detail) {
            Info("trace [" + stage + "] " + detail);
        }
        public override void UIState(string stage, string detail) {
            Info("ui [" + stage + "] " + detail);
        }
        public override void NetState(string stage, string detail) {
            Info("net [" + stage + "] " + detail);
        }
        public override void LogItemComponents(object item) {
            var comp = item as Component;
            if (comp == null) {
                Warn("item is not a Component, cannot enumerate - type=" + (item != null ? item.GetType().FullName : "null"));
                return;
            }
            var all = comp.GetComponents<Component>();
            var names = new string[all.Length];
            for (int i = 0; i < all.Length; i++) names[i] = all[i] != null ? all[i].GetType().FullName : "null";
            Info("item components after play: [" + string.Join(", ", names) + "]");
        }
        public override void ErrorCaught(string stage, string detail) {
            Error("error caught [" + stage + "] " + detail);
        }
        public override void PatchedMethodsSummary(List<MethodBase> methods) {
            Trace("Awake", "Harmony patched " + methods.Count + " method(s):");
            foreach (var m in methods) {
                Trace("Awake", "  " + (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name);
            }
            OpusFileCheck();
        }
        private void OpusFileCheck() {
            try {
                string data = Application.dataPath;
                string root = Directory.GetParent(data).FullName;
                var dirs = new[] { root, data, Path.Combine(data, "Plugins"), Path.Combine(data, Path.Combine("Plugins", "x86_64")), BepInEx.Paths.PluginPath, Path.Combine(BepInEx.Paths.PluginPath, "KrokMP"), Path.Combine(BepInEx.Paths.PluginPath, "Together") };
                var found = new List<string>();
                foreach (var d in dirs) {
                    foreach (var n in new[] { "opus.dll", "libopus.dll" }) {
                        string f = Path.Combine(d, n);
                        if (File.Exists(f)) found.Add(f + " [" + OpusNative.Describe(f) + "]");
                    }
                }
                Trace("OpusFileCheck", "cwd=" + Directory.GetCurrentDirectory() + " PATH mentions KrokMP/Together=" + (((Environment.GetEnvironmentVariable("PATH") ?? "").IndexOf("KrokMP", StringComparison.OrdinalIgnoreCase) >= 0) || ((Environment.GetEnvironmentVariable("PATH") ?? "").IndexOf("Together", StringComparison.OrdinalIgnoreCase) >= 0)));
                Trace("OpusFileCheck", "process bits=" + (IntPtr.Size * 8) + " native opus files found: " + (found.Count == 0 ? "NONE in game root, _Data, Plugins, Plugins/x86_64, BepInEx/plugins/KrokMP or BepInEx/plugins/Together - OpusSharp cannot encode or decode" : string.Join("; ", found.ToArray())));
            } catch (Exception e) {
                Warn("OpusFileCheck failed: " + e.Message);
            }
        }
        public override void StateDump(bool networkRunning, bool isServer, bool isClient, bool autoLoadMusic, bool autoRequestMusic, float uiVerticalOffset, bool uiOnTop, bool coloredButtons, string hostPluginStatus, bool pendingActive, string pendingRequester, string customMusicFolder, int customMusicSongCount, int gatedUIControlsCount, bool libDropdownBuilt, bool deviceFolderLoadTriggered, bool wasNetworkRunning) {
            Trace("DumpState", "--- MP3Client state dump ---");
            Trace("DumpState", "network_system_is_running=" + networkRunning + " is_server=" + isServer + " is_client=" + isClient);
            Trace("DumpState", "config AutoLoadMusic=" + autoLoadMusic + " AutoRequestMusic=" + autoRequestMusic + " UIVerticalOffset=" + uiVerticalOffset + " UIOnTop=" + uiOnTop + " ColoredButtons=" + coloredButtons);
            Trace("DumpState", "host plugin check status=" + hostPluginStatus);
            Trace("DumpState", "pending request active=" + pendingActive + " requester=" + pendingRequester);
            Trace("DumpState", "custommusic folder=" + customMusicFolder + " songs=" + customMusicSongCount);
            Trace("DumpState", "gated UI controls tracked=" + gatedUIControlsCount + " libDropdown built=" + libDropdownBuilt);
            Trace("DumpState", "deviceFolderLoadTriggered=" + deviceFolderLoadTriggered + " wasNetworkRunning=" + wasNetworkRunning);
            Trace("DumpState", "--- end state dump ---");
        }
    }
    internal static class OpusNative {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
        private static List<string> ReadImports(string path) {
            var names = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                var br = new BinaryReader(fs);
                fs.Seek(0x3C, SeekOrigin.Begin);
                int pe = br.ReadInt32();
                fs.Seek(pe + 6, SeekOrigin.Begin);
                int sectionCount = br.ReadUInt16();
                fs.Seek(pe + 20, SeekOrigin.Begin);
                int optSize = br.ReadUInt16();
                int opt = pe + 24;
                fs.Seek(opt, SeekOrigin.Begin);
                ushort magic = br.ReadUInt16();
                fs.Seek(opt + (magic == 0x20B ? 112 : 96) + 8, SeekOrigin.Begin);
                uint importRva = br.ReadUInt32();
                if (importRva == 0) return names;
                var vsize = new uint[sectionCount];
                var va = new uint[sectionCount];
                var rawSize = new uint[sectionCount];
                var rawPtr = new uint[sectionCount];
                for (int i = 0; i < sectionCount; i++) {
                    fs.Seek(opt + optSize + i * 40 + 8, SeekOrigin.Begin);
                    vsize[i] = br.ReadUInt32();
                    va[i] = br.ReadUInt32();
                    rawSize[i] = br.ReadUInt32();
                    rawPtr[i] = br.ReadUInt32();
                }
                Func<uint, long> toOffset = rva => {
                    for (int i = 0; i < sectionCount; i++) {
                        if (rva >= va[i] && rva < va[i] + Math.Max(vsize[i], rawSize[i])) return rawPtr[i] + (rva - va[i]);
                    }
                    return -1;
                };
                long table = toOffset(importRva);
                if (table < 0) return names;
                for (int i = 0; i < 256; i++) {
                    fs.Seek(table + i * 20 + 12, SeekOrigin.Begin);
                    uint nameRva = br.ReadUInt32();
                    if (nameRva == 0) break;
                    long nameOffset = toOffset(nameRva);
                    if (nameOffset < 0) break;
                    fs.Seek(nameOffset, SeekOrigin.Begin);
                    var sb = new System.Text.StringBuilder();
                    int c;
                    while ((c = fs.ReadByte()) > 0) sb.Append((char)c);
                    names.Add(sb.ToString());
                }
            }
            return names;
        }
        private static string FindMissing(string dllPath, List<string> imports) {
            var dirs = new List<string> { Path.GetDirectoryName(dllPath), Environment.SystemDirectory, Environment.GetEnvironmentVariable("windir") ?? "", Directory.GetParent(Application.dataPath).FullName };
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
                if (!string.IsNullOrEmpty(p)) dirs.Add(p);
            }
            var missing = new List<string>();
            foreach (var name in imports) {
                string lower = name.ToLowerInvariant();
                if (lower.StartsWith("api-ms-win-") || lower.StartsWith("ext-ms-win-")) continue;
                bool found = false;
                foreach (var d in dirs) {
                    try {
                        if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, name))) {
                            found = true;
                            break;
                        }
                    } catch (Exception) { }
                }
                if (!found) missing.Add(name);
            }
            return string.Join(",", missing.ToArray());
        }
        internal static string Describe(string path) {
            var sb = new System.Text.StringBuilder();
            try {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    var br = new BinaryReader(fs);
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    int pe = br.ReadInt32();
                    fs.Seek(pe + 4, SeekOrigin.Begin);
                    ushort machine = br.ReadUInt16();
                    sb.Append("size=" + fs.Length + " machine=0x" + machine.ToString("X") + (machine == 0x8664 ? " x64" : machine == 0x14C ? " x86" : " other"));
                }
            } catch (Exception e) {
                sb.Append("pe read failed: " + e.Message);
            }
            try {
                var imports = ReadImports(path);
                sb.Append(" imports=[" + string.Join(",", imports.ToArray()) + "] missing=[" + FindMissing(path, imports) + "]");
            } catch (Exception e) {
                sb.Append(" import scan failed: " + e.Message);
            }
            try {
                IntPtr h = LoadLibraryW(path);
                if (h == IntPtr.Zero) {
                    sb.Append(" LoadLibrary FAILED win32=" + Marshal.GetLastWin32Error());
                } else {
                    sb.Append(" LoadLibrary ok, unloaded again");
                    FreeLibrary(h);
                }
            } catch (Exception e) {
                sb.Append(" LoadLibrary probe failed: " + e.Message);
            }
            return sb.ToString();
        }
    }
    internal static class Totals {
        private static readonly Dictionary<string, long> overall = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> session = new Dictionary<string, long>();
        private static readonly Dictionary<ushort, long[]> overallSenders = new Dictionary<ushort, long[]>();
        private static readonly Dictionary<ushort, long[]> sessionSenders = new Dictionary<ushort, long[]>();
        private static float sessionStartedAt;
        internal static void Add(string key, long amount = 1) {
            Bump(overall, key, amount);
            Bump(session, key, amount);
        }
        internal static void Peak(string key, long value) {
            if (!overall.ContainsKey(key) || overall[key] < value) overall[key] = value;
            if (!session.ContainsKey(key) || session[key] < value) session[key] = value;
        }
        private static void Bump(Dictionary<string, long> d, string key, long amount) {
            long n;
            d.TryGetValue(key, out n);
            d[key] = n + amount;
        }
        internal static void AddSender(ushort sender, long bytes, bool rejected) {
            Bump(overallSenders, sender, bytes, rejected);
            Bump(sessionSenders, sender, bytes, rejected);
        }
        private static void Bump(Dictionary<ushort, long[]> d, ushort sender, long bytes, bool rejected) {
            long[] row;
            if (!d.TryGetValue(sender, out row)) {
                row = new long[3];
                d[sender] = row;
            }
            if (rejected) {
                row[2]++;
            } else {
                row[0]++;
                row[1] += bytes;
            }
        }
        internal static void ResetSession() {
            session.Clear();
            sessionSenders.Clear();
            sessionStartedAt = Time.unscaledTime;
        }
        private static long Get(Dictionary<string, long> d, string key) {
            long n;
            return d.TryGetValue(key, out n) ? n : 0;
        }
        private static string Mb(long bytes) {
            return (bytes / 1048576f).ToString("F1") + " MB";
        }
        private static void Block(Action<string> log, string title, Dictionary<string, long> d, Dictionary<ushort, long[]> senders) {
            long sendMs = Get(d, "send.ms");
            long sendBytes = Get(d, "send.bytes");
            string rate = sendMs > 0 ? ((sendBytes / 1024f) / (sendMs / 1000f)).ToString("F0") + " KB/s avg" : "n/a";
            log("totals " + title + " | sends: started=" + Get(d, "send.started") + " completed=" + Get(d, "send.completed") + " aborted=" + Get(d, "send.aborted") + " rejectedByHost=" + Get(d, "send.rejectedByHost") + " blockedBySize=" + Get(d, "send.blockedBySize") + " throttled(window/steam/rate)=" + Get(d, "send.throttled.window") + "/" + Get(d, "send.throttled.steam") + "/" + Get(d, "send.throttled.rate") + " stalls=" + Get(d, "send.stalls") + " chunkFailures=" + Get(d, "send.chunkFailed") + " sent=" + Mb(sendBytes) + " rate=" + rate);
            log("totals " + title + " | receives: begun=" + Get(d, "recv.begun") + " played=" + Get(d, "recv.played") + " cancelled=" + Get(d, "recv.cancelled") + " superseded=" + Get(d, "recv.superseded") + " resendRequests=" + Get(d, "recv.resendRequests") + " deadlineHits=" + Get(d, "recv.deadlineHits") + " received=" + Mb(Get(d, "recv.bytes")));
            log("totals " + title + " | rejects: tooLarge=" + Get(d, "reject.too large") + " busy=" + Get(d, "reject.busy") + " invalid=" + Get(d, "reject.invalid"));
            log("totals " + title + " | decode: previewStarted=" + Get(d, "preview.started") + " previewPlaying=" + Get(d, "preview.playing") + " previewDropped=" + Get(d, "preview.dropped") + " previewFailed=" + Get(d, "preview.failed") + " swapDone=" + Get(d, "swap.done") + " swapSkipped=" + Get(d, "swap.skipped") + " fullFailed=" + Get(d, "decode.fullFailed") + " replaced=" + Get(d, "decode.replaced") + " dropped=" + Get(d, "decode.dropped"));
            log("totals " + title + " | cleanup: clipsFreed=" + Get(d, "clips.freed") + " tempFiles=" + Get(d, "temp.files") + " tempBytes=" + Mb(Get(d, "temp.bytes")) + " | alerts: outdatedClients=" + Get(d, "alerts.outdatedClients") + " opusFailures=" + Get(d, "alerts.opusFailures") + " | peaks: transfers=" + Get(d, "peak.transfers") + " buffered=" + Mb(Get(d, "peak.bufferedBytes")));
            if (senders.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in senders) sb.Append(" [sender " + kv.Key + ": transfers=" + kv.Value[0] + " bytes=" + Mb(kv.Value[1]) + " rejected=" + kv.Value[2] + "]");
            log("totals " + title + " | per sender:" + sb);
        }
        internal static void Dump(Action<string> log, string reason, bool includeSession) {
            log("totals --- " + reason + " ---");
            if (includeSession) Block(log, "SESSION (" + (Time.unscaledTime - sessionStartedAt).ToString("F0") + "s)", session, sessionSenders);
            Block(log, "OVERALL (since game launch)", overall, overallSenders);
            log("totals --- end ---");
        }
    }
    internal static class MP3ClientCommands {
        internal static void RegisterAll() {
            Con.RegisterCommand(new Command("MP3stop", "MP3Client - stop your current mp3 transfer/playback.", delegate (string[] splited) {
                if (!Net.IsClient) return;
                LocalLibraryUI.StopOrCancel();
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3load", "MP3Client - CLIENT ONLY - ask the host to load their music now.", delegate (string[] splited) {
                if (!Net.IsClient || Net.IsServer) return;
                MP3TogetherDebug.SendPendingRequest(false);
                Multiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3load - sent pending request to host.");
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3accept", "MP3Client - HOST ONLY - approve the pending music-load request.", delegate (string[] splited) {
                if (!Net.IsServer) return;
                Patch_RegisterServerReceivers.AcceptPendingRequest();
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3reject", "MP3Client - HOST ONLY - deny the pending music-load request.", delegate (string[] splited) {
                if (!Net.IsServer) return;
                Patch_RegisterServerReceivers.RejectPendingRequest();
            }, null, Array.Empty<ValueTuple<string, string>>()));
            if (!MP3TogetherDebug.DebugEnabled) return;
            Con.RegisterCommand(new Command("MP3audio", "MP3Client - dump client-side stream audio state (volumes, mutes, mixers, buffers) to the log.", delegate (string[] splited) {
                StreamProbeState.DumpClientAudio("MP3audio");
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3total", "MP3Client - print session and overall totals (transfers, bytes, previews, swaps, rejects, alerts) to the log.", delegate (string[] splited) {
                MP3TogetherDebug.Debug.TotalsSummary("MP3total command");
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3autoupdate", "MP3Client - turn auto-update on or off. Usage: MP3autoupdate on, MP3autoupdate off, or no argument to flip it.", delegate (string[] splited) {
                bool enable = !Updater.AutoEnabled;
                if (splited != null && splited.Length > 1) {
                    string arg = splited[1].ToLowerInvariant();
                    if (arg == "on" || arg == "true" || arg == "1") enable = true;
                    else if (arg == "off" || arg == "false" || arg == "0") enable = false;
                }
                Multiplayer.DoMultiplayerStatusMessageLog("mp3client: " + Updater.SetAuto(enable));
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3revert", "MP3Client - go back to your original version. MP3revert last goes back to the last version that started fine. Auto-update turns off.", delegate (string[] splited) {
                bool last = splited != null && splited.Length > 1 && splited[1].ToLowerInvariant() == "last";
                Updater.Revert(MP3TogetherDebug.Log, last);
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3state", "MP3Client - dump full plugin state to the log for debugging.", delegate (string[] splited) {
                MP3TogetherDebug.DumpState();
            }, null, Array.Empty<ValueTuple<string, string>>()));
        }
    }
    internal static class StreamProbeState {
        internal static int encodeCount;
        internal static int encodeEmpty;
        internal static int encodeCalls;
        internal static float nextErrorLog;
        internal static int clientPackets;
        internal static float nextServerLog;
        internal static float nextClientLog;
        internal static float nextClientDump;
        internal static object Member(object target, Type type, string name) {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var p = type.GetProperty(name, flags);
            if (p != null) return p.GetValue(target, null);
            var f = type.GetField(name, flags);
            return f != null ? f.GetValue(target) : null;
        }
        internal static void DumpClientAudio(string stage) {
            try {
                var sb = new System.Text.StringBuilder();
                sb.Append("audioListenerVol=" + AudioListener.volume + " audioListenerPause=" + AudioListener.pause);
                sb.Append(" mp3ListenerVol=" + Static("Together.Voicechat", "mp3_listener_volume"));
                sb.Append(" generating=" + Static("Together.SharedMain", "LocalWorldIsGenerating"));
                Vector3 cam = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                var all = UnityEngine.Object.FindObjectsOfType<MP3PlayerAudioStreamPlayer_ClientOnly>();
                sb.Append(" clientPlayers=" + all.Length);
                foreach (var p in all) {
                    var src = Member(p, typeof(StreamedAudioOutput), "audioSource") as AudioSource;
                    sb.Append(" [" + p.gameObject.name + " dist=" + Vector3.Distance(cam, p.transform.position).ToString("F1"));
                    sb.Append(" enabled=" + p.enabled + " active=" + p.gameObject.activeInHierarchy);
                    if (src != null) {
                        sb.Append(" src.enabled=" + src.enabled + " mute=" + src.mute + " vol=" + src.volume + " playing=" + src.isPlaying + " blend=" + src.spatialBlend + " min=" + src.minDistance + " max=" + src.maxDistance + " mixer=" + (src.outputAudioMixerGroup != null ? src.outputAudioMixerGroup.name : "none") + " bypass=" + src.bypassEffects + " clip=" + (src.clip != null ? src.clip.frequency + "Hz/" + src.clip.samples : "null"));
                    } else {
                        sb.Append(" src=null");
                    }
                    var buf = Member(p, typeof(StreamedAudioOutput), "vc_buffer") as ICollection;
                    sb.Append(" vcBuffer=" + (buf != null ? buf.Count.ToString() : "n/a") + " buffered=" + Member(p, typeof(StreamedAudioOutput), "vc_buffered_amount") + " finalVol=" + p.GetFinalVolume() + "]");
                }
                MP3TogetherDebug.Debug.NetState(stage, sb.ToString());
            } catch (Exception e) {
                MP3TogetherDebug.Debug.NetState(stage, "audio dump failed: " + e);
            }
        }
        internal static void LogError(string stage, Exception e) {
            if (e == null || Time.unscaledTime < nextErrorLog) return;
            nextErrorLog = Time.unscaledTime + 1f;
            MP3TogetherDebug.Debug.NetState("StreamProbe.Exception", stage + " threw " + e);
        }
        internal static object Static(string typeName, string name) {
            var t = AccessTools.TypeByName(typeName);
            return t != null ? Member(null, t, name) : null;
        }
    }
    [HarmonyPatch(typeof(MP3PlayerServerAudioStreamer), "LateUpdate")]
    internal static class Patch_DebugServerStreamProbe {
        static bool Prepare() {
            return MP3TogetherDebug.DebugEnabled;
        }
        static void Prefix(MP3PlayerServerAudioStreamer __instance) {
            if (Time.unscaledTime < StreamProbeState.nextServerLog) return;
            try {
                var st = typeof(MP3PlayerServerAudioStreamer);
                var src = AccessTools.Field(st, "musicplayersource").GetValue(__instance) as AudioSource;
                if (src == null || !src.isPlaying) return;
                StreamProbeState.nextServerLog = Time.unscaledTime + 1f;
                var queued = AccessTools.Field(st, "queued_resampled_samples").GetValue(__instance) as ICollection;
                var rate = AccessTools.Field(st, "target_samplerate").GetValue(__instance);
                var outSrc = StreamProbeState.Member(__instance, typeof(StreamedAudioOutput), "audioSource") as AudioSource;
                float radius = outSrc != null ? outSrc.maxDistance : -1f;
                Vector3 p = __instance.transform.position;
                var np = AccessTools.TypeByName("Together.ScavPlayer");
                var dict = np != null ? AccessTools.Field(np, "ClientIdToPlayerDict").GetValue(null) as IDictionary : null;
                var sb = new System.Text.StringBuilder();
                if (dict != null) {
                    foreach (var pl in dict.Values) {
                        object id = StreamProbeState.Member(pl, np, "playerId");
                        object pos = StreamProbeState.Member(pl, np, "pos");
                        float d = pos is Vector2 ? Vector2.Distance((Vector2)pos, new Vector2(p.x, p.y)) : -1f;
                        sb.Append(" [id=" + id + " dist=" + d.ToString("F1") + "]");
                    }
                }
                int inRadius = -1;
                object localId = null;
                try {
                    var gpr = np != null ? AccessTools.Method(np, "GetPlayersInRadius", new[] { typeof(Vector2), typeof(float) }) : null;
                    var found = gpr != null ? gpr.Invoke(null, new object[] { new Vector2(p.x, p.y), radius }) as IList : null;
                    var local = np != null ? AccessTools.Field(np, "LocalPlayer").GetValue(null) : null;
                    if (found != null) {
                        inRadius = found.Count;
                        if (local != null && found.Contains(local)) inRadius--;
                    }
                    localId = local != null ? StreamProbeState.Member(local, np, "playerId") : null;
                } catch (Exception) { }
                SyncInfo si = null;
                var tryGet = AccessTools.Method(typeof(NetObjectRegistry), "TryGetSyncInfo", new[] { typeof(GameObject), typeof(SyncInfo).MakeByRefType() });
                var args = new object[] { __instance.gameObject, null };
                bool registered = tryGet != null && (bool)tryGet.Invoke(null, args);
                MP3TogetherDebug.Debug.NetState("StreamProbe.Server", "playing=true generating=" + StreamProbeState.Static("Together.SharedMain", "LocalWorldIsGenerating") + " enableMP3Sync=" + Multiplayer.rules.EnableMP3Sync + " condition=" + __instance.item.condition + " mp3ListenerVol=" + StreamProbeState.Static("Together.Voicechat", "mp3_listener_volume") + " audioListenerVol=" + AudioListener.volume + " pcmBuf=" + __instance.pcm_buffer_from_the_filter_thing.Count + " queued=" + (queued != null ? queued.Count : -1) + " rate=" + rate + " radius=" + radius + " syncRegistered=" + registered + " encodes=" + StreamProbeState.encodeCount + " encodeEmpty=" + StreamProbeState.encodeEmpty + " encodeCalls=" + StreamProbeState.encodeCalls + " inRadiusNonLocal=" + inRadius + " localId=" + localId + " players:" + sb);
            } catch (Exception e) {
                MP3TogetherDebug.Debug.NetState("StreamProbe.Server", "probe failed: " + e);
            }
        }
    }
    [HarmonyPatch(typeof(MP3PlayerServerAudioStreamer), "LateUpdate")]
    internal static class Patch_DebugServerLateUpdateErrors {
        static bool Prepare() {
            return MP3TogetherDebug.DebugEnabled;
        }
        static Exception Finalizer(Exception __exception) {
            StreamProbeState.LogError("LateUpdate", __exception);
            return __exception;
        }
    }
    [HarmonyPatch]
    internal static class Patch_DebugEncodeProbe {
        static bool Prepare() {
            return MP3TogetherDebug.DebugEnabled;
        }
        static MethodBase TargetMethod() {
            return AccessTools.Method("Together.AudioOpusEncoding:Encode");
        }
        static void Prefix() {
            StreamProbeState.encodeCalls++;
        }
        static Exception Finalizer(Exception __exception) {
            StreamProbeState.LogError("Encode", __exception);
            return __exception;
        }
        static void Postfix(byte[] __result) {
            StreamProbeState.encodeCount++;
            if (__result == null || __result.Length == 0) StreamProbeState.encodeEmpty++;
        }
    }
    [HarmonyPatch]
    internal static class Patch_DebugClientStreamProbe {
        static bool Prepare() {
            return MP3TogetherDebug.DebugEnabled;
        }
        static MethodBase TargetMethod() {
            return AccessTools.Method(typeof(MP3PlayerServerAudioStreamer), "Client_StreamedAudioToClients");
        }
        static void Prefix() {
            StreamProbeState.clientPackets++;
            if (Time.unscaledTime < StreamProbeState.nextClientLog) return;
            StreamProbeState.nextClientLog = Time.unscaledTime + 1f;
            MP3TogetherDebug.Debug.NetState("StreamProbe.Client", "10138 packets received so far=" + StreamProbeState.clientPackets);
        }
        static void Postfix() {
            if (Time.unscaledTime < StreamProbeState.nextClientDump) return;
            StreamProbeState.nextClientDump = Time.unscaledTime + 1f;
            StreamProbeState.DumpClientAudio("StreamProbe.ClientAudio");
        }
    }
}