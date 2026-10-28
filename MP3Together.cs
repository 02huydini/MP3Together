using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Together;

namespace MP3Together {
    internal class MP3TogetherLog {
        protected const string TAG = "[debug] ";
        public virtual void Info(string message) { MP3TogetherPlugin.Log.LogInfo(TAG + message); }
        public virtual void Warn(string message) { MP3TogetherPlugin.Log.LogWarning(TAG + message); }
        public virtual void Error(string message) { MP3TogetherPlugin.Log.LogError(TAG + message); }
        public virtual void SendStart(ushort syncId, string fileName, int totalBytes) { }
        public virtual void SendChunk(ushort syncId, int chunkIndex, int offset, int length, float elapsedSeconds, float sinceLastSeconds) { }
        public virtual void SendChunkFailed(ushort syncId, int chunkIndex, int offset, Exception e) { Error("send chunk FAILED syncId=" + syncId + " #" + chunkIndex + " offset=" + offset + " : " + e); }
        public virtual void SendProgress(ushort syncId, int sentBytes, int totalBytes, float elapsedSeconds) { }
        public virtual void SendDone(ushort syncId, int totalBytes, float elapsedSeconds) { }
        public virtual void SendAckStalled(ushort syncId) { }
        public virtual void SendCoroutineExit(ushort syncId, string reason) { }
        public virtual void RecvBegin(ushort syncId, int totalBytes, string ext) { }
        public virtual void RecvProgress(ushort syncId, int receivedChunks, int totalChunks, float elapsedSeconds) { }
        public virtual void RecvStalled(ushort syncId, int resendFromOffset) { Warn("recv stalled syncId=" + syncId + " - requesting resend from offset " + resendFromOffset); }
        public virtual void RecvDone(ushort syncId, int totalBytes, float elapsedSeconds) { }
        public virtual void RecvCancelled(ushort syncId) { }
        public virtual void DecodeReplaced(ushort syncId) { }
        public virtual void DecodeDropped(ushort syncId) { }
        public virtual void PreviewStarted(ushort syncId, int prefixBytes, int totalBytes) { }
        public virtual void PreviewPlaying(ushort syncId, float clipSeconds, float decodeSeconds) { }
        public virtual void PreviewDropped(ushort syncId, string reason) { }
        public virtual void SwapDone(ushort syncId, float resumeAt, bool paused) { }
        public virtual void SwapSkipped(ushort syncId, string reason) { }
        public virtual void TransferFrom(ushort sender, ushort syncId, int totalBytes, string ext) { }
        public virtual void TransferRejected(ushort sender, ushort syncId, string reason, int totalBytes) { }
        public virtual void TransferSuperseded(ushort sender, ushort syncId, string reason) { }
        public virtual void ActiveTransfers(int count, long bufferedBytes) { }
        public virtual void FullDecodeFailed(ushort syncId, string error) { }
        public virtual void PreviewFailed(ushort syncId, string error) { }
        public virtual void ClipFreed(ushort syncId, string why) { }
        public virtual void TempSwept(int count, long bytes) { }
        public virtual void CopyHit(ushort syncId, string name, long bytes) { }
        public virtual void CopySaved(string name, long bytes) { }
        public virtual void CopyFailed(string name, string error) { }
        public virtual void OutdatedClient(ushort sender, byte protocol) { }
        public virtual void OpusFailed(string stage, string detail) { }
        public virtual void ErrorCaught(string stage, string detail) { }
        public virtual void SendRejected(ushort syncId, byte reason, int maxBytes) { }
        public virtual void SendBlockedBySize(ushort syncId, long length, long limit) { }
        public virtual void SendAborted(ushort syncId, string reason) { }
        public virtual void SendThrottled(ushort syncId, string reason) { }
        public virtual void SessionStarted() { }
        public virtual void SessionEnded(string reason) { }
        public virtual void TotalsSummary(string reason) { }
        public virtual void RecvDeadlineHit(ushort syncId) { Warn("recv deadline hit syncId=" + syncId + " - finalizing with whatever arrived, likely incomplete/corrupt file"); }
        public virtual void LogItemComponents(object item) { }
        public virtual void Trace(string stage, string detail) { }
        public virtual void UIState(string stage, string detail) { }
        public virtual void NetState(string stage, string detail) { }
        public virtual void PatchedMethodsSummary(List<MethodBase> methods) { }
        public virtual void StateDump(bool networkRunning, bool isServer, bool isClient, float uiVerticalOffset, bool uiOnTop, bool coloredButtons, string hostPluginStatus, string customMusicFolder, int customMusicSongCount, int gatedUIControlsCount, bool libDropdownBuilt, bool wasNetworkRunning) { }
    }
    [BepInPlugin("02dumbass.mp3together", "MP3Together", UpdaterConfig.PLUGIN_VERSION)]
    [BepInDependency("CasualtiesMP", BepInDependency.DependencyFlags.SoftDependency)]
    public class MP3TogetherPlugin : BaseUnityPlugin {
        internal static ManualLogSource Log;
        internal static MP3TogetherPlugin Instance;
        internal static MP3TogetherLog Debug = new MP3TogetherLog();
        private static readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        internal static ConfigEntry<bool> DebugMode;
        internal static ConfigEntry<bool> AutoLoadMusic;
        internal static ConfigEntry<bool> AutoRequestMusic;
        internal static ConfigEntry<bool> CopyLocal;
        internal static ConfigEntry<float> UIVerticalOffset;
        internal static ConfigEntry<bool> UIOnTop;
        internal static ConfigEntry<bool> ColoredButtons;
        private static float nextSweep;
        internal static bool? DebugOverride;
        internal static bool StreamProbeOn;
        internal static bool DebugLogging { get { return DebugOverride.HasValue ? DebugOverride.Value : (DebugMode != null && DebugMode.Value); } }

        private static readonly string[] CriticalPatches = { "Patch_BuildSeparateSongUI" };
        private static bool DetectKrok() {
            try {
                return Chainloader.PluginInfos.ContainsKey(Krok.GUID) && AccessTools.TypeByName("Together.Net") != null;
            } catch (Exception) {
                return false;
            }
        }
        public void Awake() {
            Log = Logger;
            Instance = this;
            Krok.Installed = DetectKrok();
            if (!Krok.Installed) Log.LogInfo("mp3together: CasualtiesMP not found, running without it: the library dropdown and Browse load songs into the game music list and play them locally.");
            try {
                AwakeCore();
            } catch (Exception e) {
                CrashLog.Capture("awake", e);
                StartupGuard.Fail(Log, e.GetType().Name + ": " + e.Message);
            }
        }
        private void AwakeCore() {
            DebugMode = Config.Bind("General", "DebugLogging", false, "Turns on the debug logger, the stream probes and the debug commands. The debug code is always in the dll, this switch decides whether it runs.");
            AutoLoadMusic = Config.Bind("General", "AutoLoadMusic", false, "Bypass pending system: the device music folder loads without the accept or reject prompt.");
            AutoRequestMusic = Config.Bind("General", "AutoRequestMusic", true, "Host or solo: show the accept or reject prompt by itself when a session starts and when the MP3 menu opens. A client always sends its load request when it opens the MP3 menu.");
            CopyLocal = Config.Bind("General", "CopyLocal", false, "Host: On keeps a copy of every song a client sends in plugins/MP3Together/custommusic_Client. Off keeps nothing. Either way, a song that already is in that folder (same file name and size) is played from the file and the client does not send it again.");
            UIOnTop = Config.Bind("UI", "UIOnTop", false, "Stick your UI to the ceiling.");
            ColoredButtons = Config.Bind("UI", "ColoredButtons", false, "Give Stop, Pause buttons red, orange background tints.");
            UIVerticalOffset = Config.Bind("UI", "UIVerticalOffset", 80f, "Shifts the menu downward. Should only be used if your UI is broken.");
            Updater.BindConfig(Config);
            DebugMode.SettingChanged += delegate (object sender, EventArgs args) {
                DebugOverride = null;
                Guard.Run("debug logging", MP3TogetherCommands.Sync);
            };
            if (DebugLogging) Debug = new MP3TogetherLogger();
            Updater.MoveIntoFolder(Log);
            Updater.EnsureOriginalBackup(Log);
            StartupGuard.Begin(Log);
            Updater.ApplyPendingIfAny(Log);
            Updater.CleanupLeftovers(Log);
            Updater.ApplyRevertRequestAtStartup(Log);
            if (StartupGuard.SafeMode) {
                Log.LogError("mp3together: safe mode, the features are switched off for this launch while the earlier version is restored.");
                StartCoroutine(DeferredRegisterCommands());
                StartCoroutine(Updater.ShowNotice(Log, StartupGuard.Notice));
                return;
            }
            DecoderHandler.SweepTempFiles(true);
            if (Krok.Installed) OpusFailure.Preflight();
            var harmony = new Harmony("mp3together");
            foreach (Type patchType in AccessTools.GetTypesFromAssembly(typeof(MP3TogetherPlugin).Assembly)) {
                if (patchType.Name.StartsWith("Patch_Debug")) continue;
                if (!Krok.Installed && Krok.NeedsKrok(patchType)) continue;
                try {
                    harmony.CreateClassProcessor(patchType).Patch();
                } catch (Exception e) {
                    CrashLog.Capture("patch " + patchType.Name, e);
                    if (Array.IndexOf(CriticalPatches, patchType.Name) >= 0) StartupGuard.Fail(Log, "patch " + patchType.Name + " failed");
                }
            }
            LogPatchedMethods(harmony);
            Guard.Run("debug probes", MP3TogetherCommands.Sync);
            MissingFiles.CheckStartup();
            if (StartupGuard.SafeMode) {
                Log.LogError("mp3together: safe mode, a core patch failed and the earlier version is being restored.");
                StartCoroutine(DeferredRegisterCommands());
                StartCoroutine(Updater.ShowNotice(Log, StartupGuard.Notice));
                return;
            }
            StartCoroutine(Updater.CheckForUpdate(Log));
            StartCoroutine(DeferredRegisterCommands());
            StartCoroutine(StartupGuard.ConfirmHealthy(Log));
        }
        private static void LogPatchedMethods(Harmony harmony) {
            MP3TogetherPlugin.Debug.PatchedMethodsSummary(harmony.GetPatchedMethods().ToList());
        }
        private IEnumerator DeferredRegisterCommands() {
            while (!SceneManager.GetActiveScene().isLoaded) yield return null;
            yield return null;
            if (!Krok.Installed) {
                while (!ConsoleCommands.Ready) yield return null;
            }
            Guard.Run("register commands", RegisterAll);
            if (StartupGuard.SafeMode) yield break;
            if (Krok.Installed) Guard.Run("register network handlers", KrokBridge.RegisterHandlers);
            Guard.Run("register debug commands", MP3TogetherCommands.RegisterAll);
        }
        private static void Add(string name, string help, Action<string[]> run, params ValueTuple<string, string>[] arguments) {
            try {
                ConsoleCommands.Add(name, help, run, arguments);
            } catch (Exception e) {
                CrashLog.Capture("command " + name, e);
            }
        }
        internal static void RegisterAll() {
            Add("MP3version", "MP3Together - version and updates. Usage: MP3version [check|status|revert]. check (default) looks for a newer version and shows the update prompt. status prints the version and the updater state. revert goes back to the original installed version at the next launch and turns auto-update off.", delegate (string[] splited) {
                string what = splited != null && splited.Length > 1 ? splited[1].ToLowerInvariant() : "check";
                if (what == "status") Updater.Info(Updater.VersionText());
                else if (what == "check") Instance.StartCoroutine(Updater.ManualCheck(Log));
                else if (what == "revert") Updater.Revert(Log);
                else ConsoleOut.Error("mp3together: usage: MP3version [check|status|revert]");
            }, new ValueTuple<string, string>("action", "optional, check (default), status or revert"));
            Add("MP3autoUpdate", "MP3Together - turn the auto updater on or off. Usage: MP3autoUpdate [true|false]. Without a value it flips the setting.", delegate (string[] splited) {
                bool enable = !Updater.AutoEnabled;
                if (splited != null && splited.Length > 1) {
                    string arg = splited[1].ToLowerInvariant();
                    if (arg == "on" || arg == "true" || arg == "1") enable = true;
                    else if (arg == "off" || arg == "false" || arg == "0") enable = false;
                }
                ConsoleOut.Info("mp3together: " + Updater.SetAuto(enable));
            }, new ValueTuple<string, string>("bool", "optional, true turns the auto updater on, false turns it off, empty flips it"));
            if (StartupGuard.SafeMode) return;
            Add("MP3cleanMemory", "MP3Together - frees memory this plugin holds on YOUR PC only, other players are not affected. It frees: song clips you loaded that nothing plays (the playing song and the last played clip stay), finished host clips, host transfers idle for 15 s, temp files older than 5 min, then unloads unused Unity assets. It keeps the resend copies of songs you sent and the custommusic_Client files.", delegate (string[] splited) {
                MemoryCleaner.Run();
            });
            if (!Krok.Installed) return;
            Add("MP3mute", "MP3Together - mute control. HOST ONLY: MP3mute mute [player|ID] <target> [true|false] mutes or unmutes one player's music (no value flips). MP3mute load [player|ID] <target> [true|false] blocks or allows that player's song load requests for this session. MP3mute request [player|ID] <target> asks the host to mute that player (a host asks itself and gets the prompt). HOST ONLY: MP3mute pending list shows the pending mute requests, MP3mute pending <pending ID> [accept|reject] answers one.", KrokBridge.Mute,
                new ValueTuple<string, string>("action", "mute, load, request or pending"),
                new ValueTuple<string, string>("mode", "player or ID (pending: list or the pending ID)"),
                new ValueTuple<string, string>("player", "name or id of the target player (pending: accept or reject)"),
                new ValueTuple<string, string>("bool", "optional for mute and load, true applies, false lifts, empty flips"));
            Add("MP3load", "MP3Together - MP3load request sends a music load request to the host (a host or solo player gets the accept or reject prompt). MP3load pending [accept/reject] (host or solo) answers the pending request.", KrokBridge.Load,
                new ValueTuple<string, string>("action", "request or pending"),
                new ValueTuple<string, string>("accept/reject", "for pending: accept loads the music, reject denies it"));
            Add("MP3stop", "MP3Together - HOST ONLY - stop what one player started: drops their running transfer and stops the song they started on an mp3 player. It does not mute them. Usage: MP3stop [player|ID] <target player>.", KrokBridge.Stop,
                new ValueTuple<string, string>("mode", "player or ID"),
                new ValueTuple<string, string>("player", "name or id of the player"));
        }
        private static bool wasNetworkRunning;
        public void Update() {
            if (StartupGuard.SafeMode) return;
            Action a;
            while (mainThreadQueue.TryDequeue(out a)) Guard.Run("main thread action", a);
            if (Krok.Installed) Guard.Run("volume bars", KrokBridge.VolumeTick);
            bool nowRunning = Krok.IsRunning;
            if (nowRunning && !wasNetworkRunning) {
                wasNetworkRunning = true;
                Guard.Run("session start", delegate () {
                    HeadlessMusicLoader.ResetMusicStatics();
                    ResetPerSessionState();
                });
            } else if (!nowRunning && wasNetworkRunning) {
                wasNetworkRunning = false;
                Guard.Run("session end", delegate () {
                    HeadlessMusicLoader.ResetMusicStatics();
                    Debug.SessionEnded("network stopped");
                });
            }
            if (Time.unscaledTime >= nextSweep) {
                nextSweep = Time.unscaledTime + Protocol.SWEEP_INTERVAL_SECONDS;
                if (Krok.Installed) Guard.Run("network sweep", KrokBridge.Sweep);
            }
        }
        public void OnDestroy() {
            Debug.SessionEnded("plugin destroyed");
        }
        private static void ResetPerSessionState() {
            Debug.Trace("ResetPerSessionState", "new session detected (network_system_is_running false->true) - resetting per-session state so it doesn't carry over from a previous session in the same game process");
            HostPluginCheck.ResetForNewSession();
            Patch_RegisterServerReceivers.ResetForNewSession();
            Patch_BuildSeparateSongUI.ResetForNewSession();
            Patch_GateNativeMusicListRequest.ResetForNewSession();
            HostPlayback.Reset();
            InstanceVolume.ResetForNewSession();
            LocalLibraryUI.ResetForNewSession();
            Patch_RegisterClientReceivers.ResetForNewSession();
            Debug.SessionStarted();
            OpusFailure.ReportSessionStart();
            Instance.StartCoroutine(HostPluginCheck.PingAfterJoin());
            if (Net.IsServer) {
                HeadlessMusicLoader.EnsureBaseGamePopulated();
                if (AutoLoadMusic.Value) {
                    Log.LogInfo("mp3together: AutoLoadMusic is on - loading device folder music without a prompt");
                    HeadlessMusicLoader.EnsureDeviceFolderPopulated();
                } else {
                    SendPendingRequest(true);
                }
            }
        }
        internal static void PostHudOnlyStatus(string msg) {
            Multiplayer.multiplayerStatusMessage = msg;
            Multiplayer.lastMultiplayerStatusMessageChangeTime = Time.realtimeSinceStartupAsDouble;
        }
        internal static void SendPendingRequest(bool automatic) {
            if (Net.IsServer) {
                if (automatic && !AutoRequestMusic.Value) {
                    Log.LogInfo("mp3together: host self launch-trigger skipped - AutoRequestMusic is off");
                    return;
                }
                Log.LogInfo("mp3together: host self-triggered load, automatic=" + automatic);
                Patch_RegisterServerReceivers.Server_HandleLoadRequest(null);
            } else {
                Log.LogInfo("mp3together: sending pending request to host, automatic=" + automatic);
                var writer = Protocol.Writer(Protocol.MSG_ASK_LOAD);
                writer.Put(automatic);
                var dm = DeliveryMethod.ReliableOrdered;
                Net.Client_Send(in dm, in writer);
            }
        }
        internal static void DumpState() {
            Debug.StateDump(
                Krok.IsRunning,
                Krok.IsServer,
                Krok.IsClient,
                UIVerticalOffset.Value,
                UIOnTop.Value,
                ColoredButtons.Value,
                HostPluginCheck.CurrentStatus.ToString(),
                CustomMusicFolder.GetPath(),
                CustomMusicFolder.Scan().Count,
                LocalLibraryUI.HostGatedControls.Count,
                LocalLibraryUI.CurrentLibDropdown != null,
                wasNetworkRunning
            );
        }
        internal static void RunOnMainThread(Action a) {
            mainThreadQueue.Enqueue(a);
        }
    }
}