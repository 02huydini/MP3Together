using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Together;

namespace MP3Client {
    internal static class Notify {
        internal static string DescribePlayer(knetid id) {
            try {
                ScavPlayer player;
                if (ScavPlayer.TryGetPlayerFromClientId(id, out player) && player != null && !string.IsNullOrEmpty(player.playerName)) return player.playerName + " (id " + id + ")";
            } catch (Exception) { }
            return "client " + id;
        }
        internal static void ChatTo(knetid target, string msg) {
            try {
                Chat.Server_ChatAnnouncement("MP3Client", "", msg, new List<knetid> { target });
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: chat message failed: " + e.Message);
            }
        }
        internal static void AlertTo(knetid target, string msg) {
            try {
                ServerMain.Server_AnnounceAlert(msg, true, true, new List<knetid> { target });
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: alert failed: " + e.Message);
            }
        }
        internal static void LogToHostChat(string msg) {
            try {
                Chat.LogMessage("MP3Client", msg, false);
            } catch (Exception) { }
        }
        internal static void BroadcastLog(string msg, bool isError) {
            try {
                ServerMain.Server_SendMPLogMessage(msg, isError, null);
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: broadcast failed: " + e.Message);
            }
        }
    }
    internal static class Guard {
        private const float SAME_ERROR_COOLDOWN_SECONDS = 10f;
        private const int MAX_TRACKED = 64;
        private static readonly Dictionary<string, float> lastSeen = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> suppressed = new Dictionary<string, int>();
        internal static void Run(string stage, Action action) {
            try {
                action();
            } catch (Exception e) {
                Report(stage, e);
            }
        }
        internal static T Run<T>(string stage, Func<T> func, T fallback) {
            try {
                return func();
            } catch (Exception e) {
                Report(stage, e);
                return fallback;
            }
        }
        internal static Action<NetDataReader> ClientHandler(string stage, Action<knetid, NetDataReader> inner) {
            return delegate (NetDataReader reader) {
                try {
                    inner(0, reader);
                } catch (Exception e) {
                    Report(stage, e);
                }
            };
        }
        internal static Action<ScavPlayer, NetDataReader> ServerHandler(string stage, Action<knetid, NetDataReader> inner) {
            return delegate (ScavPlayer player, NetDataReader reader) {
                try {
                    inner(player.playerId, reader);
                } catch (Exception e) {
                    Report(stage, e);
                }
            };
        }
        internal static void Report(string stage, Exception e) {
            try {
                float now = Time.realtimeSinceStartup;
                string key = stage + "|" + e.GetType().Name + "|" + e.Message;
                float last;
                if (lastSeen.TryGetValue(key, out last) && now - last < SAME_ERROR_COOLDOWN_SECONDS) {
                    int n;
                    suppressed.TryGetValue(key, out n);
                    suppressed[key] = n + 1;
                    return;
                }
                if (lastSeen.Count >= MAX_TRACKED) {
                    lastSeen.Clear();
                    suppressed.Clear();
                }
                int repeats;
                suppressed.TryGetValue(key, out repeats);
                suppressed.Remove(key);
                lastSeen[key] = now;
                CrashLog.Capture(repeats > 0 ? stage + " (" + repeats + " identical errors skipped before this one)" : stage, e);
            } catch (Exception) {
            }
        }
    }
    internal static class CrashLog {
        private const string FILE_NAME = "MP3Client_CrashLog.log";
        private const long MAX_BYTES = 1048576L;
        private const int TAIL_LINES = 80;
        private const long TAIL_READ_BYTES = 524288L;
        private const float TAIL_COOLDOWN_SECONDS = 5f;
        private static float lastTail = -1000f;
        internal static string FilePath { get { return Path.Combine(Paths.PluginPath, FILE_NAME); } }
        internal static void WriteLocalErrorLog(string stage, string detail) {
            try {
                string path = FilePath;
                string versions = "Client v" + UpdaterConfig.PLUGIN_VERSION + " / Host Protocol " + HostPluginCheck.HostProtocol;
                string entry = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + versions + "] [" + stage + "] " + detail + Environment.NewLine;
                if (File.Exists(path) && new FileInfo(path).Length > MAX_BYTES) File.Delete(path);
                string tail = "";
                if (Time.unscaledTime - lastTail >= TAIL_COOLDOWN_SECONDS) {
                    lastTail = Time.unscaledTime;
                    tail = LogTail();
                }
                File.AppendAllText(path, entry + tail);
            } catch (Exception) {
            }
        }
        internal static void Capture(string stage, Exception e) {
            string detail = e.GetType().Name + ": " + e.Message + Environment.NewLine + e.StackTrace;
            MP3TogetherDebug.Log.LogError("mp3client: " + stage + " failed: " + e);
            MP3TogetherDebug.Debug.ErrorCaught(stage, detail);
            WriteLocalErrorLog(stage, detail);
        }
        private static string LogTail() {
            try {
                string log = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                if (!File.Exists(log)) return "";
                var tail = new Queue<string>();
                using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream)) {
                    if (stream.Length > TAIL_READ_BYTES) {
                        stream.Seek(-TAIL_READ_BYTES, SeekOrigin.End);
                        reader.DiscardBufferedData();
                        reader.ReadLine();
                    }
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        if (line.IndexOf("mp3client", StringComparison.OrdinalIgnoreCase) < 0 && line.IndexOf("[Error", StringComparison.Ordinal) < 0) continue;
                        tail.Enqueue(line);
                        if (tail.Count > TAIL_LINES) tail.Dequeue();
                    }
                }
                var sb = new StringBuilder();
                sb.AppendLine("--- LogOutput.log (MP3Client and error lines, last " + tail.Count + ") ---");
                foreach (string line in tail) sb.AppendLine(line);
                sb.AppendLine("--- end ---");
                return sb.ToString();
            } catch (Exception) {
                return "";
            }
        }
    }
    internal static class MissingFiles {
        private static readonly string[] Icons = { "BrowseIcon.png", "PauseIcon.png", "StopIcon.png" };
        private static readonly string[] GameTypes = { "MP3Menu", "Together.UIChoicePrompt", "Together.ServerMain", "Together.ClientMain", "Together.ScavPlayer", "Together.MP3PlayerServerAudioStreamer" };
        internal static List<string> Scan() {
            var missing = new List<string>();
            try {
                var names = Assembly.GetExecutingAssembly().GetManifestResourceNames();
                foreach (string icon in Icons) {
                    bool found = false;
                    foreach (string n in names) {
                        if (n.EndsWith(icon, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                    }
                    if (!found) missing.Add("embedded resource " + icon);
                }
            } catch (Exception) { }
            foreach (string typeName in GameTypes) {
                if (AccessTools.TypeByName(typeName) == null) missing.Add("game type " + typeName);
            }
            return missing;
        }
        internal static void CheckStartup() {
            var missing = Scan();
            if (missing.Count == 0) return;
            string detail = string.Join("; ", missing.ToArray());
            MP3TogetherDebug.Log.LogError("mp3client: missing files or types, parts of the plugin won't work: " + detail);
            MP3TogetherDebug.Debug.ErrorCaught("missing files", detail);
            CrashLog.WriteLocalErrorLog("missing files", detail);
        }
        internal static void NoteMissingResource(string resourceFileName) {
            string detail = "embedded resource " + resourceFileName + " (mark it as an Embedded Resource in the project)";
            MP3TogetherDebug.Log.LogError("mp3client: icon missing: " + detail);
            MP3TogetherDebug.Debug.ErrorCaught("missing files", detail);
            CrashLog.WriteLocalErrorLog("missing files", detail);
        }
    }
    internal static class OpusFailure {
        internal const string FIX_URL = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
        private const float REPORT_COOLDOWN_SECONDS = 30f;
        private static float lastReport = -1000f;
        private static bool preflightFailed;
        private static string preflightDetail = "";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
        internal static void Preflight() {
            try {
                if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) return;
                string found = null;
                foreach (var file in Directory.GetFiles(Paths.PluginPath, "opus.dll", SearchOption.AllDirectories)) {
                    found = file;
                    break;
                }
                if (found == null) return;
                IntPtr module = LoadLibraryW(found);
                if (module == IntPtr.Zero) {
                    int code = Marshal.GetLastWin32Error();
                    preflightFailed = true;
                    preflightDetail = Describe(code) + " in " + found;
                    MP3TogetherDebug.Debug.Trace("OpusPreflight", preflightDetail);
                    return;
                }
                FreeLibrary(module);
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: Opus startup check failed to run: " + e.Message);
            }
        }
        private static string Describe(int code) {
            if (code == 126) return "LoadLibrary error 126, a DLL that opus.dll needs is missing";
            if (code == 193) return "LoadLibrary error 193, opus.dll is not a valid 64 bit library for this game";
            return "LoadLibrary error " + code;
        }
        private static string Message(string stage, string detail) {
            return "Opus audio library failed (" + stage + "): " + detail + ". Music and voice can't be streamed on this PC. Install the Microsoft Visual C++ 2015-2022 Redistributable (x64) from " + FIX_URL + " and restart the game.";
        }
        internal static bool IsOpusError(Exception e) {
            for (int depth = 0; e != null && depth < 6; depth++) {
                if (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException) return true;
                string text = (e.Message ?? "") + " " + (e.StackTrace ?? "");
                if (text.IndexOf("opus", StringComparison.OrdinalIgnoreCase) >= 0 && (e is TypeInitializationException || e is TypeLoadException || e is MissingMethodException)) return true;
                e = e.InnerException;
            }
            return false;
        }
        internal static void ReportSessionStart() {
            if (preflightFailed) Report("session start", preflightDetail, Net.IsServer);
        }
        internal static void Report(string stage, string detail, bool broadcast) {
            try {
                float now = Time.unscaledTime;
                if (now - lastReport < REPORT_COOLDOWN_SECONDS) return;
                lastReport = now;
                string msg = Message(stage, detail);
                MP3TogetherDebug.Log.LogError("mp3client: " + msg);
                MP3TogetherDebug.Debug.OpusFailed(stage, detail);
                CrashLog.WriteLocalErrorLog(stage, detail);
                Multiplayer.DoMultiplayerStatusMessageError("mp3client: " + msg);
                Notify.LogToHostChat(msg);
                if (broadcast && Net.IsServer) {
                    Notify.BroadcastLog("[MP3Client] The host can't stream audio because the Opus library failed to load on the host PC. Music from the host won't be heard until the host fixes it.", true);
                }
            } catch (Exception) { }
        }
    }
    [HarmonyPatch]
    internal static class Patch_OpusEncodeFailure {
        static MethodBase TargetMethod() {
            return AccessTools.Method("Together.AudioOpusEncoding:Encode");
        }
        static Exception Finalizer(Exception __exception) {
            if (__exception != null && OpusFailure.IsOpusError(__exception)) OpusFailure.Report("encode", __exception.GetType().Name + ": " + __exception.Message, true);
            return __exception;
        }
    }
    [HarmonyPatch]
    internal static class Patch_OpusDecodeFailure {
        static MethodBase TargetMethod() {
            return AccessTools.Method("Together.StreamedAudioOutput:OpusDecode");
        }
        static Exception Finalizer(Exception __exception) {
            if (__exception != null && OpusFailure.IsOpusError(__exception)) OpusFailure.Report("decode", __exception.GetType().Name + ": " + __exception.Message, false);
            return __exception;
        }
    }
}