using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Together;

namespace MP3Together {
    internal static class VolumeBars {
        private const string SHOW_PLAYERS_BIND = "casualtiestogether_showplrs";
        private const string TEMPLATE = "Special/GameSettingFloat";
        private const float STEP = 0.05f;
        private const float WIDTH = 420f;
        private const float TOP = 12f;
        private const float GAP = 6f;
        private class Bar {
            public string Name;
            public bool Instance;
            public FieldInfo Field;
            public SettingDeclarerThingyFloatAttribute Attr;
            public GameObject Row;
            public TextMeshProUGUI Caption;
            public Slider Slider;
            public TextMeshProUGUI ValueText;
            public bool Dirty;
        }
        private static Bar[] bars;
        private static GameObject buildLabel;
        private static string buildText;
        private static bool built;
        private static bool shown;
        private static bool buildFailed;
        private static bool inputReady;
        private static Func<KeyCode, bool> getKey;
        private static Func<Vector3> mousePosition;
        private static Func<Vector2> scrollDelta;
        private static void InitInput() {
            string[] names = { "UnityEngine.Input, UnityEngine.InputLegacyModule", "UnityEngine.Input, UnityEngine.InputModule", "UnityEngine.Input, UnityEngine.CoreModule", "UnityEngine.Input, UnityEngine" };
            foreach (string name in names) {
                Type t = Type.GetType(name, false);
                if (t == null) continue;
                MethodInfo key = t.GetMethod("GetKey", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(KeyCode) }, null);
                PropertyInfo pos = t.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static);
                PropertyInfo scroll = t.GetProperty("mouseScrollDelta", BindingFlags.Public | BindingFlags.Static);
                if (key == null || pos == null || scroll == null) continue;
                getKey = (Func<KeyCode, bool>)Delegate.CreateDelegate(typeof(Func<KeyCode, bool>), key);
                mousePosition = (Func<Vector3>)Delegate.CreateDelegate(typeof(Func<Vector3>), pos.GetGetMethod());
                scrollDelta = (Func<Vector2>)Delegate.CreateDelegate(typeof(Func<Vector2>), scroll.GetGetMethod());
                inputReady = true;
                MP3TogetherPlugin.Debug.UIState("VolumeBars", "input type resolved from " + name);
                return;
            }
            buildFailed = true;
            MP3TogetherPlugin.Log.LogWarning("mp3together: UnityEngine.Input with GetKey, mousePosition and mouseScrollDelta not found, volume bars are off.");
        }
        private static Bar MakeInstance(string name) {
            return new Bar { Name = name, Instance = true };
        }
        private static Bar MakeSetting(string fieldName, string name) {
            var field = typeof(Voicechat).GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null) return null;
            var attrs = field.GetCustomAttributes(typeof(SettingDeclarerThingyFloatAttribute), true);
            if (attrs.Length == 0) return null;
            return new Bar { Name = name, Field = field, Attr = (SettingDeclarerThingyFloatAttribute)attrs[0] };
        }
        private static FieldInfo usedPlayerField;
        private static StreamedAudioOutput instanceTarget;
        private static Component usedItem;
        private static ushort usedSyncId;
        private static bool hasSync;
        internal static void NoteUsedPlayer() {
            if (usedPlayerField == null) usedPlayerField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
            var item = usedPlayerField != null ? usedPlayerField.GetValue(null) as Component : null;
            if (item != null) usedItem = item;
        }
        private static void ResolveInstance() {
            instanceTarget = null;
            hasSync = false;
            if (usedItem == null) return;
            instanceTarget = usedItem.GetComponent<MP3PlayerAudioStreamPlayerBase>() ?? usedItem.GetComponentInParent<MP3PlayerAudioStreamPlayerBase>() ?? usedItem.GetComponentInChildren<MP3PlayerAudioStreamPlayerBase>(true);
            SyncInfo si;
            if (NetObjectRegistry.TryGetSyncInfo(usedItem, out si)) {
                usedSyncId = si.syncId;
                hasSync = true;
            }
        }
        private static string BuildText() {
            if (buildText == null) buildText = "MP3Together " + UpdaterConfig.PLUGIN_VERSION + " build " + typeof(VolumeBars).Module.ModuleVersionId.ToString("N").Substring(0, 8).ToUpperInvariant();
            return buildText;
        }
        private static void BuildLabel(Canvas canvas, Bar reference, float y) {
            var go = new GameObject("MP3Together_BuildLabel", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(WIDTH, 24f);
            rect.anchoredPosition = new Vector2(0f, -y);
            var label = go.AddComponent<TextMeshProUGUI>();
            if (reference != null && reference.Caption != null) label.font = reference.Caption.font;
            label.fontSize = reference != null && reference.Caption != null ? Mathf.Max(12f, reference.Caption.fontSize * 0.7f) : 14f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(1f, 1f, 1f, 0.6f);
            label.raycastTarget = false;
            label.text = BuildText();
            go.SetActive(false);
            buildLabel = go;
        }
        private static string Display(float value) {
            return value.ToString("0.##");
        }
        private static void Refresh(Bar bar) {
            float value;
            if (bar.Instance) {
                value = InstanceVolume.Shown(hasSync, usedSyncId, instanceTarget);
            } else {
                value = (float)bar.Field.GetValue(null);
            }
            bar.ValueText.text = Display(value);
            bar.Slider.SetValueWithoutNotify(value);
        }
        private static void Build() {
            var camera = PlayerCamera.main;
            if (camera == null || camera.mainCanvas == null) return;
            bars = new Bar[] { MakeInstance("MP3 instance volume"), MakeSetting("mp3_listener_volume", "Client income volume") };
            float y = TOP;
            foreach (Bar bar in bars) {
                if (bar == null) continue;
                GameObject row = Utils.Create(TEMPLATE, camera.mainCanvas.transform);
                if (row == null) {
                    buildFailed = true;
                    MP3TogetherPlugin.Log.LogWarning("mp3together: settings slider template " + TEMPLATE + " not found, volume bars are off.");
                    return;
                }
                var rect = row.GetComponent<RectTransform>();
                float height = rect.sizeDelta.y > 1f ? rect.sizeDelta.y : 40f;
                rect.anchorMin = new Vector2(0.5f, 1f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(WIDTH, height);
                rect.anchoredPosition = new Vector2(0f, -y);
                y += height + GAP;
                row.transform.SetAsLastSibling();
                bar.Caption = row.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
                bar.Slider = row.transform.GetChild(1).GetComponent<Slider>();
                bar.ValueText = row.transform.GetChild(2).GetComponent<TextMeshProUGUI>();
                bar.Caption.text = bar.Name;
                bar.Slider.minValue = bar.Instance ? 0f : bar.Attr.mi;
                bar.Slider.maxValue = bar.Instance ? 2f : bar.Attr.ma;
                Bar captured = bar;
                bar.Slider.onValueChanged.AddListener(delegate (float v) {
                    if (captured.Instance) {
                        InstanceVolume.Set(hasSync, usedSyncId, instanceTarget, v);
                    } else {
                        captured.Field.SetValue(null, v);
                        captured.Dirty = true;
                    }
                    captured.ValueText.text = Display(v);
                });
                bar.Row = row;
                row.SetActive(false);
            }
            Bar first = null;
            foreach (Bar bar in bars) {
                if (bar != null && bar.Row != null) {
                    first = bar;
                    break;
                }
            }
            BuildLabel(camera.mainCanvas, first, y);
            built = true;
        }
        private static void Show() {
            ResolveInstance();
            foreach (Bar bar in bars) {
                if (bar == null) continue;
                bar.Row.SetActive(true);
                bar.Row.transform.SetAsLastSibling();
                Refresh(bar);
            }
            if (buildLabel != null) {
                buildLabel.SetActive(true);
                buildLabel.transform.SetAsLastSibling();
            }
            shown = true;
        }
        private static void Hide() {
            foreach (Bar bar in bars) {
                if (bar == null || bar.Row == null) continue;
                bar.Row.SetActive(false);
                if (bar.Dirty) {
                    bar.Attr.Save(bar.Field, bar.Field.GetValue(null));
                    bar.Dirty = false;
                }
            }
            if (buildLabel != null) buildLabel.SetActive(false);
            shown = false;
        }
        private static void Wheel() {
            float wheel = scrollDelta().y;
            if (wheel == 0f) return;
            var canvas = PlayerCamera.main.mainCanvas;
            Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            foreach (Bar bar in bars) {
                if (bar == null || bar.Row == null) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(bar.Row.GetComponent<RectTransform>(), mousePosition(), cam)) continue;
                bar.Slider.value = Mathf.Round((bar.Slider.value + Mathf.Sign(wheel) * STEP) * 100f) / 100f;
            }
        }
        internal static void Tick() {
            InstanceVolume.Tick();
            if (buildFailed) return;
            if (built && (buildLabel == null || bars[0] != null && bars[0].Row == null || bars[1] != null && bars[1].Row == null)) {
                built = false;
                shown = false;
            }
            if (!inputReady) InitInput();
            if (!inputReady) return;
            bool wanted = Krok.IsRunning && getKey(KeyBinds.GetBind(SHOW_PLAYERS_BIND));
            if (wanted && !built) Build();
            if (!built) return;
            if (wanted && !shown) Show();
            else if (!wanted && shown) Hide();
            if (shown) Wheel();
        }
    }
    internal static class InstanceVolume {
        private const float MAX_VOLUME = 2f;
        private const float SEND_INTERVAL_SECONDS = 0.1f;
        private const float REBROADCAST_SECONDS = 5f;
        private const float LOCAL_PRIORITY_SECONDS = 1f;
        private const byte SHARED_PROTOCOL = 7;
        private static readonly Dictionary<ushort, float> volumes = new Dictionary<ushort, float>();
        private static readonly Dictionary<ushort, float> pending = new Dictionary<ushort, float>();
        private static readonly Dictionary<ushort, float> localSetAt = new Dictionary<ushort, float>();
        private static float nextSend;
        private static float nextRebroadcast;
        internal static void ResetForNewSession() {
            volumes.Clear();
            pending.Clear();
            localSetAt.Clear();
            nextSend = 0f;
            nextRebroadcast = 0f;
        }
        internal static bool Shared {
            get { return Krok.IsServer || (Krok.IsClient && HostPluginCheck.CurrentStatus == HostPluginCheck.Status.Confirmed && HostPluginCheck.HostProtocol >= SHARED_PROTOCOL); }
        }
        internal static float Shown(bool has, ushort syncId, StreamedAudioOutput local) {
            float v;
            if (has && volumes.TryGetValue(syncId, out v)) return v;
            return local != null ? local.custom_volume_set : 1f;
        }
        internal static void Set(bool has, ushort syncId, StreamedAudioOutput local, float v) {
            v = Mathf.Clamp(v, 0f, MAX_VOLUME);
            if (has) {
                volumes[syncId] = v;
                localSetAt[syncId] = Time.unscaledTime;
                if (Shared) pending[syncId] = v;
            }
            if (local != null) local.custom_volume_set = v;
        }
        internal static void HostReceive(knetid sender, ushort syncId, float v) {
            if (!Krok.IsServer || float.IsNaN(v)) return;
            if (MuteList.IsMuted(sender)) {
                MuteList.NoteBlocked(sender);
                return;
            }
            SyncInfo si;
            if (!NetObjectRegistry.TryGetSyncInfo((knetid)syncId, out si)) return;
            v = Mathf.Clamp(v, 0f, MAX_VOLUME);
            volumes[syncId] = v;
            pending[syncId] = v;
            MP3TogetherPlugin.Debug.NetState("InstanceVolume", "client " + sender + " set the volume of mp3 player " + syncId + " to " + v.ToString("F2"));
        }
        internal static void ClientReceive(ushort syncId, float v) {
            if (float.IsNaN(v)) return;
            float at;
            if (localSetAt.TryGetValue(syncId, out at) && Time.unscaledTime - at < LOCAL_PRIORITY_SECONDS) return;
            volumes[syncId] = Mathf.Clamp(v, 0f, MAX_VOLUME);
        }
        internal static void SendAllTo(knetid target) {
            var items = new List<KeyValuePair<ushort, float>>();
            foreach (var kv in volumes) {
                if (Mathf.Abs(kv.Value - 1f) > 0.001f) items.Add(kv);
            }
            Broadcast(items, new List<knetid> { target });
        }
        private static void Broadcast(List<KeyValuePair<ushort, float>> items, List<knetid> targets) {
            if (items.Count == 0 || targets == null || targets.Count == 0) return;
            var writer = Protocol.Writer(Protocol.MSG_VOLUME);
            writer.Put((ushort)items.Count);
            foreach (var kv in items) {
                writer.Put(kv.Key);
                writer.Put(kv.Value);
            }
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> list = targets;
            Net.Server_SendToClients(in dm, in writer, in list);
        }
        private static void Flush() {
            var items = new List<KeyValuePair<ushort, float>>(pending);
            pending.Clear();
            if (Krok.IsServer) {
                Broadcast(items, Patch_RegisterServerReceivers.VolumeClientIds());
                return;
            }
            if (!Krok.IsClient) return;
            foreach (var kv in items) {
                var writer = Protocol.Writer(Protocol.MSG_VOLUME_SET);
                writer.Put(kv.Key);
                writer.Put(kv.Value);
                var dm = DeliveryMethod.ReliableOrdered;
                Net.Client_Send(in dm, in writer);
            }
        }
        private static void Rebroadcast() {
            var items = new List<KeyValuePair<ushort, float>>();
            foreach (ushort id in new List<ushort>(volumes.Keys)) {
                SyncInfo si;
                if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out si)) {
                    volumes.Remove(id);
                    continue;
                }
                float v = volumes[id];
                if (Mathf.Abs(v - 1f) > 0.001f) items.Add(new KeyValuePair<ushort, float>(id, v));
            }
            Broadcast(items, Patch_RegisterServerReceivers.VolumeClientIds());
        }
        private static void ApplyAll() {
            if (volumes.Count == 0) return;
            foreach (var kv in volumes) {
                SyncInfo si;
                if (!NetObjectRegistry.TryGetSyncInfo((knetid)kv.Key, out si) || si.go == null) continue;
                var target = si.go.GetComponent<MP3PlayerAudioStreamPlayerBase>();
                if (target != null && Mathf.Abs(target.custom_volume_set - kv.Value) > 0.001f) target.custom_volume_set = kv.Value;
            }
        }
        internal static void Tick() {
            ApplyAll();
            float now = Time.unscaledTime;
            if (pending.Count > 0 && now >= nextSend) {
                nextSend = now + SEND_INTERVAL_SECONDS;
                Flush();
            }
            if (Krok.IsServer && now >= nextRebroadcast) {
                nextRebroadcast = now + REBROADCAST_SECONDS;
                Rebroadcast();
            }
        }
    }
}