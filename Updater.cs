using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
#if !UPDATER_CHECK
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Together;
#endif

namespace MP3Client {
    internal static class UpdaterConfig {
        public const string RELEASE_URL = "https://api.github.com/repos/02huydini/MP3Client/releases/latest";
        public const string PLUGIN_VERSION = "0.7.0";
        public const string TAG = "MP3Client";
        public const string GITHUB_URL = "https://github.com/02huydini/MP3Client/releases/latest";
        public const string NEXUS_URL = "https://www.nexusmods.com/scavprototype/mods/541";
    }
    internal sealed class UpdateFiles {
        private readonly string dllPath;
        internal UpdateFiles(string dllPath) {
            this.dllPath = dllPath;
        }
        internal string DllPath { get { return dllPath; } }
        internal string PendingPath { get { return dllPath + ".pending"; } }
        internal string PendingTempPath { get { return dllPath + ".pending.tmp"; } }
        internal string BackupPath { get { return dllPath + ".bak"; } }
        internal string OriginalPrefix { get { return Path.GetFileName(dllPath) + ".orig-"; } }
        internal string GoodPrefix { get { return Path.GetFileName(dllPath) + ".good-"; } }
        internal string StatePath { get { return dllPath + ".state"; } }
        internal string RecoveryPath { get { return Path.Combine(Path.GetDirectoryName(dllPath), "MP3Client_RECOVERY.txt"); } }
        internal const int KEEP_GOOD_COPIES = 2;
        internal static string Sha256Hex(byte[] data) {
            using (var sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
        internal static string HashPath(string copyPath) {
            return copyPath + ".sha256";
        }
        internal static bool SameVersion(string a, string b) {
            Version x, y;
            return Version.TryParse(a, out x) && Version.TryParse(b, out y) && x == y;
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
        internal sealed class CopyInfo {
            public Version Version;
            public string Path;
            public string Text;
        }
        internal List<CopyInfo> ListCopies(string prefix) {
            var list = new List<CopyInfo>();
            string dir = Path.GetDirectoryName(dllPath);
            foreach (string file in Directory.GetFiles(dir, prefix + "*")) {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) continue;
                string text = name.Substring(prefix.Length);
                Version parsed;
                if (!Version.TryParse(text, out parsed)) continue;
                list.Add(new CopyInfo { Version = parsed, Path = file, Text = text });
            }
            return list;
        }
        internal bool VerifyCopy(string path, out string error) {
            error = null;
            try {
                byte[] data = File.ReadAllBytes(path);
                if (!LooksLikeDll(data)) {
                    error = "the saved copy is not a valid dll";
                    return false;
                }
                string actual = Sha256Hex(data);
                string sidecar = HashPath(path);
                if (!File.Exists(sidecar)) {
                    File.WriteAllText(sidecar, actual);
                    return true;
                }
                string expected = File.ReadAllText(sidecar).Trim().ToLowerInvariant();
                if (expected != actual) {
                    error = "the checksum does not match, the saved copy was changed or damaged";
                    return false;
                }
                return true;
            } catch (Exception e) {
                error = e.Message;
                return false;
            }
        }
        internal sealed class UpdateState {
            public string Version = "";
            public string Status = "none";
            public int Starts;
        }
        internal UpdateState ReadState() {
            var state = new UpdateState();
            try {
                if (!File.Exists(StatePath)) return state;
                foreach (string line in File.ReadAllLines(StatePath)) {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    if (key == "version") state.Version = value;
                    else if (key == "status") state.Status = value;
                    else if (key == "starts") {
                        int n;
                        if (int.TryParse(value, out n) && n >= 0 && n < 1000) state.Starts = n;
                    }
                }
                if (state.Status != "trial" && state.Status != "healthy" && state.Status != "pinned" && state.Status != "failed") return new UpdateState();
                return state;
            } catch (Exception) {
                return new UpdateState();
            }
        }
        internal bool WriteState(UpdateState state) {
            string temp = StatePath + ".tmp";
            try {
                File.WriteAllText(temp, "version=" + state.Version + "\r\nstatus=" + state.Status + "\r\nstarts=" + state.Starts + "\r\n");
                if (File.Exists(StatePath)) File.Delete(StatePath);
                File.Move(temp, StatePath);
                return true;
            } catch (Exception) {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                return false;
            }
        }
        internal enum LaunchAction { Normal, Fallback }
        internal LaunchAction BeginLaunch(string runningVersion, int maxFailedStarts, out UpdateState state) {
            state = ReadState();
            if (!SameVersion(state.Version, runningVersion) || state.Status == "none") {
                state = new UpdateState { Version = runningVersion, Status = "trial", Starts = 1 };
                WriteState(state);
                return LaunchAction.Normal;
            }
            if (state.Status == "failed") return LaunchAction.Fallback;
            if (state.Status != "trial") return LaunchAction.Normal;
            if (state.Starts >= maxFailedStarts) return LaunchAction.Fallback;
            state.Starts++;
            WriteState(state);
            return LaunchAction.Normal;
        }
        internal bool PickFallback(string runningVersion, bool originalOnly, List<string> notes, out string path, out string version) {
            path = null;
            version = null;
            string error;
            if (!originalOnly) {
                var good = ListCopies(GoodPrefix);
                good.Sort(delegate (CopyInfo a, CopyInfo b) { return b.Version.CompareTo(a.Version); });
                foreach (CopyInfo copy in good) {
                    if (SameVersion(copy.Text, runningVersion)) continue;
                    if (VerifyCopy(copy.Path, out error)) {
                        path = copy.Path;
                        version = copy.Text;
                        return true;
                    }
                    notes.Add("saved copy " + System.IO.Path.GetFileName(copy.Path) + " is unusable: " + error);
                }
            }
            string originalPath, originalVersion;
            if (FindOriginal(out originalPath, out originalVersion) && !SameVersion(originalVersion, runningVersion)) {
                if (VerifyCopy(originalPath, out error)) {
                    path = originalPath;
                    version = originalVersion;
                    return true;
                }
                notes.Add("original copy " + System.IO.Path.GetFileName(originalPath) + " is unusable: " + error);
            }
            return false;
        }
        internal bool RecordGood(string version, string sourcePath, out string error) {
            error = null;
            string target = dllPath + ".good-" + version;
            string temp = target + ".tmp";
            try {
                byte[] data = ReadShared(sourcePath);
                if (!LooksLikeDll(data)) throw new IOException("the source file is not a valid dll");
                File.WriteAllBytes(temp, data);
                if (new FileInfo(temp).Length != data.Length) throw new IOException("short write of the saved copy");
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                File.WriteAllText(HashPath(target), Sha256Hex(data));
                var good = ListCopies(GoodPrefix);
                good.Sort(delegate (CopyInfo a, CopyInfo b) { return b.Version.CompareTo(a.Version); });
                for (int i = KEEP_GOOD_COPIES; i < good.Count; i++) {
                    try {
                        File.Delete(good[i].Path);
                        if (File.Exists(HashPath(good[i].Path))) File.Delete(HashPath(good[i].Path));
                    } catch (Exception) { }
                }
                return true;
            } catch (Exception e) {
                error = e.Message;
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                return false;
            }
        }
        internal void WriteRecoveryNote(string runningVersion) {
            try {
                var sb = new StringBuilder();
                sb.AppendLine("MP3Client recovery");
                sb.AppendLine("If MP3Client stops working after an update, close the game and do this in this folder:");
                sb.AppendLine("1. Delete MP3Client.dll (if it is missing, that is fine).");
                sb.AppendLine("2. Make a COPY of one of the files below and name the copy MP3Client.dll. Do not move or rename the saved file.");
                sb.AppendLine("3. Start the game.");
                sb.AppendLine();
                var good = ListCopies(GoodPrefix);
                good.Sort(delegate (CopyInfo a, CopyInfo b) { return b.Version.CompareTo(a.Version); });
                foreach (CopyInfo copy in good) sb.AppendLine("- " + Path.GetFileName(copy.Path) + "   (a version that started fine, " + copy.Text + ")");
                string originalPath, originalVersion;
                if (FindOriginal(out originalPath, out originalVersion)) sb.AppendLine("- " + Path.GetFileName(originalPath) + "   (the version you first installed, " + originalVersion + ")");
                sb.AppendLine("- " + Path.GetFileName(BackupPath) + "   (only present for a moment after an update)");
                sb.AppendLine();
                sb.AppendLine("Running when this note was written: " + runningVersion);
                sb.AppendLine("Written: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                File.WriteAllText(RecoveryPath, sb.ToString());
            } catch (Exception) { }
        }
        internal static bool LooksLikeDll(byte[] data) {
            return data != null && data.Length >= 1024 && data[0] == 0x4D && data[1] == 0x5A;
        }
        internal bool Promote() {
            if (!File.Exists(PendingPath)) return false;
            if (File.Exists(BackupPath)) File.Delete(BackupPath);
            File.Move(dllPath, BackupPath);
            try {
                File.Move(PendingPath, dllPath);
            } catch (Exception) {
                File.Move(BackupPath, dllPath);
                throw;
            }
            return true;
        }
        internal int Install(byte[] data, out string error, out bool promoted) {
            error = null;
            promoted = false;
            try {
                if (!LooksLikeDll(data)) throw new IOException("the file is not a valid plugin dll");
                File.WriteAllBytes(PendingTempPath, data);
                if (File.Exists(PendingPath)) File.Delete(PendingPath);
                File.Move(PendingTempPath, PendingPath);
            } catch (Exception e) {
                error = e.Message;
                try { if (File.Exists(PendingTempPath)) File.Delete(PendingTempPath); } catch (Exception) { }
                return 0;
            }
            try {
                promoted = Promote();
            } catch (Exception e) {
                error = e.Message;
            }
            return promoted ? 2 : 1;
        }
        internal bool FindOriginal(out string path, out string version) {
            path = null;
            version = null;
            Version best = null;
            foreach (CopyInfo copy in ListCopies(OriginalPrefix)) {
                if (best == null || copy.Version < best) {
                    best = copy.Version;
                    path = copy.Path;
                    version = copy.Text;
                }
            }
            return path != null;
        }
        internal bool BackupOriginal(string version, out string error) {
            error = null;
            string target = dllPath + ".orig-" + version;
            string temp = target + ".tmp";
            try {
                string existing, existingVersion;
                if (FindOriginal(out existing, out existingVersion)) return false;
                byte[] data = ReadShared(dllPath);
                if (!LooksLikeDll(data)) throw new IOException("the running plugin file is not a valid dll");
                File.WriteAllBytes(temp, data);
                if (new FileInfo(temp).Length != data.Length) throw new IOException("short write of the backup file");
                File.Move(temp, target);
                try { File.WriteAllText(HashPath(target), Sha256Hex(data)); } catch (Exception) { }
                return true;
            } catch (Exception e) {
                error = e.Message;
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                return false;
            }
        }
        internal int Cleanup(List<string> notes) {
            int removed = 0;
            var targets = new List<string> { BackupPath, PendingTempPath, StatePath + ".tmp" };
            try {
                string dir = Path.GetDirectoryName(dllPath);
                targets.AddRange(Directory.GetFiles(dir, OriginalPrefix + "*.tmp"));
                targets.AddRange(Directory.GetFiles(dir, GoodPrefix + "*.tmp"));
            } catch (Exception) { }
            foreach (string path in targets) {
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
        private static string availableVersion;
        private static string availableUrl;
        private static bool queuing;
        private static bool swappedThisSession;
        private static int swapCount;
        private static bool healthyThisSession;
        internal static bool RevertedThisSession;
        internal static UpdateFiles Files {
            get {
                if (files == null) files = new UpdateFiles(Assembly.GetExecutingAssembly().Location);
                return files;
            }
        }
        internal static bool AutoEnabled { get { return autoUpdate == null || autoUpdate.Value; } }
        internal static bool MainMenuIdle { get { return WorldGeneration.world == null && !Net.IsRunning; } }
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
            revertRequested = config.Bind("Updater", "RevertToOriginal", false, "Set back to original installed version. A permanent copy of it is kept next to the plugin as MP3Client.dll.orig-<version>.");
            revertRequested.SettingChanged += delegate (object sender, EventArgs args) {
                if (revertRequested.Value) RunRevertRequest();
            };
        }
        internal static string Say(ManualLogSource log, string message) {
            log.LogInfo("[" + UpdaterConfig.TAG + "] " + message);
            try { Multiplayer.DoMultiplayerStatusMessageLog("mp3client: " + message); } catch (Exception) { }
            return message;
        }
        private static void RunRevertRequest() {
            revertRequested.Value = false;
            Revert(MP3TogetherDebug.Log, false);
        }
        internal static void ApplyRevertRequestAtStartup(ManualLogSource log) {
            if (revertRequested != null && revertRequested.Value) RunRevertRequest();
        }
        internal static void EnsureOriginalBackup(ManualLogSource log) {
            string error;
            bool created = Files.BackupOriginal(UpdaterConfig.PLUGIN_VERSION, out error);
            if (created) log.LogInfo("[" + UpdaterConfig.TAG + "] kept a permanent copy of the version you installed (" + UpdaterConfig.PLUGIN_VERSION + ") as " + Path.GetFileName(Files.DllPath) + ".orig-" + UpdaterConfig.PLUGIN_VERSION + ". Type MP3revert in the console to come back to it.");
            else if (error != null) log.LogWarning("[" + UpdaterConfig.TAG + "] couldn't keep a copy of the installed version: " + error);
            string path, version;
            if (Files.FindOriginal(out path, out version) && !Files.VerifyCopy(path, out error)) log.LogError("[" + UpdaterConfig.TAG + "] your saved original version " + version + " is damaged: " + error + ". MP3revert will not use it.");
        }
        public static void ApplyPendingIfAny(ManualLogSource log) {
            string tag = UpdaterConfig.TAG;
            if (!File.Exists(Files.PendingPath)) return;
            try {
                if (Files.Promote()) {
                    swappedThisSession = true;
                    swapCount++;
                    log.LogInfo("[" + tag + "] applied pending update.");
                    try {
                        string legacy = Path.Combine(Paths.ConfigPath, "mp3client.cfg");
                        if (File.Exists(legacy)) File.Delete(legacy);
                    } catch (Exception) { }
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
        private static void PreserveRunningAsGood(ManualLogSource log) {
            if (swappedThisSession) return;
            UpdateFiles.UpdateState state = Files.ReadState();
            bool knownGood = healthyThisSession || (UpdateFiles.SameVersion(state.Version, UpdaterConfig.PLUGIN_VERSION) && (state.Status == "healthy" || state.Status == "pinned"));
            if (!knownGood) return;
            string error;
            if (!Files.RecordGood(UpdaterConfig.PLUGIN_VERSION, Files.DllPath, out error)) log.LogWarning("[" + UpdaterConfig.TAG + "] couldn't save the running version before the change: " + error);
        }
        private static int InstallGuarded(ManualLogSource log, byte[] data, string newVersion, string newStatus, out string error, out bool promoted) {
            PreserveRunningAsGood(log);
            UpdateFiles.UpdateState previous = Files.ReadState();
            Files.WriteState(new UpdateFiles.UpdateState { Version = newVersion, Status = newStatus, Starts = 0 });
            int result = Files.Install(data, out error, out promoted);
            if (result == 0) Files.WriteState(previous);
            else if (promoted) {
                swappedThisSession = true;
                swapCount++;
            }
            return result;
        }
        internal static string Revert(ManualLogSource log, bool lastGood) {
            string path, version;
            var notes = new List<string>();
            if (!Files.PickFallback(UpdaterConfig.PLUGIN_VERSION, !lastGood, notes, out path, out version)) {
                string original;
                bool hasOriginal = Files.FindOriginal(out original, out version);
                foreach (string note in notes) log.LogWarning("[" + UpdaterConfig.TAG + "] " + note);
                if (!lastGood && hasOriginal && UpdateFiles.SameVersion(version, UpdaterConfig.PLUGIN_VERSION)) {
                    SetAuto(false);
                    if (skippedVersion != null) skippedVersion.Value = "";
                    if (!swappedThisSession && !File.Exists(Files.PendingPath)) return Say(log, "you are on (" + version + "). Auto-update is off.");
                    string error0;
                    bool promoted0;
                    string verifyError;
                    if (!Files.VerifyCopy(original, out verifyError)) return Say(log, "your saved original version is damaged (" + verifyError + "), it was not used.");
                    int back = InstallGuarded(log, File.ReadAllBytes(original), version, "pinned", out error0, out promoted0);
                    if (back == 0) return Say(log, "couldn't go back to " + version + ": " + error0);
                    RevertedThisSession = true;
                    return Say(log, back == 2 ? "going back to your original version " + version + ". Auto-update is off, type MP3Update when you want a newer version." : "your original version " + version + " is queued. Auto-update is off.");
                }
                return Say(log, lastGood ? "no earlier version that started fine is saved." : "no version was found.");
            }
            foreach (string note in notes) log.LogWarning("[" + UpdaterConfig.TAG + "] " + note);
            SetAuto(false);
            if (skippedVersion != null) skippedVersion.Value = "";
            byte[] data;
            try {
                data = File.ReadAllBytes(path);
            } catch (Exception e) {
                return Say(log, "couldn't read your saved version: " + e.Message);
            }
            string error;
            bool promoted;
            int result = InstallGuarded(log, data, version, "pinned", out error, out promoted);
            if (result == 0) return Say(log, "couldn't go back to " + version + ": " + error);
            RevertedThisSession = true;
            return Say(log, "going back to " + version + (lastGood ? " (the last version that started fine)" : " (your original version)") + (result == 2 ? "." : ", queued.") + " Auto-update is off, type MP3Update when you want a newer version.");
        }
        internal static bool RollBack(ManualLogSource log, string reason) {
            var notes = new List<string>();
            string path, version;
            if (!Files.PickFallback(UpdaterConfig.PLUGIN_VERSION, false, notes, out path, out version)) {
                foreach (string note in notes) log.LogWarning("[" + UpdaterConfig.TAG + "] " + note);
                log.LogError("[" + UpdaterConfig.TAG + "] " + reason + " but there is no earlier version saved to go back to.");
                return false;
            }
            foreach (string note in notes) log.LogWarning("[" + UpdaterConfig.TAG + "] " + note);
            byte[] data;
            try {
                data = File.ReadAllBytes(path);
            } catch (Exception e) {
                log.LogError("[" + UpdaterConfig.TAG + "] " + reason + " and the saved copy could not be read: " + e.Message);
                return false;
            }
            string error;
            bool promoted;
            int result = InstallGuarded(log, data, version, "pinned", out error, out promoted);
            if (result == 0) {
                log.LogError("[" + UpdaterConfig.TAG + "] " + reason + " and going back to " + version + " failed: " + error);
                return false;
            }
            SetAuto(false);
            if (skippedVersion != null) skippedVersion.Value = "";
            RevertedThisSession = true;
            StartupGuard.Notice = "MP3Client " + UpdaterConfig.PLUGIN_VERSION + " " + reason + ", so it is going back to " + version + ". It is " + (result == 2 ? "active" : "queued") + " after the next launch and MP3Client is switched off until then. Auto-update is off, type MP3Update when you want to see the newest version.";
            log.LogError("[" + UpdaterConfig.TAG + "] " + StartupGuard.Notice);
            CrashLog.WriteLocalErrorLog("fallback", StartupGuard.Notice);
            return true;
        }
        internal static void MarkHealthy(ManualLogSource log) {
            healthyThisSession = true;
            string error;
            if (swappedThisSession) {
                if (swapCount == 1 && File.Exists(Files.BackupPath) && Files.RecordGood(UpdaterConfig.PLUGIN_VERSION, Files.BackupPath, out error)) Files.WriteRecoveryNote(UpdaterConfig.PLUGIN_VERSION);
                return;
            }
            Files.WriteState(new UpdateFiles.UpdateState { Version = UpdaterConfig.PLUGIN_VERSION, Status = "healthy", Starts = 0 });
            if (Files.RecordGood(UpdaterConfig.PLUGIN_VERSION, Files.DllPath, out error)) Files.WriteRecoveryNote(UpdaterConfig.PLUGIN_VERSION);
            else log.LogWarning("[" + UpdaterConfig.TAG + "] couldn't save this version as the fallback copy: " + error);
        }
        private static string StateLabel(string status) {
            if (status == "healthy") return "started fine";
            if (status == "trial") return "new, still being checked";
            if (status == "pinned") return "restored";
            if (status == "failed") return "failed to start";
            return "first run";
        }
        private static string FactsText(string newest, string checkError) {
            string path, version;
            string original = Files.FindOriginal(out path, out version) ? version : "none kept";
            var good = Files.ListCopies(Files.GoodPrefix);
            good.Sort(delegate (UpdateFiles.CopyInfo a, UpdateFiles.CopyInfo b) { return b.Version.CompareTo(a.Version); });
            var goodNames = new List<string>();
            foreach (UpdateFiles.CopyInfo copy in good) goodNames.Add(copy.Text);
            UpdateFiles.UpdateState state = Files.ReadState();
            string newestText = checkError != null ? "could not be checked (" + checkError + ")" : (newest != null ? newest : "not checked yet");
            return "You have MP3Client " + UpdaterConfig.PLUGIN_VERSION + " (" + StateLabel(state.Status) + "). Newest: " + newestText + ". Original: " + original + ". Last good: " + (goodNames.Count > 0 ? string.Join(", ", goodNames.ToArray()) : "none") + ". Auto-update: " + (AutoEnabled ? "on" : "off") + ". Change queued: " + (File.Exists(Files.PendingPath) ? "yes" : "no") + ".";
        }
        private static string VerdictText(string newest, string checkError) {
            if (checkError != null) return "The newest version could not be checked, use the links below.";
            if (newest == null) return "Type MP3Update to check for a newer version.";
            if (!IsNewer(newest)) return "You are up to date.";
            if (File.Exists(Files.PendingPath)) return "The new version is already downloaded and active after the next launch.";
            if (AutoEnabled) return "Auto-update installs it by itself, or get it yourself from Github or Nexus.";
            return "Auto-update is off, install it yourself from Github or Nexus.";
        }
        internal static IEnumerator ShowInfo(ManualLogSource log, string newest, string checkError) {
            string facts = FactsText(newest, checkError);
            Say(log, facts);
            string confirm = checkError == null && newest != null && IsNewer(newest) ? newest : null;
            yield return ShowVersionPrompt(log, facts + " " + VerdictText(newest, checkError), confirm, false);
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
            if (File.Exists(Files.PendingPath)) yield break;
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
            MP3TogetherDebug.Instance.StartCoroutine(ShowVersionPrompt(log, "MP3Client " + latest.Version + " is out, you have " + UpdaterConfig.PLUGIN_VERSION + ". Auto-update is off, so install it yourself from Github or Nexus.", latest.Version, true));
        }
        internal static IEnumerator ManualCheck(ManualLogSource log) {
            var latest = new LatestResult();
            yield return FetchLatest(latest);
            if (latest.Ok) availableVersion = latest.Version;
            yield return ShowInfo(log, latest.Ok ? latest.Version : null, latest.Ok ? null : latest.Error);
        }
        internal static IEnumerator VersionInfo(ManualLogSource log) {
            yield return ShowInfo(log, availableVersion, null);
        }
        private static IEnumerator QueueDownload(ManualLogSource log, string remoteVersion, string dllUrl) {
            if (queuing) yield break;
            queuing = true;
            try {
                using (UnityWebRequest dl = UnityWebRequest.Get(dllUrl)) {
                    yield return dl.SendWebRequest();
                    if (dl.result != UnityWebRequest.Result.Success) {
                        Say(log, "update download failed: " + dl.error);
                        yield break;
                    }
                    string error;
                    bool promoted;
                    int result = InstallGuarded(log, dl.downloadHandler.data, remoteVersion, "trial", out error, out promoted);
                    if (result == 0) {
                        Say(log, "failed to save update " + remoteVersion + ": " + error);
                        yield break;
                    }
                    if (promoted) Say(log, "update " + remoteVersion + " installed, active after the next launch. If it fails to start twice, MP3Client goes back to the version you have now by itself.");
                    else Say(log, "update " + remoteVersion + " downloaded, will be applied on the next launch." + (error != null ? " (" + error + ")" : ""));
                }
            } finally {
                queuing = false;
            }
        }
        private static void Confirm(string version) {
            if (!string.IsNullOrEmpty(version) && skippedVersion != null) skippedVersion.Value = version;
        }
        private static void OpenPage(ManualLogSource log, string url) {
            try {
                Application.OpenURL(url);
            } catch (Exception) {
                Say(log, "couldn't open the page, copy this link: " + url);
            }
        }
        internal static IEnumerator ShowVersionPrompt(ManualLogSource log, string text, string confirmVersion, bool onlyInMainMenu) {
            if (onlyInMainMenu) yield return WaitForMainMenu();
            float deadline = Time.unscaledTime + 120f;
            while (UIChoicePrompt.instance == null && Time.unscaledTime < deadline) yield return new WaitForSecondsRealtime(0.5f);
            if (UIChoicePrompt.instance == null) {
                Say(log, text + " Github: " + UpdaterConfig.GITHUB_URL + " Nexus: " + UpdaterConfig.NEXUS_URL);
                yield break;
            }
            try {
                var prompt = new UIChoicePrompt.Prompt();
                prompt.name = "MP3Client update";
                prompt.text = text;
                prompt.timeout = 90f;
                prompt.size = new Vector2(0.5f, 0.22f);
                prompt.defaultcanignore = true;
                var close = new UIChoicePrompt.Prompt.PromptChoice();
                close.text = "Close";
                close.action = delegate () {
                    Confirm(confirmVersion);
                };
                var github = new UIChoicePrompt.Prompt.PromptChoice();
                github.text = "Github";
                github.action = delegate () {
                    Confirm(confirmVersion);
                    OpenPage(log, UpdaterConfig.GITHUB_URL);
                };
                var nexus = new UIChoicePrompt.Prompt.PromptChoice();
                nexus.text = "Nexus";
                nexus.action = delegate () {
                    Confirm(confirmVersion);
                    OpenPage(log, UpdaterConfig.NEXUS_URL);
                };
                prompt.choices = new UIChoicePrompt.Prompt.PromptChoice[] { close, github, nexus };
                UIChoicePrompt.ShowPrompt(prompt);
            } catch (Exception e) {
                CrashLog.Capture("update prompt", e);
                Say(log, text + " Github: " + UpdaterConfig.GITHUB_URL + " Nexus: " + UpdaterConfig.NEXUS_URL);
            }
        }
        internal static IEnumerator ShowNotice(ManualLogSource log, string text) {
            Say(log, text);
            yield return WaitForMainMenu();
            float deadline = Time.unscaledTime + 180f;
            while (UIChoicePrompt.instance == null && Time.unscaledTime < deadline) yield return new WaitForSecondsRealtime(0.5f);
            if (UIChoicePrompt.instance == null) yield break;
            try {
                var prompt = new UIChoicePrompt.Prompt();
                prompt.name = "MP3Client notice";
                prompt.text = text;
                prompt.timeout = 120f;
                prompt.size = new Vector2(0.55f, 0.24f);
                prompt.defaultcanignore = true;
                var ok = new UIChoicePrompt.Prompt.PromptChoice();
                ok.text = "OK";
                ok.action = delegate () { };
                prompt.choices = new UIChoicePrompt.Prompt.PromptChoice[] { ok };
                UIChoicePrompt.ShowPrompt(prompt);
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
    internal static class StartupGuard {
        internal const int MAX_FAILED_STARTS = 2;
        internal const float HEALTHY_AFTER_SECONDS = 15f;
        internal static bool SafeMode;
        internal static string Notice;
        internal static int CriticalFailures;
        internal static void Begin(ManualLogSource log) {
            try {
                if (File.Exists(Updater.Files.PendingPath)) return;
                UpdateFiles.UpdateState state;
                UpdateFiles.LaunchAction action = Updater.Files.BeginLaunch(UpdaterConfig.PLUGIN_VERSION, MAX_FAILED_STARTS, out state);
                if (action == UpdateFiles.LaunchAction.Normal) return;
                string reason = state.Status == "failed" ? "failed during its last start" : "did not start correctly " + state.Starts + " times in a row";
                log.LogError("[" + UpdaterConfig.TAG + "] version " + UpdaterConfig.PLUGIN_VERSION + " " + reason + ", going back to the last version that worked.");
                if (Updater.RollBack(log, reason)) SafeMode = true;
            } catch (Exception e) {
                log.LogWarning("[" + UpdaterConfig.TAG + "] start check failed: " + e.Message);
            }
        }
        internal static void Fail(ManualLogSource log, string reason) {
            CriticalFailures++;
            try {
                UpdateFiles.UpdateState state = Updater.Files.ReadState();
                if (!UpdateFiles.SameVersion(state.Version, UpdaterConfig.PLUGIN_VERSION) || state.Status != "trial") return;
                state.Status = "failed";
                Updater.Files.WriteState(state);
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
    internal static class UpdaterCommands {
        private static void Add(string name, string help, Action<string[]> run) {
            try {
                Con.RegisterCommand(new Command(name, help, delegate (string[] splited) { run(splited); }, null, Array.Empty<ValueTuple<string, string>>()));
            } catch (Exception e) {
                CrashLog.Capture("command " + name, e);
            }
        }
        internal static void RegisterAll() {
            Add("MP3Version", "MP3Client - show your version, the original and last good versions, auto-update and the newest version seen, and open the update window.", delegate (string[] splited) {
                MP3TogetherDebug.Instance.StartCoroutine(Updater.VersionInfo(MP3TogetherDebug.Log));
            });
            Add("MP3Update", "MP3Client - check for the newest version, show the same details as MP3Version and open the update window.", delegate (string[] splited) {
                MP3TogetherDebug.Instance.StartCoroutine(Updater.ManualCheck(MP3TogetherDebug.Log));
            });
        }
    }
#endif
}