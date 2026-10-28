using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using Together;
using LiteNetLib.Utils;

namespace MP3Together {
    internal static class NetHandlers {
        private static bool registered;
        internal static void RegisterOnce() {
            if (registered) return;
            registered = true;
            MP3TogetherPlugin.Debug.NetState("NetHandlers.RegisterOnce", "registering custom message receivers once per game process (the game keeps them across sessions)");
            bool ok = Patch_RegisterClientReceivers.Register();
            ok &= Patch_RegisterServerReceivers.Register();
            if (!ok) StartupGuard.Fail(MP3TogetherPlugin.Log, "network handlers failed");
        }
        private static Action<NetDataReader> ClientHandler(string stage, Action<knetid, NetDataReader> inner) {
            return delegate (NetDataReader reader) {
                try {
                    inner(0, reader);
                } catch (Exception e) {
                    Guard.Report(stage, e);
                }
            };
        }
        private static Action<ScavPlayer, NetDataReader> ServerHandler(string stage, Action<knetid, NetDataReader> inner) {
            return delegate (ScavPlayer player, NetDataReader reader) {
                try {
                    inner(player.playerId, reader);
                } catch (Exception e) {
                    Guard.Report(stage, e);
                }
            };
        }
        internal static bool Client(string name, string stage, Action<knetid, NetDataReader> handler) {
            try {
                Multiplayer.RegisterCustomClientReceiver(name, ClientHandler(stage, handler));
                return true;
            } catch (Exception e) {
                CrashLog.Capture("register client receiver " + name, e);
                return false;
            }
        }
        internal static bool Server(string name, string stage, Action<knetid, NetDataReader> handler) {
            try {
                Multiplayer.RegisterCustomServerReceiver(name, ServerHandler(stage, handler));
                return true;
            } catch (Exception e) {
                CrashLog.Capture("register server receiver " + name, e);
                return false;
            }
        }
    }
    internal static class Patch_RegisterClientReceivers {
        internal static bool Register() {
            bool ok = true;
            ok &= NetHandlers.Client(Protocol.MSG_RESEND_REQUEST, "OnResendRequest", OnResendRequest);
            ok &= NetHandlers.Client(Protocol.MSG_PLUGIN_PONG, "OnPluginPong", OnPluginPong);
            ok &= NetHandlers.Client(Protocol.MSG_ACK, "OnAck", OnAck);
            ok &= NetHandlers.Client(Protocol.MSG_REJECT, "OnReject", OnReject);
            ok &= NetHandlers.Client(Protocol.MSG_LOAD_APPROVED, "OnLoadApproved", OnLoadApproved);
            ok &= NetHandlers.Client(Protocol.MSG_HAVE, "OnHave", OnHave);
            ok &= NetHandlers.Client(Protocol.MSG_VOLUME, "OnVolume", OnVolume);
            MP3TogetherPlugin.Debug.NetState("Patch_RegisterClientReceivers.Register", "client handlers registered (resend request, plugin pong, ack, reject, load approved, have): " + ok);
            return ok;
        }
        internal static float lastRequestAt = -1000f;
        internal static bool requestWaiting;
        static readonly Dictionary<ushort, int> ackCounts = new Dictionary<ushort, int>();
        internal static int AckCount(ushort syncId) {
            int n;
            return ackCounts.TryGetValue(syncId, out n) ? n : 0;
        }
        static readonly Dictionary<ushort, ushort> transferIds = new Dictionary<ushort, ushort>();
        static ushort transferIdCounter;
        internal static ushort BeginTransfer(ushort syncId) {
            transferIdCounter++;
            if (transferIdCounter == 0) transferIdCounter = 1;
            transferIds[syncId] = transferIdCounter;
            if (HostPluginCheck.HostProtocol >= 3) ackCounts[syncId] = 0;
            return transferIdCounter;
        }
        internal static ushort CurrentTransferId(ushort syncId) {
            ushort id;
            return transferIds.TryGetValue(syncId, out id) ? id : (ushort)0;
        }
        internal static void ResetForNewSession() {
            lastRequestAt = -1000f;
            requestWaiting = false;
            ackCounts.Clear();
            transferIds.Clear();
        }
        static void OnAck(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int count = reader.GetInt();
            if (reader.AvailableBytes >= 2) {
                ushort tid = reader.GetUShort();
                if (tid != 0 && tid != CurrentTransferId(syncId)) return;
            }
            if (count > AckCount(syncId)) ackCounts[syncId] = count;
        }
        static void OnReject(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            ushort tid = reader.GetUShort();
            byte reason = reader.GetByte();
            int maxBytes = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            if (tid != CurrentTransferId(syncId)) return;
            MP3TogetherPlugin.Debug.SendRejected(syncId, reason, maxBytes);
            LocalLibraryUI.AbortActive(syncId);
            string text;
            if (reason == Protocol.REJECT_TOO_LARGE) text = maxBytes > 0 ? "host refused the file, too large (limit " + (maxBytes / 1048576f).ToString("F1") + " MB)." : "host ran out of memory for this file.";
            else if (reason == Protocol.REJECT_BUSY) text = "host is busy receiving other songs, try again in a moment.";
            else if (reason == Protocol.REJECT_MUTED) text = "the host muted your music.";
            else if (reason == Protocol.REJECT_STOPPED) text = "the host stopped your song.";
            else if (reason == Protocol.REJECT_REPLACED) text = "another player started a song on this MP3 player, your transfer was replaced.";
            else text = "host refused the file.";
            Msg.Error("mp3together: " + text);
        }
        static void OnVolume(knetid sender, NetDataReader reader) {
            int count = reader.GetUShort();
            for (int i = 0; i < count && reader.AvailableBytes >= 6; i++) {
                ushort syncId = reader.GetUShort();
                float volume = reader.GetFloat();
                InstanceVolume.ClientReceive(syncId, volume);
            }
        }
        static void OnHave(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            ushort tid = reader.GetUShort();
            if (tid != CurrentTransferId(syncId)) return;
            MP3TogetherPlugin.Debug.NetState("OnHave", "host already has this song, syncId=" + syncId + " - stopping the send");
            LocalLibraryUI.AbortActive(syncId);
            Msg.Log("mp3together: the host already has this song, it plays from the host's copy.");
        }
        static void OnPluginPong(knetid sender, NetDataReader reader) {
            byte hostVersion = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)1;
            HostPluginCheck.HostMaxFileBytes = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            MP3TogetherPlugin.Debug.NetState("OnPluginPong", "received from sender=" + sender + " hostProtocol=" + hostVersion + " hostMaxFileBytes=" + HostPluginCheck.HostMaxFileBytes);
            HostPluginCheck.HostProtocol = hostVersion;
            HostPluginCheck.OnPong();
        }
        static void OnLoadApproved(knetid sender, NetDataReader reader) {
            MP3TogetherPlugin.Debug.NetState("OnLoadApproved", "received MSG_LOAD_APPROVED from sender=" + sender);
            Msg.Log("mp3together: host approved - loading device folder music now.");
            Patch_GateNativeMusicListRequest.ClientApproved = true;
            Patch_GateNativeMusicListRequest.TrySendNativeMusicListRequest();
        }
        static void OnResendRequest(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int fromOffset = reader.GetInt();
            MP3TogetherPlugin.Debug.NetState("OnResendRequest", "sender=" + sender + " syncId=" + syncId + " fromOffset=" + fromOffset);
            if (!Patch_InterceptBrowsePlay.pendingFiles.TryGetValue(syncId, out var cachedData)) {
                MP3TogetherPlugin.Debug.NetState("OnResendRequest", "syncId=" + syncId + " not found in pendingFiles, ignoring");
                return;
            }
            if (fromOffset >= cachedData.Bytes.Length) { MP3TogetherPlugin.Debug.NetState("OnResendRequest", "fromOffset=" + fromOffset + " >= length " + cachedData.Bytes.Length + ", ignoring"); return; }
            Patch_InterceptBrowsePlay.StartResend(syncId, cachedData.Ext, cachedData.Bytes, fromOffset);
        }
    }
    internal class Transfer {
        public string Extension;
        public int TotalLength;
        public byte[] Data;
        public bool[] ChunkReceived;
        public int ReceivedChunkCount;
        public bool EndReceived;
        public bool Finalized;
        public bool WaitLoopStarted;
        public bool Cancelled;
        public bool PreviewStarted;
        public int ContiguousChunks;
        public int AudioStart = -1;
        public ushort TransferId;
        public float LastActivity;
        public knetid Sender;
        public int Seq;
        public string Name;
    }
    internal static class Patch_RegisterServerReceivers {
        static readonly Dictionary<ushort, Transfer> transfers = new Dictionary<ushort, Transfer>();
        static readonly Dictionary<ushort, int> ackCounters = new Dictionary<ushort, int>();
        static readonly HashSet<ushort> approvedClients = new HashSet<ushort>();
        static readonly HashSet<ushort> waiters = new HashSet<ushort>();
        internal static bool Register() {
            bool ok = true;
            ok &= NetHandlers.Server(Protocol.MSG_BEGIN, "OnBegin", OnBegin);
            ok &= NetHandlers.Server(Protocol.MSG_CHUNK, "OnChunk", OnChunk);
            ok &= NetHandlers.Server(Protocol.MSG_END, "OnEnd", OnEnd);
            ok &= NetHandlers.Server(Protocol.MSG_CANCEL, "OnCancel", OnCancel);
            ok &= NetHandlers.Server(Protocol.MSG_PAUSE, "OnPause", OnPause);
            ok &= NetHandlers.Server(Protocol.MSG_PLUGIN_PING, "OnPluginPing", OnPluginPing);
            ok &= NetHandlers.Server(Protocol.MSG_ASK_LOAD, "OnPendingRequest", OnPendingRequest);
            ok &= NetHandlers.Server(Protocol.MSG_MUTE_REQUEST, "OnMuteRequest", OnMuteRequest);
            ok &= NetHandlers.Server(Protocol.MSG_VOLUME_SET, "OnVolumeSet", OnVolumeSet);
            MP3TogetherPlugin.Debug.NetState("Patch_RegisterServerReceivers.Register", "server handlers registered (begin, chunk, end, cancel, pause, plugin ping, ask load, mute request): " + ok);
            return ok;
        }
        static readonly HashSet<ushort> ackCapable = new HashSet<ushort>();
        static readonly HashSet<ushort> outdatedAlerted = new HashSet<ushort>();
        static readonly HashSet<ushort> volumeClients = new HashSet<ushort>();
        static readonly Dictionary<ushort, ushort> starters = new Dictionary<ushort, ushort>();
        internal static void NoteStarter(ushort syncId, ushort sender) {
            starters[syncId] = sender;
        }
        internal static void ForgetStarter(ushort syncId) {
            starters.Remove(syncId);
        }
        internal static int StopStartedBy(ushort sender) {
            int stopped = 0;
            foreach (ushort id in new List<ushort>(starters.Keys)) {
                if (starters[id] != sender) continue;
                starters.Remove(id);
                HostPlayback.StopAudio(id);
                stopped++;
            }
            return stopped;
        }
        static void AlertOutdatedClient(knetid sender, byte clientVersion) {
            string who = Notify.DescribePlayer(sender);
            string clientMsg = "[MP3Together] Your MP3Together is outdated (protocol " + clientVersion + ", host needs " + Protocol.OUTDATED_BELOW + " / v" + UpdaterConfig.PLUGIN_VERSION + "+). Update to the latest version: " + Protocol.RELEASES_URL;
            string hostMsg = "[MP3Together] " + who + " runs an outdated MP3Together (protocol " + clientVersion + ", current " + Protocol.VERSION + ") - told them to update.";
            MP3TogetherPlugin.Log.LogWarning("mp3together: " + hostMsg);
            MP3TogetherPlugin.Debug.OutdatedClient((ushort)sender, clientVersion);
            Msg.Log(hostMsg);
            Notify.LogToHostChat(hostMsg);
            Notify.ChatTo(sender, clientMsg);
            Notify.AlertTo(sender, clientMsg);
        }
        static void OnPluginPing(knetid sender, NetDataReader reader) {
            byte clientVersion = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)1;
            if (clientVersion >= 2) ackCapable.Add((ushort)sender); else ackCapable.Remove((ushort)sender);
            if (clientVersion >= 7) volumeClients.Add((ushort)sender); else volumeClients.Remove((ushort)sender);
            if (clientVersion < Protocol.OUTDATED_BELOW && outdatedAlerted.Add((ushort)sender)) AlertOutdatedClient(sender, clientVersion);
            MP3TogetherPlugin.Debug.NetState("OnPluginPing", "received from sender=" + sender + " clientProtocol=" + clientVersion + ", replying with MSG_PLUGIN_PONG");
            var writer = Protocol.Writer(Protocol.MSG_PLUGIN_PONG);
            writer.Put(Protocol.VERSION);
            writer.Put(0);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { sender };
            Net.Server_SendToClients(in dm, in writer, in targets);
            if (clientVersion >= 7) InstanceVolume.SendAllTo(sender);
        }
        internal static knetid? pendingRequester;
        internal static bool pendingActive;
        private const float PROMPT_REFRESH_SECONDS = 31f;
        private static float promptShownAt = -1000f;
        internal static void ResetForNewSession() {
            pendingRequester = null;
            pendingActive = false;
            promptShownAt = -1000f;
            MuteList.ResetForNewSession();
            transfers.Clear();
            DecoderHandler.ResetForNewSession();
            ackCapable.Clear();
            volumeClients.Clear();
            outdatedAlerted.Clear();
            approvedClients.Clear();
            waiters.Clear();
            starters.Clear();
        }
        internal static void DismissPendingPromptIfAny(string reason) {
            if (!pendingActive) return;
            MP3TogetherPlugin.Debug.Trace("DismissPendingPromptIfAny", "clearing pending request (" + reason + ") - music is already loading via a different path");
            pendingActive = false;
            pendingRequester = null;
            waiters.Clear();
            PendingPromptUI.Hide();
        }
        static void OnVolumeSet(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            float volume = reader.GetFloat();
            InstanceVolume.HostReceive(sender, syncId, volume);
        }
        internal static List<knetid> VolumeClientIds() {
            var ids = new List<knetid>();
            foreach (ushort id in volumeClients) {
                ScavPlayer player;
                if (ScavPlayer.TryGetPlayerFromClientId((knetid)id, out player) && player != null) ids.Add((knetid)id);
            }
            return ids;
        }
        static void OnMuteRequest(knetid sender, NetDataReader reader) {
            ushort target = reader.GetUShort();
            MP3TogetherPlugin.Debug.NetState("OnMuteRequest", "received MSG_MUTE_REQUEST from " + sender + " target=" + target);
            MuteList.HandleRequest(sender, target);
        }
        static void OnPendingRequest(knetid sender, NetDataReader reader) {
            bool automatic;
            reader.Get(out automatic);
            MP3TogetherPlugin.Debug.NetState("OnPendingRequest", "received MSG_ASK_LOAD from " + sender + " automatic=" + automatic);
            if (MuteList.IsSongLoadBlocked(sender)) {
                MP3TogetherPlugin.Debug.Trace("OnPendingRequest", "load request from " + sender + " ignored, the host blocked its requests");
                return;
            }
            if (approvedClients.Contains((ushort)sender)) {
                MP3TogetherPlugin.Debug.Trace("OnPendingRequest", "client " + sender + " was approved earlier this session - answering without a prompt");
                HeadlessMusicLoader.EnsureDeviceFolderPopulated();
                SendLoadApproved(sender);
                return;
            }
            Server_HandleLoadRequest(sender);
        }
        internal static void ForgetClient(ushort id) {
            approvedClients.Remove(id);
            waiters.Remove(id);
            if (!pendingActive || !pendingRequester.HasValue || (ushort)pendingRequester.Value != id) return;
            if (waiters.Count == 0) {
                DismissPendingPromptIfAny("the requester was blocked");
                return;
            }
            foreach (ushort other in waiters) {
                pendingRequester = (knetid)other;
                break;
            }
        }
        internal static void Server_HandleLoadRequest(knetid? sender) {
            string who = sender.HasValue ? sender.Value.ToString() : "host (self)";
            if (sender.HasValue) waiters.Add((ushort)sender.Value);
            if (MP3TogetherPlugin.AutoLoadMusic.Value) {
                MP3TogetherPlugin.Debug.Trace("Server_HandleLoadRequest", "AutoLoadMusic is on - approving the load request from " + who + " without a prompt");
                pendingRequester = sender;
                pendingActive = true;
                AcceptPendingRequest();
                return;
            }
            if (pendingActive) {
                if (sender.HasValue) pendingRequester = sender;
                if (Time.unscaledTime - promptShownAt < PROMPT_REFRESH_SECONDS) {
                    MP3TogetherPlugin.Debug.Trace("Server_HandleLoadRequest", "load request from " + who + " while the prompt is still showing - not queuing another one");
                    return;
                }
            } else {
                pendingRequester = sender;
                pendingActive = true;
            }
            MP3TogetherPlugin.Debug.Trace("Server_HandleLoadRequest", "load request from " + who + " is PENDING - showing prompt to host");
            promptShownAt = Time.unscaledTime;
            Msg.Log("mp3together: [ALERT] music load requested - answer the prompt.");
            MP3TogetherPlugin.PostHudOnlyStatus("mp3together: pending music-load request");
            PendingPromptUI.Show(pendingRequester.HasValue ? "A player wants to load custom music. Load it now?" : "Load the music from the custommusic folder now?", AcceptPendingRequest, RejectPendingRequest);
        }
        internal static void AcceptPendingRequest() {
            PendingPromptUI.Hide();
            if (!pendingActive) {
                MP3TogetherPlugin.Debug.Trace("AcceptPendingRequest", "no pending request");
                Msg.Log("mp3together: accept - no pending request.");
                return;
            }
            knetid? requester = pendingRequester;
            string who = requester.HasValue ? requester.Value.ToString() : "host (self)";
            MP3TogetherPlugin.Debug.Trace("AcceptPendingRequest", "approving pending request from " + who);
            var waiting = new List<ushort>(waiters);
            pendingActive = false;
            pendingRequester = null;
            waiters.Clear();
            HeadlessMusicLoader.EnsureDeviceFolderPopulated();
            foreach (ushort id in waiting) {
                approvedClients.Add(id);
                SendLoadApproved((knetid)id);
                SendLoadResult((knetid)id, true);
            }
            Msg.Log("mp3together: music load approved.");
        }
        internal static void RejectPendingRequest() {
            PendingPromptUI.Hide();
            if (!pendingActive) {
                MP3TogetherPlugin.Debug.Trace("RejectPendingRequest", "no pending request");
                Msg.Log("mp3together: reject - no pending request.");
                return;
            }
            knetid? requester = pendingRequester;
            string who = requester.HasValue ? requester.Value.ToString() : "host (self)";
            MP3TogetherPlugin.Debug.Trace("RejectPendingRequest", "denying pending request from " + who);
            var waiting = new List<ushort>(waiters);
            pendingActive = false;
            pendingRequester = null;
            waiters.Clear();
            Msg.Log("mp3together: music load request denied.");
            foreach (ushort id in waiting) SendLoadResult((knetid)id, false);
        }
        static void SendLoadApproved(knetid target) {
            var writer = Protocol.Writer(Protocol.MSG_LOAD_APPROVED);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { target };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        static void SendLoadResult(knetid? requester, bool accepted) {
            if (!requester.HasValue) return;
            string msg = accepted ? "Host accepted your request - loading music now." : "Host rejected your request to load music.";
            Notify.AlertTo(requester.Value, msg);
        }
        static void OnPause(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            MP3TogetherPlugin.Debug.NetState("OnPause", "received MSG_PAUSE from sender=" + sender + " syncId=" + syncId);
            HostPlayback.TogglePause(syncId);
        }
        static string Megabytes(long bytes) {
            return (bytes / 1048576f).ToString("F1");
        }
        static void SendHave(knetid target, ushort syncId, ushort transferId) {
            var writer = Protocol.Writer(Protocol.MSG_HAVE);
            writer.Put(syncId);
            writer.Put(transferId);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { target };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        static void PlayCached(knetid sender, ushort syncId, ushort transferId, string name, string path, int totalLen, string ext) {
            int seq = DecoderHandler.NextSeq(syncId);
            starters[syncId] = (ushort)sender;
            MP3TogetherPlugin.Debug.CopyHit(syncId, name, totalLen);
            MP3TogetherPlugin.Debug.TransferFrom((ushort)sender, syncId, totalLen, ext);
            Msg.Log("mp3together: " + Notify.DescribePlayer(sender) + " plays " + name + " from the host's copy, nothing is sent.");
            DecoderHandler.StartFromFile(syncId, seq, ext, path);
            if (transferId != 0) SendHave(sender, syncId, transferId);
        }
        static void SendReject(knetid target, ushort syncId, ushort transferId, byte reason, int maxBytes) {
            var writer = Protocol.Writer(Protocol.MSG_REJECT);
            writer.Put(syncId);
            writer.Put(transferId);
            writer.Put(reason);
            writer.Put(maxBytes);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { target };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        static void RejectTransfer(knetid sender, ushort syncId, ushort transferId, byte reason, int totalLen) {
            string name = reason == Protocol.REJECT_TOO_LARGE ? "too large" : reason == Protocol.REJECT_BUSY ? "busy" : reason == Protocol.REJECT_MUTED ? "muted" : "invalid";
            string text;
            if (reason == Protocol.REJECT_TOO_LARGE) text = "The host ran out of memory for this file (" + Megabytes(Math.Max(totalLen, 0)) + " MB).";
            else if (reason == Protocol.REJECT_BUSY) text = "The host is busy receiving other songs, try again in a moment.";
            else if (reason == Protocol.REJECT_MUTED) text = "The host muted your music.";
            else text = "The host can't accept this file (empty, or not mp3, wav or ogg).";
            string who = Notify.DescribePlayer(sender);
            MP3TogetherPlugin.Log.LogWarning("mp3together: rejected transfer from " + who + " syncId=" + syncId + " (" + name + "): " + text);
            MP3TogetherPlugin.Debug.TransferRejected((ushort)sender, syncId, name, totalLen);
            Msg.Log("mp3together: rejected a song from " + who + " - " + text);
            Notify.AlertTo(sender, "[MP3Together] " + text);
            if (transferId != 0) SendReject(sender, syncId, transferId, reason, 0);
        }
        static void DropTransfer(ushort syncId, Transfer tr, string reason) {
            tr.Cancelled = true;
            transfers.Remove(syncId);
            DecoderHandler.Invalidate(syncId);
            DecoderHandler.Cancel(syncId);
            HostPlayback.StopIfPreviewOf(syncId, tr.Seq);
            tr.Data = null;
            MP3TogetherPlugin.Debug.TransferSuperseded((ushort)tr.Sender, syncId, reason);
        }
        static void OnBegin(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int totalLen = reader.GetInt();
            string ext = reader.GetString();
            ushort transferId = reader.AvailableBytes >= 2 ? reader.GetUShort() : (ushort)0;
            string rawName = reader.AvailableBytes >= 2 ? reader.GetString() : null;
            ushort senderId = (ushort)sender;
            ext = ext != null ? ext.ToLowerInvariant() : "";
            if (!PreviewData.IsAllowedExtension(ext) || totalLen <= 0) {
                RejectTransfer(sender, syncId, transferId, Protocol.REJECT_INVALID, totalLen);
                return;
            }
            if (MuteList.IsMuted(sender)) {
                RejectTransfer(sender, syncId, transferId, Protocol.REJECT_MUTED, totalLen);
                return;
            }
            string name = DecoderHandler.SafeName(rawName, ext);
            string cached = name != null ? DecoderHandler.FindCopy(name, totalLen) : null;
            Transfer old;
            bool replacing = transfers.TryGetValue(syncId, out old);
            if (!replacing && cached == null && transfers.Count >= Protocol.MAX_CONCURRENT_TRANSFERS) {
                RejectTransfer(sender, syncId, transferId, Protocol.REJECT_BUSY, totalLen);
                return;
            }
            foreach (ushort other in new List<ushort>(transfers.Keys)) {
                Transfer ot = transfers[other];
                if (other != syncId && (ushort)ot.Sender == senderId) DropTransfer(other, ot, "the same player started a new transfer");
            }
            if (replacing) {
                bool otherPlayer = (ushort)old.Sender != senderId;
                DropTransfer(syncId, old, otherPlayer ? "another player took over this mp3 player" : "the same player restarted this transfer");
                if (otherPlayer) {
                    Notify.AlertTo(old.Sender, "[MP3Together] Another player started a song on this MP3 player, your transfer was replaced.");
                    if (old.TransferId != 0) SendReject(old.Sender, syncId, old.TransferId, Protocol.REJECT_REPLACED, 0);
                }
            }
            if (DecoderHandler.Cancel(syncId)) MP3TogetherPlugin.Debug.DecodeReplaced(syncId);
            if (cached != null) {
                PlayCached(sender, syncId, transferId, name, cached, totalLen, ext);
                return;
            }
            byte[] buffer;
            try {
                buffer = new byte[totalLen];
            } catch (OutOfMemoryException) {
                RejectTransfer(sender, syncId, transferId, Protocol.REJECT_TOO_LARGE, totalLen);
                return;
            }
            int chunkCount = (totalLen + Protocol.CHUNK_SIZE - 1) / Protocol.CHUNK_SIZE;
            int seq = DecoderHandler.NextSeq(syncId);
            if (transferId != 0) ackCounters[syncId] = 0;
            transfers[syncId] = new Transfer {
                Seq = seq,
                Extension = ext,
                TotalLength = totalLen,
                Sender = sender,
                Name = name,
                TransferId = transferId,
                LastActivity = Time.unscaledTime,
                Data = buffer,
                ChunkReceived = new bool[Math.Max(chunkCount, 1)]
            };
            starters[syncId] = senderId;
            MP3TogetherPlugin.Debug.RecvBegin(syncId, totalLen, ext);
            MP3TogetherPlugin.Debug.TransferFrom(senderId, syncId, totalLen, ext);
            MP3TogetherPlugin.Debug.ActiveTransfers(transfers.Count, BufferedBytes());
        }
        static long BufferedBytes() {
            long total = 0;
            foreach (var t in transfers.Values) {
                if (t.Data != null) total += t.Data.Length;
            }
            return total;
        }
        internal static int DropTransfersFrom(ushort sender, byte reason, string why) {
            int dropped = 0;
            foreach (ushort id in new List<ushort>(transfers.Keys)) {
                Transfer t = transfers[id];
                if ((ushort)t.Sender != sender) continue;
                DropTransfer(id, t, why);
                if (t.TransferId != 0) SendReject(t.Sender, id, t.TransferId, reason, 0);
                dropped++;
            }
            return dropped;
        }
        internal static int SweepIdle(float idleSeconds) {
            int dropped = 0;
            float now = Time.unscaledTime;
            foreach (ushort id in new List<ushort>(transfers.Keys)) {
                Transfer t = transfers[id];
                if (t.EndReceived || now - t.LastActivity < idleSeconds) continue;
                DropTransfer(id, t, "no data for " + idleSeconds.ToString("F0") + "s (sender gone or stalled)");
                dropped++;
            }
            return dropped;
        }
        static void OnChunk(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int offset = reader.GetInt();
            byte[] chunk = reader.GetBytesWithLength();
            Transfer tr;
            if (!transfers.TryGetValue(syncId, out tr) || tr.Data == null || (ushort)tr.Sender != (ushort)sender) return;
            tr.LastActivity = Time.unscaledTime;
            SendAckIfDue(sender, syncId, tr.TransferId);
            if (offset < 0 || (long)offset + chunk.Length > tr.Data.Length) return;
            Array.Copy(chunk, 0, tr.Data, offset, chunk.Length);
            int chunkIndex = offset / Protocol.CHUNK_SIZE;
            if (chunkIndex >= 0 && chunkIndex < tr.ChunkReceived.Length && !tr.ChunkReceived[chunkIndex]) {
                tr.ChunkReceived[chunkIndex] = true;
                tr.ReceivedChunkCount++;
            }
            if (TryFinalize(syncId, tr)) return;
            TryStartPreview(syncId, tr);
        }
        static void TryStartPreview(ushort syncId, Transfer tr) {
            if (tr.PreviewStarted || tr.Cancelled || tr.Finalized) return;
            if (tr.TotalLength < Protocol.PREVIEW_MIN_TOTAL_BYTES) return;
            while (tr.ContiguousChunks < tr.ChunkReceived.Length && tr.ChunkReceived[tr.ContiguousChunks]) tr.ContiguousChunks++;
            if (tr.ContiguousChunks < 1 || tr.ContiguousChunks >= tr.ChunkReceived.Length) return;
            int prefixBytes = tr.ContiguousChunks * Protocol.CHUNK_SIZE;
            if (tr.AudioStart < 0) tr.AudioStart = Math.Min(PreviewData.AudioStart(tr.Extension, tr.Data, prefixBytes), tr.TotalLength);
            long audioBytes = (long)tr.TotalLength - tr.AudioStart;
            if ((long)(prefixBytes - tr.AudioStart) * 100L < audioBytes * Protocol.PREVIEW_PERCENT) return;
            tr.PreviewStarted = true;
            var prefix = new byte[prefixBytes];
            Array.Copy(tr.Data, prefix, prefixBytes);
            MP3TogetherPlugin.Debug.PreviewStarted(syncId, prefixBytes, tr.TotalLength);
            DecoderHandler.Start(syncId, tr.Seq, tr.Extension, PreviewData.Prepare(tr.Extension, prefix), true, IsLiveTransfer);
        }
        static void SendAckIfDue(knetid sender, ushort syncId, ushort transferId) {
            int n;
            ackCounters.TryGetValue(syncId, out n);
            n++;
            ackCounters[syncId] = n;
            if (n % 2 != 0) return;
            if (!ackCapable.Contains((ushort)sender)) return;
            var writer = Protocol.Writer(Protocol.MSG_ACK);
            writer.Put(syncId);
            writer.Put(n);
            writer.Put(transferId);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { sender };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        static void OnEnd(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            Transfer tr;
            if (!transfers.TryGetValue(syncId, out tr) || (ushort)tr.Sender != (ushort)sender) return;
            tr.LastActivity = Time.unscaledTime;
            tr.EndReceived = true;
            if (!TryFinalize(syncId, tr) && !tr.WaitLoopStarted) {
                tr.WaitLoopStarted = true;
                MP3TogetherPlugin.Instance.StartCoroutine(WaitThenFinalize(syncId, tr));
            }
        }
        static bool TryFinalize(ushort syncId, Transfer tr) {
            if (tr.Cancelled) return true;
            if (tr.Finalized) return true;
            if (!tr.EndReceived) return false;
            if (tr.ReceivedChunkCount < tr.ChunkReceived.Length) return false;
            tr.Finalized = true;
            transfers.Remove(syncId);
            FinalizeTransfer(syncId, tr);
            return true;
        }
        static int FirstMissingChunkOffset(Transfer tr) {
            for (int i = 0; i < tr.ChunkReceived.Length; i++) {
                if (!tr.ChunkReceived[i]) return i * Protocol.CHUNK_SIZE;
            }
            return tr.TotalLength;
        }
        static IEnumerator WaitThenFinalize(ushort syncId, Transfer tr) {
            int lastCount = tr.ReceivedChunkCount;
            float lastProgressTime = Time.unscaledTime;
            float overallDeadline = Time.unscaledTime + Protocol.OVERALL_DEADLINE_SECONDS;
            while (Time.unscaledTime < overallDeadline) {
                if (TryFinalize(syncId, tr)) yield break;
                if (tr.ReceivedChunkCount > lastCount) {
                    lastCount = tr.ReceivedChunkCount;
                    lastProgressTime = Time.unscaledTime;
                } else if (Time.unscaledTime - lastProgressTime > Protocol.STALL_TIMEOUT_SECONDS) {
                    int resendFrom = FirstMissingChunkOffset(tr);
                    MP3TogetherPlugin.Debug.RecvStalled(syncId, resendFrom);
                    RequestResend(tr.Sender, syncId, resendFrom);
                    lastProgressTime = Time.unscaledTime;
                }
                yield return null;
            }
            if (!tr.Finalized) {
                MP3TogetherPlugin.Debug.RecvDeadlineHit(syncId);
                tr.Finalized = true;
                transfers.Remove(syncId);
                FinalizeTransfer(syncId, tr);
            }
        }
        static void RequestResend(knetid target, ushort syncId, int fromOffset) {
            var writer = Protocol.Writer(Protocol.MSG_RESEND_REQUEST);
            writer.Put(syncId);
            writer.Put(fromOffset);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { target };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        static void FinalizeTransfer(ushort syncId, Transfer tr) {
            if (DecoderHandler.Cancel(syncId)) MP3TogetherPlugin.Debug.DecodeReplaced(syncId);
            string copy = null;
            if (MP3TogetherPlugin.CopyLocal.Value && tr.Name != null && tr.ReceivedChunkCount >= tr.ChunkReceived.Length) copy = DecoderHandler.CopyLocal(tr.Name, tr.Data);
            if (copy != null) DecoderHandler.StartFromFile(syncId, tr.Seq, tr.Extension, copy);
            else DecoderHandler.Start(syncId, tr.Seq, tr.Extension, tr.Data, false, IsLiveTransfer);
            tr.Data = null;
        }
        static void OnCancel(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            Transfer tr;
            bool anyTransfer = transfers.TryGetValue(syncId, out tr);
            bool hadTransfer = anyTransfer && (ushort)tr.Sender == (ushort)sender;
            if (anyTransfer && !hadTransfer) {
                MP3TogetherPlugin.Debug.Trace("OnCancel", "ignored cancel from " + sender + " for syncId=" + syncId + ", another player's transfer is in progress");
                return;
            }
            if (hadTransfer) {
                tr.Cancelled = true;
                transfers.Remove(syncId);
                tr.Data = null;
            }
            DecoderHandler.Invalidate(syncId);
            DecoderHandler.Cancel(syncId);
            if (hadTransfer) HostPlayback.StopIfPreviewOf(syncId, tr.Seq);
            else HostPlayback.StopAudio(syncId);
            MP3TogetherPlugin.Debug.RecvCancelled(syncId);
        }
        static bool IsLiveTransfer(ushort syncId, int seq) {
            Transfer live;
            return transfers.TryGetValue(syncId, out live) && live.Seq == seq && !live.Finalized && !live.Cancelled;
        }
    }
    [HarmonyPatch(typeof(MP3PlayerServerAudioStreamer), "Server_ForceLoadMusic")]
    internal static class Patch_BypassForceLoadMusic {
        static void Prefix() {
            Guard.Run("Patch_BypassForceLoadMusic.Prefix", PrefixCore);
        }
        static void PrefixCore() {
            if (!Krok.IsRunning) return;
            MP3TogetherPlugin.Debug.Trace("Patch_BypassForceLoadMusic", "Server_ForceLoadMusic invoked (loadcustommusic command or native list-request auto-recovery) - host-only/authorized upstream, dismissing any pending prompt");
            Patch_RegisterServerReceivers.DismissPendingPromptIfAny("loadcustommusic/native auto-recovery bypass fired");
        }
    }
    internal static class Patch_GateNativeMusicListRequest {
        private const byte NATIVE_MSG_REQUEST_MUSIC_LIST = (byte)NetmsgId.SERVER_MP3RequestMusicList;
        internal static bool ClientApproved;
        internal static void ResetForNewSession() {
            ClientApproved = false;
        }
        internal static void TrySendNativeMusicListRequest() {
            try {
                var writer = Net.CreateWriter(NATIVE_MSG_REQUEST_MUSIC_LIST);
                writer.Put(1);
                var dm = DeliveryMethod.ReliableUnordered;
                Net.Client_Send(in dm, in writer);
                MP3TogetherPlugin.Debug.NetState("TrySendNativeMusicListRequest", "sent native 'request server music list' (msg " + NATIVE_MSG_REQUEST_MUSIC_LIST + ") now that approval was granted");
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: TrySendNativeMusicListRequest failed: " + e.Message);
            }
        }
    }
    [HarmonyPatch(typeof(MP3PlayerServerAudioStreamer), "Server_MP3RequestMusicList")]
    internal static class Patch_EnsureBaseBeforeListRequest {
        static void Prefix() {
            Guard.Run("Patch_EnsureBaseBeforeListRequest.Prefix", delegate () {
                HeadlessMusicLoader.EnsureBaseGamePopulated();
            });
        }
    }
    internal static class MuteList {
        private const float NOTICE_SECONDS = 5f;
        private const float REQUEST_COOLDOWN_SECONDS = 10f;
        private const float PROMPT_OPEN_SECONDS = 31f;
        private const int MAX_PENDING_MUTES = 8;
        private const int HOST_KEY = 65536;
        private class PendingMute {
            public long Key;
            public int Requester;
            public knetid? RequesterId;
            public ScavPlayer Target;
            public string Name;
            public float At;
            public UIChoicePrompt.Prompt Prompt;
            public int Id;
        }
        private static int nextPendingId = 1;
        private static readonly HashSet<ushort> muted = new HashSet<ushort>();
        private static readonly HashSet<ushort> songLoadBlocked = new HashSet<ushort>();
        private static readonly Dictionary<ushort, float> noticeAt = new Dictionary<ushort, float>();
        private static readonly Dictionary<int, float> requestAt = new Dictionary<int, float>();
        private static readonly List<PendingMute> pendingMutes = new List<PendingMute>();
        private static float lastSentAt = -1000f;
        internal static void ResetForNewSession() {
            muted.Clear();
            songLoadBlocked.Clear();
            noticeAt.Clear();
            requestAt.Clear();
            pendingMutes.Clear();
            nextPendingId = 1;
            lastSentAt = -1000f;
        }
        internal static bool IsMuted(knetid id) {
            return muted.Contains((ushort)id);
        }
        internal static bool IsSongLoadBlocked(knetid id) {
            return songLoadBlocked.Contains((ushort)id);
        }
        internal static void NoteBlocked(knetid id) {
            float now = Time.unscaledTime;
            float last;
            if (noticeAt.TryGetValue((ushort)id, out last) && now - last < NOTICE_SECONDS) return;
            noticeAt[(ushort)id] = now;
            MP3TogetherPlugin.Debug.Trace("MuteList", "play request from muted client " + (ushort)id + " ignored");
            Notify.AlertTo(id, "[MP3Together] The host muted your music.");
        }
        private static bool TryBool(string text, out bool value) {
            string t = text.ToLowerInvariant();
            value = t == "true" || t == "on" || t == "1" || t == "yes";
            return value || t == "false" || t == "off" || t == "0" || t == "no";
        }
        private static ScavPlayer FindById(string query, out string problem) {
            problem = null;
            ushort id;
            ScavPlayer found;
            if (!ushort.TryParse(query.Trim(), out id)) {
                problem = "the ID must be a number.";
                return null;
            }
            if (ScavPlayer.TryGetPlayerFromClientId((knetid)id, out found) && found != null) return found;
            problem = "no player has the ID " + id + ".";
            return null;
        }
        private static ScavPlayer Find(string query, out string problem) {
            problem = null;
            var dict = AccessTools.Field(typeof(ScavPlayer), "ClientIdToPlayerDict").GetValue(null) as System.Collections.IDictionary;
            if (dict == null) {
                problem = "the player list is not available.";
                return null;
            }
            ushort id;
            ScavPlayer found;
            if (ushort.TryParse(query, out id) && ScavPlayer.TryGetPlayerFromClientId((knetid)id, out found) && found != null) return found;
            var matches = new List<ScavPlayer>();
            foreach (object value in dict.Values) {
                ScavPlayer p = value as ScavPlayer;
                if (p == null || string.IsNullOrEmpty(p.playerName)) continue;
                if (string.Equals(p.playerName, query, StringComparison.OrdinalIgnoreCase)) return p;
                if (p.playerName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(p);
            }
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) {
                var names = new List<string>();
                foreach (ScavPlayer p in matches) names.Add(p.playerName + " (id " + p.playerId + ")");
                problem = "more than one player matches: " + string.Join(", ", names.ToArray()) + ".";
                return null;
            }
            problem = "no player matches \"" + query + "\".";
            return null;
        }
        private static ScavPlayer Target(string[] args, string usage, bool allowBool, bool hostOnly, bool byId, out bool? value) {
            value = null;
            if (hostOnly ? !Krok.IsServer : !Krok.IsRunning) {
                Msg.Error(hostOnly ? "mp3together: this command works only for the host of a running session." : "mp3together: this command needs a running session.");
                return null;
            }
            if (args == null || args.Length < 2) {
                Msg.Log("mp3together: usage: " + usage);
                return null;
            }
            int end = args.Length;
            bool parsed;
            if (allowBool && args.Length > 2 && TryBool(args[args.Length - 1], out parsed)) {
                value = parsed;
                end--;
            }
            string query = string.Join(" ", args, 1, end - 1).Trim();
            string problem;
            ScavPlayer target = byId ? FindById(query, out problem) : Find(query, out problem);
            if (target == null) {
                Msg.Error("mp3together: " + problem);
                return null;
            }
            if (target.IsLocal) {
                Msg.Error("mp3together: you can't use this on yourself.");
                return null;
            }
            return target;
        }
        private static void ApplyMute(ScavPlayer target, bool mute) {
            ushort id = (ushort)target.playerId;
            if (mute) {
                muted.Add(id);
                Patch_RegisterServerReceivers.DropTransfersFrom(id, Protocol.REJECT_MUTED, "the player was muted by the host");
            } else {
                muted.Remove(id);
            }
            Msg.Log("mp3together: " + Notify.DescribePlayer(target.playerId) + (mute ? " is muted and can't play music." : " is unmuted."));
            Notify.AlertTo(target.playerId, mute ? "[MP3Together] The host muted your music." : "[MP3Together] The host unmuted your music.");
        }
        internal static void RunMute(string[] args, bool byId) {
            bool? value;
            ScavPlayer target = Target(args, byId ? "MP3mute mute ID <ID> [true|false]" : "MP3mute mute player <name or id> [true|false]", true, true, byId, out value);
            if (target == null) return;
            ushort id = (ushort)target.playerId;
            ApplyMute(target, value.HasValue ? value.Value : !muted.Contains(id));
        }
        internal static void RunMuteSongLoad(string[] args, bool byId) {
            bool? value;
            ScavPlayer target = Target(args, byId ? "MP3mute load ID <ID> [true|false]" : "MP3mute load player <name or id> [true|false]", true, true, byId, out value);
            if (target == null) return;
            ushort id = (ushort)target.playerId;
            bool block = value.HasValue ? value.Value : !songLoadBlocked.Contains(id);
            if (block) {
                songLoadBlocked.Add(id);
                Patch_RegisterServerReceivers.ForgetClient(id);
            } else {
                songLoadBlocked.Remove(id);
            }
            Msg.Log("mp3together: " + Notify.DescribePlayer(target.playerId) + (block ? " can't send song load requests any more this session." : " can send song load requests again."));
        }
        private static void Reply(knetid? requester, string text) {
            if (requester.HasValue) Notify.AlertTo(requester.Value, "[MP3Together] " + text);
            else Msg.Log("mp3together: " + text);
        }
        internal static void RunMuteRequest(string[] args, bool byId) {
            bool? value;
            ScavPlayer target = Target(args, byId ? "MP3mute request ID <ID>" : "MP3mute request player <name or id>", false, false, byId, out value);
            if (target == null) return;
            ushort id = (ushort)target.playerId;
            if (Krok.IsServer) {
                HandleRequest(null, id);
                return;
            }
            if (HostPluginCheck.CurrentStatus != HostPluginCheck.Status.Confirmed) {
                Msg.Error("mp3together: the host has not answered as an MP3Together host yet, open the MP3 menu and try again.");
                return;
            }
            if (HostPluginCheck.HostProtocol < 6) {
                Msg.Error("mp3together: the host's MP3Together is too old for mute requests.");
                return;
            }
            float now = Time.unscaledTime;
            if (now - lastSentAt < REQUEST_COOLDOWN_SECONDS) {
                Msg.Error("mp3together: wait a few seconds before the next mute request.");
                return;
            }
            lastSentAt = now;
            var writer = Protocol.Writer(Protocol.MSG_MUTE_REQUEST);
            writer.Put(id);
            var dm = DeliveryMethod.ReliableOrdered;
            Net.Client_Send(in dm, in writer);
            Msg.Log("mp3together: asked the host to mute " + Notify.DescribePlayer(target.playerId) + ".");
        }
        private static void Resolve(PendingMute p, bool accept) {
            if (!pendingMutes.Remove(p)) return;
            PendingPromptUI.Close(p.Prompt);
            if (accept) {
                ApplyMute(p.Target, true);
                Reply(p.RequesterId, p.Name + " is muted.");
            } else {
                Reply(p.RequesterId, "the host rejected the request to mute " + p.Name + ".");
            }
        }
        private static void RunPending(string[] args) {
            if (!Krok.IsServer) {
                Msg.Error("mp3together: this command works only for the host of a running session.");
                return;
            }
            string sub = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            if (sub == "list") {
                if (pendingMutes.Count == 0) {
                    Msg.Log("mp3together: no pending mute request.");
                    return;
                }
                float now = Time.unscaledTime;
                foreach (PendingMute p in pendingMutes) Msg.Log("mp3together: pending " + p.Id + ": " + (p.RequesterId.HasValue ? Notify.DescribePlayer(p.RequesterId.Value) : "the host") + " asks to mute " + p.Name + " (" + (now - p.At).ToString("F0") + "s ago)");
                return;
            }
            string choice = args.Length > 3 ? args[3].ToLowerInvariant() : "";
            int id;
            if (!int.TryParse(sub, out id) || (choice != "accept" && choice != "reject")) {
                Msg.Log("mp3together: usage: MP3mute pending list, MP3mute pending <pending ID> [accept|reject]");
                return;
            }
            PendingMute found = null;
            foreach (PendingMute p in pendingMutes) {
                if (p.Id == id) found = p;
            }
            if (found == null) {
                Msg.Error("mp3together: no pending mute request with the ID " + id + ".");
                return;
            }
            Resolve(found, choice == "accept");
        }
        private static bool Split(string[] args, int modeIndex, string usage, out string[] rest, out bool byId) {
            rest = null;
            byId = false;
            string mode = args != null && args.Length > modeIndex ? args[modeIndex].ToLowerInvariant() : "";
            if ((mode != "player" && mode != "id") || args.Length <= modeIndex + 1) {
                Msg.Log("mp3together: usage: " + usage);
                return false;
            }
            byId = mode == "id";
            var list = new List<string> { args[0] };
            for (int i = modeIndex + 1; i < args.Length; i++) list.Add(args[i]);
            rest = list.ToArray();
            return true;
        }
        internal static void RunCommand(string[] args) {
            string action = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
            string[] rest;
            bool byId;
            if (action == "mute") {
                if (Split(args, 2, "MP3mute mute [player|ID] <target> [true|false]", out rest, out byId)) RunMute(rest, byId);
            } else if (action == "load") {
                if (Split(args, 2, "MP3mute load [player|ID] <target> [true|false]", out rest, out byId)) RunMuteSongLoad(rest, byId);
            } else if (action == "request") {
                if (Split(args, 2, "MP3mute request [player|ID] <target>", out rest, out byId)) RunMuteRequest(rest, byId);
            } else if (action == "pending") {
                RunPending(args);
            } else {
                Msg.Log("mp3together: usage: MP3mute mute|load|request [player|ID] <target> [true|false], MP3mute pending list, MP3mute pending <pending ID> [accept|reject]");
            }
        }
        internal static void RunStop(string[] args) {
            string[] rest;
            bool byId;
            if (!Split(args, 1, "MP3stop [player|ID] <target player>", out rest, out byId)) return;
            bool? value;
            ScavPlayer target = Target(rest, "MP3stop [player|ID] <target player>", false, true, byId, out value);
            if (target == null) return;
            ushort id = (ushort)target.playerId;
            int dropped = Patch_RegisterServerReceivers.DropTransfersFrom(id, Protocol.REJECT_STOPPED, "the host stopped the player's song");
            int stopped = Patch_RegisterServerReceivers.StopStartedBy(id);
            Msg.Log("mp3together: " + Notify.DescribePlayer(target.playerId) + ": stopped " + stopped + " song(s), dropped " + dropped + " transfer(s).");
            if (dropped + stopped > 0) Notify.AlertTo(target.playerId, "[MP3Together] The host stopped your song.");
        }
        internal static void HandleRequest(knetid? requester, ushort targetId) {
            int key = requester.HasValue ? (int)(ushort)requester.Value : HOST_KEY;
            float now = Time.unscaledTime;
            float last;
            if (requestAt.TryGetValue(key, out last) && now - last < REQUEST_COOLDOWN_SECONDS) {
                Reply(requester, "wait a few seconds before the next mute request.");
                return;
            }
            ScavPlayer target;
            if (!ScavPlayer.TryGetPlayerFromClientId((knetid)targetId, out target) || target == null) {
                Reply(requester, "that player is not in the session.");
                return;
            }
            if (target.IsLocal) {
                Reply(requester, "the host can't be muted.");
                return;
            }
            if (requester.HasValue && (ushort)requester.Value == targetId) {
                Reply(requester, "you can't ask to mute yourself.");
                return;
            }
            string name = Notify.DescribePlayer(target.playerId);
            if (muted.Contains(targetId)) {
                Reply(requester, name + " is already muted.");
                return;
            }
            long promptKey = ((long)key << 16) | targetId;
            foreach (PendingMute old in new List<PendingMute>(pendingMutes)) {
                if (old.Key != promptKey) continue;
                if (now - old.At < PROMPT_OPEN_SECONDS) return;
                pendingMutes.Remove(old);
                PendingPromptUI.Close(old.Prompt);
            }
            while (pendingMutes.Count >= MAX_PENDING_MUTES) {
                PendingPromptUI.Close(pendingMutes[0].Prompt);
                pendingMutes.RemoveAt(0);
            }
            requestAt[key] = now;
            var pending = new PendingMute { Id = nextPendingId++, Key = promptKey, Requester = key, RequesterId = requester, Target = target, Name = name, At = now };
            pendingMutes.Add(pending);
            string who = requester.HasValue ? Notify.DescribePlayer(requester.Value) : "The host";
            Msg.Log("mp3together: " + who + " asks to mute " + name + " (pending " + pending.Id + ") - answer the prompt or type MP3mute pending " + pending.Id + " [accept|reject].");
            pending.Prompt = PendingPromptUI.ShowChoice(who + " asks to mute " + name + ". Mute them?", delegate () {
                Resolve(pending, true);
            }, delegate () {
                Resolve(pending, false);
            });
        }
    }
    [HarmonyPatch]
    internal static class Patch_MutePlayRequest {
        static MethodBase TargetMethod() {
            return AccessTools.Method(typeof(MP3PlayerServerAudioStreamer), "Server_MP3PlayCustomMusic");
        }
        static bool Prefix(knetid cid, NetDataReader reader) {
            if (MuteList.IsMuted(cid)) {
                MuteList.NoteBlocked(cid);
                return false;
            }
            try {
                if (reader != null && reader.AvailableBytes >= 6) Patch_RegisterServerReceivers.NoteStarter(BitConverter.ToUInt16(reader.RawData, reader.Position + 4), (ushort)cid);
            } catch (Exception) { }
            return true;
        }
    }
    internal static class KrokBridge {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RegisterHandlers() {
            NetHandlers.RegisterOnce();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Sweep() {
            if (Net.IsRunning && Net.IsServer) Guard.Run("idle transfer sweep", delegate () { Patch_RegisterServerReceivers.SweepIdle(Protocol.TRANSFER_IDLE_SECONDS); });
            Guard.Run("clip sweep", HostPlayback.Sweep);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void NoteUsedPlayer() {
            VolumeBars.NoteUsedPlayer();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void VolumeTick() {
            VolumeBars.Tick();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void OnHostConfirmed() {
            if (Patch_RegisterClientReceivers.requestWaiting && Net.IsRunning && Net.IsClient) SendClientRequest();
        }
        private static void SendClientRequest() {
            Patch_RegisterClientReceivers.requestWaiting = false;
            float now = Time.unscaledTime;
            if (now - Patch_RegisterClientReceivers.lastRequestAt < Protocol.REQUEST_COOLDOWN_SECONDS) return;
            Patch_RegisterClientReceivers.lastRequestAt = now;
            MP3TogetherPlugin.SendPendingRequest(true);
        }
        private static void ClientRequestOnOpen() {
            if (Patch_GateNativeMusicListRequest.ClientApproved) return;
            if (HostPluginCheck.CurrentStatus == HostPluginCheck.Status.Confirmed) SendClientRequest();
            else if (HostPluginCheck.CurrentStatus == HostPluginCheck.Status.Unknown) Patch_RegisterClientReceivers.requestWaiting = true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Mute(string[] args) {
            MuteList.RunCommand(args);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Stop(string[] args) {
            MuteList.RunStop(args);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int CleanHostClips() {
            int before = HostPlayback.OwnedClipCount;
            HostPlayback.FreeFinished();
            HostPlayback.Sweep();
            return before - HostPlayback.OwnedClipCount;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int CleanHostTransfers() {
            return Patch_RegisterServerReceivers.SweepIdle(Protocol.CLEANUP_IDLE_SECONDS);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Load(string[] args) {
            string sub = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (sub == "request") RequestLoad();
            else if (sub == "pending") LoadPending(args);
            else Msg.Log("mp3together: usage: MP3load request, MP3load pending [accept/reject]");
        }
        private static void LoadPending(string[] args) {
            if (Net.IsRunning && Net.IsClient) {
                Msg.Error("mp3together: this command works only for the host or a solo player.");
                return;
            }
            string choice = args != null && args.Length > 2 ? args[2].ToLowerInvariant() : "";
            if (choice == "accept") Patch_RegisterServerReceivers.AcceptPendingRequest();
            else if (choice == "reject") Patch_RegisterServerReceivers.RejectPendingRequest();
            else Msg.Log("mp3together: usage: MP3load pending [accept/reject]");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void OnMenuOpened() {
            if (Net.IsRunning && Net.IsClient) {
                ClientRequestOnOpen();
                return;
            }
            if (MP3TogetherPlugin.AutoLoadMusic.Value) {
                MP3TogetherPlugin.Debug.Trace("KrokBridge.OnMenuOpened", "AutoLoadMusic is on - loading the device folder music without a prompt");
                HeadlessMusicLoader.EnsureDeviceFolderPopulated();
            } else if (MP3TogetherPlugin.AutoRequestMusic.Value) {
                MP3TogetherPlugin.Debug.Trace("KrokBridge.OnMenuOpened", "menu opened with device folder music not loaded - showing the pending-request prompt");
                Patch_RegisterServerReceivers.Server_HandleLoadRequest(null);
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RequestLoad() {
            if (Net.IsRunning && Net.IsClient) {
                if (HostPluginCheck.CurrentStatus == HostPluginCheck.Status.Missing) {
                    Msg.Error("mp3together: the host doesn't have MP3Together, there is nothing to ask.");
                    return;
                }
                Patch_RegisterClientReceivers.lastRequestAt = Time.unscaledTime;
                MP3TogetherPlugin.SendPendingRequest(false);
                Msg.Log(Patch_GateNativeMusicListRequest.ClientApproved ? "mp3together: MP3load - the host approved you before, asked it to refresh the music list." : "mp3together: MP3load - sent pending request to host.");
            } else {
                Patch_RegisterServerReceivers.Server_HandleLoadRequest(null);
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void AcceptPending() {
            Patch_RegisterServerReceivers.AcceptPendingRequest();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RejectPending() {
            Patch_RegisterServerReceivers.RejectPendingRequest();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SendPluginPing() {
            var writer = Protocol.Writer(Protocol.MSG_PLUGIN_PING);
            writer.Put(Protocol.VERSION);
            var dm = DeliveryMethod.ReliableOrdered;
            Net.Client_Send(in dm, in writer);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PushMusicList() {
            if (!Net.IsServer || !Net.IsRunning) return;
            try {
                var send = AccessTools.Method(typeof(MP3PlayerServerAudioStreamer), "Server_SendMusicList");
                if (send != null) send.Invoke(null, new object[] { null });
            } catch (Exception e) {
                MP3TogetherPlugin.Log.LogWarning("mp3together: PushMusicList failed: " + e.Message);
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool PlayBase(int index) {
            var menuType = UITypes.MP3Menu;
            var itemField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
            var itemObj = itemField != null ? itemField.GetValue(null) : null;
            if (itemObj == null) {
                MP3TogetherPlugin.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer active but no known mp3 player instance - falling back to local MusicManager playback");
                return false;
            }
            if (Net.IsClient) {
                SyncInfo syncInfo;
                if (NetObjectRegistry.TryGetSyncInfoOrRegister((Component)itemObj, out syncInfo)) {
                    var writer = Net.CreateWriter((byte)NetmsgId.SERVER_MP3PlayCustomMusic);
                    writer.Put(index);
                    writer.Put(syncInfo.syncId);
                    var dm = DeliveryMethod.ReliableUnordered;
                    Net.Client_Send(in dm, in writer);
                    MP3TogetherPlugin.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer client - sent native play-request msg 10137 index=" + index + " syncId=" + syncInfo.syncId);
                } else {
                    MP3TogetherPlugin.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer client - mp3 player not registered, cannot send play request");
                }
                return true;
            }
            var clipsFieldMp = AccessTools.Field(menuType, "clips");
            var clipsMp = clipsFieldMp != null ? clipsFieldMp.GetValue(null) as System.Collections.IList : null;
            if (clipsMp != null && index >= 0 && index < clipsMp.Count) {
                var serverPlayMethod = AccessTools.Method(typeof(MP3Menu_Play_MultiplayerPatch), "Server_PlayThisSongOnThisMp3Player");
                if (serverPlayMethod != null) {
                    serverPlayMethod.Invoke(null, new object[] { clipsMp[index], itemObj });
                    ushort hostPlayerId;
                    if (Patch_InterceptBrowsePlay.TryGetCurrentPlayerSyncId(out hostPlayerId)) Patch_RegisterServerReceivers.ForgetStarter(hostPlayerId);
                    MP3TogetherPlugin.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - played directly via Server_PlayThisSongOnThisMp3Player index=" + index);
                } else {
                    MP3TogetherPlugin.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - Server_PlayThisSongOnThisMp3Player method not found");
                }
            } else {
                MP3TogetherPlugin.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - clips null or index " + index + " out of range");
            }
            return true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void HostStop() {
            ushort syncId;
            if (Patch_InterceptBrowsePlay.TryGetCurrentPlayerSyncId(out syncId)) HostPlayback.StopAudio(syncId);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void HostTogglePause() {
            ushort syncId;
            if (Patch_InterceptBrowsePlay.TryGetCurrentPlayerSyncId(out syncId)) HostPlayback.TogglePause(syncId);
        }
    }
}