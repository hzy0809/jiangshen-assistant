using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    internal sealed class SoulBoardAuthority
    {
        internal string SourceSession = "", SourceOperation = "", Witness = "";
        internal int Mode;
    }
    // A read-only transition for a retired soul executor. Disk evidence
    // binds the request; only the live main-thread proof creates command authority.
    internal sealed class SoulRecoveryRequest
    {
        internal string Nonce, Role, OldSession, OldOperation, OldAssembly, MarkerHash, StateHash, SessionHash, Text;
        internal uint Pid;
        internal long Creation;
        internal int Growth, Stop;
        internal const string Kind = "soul-connection-reconciliation";
        internal const string OldReason = "daily_soul_request_or_cleanup_unresolved_restart_required";
        private static readonly string[] Keys = new string[] { "version", "kind", "nonce", "pid", "process_creation_filetime", "character_id",
            "old_session_id", "old_operation_id", "old_growth_sequence", "old_stop_sequence", "old_assembly_name", "old_marker_sha256", "old_state_sha256", "old_session_sha256" };

        internal static bool Hex(string text, int length)
        {
            if (text == null || text.Length != length) return false;
            foreach (char c in text) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        internal static void Require(bool predicate, string why) { if (!predicate) throw new ProbeFailure("same_process_recovery_" + why); }
        internal static byte[] ReadBounded(string path, int maximum)
        {
            Require(File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "file_missing_or_reparse:" + Path.GetFileName(path));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(stream.Length > 0 && stream.Length <= maximum, "file_size:" + Path.GetFileName(path));
                byte[] bytes = new byte[(int)stream.Length]; int read = 0;
                while (read != bytes.Length) { int count = stream.Read(bytes, read, bytes.Length - read); Require(count > 0, "file_short_read"); read += count; }
                Require(stream.Length == bytes.Length, "file_changed"); return bytes;
            }
        }
        internal static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        internal static SoulRecoveryRequest Read(string path, string nonce, uint pid, long creation)
        {
            byte[] bytes = ReadBounded(Path.Combine(path, "same-process-recovery.txt"), 2048);
            foreach (byte b in bytes) Require(b == 10 || b >= 32 && b <= 126, "request_ascii");
            string text = Encoding.ASCII.GetString(bytes); string[] lines = text.Split('\n');
            Require(lines.Length == Keys.Length + 1 && lines[Keys.Length] == "", "request_line_count");
            string[] values = new string[Keys.Length];
            for (int i = 0; i < Keys.Length; i++)
            { string prefix = Keys[i] + "="; Require(lines[i].StartsWith(prefix, StringComparison.Ordinal), "request_key:" + Keys[i]); values[i] = lines[i].Substring(prefix.Length); }
            SoulRecoveryRequest r = new SoulRecoveryRequest(); r.Text = text;
            r.Nonce = values[2]; r.Role = values[5]; r.OldSession = values[6]; r.OldOperation = values[7]; r.OldAssembly = values[10];
            r.MarkerHash = values[11]; r.StateHash = values[12]; r.SessionHash = values[13];
            ulong role;
            Require(values[0] == "1" && values[1] == Kind && r.Nonce == nonce && Command.ValidNonce(r.Nonce) &&
                UInt32.TryParse(values[3], NumberStyles.None, CultureInfo.InvariantCulture, out r.Pid) && r.Pid == pid && values[3] == pid.ToString(CultureInfo.InvariantCulture) &&
                Int64.TryParse(values[4], NumberStyles.None, CultureInfo.InvariantCulture, out r.Creation) && r.Creation == creation && values[4] == creation.ToString(CultureInfo.InvariantCulture) &&
                UInt64.TryParse(r.Role, NumberStyles.None, CultureInfo.InvariantCulture, out role) && role > 0 && r.Role == role.ToString(CultureInfo.InvariantCulture) &&
                Command.ValidNonce(r.OldSession) && r.OldSession != nonce && Command.ValidNonce(r.OldOperation) &&
                Int32.TryParse(values[8], NumberStyles.None, CultureInfo.InvariantCulture, out r.Growth) && r.Growth == 223 &&
                Int32.TryParse(values[9], NumberStyles.None, CultureInfo.InvariantCulture, out r.Stop) && r.Stop == 225 &&
                r.OldAssembly == "JSAssistantBridge_7486cd5cb2f8ce3b" && Hex(r.MarkerHash, 64) && Hex(r.StateHash, 64) && Hex(r.SessionHash, 64), "request_identity_or_schema");
            r.CheckFiles(path); return r;
        }
        internal void CheckFiles(string newDirectory)
        {
            Require(Encoding.ASCII.GetString(ReadBounded(Path.Combine(newDirectory, "same-process-recovery.txt"), 2048)) == Text, "request_changed");
            string directory = Path.Combine(Entry.SessionsRoot, OldSession);
            Require(Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "old_session_path");
            byte[] markerBytes = ReadBounded(Path.Combine(directory, "recovery_unknown.json"), 32768);
            byte[] stateBytes = ReadBounded(Path.Combine(directory, "state.json"), 2 * 1024 * 1024);
            byte[] sessionBytes = ReadBounded(Path.Combine(directory, "session.json"), 2 * 1024 * 1024);
            Require(Hash(markerBytes) == MarkerHash && Hash(stateBytes) == StateHash && Hash(sessionBytes) == SessionHash, "old_artifact_hash_changed");
            Hashtable marker = RecoveryJson.Object(markerBytes), state = RecoveryJson.Object(stateBytes), session = RecoveryJson.Object(sessionBytes);
            CheckIdentity(marker, true); CheckIdentity(state, true); CheckIdentity(session, false);
            Require(RecoveryJson.String(marker, "reason") == OldReason && RecoveryJson.Boolean(marker, "process_restart_required") &&
                RecoveryJson.Boolean(state, "component_destroyed") && !RecoveryJson.Boolean(state, "ready") && RecoveryJson.Boolean(state, "process_restart_required") &&
                RecoveryJson.Number(state, "last_command_seq") == Stop && RecoveryJson.String(state, "reason") == OldReason &&
                RecoveryJson.String(session, "state") == "unknown" && RecoveryJson.Number(session, "last_command_seq") >= Stop, "old_artifact_state");
            Hashtable soul = RecoveryJson.Child(RecoveryJson.Child(state, "daily_soul"), "operation");
            Require(RecoveryJson.String(soul, "operation_id") == OldOperation && RecoveryJson.Number(soul, "sequence") == Growth &&
                RecoveryJson.String(soul, "stage") == "grow" && RecoveryJson.String(soul, "status") == "UNKNOWN" &&
                RecoveryJson.Boolean(soul, "start_verified") && RecoveryJson.Boolean(soul, "growth_verified") &&
                !RecoveryJson.Boolean(soul, "harvest_verified") && RecoveryJson.Number(soul, "growth_evidence_sequence") == Growth, "old_artifact_soul");
        }
        private void CheckIdentity(Hashtable value, bool native)
        {
            Require(RecoveryJson.Number(value, "protocol") == 3 && RecoveryJson.String(value, "bridge_id") == Entry.BridgeId &&
                RecoveryJson.String(value, "nonce") == OldSession && RecoveryJson.String(value, "session_id") == OldSession &&
                RecoveryJson.Number(value, "pid") == Pid && RecoveryJson.Number(value, "process_creation_filetime") == Creation &&
                (!native || RecoveryJson.String(value, "assembly") == OldAssembly), "old_artifact_identity");
        }
        internal string Fields()
        {
            return "\"schema\":1,\"kind\":" + Command.Quote(Kind) + ",\"new_session_id\":" + Command.Quote(Nonce) + ",\"pid\":" + Pid +
                ",\"process_creation_filetime\":" + Creation.ToString(CultureInfo.InvariantCulture) + ",\"character_id\":" + Command.Quote(Role) +
                ",\"old_session_id\":" + Command.Quote(OldSession) + ",\"old_operation_id\":" + Command.Quote(OldOperation) +
                ",\"old_growth_sequence\":" + Growth + ",\"old_stop_sequence\":" + Stop + ",\"old_assembly_name\":" + Command.Quote(OldAssembly) +
                ",\"old_marker_sha256\":" + Command.Quote(MarkerHash) + ",\"old_state_sha256\":" + Command.Quote(StateHash) + ",\"old_session_sha256\":" + Command.Quote(SessionHash);
        }
    }

    // Bounded JSON reader for archived receipts. Duplicate fields, invalid UTF8,
    // unrecognized numeric forms and excessive depth fail before any target access.
    internal sealed class RecoveryJson
    {
        private readonly string text; private int at, items;
        private RecoveryJson(byte[] bytes) { text = new UTF8Encoding(false, true).GetString(bytes); }
        internal static Hashtable Object(byte[] bytes)
        { RecoveryJson r = new RecoveryJson(bytes); Hashtable value = r.Value(0) as Hashtable; r.White(); SoulRecoveryRequest.Require(value != null && r.at == r.text.Length, "json_root"); return value; }
        internal static Hashtable Child(Hashtable value, string key)
        { Hashtable child = value[key] as Hashtable; SoulRecoveryRequest.Require(child != null, "json_object:" + key); return child; }
        internal static string String(Hashtable value, string key)
        { string result = value[key] as string; SoulRecoveryRequest.Require(result != null, "json_string:" + key); return result; }
        internal static long Number(Hashtable value, string key)
        { SoulRecoveryRequest.Require(value[key] is long, "json_number:" + key); return (long)value[key]; }
        internal static bool Boolean(Hashtable value, string key)
        { SoulRecoveryRequest.Require(value[key] is bool, "json_boolean:" + key); return (bool)value[key]; }
        private void White() { while (at < text.Length && (text[at] == ' ' || text[at] == '\r' || text[at] == '\n' || text[at] == '\t')) at++; }
        private char Take() { SoulRecoveryRequest.Require(at < text.Length, "json_end"); return text[at++]; }
        private void Expect(char c) { SoulRecoveryRequest.Require(Take() == c, "json_token"); }
        private string Quoted()
        {
            Expect('"'); StringBuilder b = new StringBuilder();
            while (true)
            {
                char c = Take(); if (c == '"') return b.ToString();
                SoulRecoveryRequest.Require(c >= 32, "json_control");
                if (c != '\\') { b.Append(c); continue; }
                c = Take();
                if (c == '"' || c == '\\' || c == '/') b.Append(c);
                else if (c == 'b') b.Append('\b'); else if (c == 'f') b.Append('\f'); else if (c == 'n') b.Append('\n');
                else if (c == 'r') b.Append('\r'); else if (c == 't') b.Append('\t');
                else if (c == 'u')
                {
                    int n = 0; for (int i = 0; i < 4; i++) { c = Take(); int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; SoulRecoveryRequest.Require(d >= 0, "json_escape"); n = n * 16 + d; } b.Append((char)n);
                }
                else throw new ProbeFailure("same_process_recovery_json_escape");
            }
        }
        private object Value(int depth)
        {
            SoulRecoveryRequest.Require(depth <= 48 && ++items <= 100000, "json_bound"); White();
            SoulRecoveryRequest.Require(at < text.Length, "json_end"); char c = text[at];
            if (c == '"') return Quoted();
            if (c == '{')
            {
                at++; Hashtable map = new Hashtable(); White(); if (at < text.Length && text[at] == '}') { at++; return map; }
                while (true) { White(); string key = Quoted(); SoulRecoveryRequest.Require(!map.ContainsKey(key), "json_duplicate"); White(); Expect(':'); map.Add(key, Value(depth + 1)); White(); c = Take(); if (c == '}') return map; SoulRecoveryRequest.Require(c == ',', "json_separator"); }
            }
            if (c == '[')
            {
                at++; ArrayList list = new ArrayList(); White(); if (at < text.Length && text[at] == ']') { at++; return list; }
                while (true) { list.Add(Value(depth + 1)); White(); c = Take(); if (c == ']') return list; SoulRecoveryRequest.Require(c == ',', "json_separator"); }
            }
            if (c == 't' || c == 'f' || c == 'n')
            {
                string token = c == 't' ? "true" : c == 'f' ? "false" : "null";
                SoulRecoveryRequest.Require(at + token.Length <= text.Length && text.Substring(at, token.Length) == token, "json_literal"); at += token.Length;
                return c == 'n' ? null : (object)(c == 't');
            }
            int start = at; if (c == '-') at++;
            SoulRecoveryRequest.Require(at < text.Length && text[at] >= '0' && text[at] <= '9', "json_number");
            if (text[at] == '0') at++; else while (at < text.Length && text[at] >= '0' && text[at] <= '9') at++;
            // Session diagnostics can contain fractional timings. Preserve these
            // as doubles; identity readers above accept integer tokens only.
            bool fraction = false;
            if (at < text.Length && text[at] == '.') { fraction = true; at++; int first = at; while (at < text.Length && text[at] >= '0' && text[at] <= '9') at++; SoulRecoveryRequest.Require(at > first, "json_fraction"); }
            if (at < text.Length && (text[at] == 'e' || text[at] == 'E')) { fraction = true; at++; if (at < text.Length && (text[at] == '+' || text[at] == '-')) at++; int first = at; while (at < text.Length && text[at] >= '0' && text[at] <= '9') at++; SoulRecoveryRequest.Require(at > first, "json_exponent"); }
            string number = text.Substring(start, at - start); long integer; double floating;
            if (!fraction && Int64.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer)) return integer;
            SoulRecoveryRequest.Require(Double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out floating) && !Double.IsInfinity(floating) && !Double.IsNaN(floating), "json_numeric_range"); return floating;
        }
    }

    internal static class ProcessRecovery
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        // Fixed read-only cross-build cache. Only a durable native transition
        // writes these fields, and no disk-only receipt can populate them.
        private static string authorizedOldSession, authorizedOldOperation, authorizedOldAssembly, authorizedHash, authorizedJson;
        private static uint authorizedPid;
        private static long authorizedCreation;
        private static bool authorizedSoulQuarantine;
        private static string adoptedOperation, currentBoardHarvestJson, adoptionWitnessHash, adoptedSourceSession, adoptedSourceOperation;
        private static string completedSoulSources = "";
        private static SoulConnectionRecovery activeRecovery;

        internal static object Field(object target, string name, Type expected)
        {
            SoulRecoveryRequest.Require(!ReferenceEquals(target, null), "native_witness_missing:" + name);
            return Field(target.GetType(), target, name, expected, false);
        }
        private static object Field(Type type, object target, string name, Type expected, bool stat)
        {
            FieldInfo f = type.GetField(name, stat ? Static : Instance);
            SoulRecoveryRequest.Require(f != null && f.IsStatic == stat && (expected == null || f.FieldType == expected), "native_schema:" + type.Name + "." + name);
            return f.GetValue(target);
        }
        private static bool Flag(Type type, object target, string name, bool stat)
        {
            PropertyInfo p = type.GetProperty(name, stat ? Static : Instance);
            SoulRecoveryRequest.Require(p != null && p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null && p.GetGetMethod(true).IsStatic == stat, "native_property_schema:" + type.Name + "." + name);
            return (bool)p.GetValue(target, null);
        }
        private static Type Type(Assembly assembly, string name)
        { Type type = assembly.GetType("JiangShen.AssistantRuntime." + name, false); SoulRecoveryRequest.Require(type != null, "assembly_schema:" + assembly.GetName().Name + ":" + name); return type; }
        private static ArrayList Probes(Assembly assembly)
        {
            Type entry = Type(assembly, "Entry"); ArrayList result = new ArrayList();
            object active = Field(entry, null, "activeProbe", null, true);
            if (!ReferenceEquals(active, null)) result.Add(active);
            FieldInfo retiredField = entry.GetField("retiredProbes", Static);
            IList retired = retiredField == null ? null : retiredField.GetValue(null) as IList;
            if (retired != null) foreach (object probe in retired)
                if (!ReferenceEquals(probe, null) && !ReferenceEquals(probe, active)) result.Add(probe);
            return result;
        }
        internal static void CheckConnectionOwners()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                Type entry = Type(assembly, "Entry");
                object probe = Field(entry, null, "activeProbe", null, true);
                SoulRecoveryRequest.Require(probe == null || (bool)Field(probe, "destroyed", typeof(bool)), "connection_owner_active");
                Type recovery = assembly.GetType("JiangShen.AssistantRuntime.ProcessRecovery", false);
                FieldInfo helper = recovery == null ? null : recovery.GetField("activeRecovery", Static);
                SoulRecoveryRequest.Require(helper == null || ReferenceEquals(helper.GetValue(null), null), "connection_helper_active");
                // A destroyed component can keep its UNKNOWN registry bit; it
                // cannot keep a live connection lease.
                Type registry = Type(assembly, "SessionRegistry");
                object owner = Field(registry, null, "owner", typeof(string), true);
                SoulRecoveryRequest.Require(owner == null, "connection_lease_active");
            }
        }
        private static void Idle(object probe, string field, string property)
        { object model = Field(probe, field, null); if (model != null) SoulRecoveryRequest.Require(!Flag(model.GetType(), model, property, false), "non_soul_owner:" + field); }
        private static void CampaignIdle(object probe, string name)
        {
            object campaign = Field(probe, name, null); if (campaign == null) return;
            SoulRecoveryRequest.Require(!Flag(campaign.GetType(), campaign, "NavigationPending", false), "campaign_navigation:" + name);
            object observer = Field(campaign, "Observer", null);
            SoulRecoveryRequest.Require(observer != null && !Flag(observer.GetType(), observer, "Pending", false), "campaign_pending:" + name);
        }
        private static bool Granted(string assembly, string nonce, uint pid, long creation)
        {
            if (authorizedOldAssembly == assembly && authorizedOldSession == nonce && authorizedPid == pid && authorizedCreation == creation && SoulRecoveryRequest.Hex(authorizedHash, 64)) return true;
            foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!candidate.GetName().Name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                Type type = candidate.GetType("JiangShen.AssistantRuntime.ProcessRecovery", false); if (type == null) continue;
                if ((string)Field(type, null, "authorizedOldAssembly", typeof(string), true) == assembly &&
                    (string)Field(type, null, "authorizedOldSession", typeof(string), true) == nonce &&
                    (uint)Field(type, null, "authorizedPid", typeof(uint), true) == pid &&
                    (long)Field(type, null, "authorizedCreation", typeof(long), true) == creation &&
                    SoulRecoveryRequest.Hex((string)Field(type, null, "authorizedHash", typeof(string), true), 64)) return true;
            }
            return false;
        }
        internal static void CheckAll(SoulRecoveryRequest request, uint pid, long creation)
        {
            bool found = false;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name; if (!name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                SoulRecoveryRequest.Require(name.Length == 34 && SoulRecoveryRequest.Hex(name.Substring(18), 16), "assembly_identity:" + name);
                Type registry = Type(assembly, "SessionRegistry"), entry = Type(assembly, "Entry"), battle = Type(assembly, "BattleLaunch"), capture = Type(assembly, "DevelopmentCallbackCapture");
                SoulRecoveryRequest.Require(Field(registry, null, "owner", typeof(string), true) == null, "registry_owner:" + name);
                SoulRecoveryRequest.Require(!Flag(capture, null, "AnyCallbackExecuting", true) && !Flag(battle, null, "RestartRequired", true), "callback_or_battle_restart:" + name);
                SoulRecoveryRequest.Require(!Flag(battle, null, "Unresolved", true), "battle_unresolved:" + name);
                Type recoveryType = assembly.GetType("JiangShen.AssistantRuntime.ProcessRecovery", false);
                if (recoveryType != null)
                {
                    object helper = Field(recoveryType, null, "activeRecovery", null, true);
                    SoulRecoveryRequest.Require(ReferenceEquals(helper, null) || recoveryType == typeof(ProcessRecovery) && request != null && ReferenceEquals(helper, activeRecovery), "other_recovery_helper:" + name);
                }
                object probe = Field(entry, null, "activeProbe", null, true);
                bool unknown = (bool)Field(registry, null, "unknown", typeof(bool), true);
                bool selected = request != null && name == request.OldAssembly;
                if (ReferenceEquals(probe, null)) { SoulRecoveryRequest.Require(!selected && !unknown, "old_probe_missing_or_unknown:" + name); continue; }
                object life = Field(probe, "life", null); string nonce = (string)Field(probe, "nonce", typeof(string));
                SoulRecoveryRequest.Require((bool)Field(probe, "destroyed", typeof(bool)) && (bool)Field(life, "Destroyed", typeof(bool)) &&
                    (bool)Field(life, "StopRequested", typeof(bool)) && !(bool)Field(probe, "ready", typeof(bool)) &&
                    !(bool)Field(life, "ActionPending", typeof(bool)) && Field(probe, "guiCommand", null) == null &&
                    !(bool)Field(probe, "recoveryActive", typeof(bool)) &&
                    (int)Field(probe, "phase", typeof(int)) == 0 && !(bool)Field(probe, "titleQueued", typeof(bool)) &&
                    !(bool)Field(probe, "threadFailureLogged", typeof(bool)), "probe_not_retired:" + name);
                CampaignIdle(probe, "campaign"); CampaignIdle(probe, "challenge"); CampaignIdle(probe, "elite");
                Idle(probe, "development", "Pending"); Idle(probe, "playerRefresh", "Pending"); Idle(probe, "dailyRefresh", "Pending");
                string[] daily = new string[] { "dailyGuild", "dailyReward", "dailyTimed", "dailyStone", "dailyMingshi", "dailyArena", "dailyRefine", "dailyScript" };
                foreach (string field in daily) Idle(probe, field, "Busy");
                FieldInfo wood = probe.GetType().GetField("dailyWood", Instance); if (wood != null && wood.GetValue(probe) != null) SoulRecoveryRequest.Require(!Flag(wood.GetValue(probe).GetType(), wood.GetValue(probe), "Busy", false), "wood_owner");
                object settlement = Field(probe, "battleSettlement", null); if (settlement != null) SoulRecoveryRequest.Require(!Flag(settlement.GetType(), settlement, "BlocksGuiInvocation", false), "settlement_owner");
                bool recoveryUnknown = (bool)Field(life, "RecoveryUnknown", typeof(bool));
                if (selected)
                {
                    SoulRecoveryRequest.Require(!found && unknown && recoveryUnknown && nonce == request.OldSession &&
                        (long)Field(probe, "creationFiletime", typeof(long)) == creation &&
                        (string)Field(probe, "sessionDir", typeof(string)) == Path.Combine(Entry.SessionsRoot, request.OldSession) &&
                        (int)Field(life, "LastSequence", typeof(int)) == request.Stop &&
                        (string)Field(probe, "reason", typeof(string)) == SoulRecoveryRequest.OldReason &&
                        !(bool)Field(probe, "mutationAttempted", typeof(bool)), "old_probe_identity");
                    CheckOldSoul(Field(probe, "dailySoul", null), request); found = true;
                }
                else if (unknown || recoveryUnknown)
                    SoulRecoveryRequest.Require(Granted(name, nonce, pid, creation), "unresolved_other_epoch:" + name);
                else { object soul = Field(probe, "dailySoul", null); if (soul != null) SoulRecoveryRequest.Require(!Flag(soul.GetType(), soul, "Busy", false), "other_soul_owner"); }
            }
            SoulRecoveryRequest.Require(request == null || found, "old_assembly_missing");
            if (!ReferenceEquals(activeRecovery, null)) SoulRecoveryRequest.Require(request != null, "recovery_helper_active");
        }
        private static void CheckCapture(object capture)
        {
            if (ReferenceEquals(capture, null)) return;
            SoulRecoveryRequest.Require(!(bool)Field(capture, "armed", typeof(bool)), "callback_still_armed");
            object handler = Field(capture, "handler", null), wrapper = Field(capture, "wrapper", typeof(Delegate));
            FieldInfo callback = Field(capture, "callbackField", typeof(FieldInfo)) as FieldInfo;
            SoulRecoveryRequest.Require(callback != null && handler != null && !ReferenceEquals(callback.GetValue(handler), wrapper), "callback_still_installed");
        }
        private static void CheckOldSoul(object soul, SoulRecoveryRequest r)
        {
            SoulRecoveryRequest.Require((string)Field(soul, "operation", typeof(string)) == r.OldOperation && (int)Field(soul, "sequence", typeof(int)) == r.Growth &&
                (string)Field(soul, "stage", typeof(string)) == "grow" && (string)Field(soul, "status", typeof(string)) == "UNKNOWN" &&
                (string)Field(soul, "reason", typeof(string)) == "stop" && (string)Field(soul, "uiCleanup", typeof(string)) == "clear" &&
                (string)Field(soul, "queuedAction", typeof(string)) == "" && (bool)Field(soul, "startVerified", typeof(bool)) &&
                (bool)Field(soul, "growthVerified", typeof(bool)) && !(bool)Field(soul, "growPending", typeof(bool)) &&
                !(bool)Field(soul, "harvestInvoked", typeof(bool)) && !(bool)Field(soul, "harvestVerified", typeof(bool)) &&
                (int)Field(soul, "growthEvidenceSequence", typeof(int)) == r.Growth && (long)Field(soul, "coinsSpent", typeof(long)) == 53000,
                "old_soul_causal_chain");
            CheckCapture(Field(soul, "response", null)); CheckCapture(Field(soul, "activity", null));
            object origin = Field(soul, "origin", null);
            SoulRecoveryRequest.Require(origin != null && ((ulong)Field(origin, "Role", typeof(ulong))).ToString(CultureInfo.InvariantCulture) == r.Role, "old_soul_role");
        }
        internal static string Start(string directory, string nonce, int native, long creation)
        {
            SoulRecoveryRequest r = SoulRecoveryRequest.Read(directory, nonce, Entry.GetCurrentProcessId(), creation);
            CheckAll(r, r.Pid, creation);
            SoulRecoveryRequest.Require(ReferenceEquals(activeRecovery, null), "helper_already_exists");
            GameObject helper = null;
            try
            {
                helper = new GameObject("JSAssistantRecovery_" + nonce); helper.hideFlags = HideFlags.HideInHierarchy; UObject.DontDestroyOnLoad(helper);
                SoulConnectionRecovery recovery = helper.AddComponent<SoulConnectionRecovery>(); activeRecovery = recovery;
                recovery.Initialize(r, directory, native, Thread.CurrentThread.ManagedThreadId);
                return "BOOTSTRAP_OK;protocol=1;await=same_process_recovery";
            }
            catch { activeRecovery = null; if (helper != null) UObject.Destroy(helper); throw; }
        }
        internal static void End(SoulConnectionRecovery recovery) { if (ReferenceEquals(activeRecovery, recovery)) activeRecovery = null; }
        internal static void Grant(SoulRecoveryRequest request, string json, string hash, bool quarantine)
        {
            authorizedOldSession = request.OldSession; authorizedOldOperation = request.OldOperation; authorizedOldAssembly = request.OldAssembly;
            authorizedPid = request.Pid; authorizedCreation = request.Creation; authorizedHash = hash; authorizedSoulQuarantine = quarantine;
            authorizedJson = json.Substring(0, json.Length - 1) + ",\"resolution_sha256\":" + Command.Quote(hash) + "}";
        }
        internal static string StateJson { get { return authorizedJson ?? "null"; } }
        internal static string CurrentBoardHarvestJson { get { return currentBoardHarvestJson ?? "null"; } }
        private static bool SoulSourceSettled(string session, string operation)
        {
            string identity = "|" + session + "/" + operation + "|";
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                Type type = assembly.GetType("JiangShen.AssistantRuntime.ProcessRecovery", false);
                FieldInfo field = type == null ? null : type.GetField("completedSoulSources", Static);
                string settled = field == null ? null : field.GetValue(null) as string;
                if (settled != null && settled.IndexOf(identity, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
        internal static string BusinessRefusal(Command command)
        {
            if (command == null || command.Name.EndsWith("-observe", StringComparison.Ordinal) ||
                command.Name == "daily-script" && command.DailyScriptAction == "observe") return "";
            string field = null, property = "Busy";
            switch (command.Name)
            {
                case "daily-guild-command": field = "dailyGuild"; break;
                case "daily-reward-command": field = "dailyReward"; break;
                case "daily-timed-command": field = "dailyTimed"; break;
                case "daily-stone-command": field = "dailyStone"; break;
                case "daily-mingshi-command": field = "dailyMingshi"; break;
                case "daily-arena-command": field = "dailyArena"; break;
                case "daily-refine-command": field = "dailyRefine"; break;
                case "daily-wood-command": field = "dailyWood"; break;
                case "daily-script": field = "dailyScript"; break;
                case "development-command": field = "development"; property = "Pending"; break;
                case "player-state-refresh": field = "playerRefresh"; property = "Pending"; break;
                case "daily-state-refresh": field = "dailyRefresh"; property = "Pending"; break;
            }
            bool battleCommand = command.Name == "campaign-command" || command.Name == "challenge-command" || command.Name == "elite-command" ||
                command.Name == "daily-arena-command" || command.Name == "daily-script" && command.DailyScriptAction != "observe";
            if (field == null && !battleCommand && command.Name != "daily-soul-command") return "";
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name;
                    if (!name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                    if (battleCommand)
                    {
                        Type battle = Type(assembly, "BattleLaunch");
                        if (Flag(battle, null, "Unresolved", true) || Flag(battle, null, "RestartRequired", true)) return "battle_prior_operation_unresolved";
                    }
                    foreach (object probe in Probes(assembly))
                    {
                    if (ReferenceEquals(probe, Entry.CurrentProbe)) continue;
                    if (field != null)
                    {
                        FieldInfo modelField = probe.GetType().GetField(field, Instance);
                        object owner = modelField == null ? null : modelField.GetValue(probe);
                        if (owner != null && Flag(owner.GetType(), owner, property, false)) return "business_prior_operation_unresolved:" + field;
                    }
                    if (command.Name != "daily-soul-command") continue;
                    object soul = Field(probe, "dailySoul", null);
                    if (soul == null || !Flag(soul.GetType(), soul, "Unresolved", false)) continue;
                    string session = (string)Field(probe, "nonce", typeof(string)), operation = (string)Field(soul, "operation", typeof(string));
                    long creation = (long)Field(probe, "creationFiletime", typeof(long));
                    if (Granted(name, session, Entry.GetCurrentProcessId(), creation) || SoulSourceSettled(session, operation)) continue;
                    if (command.DailySoulAction != "adopt" && !(command.OperationId == adoptedOperation &&
                        (command.DailySoulAction == "grow" || command.DailySoulAction == "harvest" || command.DailySoulAction == "close"))) return "daily_soul_prior_operation_unresolved";
                    }
                }
                return "";
            }
            catch (Exception) { return "business_prior_operation_witness_unavailable"; }
        }
        // This proof belongs to soul admission, never to connection attachment.
        internal static SoulBoardAuthority CaptureBoardAuthority(D context, long board)
        {
            SoulRecoveryRequest.Require(context != null && context.Client != null, "adoption_context");
            object client = context.Client;
            SoulRecoveryRequest.Require(Monitor.TryEnter(client, 0), "adoption_client_busy");
            try { return CaptureBoardAuthorityLocked(context, board); }
            finally { Monitor.Exit(client); }
        }
        private static SoulBoardAuthority CaptureBoardAuthorityLocked(D context, long board)
        {
            SoulRecoveryRequest.Require(context != null && board != 0 && Entry.CurrentProbe != null, "adoption_context");
            SoulRecoveryRequest.Require(Entry.GetCurrentThreadId() == (int)Field(Entry.CurrentProbe, "nativeId", typeof(int)) &&
                Thread.CurrentThread.ManagedThreadId == (int)Field(Entry.CurrentProbe, "managedId", typeof(int)) &&
                context.SameOwnership(context) && object.ReferenceEquals(DailyTransport.CurrentClient(context.Character), context.Client), "adoption_main_thread_or_identity");
            object liveBoard = D.Read(context.Character, "gather_souls"); long livePacked = 0;
            SoulRecoveryRequest.Require(liveBoard != null && liveBoard.GetType() == Model.FirstType("Database.GatherSoul"), "adoption_board_schema");
            for (int i = 0; i < 8; i++) livePacked |= DailySoulWire.Slot(i, (byte)D.Read(liveBoard, "soul" + i + "_color"), (byte)D.Read(liveBoard, "soul" + i + "_level"));
            SoulRecoveryRequest.Require(livePacked == board, "adoption_board_changed");
            long creation = (long)Field(Entry.CurrentProbe, "creationFiletime", typeof(long));
            SoulBoardAuthority authority = new SoulBoardAuthority();
            string owners = "["; int ownerCount = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (!name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                Type capture = Type(assembly, "DevelopmentCallbackCapture"), battle = Type(assembly, "BattleLaunch");
                SoulRecoveryRequest.Require(!Flag(capture, null, "AnyCallbackExecuting", true), "adoption_callback_executing");
                SoulRecoveryRequest.Require(!Flag(battle, null, "Unresolved", true) && !Flag(battle, null, "RestartRequired", true), "adoption_battle_unresolved");
                foreach (object probe in Probes(assembly))
                {
                if (ReferenceEquals(probe, Entry.CurrentProbe)) continue;
                object life = Field(probe, "life", null);
                SoulRecoveryRequest.Require((bool)Field(probe, "destroyed", typeof(bool)) && (bool)Field(life, "Destroyed", typeof(bool)) &&
                    (bool)Field(life, "StopRequested", typeof(bool)) && !(bool)Field(probe, "ready", typeof(bool)) &&
                    !(bool)Field(life, "ActionPending", typeof(bool)) && Field(probe, "guiCommand", null) == null &&
                    !(bool)Field(probe, "recoveryActive", typeof(bool)) && (int)Field(probe, "phase", typeof(int)) == 0 &&
                    !(bool)Field(probe, "titleQueued", typeof(bool)) && !(bool)Field(probe, "mutationAttempted", typeof(bool)), "adoption_owner_not_retired");
                CampaignIdle(probe, "campaign"); CampaignIdle(probe, "challenge"); CampaignIdle(probe, "elite");
                Idle(probe, "development", "Pending"); Idle(probe, "playerRefresh", "Pending"); Idle(probe, "dailyRefresh", "Pending");
                foreach (string field in new string[] { "dailyGuild", "dailyReward", "dailyTimed", "dailyStone", "dailyMingshi", "dailyArena", "dailyRefine", "dailyScript" }) Idle(probe, field, "Busy");
                FieldInfo wood = probe.GetType().GetField("dailyWood", Instance);
                if (wood != null && wood.GetValue(probe) != null) SoulRecoveryRequest.Require(!Flag(wood.GetValue(probe).GetType(), wood.GetValue(probe), "Busy", false), "adoption_wood_owner");
                object settlement = Field(probe, "battleSettlement", null);
                if (settlement != null) SoulRecoveryRequest.Require(!Flag(settlement.GetType(), settlement, "BlocksGuiInvocation", false), "adoption_settlement_owner");
                object soul = Field(probe, "dailySoul", null); if (soul == null) continue;
                CheckCapture(Field(soul, "response", null)); CheckCapture(Field(soul, "activity", null));
                string oldSession = (string)Field(probe, "nonce", typeof(string)), oldOperation = (string)Field(soul, "operation", typeof(string));
                bool unresolved = Flag(soul.GetType(), soul, "Unresolved", false);
                if (!unresolved || Granted(name, oldSession, Entry.GetCurrentProcessId(), creation) || SoulSourceSettled(oldSession, oldOperation)) continue;
                object origin = Field(soul, "origin", null);
                SoulRecoveryRequest.Require(authority.SourceSession == "" && (long)Field(probe, "creationFiletime", typeof(long)) == creation &&
                    origin != null && ((ulong)Field(origin, "Role", typeof(ulong))).ToString(CultureInfo.InvariantCulture) == context.CharacterId &&
                    (string)Field(soul, "status", typeof(string)) == "UNKNOWN" && (string)Field(soul, "queuedAction", typeof(string)) == "" &&
                    (bool)Field(soul, "startVerified", typeof(bool)) && !(bool)Field(soul, "growPending", typeof(bool)) &&
                    !(bool)Field(soul, "harvestInvoked", typeof(bool)) && !(bool)Field(soul, "harvestVerified", typeof(bool)) &&
                    (long)Field(soul, "startBoard", typeof(long)) == board && Command.ValidNonce(oldSession) && Command.ValidNonce(oldOperation), "adoption_old_soul_not_settled");
                authority.SourceSession = oldSession; authority.SourceOperation = oldOperation;
                authority.Mode = (int)Field(soul, "selectedMode", typeof(int));
                SoulRecoveryRequest.Require(authority.Mode == 1 || authority.Mode == 2, "adoption_old_mode");
                owners += (ownerCount++ == 0 ? "" : ",") + "{\"assembly\":" + D.Q(name) + ",\"session_id\":" + D.Q(oldSession) +
                    ",\"operation_id\":" + D.Q(oldOperation) + ",\"sequence\":" + (int)Field(soul, "sequence", typeof(int)) +
                    ",\"last_command_sequence\":" + (int)Field(life, "LastSequence", typeof(int)) + ",\"stage\":" + D.Q((string)Field(soul, "stage", typeof(string))) +
                    ",\"outcome\":\"UNKNOWN\",\"owner_retired\":true,\"callbacks_detached\":true,\"replay_allowed\":false}";
                }
            }
            IList messages = D.Read(context.Client, "messages") as IList, sends = D.Read(context.Client, "m_sendDatas") as IList;
            IDictionary handlers = D.Read(context.Client, "m_handlers") as IDictionary;
            SoulRecoveryRequest.Require(messages != null && sends != null && handlers != null && messages.Count == 0 && sends.Count == 0 &&
                !D.Bool(D.Read(context.Client, "m_bSending")) && handlers.Count <= 65536, "adoption_transport_not_idle");
            foreach (ushort opcode in new ushort[] { 517, 518, 519, 522, 523 }) SoulRecoveryRequest.Require(!handlers.Contains(opcode), "adoption_soul_handler");
            foreach (DictionaryEntry entry in handlers)
            {
                object handler = entry.Value; SoulRecoveryRequest.Require(handler != null, "adoption_handler_missing");
                for (Type type = handler.GetType(); type != null; type = type.BaseType)
                    foreach (FieldInfo field in type.GetFields(Instance))
                    {
                        if (!typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                        Delegate callback = field.GetValue(handler) as Delegate; if (callback == null) continue;
                        foreach (Delegate part in callback.GetInvocationList())
                        {
                            string owner = part.Method.DeclaringType == null ? "" : part.Method.DeclaringType.Assembly.GetName().Name;
                            if (!owner.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                            // The current read-only clock observer is allowed;
                            // retired or action callback wrappers are not.
                            object clock = Field(typeof(DailyRefreshProof), null, "clockCapture", null, true);
                            SoulRecoveryRequest.Require(clock != null && ReferenceEquals(Field(clock, "handler", null), handler) &&
                                ReferenceEquals(Field(clock, "wrapper", typeof(Delegate)), callback), "adoption_installed_app_callback");
                        }
                    }
            }
            authority.Witness = "{\"pid\":" + Entry.GetCurrentProcessId() + ",\"process_creation_filetime\":" + creation.ToString(CultureInfo.InvariantCulture) +
                ",\"character_id\":" + D.Q(context.CharacterId) + ",\"old_owners\":" + owners + "]" +
                ",\"callbacks_quiescent\":true,\"transport_quiescent\":true,\"messages\":0,\"send_queue\":0,\"sending\":false}";
            return authority;
        }
        internal static string SoulRefusal(string operation, string action, bool ownsAdoption)
        {
            if (authorizedPid == Entry.GetCurrentProcessId())
            {
                if (authorizedOldOperation == operation) return "same_process_recovery_old_operation_quarantined";
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                Type type = assembly.GetType("JiangShen.AssistantRuntime.ProcessRecovery", false); if (type == null) continue;
                if ((uint)Field(type, null, "authorizedPid", typeof(uint), true) != Entry.GetCurrentProcessId()) continue;
                if ((string)Field(type, null, "authorizedOldOperation", typeof(string), true) == operation) return "same_process_recovery_old_operation_quarantined";
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                foreach (object probe in Probes(assembly))
                {
                if (ReferenceEquals(probe, Entry.CurrentProbe)) continue;
                object soul = Field(probe, "dailySoul", null);
                if (soul != null && (string)Field(soul, "status", typeof(string)) == "UNKNOWN" &&
                    (string)Field(soul, "operation", typeof(string)) == operation) return "daily_soul_old_operation_quarantined";
                }
            }
            if (adoptedOperation != null && operation == adoptedOperation && !ownsAdoption) return "daily_soul_adoption_owner_changed";
            if (ownsAdoption && action != "grow" && action != "harvest" && action != "close") return "daily_soul_adoption_existing_board_only";
            return "";
        }
        internal static void OwnBoardAdoption(string operation, string witnessHash, string sourceSession, string sourceOperation)
        {
            adoptedOperation = operation; adoptionWitnessHash = witnessHash; adoptedSourceSession = sourceSession; adoptedSourceOperation = sourceOperation;
            BoardHarvestProgress(operation, false, false);
        }
        internal static void BoardHarvestProgress(string operation, bool harvestVerified, bool closeVerified)
        {
            if (operation != adoptedOperation) return;
            currentBoardHarvestJson = "{\"adopted_operation_id\":" + D.Q(operation) + ",\"harvest_verified\":" + D.B(harvestVerified) +
                ",\"close_verified\":" + D.B(closeVerified) + ",\"board_cleared\":" + D.B(harvestVerified) + ",\"resolution_sha256\":" + D.Q(adoptionWitnessHash) + "}";
            if (harvestVerified && closeVerified && adoptedSourceSession != "") completedSoulSources += "|" + adoptedSourceSession + "/" + adoptedSourceOperation + "|";
        }
        internal static void InheritGrant(uint pid, long creation)
        {
            if (authorizedJson != null) return;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.StartsWith("JSAssistantBridge_", StringComparison.Ordinal)) continue;
                Type type = assembly.GetType("JiangShen.AssistantRuntime.ProcessRecovery", false); if (type == null || type == typeof(ProcessRecovery)) continue;
                if ((uint)Field(type, null, "authorizedPid", typeof(uint), true) != pid || (long)Field(type, null, "authorizedCreation", typeof(long), true) != creation) continue;
                authorizedOldSession = (string)Field(type, null, "authorizedOldSession", typeof(string), true); authorizedOldOperation = (string)Field(type, null, "authorizedOldOperation", typeof(string), true);
                authorizedOldAssembly = (string)Field(type, null, "authorizedOldAssembly", typeof(string), true); authorizedHash = (string)Field(type, null, "authorizedHash", typeof(string), true);
                authorizedJson = (string)Field(type, null, "authorizedJson", typeof(string), true); authorizedSoulQuarantine = (bool)Field(type, null, "authorizedSoulQuarantine", typeof(bool), true);
                authorizedPid = pid; authorizedCreation = creation; return;
            }
        }
    }

    // Never polls a command or installs an app/game response callback.
    public sealed class SoulConnectionRecovery : MonoBehaviour
    {
        private SoulRecoveryRequest request;
        private string directory;
        private int nativeId, managedId, lastFrame = -1, started;
        private bool completed;
        private Model model;
        private Fresh first;
        private sealed class Fresh
        {
            internal D Context;
            internal DailyTransport Transport;
            internal int Frame;
            internal uint Cycle, Next;
            internal int Blue, Purple, Orange;
            internal long Board;
            internal string Json;
        }
        internal void Initialize(SoulRecoveryRequest r, string path, int native, int managed)
        { request = r; directory = path; nativeId = native; managedId = managed; started = Environment.TickCount; WriteStatus("WAITING", "await_distinct_update_frames"); }
        private void WriteStatus(string status, string reason)
        {
            ReceiptStore.WriteRecoveryStatus(directory, "{" + request.Fields() + ",\"status\":" + Command.Quote(status) + ",\"reason\":" + Command.Quote(reason) +
                ",\"native_main_thread_id\":" + nativeId + ",\"managed_main_thread_id\":" + managedId + ",\"first_frame\":" + (first == null ? -1 : first.Frame) + ",\"last_frame\":" + lastFrame + "}");
        }
        private Fresh Capture()
        {
            Snapshot common = model.Capture(); SoulRecoveryRequest.Require(common.Gate == "PASS" && common.Safe, "fresh_main_gate:" + common.Gate);
            D context = DevelopmentContextReader.Read(model, common, "PASS", false, true);
            SoulRecoveryRequest.Require(context.Gate == "PASS" && context.CharacterId == request.Role && context.SubUi == null && context.Panels() == "PASS", "fresh_role_or_panel:" + context.Gate);
            DailyTransport transport = DailyTransport.Read(context.Client); SoulRecoveryRequest.Require(transport != null, "fresh_transport");
            IList messages = D.Read(context.Client, "messages") as IList, sends = D.Read(context.Client, "m_sendDatas") as IList;
            IDictionary handlers = D.Read(context.Client, "m_handlers") as IDictionary;
            SoulRecoveryRequest.Require(messages != null && sends != null && handlers != null && messages.Count == 0 && sends.Count == 0 && !D.Bool(D.Read(context.Client, "m_bSending")), "fresh_transport_not_idle");
            foreach (ushort opcode in new ushort[] { 517, 518, 519, 522, 523 }) SoulRecoveryRequest.Require(!handlers.Contains(opcode), "fresh_soul_handler:" + opcode);
            SoulRecoveryRequest.Require(handlers.Count <= 65536, "handler_bound");
            foreach (DictionaryEntry entry in handlers)
            {
                object handler = entry.Value; SoulRecoveryRequest.Require(handler != null, "handler_missing");
                // All callback-bearing handler fields must have detached app
                // wrappers, including permanent activity/clock handlers.
                for (Type t = handler.GetType(); t != null; t = t.BaseType)
                    foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                        Delegate callback = field.GetValue(handler) as Delegate; if (callback == null) continue;
                        foreach (Delegate part in callback.GetInvocationList())
                        {
                            string assembly = part.Method.DeclaringType == null ? "" : part.Method.DeclaringType.Assembly.GetName().Name;
                            SoulRecoveryRequest.Require(!assembly.StartsWith("JSAssistantBridge_", StringComparison.Ordinal), "installed_app_callback");
                        }
                    }
            }
            uint now, day, cycle; string clock = DailyRefreshGate.ReadClock(context.Character, out now, out day, out cycle);
            SoulRecoveryRequest.Require(clock == "", "fresh_clock:" + clock);
            object board = D.Read(context.Character, "gather_souls"); SoulRecoveryRequest.Require(board != null && board.GetType() == Model.FirstType("Database.GatherSoul"), "fresh_board_schema");
            long packed = 0; string slots = "[";
            for (int i = 0; i < 8; i++)
            {
                object colorValue = D.Read(board, "soul" + i + "_color"), levelValue = D.Read(board, "soul" + i + "_level");
                SoulRecoveryRequest.Require(colorValue is byte && levelValue is byte, "fresh_board_slot_schema");
                int color = (byte)colorValue, level = (byte)levelValue; packed |= DailySoulWire.Slot(i, color, level);
                slots += (i == 0 ? "" : ",") + "{\"color\":" + color + ",\"level\":" + level + "}";
            }
            slots += "]";
            Fresh f = new Fresh(); f.Context = context; f.Transport = transport; f.Frame = Time.frameCount; f.Cycle = cycle;
            f.Next = (uint)D.Read(context.Character, "next_resource_time");
            f.Blue = (byte)D.Read(context.Character, "generalsoul_blue_times"); f.Purple = (byte)D.Read(context.Character, "generalsoul_purple_times"); f.Orange = (byte)D.Read(context.Character, "generalsoul_orange_times"); f.Board = packed;
            f.Json = "{\"frame\":" + f.Frame + ",\"observed_utc\":" + Command.Quote(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)) +
                ",\"native_main_thread_id\":" + nativeId + ",\"managed_main_thread_id\":" + managedId + ",\"character_id\":" + Command.Quote(context.CharacterId) +
                ",\"root_instance_id\":" + context.Root.GetInstanceID() + ",\"root_level_id\":" + Command.Quote(common.LevelId) +
                ",\"gate\":\"PASS\",\"transport_quiescent\":true,\"callbacks_quiescent\":true,\"messages\":0,\"send_queue\":0,\"sending\":false,\"soul_handlers_present\":false" +
                ",\"board\":" + slots + ",\"board_packed\":" + packed.ToString(CultureInfo.InvariantCulture) + ",\"cycle_id\":" + cycle + ",\"server_now\":" + now + ",\"next_resource_time\":" + f.Next +
                ",\"blue_used\":" + f.Blue + ",\"purple_used\":" + f.Purple + ",\"orange_used\":" + f.Orange + ",\"resources\":" + common.Resources + "}";
            SoulRecoveryRequest.Require(transport.StillCurrent() && context.SameIdentity(context) && messages.Count == 0 && sends.Count == 0 && !D.Bool(D.Read(context.Client, "m_bSending")), "fresh_capture_changed");
            return f;
        }
        private void Update()
        {
            if (completed || request == null) return;
            try
            {
                SoulRecoveryRequest.Require(Entry.GetCurrentThreadId() == nativeId && Thread.CurrentThread.ManagedThreadId == managedId, "update_thread");
                SoulRecoveryRequest.Require(unchecked((uint)(Environment.TickCount - started)) < 10000, "timeout");
                if (Time.frameCount == lastFrame) return;
                SoulRecoveryRequest.Require(lastFrame < Time.frameCount, "frame_regression"); lastFrame = Time.frameCount;
                ProcessRecovery.CheckAll(request, request.Pid, request.Creation);
                if (model == null) model = new Model(); Fresh current = Capture();
                if (first == null) { first = current; WriteStatus("WAITING", "await_second_stable_frame"); return; }
                SoulRecoveryRequest.Require(current.Frame > first.Frame && current.Context.SameIdentity(first.Context) && current.Transport.Same(first.Transport), "fresh_identity_changed");
                request.CheckFiles(directory); ProcessRecovery.CheckAll(request, request.Pid, request.Creation);
                bool quarantine = first.Board != 0 || current.Board != 0 || first.Cycle != current.Cycle || first.Next != current.Next ||
                    first.Blue != current.Blue || first.Purple != current.Purple || first.Orange != current.Orange;
                string json = "{" + request.Fields() + ",\"status\":\"RECONCILED\",\"reason\":\"\",\"scope\":\"connection_only\",\"old_outcome\":\"UNKNOWN\",\"replay_allowed\":false" +
                    ",\"old_operation_quarantined\":true,\"soul_mutations_quarantined\":" + D.B(quarantine) + ",\"old_owner_retired\":true,\"callbacks_quiescent\":true,\"transport_quiescent\":true" +
                    ",\"managed_assembly_name\":" + Command.Quote(typeof(Entry).Assembly.GetName().Name) + ",\"native_main_thread_id\":" + nativeId + ",\"managed_main_thread_id\":" + managedId +
                    ",\"first_frame\":" + first.Frame + ",\"last_frame\":" + current.Frame + ",\"samples\":[" + first.Json + "," + current.Json + "],\"fresh\":" + current.Json + "}";
                string hash = ReceiptStore.WriteRecoveryResolution(directory, json);
                ProcessRecovery.Grant(request, json, hash, quarantine);
                completed = true; enabled = false; ProcessRecovery.End(this);
                string result = Entry.ActivateRecovered(directory, request.Nonce, nativeId, request.Creation);
                SoulRecoveryRequest.Require(result.StartsWith("BOOTSTRAP_OK;", StringComparison.Ordinal), "normal_activation:" + result);
                WriteStatus("RECONCILED", ""); UObject.Destroy(gameObject);
            }
            catch (Exception error)
            {
                completed = true; enabled = false; ProcessRecovery.End(this);
                try { WriteStatus("REJECTED", error is ProbeFailure ? error.Message : "same_process_recovery_exception:" + error.GetType().FullName + ":" + error.Message); } catch { }
                UObject.Destroy(gameObject);
            }
        }
        private void OnDestroy() { ProcessRecovery.End(this); }
    }
}
