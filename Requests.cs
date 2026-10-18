using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using Together;
using LiteNetLib.Utils;

namespace MP3Client {
    internal static class NetHandlers {
        private static bool registered;
        internal static void RegisterOnce() {
            if (registered) return;
            registered = true;
            MP3TogetherDebug.Debug.NetState("NetHandlers.RegisterOnce", "registering custom message receivers once per game process (the game keeps them across sessions)");
            bool ok = Patch_RegisterClientReceivers.Register();
            ok &= Patch_RegisterServerReceivers.Register();
            if (!ok) StartupGuard.Fail(MP3TogetherDebug.Log, "network handlers failed");
        }
        internal static bool Client(string name, string stage, Action<knetid, NetDataReader> handler) {
            try {
                Multiplayer.RegisterCustomClientReceiver(name, Guard.ClientHandler(stage, handler));
                return true;
            } catch (Exception e) {
                CrashLog.Capture("register client receiver " + name, e);
                return false;
            }
        }
        internal static bool Server(string name, string stage, Action<knetid, NetDataReader> handler) {
            try {
                Multiplayer.RegisterCustomServerReceiver(name, Guard.ServerHandler(stage, handler));
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
            ok &= NetHandlers.Client(Protocol.MSG_LOAD_APPROVED, "OnLoadApproved", OnLoadApproved);
            ok &= NetHandlers.Client(Protocol.MSG_ACK, "OnAck", OnAck);
            ok &= NetHandlers.Client(Protocol.MSG_REJECT, "OnReject", OnReject);
            MP3TogetherDebug.Debug.NetState("Patch_RegisterClientReceivers.Register", "client handlers registered (resend request, plugin pong, load approved, ack, reject): " + ok);
            return ok;
        }
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
            MP3TogetherDebug.Debug.SendRejected(syncId, reason, maxBytes);
            LocalLibraryUI.AbortActive(syncId);
            string text;
            if (reason == Protocol.REJECT_TOO_LARGE) text = "host refused the file, too large" + (maxBytes > 0 ? " (limit " + (maxBytes / 1048576f).ToString("F1") + " MB)." : ".");
            else if (reason == Protocol.REJECT_BUSY) text = "host is busy receiving other songs, try again in a moment.";
            else if (reason == Protocol.REJECT_REPLACED) text = "another player started a song on this MP3 player, your transfer was replaced.";
            else text = "host refused the file.";
            Multiplayer.DoMultiplayerStatusMessageError("mp3client: " + text);
        }
        static void OnPluginPong(knetid sender, NetDataReader reader) {
            byte hostVersion = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)1;
            HostPluginCheck.HostMaxFileBytes = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
            MP3TogetherDebug.Debug.NetState("OnPluginPong", "received from sender=" + sender + " hostProtocol=" + hostVersion + " hostMaxFileBytes=" + HostPluginCheck.HostMaxFileBytes);
            HostPluginCheck.HostProtocol = hostVersion;
            HostPluginCheck.OnPong();
        }
        static void OnLoadApproved(knetid sender, NetDataReader reader) {
            MP3TogetherDebug.Debug.NetState("OnLoadApproved", "received MSG_LOAD_APPROVED from sender=" + sender);
            MP3TogetherDebug.Debug.Trace("OnLoadApproved", "host approved custom music load");
            Multiplayer.DoMultiplayerStatusMessageLog("mp3client: host approved - loading device folder music now.");
            Patch_GateNativeMusicListRequest.ClientApproved = true;
            Patch_GateNativeMusicListRequest.TrySendNativeMusicListRequest();
        }
        static void OnResendRequest(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int fromOffset = reader.GetInt();
            MP3TogetherDebug.Debug.NetState("OnResendRequest", "sender=" + sender + " syncId=" + syncId + " fromOffset=" + fromOffset);
            if (!Patch_InterceptBrowsePlay.pendingFiles.TryGetValue(syncId, out var cachedData)) {
                MP3TogetherDebug.Debug.NetState("OnResendRequest", "syncId=" + syncId + " not found in pendingFiles, ignoring");
                return;
            }
            if (fromOffset >= cachedData.Bytes.Length) { MP3TogetherDebug.Debug.NetState("OnResendRequest", "fromOffset=" + fromOffset + " >= length " + cachedData.Bytes.Length + ", ignoring"); return; }
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
    }
    internal static class Patch_RegisterServerReceivers {
        static readonly Dictionary<ushort, Transfer> transfers = new Dictionary<ushort, Transfer>();
        static readonly Dictionary<ushort, int> ackCounters = new Dictionary<ushort, int>();
        internal static bool Register() {
            bool ok = true;
            ok &= NetHandlers.Server(Protocol.MSG_BEGIN, "OnBegin", OnBegin);
            ok &= NetHandlers.Server(Protocol.MSG_CHUNK, "OnChunk", OnChunk);
            ok &= NetHandlers.Server(Protocol.MSG_END, "OnEnd", OnEnd);
            ok &= NetHandlers.Server(Protocol.MSG_CANCEL, "OnCancel", OnCancel);
            ok &= NetHandlers.Server(Protocol.MSG_PAUSE, "OnPause", OnPause);
            ok &= NetHandlers.Server(Protocol.MSG_PLUGIN_PING, "OnPluginPing", OnPluginPing);
            ok &= NetHandlers.Server(Protocol.MSG_ASK_LOAD, "OnPendingRequest", OnPendingRequest);
            MP3TogetherDebug.Debug.NetState("Patch_RegisterServerReceivers.Register", "server handlers registered (begin, chunk, end, cancel, pause, plugin ping, ask load): " + ok);
            return ok;
        }
        static readonly HashSet<ushort> ackCapable = new HashSet<ushort>();
        static readonly HashSet<ushort> outdatedAlerted = new HashSet<ushort>();
        static void AlertOutdatedClient(knetid sender, byte clientVersion) {
            string who = Notify.DescribePlayer(sender);
            string clientMsg = "[MP3Client] Your MP3Client is outdated (protocol " + clientVersion + ", host needs " + Protocol.VERSION + " / v" + UpdaterConfig.PLUGIN_VERSION + "+). Update to the latest version: " + Protocol.RELEASES_URL;
            string hostMsg = "[MP3Client] " + who + " runs an outdated MP3Client (protocol " + clientVersion + ", current " + Protocol.VERSION + ") - told them to update.";
            MP3TogetherDebug.Log.LogWarning("mp3client: " + hostMsg);
            MP3TogetherDebug.Debug.OutdatedClient((ushort)sender, clientVersion);
            Multiplayer.DoMultiplayerStatusMessageLog(hostMsg);
            Notify.LogToHostChat(hostMsg);
            Notify.ChatTo(sender, clientMsg);
            Notify.AlertTo(sender, clientMsg);
        }
        static void OnPluginPing(knetid sender, NetDataReader reader) {
            byte clientVersion = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)1;
            if (clientVersion >= 2) ackCapable.Add((ushort)sender); else ackCapable.Remove((ushort)sender);
            if (clientVersion < Protocol.VERSION && outdatedAlerted.Add((ushort)sender)) AlertOutdatedClient(sender, clientVersion);
            MP3TogetherDebug.Debug.NetState("OnPluginPing", "received from sender=" + sender + " clientProtocol=" + clientVersion + ", replying with MSG_PLUGIN_PONG");
            var writer = Protocol.Writer(Protocol.MSG_PLUGIN_PONG);
            writer.Put(Protocol.VERSION);
            writer.Put(MaxFileBytes());
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { sender };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        internal static knetid? pendingRequester;
        internal static bool pendingActive;
        internal static void ResetForNewSession() {
            pendingRequester = null;
            pendingActive = false;
            transfers.Clear();
            DecoderHandler.ResetForNewSession();
            ackCapable.Clear();
            outdatedAlerted.Clear();
        }
        internal static void DismissPendingPromptIfAny(string reason) {
            if (!pendingActive) return;
            MP3TogetherDebug.Debug.Trace("DismissPendingPromptIfAny", "clearing pending request (" + reason + ") - music is already loading via a different path");
            pendingActive = false;
            pendingRequester = null;
            PendingPromptUI.Hide();
        }
        static void OnPendingRequest(knetid sender, NetDataReader reader) {
            bool automatic;
            reader.Get(out automatic);
            MP3TogetherDebug.Debug.NetState("OnPendingRequest", "received MSG_ASK_LOAD from " + sender + " automatic=" + automatic);
            Server_HandleLoadRequest(sender);
        }
        internal static void Server_HandleLoadRequest(knetid? sender) {
            string who = sender.HasValue ? sender.Value.ToString() : "host (self)";
            if (pendingActive) {
                MP3TogetherDebug.Debug.Trace("Server_HandleLoadRequest", "load request from " + who + " supersedes previous pending request - prompt already showing, not re-queuing");
                pendingRequester = sender;
                return;
            }
            pendingRequester = sender;
            pendingActive = true;
            MP3TogetherDebug.Debug.Trace("Server_HandleLoadRequest", "load request from " + who + " is now PENDING - showing prompt to host");
            Multiplayer.DoMultiplayerStatusMessageLog("mp3client: [ALERT] music load requested - type MP3accept or MP3reject, or use the prompt.");
            MP3TogetherDebug.PostHudOnlyStatus("mp3client: pending music-load request - MP3accept / MP3reject");
            PendingPromptUI.Show(AcceptPendingRequest, RejectPendingRequest);
        }
        internal static void ForceLoad() {
            if (!Net.IsServer) {
                MP3TogetherDebug.Debug.Trace("ForceLoad", "not the host, ignoring");
                return;
            }
            MP3TogetherDebug.Debug.Trace("ForceLoad", "bypassing the pending-request system entirely - loading device folder music now");
            pendingActive = false;
            pendingRequester = null;
            HeadlessMusicLoader.EnsureDeviceFolderPopulated();
            Multiplayer.DoMultiplayerStatusMessageLog("mp3client: loadcustommusic - bypassed the pending system, loading device folder music now.");
        }
        internal static void AcceptPendingRequest() {
            if (!pendingActive) {
                MP3TogetherDebug.Debug.Trace("AcceptPendingRequest", "no pending request");
                Multiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3accept - no pending request.");
                return;
            }
            knetid? requester = pendingRequester;
            string who = requester.HasValue ? requester.Value.ToString() : "host (self)";
            MP3TogetherDebug.Debug.Trace("AcceptPendingRequest", "approving pending request from " + who);
            pendingActive = false;
            pendingRequester = null;
            if (requester.HasValue) {
                HeadlessMusicLoader.EnsureDeviceFolderPopulated();
                SendLoadApproved(requester.Value);
                SendLoadResult(requester, true);
            } else {
                HeadlessMusicLoader.EnsureDeviceFolderPopulated();
            }
            Multiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3accept - approved.");
        }
        internal static void RejectPendingRequest() {
            if (!pendingActive) {
                MP3TogetherDebug.Debug.Trace("RejectPendingRequest", "no pending request");
                Multiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3reject - no pending request.");
                return;
            }
            knetid? requester = pendingRequester;
            string who = requester.HasValue ? requester.Value.ToString() : "host (self)";
            MP3TogetherDebug.Debug.Trace("RejectPendingRequest", "denying pending request from " + who);
            pendingActive = false;
            pendingRequester = null;
            Multiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3reject - request denied.");
            SendLoadResult(requester, false);
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
            MP3TogetherDebug.Debug.NetState("OnPause", "received MSG_PAUSE from sender=" + sender + " syncId=" + syncId);
            HostPlayback.TogglePause(syncId);
        }
        internal static int MaxFileBytes() {
            return Protocol.MAX_FILE_BYTES;
        }
        static string Megabytes(long bytes) {
            return (bytes / 1048576f).ToString("F1");
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
        static void RejectTransfer(knetid sender, ushort syncId, ushort transferId, byte reason, int totalLen, int maxBytes) {
            string name = reason == Protocol.REJECT_TOO_LARGE ? "too large" : reason == Protocol.REJECT_BUSY ? "busy" : "invalid";
            string text;
            if (reason == Protocol.REJECT_TOO_LARGE) text = "File is too large: " + Megabytes(Math.Max(totalLen, 0)) + " MB, the host allows " + Megabytes(maxBytes) + " MB.";
            else if (reason == Protocol.REJECT_BUSY) text = "The host is busy receiving other songs, try again in a moment.";
            else text = "The host can't accept this file type (mp3, wav and ogg only).";
            string who = Notify.DescribePlayer(sender);
            MP3TogetherDebug.Log.LogWarning("mp3client: rejected transfer from " + who + " syncId=" + syncId + " (" + name + "): " + text);
            MP3TogetherDebug.Debug.TransferRejected((ushort)sender, syncId, name, totalLen);
            Multiplayer.DoMultiplayerStatusMessageLog("mp3client: rejected a song from " + who + " - " + text);
            Notify.AlertTo(sender, "[MP3Client] " + text);
            if (transferId != 0) SendReject(sender, syncId, transferId, reason, maxBytes);
        }
        static void DropTransfer(ushort syncId, Transfer tr, string reason) {
            tr.Cancelled = true;
            transfers.Remove(syncId);
            DecoderHandler.Invalidate(syncId);
            DecoderHandler.Cancel(syncId);
            HostPlayback.StopIfPreviewOf(syncId, tr.Seq);
            tr.Data = null;
            MP3TogetherDebug.Debug.TransferSuperseded((ushort)tr.Sender, syncId, reason);
        }
        static void OnBegin(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int totalLen = reader.GetInt();
            string ext = reader.GetString();
            ushort transferId = reader.AvailableBytes >= 2 ? reader.GetUShort() : (ushort)0;
            ushort senderId = (ushort)sender;
            ext = ext != null ? ext.ToLowerInvariant() : "";
            int maxBytes = MaxFileBytes();
            if (!PreviewData.IsAllowedExtension(ext)) {
                RejectTransfer(sender, syncId, transferId, Protocol.REJECT_INVALID, totalLen, maxBytes);
                return;
            }
            if (totalLen <= 0 || totalLen > maxBytes) {
                RejectTransfer(sender, syncId, transferId, Protocol.REJECT_TOO_LARGE, totalLen, maxBytes);
                return;
            }
            Transfer old;
            bool replacing = transfers.TryGetValue(syncId, out old);
            if (!replacing && transfers.Count >= Protocol.MAX_CONCURRENT_TRANSFERS) {
                RejectTransfer(sender, syncId, transferId, Protocol.REJECT_BUSY, totalLen, maxBytes);
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
                    Notify.AlertTo(old.Sender, "[MP3Client] Another player started a song on this MP3 player, your transfer was replaced.");
                    if (old.TransferId != 0) SendReject(old.Sender, syncId, old.TransferId, Protocol.REJECT_REPLACED, 0);
                }
            }
            if (DecoderHandler.Cancel(syncId)) MP3TogetherDebug.Debug.DecodeReplaced(syncId);
            int chunkCount = (totalLen + Protocol.CHUNK_SIZE - 1) / Protocol.CHUNK_SIZE;
            int seq = DecoderHandler.NextSeq(syncId);
            if (transferId != 0) ackCounters[syncId] = 0;
            transfers[syncId] = new Transfer {
                Seq = seq,
                Extension = ext,
                TotalLength = totalLen,
                Sender = sender,
                TransferId = transferId,
                LastActivity = Time.unscaledTime,
                Data = new byte[totalLen],
                ChunkReceived = new bool[Math.Max(chunkCount, 1)]
            };
            MP3TogetherDebug.Debug.RecvBegin(syncId, totalLen, ext);
            MP3TogetherDebug.Debug.TransferFrom(senderId, syncId, totalLen, ext);
            MP3TogetherDebug.Debug.ActiveTransfers(transfers.Count, BufferedBytes());
        }
        static long BufferedBytes() {
            long total = 0;
            foreach (var t in transfers.Values) {
                if (t.Data != null) total += t.Data.Length;
            }
            return total;
        }
        internal static void SweepIdle() {
            float now = Time.unscaledTime;
            foreach (ushort id in new List<ushort>(transfers.Keys)) {
                Transfer t = transfers[id];
                if (t.EndReceived || now - t.LastActivity < Protocol.TRANSFER_IDLE_SECONDS) continue;
                DropTransfer(id, t, "no data for " + Protocol.TRANSFER_IDLE_SECONDS.ToString("F0") + "s (sender gone or stalled)");
            }
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
            MP3TogetherDebug.Debug.PreviewStarted(syncId, prefixBytes, tr.TotalLength);
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
                MP3TogetherDebug.Instance.StartCoroutine(WaitThenFinalize(syncId, tr));
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
                    MP3TogetherDebug.Debug.RecvStalled(syncId, resendFrom);
                    RequestResend(tr.Sender, syncId, resendFrom);
                    lastProgressTime = Time.unscaledTime;
                }
                yield return null;
            }
            if (!tr.Finalized) {
                MP3TogetherDebug.Debug.RecvDeadlineHit(syncId);
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
            if (DecoderHandler.Cancel(syncId)) MP3TogetherDebug.Debug.DecodeReplaced(syncId);
            DecoderHandler.Start(syncId, tr.Seq, tr.Extension, tr.Data, false, IsLiveTransfer);
            tr.Data = null;
        }
        static void OnCancel(knetid sender, NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            Transfer tr;
            bool anyTransfer = transfers.TryGetValue(syncId, out tr);
            bool hadTransfer = anyTransfer && (ushort)tr.Sender == (ushort)sender;
            if (anyTransfer && !hadTransfer) {
                MP3TogetherDebug.Debug.Trace("OnCancel", "ignored cancel from " + sender + " for syncId=" + syncId + ", another player's transfer is in progress");
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
            MP3TogetherDebug.Debug.RecvCancelled(syncId);
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
            if (!MP3TogetherDebug.Active) return;
            MP3TogetherDebug.Debug.Trace("Patch_BypassForceLoadMusic", "Server_ForceLoadMusic invoked (loadcustommusic command or native list-request auto-recovery) - host-only/authorized upstream, dismissing any pending prompt");
            Patch_RegisterServerReceivers.DismissPendingPromptIfAny("loadcustommusic/native auto-recovery bypass fired");
        }
    }
    [HarmonyPatch(typeof(MP3PlayerServerAudioStreamer), "Server_MP3RequestMusicList")]
    internal static class Patch_EnsureBaseBeforeListRequest {
        static void Prefix() {
            Guard.Run("Patch_EnsureBaseBeforeListRequest.Prefix", delegate () {
                if (MP3TogetherDebug.Active && Net.IsServer) HeadlessMusicLoader.EnsureBaseGamePopulated();
            });
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
                MP3TogetherDebug.Debug.NetState("TrySendNativeMusicListRequest", "sent native 'request server music list' (msg " + NATIVE_MSG_REQUEST_MUSIC_LIST + ") now that approval was granted");
            } catch (Exception e) {
                MP3TogetherDebug.Log.LogWarning("mp3client: TrySendNativeMusicListRequest failed: " + e.Message);
            }
        }
    }
}