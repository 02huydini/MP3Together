using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
#if !UPDATER_CHECK
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Together;
#endif

namespace MP3Together {
    internal static class UpdaterConfig {
        public const string RELEASE_URL = "https://api.github.com/repos/02huydini/MP3Together/releases/latest";
        public const string PLUGIN_VERSION = "0.1";
        public const string TAG = "MP3Together";
        public const string FOLDER_NAME = "MP3Together";
        public const string DLL_NAME = "MP3Together.dll";
        public const string GITHUB_URL = "https://github.com/02huydini/MP3Together/releases/latest";
        public const string NEXUS_URL = "https://www.nexusmods.com/games/scavprototype/mods/850";
    }
    internal sealed class UpdateFiles {
        private readonly string dllPath;
        internal UpdateFiles(string dllPath) {
            this.dllPath = dllPath;
        }
        internal string DllPath { get { return dllPath; } }
        internal string Folder { get { return Path.GetDirectoryName(dllPath); } }
        internal string OriginalPath { get { return dllPath + ".orig"; } }
        internal string NewPath { get { return dllPath + ".new"; } }
        internal string OldPath { get { return dllPath + ".old"; } }
        internal bool HasOriginal { get { return File.Exists(OriginalPath); } }
        internal sealed class UpdateState {
            public string Version = "";
            public string Status = "none";
            public int Starts;
        }
        internal enum LaunchAction { Normal, Fallback }
        internal static LaunchAction BeginLaunch(UpdateState state, string runningVersion, int maxFailedStarts) {
            if (!SameVersion(state.Version, runningVersion) || state.Status == "none") {
                state.Version = runningVersion;
                state.Status = "trial";
                state.Starts = 1;
                return LaunchAction.Normal;
            }
            if (state.Status == "failed") return LaunchAction.Fallback;
            if (state.Status != "trial") return LaunchAction.Normal;
            if (state.Starts >= maxFailedStarts) return LaunchAction.Fallback;
            state.Starts++;
            return LaunchAction.Normal;
        }
        internal static string Sha256Hex(byte[] data) {
            using (var sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
        internal static bool SameVersion(string a, string b) {
            Version x, y;
            return Version.TryParse(a, out x) && Version.TryParse(b, out y) && x == y;
        }
        internal static bool SamePath(string a, string b) {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        internal static bool LooksLikeDll(byte[] data) {
            return data != null && data.Length >= 1024 && data[0] == 0x4D && data[1] == 0x5A;
        }
        private static byte[] ReadShared(string path) {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                var data = new byte[fs.Length];
                int read = 0;
                while (read < data.Length) {
                    int n = fs.Read(data, read, data.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != data.Length) throw new IOException("short read of " + Path.GetFileName(path));
                return data;
            }
        }
        internal bool BackupOriginal(out string hash, out string error) {
            hash = null;
            error = null;
            if (HasOriginal) return false;
            string temp = OriginalPath + ".tmp";
            try {
                byte[] data = ReadShared(dllPath);
                if (!LooksLikeDll(data)) throw new IOException("the running plugin file is not a valid dll");
                File.WriteAllBytes(temp, data);
                if (new FileInfo(temp).Length != data.Length) throw new IOException("short write of the original copy");
                File.Move(temp, OriginalPath);
                hash = Sha256Hex(data);
                return true;
            } catch (Exception e) {
                error = e.Message;
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                return false;
            }
        }
        internal bool VerifyOriginal(string expectedHash, out string actualHash, out string error) {
            actualHash = null;
            error = null;
            try {
                byte[] data = ReadShared(OriginalPath);
                if (!LooksLikeDll(data)) {
                    error = "the saved copy is not a valid dll";
                    return false;
                }
                actualHash = Sha256Hex(data);
                if (!string.IsNullOrEmpty(expectedHash) && expectedHash.Trim().ToLowerInvariant() != actualHash) {
                    error = "the checksum does not match, the saved copy was changed or damaged";
                    return false;
                }
                return true;
            } catch (Exception e) {
                error = e.Message;
                return false;
            }
        }
        internal bool ApplyNew() {
            if (!File.Exists(NewPath)) return false;
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(dllPath, OldPath);
            try {
                File.Move(NewPath, dllPath);
            } catch (Exception) {
                File.Move(OldPath, dllPath);
                throw;
            }
            try { File.Delete(OldPath); } catch (Exception) { }
            return true;
        }
        internal int Install(byte[] data, out string error, out bool applied) {
            error = null;
            applied = false;
            string temp = NewPath + ".tmp";
            try {
                if (!LooksLikeDll(data)) throw new IOException("the file is not a valid plugin dll");
                File.WriteAllBytes(temp, data);
                if (File.Exists(NewPath)) File.Delete(NewPath);
                File.Move(temp, NewPath);
            } catch (Exception e) {
                error = e.Message;
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                return 0;
            }
            try {
                applied = ApplyNew();
            } catch (Exception e) {
                error = e.Message;
            }
            return applied ? 2 : 1;
        }
        internal int Cleanup(List<string> notes) {
            int removed = 0;
            foreach (string path in new[] { OldPath, NewPath + ".tmp", OriginalPath + ".tmp" }) {
                try {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    removed++;
                    notes.Add("removed " + Path.GetFileName(path));
                } catch (Exception e) {
                    notes.Add("couldn't remove " + Path.GetFileName(path) + " yet (" + e.Message + "), will retry next launch");
                }
            }
            return removed;
        }
    }
#if !UPDATER_CHECK
    internal static class Updater {
        private static UpdateFiles files;
        private static ConfigEntry<bool> autoUpdate;
        private static ConfigEntry<string> skippedVersion;
        private static ConfigEntry<bool> revertRequested;
        private static ConfigEntry<string> installedVersion;
        private static ConfigEntry<string> installedStatus;
        private static ConfigEntry<int> failedStarts;
        private static ConfigEntry<string> originalVersion;
        private static ConfigEntry<string> originalHash;
        private static string availableVersion;
        private static bool queuing;
        private static bool swappedThisSession;
        private static bool healthyThisSession;
        internal static bool RevertedThisSession;
        internal static string Folder { get { return Path.Combine(Paths.PluginPath, UpdaterConfig.FOLDER_NAME); } }
        internal static UpdateFiles Files {
            get {
                if (files == null) files = new UpdateFiles(Assembly.GetExecutingAssembly().Location);
                return files;
            }
        }
        internal static bool AutoEnabled { get { return autoUpdate == null || autoUpdate.Value; } }
        internal static bool MainMenuIdle { get { return WorldGeneration.world == null && !Krok.IsRunning; } }
        private static IEnumerator WaitForMainMenu() {
            while (!MainMenuIdle) yield return new WaitForSecondsRealtime(0.5f);
        }
        internal static string SetAuto(bool enabled) {
            if (autoUpdate != null) autoUpdate.Value = enabled;
            return enabled ? "auto-update is on." : "auto-update is off, you'll get a prompt.";
        }
        internal static void BindConfig(ConfigFile config) {
            var oldDefinition = new ConfigDefinition("Updater", "AutoUpdate");
            var newDefinition = new ConfigDefinition("Updater", "Auto-updater");
            bool initial = true;
            try {
                var property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var orphans = property != null ? property.GetValue(config, null) as Dictionary<ConfigDefinition, string> : null;
                if (orphans != null) {
                    string oldValue;
                    bool parsed;
                    if (orphans.TryGetValue(oldDefinition, out oldValue) && !orphans.ContainsKey(newDefinition) && bool.TryParse(oldValue, out parsed)) initial = parsed;
                    orphans.Remove(oldDefinition);
                }
            } catch (Exception) { }
            autoUpdate = config.Bind(newDefinition, initial, new ConfigDescription("On: Download and install every newest version automatically. Off: Nothing is installed, you will get a prompt once for each new version in the main menu and install it yourself."));
            skippedVersion = config.Bind("Updater", "SkippedVersion", "", "The newest version you already answered the prompt for. You are not asked again for it.");
            revertRequested = config.Bind("Updater", "RevertToOriginal", false, "Set back to the original installed version. A permanent copy of it is kept in the plugins MP3Together folder as MP3Together.dll.orig.");
            installedVersion = config.Bind("Updater", "InstalledVersion", "", "Kept by the updater, do not edit. The version the updater last installed or started.");
            installedStatus = config.Bind("Updater", "InstalledStatus", "none", "Kept by the updater, do not edit. none, trial, healthy, pinned or failed.");
            failedStarts = config.Bind("Updater", "FailedStarts", 0, "Kept by the updater, do not edit. Starts of a new version that did not finish starting.");
            originalVersion = config.Bind("Updater", "OriginalVersion", "", "Kept by the updater, do not edit. The version of MP3Together.dll.orig.");
            originalHash = config.Bind("Updater", "OriginalSha256", "", "Kept by the updater, do not edit. Checksum of MP3Together.dll.orig.");
            revertRequested.SettingChanged += delegate (object sender, EventArgs args) {
                if (revertRequested.Value) RunRevertRequest();
            };
        }
        internal static UpdateFiles.UpdateState ReadState() {
            var state = new UpdateFiles.UpdateState();
            if (installedVersion == null) return state;
            string status = installedStatus.Value;
            if (status != "trial" && status != "healthy" && status != "pinned" && status != "failed") return state;
            state.Version = installedVersion.Value;
            state.Status = status;
            state.Starts = Math.Max(0, Math.Min(failedStarts.Value, 999));
            return state;
        }
        internal static void WriteState(UpdateFiles.UpdateState state) {
            if (installedVersion == null) return;
            installedVersion.Value = state.Version;
            installedStatus.Value = state.Status;
            failedStarts.Value = state.Starts;
        }
        internal static string Info(string message) {
            ConsoleOut.Info("mp3together: " + message);
            return message;
        }
        internal static string Fail(ManualLogSource log, string message) {
            log.LogError("[" + UpdaterConfig.TAG + "] " + message);
            ConsoleOut.Error("mp3together: " + message);
            return message;
        }
        internal static string Note(ManualLogSource log, string message) {
            log.LogInfo("[" + UpdaterConfig.TAG + "] " + message);
            ConsoleOut.Info("mp3together: " + message);
            return message;
        }
        private static void RunRevertRequest() {
            revertRequested.Value = false;
            Revert(MP3TogetherPlugin.Log);
        }
        internal static void ApplyRevertRequestAtStartup(ManualLogSource log) {
            if (revertRequested != null && revertRequested.Value) RunRevertRequest();
        }
        internal static void MoveIntoFolder(ManualLogSource log) {
            string tag = "[" + UpdaterConfig.TAG + "] ";
            try {
                string running = Assembly.GetExecutingAssembly().Location;
                string oldDir = Path.GetDirectoryName(running);
                string oldName = Path.GetFileName(running);
                Directory.CreateDirectory(Folder);
                string target = running;
                if (!UpdateFiles.SamePath(oldDir, Folder)) {
                    target = Path.Combine(Folder, UpdaterConfig.DLL_NAME);
                    try {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(running, target);
                        log.LogInfo(tag + "moved " + oldName + " into " + Folder + ", it loads from there from the next launch.");
                    } catch (Exception e) {
                        target = running;
                        log.LogWarning(tag + "couldn't move the plugin into " + Folder + " yet (" + e.Message + "), will retry next launch.");
                    }
                }
                files = new UpdateFiles(target);
            } catch (Exception e) {
                log.LogWarning(tag + "couldn't set up the " + UpdaterConfig.FOLDER_NAME + " folder: " + e.Message);
            }
        }
        internal static void EnsureOriginalBackup(ManualLogSource log) {
            string tag = "[" + UpdaterConfig.TAG + "] ";
            string hash, error;
            if (Files.BackupOriginal(out hash, out error)) {
                originalVersion.Value = UpdaterConfig.PLUGIN_VERSION;
                originalHash.Value = hash;
                log.LogInfo(tag + "kept a permanent copy of the version you installed (" + UpdaterConfig.PLUGIN_VERSION + ") as " + Path.GetFileName(Files.OriginalPath) + ". Type MP3version revert in the console to come back to it.");
            } else if (error != null) {
                log.LogWarning(tag + "couldn't keep a copy of the installed version: " + error);
            } else if (Files.HasOriginal && string.IsNullOrEmpty(originalVersion.Value)) {
                originalVersion.Value = "unknown";
            }
            if (!Files.HasOriginal) return;
            string actual;
            if (!Files.VerifyOriginal(originalHash.Value, out actual, out error)) log.LogError(tag + "your saved original version " + originalVersion.Value + " is damaged: " + error + ". MP3version revert will not use it.");
            else if (string.IsNullOrEmpty(originalHash.Value)) originalHash.Value = actual;
        }
        public static void ApplyPendingIfAny(ManualLogSource log) {
            string tag = UpdaterConfig.TAG;
            if (!File.Exists(Files.NewPath)) return;
            try {
                if (Files.ApplyNew()) {
                    swappedThisSession = true;
                    log.LogInfo("[" + tag + "] applied pending update.");
                }
            } catch (Exception e) {
                log.LogWarning("[" + tag + "] failed to apply pending update: " + e.Message);
            }
        }
        internal static void CleanupLeftovers(ManualLogSource log) {
            var notes = new List<string>();
            Files.Cleanup(notes);
            foreach (string note in notes) log.LogInfo("[" + UpdaterConfig.TAG + "] " + note);
        }
        private static int InstallGuarded(ManualLogSource log, byte[] data, string newVersion, string newStatus, out string error, out bool applied) {
            UpdateFiles.UpdateState previous = ReadState();
            WriteState(new UpdateFiles.UpdateState { Version = newVersion, Status = newStatus, Starts = 0 });
            int result = Files.Install(data, out error, out applied);
            if (result == 0) WriteState(previous);
            else if (applied) swappedThisSession = true;
            return result;
        }
        private static bool TryReadOriginal(out byte[] data, out string version, out string problem) {
            data = null;
            version = originalVersion.Value;
            problem = null;
            if (!Files.HasOriginal) {
                problem = "no original version is saved.";
                return false;
            }
            string actual, error;
            if (!Files.VerifyOriginal(originalHash.Value, out actual, out error)) {
                problem = "your saved original version is damaged (" + error + "), it was not used.";
                return false;
            }
            try {
                data = File.ReadAllBytes(Files.OriginalPath);
            } catch (Exception e) {
                problem = "couldn't read your saved original version: " + e.Message;
                return false;
            }
            return true;
        }
        internal static string Revert(ManualLogSource log) {
            byte[] data;
            string version, problem;
            if (!TryReadOriginal(out data, out version, out problem)) return Fail(log, problem);
            SetAuto(false);
            if (skippedVersion != null) skippedVersion.Value = "";
            if (UpdateFiles.SameVersion(version, UpdaterConfig.PLUGIN_VERSION) && !swappedThisSession && !File.Exists(Files.NewPath)) return Info("you are on your original version " + version + ". Auto-update is off.");
            string error;
            bool applied;
            int result = InstallGuarded(log, data, version, "pinned", out error, out applied);
            if (result == 0) return Fail(log, "couldn't go back to " + version + ": " + error);
            RevertedThisSession = true;
            return Info("going back to your original version " + version + " at the next launch. Auto-update is off, type MP3version check when you want a newer version.");
        }
        internal static bool RollBack(ManualLogSource log, string reason) {
            string tag = "[" + UpdaterConfig.TAG + "] ";
            byte[] data;
            string version, problem;
            if (!TryReadOriginal(out data, out version, out problem)) {
                log.LogError(tag + reason + " but " + problem);
                return false;
            }
            if (UpdateFiles.SameVersion(version, UpdaterConfig.PLUGIN_VERSION)) {
                log.LogError(tag + reason + " but this already is the original version, there is nothing earlier to go back to.");
                return false;
            }
            string error;
            bool applied;
            int result = InstallGuarded(log, data, version, "pinned", out error, out applied);
            if (result == 0) {
                log.LogError(tag + reason + " and going back to " + version + " failed: " + error);
                return false;
            }
            SetAuto(false);
            if (skippedVersion != null) skippedVersion.Value = "";
            RevertedThisSession = true;
            StartupGuard.Notice = "MP3Together " + UpdaterConfig.PLUGIN_VERSION + " " + reason + ", so it is going back to the original version " + version + ". It is " + (result == 2 ? "active" : "queued") + " after the next launch and MP3Together is switched off until then. Auto-update is off, type MP3version check when you want to see the newest version.";
            log.LogError(tag + StartupGuard.Notice);
            return true;
        }
        internal static void MarkHealthy(ManualLogSource log) {
            healthyThisSession = true;
            if (swappedThisSession) return;
            WriteState(new UpdateFiles.UpdateState { Version = UpdaterConfig.PLUGIN_VERSION, Status = "healthy", Starts = 0 });
        }
        private static string StateLabel(string status) {
            if (status == "healthy") return "started fine";
            if (status == "trial") return "new, still being checked";
            if (status == "pinned") return "restored";
            if (status == "failed") return "failed to start";
            return "first run";
        }
        internal static string VersionText() {
            UpdateFiles.UpdateState state = ReadState();
            return "Current Version: " + UpdaterConfig.PLUGIN_VERSION + ", " + StateLabel(state.Status) + ". Auto-updater: " + (AutoEnabled ? "on" : "off") + ". Change queued: " + (File.Exists(Files.NewPath) ? "yes" : "no") + ".";
        }
        private sealed class LatestResult {
            public bool Ok;
            public string Version;
            public string DllUrl;
            public string Error;
        }
        private static bool IsNewer(string remoteVersion) {
            Version local, remote;
            return Version.TryParse(UpdaterConfig.PLUGIN_VERSION, out local) && Version.TryParse(remoteVersion, out remote) && remote.CompareTo(local) > 0;
        }
        private static IEnumerator FetchLatest(LatestResult result) {
            using (UnityWebRequest www = UnityWebRequest.Get(UpdaterConfig.RELEASE_URL)) {
                www.SetRequestHeader("User-Agent", "bepinex-plugin-updater");
                www.SetRequestHeader("Accept", "application/vnd.github+json");
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success) {
                    result.Error = www.error;
                    yield break;
                }
                string json = www.downloadHandler.text;
                string remoteTag = ExtractField(json, "tag_name");
                if (string.IsNullOrEmpty(remoteTag)) {
                    result.Error = "the release has no version tag";
                    yield break;
                }
                result.Version = remoteTag.TrimStart('v', 'V');
                result.DllUrl = ExtractField(json, "browser_download_url", ".dll");
                result.Ok = true;
            }
        }
        public static IEnumerator CheckForUpdate(ManualLogSource log) {
            string tag = UpdaterConfig.TAG;
            if (RevertedThisSession) yield break;
            if (File.Exists(Files.NewPath)) yield break;
            var latest = new LatestResult();
            yield return FetchLatest(latest);
            if (!latest.Ok) {
                log.LogWarning("[" + tag + "] update check failed: " + latest.Error);
                yield break;
            }
            availableVersion = latest.Version;
            if (!IsNewer(latest.Version)) yield break;
            log.LogInfo("[" + tag + "] running " + UpdaterConfig.PLUGIN_VERSION + ", found " + latest.Version + " available.");
            if (AutoEnabled) {
                if (string.IsNullOrEmpty(latest.DllUrl)) {
                    log.LogWarning("[" + tag + "] update " + latest.Version + " found but release has no .dll asset.");
                    yield break;
                }
                yield return QueueDownload(log, latest.Version, latest.DllUrl);
                yield break;
            }
            if (skippedVersion != null && skippedVersion.Value == latest.Version) {
                log.LogInfo("[" + tag + "] you already answered the prompt for " + latest.Version + ", not asking again.");
                yield break;
            }
            MP3TogetherPlugin.Instance.StartCoroutine(ShowVersionPrompt(log, "MP3Together " + latest.Version + " is out, you have " + UpdaterConfig.PLUGIN_VERSION + ". Auto-update is off, so install it yourself from Github or Nexus.", latest.Version, true));
        }
        internal static IEnumerator ManualCheck(ManualLogSource log) {
            var latest = new LatestResult();
            yield return FetchLatest(latest);
            string text;
            string confirm = null;
            if (!latest.Ok) {
                text = "Couldn't check for updates (" + latest.Error + ").";
            } else {
                availableVersion = latest.Version;
                if (IsNewer(latest.Version)) {
                    text = "Latest MP3Together is " + latest.Version;
                    confirm = latest.Version;
                } else {
                    text = "You are up-to-date";
                }
            }
            if (latest.Ok) Info(text);
            else Fail(log, text);
            yield return ShowVersionPrompt(log, text, confirm, false, true);
        }
        private static IEnumerator QueueDownload(ManualLogSource log, string remoteVersion, string dllUrl) {
            if (queuing) yield break;
            queuing = true;
            try {
                using (UnityWebRequest dl = UnityWebRequest.Get(dllUrl)) {
                    yield return dl.SendWebRequest();
                    if (dl.result != UnityWebRequest.Result.Success) {
                        Fail(log, "update download failed: " + dl.error);
                        yield break;
                    }
                    string error;
                    bool applied;
                    int result = InstallGuarded(log, dl.downloadHandler.data, remoteVersion, "trial", out error, out applied);
                    if (result == 0) {
                        Fail(log, "failed to save update " + remoteVersion + ": " + error);
                        yield break;
                    }
                    if (applied) Note(log, "update " + remoteVersion + " installed, active after the next launch. If it fails to start twice, MP3Together goes back to your original version by itself.");
                    else Note(log, "update " + remoteVersion + " downloaded, will be applied on the next launch." + (error != null ? " (" + error + ")" : ""));
                }
            } finally {
                queuing = false;
            }
        }
        internal static void Confirm(string version) {
            if (!string.IsNullOrEmpty(version) && skippedVersion != null) skippedVersion.Value = version;
        }
        internal static void OpenPage(ManualLogSource log, string url) {
            try {
                Application.OpenURL(url);
            } catch (Exception) {
                Fail(log, "couldn't open the page, copy this link: " + url);
            }
        }
        internal static IEnumerator ShowVersionPrompt(ManualLogSource log, string text, string confirmVersion, bool onlyInMainMenu, bool manual = false) {
            string links = " Github: " + UpdaterConfig.GITHUB_URL + " Nexus: " + UpdaterConfig.NEXUS_URL;
            if (!Krok.Installed) {
                PromptFallback(log, text, links, manual);
                yield break;
            }
            if (onlyInMainMenu) yield return WaitForMainMenu();
            float deadline = Time.unscaledTime + 120f;
            while (!UpdatePrompts.Ready() && Time.unscaledTime < deadline) yield return new WaitForSecondsRealtime(0.5f);
            if (!UpdatePrompts.Ready()) {
                PromptFallback(log, text, links, manual);
                yield break;
            }
            try {
                UpdatePrompts.ShowUpdate(log, text, confirmVersion);
            } catch (Exception e) {
                CrashLog.Capture("update prompt", e);
                PromptFallback(log, text, links, manual);
            }
        }
        private static void PromptFallback(ManualLogSource log, string text, string links, bool manual) {
            if (manual) Info(links.Trim());
            else Note(log, text + links);
        }
        internal static IEnumerator ShowNotice(ManualLogSource log, string text) {
            Note(log, text);
            if (!Krok.Installed) yield break;
            yield return WaitForMainMenu();
            float deadline = Time.unscaledTime + 180f;
            while (!UpdatePrompts.Ready() && Time.unscaledTime < deadline) yield return new WaitForSecondsRealtime(0.5f);
            if (!UpdatePrompts.Ready()) yield break;
            try {
                UpdatePrompts.ShowNotice(text);
            } catch (Exception e) {
                CrashLog.Capture("notice prompt", e);
            }
        }
        private static string ExtractField(string json, string key, string suffix = null) {
            var pattern = "\"" + key + "\"\\s*:\\s*\"([^\"]+" + (suffix != null ? Regex.Escape(suffix) : "") + ")\"";
            var m = Regex.Match(json, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
    internal static class UpdatePrompts {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Ready() {
            return UIChoicePrompt.instance != null;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ShowUpdate(ManualLogSource log, string text, string confirmVersion) {
            var prompt = new UIChoicePrompt.Prompt();
            prompt.name = "MP3Together update";
            prompt.text = text;
            prompt.timeout = 90f;
            prompt.size = new Vector2(0.5f, 0.22f);
            prompt.defaultcanignore = true;
            var close = new UIChoicePrompt.Prompt.PromptChoice();
            close.text = "Close";
            close.action = delegate () {
                Updater.Confirm(confirmVersion);
            };
            var github = new UIChoicePrompt.Prompt.PromptChoice();
            github.text = "Github";
            github.action = delegate () {
                Updater.Confirm(confirmVersion);
                Updater.OpenPage(log, UpdaterConfig.GITHUB_URL);
            };
            var nexus = new UIChoicePrompt.Prompt.PromptChoice();
            nexus.text = "Nexus";
            nexus.action = delegate () {
                Updater.Confirm(confirmVersion);
                Updater.OpenPage(log, UpdaterConfig.NEXUS_URL);
            };
            prompt.choices = new UIChoicePrompt.Prompt.PromptChoice[] { close, github, nexus };
            UIChoicePrompt.ShowPrompt(prompt);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ShowNotice(string text) {
            var prompt = new UIChoicePrompt.Prompt();
            prompt.name = "MP3Together notice";
            prompt.text = text;
            prompt.timeout = 120f;
            prompt.size = new Vector2(0.55f, 0.24f);
            prompt.defaultcanignore = true;
            var ok = new UIChoicePrompt.Prompt.PromptChoice();
            ok.text = "OK";
            ok.action = delegate () { };
            prompt.choices = new UIChoicePrompt.Prompt.PromptChoice[] { ok };
            UIChoicePrompt.ShowPrompt(prompt);
        }
    }
    internal static class StartupGuard {
        internal const int MAX_FAILED_STARTS = 2;
        internal const float HEALTHY_AFTER_SECONDS = 15f;
        internal static bool SafeMode;
        internal static string Notice;
        internal static int CriticalFailures;
        internal static void Begin(ManualLogSource log) {
            try {
                if (File.Exists(Updater.Files.NewPath)) return;
                UpdateFiles.UpdateState state = Updater.ReadState();
                UpdateFiles.LaunchAction action = UpdateFiles.BeginLaunch(state, UpdaterConfig.PLUGIN_VERSION, MAX_FAILED_STARTS);
                if (action == UpdateFiles.LaunchAction.Normal) {
                    Updater.WriteState(state);
                    return;
                }
                string reason = state.Status == "failed" ? "failed during its last start" : "did not start correctly " + state.Starts + " times in a row";
                log.LogError("[" + UpdaterConfig.TAG + "] version " + UpdaterConfig.PLUGIN_VERSION + " " + reason + ", going back to the original version.");
                if (Updater.RollBack(log, reason)) SafeMode = true;
            } catch (Exception e) {
                log.LogWarning("[" + UpdaterConfig.TAG + "] start check failed: " + e.Message);
            }
        }
        internal static void Fail(ManualLogSource log, string reason) {
            CriticalFailures++;
            try {
                UpdateFiles.UpdateState state = Updater.ReadState();
                if (!UpdateFiles.SameVersion(state.Version, UpdaterConfig.PLUGIN_VERSION) || state.Status != "trial") return;
                state.Status = "failed";
                Updater.WriteState(state);
                if (Updater.RollBack(log, "failed to start (" + reason + ")")) SafeMode = true;
            } catch (Exception e) {
                log.LogWarning("[" + UpdaterConfig.TAG + "] couldn't record the failed start: " + e.Message);
            }
        }
        internal static IEnumerator ConfirmHealthy(ManualLogSource log) {
            while (!SceneManager.GetActiveScene().isLoaded) yield return null;
            float until = Time.unscaledTime + HEALTHY_AFTER_SECONDS;
            while (Time.unscaledTime < until) yield return null;
            if (CriticalFailures > 0 || SafeMode) yield break;
            try {
                Updater.MarkHealthy(log);
            } catch (Exception e) {
                log.LogWarning("[" + UpdaterConfig.TAG + "] couldn't record a healthy start: " + e.Message);
            }
        }
    }
#endif
}