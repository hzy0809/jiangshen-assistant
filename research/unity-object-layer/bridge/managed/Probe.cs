using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace JiangShen.ManagedProbe
{
    public static class Entry
    {
        private static bool started;
        internal const string SessionsRoot = @"G:\development\enjoy\jiangshen-assistant\research\unity-object-layer\bridge\sessions";
        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern int GetCurrentThreadId();

        // Host contract: invoke synchronously on independently verified Unity native + Mono main thread.
        // Before returning this method never looks up a target game object or invokes a game method.
        public static string Bootstrap(string absoluteSessionDir, string nonce, int verifiedUnityNativeThreadId)
        {
            if (verifiedUnityNativeThreadId <= 0 || GetCurrentThreadId() != verifiedUnityNativeThreadId)
                return "BOOTSTRAP_REJECTED;thread";
            if (!Command.ValidNonce(nonce)) return "BOOTSTRAP_REJECTED;nonce";
            if (started) return "BOOTSTRAP_REJECTED;already_started";
            StreamWriter writer = null;
            GameObject helper = null;
            try
            {
                string expected = Path.Combine(SessionsRoot, nonce);
                if (!Path.IsPathRooted(absoluteSessionDir) || !String.Equals(Path.GetFullPath(absoluteSessionDir), expected, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(expected))
                    return "BOOTSTRAP_REJECTED;session_path";
                started = true; // Never permit a second bootstrap, including after a partial failure.
                writer = new StreamWriter(new FileStream(Path.Combine(expected, "probe.events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                int managedId = Thread.CurrentThread.ManagedThreadId;
                helper = new GameObject("JSUnityProbe_" + nonce);
                helper.hideFlags = HideFlags.HideInHierarchy;
                Probe probe = helper.AddComponent<Probe>();
                probe.Initialize(writer, expected, nonce, verifiedUnityNativeThreadId, managedId);
                return "BOOTSTRAP_OK;protocol=1;native_tid=" + verifiedUnityNativeThreadId.ToString(CultureInfo.InvariantCulture) + ";managed_tid=" + managedId.ToString(CultureInfo.InvariantCulture) + ";await=update_ready";
            }
            catch
            {
                if (helper != null) UObject.Destroy(helper);
                if (writer != null) writer.Close();
                return "BOOTSTRAP_REJECTED;initialization";
            }
        }
    }

    public sealed class Probe : MonoBehaviour
    {
        private StreamWriter output;
        private string sessionDir, nonce;
        private int nativeId, managedId, callbacks, lastFrame = -1, verifiedFrames, sequence, eventCount;
        private bool initialized, ready, stopping, actionUsed, closed, threadFailureLogged;
        private DateTime startedUtc, phaseUtc;
        private int phase, phaseCallbacks;
        private Model model;
        private Snapshot original;
        private bool actionObserved, restoreObserved, restoreMethodInvoked, failureObserved;
        private ResourceObservation resourceObservation;
        private Snapshot lastResourceObservation;
        private string outcome = "UNVERIFIED";

        internal void Initialize(StreamWriter writer, string directory, string sessionNonce, int nativeThread, int managedThread)
        {
            output = writer; sessionDir = directory; nonce = sessionNonce;
            nativeId = nativeThread; managedId = managedThread;
            startedUtc = DateTime.UtcNow; initialized = true;
            Emit("bootstrap", "\"assembly\":" + Command.Quote(typeof(Entry).Assembly.GetName().Name) + ",\"target_access\":false,\"await_distinct_update_frames\":2");
        }

        private void Update()
        {
            if (!initialized || closed) return;
            try
            {
                if (Entry.GetCurrentThreadId() != nativeId || Thread.CurrentThread.ManagedThreadId != managedId)
                {
                    if (!threadFailureLogged) TryEmit("thread_invariant_failed", "\"target_access\":false");
                    threadFailureLogged = true; stopping = true; failureObserved = true; outcome = "FAIL";
                    // No Unity calls or target reflection from an unexpected thread.
                    // A subsequent legitimate Update performs restore/cleanup on the owning thread.
                    return;
                }
                callbacks++;
                int frame = Time.frameCount;
                if (frame > lastFrame) verifiedFrames++;
                else if (frame < lastFrame) throw new ProbeFailure("frame_regressed");
                lastFrame = frame;
                if (!ready)
                {
                    if (verifiedFrames < 2) { Emit("update_witness", "\"distinct_frames\":" + verifiedFrames); return; }
                    ready = true;
                    Emit("update_ready", "\"distinct_frames\":" + verifiedFrames);
                    // Runtime/file identity checks and ALL target enumeration wait until this point.
                    model = new Model();
                    RecordSnapshot("initial_snapshot", model.Capture());
                    return;
                }
                if (phase != 0)
                {
                    Command pending = ReadCommand();
                    if (pending != null && pending.Name == "stop")
                    {
                        sequence = pending.Sequence; stopping = true;
                        Emit("stop_requested", "\"sequence\":" + sequence);
                    }
                    if ((stopping || callbacks >= 7200 || (DateTime.UtcNow - startedUtc).TotalSeconds >= 120) && phase <= 2)
                        TryStartRestore("stop_or_lifetime");
                    else Advance();
                }
                else if (stopping || callbacks >= 7200 || (DateTime.UtcNow - startedUtc).TotalSeconds >= 120) Finish("lifetime_or_stop");
                else Poll();
            }
            catch (Exception e)
            {
                string reason = e is ProbeFailure ? e.Message : e.GetType().Name;
                TryEmit("failure", "\"reason\":" + Command.Quote(reason));
                outcome = "FAIL"; failureObserved = true;
                if (phase == 1 || phase == 2) TryStartRestore("failure");
                else Finish("failure");
            }
        }

        private Command ReadCommand()
        {
            string path = Path.Combine(sessionDir, "command.txt");
            if (!File.Exists(path)) return null;
            FileInfo info = new FileInfo(path);
            if (info.Length > 256) return null;
            string text;
            try { text = File.ReadAllText(path, Encoding.ASCII); }
            catch (IOException) { return null; } // Writer should atomically rename command.tmp to command.txt.
            return Command.Parse(text, nonce, sequence);
        }

        private void Poll()
        {
            Command command = ReadCommand();
            if (command == null) return;
            sequence = command.Sequence;
            Emit("command", "\"sequence\":" + sequence + ",\"name\":" + Command.Quote(command.Name));
            if (command.Name == "stop") { stopping = true; Finish("stop"); return; }
            Snapshot snap = model.Capture();
            RecordSnapshot("snapshot", snap);
            if (command.Name != "disable-restore") return;
            if (actionUsed) { Emit("action_rejected", "\"reason\":\"one_shot\""); return; }
            actionUsed = true; // Even an unsuccessful action request consumes the one-shot guard.
            if (!snap.Safe) { Emit("action_rejected", "\"reason\":" + Command.Quote(snap.Gate)); return; }
            original = snap;
            // A second independent capture immediately before invoke checks identity + formation stability.
            Snapshot check = model.Capture();
            if (!snap.InteractionEnabled || !check.Safe || !check.InteractionEnabled || !snap.SameTarget(check) || snap.Resources != check.Resources)
                throw new ProbeFailure("preinvoke_snapshot_changed");
            resourceObservation = new ResourceObservation(Model.ResourceNames, original.ResourceValues);
            lastResourceObservation = original;
            phase = 1; phaseUtc = DateTime.UtcNow; phaseCallbacks = callbacks;
            model.SetInteraction(original, false);
            Snapshot applied = model.Capture(original.Main);
            RecordSnapshot("action_invoked", applied);
            if (!applied.Safe || !original.SameTarget(applied) || applied.InteractionEnabled) throw new ProbeFailure("disable_not_observed");
        }

        private void Advance()
        {
            Snapshot current = model.Capture(original.Main);
            if (!original.SameTarget(current)) throw new ProbeFailure("target_or_character_changed");
            if (!ContextSafe(current)) { AbandonRestore(current.Gate); return; }
            ObserveResources(current);
            bool expectedEnabled = phase == 3;
            bool stateVerified = current.InteractionEnabled == expectedEnabled;
            if (phase <= 2 && current.InteractionEnabled)
            {
                failureObserved = true; outcome = "FAIL";
                RecordSnapshot("interaction_state_drift", current);
                TryStartRestore("interaction_state_drift"); return;
            }
            if ((DateTime.UtcNow - phaseUtc).TotalSeconds > (phase == 2 ? 12 : 8) || callbacks - phaseCallbacks > (phase == 2 ? 1800 : 360))
            {
                outcome = "FAIL"; failureObserved = true;
                if (phase <= 2) TryStartRestore("interaction_timeout");
                else { RecordSnapshot("restore_timeout", current); Finish("restore_timeout"); }
                return;
            }
            if (phase == 1 && stateVerified && callbacks - phaseCallbacks >= 2)
            {
                actionObserved = true; phase = 2; phaseUtc = DateTime.UtcNow; phaseCallbacks = callbacks;
                RecordSnapshot("action_verified", current);
                Emit("interaction_evidence", "\"action_name\":\"mainui_disable_restore\",\"original_main_enabled\":true,\"disabled_observed\":true,\"visual_gray_state\":\"UNVERIFIED\"");
            }
            else if (phase == 2 && (DateTime.UtcNow - phaseUtc).TotalSeconds >= 6.0)
                TryStartRestore("normal");
            else if (phase == 3 && stateVerified && callbacks - phaseCallbacks >= 2)
            {
                restoreObserved = true;
                RecordSnapshot("restore_verified", current);
                bool resourcesEqual = resourceObservation.FinalEqual(current.ResourceValues);
                bool everySampleEqual = resourceObservation.EverySampleEqual();
                outcome = actionObserved && restoreMethodInvoked && !failureObserved ? "PASS" : "FAIL";
                Emit("transaction_result", "\"action_name\":\"mainui_disable_restore\",\"ui_status\":" + Command.Quote(outcome) + ",\"disabled_state_verified\":" + Bool(actionObserved) + ",\"restored_enabled_verified\":true,\"restore_method_invoked\":" + Bool(restoreMethodInvoked) + ",\"visual_gray_state\":\"UNVERIFIED\",\"resource_equality_status\":" + Command.Quote(resourcesEqual && everySampleEqual ? "PASS" : "FAIL") + ",\"allowlist_resources_equal\":" + Bool(resourcesEqual) + ",\"allowlist_equal_at_every_sample\":" + Bool(everySampleEqual) + ",\"resource_observations\":" + resourceObservation.Samples + ",\"resource_comparison\":" + resourceObservation.Json(current.ResourceValues) + ",\"resource_consumption_causality\":\"UNVERIFIED\",\"global_network_effects\":\"UNVERIFIED\"");
                phase = 0; Finish("transaction_complete");
            }
        }

        private void TryStartRestore(string reason)
        {
            try
            {
                Snapshot current = model.Capture(original.Main);
                if (!original.SameTarget(current)) throw new ProbeFailure("restore_context_changed");
                if (!ContextSafe(current)) { AbandonRestore(current.Gate); return; }
                ObserveResources(current);
                phase = 3; phaseUtc = DateTime.UtcNow; phaseCallbacks = callbacks;
                if (current.InteractionEnabled)
                    RecordSnapshot("restore_already_observed", current);
                else
                {
                    model.SetInteraction(original, true); restoreMethodInvoked = true;
                    RecordSnapshot("restore_invoked", model.Capture(original.Main));
                }
                Emit("restore_reason", "\"reason\":" + Command.Quote(reason));
            }
            catch (Exception e)
            {
                TryEmit("restore_failed", "\"reason\":" + Command.Quote(e is ProbeFailure ? e.Message : e.GetType().Name));
                phase = 0; outcome = "FAIL"; Finish("restore_failed");
            }
        }

        private static bool ContextSafe(Snapshot current)
        {
            // Capture's narrowly owned disabled-state allowance is evaluated only
            // after every identity, scene, global-input and overlay check.
            return current.Safe;
        }

        private void AbandonRestore(string gate)
        {
            failureObserved = true; outcome = "FAIL"; phase = 0;
            TryEmit("restoration_unverified", "\"reason\":" + Command.Quote(gate) + ",\"target_invoked\":false");
            Finish("unsafe_context");
        }

        private void RecordSnapshot(string name, Snapshot s)
        {
            ObserveResources(s);
            Emit(name, s.Json());
        }
        private void ObserveResources(Snapshot s)
        {
            if (resourceObservation == null || original == null || !original.SameTarget(s) || object.ReferenceEquals(lastResourceObservation, s)) return;
            resourceObservation.Observe(s.ResourceValues); lastResourceObservation = s;
        }
        private static string Bool(bool b) { return b ? "true" : "false"; }
        private void Emit(string name, string fields)
        {
            if (output == null || ++eventCount > 256) throw new ProbeFailure("output_unavailable_or_limit");
            output.WriteLine("{\"event\":" + Command.Quote(name) + ",\"nonce\":" + Command.Quote(nonce) + ",\"utc\":" + Command.Quote(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)) + ",\"native_tid\":" + Entry.GetCurrentThreadId() + ",\"managed_tid\":" + Thread.CurrentThread.ManagedThreadId + ",\"callbacks\":" + callbacks + ",\"frame\":" + lastFrame + "," + fields + "}");
            output.Flush();
        }
        private void TryEmit(string name, string fields) { try { Emit(name, fields); } catch { } }
        private void Finish(string reason)
        {
            if (closed) return;
            closed = true;
            TryEmit("cleanup_requested", "\"reason\":" + Command.Quote(reason) + ",\"action_used\":" + Bool(actionUsed) + ",\"restored\":" + Bool(restoreObserved) + ",\"ui_status\":" + Command.Quote(outcome) + ",\"assembly_unload\":\"process_restart_required\"");
            enabled = false;
            UObject.Destroy(gameObject);
            // Keep output until OnDestroy so cleanup has an actual Unity lifecycle receipt.
        }
        private void OnDestroy()
        {
            if (!initialized) return;
            TryEmit("component_destroyed", "\"own_component\":true,\"restored\":" + Bool(restoreObserved));
            if (output != null) { output.Close(); output = null; }
            model = null; original = null;
        }
    }

    internal sealed class ProbeFailure : Exception
    {
        internal ProbeFailure(string code) : base(code) { }
    }

    internal sealed class Snapshot
    {
        internal Component Main, Root;
        internal object Character;
        internal int InstanceId, Formation, Scene;
        internal bool InteractionEnabled;
        internal string Resources, Gate, LevelId, OtherVisibleUiTypes;
        internal long[] ResourceValues;
        internal bool Safe;
        internal bool SameTarget(Snapshot other)
        {
            return other != null && Main != null && other.Main != null && object.ReferenceEquals(Main, other.Main) &&
                object.ReferenceEquals(Character, other.Character) && Root == other.Root && Formation == other.Formation && Scene == other.Scene;
        }
        internal string Json()
        {
            return "\"action_name\":\"mainui_disable_restore\",\"main_instance_id\":" + InstanceId + ",\"scene\":" + Scene + ",\"root_level_id\":" + Command.Quote(LevelId) + ",\"gate\":" + Command.Quote(Gate) + ",\"safe\":" + (Safe ? "true" : "false") + ",\"main_enabled\":" + (InteractionEnabled ? "true" : "false") + ",\"formation_set\":" + Formation + ",\"other_visible_ui_types\":" + (OtherVisibleUiTypes == null ? "null" : OtherVisibleUiTypes) + ",\"resources\":" + (Resources == null ? "null" : Resources);
        }
    }

    internal sealed class Model
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private readonly Type mainType, globalType, baseUiType, assetType, buildingType;
        private readonly Type rootUiType, popupType, tooltipType, messengerType, flowerType, buildingBehaviourType, capitalType, buildingAreaType;
        private readonly Type[] ambientTypes;
        private readonly MethodInfo disable, enable, findObjects;
        internal static readonly string[] ResourceNames = new string[] { "level", "stamina", "gold", "bind_gold", "experience", "practice", "spirit_food", "mana", "stone", "reputation", "money", "money64", "cur_formation_set", "formation_set" };

        internal Model()
        {
            Pin(typeof(object).Assembly, "mscorlib", "2.0.5.0", "3F34C90AB282E8F3546946263087FB5002CF96EF9F412DD4011DAB3F0F7CD8A4");
            Pin(typeof(UObject).Assembly, "UnityEngine", "0.0.0.0", "D479BEFC03AFB0F2071FFCF108847F06A0D560075EB8A76980F0AE929115ED73");
            // UnityEngine's System.Type API signatures refer to desktop mscorlib
            // 2.0.0.0, while this installed profile is 2.0.5.0. Keep that foreign
            // TypeRef out of our payload; invoke only this fixed/pinned API.
            MethodInfo[] unityMethods = typeof(UObject).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            for (int i = 0; i < unityMethods.Length; i++)
            {
                MethodInfo m = unityMethods[i];
                if (m.Name != "FindObjectsOfType" || m.IsGenericMethod || m.ReturnType != typeof(UObject[])) continue;
                ParameterInfo[] parameters = m.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType.FullName != "System.Type") continue;
                if (findObjects != null) throw new ProbeFailure("unity_find_ambiguous");
                findObjects = m;
            }
            if (findObjects == null) throw new ProbeFailure("unity_find_signature");
            Assembly game = Loaded("Assembly-CSharp"), first = Loaded("Assembly-CSharp-firstpass"), pb = Loaded("pbdata");
            Pin(game, "Assembly-CSharp", null, "C1C8EBAC7DDEF4173C873A00774975A5CA366F95B19164C4CA775165EB994911");
            Pin(first, "Assembly-CSharp-firstpass", null, "29D8FA2CB55E800CCDF1CDC6C5B274B924EA7D03C989296DFA569731E02A8D87");
            Pin(pb, "pbdata", null, "905CF19394545E2C4B5D49D994A7D981E13AE38A2A629F7D02329F27C6D2D5AB");
            mainType = RequiredType(game, "MainUI"); globalType = RequiredType(first, "Global");
            baseUiType = FindInHierarchy(mainType, "__BaseUI"); assetType = FindInHierarchy(mainType, "BaseAsset");
            buildingType = RequiredType(game, "BuildingBuildUI");
            rootUiType = RequiredType(game, "RootUI");
            popupType = RequiredType(first, "PopupUI"); tooltipType = RequiredType(first, "TooltipUI");
            messengerType = RequiredType(game, "MessengerUI"); flowerType = RequiredType(game, "FlowerMessUI");
            buildingBehaviourType = RequiredType(game, "BuildingBehaviourScript"); capitalType = RequiredType(game, "CapitalUI");
            buildingAreaType = RequiredType(first, "Database.BuildingArea");
            ambientTypes = new Type[] { RequiredType(game, "BuildingMonsterScript"), RequiredType(game, "SecretShopNPC"), RequiredType(game, "CheckLauncherVersion"), RequiredType(game, "AnnounceBehaviour"), RequiredType(game, "MeditationUI"), RequiredType(game, "ChatBehaviour") };
            if (!typeof(MonoBehaviour).IsAssignableFrom(mainType)) throw new ProbeFailure("main_not_component");
            disable = VoidMethod(baseUiType, "Disable"); enable = VoidMethod(baseUiType, "Enable");
            if (!disable.IsVirtual || !enable.IsVirtual || disable.DeclaringType != baseUiType || enable.DeclaringType != baseUiType) throw new ProbeFailure("interaction_method_signature");
            for (Type t = mainType; t != baseUiType; t = t.BaseType)
            {
                if (t == null) throw new ProbeFailure("interaction_method_hierarchy");
                MethodInfo[] declared = t.GetMethods(AnyInstance);
                for (int i = 0; i < declared.Length; i++)
                    if ((declared[i].Name == "Enable" || declared[i].Name == "Disable") && declared[i].GetParameters().Length == 0) throw new ProbeFailure("interaction_method_override");
            }
        }

        internal Snapshot Capture() { return Capture(null); }
        internal Snapshot Capture(Component ownDisabledMain)
        {
            Snapshot s = new Snapshot(); s.Gate = "main_not_unique";
            s.Scene = Application.loadedLevel;
            UObject[] candidates = Find(mainType);
            int count = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                Behaviour b = candidates[i] as Behaviour;
                if (b != null && b.enabled && b.gameObject.activeInHierarchy)
                {
                    if (b.GetType() != mainType) { s.Gate = "unexpected_main_type"; return s; }
                    s.Main = b; count++;
                }
            }
            if (count != 1) return s;
            s.InstanceId = s.Main.GetInstanceID();
            object character = Read(globalType, "character_", null, true);
            if (character == null) { s.Gate = "character_null"; return s; }
            s.Character = character;
            s.Resources = ResourceJson(character, out s.ResourceValues);
            s.Formation = Convert.ToInt32(Read(character.GetType(), "formation_set", character, false));
            s.InteractionEnabled = (bool)Read(baseUiType, "enable", s.Main, false);
            object loadScreen = Read(assetType, "lds", null, true);
            Component load = loadScreen as Component;
            if (load == null || !load.gameObject.activeInHierarchy) { s.Gate = "loadscreen_missing"; return s; }
            s.Root = Read(load.GetType(), "currmg", load, false) as Component;
            Component mainRoot = Read(assetType, "rmg", s.Main, false) as Component;
            if (s.Root == null || s.Root != mainRoot || !s.Root.gameObject.activeInHierarchy || s.Main.transform.root != s.Root.transform) { s.Gate = "not_current_root"; return s; }
            s.LevelId = (string)Read(s.Root.GetType(), "LevelID", s.Root, false);
            if (String.IsNullOrEmpty(s.LevelId)) { s.Gate = "root_level_unknown"; return s; }
            if (((Behaviour)load).enabled || (bool)Read(load.GetType(), "isBattling", load, false) || (bool)Read(s.Root.GetType(), "bLoading", s.Root, false) || Convert.ToInt32(Read(s.Root.GetType(), "ExpectingCount", s.Root, false)) != 0)
            { s.Gate = "loading_or_battle"; return s; }
            if (!(bool)Read(globalType, "extern_enabled", null, true)) { s.Gate = "global_input_disabled"; return s; }
            if ((bool)Read(baseUiType, "first", s.Main, false) || !(bool)Read(assetType, "m_bInit", s.Main, false)) { s.Gate = "main_not_initialized"; return s; }
            if ((bool)Read(buildingType, "m_bBuildVisible", null, true)) { s.Gate = "building_overlay"; return s; }
            if ((bool)Read(popupType, "popup", null, true)) { s.Gate = "popup_active"; return s; }
            if ((bool)Read(buildingBehaviourType, "m_bEditVisible", null, true)) { s.Gate = "building_edit_visible"; return s; }
            UObject sub = Read(mainType, "m_subui", null, true) as UObject;
            if (sub != null) { s.Gate = "subui_present"; return s; }
            if (Convert.ToInt32(Read(baseUiType, "UICount_Visible", null, true)) != 0) { s.Gate = "visible_ui_count"; return s; }
            // Exact Type identities from pinned assemblies distinguish ambient scene
            // components from dynamic panels. Unknown types remain blocking. Every
            // dynamic check precedes the final owned interaction-state allowance.
            UObject[] overlays = Find(baseUiType);
            StringBuilder others = new StringBuilder("[");
            bool otherVisible = false;
            string blockedGate = null;
            for (int i = 0; i < overlays.Length; i++)
            {
                Behaviour overlay = overlays[i] as Behaviour;
                if (overlay != null && overlay != s.Main && overlay.enabled && overlay.gameObject.activeInHierarchy && overlay.GetType() != rootUiType)
                {
                    if (otherVisible) others.Append(',');
                    others.Append(Command.Quote(overlay.GetType().FullName)); otherVisible = true;
                    string gate = OverlayGate(overlay);
                    if (blockedGate == null && gate != null) blockedGate = gate;
                }
            }
            others.Append(']'); s.OtherVisibleUiTypes = others.ToString();
            if (blockedGate != null) { s.Gate = blockedGate; return s; }
            if (s.Formation < 1 || s.Formation > 6) { s.Gate = "state_invalid"; return s; }
            if (!s.InteractionEnabled && (ownDisabledMain == null || !object.ReferenceEquals(s.Main, ownDisabledMain))) { s.Gate = "main_interaction_disabled"; return s; }
            s.Safe = true; s.Gate = "PASS";
            return s;
        }

        private string OverlayGate(Behaviour overlay)
        {
            Type type = overlay.GetType();
            for (int i = 0; i < ambientTypes.Length; i++) if (type == ambientTypes[i]) return null;
            if (type == popupType) return (bool)Read(popupType, "popup", null, true) ? "popup_active" : null;
            if (type == tooltipType)
                return (string)Read(tooltipType, "TooltipFlag", overlay, false) == String.Empty && (string)Read(tooltipType, "tttitle", overlay, false) == String.Empty ? null : "tooltip_active";
            if (type == buildingBehaviourType)
                return !(bool)Read(buildingBehaviourType, "m_bEdit", overlay, false) && !(bool)Read(buildingBehaviourType, "m_bEditVisible", null, true) ? null : "building_edit_active";
            if (type == flowerType)
                return !(bool)Read(flowerType, "fc", overlay, false) && !(bool)Read(flowerType, "rm", overlay, false) ? null : "flower_modal_active";
            if (type == capitalType)
            {
                object area = Read(capitalType, "curArea", null, true);
                return area != null && area.GetType() == buildingAreaType && (bool)Read(buildingAreaType, "unlocked", area, false) ? null : "capital_unlock_panel";
            }
            if (type == messengerType)
            {
                FieldInfo activeField = Field(messengerType, "active_sessions", false), pendingField = Field(messengerType, "pending_sessions", false);
                object active = activeField.GetValue(overlay), pending = pendingField.GetValue(overlay);
                if (active == null || pending == null || active.GetType() != activeField.FieldType || pending.GetType() != pendingField.FieldType) return "messenger_schema";
                IDictionary sessions = active as IDictionary; IList waiting = pending as IList;
                // Counts only: never enumerate keys, player names, sessions or messages.
                return sessions != null && waiting != null && sessions.Count == 0 && waiting.Count == 0 ? null : "messenger_active";
            }
            return "visible_unknown_ui";
        }

        internal void SetInteraction(Snapshot original, bool restore)
        {
            if (original.Main == null || original.Main.GetType() != mainType || !original.InteractionEnabled) throw new ProbeFailure("interaction_target_invalid");
            (restore ? enable : disable).Invoke(original.Main, new object[0]);
        }

        private UObject[] Find(Type type)
        {
            if (type != mainType && type != baseUiType) throw new ProbeFailure("unity_find_type_not_whitelisted");
            return (UObject[])findObjects.Invoke(null, new object[] { type });
        }

        private static string ResourceJson(object character, out long[] values)
        {
            StringBuilder b = new StringBuilder("{");
            values = new long[ResourceNames.Length];
            for (int i = 0; i < ResourceNames.Length; i++)
            {
                if (i != 0) b.Append(',');
                FieldInfo field = Field(character.GetType(), ResourceNames[i], false);
                Type t = field.FieldType;
                if (t != typeof(short) && t != typeof(int) && t != typeof(uint) && t != typeof(long)) throw new ProbeFailure("resource_type");
                values[i] = Convert.ToInt64(field.GetValue(character), CultureInfo.InvariantCulture);
                b.Append(Command.Quote(ResourceNames[i])); b.Append(':'); b.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            b.Append('}'); return b.ToString();
        }
        private static Type RequiredType(Assembly a, string name)
        {
            Type t = a.GetType(name, false); if (t == null) throw new ProbeFailure("type_missing"); return t;
        }
        private static Type FindInHierarchy(Type t, string name)
        {
            for (; t != null; t = t.BaseType) if (t.FullName == name) return t;
            throw new ProbeFailure("base_type_missing");
        }
        private static FieldInfo Field(Type t, string name, bool isStatic)
        {
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, isStatic ? AnyStatic : AnyInstance);
                if (f != null && f.IsStatic == isStatic) return f;
            }
            throw new ProbeFailure("field_missing_" + name);
        }
        private static object Read(Type t, string name, object target, bool isStatic) { return Field(t, name, isStatic).GetValue(target); }
        private static MethodInfo VoidMethod(Type t, string name)
        {
            MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new Type[0], null);
            if (m == null || m.ReturnType != typeof(void) || m.IsStatic || m.IsGenericMethod) throw new ProbeFailure("method_signature");
            return m;
        }
        private static Assembly Loaded(string name)
        {
            Assembly found = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
                if (assemblies[i].GetName().Name == name)
                { if (found != null) throw new ProbeFailure("duplicate_assembly"); found = assemblies[i]; }
            if (found == null) throw new ProbeFailure("assembly_missing"); return found;
        }
        private static void Pin(Assembly a, string name, string version, string hash)
        {
            AssemblyName n = a.GetName();
            if (n.Name != name || (version != null && n.Version.ToString() != version) || String.IsNullOrEmpty(a.Location)) throw new ProbeFailure("assembly_identity");
            using (FileStream input = File.OpenRead(a.Location))
            using (SHA256Managed sha = new SHA256Managed())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                if (actual != hash) throw new ProbeFailure("assembly_hash_" + name);
            }
        }
    }
}
