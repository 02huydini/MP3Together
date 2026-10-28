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

namespace MP3Together {
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
                Chat.Server_ChatAnnouncement("MP3Together", "", msg, new List<knetid> { target });
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: chat message failed: " + e.Message);
            }
        }
        internal static void AlertTo(knetid target, string msg) {
            try {
                ServerMain.Server_AnnounceAlert(msg, true, true, new List<knetid> { target });
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: alert failed: " + e.Message);
            }
        }
        internal static void LogToHostChat(string msg) {
            try {
                Chat.LogMessage("MP3Together", msg, false);
            } catch (Exception) { }
        }
        internal static void BroadcastLog(string msg, bool isError) {
            try {
                ServerMain.Server_SendMPLogMessage(msg, isError, null);
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: broadcast failed: " + e.Message);
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
        private static string Versions() {
            return "client v" + UpdaterConfig.PLUGIN_VERSION + ", host protocol " + HostPluginCheck.HostProtocol;
        }
        internal static void Capture(string stage, Exception e) {
            string detail = e.GetType().Name + ": " + e.Message + Environment.NewLine + e.StackTrace;
            MP3TogetherPlugin.Log.LogError("mp3together: " + stage + " failed [" + Versions() + "]: " + e);
            MP3TogetherPlugin.Debug.ErrorCaught(stage, detail);
        }
    }
    internal static class MissingFiles {
        private static readonly string[] Icons = { "BrowseIcon.png", "PauseIcon.png", "StopIcon.png" };
        private static readonly string[] GameTypes = { "MP3Menu", "MusicManager", "ConsoleScript" };
        private static readonly string[] KrokTypes = { "Together.UIChoicePrompt", "Together.ServerMain", "Together.ClientMain", "Together.ScavPlayer", "Together.MP3PlayerServerAudioStreamer" };
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
            if (Krok.Installed) {
                foreach (string typeName in KrokTypes) {
                    if (AccessTools.TypeByName(typeName) == null) missing.Add("CasualtiesMP type " + typeName);
                }
            }
            return missing;
        }
        internal static void CheckStartup() {
            var missing = Scan();
            if (missing.Count == 0) return;
            string detail = string.Join("; ", missing.ToArray());
            MP3TogetherPlugin.Log.LogError("mp3together: missing files or types, parts of the plugin won't work: " + detail);
            MP3TogetherPlugin.Debug.ErrorCaught("missing files", detail);
        }
        internal static void NoteMissingResource(string resourceFileName) {
            string detail = "embedded resource " + resourceFileName + " (mark it as an Embedded Resource in the project)";
            MP3TogetherPlugin.Log.LogError("mp3together: icon missing: " + detail);
            MP3TogetherPlugin.Debug.ErrorCaught("missing files", detail);
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
                    MP3TogetherPlugin.Debug.Trace("OpusPreflight", preflightDetail);
                    return;
                }
                FreeLibrary(module);
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: Opus startup check failed to run: " + e.Message);
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
                MP3TogetherPlugin.Log.LogError("mp3together: " + msg);
                MP3TogetherPlugin.Debug.OpusFailed(stage, detail);
                Msg.Error("mp3together: " + msg);
                Notify.LogToHostChat(msg);
                if (broadcast && Net.IsServer) {
                    Notify.BroadcastLog("[MP3Together] The host can't stream audio because the Opus library failed to load on the host PC. Music from the host won't be heard until the host fixes it.", true);
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