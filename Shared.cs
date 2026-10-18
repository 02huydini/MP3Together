using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Networking;
using Together;

namespace MP3Client {
    internal static class Protocol {
        public const string MSG_BEGIN = "MP3Client.Begin";
        public const string MSG_CHUNK = "MP3Client.Chunk";
        public const string MSG_END = "MP3Client.End";
        public const string MSG_RESEND_REQUEST = "MP3Client.ResendRequest";
        public const string MSG_CANCEL = "MP3Client.Cancel";
        public const string MSG_PLUGIN_PING = "MP3Client.PluginPing";
        public const string MSG_PLUGIN_PONG = "MP3Client.PluginPong";
        public const string MSG_ASK_LOAD = "MP3Client.AskLoad";
        public const string MSG_PAUSE = "MP3Client.Pause";
        public const string MSG_LOAD_APPROVED = "MP3Client.LoadApproved";
        public const string MSG_ACK = "MP3Client.Ack";
        public const string MSG_REJECT = "MP3Client.Reject";
        internal static NetDataWriter Writer(string name) {
            return Multiplayer.CreateNamedWriter(name);
        }
        public const byte REJECT_TOO_LARGE = 1;
        public const byte REJECT_BUSY = 2;
        public const byte REJECT_REPLACED = 3;
        public const byte REJECT_INVALID = 4;
        public const int MAX_CONCURRENT_TRANSFERS = 4;
        public const long CLIENT_ABSOLUTE_MAX_BYTES = 209715200L;
        public const int MAX_FILE_BYTES = 12582912;
        public const float TRANSFER_IDLE_SECONDS = 60f;
        public const float SWEEP_INTERVAL_SECONDS = 5f;
        public const float TEMP_FILE_MAX_AGE_SECONDS = 300f;
        public const byte VERSION = 4;
        public const float LEGACY_CHUNK_INTERVAL_SECONDS = 0.35f;
        public const float PLUGIN_CHECK_TIMEOUT_SECONDS = 3f;
        public const int CHUNK_SIZE = 32000;
        public const int PREVIEW_PERCENT = 25;
        public const int PREVIEW_MIN_TOTAL_BYTES = 256000;
        public const string RELEASES_URL = "https://github.com/02huydini/MP3Client/releases/latest";
        public const int CHUNKS_PER_FRAME = 1;
        public const int WINDOW_CHUNKS = 6;
        public const float ACK_STALL_SECONDS = 2f;
        public const float SEND_ABORT_SECONDS = 20f;
        public const int STEAM_PENDING_LIMIT_BYTES = 393216;
        public const int MAX_SEND_BYTES_PER_SECOND = 196608;
        public const int SEND_BURST_BYTES = 64000;
        public const float STALL_TIMEOUT_SECONDS = 6f;
        public const float OVERALL_DEADLINE_SECONDS = 300f;
        public const string BROWSE_LABEL = "Play your own song?";
    }
    internal static class HostPluginCheck {
        internal enum Status { Unknown, Confirmed, Missing }
        internal static Status CurrentStatus = Status.Unknown;
        internal static byte HostProtocol;
        internal static int HostMaxFileBytes;
        private static bool pingSent;
        internal static void ResetForNewSession() {
            CurrentStatus = Status.Unknown;
            HostProtocol = 0;
            HostMaxFileBytes = 0;
            pingSent = false;
        }
        private static bool TryApplyNonClientState(object libDropdown) {
            if (!Net.IsRunning) {
                MP3TogetherDebug.Debug.UIState("TryApplyNonClientState", "no active session - showing no-server message, gate controls disabled");
                LocalLibraryUI.SetDropdownNoServerText(libDropdown);
                LocalLibraryUI.SetHostGateEnabled(false);
                return true;
            }
            if (Net.IsServer) {
                MP3TogetherDebug.Debug.UIState("TryApplyNonClientState", "local player is host - showing client-only message, gate controls disabled");
                LocalLibraryUI.SetDropdownLocalIsHostText(libDropdown);
                LocalLibraryUI.SetHostGateEnabled(false);
                return true;
            }
            return false;
        }
        public static void EnsureChecked(object libDropdown) {
            if (TryApplyNonClientState(libDropdown)) return;
            if (CurrentStatus == Status.Missing) {
                LocalLibraryUI.SetDropdownHostMissingText(libDropdown, true);
                LocalLibraryUI.SetHostGateEnabled(false);
                return;
            }
            if (CurrentStatus == Status.Confirmed) return;
            LocalLibraryUI.SetDropdownHostMissingText(libDropdown, false);
            if (!pingSent) {
                pingSent = true;
                MP3TogetherDebug.Debug.NetState("HostPluginCheck", "sending MSG_PLUGIN_PING to host, awaiting pong");
                var writer = Protocol.Writer(Protocol.MSG_PLUGIN_PING);
                writer.Put(Protocol.VERSION);
                var dm = DeliveryMethod.ReliableOrdered;
                Net.Client_Send(in dm, in writer);
            }
            MP3TogetherDebug.Instance.StartCoroutine(WaitForPong(libDropdown));
        }
        private static IEnumerator WaitForPong(object libDropdown) {
            float deadline = Time.unscaledTime + Protocol.PLUGIN_CHECK_TIMEOUT_SECONDS;
            while (Time.unscaledTime < deadline) {
                if (CurrentStatus == Status.Confirmed) yield break;
                if (TryApplyNonClientState(libDropdown)) yield break;
                yield return null;
            }
            if (CurrentStatus == Status.Unknown) {
                if (TryApplyNonClientState(libDropdown)) yield break;
                CurrentStatus = Status.Missing;
                Multiplayer.DoMultiplayerStatusMessageError("mp3client: Host doesn't have this plugin - custom music can't be sent.");
                LocalLibraryUI.SetDropdownHostMissingText(libDropdown, true);
                LocalLibraryUI.SetHostGateEnabled(false);
            }
        }
        public static void OnPong() {
            MP3TogetherDebug.Debug.NetState("HostPluginCheck", "received MSG_PLUGIN_PONG - host has the plugin, enabling gate controls");
            CurrentStatus = Status.Confirmed;
            LocalLibraryUI.SetDropdownHostMissingText(LocalLibraryUI.CurrentLibDropdown, false);
            LocalLibraryUI.SetHostGateEnabled(true);
        }
    }
    internal static class SendLimiter {
        private static float tokens;
        private static float lastRefill;
        private static void Refill() {
            float now = Time.unscaledTime;
            tokens = Mathf.Min(Protocol.SEND_BURST_BYTES, tokens + Math.Max(0f, now - lastRefill) * Protocol.MAX_SEND_BYTES_PER_SECOND);
            lastRefill = now;
        }
        internal static bool TryTake(int bytes) {
            Refill();
            if (tokens < bytes) return false;
            tokens -= bytes;
            return true;
        }
    }
    internal static class SteamSendGate {
        private static MethodInfo tryGetTransport;
        private static FieldInfo statusField;
        private static FieldInfo pendingReliableField;
        private static FieldInfo sentUnackedField;
        internal static bool TryGetPendingBytes(out int pending) {
            pending = 0;
            try {
                if (tryGetTransport == null) tryGetTransport = AccessTools.Method(typeof(Net), "TryGetSteamTransport");
                if (tryGetTransport == null) return false;
                var args = new object[] { null };
                if (!(bool)tryGetTransport.Invoke(null, args) || args[0] == null) return false;
                var transport = args[0];
                if (statusField == null) statusField = AccessTools.Field(transport.GetType(), "client_status");
                if (statusField == null) return false;
                object status = statusField.GetValue(transport);
                if (status == null) return false;
                if (pendingReliableField == null) {
                    pendingReliableField = AccessTools.Field(status.GetType(), "m_cbPendingReliable");
                    sentUnackedField = AccessTools.Field(status.GetType(), "m_cbSentUnackedReliable");
                }
                if (pendingReliableField == null || sentUnackedField == null) return false;
                pending = Convert.ToInt32(pendingReliableField.GetValue(status)) + Convert.ToInt32(sentUnackedField.GetValue(status));
                return true;
            } catch (Exception) {
                return false;
            }
        }
    }
    internal static class UITypes {
        private static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();
        public static Type Get(string name) {
            Type t;
            if (cache.TryGetValue(name, out t)) return t;
            t = AccessTools.TypeByName(name);
            cache[name] = t;
            return t;
        }
        public static Type Button { get { return Get("UnityEngine.UI.Button"); } }
        public static Type Image { get { return Get("UnityEngine.UI.Image"); } }
        public static Type Text { get { return Get("UnityEngine.UI.Text"); } }
        public static Type TMPText { get { return Get("TMPro.TMP_Text") ?? Get("TMPro.TextMeshProUGUI"); } }
        public static Type LayoutElement { get { return Get("UnityEngine.UI.LayoutElement"); } }
        public static Type VerticalLayoutGroup { get { return Get("UnityEngine.UI.VerticalLayoutGroup"); } }
        public static Type HorizontalLayoutGroup { get { return Get("UnityEngine.UI.HorizontalLayoutGroup"); } }
        public static Type ContentSizeFitter { get { return Get("UnityEngine.UI.ContentSizeFitter"); } }
        public static Type LayoutRebuilder { get { return Get("UnityEngine.UI.LayoutRebuilder"); } }
        public static Type AudioSource { get { return Get("UnityEngine.AudioSource"); } }
        public static Type MP3Menu { get { return Get("MP3Menu"); } }
    }
    internal static class ReflectionHelpers {
        private static readonly Dictionary<string, MemberInfo> memberCache = new Dictionary<string, MemberInfo>();
        private static readonly Dictionary<string, MethodInfo> methodCache = new Dictionary<string, MethodInfo>();
        private static MemberInfo ResolveMember(Type t, string name) {
            string key = t.FullName + "|" + name;
            MemberInfo mi;
            if (memberCache.TryGetValue(key, out mi)) return mi;
            mi = (MemberInfo)t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            memberCache[key] = mi;
            return mi;
        }
        public static object GetMember(object target, string name) {
            var mi = ResolveMember(target.GetType(), name);
            var prop = mi as PropertyInfo;
            if (prop != null) return prop.GetValue(target);
            var field = mi as FieldInfo;
            if (field != null) return field.GetValue(target);
            return null;
        }
        public static void CallMethod(object target, string name, Type[] paramTypes, object[] args) {
            var t = target.GetType();
            string key = t.FullName + "|" + name + "|" + paramTypes.Length;
            MethodInfo m;
            if (!methodCache.TryGetValue(key, out m)) {
                m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, paramTypes, null);
                methodCache[key] = m;
            }
            if (m == null) throw new MissingMethodException(t.Name, name);
            m.Invoke(target, args);
        }
        public static void SetMember(object target, string name, object value) {
            var t = target.GetType();
            var mi = ResolveMember(t, name);
            var prop = mi as PropertyInfo;
            if (prop != null) { prop.SetValue(target, value); return; }
            var field = mi as FieldInfo;
            if (field != null) { field.SetValue(target, value); return; }
            throw new MissingMemberException(t.Name, name);
        }
    }
    internal static class CustomMusicFolder {
        public static string GetPath() {
            return Path.Combine(Application.dataPath, "custommusic");
        }
        public static void EnsureExists() {
            var dir = GetPath();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        public static List<string> Scan() {
            EnsureExists();
            var result = new List<string>();
            try {
                foreach (var f in Directory.GetFiles(GetPath())) {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".mp3" || ext == ".wav" || ext == ".ogg") result.Add(f);
                }
                result.Sort(StringComparer.OrdinalIgnoreCase);
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: failed to scan " + GetPath() + ": " + e);
            }
            return result;
        }
        public static string CopyIn(string sourcePath) {
            EnsureExists();
            var name = Path.GetFileName(sourcePath);
            var dest = Path.Combine(GetPath(), name);
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return dest;
            int i = 1;
            while (File.Exists(dest)) {
                dest = Path.Combine(GetPath(), Path.GetFileNameWithoutExtension(name) + "_" + i + Path.GetExtension(name));
                i++;
            }
            File.Copy(sourcePath, dest);
            return dest;
        }
    }
    internal static class HeadlessMusicLoader {
        internal static bool DeviceFolderLoadTriggered;
        internal static void ResetForNewSession() {
            DeviceFolderLoadTriggered = false;
        }
        internal static void ResetMusicStatics() {
            try {
                var menuType = UITypes.MP3Menu;
                var clipsField = AccessTools.Field(menuType, "clips");
                var dropdownListField = AccessTools.Field(menuType, "dropdownList");
                if (clipsField != null) clipsField.SetValue(null, null);
                if (dropdownListField != null) dropdownListField.SetValue(null, null);
                MP3TogetherDebug.Debug.Trace("ResetMusicStatics", "session boundary - cleared MP3Menu.clips/dropdownList so a previous world's list can't carry over");
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: ResetMusicStatics failed: " + e.Message);
            }
        }
        private static void PushMusicListToClients() {
            if (!Net.IsServer || !Net.IsRunning) return;
            try {
                var send = AccessTools.Method(typeof(MP3PlayerServerAudioStreamer), "Server_SendMusicList");
                if (send != null) send.Invoke(null, new object[] { null });
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: PushMusicListToClients failed: " + e.Message);
            }
        }
        internal static void EnsureBaseGamePopulated() {
            try {
                var menuType = UITypes.MP3Menu;
                var dropdownListField = AccessTools.Field(menuType, "dropdownList");
                if (dropdownListField != null && dropdownListField.GetValue(null) != null) {
                    MP3TogetherDebug.Debug.Trace("EnsureBaseGamePopulated", "MP3Menu.clips/dropdownList already populated (a real menu was opened this session, or this already ran), nothing to do");
                    return;
                }
                MP3TogetherDebug.Debug.Trace("EnsureBaseGamePopulated", "populating MP3Menu.clips/dropdownList with base game music headlessly, ungated, so any client can play base-dropdown songs without the host ever opening their own menu or approving device-folder music");
                var clipsField = AccessTools.Field(menuType, "clips");
                var clips = Resources.LoadAll<AudioClip>("Sounds/music").ToList();
                clipsField.SetValue(null, clips);
                PopulatePlaceholderDropdownList(menuType, dropdownListField, clips);
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: EnsureBaseGamePopulated failed: " + e.Message);
            }
        }
        internal static void EnsureDeviceFolderPopulated() {
            DeviceFolderLoadTriggered = true;
            try {
                EnsureBaseGamePopulated();
                var menuType = UITypes.MP3Menu;
                string folder = Path.Combine(Application.dataPath, "custommusic");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                MP3TogetherDebug.Instance.StartCoroutine(HeadlessLoadAllMusic(menuType, folder));
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: EnsureDeviceFolderPopulated failed: " + e.Message);
            }
        }
        private static void PopulatePlaceholderDropdownList(Type menuType, FieldInfo dropdownListField, List<AudioClip> clips) {
            try {
                var optionDataType = AccessTools.TypeByName("TMPro.TMP_Dropdown+OptionData");
                var ctor = optionDataType != null ? optionDataType.GetConstructor(new[] { typeof(string) }) : null;
                var listType = typeof(List<>).MakeGenericType(optionDataType ?? typeof(object));
                var typedList = Activator.CreateInstance(listType);
                var addMethod = listType.GetMethod("Add");
                if (ctor != null) {
                    foreach (var clip in clips) {
                        var option = ctor.Invoke(new object[] { clip != null ? clip.name : "" });
                        addMethod.Invoke(typedList, new object[] { option });
                    }
                }
                dropdownListField.SetValue(null, typedList);
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: PopulatePlaceholderDropdownList failed, dropdownList stays null (clips is still populated - the important part): " + e.Message);
            }
        }
        private static IEnumerator HeadlessLoadAllMusic(Type menuType, string folderPath) {
            var loadingField = AccessTools.Field(menuType, "loadingMusic");
            var clipsField = AccessTools.Field(menuType, "clips");
            var dropdownListField = AccessTools.Field(menuType, "dropdownList");
            var optionDataType = AccessTools.TypeByName("TMPro.TMP_Dropdown+OptionData");
            var optionCtor = optionDataType != null ? optionDataType.GetConstructor(new[] { typeof(string) }) : null;
            if (loadingField != null) loadingField.SetValue(null, true);
            string[] files = Array.Empty<string>();
            try {
                files = Directory.GetFiles(folderPath, "*.*");
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: HeadlessLoadAllMusic - couldn't list " + folderPath + ": " + e.Message);
            }
            var clipsListObj = clipsField.GetValue(null) as System.Collections.IList;
            var dropdownListObj = dropdownListField != null ? dropdownListField.GetValue(null) as System.Collections.IList : null;
            foreach (string file in files) {
                if (!(file.EndsWith(".mp3") || file.EndsWith(".wav") || file.EndsWith(".ogg"))) continue;
                string uri = "file://" + file;
                using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.UNKNOWN)) {
                    yield return www.SendWebRequest();
                    if (www.result == UnityWebRequest.Result.Success) {
                        AudioClip content = DownloadHandlerAudioClip.GetContent(www);
                        clipsListObj.Add(content);
                        if (dropdownListObj != null && optionCtor != null) dropdownListObj.Add(optionCtor.Invoke(new object[] { Path.GetFileName(file) }));
                    } else {
                        MP3TogetherDebug.Log.LogWarning("mp3client: HeadlessLoadAllMusic - failed to load " + file);
                    }
                }
            }
            if (loadingField != null) loadingField.SetValue(null, false);
            Patch_BuildSeparateSongUI.RefreshOpenMenuDropdown();
            PushMusicListToClients();
            MP3TogetherDebug.Debug.Trace("HeadlessLoadAllMusic", "finished, MP3Menu.clips now has " + clipsListObj.Count + " total entries, dropdownList now has " + (dropdownListObj != null ? dropdownListObj.Count.ToString() : "n/a") + " total entries");
        }
    }
}