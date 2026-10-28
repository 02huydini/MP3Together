using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Networking;
using Together;

namespace MP3Together {
    internal static class Protocol {
        public const string MSG_BEGIN = "MP3Client.Begin";
        public const string MSG_CHUNK = "MP3Client.Chunk";
        public const string MSG_END = "MP3Client.End";
        public const string MSG_RESEND_REQUEST = "MP3Client.ResendRequest";
        public const string MSG_CANCEL = "MP3Client.Cancel";
        public const string MSG_PLUGIN_PING = "MP3Client.PluginPing";
        public const string MSG_PLUGIN_PONG = "MP3Client.PluginPong";
        public const string MSG_PAUSE = "MP3Client.Pause";
        public const string MSG_ACK = "MP3Client.Ack";
        public const string MSG_REJECT = "MP3Client.Reject";
        public const string MSG_ASK_LOAD = "MP3Client.AskLoad";
        public const string MSG_LOAD_APPROVED = "MP3Client.LoadApproved";
        public const string MSG_HAVE = "MP3Client.Have";
        public const string MSG_MUTE_REQUEST = "MP3Client.MuteRequest";
        public const string MSG_VOLUME_SET = "MP3Client.VolumeSet";
        public const string MSG_VOLUME = "MP3Client.Volume";
        internal static NetDataWriter Writer(string name) {
            return Multiplayer.CreateNamedWriter(name);
        }
        public const byte REJECT_TOO_LARGE = 1;
        public const byte REJECT_BUSY = 2;
        public const byte REJECT_REPLACED = 3;
        public const byte REJECT_INVALID = 4;
        public const byte REJECT_MUTED = 5;
        public const byte REJECT_STOPPED = 6;
        public const int MAX_CONCURRENT_TRANSFERS = 4;
        public const float TRANSFER_IDLE_SECONDS = 60f;
        public const float SWEEP_INTERVAL_SECONDS = 5f;
        public const float TEMP_FILE_MAX_AGE_SECONDS = 300f;
        public const byte VERSION = 7;
        public const byte OUTDATED_BELOW = 4;
        public const float REQUEST_COOLDOWN_SECONDS = 30f;
        public const string COPY_FOLDER_NAME = "custommusic_Client";
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
        public const float CLEANUP_IDLE_SECONDS = 15f;
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
            if (Krok.IsClient) return false;
            MP3TogetherPlugin.Debug.UIState("TryApplyNonClientState", "not a remote client - nothing to ask a host, library dropdown and all controls enabled");
            LocalLibraryUI.EnableLibraryDropdown(libDropdown);
            LocalLibraryUI.SetHostGateEnabled(true);
            return true;
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
                MP3TogetherPlugin.Debug.NetState("HostPluginCheck", "sending MSG_PLUGIN_PING to host, awaiting pong");
                KrokBridge.SendPluginPing();
            }
            MP3TogetherPlugin.Instance.StartCoroutine(WaitForPong(libDropdown));
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
                Msg.Error("mp3together: Host doesn't have this plugin - custom music can't be sent.");
                LocalLibraryUI.SetDropdownHostMissingText(libDropdown, true);
                LocalLibraryUI.SetHostGateEnabled(false);
            }
        }
        internal static IEnumerator PingAfterJoin() {
            yield return new WaitForSecondsRealtime(3f);
            if (!Krok.IsClient || pingSent) yield break;
            pingSent = true;
            MP3TogetherPlugin.Debug.NetState("HostPluginCheck", "sending MSG_PLUGIN_PING after joining so the host can include this client in volume updates");
            KrokBridge.SendPluginPing();
        }
        public static void OnPong() {
            MP3TogetherPlugin.Debug.NetState("HostPluginCheck", "received MSG_PLUGIN_PONG - host has the plugin, enabling gate controls");
            CurrentStatus = Status.Confirmed;
            LocalLibraryUI.SetDropdownHostMissingText(LocalLibraryUI.CurrentLibDropdown, false);
            LocalLibraryUI.SetHostGateEnabled(true);
            if (Krok.Installed) KrokBridge.OnHostConfirmed();
        }
    }
    internal static class Krok {
        internal const string GUID = "CasualtiesMP";
        internal static bool Installed;
        private static readonly HashSet<string> OnlyWithKrok = new HashSet<string> {
            "Patch_RegisterServerReceivers", "Patch_RegisterClientReceivers", "Patch_EnsureBaseBeforeListRequest", "Patch_BypassForceLoadMusic", "Patch_GateNativeMusicListRequest", "PendingPromptUI", "MuteList", "Patch_MutePlayRequest",
            "Transfer", "NetHandlers", "Notify", "KrokBridge", "KrokNet", "HostPlayback", "UpdatePrompts",
            "Patch_OpusEncodeFailure", "Patch_OpusDecodeFailure", "VolumeBars", "InstanceVolume", "StreamProbeState", "Patch_DebugServerStreamProbe", "Patch_DebugServerLateUpdateErrors",
            "Patch_DebugEncodeProbe", "Patch_DebugClientStreamProbe"
        };
        internal static bool NeedsKrok(Type type) {
            while (type.DeclaringType != null) type = type.DeclaringType;
            return OnlyWithKrok.Contains(type.Name);
        }
        internal static bool IsRunning { get { return Installed && KrokNet.Running(); } }
        internal static bool IsServer { get { return Installed && KrokNet.Running() && KrokNet.Server(); } }
        internal static bool IsClient { get { return Installed && KrokNet.Running() && KrokNet.Client(); } }
    }
    internal static class KrokNet {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Running() { return Net.IsRunning; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Server() { return Net.IsServer; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Client() { return Net.IsClient; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool MP3Sync() { return Multiplayer.rules.EnableMP3Sync; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Log(string text) { Multiplayer.DoMultiplayerStatusMessageLog(text); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Error(string text) { Multiplayer.DoMultiplayerStatusMessageError(text); }
    }
    internal static class Msg {
        internal static void Log(string text) {
            if (Krok.Installed) {
                try {
                    KrokNet.Log(text);
                    return;
                } catch (Exception) { }
            }
            if (MP3TogetherPlugin.Log != null) MP3TogetherPlugin.Log.LogInfo(text);
        }
        internal static void Error(string text) {
            if (Krok.Installed) {
                try {
                    KrokNet.Error(text);
                    return;
                } catch (Exception) { }
            }
            if (MP3TogetherPlugin.Log != null) MP3TogetherPlugin.Log.LogError(text);
        }
    }
    internal static class ConsoleCommands {
        internal static bool Ready { get { return ConsoleScript.Commands.Count > 0; } }
        internal static void Add(string name, string help, Action<string[]> run, params ValueTuple<string, string>[] arguments) {
            ConsoleScript.Commands.Add(new Command(name, help, delegate (string[] args) { run(args); }, null, arguments ?? Array.Empty<ValueTuple<string, string>>()));
        }
    }
    internal static class ConsoleOut {
        private static MethodInfo logMethod;
        private static bool Write(string text) {
            try {
                var con = ConsoleScript.instance;
                if (con == null) return false;
                if (logMethod == null) logMethod = AccessTools.Method(typeof(ConsoleScript), "LogToConsole");
                if (logMethod == null) return false;
                logMethod.Invoke(con, new object[] { text });
                return true;
            } catch (Exception) {
                return false;
            }
        }
        internal static void Info(string text) {
            if (!Write(text) && MP3TogetherPlugin.Log != null) MP3TogetherPlugin.Log.LogInfo(text);
        }
        internal static void Error(string text) {
            Write("<color=red>" + text + "</color>");
        }
    }
    internal static class MemoryCleaner {
        private static string Mb(long bytes) {
            return (bytes / 1048576f).ToString("F1") + " MB";
        }
        internal static void Run() {
            long before = GC.GetTotalMemory(false);
            long loadedBytes;
            int loaded = HeadlessMusicLoader.FreeUnused(out loadedBytes);
            int transfers = Krok.IsServer ? KrokBridge.CleanHostTransfers() : 0;
            int clips = Krok.IsServer ? KrokBridge.CleanHostClips() : 0;
            int temp = DecoderHandler.SweepTempFiles(false);
            Resources.UnloadUnusedAssets();
            long after = GC.GetTotalMemory(true);
            ConsoleOut.Info("mp3together: MP3cleanMemory - freed " + loaded + " loaded song clips (about " + Mb(loadedBytes) + " decoded), " + clips + " finished or unused host clips, " + transfers + " stalled transfers, " + temp + " old temp files. Kept: the resend copies of sent songs and the custommusic_Client copies. Managed memory " + Mb(before) + " -> " + Mb(after) + ". Unity is unloading unused assets.");
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
                MP3TogetherPlugin.Log.LogWarning("mp3together: failed to scan " + GetPath() + ": " + e);
            }
            return result;
        }
    }
    internal static class HeadlessMusicLoader {
        private static readonly Dictionary<string, AudioClip> loadedByPath = new Dictionary<string, AudioClip>();
        private static readonly List<AudioClip> loadedClips = new List<AudioClip>();
        private static bool folderLoading;
        internal static void ResetMusicStatics() {
            try {
                var menuType = UITypes.MP3Menu;
                var clipsField = AccessTools.Field(menuType, "clips");
                var dropdownListField = AccessTools.Field(menuType, "dropdownList");
                foreach (AudioClip clip in loadedClips) {
                    if (clip != null) UnityEngine.Object.Destroy(clip);
                }
                loadedClips.Clear();
                loadedByPath.Clear();
                if (clipsField != null) clipsField.SetValue(null, null);
                if (dropdownListField != null) dropdownListField.SetValue(null, null);
                MP3TogetherPlugin.Debug.Trace("ResetMusicStatics", "session boundary - cleared MP3Menu.clips/dropdownList and the songs loaded from files so a previous world's list can't carry over");
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: ResetMusicStatics failed: " + e.Message);
            }
        }
        internal static int FreeUnused(out long bytes) {
            bytes = 0;
            if (folderLoading || loadedClips.Count == 0) return 0;
            var menuType = UITypes.MP3Menu;
            var clipsField = AccessTools.Field(menuType, "clips");
            var clips = clipsField != null ? clipsField.GetValue(null) as System.Collections.IList : null;
            if (clips == null) return 0;
            var dropdownListField = AccessTools.Field(menuType, "dropdownList");
            var dropdown = dropdownListField != null ? dropdownListField.GetValue(null) as System.Collections.IList : null;
            var inUse = new HashSet<AudioClip>();
            foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>()) {
                if (source.clip != null) inUse.Add(source.clip);
            }
            int freed = 0;
            foreach (AudioClip clip in new List<AudioClip>(loadedClips)) {
                if (clip == null) {
                    loadedClips.Remove(clip);
                    continue;
                }
                if (inUse.Contains(clip)) continue;
                int index = clips.IndexOf(clip);
                if (index >= 0) {
                    clips.RemoveAt(index);
                    if (dropdown != null && index < dropdown.Count) dropdown.RemoveAt(index);
                }
                bytes += (long)clip.samples * clip.channels * 4;
                loadedClips.Remove(clip);
                UnityEngine.Object.Destroy(clip);
                freed++;
            }
            if (freed == 0) return 0;
            foreach (string key in new List<string>(loadedByPath.Keys)) {
                if (!loadedClips.Contains(loadedByPath[key])) loadedByPath.Remove(key);
            }
            Patch_BuildSeparateSongUI.RefreshOpenMenuDropdown();
            if (Krok.IsServer) KrokBridge.PushMusicList();
            MP3TogetherPlugin.Debug.Trace("HeadlessMusicLoader.FreeUnused", "freed " + freed + " loaded song clips, music list now has " + clips.Count + " entries");
            return freed;
        }
        internal static void EnsureBaseGamePopulated() {
            try {
                var menuType = UITypes.MP3Menu;
                var dropdownListField = AccessTools.Field(menuType, "dropdownList");
                if (dropdownListField != null && dropdownListField.GetValue(null) != null) {
                    MP3TogetherPlugin.Debug.Trace("EnsureBaseGamePopulated", "MP3Menu.clips/dropdownList already populated (a real menu was opened this session, or this already ran), nothing to do");
                    return;
                }
                MP3TogetherPlugin.Debug.Trace("EnsureBaseGamePopulated", "populating MP3Menu.clips/dropdownList with base game music headlessly so any client can play base-dropdown songs without the host ever opening their own menu");
                var clipsField = AccessTools.Field(menuType, "clips");
                var clips = Resources.LoadAll<AudioClip>("Sounds/music").ToList();
                clipsField.SetValue(null, clips);
                PopulatePlaceholderDropdownList(menuType, dropdownListField, clips);
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: EnsureBaseGamePopulated failed: " + e.Message);
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
                MP3TogetherPlugin.Log.LogWarning("mp3together: PopulatePlaceholderDropdownList failed, dropdownList stays null (clips is still populated - the important part): " + e.Message);
            }
        }
        private static int FindByLabel(string label) {
            var dropdownListField = AccessTools.Field(UITypes.MP3Menu, "dropdownList");
            var list = dropdownListField != null ? dropdownListField.GetValue(null) as System.Collections.IList : null;
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++) {
                if (list[i] != null && string.Equals(ReflectionHelpers.GetMember(list[i], "text") as string, label, StringComparison.Ordinal)) return i;
            }
            return -1;
        }
        internal static void EnsureDeviceFolderPopulated() {
            if (folderLoading) return;
            folderLoading = true;
            try {
                EnsureBaseGamePopulated();
                string folder = CustomMusicFolder.GetPath();
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                MP3TogetherPlugin.Instance.StartCoroutine(HeadlessLoadAllMusic(UITypes.MP3Menu, folder));
            } catch (Exception e) {
                folderLoading = false;
                MP3TogetherPlugin.Log.LogWarning("mp3together: EnsureDeviceFolderPopulated failed: " + e.Message);
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
                MP3TogetherPlugin.Log.LogWarning("mp3together: HeadlessLoadAllMusic - couldn't list " + folderPath + ": " + e.Message);
            }
            var clipsListObj = clipsField.GetValue(null) as System.Collections.IList;
            var dropdownListObj = dropdownListField != null ? dropdownListField.GetValue(null) as System.Collections.IList : null;
            var known = new HashSet<string>();
            if (dropdownListObj != null) {
                foreach (object option in dropdownListObj) {
                    string text = option != null ? ReflectionHelpers.GetMember(option, "text") as string : null;
                    if (text != null) known.Add(text);
                }
            }
            foreach (string file in files) {
                if (!PreviewData.IsAllowedExtension(Path.GetExtension(file).ToLowerInvariant())) continue;
                string label = Path.GetFileName(file);
                if (known.Contains(label)) continue;
                string url;
                try {
                    url = new Uri(file).AbsoluteUri;
                } catch (Exception) {
                    continue;
                }
                using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.UNKNOWN)) {
                    yield return www.SendWebRequest();
                    if (www.result == UnityWebRequest.Result.Success) {
                        AudioClip content = DownloadHandlerAudioClip.GetContent(www);
                        content.name = label;
                        clipsListObj.Add(content);
                        loadedClips.Add(content);
                        known.Add(label);
                        if (dropdownListObj != null && optionCtor != null) dropdownListObj.Add(optionCtor.Invoke(new object[] { label }));
                    } else {
                        MP3TogetherPlugin.Log.LogWarning("mp3together: HeadlessLoadAllMusic - failed to load " + file);
                    }
                }
            }
            if (loadingField != null) loadingField.SetValue(null, false);
            folderLoading = false;
            Patch_BuildSeparateSongUI.RefreshOpenMenuDropdown();
            if (Krok.IsServer) KrokBridge.PushMusicList();
            MP3TogetherPlugin.Debug.Trace("HeadlessLoadAllMusic", "finished, MP3Menu.clips now has " + clipsListObj.Count + " total entries, dropdownList now has " + (dropdownListObj != null ? dropdownListObj.Count.ToString() : "n/a") + " total entries");
        }
        internal static IEnumerator LoadOne(string path, string ext, Action<int> done, Action<string> failed) {
            EnsureBaseGamePopulated();
            var menuType = UITypes.MP3Menu;
            var clipsField = AccessTools.Field(menuType, "clips");
            var clipsListObj = clipsField != null ? clipsField.GetValue(null) as System.Collections.IList : null;
            if (clipsListObj == null) {
                failed("the music list is not available yet");
                yield break;
            }
            string full = Path.GetFullPath(path);
            AudioClip knownClip;
            if (loadedByPath.TryGetValue(full, out knownClip) && knownClip != null) {
                int known = clipsListObj.IndexOf(knownClip);
                if (known >= 0) {
                    done(known);
                    yield break;
                }
            }
            string folderFull = Path.GetFullPath(CustomMusicFolder.GetPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase)) {
                int byLabel = FindByLabel(Path.GetFileName(path));
                if (byLabel >= 0 && byLabel < clipsListObj.Count && clipsListObj[byLabel] != null) {
                    done(byLabel);
                    yield break;
                }
            }
            AudioClip clip = null;
            string error = null;
            yield return DecoderHandler.LoadClip(path, ext, delegate (AudioClip c) { clip = c; }, delegate (string e) { error = e; });
            if (clip == null) {
                failed(error ?? "no audio data");
                yield break;
            }
            clipsListObj = clipsField.GetValue(null) as System.Collections.IList;
            if (clipsListObj == null) {
                UnityEngine.Object.Destroy(clip);
                failed("the music list was reset while the song loaded");
                yield break;
            }
            string label = Path.GetFileName(path);
            clip.name = label;
            clipsListObj.Add(clip);
            int index = clipsListObj.Count - 1;
            loadedClips.Add(clip);
            loadedByPath[full] = clip;
            var dropdownListField = AccessTools.Field(menuType, "dropdownList");
            var dropdownListObj = dropdownListField != null ? dropdownListField.GetValue(null) as System.Collections.IList : null;
            var optionDataType = AccessTools.TypeByName("TMPro.TMP_Dropdown+OptionData");
            var optionCtor = optionDataType != null ? optionDataType.GetConstructor(new[] { typeof(string) }) : null;
            if (dropdownListObj != null && optionCtor != null) dropdownListObj.Add(optionCtor.Invoke(new object[] { label }));
            Patch_BuildSeparateSongUI.RefreshOpenMenuDropdown();
            if (Krok.IsServer) KrokBridge.PushMusicList();
            MP3TogetherPlugin.Debug.Trace("HeadlessMusicLoader.LoadOne", "loaded " + label + " as MP3Menu.clips[" + index + "], list now has " + clipsListObj.Count + " entries");
            done(index);
        }
    }
}