#if PREVIEW_CHECK
using System;
using System.Diagnostics;
using System.IO;
using MP3Client;
internal static class Program {
    private static int failures;
    private static void Check(bool ok, string name) {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }
    private static string Run(string exe, string args) {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using (var p = Process.Start(psi)) {
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode + "|" + o;
        }
    }
    private static double Duration(string file) {
        string r = Run("ffprobe", "-v error -show_entries format=duration -of default=nw=1:nk=1 \"" + file + "\"");
        string[] parts = r.Split('|');
        double d;
        return parts[0] == "0" && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : -1;
    }
    private static bool DecodesClean(string file) {
        string r = Run("ffmpeg", "-v error -i \"" + file + "\" -f null -");
        return r.StartsWith("0|") && r.Substring(2).Trim().Length == 0;
    }
    private static void CheckKind(string dir, string ext, string source) {
        byte[] full = File.ReadAllBytes(source);
        double fullSeconds = Duration(source);
        int cut = (int)(full.Length * 0.25);
        cut -= cut % 32000;
        var prefix = new byte[cut];
        Array.Copy(full, prefix, cut);
        byte[] prepared = PreviewData.Prepare(ext, prefix);
        string preparedPath = Path.Combine(dir, "prepared" + ext);
        File.WriteAllBytes(preparedPath, prepared);
        double seconds = Duration(preparedPath);
        Console.WriteLine("  " + ext + ": full=" + fullSeconds.ToString("F1") + "s prefix=" + cut + "B prepared=" + prepared.Length + "B preview=" + seconds.ToString("F1") + "s");
        Check(seconds > 0, ext + " prepared prefix has a readable duration");
        Check(seconds > fullSeconds * 0.12 && seconds <= fullSeconds * 0.30, ext + " preview length is close to 25 percent of the song");
        Check(DecodesClean(preparedPath), ext + " prepared prefix decodes with no errors");
        if (ext != ".mp3") {
            string rawPath = Path.Combine(dir, "raw" + ext);
            File.WriteAllBytes(rawPath, prefix);
            Console.WriteLine("  (unprepared prefix decodes clean: " + DecodesClean(rawPath) + ")");
        }
    }
    private static int Main(string[] args) {
        string dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "previewdatacheck");
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "source.wav");
        Run("ffmpeg", "-y -v error -f lavfi -i \"sine=frequency=440:duration=60\" -f lavfi -i \"sine=frequency=660:duration=60\" -filter_complex amerge=inputs=2 -ar 44100 \"" + source + "\"");
        string mp3 = Path.Combine(dir, "source.mp3");
        string ogg = Path.Combine(dir, "source.ogg");
        string mp3Tag = Path.Combine(dir, "tagged.mp3");
        Run("ffmpeg", "-y -v error -i \"" + source + "\" -codec:a libmp3lame -b:a 128k \"" + mp3 + "\"");
        Run("ffmpeg", "-y -v error -i \"" + source + "\" -codec:a libvorbis -q:a 4 \"" + ogg + "\"");
        Run("ffmpeg", "-y -v error -i \"" + mp3 + "\" -codec copy -id3v2_version 3 -metadata title=\"Test\" -metadata artist=\"Check\" \"" + mp3Tag + "\"");
        Check(File.Exists(source) && File.Exists(mp3) && File.Exists(ogg), "sample files were generated");
        CheckKind(dir, ".wav", source);
        CheckKind(dir, ".mp3", mp3);
        string taggedPrefix = Path.Combine(dir, "taggedprefix.mp3");
        byte[] taggedFull = File.ReadAllBytes(mp3Tag);
        var taggedCut = new byte[taggedFull.Length / 4 - (taggedFull.Length / 4) % 32000];
        Array.Copy(taggedFull, taggedCut, taggedCut.Length);
        File.WriteAllBytes(taggedPrefix, PreviewData.Prepare(".mp3", taggedCut));
        double taggedSeconds = Duration(taggedPrefix);
        Console.WriteLine("  tagged .mp3 prefix: " + taggedCut.Length + "B preview=" + taggedSeconds.ToString("F1") + "s");
        Check(taggedSeconds > 6 && taggedSeconds < 20 && DecodesClean(taggedPrefix), "tagged mp3 prefix length is about a quarter and decodes clean");
        CheckKind(dir, ".ogg", ogg);
        byte[] tagged = File.ReadAllBytes(mp3Tag);
        int start = PreviewData.AudioStart(".mp3", tagged, tagged.Length);
        Console.WriteLine("  tagged mp3: audio starts at byte " + start);
        Check(start > 10 && start < tagged.Length && (tagged[start] == 0xFF), "mp3 ID3v2 tag size is skipped and the next byte is a frame sync");
        Check(PreviewData.AudioStart(".ogg", tagged, tagged.Length) == 0, "non-mp3 files report audio start 0");
        Check(PreviewData.IsAllowedExtension(".mp3") && PreviewData.IsAllowedExtension(".wav") && PreviewData.IsAllowedExtension(".ogg"), "allowed extensions");
        Check(!PreviewData.IsAllowedExtension("..\\evil.dll") && !PreviewData.IsAllowedExtension(".exe") && !PreviewData.IsAllowedExtension("") && !PreviewData.IsAllowedExtension("/../x.mp3"), "unsafe extensions are refused");
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
#else
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.Events;
using Together;

namespace MP3Client {
    [HarmonyPatch]
    internal static class Patch_BuildSeparateSongUI {
        private static readonly ConditionalWeakTable<object, object> builtFor = new ConditionalWeakTable<object, object>();
        internal static WeakReference LastMp3MenuInstance;
        internal static void ResetForNewSession() {
            LastMp3MenuInstance = null;
        }
        internal static void RefreshOpenMenuDropdown() {
            var target = LastMp3MenuInstance != null ? LastMp3MenuInstance.Target as UnityEngine.Object : null;
            if (target == null) return;
            try {
                var listField = AccessTools.Field(UITypes.MP3Menu, "dropdownList");
                var list = listField != null ? listField.GetValue(null) : null;
                var ilist = list as System.Collections.IList;
                if (ilist == null) return;
                var dropdown = ReflectionHelpers.GetMember(target, "dropdown");
                if (dropdown == null) return;
                int previous = (int)ReflectionHelpers.GetMember(dropdown, "value");
                ReflectionHelpers.CallMethod(dropdown, "ClearOptions", Type.EmptyTypes, null);
                var add = dropdown.GetType().GetMethod("AddOptions", new[] { list.GetType() });
                if (add == null) return;
                add.Invoke(dropdown, new object[] { list });
                int restored = previous < ilist.Count ? previous : 0;
                ReflectionHelpers.CallMethod(dropdown, "SetValueWithoutNotify", new[] { typeof(int) }, new object[] { restored });
                MP3TogetherDebug.Debug.UIState("RefreshOpenMenuDropdown", "open MP3 menu dropdown refreshed, " + ilist.Count + " options");
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: RefreshOpenMenuDropdown failed: " + e.Message);
            }
        }
        static MethodBase TargetMethod() {
            var t = UITypes.MP3Menu;
            return AccessTools.Method(t, "UpdateList");
        }
        static void Postfix(object __instance) {
            Guard.Run("Patch_BuildSeparateSongUI.Postfix", delegate () { PostfixCore(__instance); });
        }
        static void PostfixCore(object __instance) {
            if (!MP3TogetherDebug.Active) return;
            MP3TogetherDebug.Debug.Trace("Patch_BuildSeparateSongUI.Postfix", "UpdateList returned for instance=" + __instance.GetHashCode() + " is_server=" + Net.IsServer + " is_client=" + Net.IsClient);
            LastMp3MenuInstance = new WeakReference(__instance);
            if (Net.IsServer && Net.IsRunning && !MP3TogetherDebug.AutoLoadMusic.Value && !HeadlessMusicLoader.DeviceFolderLoadTriggered && !Patch_RegisterServerReceivers.pendingActive) {
                MP3TogetherDebug.Debug.Trace("Patch_BuildSeparateSongUI.Postfix", "host opened the menu with device folder music not yet approved - auto-firing the pending-request prompt");
                Patch_RegisterServerReceivers.Server_HandleLoadRequest(null);
            }
            object marker;
            if (builtFor.TryGetValue(__instance, out marker)) {
                MP3TogetherDebug.Debug.Trace("Patch_BuildSeparateSongUI.Postfix", "instance=" + __instance.GetHashCode() + " already built, skipping");
                return;
            }
            MP3TogetherDebug.Debug.Trace("Patch_BuildSeparateSongUI.Postfix", "instance=" + __instance.GetHashCode() + " starting DeferredBuild");
            MP3TogetherDebug.Instance.StartCoroutine(DeferredBuild(__instance));
        }
        static IEnumerator DeferredBuild(object mp3MenuInstance) {
            MP3TogetherDebug.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " started, looking up 'dropdown' field");
            object dropdown = null;
            try {
                dropdown = ReflectionHelpers.GetMember(mp3MenuInstance, "dropdown");
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogError("mp3client: 'dropdown' field lookup failed: " + e);
                yield break;
            }
            if (dropdown == null) {
                MP3TogetherDebug.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " 'dropdown' field is null, giving up");
                yield break;
            }
            MP3TogetherDebug.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " dropdown found, waiting up to 5s for options to populate");
            float deadline = Time.unscaledTime + 5f;
            System.Collections.IList options = null;
            while (Time.unscaledTime < deadline) {
                options = ReflectionHelpers.GetMember(dropdown, "options") as System.Collections.IList;
                if (options != null && options.Count > 0) break;
                yield return null;
            }
            if (options == null || options.Count == 0) {
                MP3TogetherDebug.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " gave up after 5s, options never populated (options=" + (options == null ? "null" : "count 0") + ")");
                yield break;
            }
            MP3TogetherDebug.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " options populated (count=" + options.Count + "), calling LocalLibraryUI.Build");
            try {
                LocalLibraryUI.Build(mp3MenuInstance, (Component)dropdown);
                builtFor.Add(mp3MenuInstance, new object());
                MP3TogetherDebug.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " LocalLibraryUI.Build completed without throwing");
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogError("mp3client: Patch_BuildSeparateSongUI failed: " + e);
            }
        }
    }
    [HarmonyPatch]
    internal static class Patch_LogExitClick {
        static MethodBase TargetMethod() {
            var t = UITypes.MP3Menu;
            return AccessTools.Method(t, "Exit");
        }
        static void Prefix(object __instance) {
            Guard.Run("MP3Menu.Exit.Prefix", delegate () { PrefixCore(__instance); });
        }
        static void PrefixCore(object __instance) {
            if (!MP3TogetherDebug.Active) return;
            MP3TogetherDebug.Debug.Trace("MP3Menu.Exit", "instance=" + __instance.GetHashCode() + " is_server=" + Net.IsServer + " is_client=" + Net.IsClient);
        }
    }
    [HarmonyPatch]
    internal static class Patch_LogMenuStart {
        static MethodBase TargetMethod() {
            var t = UITypes.MP3Menu;
            return AccessTools.Method(t, "Start");
        }
        static void Prefix(object __instance) {
            Guard.Run("MP3Menu.Start.Prefix", delegate () { PrefixCore(__instance); });
        }
        static void PrefixCore(object __instance) {
            if (!MP3TogetherDebug.Active) return;
            MP3TogetherDebug.Debug.Trace("MP3Menu.Start", "instance=" + __instance.GetHashCode() + " is_server=" + Net.IsServer + " is_client=" + Net.IsClient + " network_system_is_running=" + Net.IsRunning);
        }
        static void Postfix(object __instance) {
            Guard.Run("MP3Menu.Start.Postfix", delegate () { PostfixCore(__instance); });
        }
        static void PostfixCore(object __instance) {
            if (!MP3TogetherDebug.Active) return;
            var dropdown = ReflectionHelpers.GetMember(__instance, "dropdown");
            var options = dropdown != null ? ReflectionHelpers.GetMember(dropdown, "options") as System.Collections.IList : null;
            MP3TogetherDebug.Debug.Trace("MP3Menu.Start", "instance=" + __instance.GetHashCode() + " finished, dropdown=" + (dropdown != null ? "found" : "NULL") + " options.count=" + (options != null ? options.Count.ToString() : "n/a"));
        }
    }
    internal static class LocalLibraryUI {
        internal static bool LocalIsSelected;
        private const string LibraryDropdownName = "MP3Client_LibraryDropdown";
        private const string BrowseButtonName = "MP3Client_BrowseButton";
        private static readonly ConditionalWeakTable<object, object> libraryDropdownFor = new ConditionalWeakTable<object, object>();
        private static readonly ConditionalWeakTable<object, string> selectedSongFor = new ConditionalWeakTable<object, string>();
        private static readonly ConditionalWeakTable<object, string> lastSourceFor = new ConditionalWeakTable<object, string>();
        private class ActiveTransfer { public ushort SyncId; public Coroutine Routine; }
        private static readonly ConditionalWeakTable<object, ActiveTransfer> activeTransferFor = new ConditionalWeakTable<object, ActiveTransfer>();
        private static readonly ConditionalWeakTable<object, object> pauseButtonFor = new ConditionalWeakTable<object, object>();
        private static object lastActiveInstanceKey;
        internal static readonly List<object> HostGatedControls = new List<object>();
        private static List<string> cachedSongs = new List<string>();
        internal static object CurrentLibDropdown;
        internal static bool SongActive;
        private static WeakReference currentMenu;
        internal static void ResetForNewSession() {
            SongActive = false;
            currentMenu = null;
        }
        internal static void SetSongActive(bool active) {
            SongActive = active;
            RefreshPause();
        }
        private static void RefreshPause() {
            object menu = currentMenu != null ? currentMenu.Target : null;
            if (menu != null) SetPauseInteractable(menu, SongActive);
        }
        internal static void RegisterPauseButton(object mp3MenuInstance, object pauseButtonComponent) {
            pauseButtonFor.Remove(mp3MenuInstance);
            pauseButtonFor.Add(mp3MenuInstance, pauseButtonComponent);
            SetPauseInteractable(mp3MenuInstance, SongActive);
        }
        internal static void SetPauseInteractable(object mp3MenuInstance, bool interactable) {
            object pauseButtonComponent;
            if (!pauseButtonFor.TryGetValue(mp3MenuInstance, out pauseButtonComponent)) return;
            bool allowed = interactable && HostPluginCheck.CurrentStatus == HostPluginCheck.Status.Confirmed;
            try { ReflectionHelpers.SetMember(pauseButtonComponent, "interactable", allowed); } catch { }
        }
        internal static void SetHostGateEnabled(bool enabled) {
            foreach (var c in HostGatedControls) {
                try { ReflectionHelpers.SetMember(c, "interactable", enabled); } catch { }
            }
            RefreshPause();
        }
        internal static void SetDropdownHostMissingText(object libDropdown, bool missing) {
            if (libDropdown == null) return;
            if (missing) {
                SetDropdownSingleMessage(libDropdown, "Host doesn't have MP3Client plugin");
            } else {
                RefreshLibraryOptions(libDropdown);
                try { ReflectionHelpers.SetMember(libDropdown, "interactable", true); } catch { }
            }
        }
        internal static void SetDropdownSingleMessage(object libDropdown, string message) {
            if (libDropdown == null) return;
            ReflectionHelpers.CallMethod(libDropdown, "ClearOptions", Type.EmptyTypes, null);
            ReflectionHelpers.CallMethod(libDropdown, "AddOptions", new[] { typeof(List<string>) }, new object[] { new List<string> { message } });
            ReflectionHelpers.CallMethod(libDropdown, "SetValueWithoutNotify", new[] { typeof(int) }, new object[] { 0 });
            try { ReflectionHelpers.SetMember(libDropdown, "interactable", false); } catch { }
        }
        internal static void SetDropdownLocalIsHostText(object libDropdown) {
            SetDropdownSingleMessage(libDropdown, "This dropdown is for Client");
        }
        internal static void SetDropdownNoServerText(object libDropdown) {
            SetDropdownSingleMessage(libDropdown, "This dropdown only available in server");
        }
        public static void BeginSend(object mp3MenuInstance) {
            CancelActive(mp3MenuInstance);
            lastActiveInstanceKey = mp3MenuInstance;
        }
        public static void RegisterTransfer(object mp3MenuInstance, ushort syncId, Coroutine routine) {
            activeTransferFor.Remove(mp3MenuInstance);
            activeTransferFor.Add(mp3MenuInstance, new ActiveTransfer { SyncId = syncId, Routine = routine });
            SetPauseInteractable(mp3MenuInstance, SongActive);
        }
        public static void ClearIfCurrent(object mp3MenuInstance, ushort syncId) {
            ActiveTransfer existing;
            if (activeTransferFor.TryGetValue(mp3MenuInstance, out existing) && existing.SyncId == syncId) {
                activeTransferFor.Remove(mp3MenuInstance);
                SetPauseInteractable(mp3MenuInstance, SongActive);
            }
        }
        public static void CancelActive(object mp3MenuInstance) {
            ActiveTransfer existing;
            if (!activeTransferFor.TryGetValue(mp3MenuInstance, out existing)) return;
            if (existing.Routine != null) MP3TogetherDebug.Instance.StopCoroutine(existing.Routine);
            Patch_InterceptBrowsePlay.StopResend(existing.SyncId);
            Patch_InterceptBrowsePlay.SendCancel(existing.SyncId);
            Patch_InterceptBrowsePlay.ForgetPending(existing.SyncId);
            activeTransferFor.Remove(mp3MenuInstance);
            SetPauseInteractable(mp3MenuInstance, SongActive);
        }
        public static bool AbortActive(ushort syncId) {
            object instance = lastActiveInstanceKey;
            if (instance == null) return false;
            ActiveTransfer existing;
            if (!activeTransferFor.TryGetValue(instance, out existing) || existing.SyncId != syncId) return false;
            if (existing.Routine != null) MP3TogetherDebug.Instance.StopCoroutine(existing.Routine);
            Patch_InterceptBrowsePlay.StopResend(syncId);
            Patch_InterceptBrowsePlay.ForgetPending(syncId);
            activeTransferFor.Remove(instance);
            SetPauseInteractable(instance, SongActive);
            return true;
        }
        public static bool CancelActiveGlobal() {
            object instance = lastActiveInstanceKey;
            if (instance == null) return false;
            ActiveTransfer existing;
            if (!activeTransferFor.TryGetValue(instance, out existing)) return false;
            CancelActive(instance);
            return true;
        }
        internal static void StopOrCancel() {
            bool cancelled = CancelActiveGlobal();
            ushort playerSyncId;
            bool haveId = Patch_InterceptBrowsePlay.TryGetCurrentPlayerSyncId(out playerSyncId);
            if (haveId) Patch_InterceptBrowsePlay.StopResend(playerSyncId);
            if (!cancelled && haveId) Patch_InterceptBrowsePlay.SendCancel(playerSyncId);
            SetSongActive(false);
            Multiplayer.DoMultiplayerStatusMessageLog(cancelled ? "mp3client: Stop - dropped current transfer." : "mp3client: Stop - asked host to stop playback.");
        }
        private static void OnStopButtonClicked() {
            StopOrCancel();
        }
        internal static void OnPauseButtonClicked() {
            ushort playerSyncId;
            if (Patch_InterceptBrowsePlay.TryGetCurrentPlayerSyncId(out playerSyncId)) {
                Patch_InterceptBrowsePlay.SendPause(playerSyncId);
                Multiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3pause - sent pause/resume toggle.");
            }
        }
        public static void Build(object mp3MenuInstance, Component originalDropdown) {
            MP3TogetherDebug.Debug.UIState("Build", "building MP3Client UI onto the MP3 menu, instance=" + mp3MenuInstance.GetHashCode());
            HostGatedControls.Clear();
            currentMenu = new WeakReference(mp3MenuInstance);
            Transform parent = originalDropdown.transform.parent != null ? originalDropdown.transform.parent : originalDropdown.transform;
            var origRect = originalDropdown.GetComponent<RectTransform>();
            MP3TogetherDebug.Debug.UIState("Build", "parent=" + parent.name + " origRect=" + (origRect != null ? origRect.rect.ToString() : "null"));
            float rowHeight = origRect != null ? origRect.rect.height + 4f : 34f;
            GameObject libraryGO = UnityEngine.Object.Instantiate(originalDropdown.gameObject, parent, false);
            libraryGO.name = LibraryDropdownName;
            object libDropdown = libraryGO.GetComponent(originalDropdown.GetType());
            ReflectionHelpers.CallMethod(libDropdown, "ClearOptions", Type.EmptyTypes, null);
            var libValueChangedEvt = ReflectionHelpers.GetMember(libDropdown, "onValueChanged");
            libValueChangedEvt.GetType().GetMethod("RemoveAllListeners").Invoke(libValueChangedEvt, null);
            libraryDropdownFor.Remove(mp3MenuInstance);
            libraryDropdownFor.Add(mp3MenuInstance, libDropdown);
            CurrentLibDropdown = libDropdown;
            int optionCount = RefreshLibraryOptions(libDropdown);
            MP3TogetherDebug.Debug.UIState("Build", "library dropdown cloned and populated, " + optionCount + " options (including placeholder)");
            HookValueChanged(libDropdown, originalDropdown, mp3MenuInstance);
            HookBaseDropdownValueChanged(originalDropdown, libDropdown, mp3MenuInstance);
            var panel = FindPanel(originalDropdown.transform);
            MP3TogetherDebug.Debug.UIState("Build", "FindPanel result: " + (panel != null ? panel.name : "null (will use parent as search root)"));
            var searchRoot = panel != null ? panel : parent;
            var buttonType = UITypes.Button;
            var allButtons = searchRoot.GetComponentsInChildren(buttonType, true);
            MP3TogetherDebug.Debug.UIState("Build", "searched '" + searchRoot.name + "' for buttons, found " + allButtons.Length + ": [" + string.Join(", ", allButtons.Select(b => b.gameObject.name)) + "]");
            Component playButton = null;
            Component exitButton = null;
            foreach (Component c in allButtons) {
                var n = c.gameObject.name.ToLowerInvariant();
                if (playButton == null && n.Contains("play")) playButton = c;
                if (exitButton == null && n.Contains("exit")) exitButton = c;
            }
            MP3TogetherDebug.Debug.UIState("Build", "playButton=" + (playButton != null ? playButton.gameObject.name : "NOT FOUND") + " exitButton=" + (exitButton != null ? exitButton.gameObject.name : "NOT FOUND"));

            if (playButton != null) {
                MP3TogetherDebug.Debug.UIState("Build", "using row-layout path (Browse/Stop/Pause + Play/Exit)");
                Component anchorButton = exitButton != null ? exitButton : playButton;
                var buttons = ButtonTemplates.CreateAll(anchorButton, mp3MenuInstance);
                GameObject browseGO = buttons.Browse;
                GameObject stopGO = buttons.Stop;
                GameObject pauseGO = buttons.Pause;

                AssembleContainerLayout(originalDropdown, libraryGO, playButton, exitButton, browseGO, stopGO, pauseGO, rowHeight, panel);
                HookPlayButton(playButton, mp3MenuInstance);
                HostGatedControls.Add(browseGO.GetComponent(buttonType));
                HostGatedControls.Add(stopGO.GetComponent(buttonType));
                RegisterPauseButton(mp3MenuInstance, pauseGO.GetComponent(buttonType));
            } else {
                MP3TogetherDebug.Debug.UIState("Build", "using fallback path (no base Play button found in search root)");
                var libRect = libraryGO.GetComponent<RectTransform>();
                if (libRect != null && origRect != null) libRect.anchoredPosition = origRect.anchoredPosition + new Vector2(0f, -(rowHeight + MP3TogetherDebug.UIVerticalOffset.Value));
                BuildFallbackRow(mp3MenuInstance, parent, origRect, rowHeight, MP3TogetherDebug.UIVerticalOffset.Value);
                GrowPanel(panel, rowHeight * 1.5f + MP3TogetherDebug.UIVerticalOffset.Value);
            }
            try {
                HostPluginCheck.EnsureChecked(libDropdown);
                MP3TogetherDebug.Debug.UIState("Build", "HostPluginCheck.EnsureChecked completed");
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogError("mp3client: EnsureChecked failed: " + e);
            }
            MP3TogetherDebug.Debug.UIState("Build", "finished, HostGatedControls count=" + HostGatedControls.Count);
        }
        private static void NeutralizeCompetingLayout(RectTransform rt) {
            if (rt == null) return;
            var fitterType = UITypes.ContentSizeFitter;
            if (fitterType != null) {
                var fitter = rt.GetComponent(fitterType) as Behaviour;
                if (fitter != null && fitter.enabled) fitter.enabled = false;
            }
        }
        private static void HookPlayButton(Component playButton, object mp3MenuInstance) {
            var go = playButton.gameObject;
            var newButton = ButtonTemplates.ReplaceButtonComponent(go);
            var onClick = ReflectionHelpers.GetMember(newButton, "onClick");
            var addListener = onClick.GetType().GetMethod("AddListener");
            UnityAction handler = () => {
                MP3TogetherDebug.Debug.Trace("HookPlayButton", "clicked, instance=" + mp3MenuInstance.GetHashCode() + " IsLocalLastSelected=" + LocalLibraryUI.IsLocalLastSelected(mp3MenuInstance));
                if (LocalLibraryUI.IsLocalLastSelected(mp3MenuInstance)) {
                    string path;
                    if (LocalLibraryUI.TryGetStaged(mp3MenuInstance, out path) && !string.IsNullOrEmpty(path)) {
                        MP3TogetherDebug.Debug.Trace("HookPlayButton", "routing to local staged song: " + path);
                        Patch_InterceptBrowsePlay.SendStagedSong(mp3MenuInstance, path);
                        return;
                    }
                    MP3TogetherDebug.Debug.Trace("HookPlayButton", "IsLocalLastSelected true but TryGetStaged failed/empty - falling through to base selection");
                }
                MP3TogetherDebug.Debug.Trace("HookPlayButton", "routing to base dropdown selection");
                LocalLibraryUI.SetSongActive(true);
                LocalLibraryUI.PlayBaseSelectionWithoutExit(mp3MenuInstance);
            };
            addListener.Invoke(onClick, new object[] { handler });
        }
        internal static void PlayBaseSelectionWithoutExit(object mp3MenuInstance) {
            var menuType = UITypes.MP3Menu;
            bool multiplayerActive = Net.IsRunning && Multiplayer.rules.EnableMP3Sync;
            bool mpClient = multiplayerActive && Net.IsClient;
            var loadingField = AccessTools.Field(menuType, "loadingMusic");
            if (!mpClient && loadingField != null && (bool)loadingField.GetValue(null)) {
                MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "skipped - still loading music");
                return;
            }
            var dropdown = ReflectionHelpers.GetMember(mp3MenuInstance, "dropdown");
            int index = (int)ReflectionHelpers.GetMember(dropdown, "value");
            if (multiplayerActive) {
                var itemField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
                var itemObj = itemField != null ? itemField.GetValue(null) : null;
                if (itemObj != null) {
                    if (Net.IsClient) {
                        SyncInfo syncInfo;
                        if (NetObjectRegistry.TryGetSyncInfoOrRegister((Component)itemObj, out syncInfo)) {
                            var writer = Net.CreateWriter((byte)NetmsgId.SERVER_MP3PlayCustomMusic);
                            writer.Put(index);
                            writer.Put(syncInfo.syncId);
                            var dm = DeliveryMethod.ReliableUnordered;
                            Net.Client_Send(in dm, in writer);
                            MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer client - sent native play-request msg 10137 index=" + index + " syncId=" + syncInfo.syncId);
                        } else {
                            MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer client - mp3 player not registered, cannot send play request");
                        }
                    } else {
                        var clipsFieldMp = AccessTools.Field(menuType, "clips");
                        var clipsMp = clipsFieldMp != null ? clipsFieldMp.GetValue(null) as System.Collections.IList : null;
                        if (clipsMp != null && index >= 0 && index < clipsMp.Count) {
                            var serverPlayMethod = AccessTools.Method(typeof(MP3Menu_Play_MultiplayerPatch), "Server_PlayThisSongOnThisMp3Player");
                            if (serverPlayMethod != null) {
                                serverPlayMethod.Invoke(null, new object[] { clipsMp[index], itemObj });
                                MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - played directly via Server_PlayThisSongOnThisMp3Player index=" + index);
                            } else {
                                MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - Server_PlayThisSongOnThisMp3Player method not found");
                            }
                        } else {
                            MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - clips null or index " + index + " out of range");
                        }
                    }
                    return;
                }
                MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer active but no known mp3 player instance - falling back to local MusicManager playback");
            }
            var clipsField = AccessTools.Field(menuType, "clips");
            var clips = clipsField != null ? clipsField.GetValue(null) as System.Collections.IList : null;
            if (clips == null || index < 0 || index >= clips.Count) {
                MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "skipped - clips null or index " + index + " out of range");
                return;
            }
            var clip = clips[index] as AudioClip;
            MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "solo/offline - index=" + index + " clip=" + (clip != null ? clip.name : "NULL"));
            var musicManagerType = AccessTools.TypeByName("MusicManager");
            var mainField = musicManagerType != null ? AccessTools.Field(musicManagerType, "main") : null;
            var mainInstance = mainField != null ? mainField.GetValue(null) : null;
            var playSongMethod = musicManagerType != null ? AccessTools.Method(musicManagerType, "PlaySong") : null;
            if (mainInstance == null || playSongMethod == null) {
                MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "skipped - MusicManager.main/PlaySong not found (main=" + (mainInstance != null) + " method=" + (playSongMethod != null) + ")");
                return;
            }
            playSongMethod.Invoke(mainInstance, new object[] { clip });
            MP3TogetherDebug.Debug.Trace("PlayBaseSelectionWithoutExit", "PlaySong invoked successfully");
        }
        private const string LayoutRootName = "MP3Client_LayoutRoot";
        private static GameObject BuildRow(Transform parent, string name, Type hLayoutType, Type layoutElementType, float rowHeight) {
            GameObject row = new GameObject(name, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var hLayout = row.AddComponent(hLayoutType);
            ReflectionHelpers.SetMember(hLayout, "spacing", 8f);
            ReflectionHelpers.SetMember(hLayout, "childForceExpandWidth", true);
            ReflectionHelpers.SetMember(hLayout, "childForceExpandHeight", true);
            ReflectionHelpers.SetMember(hLayout, "childControlWidth", true);
            ReflectionHelpers.SetMember(hLayout, "childControlHeight", true);
            ReflectionHelpers.SetMember(hLayout, "childAlignment", TextAnchor.MiddleCenter);
            SetRowLayoutElement(row, layoutElementType, rowHeight);
            return row;
        }
        private static void SetRowLayoutElement(GameObject go, Type layoutElementType, float rowHeight) {
            if (layoutElementType == null) return;
            var existing = go.GetComponent(layoutElementType);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
            var le = go.AddComponent(layoutElementType);
            ReflectionHelpers.SetMember(le, "preferredHeight", rowHeight);
            ReflectionHelpers.SetMember(le, "minHeight", rowHeight);
        }
        private static void StripLayoutElement(GameObject go, Type layoutElementType) {
            if (layoutElementType == null || go == null) return;
            var existing = go.GetComponent(layoutElementType);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
        }
        private static void AssembleContainerLayout(Component originalDropdown, GameObject libraryGO, Component playButton, Component exitButton, GameObject browseGO, GameObject stopGO, GameObject pauseGO, float rowHeight, Transform panel) {
            var origRect = originalDropdown.GetComponent<RectTransform>();
            if (origRect == null || playButton == null) return;
            Transform oldParent = originalDropdown.transform.parent;
            const float spacing = 4f;
            GameObject root = new GameObject(LayoutRootName, typeof(RectTransform));
            root.transform.SetParent(oldParent, false);
            var rootRect = root.GetComponent<RectTransform>();
            float newRootHeight = (rowHeight * 4f) + (spacing * 3f);

            if (MP3TogetherDebug.UIOnTop.Value) {
                rootRect.anchorMin = new Vector2(0.5f, 1f);
                rootRect.anchorMax = new Vector2(0.5f, 1f);
                rootRect.pivot = new Vector2(0.5f, 1f);
            } else {
                rootRect.anchorMin = new Vector2(0.5f, 0.5f);
                rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                rootRect.pivot = new Vector2(0.5f, 0.5f);
            }

            rootRect.anchoredPosition = new Vector2(0f, -(rowHeight * 0.25f));
            rootRect.sizeDelta = new Vector2(origRect.rect.width, newRootHeight);

            root.transform.SetSiblingIndex(originalDropdown.transform.GetSiblingIndex());
            var vLayoutType = UITypes.VerticalLayoutGroup;
            var hLayoutType = UITypes.HorizontalLayoutGroup;
            var layoutElementType = UITypes.LayoutElement;
            if (vLayoutType == null || hLayoutType == null) {
                MP3TogetherDebug.Log.LogError("mp3client: VerticalLayoutGroup/HorizontalLayoutGroup not found - cannot build container layout");
                UnityEngine.Object.DestroyImmediate(root);
                return;
            }
            var vLayout = root.AddComponent(vLayoutType);
            ReflectionHelpers.SetMember(vLayout, "spacing", spacing);
            ReflectionHelpers.SetMember(vLayout, "childForceExpandWidth", true);
            ReflectionHelpers.SetMember(vLayout, "childForceExpandHeight", false);
            ReflectionHelpers.SetMember(vLayout, "childControlWidth", true);
            ReflectionHelpers.SetMember(vLayout, "childControlHeight", true);
            ReflectionHelpers.SetMember(vLayout, "childAlignment", TextAnchor.UpperCenter);

            originalDropdown.transform.SetParent(root.transform, false);
            SetRowLayoutElement(originalDropdown.gameObject, layoutElementType, rowHeight);
            libraryGO.transform.SetParent(root.transform, false);
            SetRowLayoutElement(libraryGO, layoutElementType, rowHeight);
            GameObject row1 = BuildRow(root.transform, "MP3Client_Row_BrowseStopPause", hLayoutType, layoutElementType, rowHeight);
            browseGO.transform.SetParent(row1.transform, false);
            stopGO.transform.SetParent(row1.transform, false);
            pauseGO.transform.SetParent(row1.transform, false);
            GameObject row2 = BuildRow(root.transform, "MP3Client_Row_PlayExit", hLayoutType, layoutElementType, rowHeight);
            if (exitButton != null) exitButton.transform.SetParent(row2.transform, false);
            playButton.transform.SetParent(row2.transform, false);

            StripLayoutElement(browseGO, layoutElementType);
            StripLayoutElement(stopGO, layoutElementType);
            StripLayoutElement(pauseGO, layoutElementType);
            StripLayoutElement(playButton.gameObject, layoutElementType);
            if (exitButton != null) StripLayoutElement(exitButton.gameObject, layoutElementType);
            NeutralizeCompetingLayout(browseGO.GetComponent<RectTransform>());
            NeutralizeCompetingLayout(stopGO.GetComponent<RectTransform>());
            NeutralizeCompetingLayout(pauseGO.GetComponent<RectTransform>());
            NeutralizeCompetingLayout(playButton.GetComponent<RectTransform>());
            if (exitButton != null) NeutralizeCompetingLayout(exitButton.GetComponent<RectTransform>());

            float addedHeight = (rowHeight * 2.5f) + (spacing * 2f);
            GrowPanel(panel, addedHeight);

            var rebuilderType = UITypes.LayoutRebuilder;
            if (rebuilderType != null) {
                var forceRebuild = rebuilderType.GetMethod("ForceRebuildLayoutImmediate", BindingFlags.Public | BindingFlags.Static);
                if (forceRebuild != null) forceRebuild.Invoke(null, new object[] { rootRect });
            }
        }
        private static readonly ConditionalWeakTable<object, object> menuForDropdown = new ConditionalWeakTable<object, object>();
        private static void HookBaseDropdownValueChanged(Component originalDropdown, object libDropdown, object mp3MenuInstance) {
            menuForDropdown.Remove(originalDropdown);
            menuForDropdown.Add(originalDropdown, mp3MenuInstance);
            var evt = ReflectionHelpers.GetMember(originalDropdown, "onValueChanged");
            var addListener = evt.GetType().GetMethod("AddListener");
            UnityAction<int> handler = (index) => {
                lastSourceFor.Remove(mp3MenuInstance);
                lastSourceFor.Add(mp3MenuInstance, "base");
                LocalIsSelected = false;
            };
            addListener.Invoke(evt, new object[] { handler });
        }
        private static Transform FindPanel(Transform from) {
            var imageType = UITypes.Image;
            var ownHeight = from.GetComponent<RectTransform>() != null ? from.GetComponent<RectTransform>().rect.height : 0f;
            var t = from.parent;
            for (int i = 0; i < 6 && t != null; i++) {
                var rt = t as RectTransform;
                var img = t.GetComponent(imageType);
                if (rt != null && img != null && rt.rect.height > ownHeight * 1.5f) return t;
                t = t.parent;
            }
            return null;
        }
        private static void GrowPanel(Transform panel, float extraHeight) {
            if (panel == null) return;
            var rt = panel as RectTransform;
            if (rt == null) return;

            if (MP3TogetherDebug.UIOnTop.Value) {
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
            } else {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
            }

            rt.sizeDelta = new Vector2(rt.sizeDelta.x, rt.sizeDelta.y + extraHeight);
            rt.anchoredPosition = Vector2.zero;
        }
        private static void BuildFallbackRow(object mp3MenuInstance, Transform parent, RectTransform origRect, float rowHeight, float verticalOffset) {
            float fullWidth = origRect != null ? origRect.rect.width : 150f;
            float fourthWidth = fullWidth / 4f - 4f;
            Vector2 stepDown2 = new Vector2(0f, -(rowHeight * 1.5f + verticalOffset));
            float baseX = origRect != null ? origRect.anchoredPosition.x : 0f;
            float baseY = origRect != null ? origRect.anchoredPosition.y + stepDown2.y : 0f;
            MakeButton(parent, BrowseButtonName, origRect, new Vector2(baseX - fourthWidth * 1.5f - 4f, baseY), new Vector2(fourthWidth, rowHeight - 4f), new Color(1f, 0f, 1f, 0.85f), Protocol.BROWSE_LABEL, () => Patch_InterceptBrowsePlay.BrowseAndPackage(mp3MenuInstance));
            MakeButton(parent, "MP3Client_SendButton", origRect, new Vector2(baseX - fourthWidth * 0.5f, baseY), new Vector2(fourthWidth, rowHeight - 4f), new Color(0f, 1f, 1f, 0.85f), "Send", () => {
                string path;
                if (!selectedSongFor.TryGetValue(mp3MenuInstance, out path) || string.IsNullOrEmpty(path)) {
                    Multiplayer.DoMultiplayerStatusMessageError("mp3client: pick a song first.");
                    return;
                }
                Patch_InterceptBrowsePlay.SendStagedSong(mp3MenuInstance, path);
            });
            MakeButton(parent, "MP3Client_PauseButton", origRect, new Vector2(baseX + fourthWidth * 0.5f, baseY), new Vector2(fourthWidth, rowHeight - 4f), new Color(1f, 0.75f, 0.1f, 0.85f), "Pause", OnPauseButtonClicked);
            MakeButton(parent, "MP3Client_StopButton", origRect, new Vector2(baseX + fourthWidth * 1.5f + 4f, baseY), new Vector2(fourthWidth, rowHeight - 4f), new Color(1f, 0.25f, 0.25f, 0.85f), "Stop", OnStopButtonClicked);
        }
        private static RectTransform MakeButton(Transform parent, string name, RectTransform origRect, Vector2 anchoredPosition, Vector2 size, Color color, string label, Action onClick) {
            GameObject buttonGO = new GameObject(name, typeof(RectTransform));
            buttonGO.transform.SetParent(parent, false);
            var btnRect = buttonGO.GetComponent<RectTransform>();
            btnRect.sizeDelta = size;
            if (origRect != null) {
                btnRect.anchorMin = origRect.anchorMin;
                btnRect.anchorMax = origRect.anchorMax;
                btnRect.pivot = origRect.pivot;
                btnRect.anchoredPosition = anchoredPosition;
            }
            var imageType = UITypes.Image;
            var buttonType = UITypes.Button;
            var image = buttonGO.AddComponent(imageType);
            ReflectionHelpers.SetMember(image, "color", color);
            var button = buttonGO.AddComponent(buttonType);
            var onClickObj = ReflectionHelpers.GetMember(button, "onClick");
            var addListener = onClickObj.GetType().GetMethod("AddListener");
            UnityAction handler = () => onClick();
            addListener.Invoke(onClickObj, new object[] { handler });
            if (!string.IsNullOrEmpty(label)) {
                GameObject textGO = new GameObject("Label", typeof(RectTransform));
                textGO.transform.SetParent(buttonGO.transform, false);
                var textRect = textGO.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
                var tmpTextType = UITypes.TMPText;
                if (tmpTextType != null) {
                    var text = textGO.AddComponent(tmpTextType);
                    ReflectionHelpers.SetMember(text, "text", label);
                    ReflectionHelpers.SetMember(text, "color", Color.black);
                } else {
                    var uiTextType = UITypes.Text;
                    if (uiTextType != null) {
                        var text = textGO.AddComponent(uiTextType);
                        ReflectionHelpers.SetMember(text, "text", label);
                        ReflectionHelpers.SetMember(text, "color", Color.black);
                    }
                }
            }
            HostGatedControls.Add(button);
            return btnRect;
        }
        public static int RefreshLibraryOptions(object libDropdown) {
            return RefreshLibraryOptions(libDropdown, null);
        }
        public static int RefreshLibraryOptions(object libDropdown, string selectPath) {
            cachedSongs = CustomMusicFolder.Scan();
            var labels = new List<string> { "Select a song..." };
            labels.AddRange(cachedSongs.Select(Path.GetFileNameWithoutExtension));
            ReflectionHelpers.CallMethod(libDropdown, "ClearOptions", Type.EmptyTypes, null);
            ReflectionHelpers.CallMethod(libDropdown, "AddOptions", new[] { typeof(List<string>) }, new object[] { labels });
            int selectedIndex = 0;
            if (selectPath != null) {
                string wanted = Path.GetFullPath(selectPath);
                int songIndex = cachedSongs.FindIndex(s => string.Equals(Path.GetFullPath(s), wanted, StringComparison.OrdinalIgnoreCase));
                if (songIndex >= 0) selectedIndex = songIndex + 1;
            }
            ReflectionHelpers.CallMethod(libDropdown, "SetValueWithoutNotify", new[] { typeof(int) }, new object[] { selectedIndex });
            return labels.Count;
        }
        public static void TryRefresh(object mp3MenuInstance) {
            TryRefresh(mp3MenuInstance, null);
        }
        public static void TryRefresh(object mp3MenuInstance, string selectPath) {
            object libDropdown;
            if (libraryDropdownFor.TryGetValue(mp3MenuInstance, out libDropdown)) RefreshLibraryOptions(libDropdown, selectPath);
        }
        internal static void SelectLocal(object mp3MenuInstance, string path) {
            try {
                TryRefresh(mp3MenuInstance, path);
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: couldn't show " + Path.GetFileName(path) + " in the library dropdown: " + e.Message);
            }
            Stage(mp3MenuInstance, path);
            lastSourceFor.Remove(mp3MenuInstance);
            lastSourceFor.Add(mp3MenuInstance, "local");
            LocalIsSelected = true;
            Multiplayer.DoMultiplayerStatusMessageLog("mp3client: selected " + Path.GetFileName(path) + ", press Play.");
        }
        public static void Stage(object mp3MenuInstance, string path) {
            selectedSongFor.Remove(mp3MenuInstance);
            selectedSongFor.Add(mp3MenuInstance, path);
        }
        public static bool TryGetStaged(object mp3MenuInstance, out string path) {
            return selectedSongFor.TryGetValue(mp3MenuInstance, out path);
        }
        public static bool IsLocalLastSelected(object mp3MenuInstance) {
            string src;
            return lastSourceFor.TryGetValue(mp3MenuInstance, out src) && src == "local";
        }
        private static void HookValueChanged(object libDropdown, Component originalDropdown, object mp3MenuInstance) {
            var evt = ReflectionHelpers.GetMember(libDropdown, "onValueChanged");
            var addListener = evt.GetType().GetMethod("AddListener");
            UnityAction<int> handler = (index) => {
                int songIndex = index - 1;
                if (songIndex < 0) {
                    LocalIsSelected = false;
                    return;
                }
                var songs = cachedSongs;
                if (songIndex >= songs.Count) return;
                Stage(mp3MenuInstance, songs[songIndex]);
                lastSourceFor.Remove(mp3MenuInstance);
                lastSourceFor.Add(mp3MenuInstance, "local");
                LocalIsSelected = true;
                Multiplayer.DoMultiplayerStatusMessageLog("mp3client: staged " + Path.GetFileName(songs[songIndex]) + ", press Play.");
            };
            addListener.Invoke(evt, new object[] { handler });
        }
    }
    internal static class ButtonTemplates {
        internal struct Set {
            public GameObject Browse;
            public GameObject Stop;
            public GameObject Pause;
        }
        internal static Set CreateAll(Component anchorButton, object mp3MenuInstance) {
            var set = new Set();
            set.Browse = Clone(anchorButton.gameObject, "MP3Client_BrowseButton", () => Patch_InterceptBrowsePlay.BrowseAndPackage(mp3MenuInstance));
            ApplyIconOnly(set.Browse, "BrowseIcon.png", "FILE");
            set.Stop = Clone(set.Browse, "MP3Client_StopButton", LocalLibraryUI.StopOrCancel);
            ApplyLabeled(set.Stop, "Stop", MP3TogetherDebug.ColoredButtons.Value ? new Color(1f, 0.25f, 0.25f, 0.85f) : Color.white, "StopIcon.png");
            set.Pause = Clone(set.Browse, "MP3Client_PauseButton", LocalLibraryUI.OnPauseButtonClicked);
            ApplyLabeled(set.Pause, "Pause", MP3TogetherDebug.ColoredButtons.Value ? new Color(1f, 0.75f, 0.1f, 0.85f) : Color.white, "PauseIcon.png");
            return set;
        }
        internal static GameObject Clone(GameObject template, string name, Action onClick) {
            GameObject go = UnityEngine.Object.Instantiate(template, template.transform.parent, false);
            go.name = name;
            WireClick(ReplaceButtonComponent(go), onClick);
            return go;
        }
        internal static void WireClick(object buttonComp, Action onClick) {
            var onClickObj = ReflectionHelpers.GetMember(buttonComp, "onClick");
            var addListener = onClickObj.GetType().GetMethod("AddListener");
            UnityAction handler = () => onClick();
            addListener.Invoke(onClickObj, new object[] { handler });
        }
        private static void AddFallbackLabel(GameObject parent, string text) {
            var uiTextType = UITypes.Text;
            GameObject labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(parent.transform, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero; labelRect.offsetMax = Vector2.zero;
            var textComp = labelGO.AddComponent(uiTextType);
            ReflectionHelpers.SetMember(textComp, "text", text);
            ReflectionHelpers.SetMember(textComp, "color", Color.white);
            ReflectionHelpers.SetMember(textComp, "alignment", 4);
        }
        internal static void ApplyIconOnly(GameObject go, string iconName, string fallbackText) {
            var imageType = UITypes.Image;
            Sprite customSprite = LoadEmbeddedIcon(iconName);
            Component iconImage = null;
            foreach (Component img in go.GetComponentsInChildren(imageType, true)) {
                if (img.gameObject == go) continue;
                if (iconImage == null) {
                    iconImage = img;
                    img.gameObject.SetActive(true);
                } else {
                    img.gameObject.SetActive(false);
                }
            }
            if (customSprite != null && iconImage != null) {
                ReflectionHelpers.SetMember(iconImage, "sprite", customSprite);
                ReflectionHelpers.SetMember(iconImage, "color", Color.white);
                var tmpTextType = UITypes.TMPText;
                if (tmpTextType != null) {
                    foreach (Component txt in go.GetComponentsInChildren(tmpTextType, true)) {
                        UnityEngine.Object.DestroyImmediate(txt.gameObject);
                    }
                }
            } else if (customSprite == null) {
                AddFallbackLabel(go, fallbackText);
            }
        }
        internal static void ApplyLabeled(GameObject go, string label, Color color, string iconName) {
            var tmpTextType = UITypes.TMPText;
            bool labelSet = false;
            if (tmpTextType != null) {
                foreach (Component txt in go.GetComponentsInChildren(tmpTextType, true)) {
                    ReflectionHelpers.SetMember(txt, "text", label);
                    txt.gameObject.SetActive(true);
                    labelSet = true;
                }
            }
            var uiTextType = UITypes.Text;
            if (uiTextType != null) {
                foreach (Component txt in go.GetComponentsInChildren(uiTextType, true)) {
                    ReflectionHelpers.SetMember(txt, "text", label);
                    txt.gameObject.SetActive(true);
                    labelSet = true;
                }
            }
            if (!labelSet && uiTextType != null) AddFallbackLabel(go, label);
            var imageType = UITypes.Image;
            var mainImage = go.GetComponent(imageType);
            if (mainImage != null) ReflectionHelpers.SetMember(mainImage, "color", color);
            if (!string.IsNullOrEmpty(iconName)) {
                Sprite customSprite = LoadEmbeddedIcon(iconName);
                if (customSprite != null) {
                    foreach (Component img in go.GetComponentsInChildren(imageType, true)) {
                        if (img.gameObject == go) continue;
                        if (img.gameObject.activeSelf) {
                            ReflectionHelpers.SetMember(img, "sprite", customSprite);
                            break;
                        }
                    }
                }
            }
        }
        internal static Sprite LoadEmbeddedIcon(string resourceFileName) {
            try {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(r => r.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(resourceName)) {
                    MissingFiles.NoteMissingResource(resourceFileName);
                    return null;
                }
                using (var stream = assembly.GetManifestResourceStream(resourceName)) {
                    if (stream == null) return null;
                    byte[] buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    MethodInfo legacyLoad = typeof(Texture2D).GetMethod("LoadImage", new[] { typeof(byte[]) });
                    if (legacyLoad != null) {
                        legacyLoad.Invoke(texture, new object[] { buffer });
                    } else {
                        Type imgConvType = AccessTools.TypeByName("UnityEngine.ImageConversion");
                        var modernLoad = imgConvType?.GetMethod("LoadImage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Texture2D), typeof(byte[]) }, null);
                        modernLoad?.Invoke(null, new object[] { texture, buffer });
                    }
                    return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                }
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogError("mp3client: Icon load exception: " + e);
                return null;
            }
        }
        internal static object ReplaceButtonComponent(GameObject go) {
            var buttonType = UITypes.Button;
            var oldButton = go.GetComponent(buttonType);
            var targetGraphic = ReflectionHelpers.GetMember(oldButton, "targetGraphic");
            var transition = ReflectionHelpers.GetMember(oldButton, "transition");
            UnityEngine.Object.DestroyImmediate(oldButton);
            var newButton = go.AddComponent(buttonType);
            if (targetGraphic != null) ReflectionHelpers.SetMember(newButton, "targetGraphic", targetGraphic);
            if (transition != null) ReflectionHelpers.SetMember(newButton, "transition", transition);
            return newButton;
        }
    }
    internal static class PendingPromptUI {
        internal static void Show(Action onAccept, Action onReject) {
            try {
                var prompt = new UIChoicePrompt.Prompt();
                prompt.name = "MP3Client";
                prompt.text = "A player wants to load custom music. Load it now?";
                prompt.timeout = 30f;
                prompt.defaultcanignore = true;
                var accept = new UIChoicePrompt.Prompt.PromptChoice();
                accept.text = "Accept";
                accept.action = delegate () {
                    MP3TogetherDebug.Debug.Trace("ShowPendingPrompt", "Accept clicked");
                    onAccept();
                };
                var reject = new UIChoicePrompt.Prompt.PromptChoice();
                reject.text = "Reject";
                reject.action = delegate () {
                    MP3TogetherDebug.Debug.Trace("ShowPendingPrompt", "Reject clicked");
                    onReject();
                };
                prompt.choices = new UIChoicePrompt.Prompt.PromptChoice[] { accept, reject };
                UIChoicePrompt.ShowPrompt(prompt);
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: ShowPendingPrompt failed, falling back to MP3accept/MP3reject console commands: " + e.Message);
            }
        }
        internal static void Hide() {
            try {
                var promptType = AccessTools.TypeByName("UIChoicePrompt");
                if (promptType == null) return;
                foreach (string name in new[] { "HidePrompt", "ClosePrompt", "Hide", "Close", "Dismiss", "CancelPrompt" }) {
                    var m = AccessTools.Method(promptType, name);
                    if (m != null && m.GetParameters().Length == 0) { m.Invoke(null, null); return; }
                }
                MP3TogetherDebug.Debug.Trace("TryHideNativePrompt", "no known hide method found on UIChoicePrompt - prompt state is cleared, but the visual will linger until timeout or manually clicked");
            } catch (Exception e) {
                MP3TogetherDebug.Debug.Trace("TryHideNativePrompt", "couldn't hide the native prompt automatically: " + e.Message);
            }
        }
    }
    internal static class Patch_InterceptBrowsePlay {
        private const string FileFilter = "Audio files (*.mp3;*.wav;*.ogg)\0*.mp3;*.wav;*.ogg\0All files\0*.*\0\0";
        private static bool browseOpen;
        public static void BrowseAndPackage(object mp3MenuInstance) {
            if (browseOpen) return;
            browseOpen = true;
            NativeFileDialog.ShowAsync(FileFilter, path => {
                browseOpen = false;
                if (string.IsNullOrEmpty(path)) {
                    return;
                }
                string packaged;
                try {
                    packaged = CustomMusicFolder.CopyIn(path);
                } catch (Exception e) {
                    Multiplayer.DoMultiplayerStatusMessageError("mp3client: couldn't copy into custommusic: " + e.Message);
                    return;
                }
                LocalLibraryUI.SelectLocal(mp3MenuInstance, packaged);
            });
        }
        public static void SendStagedSong(object mp3MenuInstance, string path) {
            SendLocalFile(mp3MenuInstance, path);
        }
        internal static readonly Dictionary<ushort, Coroutine> resendRoutines = new Dictionary<ushort, Coroutine>();
        internal static void StopResend(ushort syncId) {
            Coroutine co;
            if (!resendRoutines.TryGetValue(syncId, out co)) return;
            if (co != null) MP3TogetherDebug.Instance.StopCoroutine(co);
            resendRoutines.Remove(syncId);
        }
        internal static void StartResend(ushort syncId, string ext, byte[] bytes, int fromOffset) {
            StopResend(syncId);
            resendRoutines[syncId] = MP3TogetherDebug.Instance.StartCoroutine(SendFileCoroutine(syncId, ext, bytes, fromOffset, false));
        }
        public static void SendCancel(ushort syncId) {
            var writer = Protocol.Writer(Protocol.MSG_CANCEL);
            writer.Put(syncId);
            var dm = DeliveryMethod.ReliableOrdered;
            Net.Client_Send(in dm, in writer);
        }
        public static void SendPause(ushort syncId) {
            var writer = Protocol.Writer(Protocol.MSG_PAUSE);
            writer.Put(syncId);
            var dm = DeliveryMethod.ReliableOrdered;
            Net.Client_Send(in dm, in writer);
        }
        internal static bool TryGetCurrentPlayerSyncId(out ushort syncId) {
            syncId = 0;
            var itemField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
            var itemObj = itemField.GetValue(null);
            if (itemObj == null) return false;
            SyncInfo si;
            if (!NetObjectRegistry.TryGetSyncInfoOrRegister((Component)itemObj, out si)) return false;
            syncId = si.syncId;
            return true;
        }
        private static void SendLocalFile(object mp3MenuInstance, string path) {
            var itemField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
            var itemObj = itemField.GetValue(null);
            if (itemObj == null) {
                Multiplayer.DoMultiplayerStatusMessageError("mp3client: no mp3 player selected.");
                return;
            }
            var component = (Component)itemObj;
            SyncInfo si;
            if (!NetObjectRegistry.TryGetSyncInfoOrRegister(component, out si)) {
                Multiplayer.DoMultiplayerStatusMessageError("mp3client: mp3 player not registered on network yet, try again.");
                return;
            }
            ushort syncId = si.syncId;
            long length;
            try {
                length = new FileInfo(path).Length;
            } catch (Exception e) {
                Multiplayer.DoMultiplayerStatusMessageError("mp3client: couldn't read " + path + ": " + e.Message);
                return;
            }
            long limit = HostPluginCheck.HostMaxFileBytes > 0 ? HostPluginCheck.HostMaxFileBytes : Protocol.CLIENT_ABSOLUTE_MAX_BYTES;
            if (length <= 0 || length > limit) {
                Multiplayer.DoMultiplayerStatusMessageError(length <= 0 ? "mp3client: " + Path.GetFileName(path) + " is empty." : "mp3client: " + Path.GetFileName(path) + " is " + (length / 1048576f).ToString("F1") + " MB, the host allows " + (limit / 1048576f).ToString("F1") + " MB.");
                MP3TogetherDebug.Debug.SendBlockedBySize(syncId, length, limit);
                return;
            }
            byte[] bytes;
            try {
                bytes = File.ReadAllBytes(path);
            } catch (Exception e) {
                Multiplayer.DoMultiplayerStatusMessageError("mp3client: couldn't read " + path + ": " + e.Message);
                return;
            }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".mp3" && ext != ".wav" && ext != ".ogg") {
                Multiplayer.DoMultiplayerStatusMessageError("mp3client: unsupported file type " + ext);
                return;
            }
            LocalLibraryUI.SetSongActive(true);
            LocalLibraryUI.BeginSend(mp3MenuInstance);
            SendFile(mp3MenuInstance, syncId, ext, bytes);
        }
        internal static readonly Dictionary<ushort, (string Ext, byte[] Bytes)> pendingFiles = new Dictionary<ushort, (string, byte[])>();
        internal static void ForgetPending(ushort syncId) {
            pendingFiles.Remove(syncId);
        }
        private static IEnumerator ReleasePendingLater(ushort syncId, byte[] bytes) {
            yield return new WaitForSecondsRealtime(Protocol.OVERALL_DEADLINE_SECONDS);
            (string Ext, byte[] Bytes) cached;
            if (pendingFiles.TryGetValue(syncId, out cached) && ReferenceEquals(cached.Bytes, bytes)) pendingFiles.Remove(syncId);
        }
        static void SendFile(object mp3MenuInstance, ushort syncId, string ext, byte[] bytes) {
            StopResend(syncId);
            pendingFiles[syncId] = (ext, bytes);
            var co = MP3TogetherDebug.Instance.StartCoroutine(SendFileCoroutine(syncId, ext, bytes, 0, true, mp3MenuInstance));
            LocalLibraryUI.RegisterTransfer(mp3MenuInstance, syncId, co);
        }
        internal static IEnumerator SendFileCoroutine(ushort syncId, string ext, byte[] bytes, int startOffset, bool sendBegin, object mp3MenuInstance = null) {
            float startTime = Time.unscaledTime;
            string exitReason = "interrupted/unknown (finally reached without normal completion)";
            try {
                ushort transferId = sendBegin ? Patch_RegisterClientReceivers.BeginTransfer(syncId) : Patch_RegisterClientReceivers.CurrentTransferId(syncId);
                if (sendBegin) {
                    var beginWriter = Protocol.Writer(Protocol.MSG_BEGIN);
                    beginWriter.Put(syncId);
                    beginWriter.Put(bytes.Length);
                    beginWriter.Put(ext);
                    beginWriter.Put(transferId);
                    var dmBegin = DeliveryMethod.ReliableOrdered;
                    Net.Client_Send(in dmBegin, in beginWriter);
                    MP3TogetherDebug.Debug.SendStart(syncId, ext, bytes.Length);
                    yield return null;
                }
                int offset = startOffset;
                int chunkIndex = 0;
                int ackBase = Patch_RegisterClientReceivers.AckCount(syncId);
                int sentInRun = 0;
                int seenAck = ackBase;
                float lastProgress = Time.unscaledTime;
                bool stallLogged = false;
                int[] percentThresholds = { 0, 25, 50, 75, 100 };
                int nextThresholdIndex = 0;
                int startPercent = bytes.Length > 0 ? (int)((offset / (float)bytes.Length) * 100f) : 0;
                while (nextThresholdIndex < percentThresholds.Length && percentThresholds[nextThresholdIndex] <= startPercent) nextThresholdIndex++;
                if (sendBegin) Multiplayer.DoMultiplayerStatusMessageLog("mp3client: staged with 0% package");
                while (offset < bytes.Length) {
                    int len = Math.Min(Protocol.CHUNK_SIZE, bytes.Length - offset);
                    bool throttled = false;
                    while (true) {
                        int nowAck = Patch_RegisterClientReceivers.AckCount(syncId);
                        bool windowFull = HostPluginCheck.HostProtocol >= 2 && sentInRun - (nowAck - ackBase) >= Protocol.WINDOW_CHUNKS;
                        int pending;
                        bool steamFull = SteamSendGate.TryGetPendingBytes(out pending) && pending + len > Protocol.STEAM_PENDING_LIMIT_BYTES;
                        if (!windowFull && !steamFull) {
                            if (SendLimiter.TryTake(len)) break;
                            if (!throttled) {
                                throttled = true;
                                MP3TogetherDebug.Debug.SendThrottled(syncId, "rate");
                            }
                            lastProgress = Time.unscaledTime;
                            yield return null;
                            continue;
                        }
                        if (!throttled) {
                            throttled = true;
                            MP3TogetherDebug.Debug.SendThrottled(syncId, windowFull ? "window" : "steam");
                        }
                        if (nowAck != seenAck) {
                            seenAck = nowAck;
                            lastProgress = Time.unscaledTime;
                            stallLogged = false;
                        }
                        float idle = Time.unscaledTime - lastProgress;
                        if (idle > Protocol.ACK_STALL_SECONDS && !stallLogged) {
                            stallLogged = true;
                            MP3TogetherDebug.Debug.SendAckStalled(syncId);
                        }
                        if (idle > Protocol.SEND_ABORT_SECONDS) {
                            Multiplayer.DoMultiplayerStatusMessageError("mp3client: send aborted - no progress for " + Protocol.SEND_ABORT_SECONDS.ToString("F0") + "s (host not acknowledging or the connection is saturated).");
                            Patch_InterceptBrowsePlay.SendCancel(syncId);
                            ForgetPending(syncId);
                            MP3TogetherDebug.Debug.SendAborted(syncId, "no progress for " + Protocol.SEND_ABORT_SECONDS.ToString("F0") + "s");
                            if (mp3MenuInstance != null) LocalLibraryUI.ClearIfCurrent(mp3MenuInstance, syncId);
                            exitReason = "aborted: no progress for " + Protocol.SEND_ABORT_SECONDS.ToString("F0") + "s";
                            yield break;
                        }
                        yield return null;
                    }
                    byte[] chunk = new byte[len];
                    Array.Copy(bytes, offset, chunk, 0, len);
                    var chunkWriter = Protocol.Writer(Protocol.MSG_CHUNK);
                    chunkWriter.Put(syncId);
                    chunkWriter.Put(offset);
                    chunkWriter.PutBytesWithLength(chunk);
                    var dmChunk = DeliveryMethod.ReliableOrdered;
                    bool chunkSent = true;
                    try {
                        Net.Client_Send(in dmChunk, in chunkWriter);
                    } catch (Exception e) {
                        chunkSent = false;
                        MP3TogetherDebug.Debug.SendChunkFailed(syncId, chunkIndex, offset, e);
                        CrashLog.Capture("send chunk", e);
                    }
                    if (!chunkSent) {
                        Multiplayer.DoMultiplayerStatusMessageError("mp3client: send failed - the connection rejected a chunk, transfer stopped.");
                        try { Patch_InterceptBrowsePlay.SendCancel(syncId); } catch (Exception) { }
                        ForgetPending(syncId);
                        MP3TogetherDebug.Debug.SendAborted(syncId, "chunk send failed");
                        if (mp3MenuInstance != null) LocalLibraryUI.ClearIfCurrent(mp3MenuInstance, syncId);
                        exitReason = "aborted: chunk send failed";
                        yield break;
                    }
                    offset += len;
                    sentInRun++;
                    chunkIndex++;
                    lastProgress = Time.unscaledTime;
                    int percent = bytes.Length > 0 ? (int)((offset / (float)bytes.Length) * 100f) : 100;
                    while (nextThresholdIndex < percentThresholds.Length && percent >= percentThresholds[nextThresholdIndex]) {
                        Multiplayer.DoMultiplayerStatusMessageLog("mp3client: staged with " + percentThresholds[nextThresholdIndex] + "% package");
                        MP3TogetherDebug.Debug.SendProgress(syncId, offset, bytes.Length, Time.unscaledTime - startTime);
                        nextThresholdIndex++;
                    }
                    if (HostPluginCheck.HostProtocol < 2) yield return new WaitForSeconds(Protocol.LEGACY_CHUNK_INTERVAL_SECONDS);
                    else if (chunkIndex % Protocol.CHUNKS_PER_FRAME == 0) yield return null;
                }
                yield return null;
                var endWriter = Protocol.Writer(Protocol.MSG_END);
                endWriter.Put(syncId);
                var dmEnd = DeliveryMethod.ReliableOrdered;
                Net.Client_Send(in dmEnd, in endWriter);
                MP3TogetherDebug.Debug.SendDone(syncId, bytes.Length, Time.unscaledTime - startTime);
                if (sendBegin) MP3TogetherDebug.Instance.StartCoroutine(ReleasePendingLater(syncId, bytes));
                if (mp3MenuInstance != null) LocalLibraryUI.ClearIfCurrent(mp3MenuInstance, syncId);
                exitReason = "completed normally";
            } finally {
                MP3TogetherDebug.Debug.SendCoroutineExit(syncId, exitReason);
            }
        }
    }
    internal static class NativeFileDialog {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }
        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName([In, Out] ref OPENFILENAME ofn);
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        public static void ShowAsync(string filter, Action<string> onResult) {
            var thread = new Thread(() => {
                string result = null;
                try {
                    result = ShowBlocking(filter);
                } catch (Exception e) {
                    MP3TogetherDebug.Log.LogError("mp3client: file dialog failed: " + e);
                }
                MP3TogetherDebug.RunOnMainThread(() => onResult(result));
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
        private static string ShowBlocking(string filter) {
            var ofn = new OPENFILENAME();
            ofn.lStructSize = Marshal.SizeOf(ofn);
            ofn.lpstrFilter = filter;
            IntPtr fileBuffer = Marshal.AllocHGlobal(2048);
            try {
                Marshal.WriteInt16(fileBuffer, 0, 0);
                ofn.lpstrFile = fileBuffer;
                ofn.nMaxFile = 1024;
                ofn.lpstrTitle = "Pick a song to broadcast";
                ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
                if (GetOpenFileName(ref ofn)) return Marshal.PtrToStringUni(fileBuffer);
                return null;
            } finally {
                Marshal.FreeHGlobal(fileBuffer);
            }
        }
    }
}
#endif