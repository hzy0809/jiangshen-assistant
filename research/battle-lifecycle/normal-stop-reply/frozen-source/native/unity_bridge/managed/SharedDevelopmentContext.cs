using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    internal delegate void DevelopmentPublication();
    internal delegate string DevelopmentOverlayGate(Behaviour ui);
    internal sealed class DevelopmentCommand
    {
        internal string Action = "", OperationId = "", Mode = "";
        internal int Sequence, ExpectedRevision, TargetId, DesiredLevel, MaxRequests;
        internal long MaxMoney, MaxStone, MaxFood, MaxPractice;
        internal string GoalKey()
        { return OperationId + ":" + TargetId + ":" + DesiredLevel + ":" + Mode + ":" + MaxRequests + ":" + MaxMoney + ":" + MaxStone + ":" + MaxFood + ":" + MaxPractice; }
        internal DevelopmentCommand Copy() { return (DevelopmentCommand)MemberwiseClone(); }
        internal bool ValidId()
        { if (OperationId == null || OperationId.Length != 32) return false; foreach (char c in OperationId) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; return true; }
    }
    // The controller supplies its already assessed overlay/loading/pause gate.
    // This context independently binds every fixed component to the global UI
    // root and every request to the current character and client. It has no wire
    // reflection/path parameter and never sends a packet.
    internal sealed class SharedDevelopmentContext
    {
        internal readonly UObject[] All;
        internal readonly Component Root, GlobalUi, UiRoot;
        internal readonly object Character, Client;
        internal readonly string CharacterId, Gate;
        internal readonly bool Paused;
        internal readonly DevelopmentNetwork Network;
        private readonly DevelopmentOverlayGate overlays;
        internal SharedDevelopmentContext(UObject[] all, Component root, Component globalUi, object character, string characterId, string externalGate, bool paused)
            : this(all, root, globalUi, character, characterId, externalGate, paused, null) { }
        internal SharedDevelopmentContext(UObject[] all, Component root, Component globalUi, object character, string characterId, string externalGate, bool paused, DevelopmentOverlayGate overlayGate)
        {
            All = all ?? new UObject[0]; Root = root; GlobalUi = globalUi; Character = character; CharacterId = characterId; Paused = paused; overlays = overlayGate;
            UiRoot = globalUi == null ? null : Read(globalUi, "rmg") as Component;
            Client = Static(Model.GameType("RootUI"), "m_client");
            Gate = paused ? "paused" : externalGate != "PASS" ? "controller_gate:" + externalGate : IdentityGate(true);
            Network = DevelopmentNetwork.Sample(Client);
        }
        private string IdentityGate(bool checkInput)
        {
            if (Root == null || UiRoot == null || GlobalUi == null || !Root.gameObject.activeInHierarchy || !UiRoot.gameObject.activeInHierarchy ||
                (Read(Root, "LevelID") as string != "Campaign" && Read(Root, "LevelID") as string != "Capital")) return "development_campaign_or_capital_root_required";
            if (Character == null || String.IsNullOrEmpty(CharacterId) || CharacterId == "0" || !object.ReferenceEquals(Character, Static(Model.FirstType("Global"), "character_")) || Convert.ToUInt64(Read(Character, "id"), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) != CharacterId || Bool(Read(Character, "gm"))) return "development_character_identity";
            object cached = Static(Model.GameType("BaseUI"), "rootUI");
            if (cached != null && !object.ReferenceEquals(cached, GlobalUi)) return "development_cached_root_identity";
            if (Client == null || !object.ReferenceEquals(Client, Static(Model.FirstType("Global"), "m_client"))) return "development_client_identity";
            if (checkInput && !Bool(Static(Model.FirstType("Global"), "extern_enabled"))) return "development_global_input_disabled";
            return "PASS";
        }
        internal bool SameIdentity(SharedDevelopmentContext before)
        {
            return Gate == "PASS" && SameOwnership(before) && IdentityGate(true) == "PASS";
        }
        internal string OwnershipGate() { return IdentityGate(false); }
        internal bool SameOwnership(SharedDevelopmentContext before)
        { return before != null && object.ReferenceEquals(Root, before.Root) && object.ReferenceEquals(UiRoot, before.UiRoot) && object.ReferenceEquals(GlobalUi, before.GlobalUi) && object.ReferenceEquals(Character, before.Character) && CharacterId == before.CharacterId && object.ReferenceEquals(Client, before.Client) && IdentityGate(false) == "PASS"; }
        internal Component Find(string name)
        {
            Type type = name == "SpiritUI" || name == "SpiritUI_3" || name == "SpiritRankupUI" || name == "BattleResultUI" || name == "ReputationUI" || name == "SpecialGetUI" || name == "MessageBox" || name == "TooltipUI" || name == "GuildUI_3" || name == "ConsumeUI" ? Model.FirstType(name) : Model.GameType(name); Component found = null; int count = 0;
            foreach (UObject item in All)
            {
                Component c = item as Component;
                if (c == null || c.GetType() != type || !object.ReferenceEquals(Read(c, "rmg"), UiRoot) || c.transform.root != UiRoot.transform || c.gameObject != GlobalUi.gameObject) continue;
                found = c; count++;
            }
            if (count != 1) throw new ProbeFailure("development_ui_uniqueness:" + name + ":" + count);
            return found;
        }
        internal Component GrowthRewardDialog()
        {
            // NewbieQuestUI uses __BaseUI.GetBattleResultUI(), whose cache is
            // hosted under Battle/BattleUI rather than the global UI object.
            // Read that exact existing cache without instantiating a dialog.
            Component reward = Static(Model.FirstType("__BaseUI"), "battle_result_ui") as Component;
            if (reward == null) return null;
            if (reward.GetType() != Model.FirstType("BattleResultUI")) throw new ProbeFailure("growth_reward_cache_type");
            int count = 0;
            foreach (UObject item in All) if (object.ReferenceEquals(item, reward)) count++;
            if (count != 1) throw new ProbeFailure("growth_reward_cache_not_observed");
            return reward;
        }
        internal object SubUi { get { return Static(Model.GameType("MainUI"), "m_subui"); } }
        internal string Overlay(Behaviour ui)
        { return overlays == null ? "overlay_assessor_required:" + ui.GetType().FullName : overlays(ui); }
        internal string Panels(params Component[] allowed)
        { return Panels(false, allowed); }
        internal Component FindCapitalDemon()
        { return CapitalDemon(All, Root, Character); }
        internal static Component CapitalDemon(UObject[] all, Component root, object character)
        {
            // This scene icon can open separate panels; those remain gated.
            if (root == null || character == null || !object.ReferenceEquals(Static(Model.FirstType("Global"), "character_"), character) ||
                (string)Read(root, "LevelID") != "Capital") return null;
            object demon = Read(character, "m_demon");
            if (demon == null || demon.GetType() != Model.FirstType("Database.Demon")) return null;
            GameObject mesh = Read(demon, "mesh") as GameObject;
            if (mesh == null || !mesh.activeInHierarchy || mesh.transform.root != root.transform) return null;
            Component script = mesh.GetComponent("DemonScript");
            if (script == null || script.GetType() != Model.GameType("DemonScript") ||
                Bool(Read(script, "m_bFriend")) || !object.ReferenceEquals(Read(script, "m_demon"), demon)) return null;
            Component parent = Read(script, "ParentUI") as Component;
            if (parent == null || parent.GetType() != Model.GameType("CapitalUI") ||
                parent.transform.root != root.transform || !object.ReferenceEquals(Read(parent, "rmg"), root)) return null;
            int count = 0;
            foreach (UObject item in all)
            {
                Component candidate = item as Component;
                if (candidate != null && candidate.GetType() == script.GetType() && candidate.gameObject == mesh) count++;
            }
            if (count != 1) throw new ProbeFailure("flower_capital_demon_identity");
            return script;
        }
        internal string RewardPanels(params Component[] allowed)
        { return Panels(true, allowed); }
        private string Panels(bool rewardConfirmation, Component[] allowed)
        {
            if (Gate != "PASS" && !(rewardConfirmation &&
                (Gate == "controller_gate:global_input_disabled" || Gate == "development_global_input_disabled") && IdentityGate(false) == "PASS")) return Gate;
            foreach (UObject item in All)
            {
                Behaviour b = item as Behaviour; if (b == null || !Visible(b)) continue;
                bool ok = b == Root || b == UiRoot || b == GlobalUi || b.GetType() == Model.GameType("MainUI");
                foreach (Component a in allowed) if (b == a) ok = true;
                if (ok) continue;
                // The controller's fixed ambient/chrome assessor has the
                // original ownership checks. Missing assessor fails closed.
                string why = Overlay(b);
                if (why != null) return "development_overlay:" + why;
            }
            return "PASS";
        }
        internal static object Template(string name, uint id)
        { return Method(Model.FirstType("Global"), "getTemplate", new Type[] { typeof(string), typeof(uint) }, true, typeof(object)).Invoke(null, new object[] { name, id }); }
        internal static object Read(object target, string name)
        {
            if (target == null) throw new ProbeFailure("development_null:" + name);
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            { FieldInfo f = t.GetField(name, Instance); if (f != null) return f.GetValue(target); }
            throw new ProbeFailure("development_field_schema:" + name);
        }
        internal static void Selection(object target, string name, object value)
        {
            // Call sites are fixed normal-button branches only, never business
            // data/quest flags. Type equality avoids conversion-based writes.
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            { FieldInfo f = t.GetField(name, Instance); if (f != null && f.FieldType == value.GetType()) { f.SetValue(target, value); return; } }
            throw new ProbeFailure("development_selection_schema:" + name);
        }
        internal static object Static(Type type, string name)
        { FieldInfo f = type.GetField(name, StaticFlags); if (f == null) throw new ProbeFailure("development_static_schema:" + name); return f.GetValue(null); }
        internal static MethodInfo Method(Type type, string name, Type[] args, bool isStatic, Type result)
        {
            for (Type t = type; t != null; t = t.BaseType)
            { MethodInfo m = t.GetMethod(name, isStatic ? StaticFlags : Instance, null, args, null); if (m != null && !m.IsGenericMethod && m.ReturnType == result) return m; }
            throw new ProbeFailure("development_method_schema:" + name);
        }
        internal static void Call(object target, string name)
        { Method(target.GetType(), name, Type.EmptyTypes, false, typeof(void)).Invoke(target, new object[0]); }
        internal static void MainBranch(string name)
        { Method(Model.GameType("MainUI"), name, Type.EmptyTypes, true, typeof(void)).Invoke(null, new object[0]); }
        internal static bool Visible(Component c)
        { Behaviour b = c as Behaviour; return b != null && b.enabled && b.gameObject.activeInHierarchy && Bool(Method(c.GetType(), "IsVisible", Type.EmptyTypes, false, typeof(bool)).Invoke(c, new object[0])); }
        internal static bool Enabled(Component c)
        { return Visible(c) && Bool(Method(c.GetType(), "IsEnable", Type.EmptyTypes, false, typeof(bool)).Invoke(c, new object[0])); }
        internal static bool Loading(Component c)
        { return Bool(Read(c, "loading")) || Bool(Read(c, "collecting")); }
        internal static bool Gui { get { return Event.current != null && GUI.enabled; } }
        internal static int Int(object v) { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        internal static uint UInt(object v) { return Convert.ToUInt32(v, CultureInfo.InvariantCulture); }
        internal static long Long(object v) { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
        // Character.get_money (060003fa) is the pinned pure expression
        // (long)_character.money + _character.money64. Read its two fields;
        // inherited money alone is not the normal UI's spendable balance.
        internal static long Money(object character)
        { return checked(Long(Read(character, "money")) + Long(Read(character, "money64"))); }
        internal static bool Bool(object v) { if (!(v is bool)) throw new ProbeFailure("development_bool_schema"); return (bool)v; }
        internal static string Q(string v) { return Command.Quote(v ?? ""); }
        internal static string B(bool v) { return v ? "true" : "false"; }
        internal static string GateKind(string gate)
        {
            // Normal UI loading temporarily blocks controller input. Preserve
            // the raw context gate (and therefore every execution refusal),
            // while describing these two known capture states as waitable.
            if (gate == "controller_gate:loading_or_battle" || gate == "controller_gate:global_input_disabled") return "WAITING_UI";
            return gate == "PASS" || gate == "BLOCKED" || gate == "WAITING_UI" || gate == "WAITING_NETWORK" || gate == "WAITING" || gate == "UNKNOWN" || gate == "ERROR" ? gate : "BLOCKED";
        }
        internal static string GateReason(string gate, string reason)
        { return GateKind(gate) == gate ? reason : gate; }
        internal static string UiJson(Component ui)
        { return "{\"instance_id\":" + (ui == null ? 0 : ui.GetInstanceID()) + ",\"visible\":" + B(Visible(ui)) + ",\"enabled\":" + B(Enabled(ui)) + "}"; }
        internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        internal const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    }

    internal sealed class DevelopmentPacket
    {
        internal object Token;
        internal int Opcode;
        internal uint Result, Id, Level, Experience, Food, Gold, ExpGet, Price, Practice;
        internal string Error;
        // Detached, bounded protobuf scalar reader. No game serializer or game
        // assembly is loaded/called by this parser. Unknown fields are skipped.
        internal static DevelopmentPacket Decode(int opcode, byte[] bytes)
        {
            DevelopmentPacket p = new DevelopmentPacket { Opcode = opcode };
            if (bytes == null || bytes.Length > 65536) { p.Error = "packet_bound"; return p; }
            try
            {
                int pos = 0, seen = 0, nested = 0; uint[] values = new uint[10];
                while (pos < bytes.Length)
                {
                    ulong tag = Varint(bytes, ref pos); int field = checked((int)(tag >> 3)), wire = (int)(tag & 7);
                    if (field < 1) throw new FormatException();
                    if (wire == 0)
                    {
                        ulong value = Varint(bytes, ref pos);
                        if (field < values.Length) { if ((seen & (1 << field)) != 0 || value > UInt32.MaxValue) throw new FormatException(); seen |= 1 << field; values[field] = (uint)value; }
                    }
                    else if (wire == 1) pos = checked(pos + 8);
                    else if (wire == 2) { int length = checked((int)Varint(bytes, ref pos)); if (field < 10) nested |= 1 << field; pos = checked(pos + length); }
                    else if (wire == 5) pos = checked(pos + 4);
                    else throw new FormatException();
                    if (pos > bytes.Length) throw new FormatException();
                }
                if (opcode == 514) { if ((seen & 6) != 6) throw new FormatException(); p.Level = values[1]; p.Practice = values[2]; return p; }
                if (opcode == 810) { if (bytes.Length != 0) throw new FormatException(); return p; }
                if (opcode == 305)
                { if ((seen & 510) != 510 || values[1] > 1 || values[8] > 1) throw new FormatException(); p.Result = values[1]; p.Id = values[3]; p.Level = values[2]; p.Food = values[8]; return p; }
                if (opcode == 298) { if ((seen & 2) == 0 || values[1] > 1) throw new FormatException(); p.Result = values[1]; return p; }
                // Soul responses carry nested status/exchange/result messages,
                // not a shared result enum. Keep that distinction explicit.
                if (opcode == 517)
                { if ((nested & 6) != 6 || (seen & 56) != 56 || values[3] != 1) throw new FormatException(); p.Id = values[3]; p.Level = values[4]; p.Result = values[5]; return p; }
                if (opcode == 519)
                { if ((nested & 4) == 0 || (seen & 16) == 0 || values[4] != 1) throw new FormatException(); p.Id = values[4]; return p; }
                if (opcode == 518) { if ((nested & 2) == 0) throw new FormatException(); return p; }
                if ((seen & 2) == 0) throw new FormatException(); p.Result = values[1];
                if (opcode == 811 || opcode == 818) { if (p.Result > 1) throw new FormatException(); }
                else if (opcode == 863) { if (p.Result > 1) throw new FormatException(); p.Id = values[2]; }
                else if (opcode == 1104)
                { if ((seen & 30) != 30) throw new FormatException(); p.Id = values[2]; p.Level = values[3]; p.Price = values[4]; }
                else if (opcode == 1136) { p.Food = values[2]; p.Gold = values[3]; p.Experience = values[4]; p.Level = values[5]; p.ExpGet = values[6]; }
                else if (opcode == 1139) { p.Level = values[2]; p.Experience = values[3]; }
                else throw new FormatException();
            }
            catch (Exception) { p.Error = "packet_schema"; }
            return p;
        }
        private static ulong Varint(byte[] bytes, ref int pos)
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            { if (pos >= bytes.Length) throw new FormatException(); byte b = bytes[pos++]; if (shift == 63 && b > 1) throw new FormatException(); value |= (ulong)(b & 127) << shift; if ((b & 128) == 0) return value; }
            throw new FormatException();
        }
    }
    internal sealed class DevelopmentNetwork
    {
        internal bool Observed;
        internal string Error;
        internal readonly Hashtable Handlers = new Hashtable();
        internal readonly ArrayList Packets = new ArrayList();
        internal bool Idle { get { return Observed && Error == null && Handlers.Count == 0 && Packets.Count == 0; } }
        internal static bool Tracked(int op) { return op == 514 || op == 305 || op == 298 || op == 517 || op == 518 || op == 519 || op == 818 || op == 863 || op == 810 || op == 811 || op == 1104 || op == 1136 || op == 1139; }
        internal static DevelopmentNetwork Sample(object client)
        {
            DevelopmentNetwork n = new DevelopmentNetwork(); if (client == null || !Monitor.TryEnter(client, 0)) return n;
            try
            {
                IDictionary handlers = SharedDevelopmentContext.Read(client, "m_handlers") as IDictionary;
                IList messages = SharedDevelopmentContext.Read(client, "messages") as IList;
                if (handlers == null || messages == null || messages.Count > 64) { n.Error = "network_schema_or_bound"; return n; }
                foreach (int op in new int[] { 514, 305, 298, 517, 518, 519, 818, 863, 810, 811, 1104, 1136, 1139 }) if (handlers.Contains((ushort)op)) n.Handlers[(ushort)op] = handlers[(ushort)op];
                for (int i = 0; i < messages.Count; i++)
                {
                    object message = messages[i]; int op = SharedDevelopmentContext.Int(SharedDevelopmentContext.Read(message, "op")); if (!Tracked(op)) continue;
                    MemoryStream stream = SharedDevelopmentContext.Read(message, "stream") as MemoryStream;
                    if (n.Packets.Count >= 8 || stream == null || !stream.CanRead || stream.Position != 0 || stream.Length > 65536) { n.Error = "network_stream_or_bound"; return n; }
                    DevelopmentPacket packet = DevelopmentPacket.Decode(op, stream.ToArray()); packet.Token = message; n.Packets.Add(packet);
                }
                n.Observed = true;
            }
            catch (Exception) { n.Error = "network_copy_failed"; }
            finally { Monitor.Exit(client); }
            return n;
        }
    }
    internal sealed class DevelopmentOperation
    {
        internal string Status = "IDLE", Server = "UNVERIFIED", Reason = "not_invoked", Action = "", OperationId = "", Mode = "";
        internal bool MutationAttempted;
        internal bool ServerProgressObserved;
        internal int Requests, Opcode, Sequence, ExpectedRevision, TargetId, DesiredLevel;
        internal DevelopmentPacket Receipt;
        internal object ExpectedArgument;
        private SharedDevelopmentContext before;
        private object handler;
        private DevelopmentCallbackCapture capture;
        private readonly Hashtable used = new Hashtable();
        internal bool Pending { get { return Status == "WAITING" || Status == "UNKNOWN"; } }
        internal bool Begin(SharedDevelopmentContext ctx, DevelopmentCommand command, int opcode, int budget, DevelopmentPublication publish, out string why)
        {
            int sequence = command.Sequence;
            why = "";
            if (sequence <= 0 || used.Contains(sequence) || Pending || !ctx.Network.Idle || ctx.Gate != "PASS" || (opcode != 0 && Requests >= budget))
            { why = "development_operation_unavailable"; return false; }
            DetachCapture();
            used[sequence] = true; before = ctx; Action = command.Action; Opcode = opcode; Sequence = sequence; OperationId = command.OperationId;
            ExpectedRevision = command.ExpectedRevision; TargetId = command.TargetId; DesiredLevel = command.DesiredLevel; Mode = command.Mode;
            if (opcode != 0) { Receipt = null; ServerProgressObserved = false; } handler = null;
            Status = "WAITING"; Server = "UNVERIFIED"; Reason = "published_before_invocation";
            // Publication is before *every* normal UI side effect, including
            // opening NewbieQuestUI (whose Show may itself send 810).
            MutationAttempted = true; if (opcode != 0) Requests++; publish(); return true;
        }
        internal void Invoked(SharedDevelopmentContext ctx, object receiver, string callback) { Invoked(ctx, receiver, callback, null); }
        internal void Invoked(SharedDevelopmentContext ctx, object receiver, string callback, IList combinedIds)
        {
            if (Opcode == 0) { Status = "OBSERVED"; Reason = "normal_ui_branch_invoked"; return; }
            DevelopmentNetwork n = DevelopmentNetwork.Sample(ctx.Client);
            if (!n.Observed || n.Error != null || !n.Handlers.Contains((ushort)Opcode)) { Unknown("own_request_handler_not_observed"); return; }
            handler = n.Handlers[(ushort)Opcode];
            Delegate original;
            try
            {
                Delegate d = SharedDevelopmentContext.Read(handler, "handler") as Delegate; object arg = SharedDevelopmentContext.Read(handler, "arg");
                if (d == null || d.GetInvocationList().Length != 1 || !object.ReferenceEquals(d.Target, receiver) || d.Method.DeclaringType != receiver.GetType() || d.Method.Name != callback || d.Method.ReturnType != typeof(void)) throw new FormatException();
                ParameterInfo[] parameters = d.Method.GetParameters();
                if (parameters.Length != 2 || parameters[0].ParameterType != typeof(MemoryStream) || parameters[1].ParameterType != typeof(object)) throw new FormatException();
                if (Opcode == 810)
                {
                    IList ids = arg as IList;
                    if (combinedIds == null ? ids == null || ids.Count != 1 || SharedDevelopmentContext.Int(ids[0]) != TargetId : !object.ReferenceEquals(ids, combinedIds) || ids.Count < 1 || ids.Count > 128) throw new FormatException();
                }
                else if (Opcode == 811) { if (!(arg is ushort) || (ushort)arg != TargetId) throw new FormatException(); }
                else if (Opcode == 517) { if (!(arg is uint) || (uint)arg != 1u) throw new FormatException(); }
                else if (Opcode == 863) { if (!(arg is ushort) || (ushort)arg != 0) throw new FormatException(); }
                else if (Opcode == 305) { if (ExpectedArgument == null || !object.ReferenceEquals(arg, ExpectedArgument) || SharedDevelopmentContext.Int(SharedDevelopmentContext.Read(SharedDevelopmentContext.Read(arg, "temp"), "id")) != 71) throw new FormatException(); }
                else if (Opcode == 298) { if (!(arg is bool) || !(bool)arg) throw new FormatException(); }
                else if (arg != null) throw new FormatException();
                original = d;
            }
            catch (Exception) { Unknown("own_request_callback_or_argument_unbound"); return; }
            string why; capture = DevelopmentCallbackCapture.Install(ctx.Client, handler, Opcode, OperationId, Sequence, original, out why);
            if (capture == null) { Unknown(why); return; }
            Reason = "own_request_callback_capture_installed";
        }
        internal bool InvokeNormal(SharedDevelopmentContext ctx, object receiver, string callback, DevelopmentPublication normal, out string why)
        {
            why = "";
            if (!Monitor.TryEnter(ctx.Client, 0)) { Unknown("client_busy_before_normal_request"); why = Reason; return false; }
            try
            {
                if (!ctx.SameIdentity(before) || !DevelopmentNetwork.Sample(ctx.Client).Idle) { Unknown("normal_request_baseline_changed"); why = Reason; return false; }
                // Send.handler_add and Send itself use this same monitor. Keep
                // it through exact handler binding, before Parse/Run can race.
                normal(); Invoked(ctx, receiver, callback); why = Status == "UNKNOWN" ? Reason : ""; return Status == "WAITING";
            }
            finally { Monitor.Exit(ctx.Client); }
        }
        internal void UiObservedWithoutRequest()
        { if (Status != "WAITING") return; Opcode = 0; Status = "OBSERVED"; Server = "UNVERIFIED"; Reason = "normal_open_observed_without_completion_request"; }
        internal void Observe(SharedDevelopmentContext ctx)
        {
            if (Status == "UNKNOWN" || Status == "IDLE" || Opcode == 0) return;
            if (!ctx.SameOwnership(before)) { Unknown("operation_identity_changed"); return; }
            if (capture != null)
            {
                DevelopmentCallbackWitness witness = capture.Read();
                if (capture.OperationId != OperationId || capture.Sequence != Sequence || capture.Opcode != Opcode) { Unknown("normal_callback_operation_identity_changed"); return; }
                if (witness.Packet != null) Receipt = witness.Packet; // literal evidence survives an original-callback failure
                if (witness.Error != null || witness.OriginalThrew) { Unknown(witness.Error ?? "normal_callback_threw"); return; }
                if (witness.Calls > 0)
                {
                    if (witness.NullResponse) { Unknown("normal_callback_null_response"); return; }
                    if (!witness.Returned) return;
                    if (witness.Calls != 1 || witness.Packet == null) { Unknown("normal_callback_receipt_unavailable"); return; }
                    Receipt = witness.Packet;
                    if (Opcode != 514 && Opcode != 810 && Opcode != 1104 && Receipt.Result != (Opcode == 517 || Opcode == 305 || Opcode == 298 ? 1u : 0u)) { Status = "ERROR"; Server = "FAIL"; Reason = "server_result_error"; return; }
                    Status = "OBSERVED"; Server = Opcode == 1104 ? "UNVERIFIED" : "PASS";
                    Reason = Opcode == 1104 ? "normal_callback_returned_result_semantics_unverified" : "own_normal_callback_captured_and_returned";
                    return;
                }
                if (!witness.Armed) { Unknown("normal_callback_capture_disarmed"); return; }
            }
            if (Status != "WAITING") return;
            DevelopmentNetwork n = ctx.Network; if (!n.Observed) return;
            if (n.Error != null) { Unknown(n.Error); return; }
            if (!n.Handlers.Contains((ushort)Opcode)) { Unknown("response_not_captured_before_normal_handler"); return; }
            if (!object.ReferenceEquals(n.Handlers[(ushort)Opcode], handler)) { Unknown("operation_handler_replaced"); return; }
            if (capture == null || !capture.StillInstalled()) { Unknown("operation_callback_replaced_or_unobserved"); return; }
        }
        private string DetachCapture() { if (capture == null) return ""; string result = capture.DisarmAndRestore(); capture = null; return result; }
        internal void Unknown(string why) { Status = "UNKNOWN"; Server = "UNVERIFIED"; string cleanup = DetachCapture(); Reason = why + (cleanup.EndsWith("forward_only", StringComparison.Ordinal) ? ":" + cleanup : ""); }
        internal void Stop(string reason) { if (Status == "WAITING") Unknown(reason); else DetachCapture(); }
        internal string Json()
        {
            return "{\"status\":" + SharedDevelopmentContext.Q(Status) + ",\"action\":" + SharedDevelopmentContext.Q(Action) + ",\"operation_id\":" + SharedDevelopmentContext.Q(OperationId) + ",\"sequence\":" + Sequence + ",\"expected_revision\":" + ExpectedRevision + ",\"target_id\":" + TargetId + ",\"desired_level\":" + DesiredLevel + ",\"mode\":" + SharedDevelopmentContext.Q(Mode) + ",\"opcode\":" + Opcode + ",\"server\":" + SharedDevelopmentContext.Q(Server) + ",\"reason\":" + SharedDevelopmentContext.Q(Reason) + ",\"requests\":" + Requests + ",\"mutation_attempted\":" + SharedDevelopmentContext.B(MutationAttempted) +
                ",\"receipt_result\":" + (Receipt == null || Receipt.Opcode == 514 || Receipt.Opcode == 518 || Receipt.Opcode == 519 ? "null" : Receipt.Result.ToString(CultureInfo.InvariantCulture)) + ",\"receipt_opcode\":" + (Receipt == null ? 0 : Receipt.Opcode) + ",\"receipt_id\":" + (Receipt == null ? 0 : Receipt.Id) + ",\"receipt_level\":" + (Receipt == null ? 0 : Receipt.Level) + ",\"receipt_experience\":" + (Receipt == null ? 0 : Receipt.Experience) + ",\"receipt_food\":" + (Receipt == null ? 0 : Receipt.Food) + ",\"receipt_gold\":" + (Receipt == null ? 0 : Receipt.Gold) + (Opcode == 514 ? ",\"receipt_practice\":" + (Receipt == null ? 0 : Receipt.Practice) : "") + ",\"server_progress_observed\":" + SharedDevelopmentContext.B(ServerProgressObserved) + ",\"result_semantics\":" + SharedDevelopmentContext.Q(Receipt == null || Receipt.Opcode == 1104 || Receipt.Opcode == 514 || Receipt.Opcode == 518 || Receipt.Opcode == 519 ? "UNVERIFIED" : "CONFIRMED") + "}";
        }
    }
    internal sealed class DevelopmentSession
    {
        internal DevelopmentCommand Goal, Queued;
        internal SharedDevelopmentContext QueuedContext;
        internal object Anchor;
        internal string Stamp;
        private int lastSequence;
        private readonly Hashtable usedOperations = new Hashtable();
        internal bool Queue(DevelopmentCommand command, SharedDevelopmentContext ctx, object anchor, string stamp, out string why)
        {
            why = "";
            if (command == null || !command.ValidId() || command.Sequence <= lastSequence || command.ExpectedRevision < 1 || Queued != null)
            { why = "development_command_identity_or_pending"; return false; }
            if (Goal != null && Goal.OperationId == command.OperationId ? Goal.GoalKey() != command.GoalKey() : usedOperations.Contains(command.OperationId))
            { why = "development_operation_goal_changed_or_replayed"; return false; }
            Queued = command.Copy(); QueuedContext = ctx; Anchor = anchor; Stamp = stamp; return true;
        }
        internal bool Take(DevelopmentCommand command, SharedDevelopmentContext ctx, out object anchor, out string stamp, out string why)
        {
            anchor = Anchor; stamp = Stamp; why = ""; DevelopmentCommand pending = Queued;
            Queued = null; Anchor = null; Stamp = null;
            if (pending == null || command == null || pending.Sequence != command.Sequence || pending.Action != command.Action || pending.GoalKey() != command.GoalKey() ||
                pending.ExpectedRevision != command.ExpectedRevision || !ctx.SameIdentity(QueuedContext) || !SharedDevelopmentContext.Gui)
            { why = "development_gui_context_or_command_changed"; return false; }
            return true;
        }
        internal void Commit(DevelopmentCommand command)
        { Goal = command.Copy(); usedOperations[command.OperationId] = true; lastSequence = command.Sequence; }
        internal void Stop() { Queued = null; Anchor = null; Stamp = null; }
    }
}


namespace JiangShen.AssistantRuntime
{
    internal delegate void DevelopmentNormalCallback(MemoryStream stream, object arg);
    internal sealed class DevelopmentCallbackWitness
    {
        internal int Calls;
        internal bool Returned, CallbackReturned, NullResponse, OriginalThrew, Armed;
        internal DevelopmentPacket Packet;
        internal byte[] RawBytes;
        internal string Error;
    }
    // Fixed own-handler callback capture, verified first as a detached prototype.
    internal sealed class DevelopmentCallbackCapture
    {
        private const ushort DailyRewardOpcode = 0x164;
        internal readonly string OperationId;
        internal readonly int Sequence, Opcode;
        private readonly object client, handler, expectedArg, stateLock = new object();
        private readonly FieldInfo callbackField;
        private readonly Delegate original, wrapper;
        private readonly DevelopmentNormalCallback forward;
        private readonly int threadId;
        private readonly bool raw16;
        private readonly bool timed, persistent;
        private readonly Action<byte[]> guildBody;
        private readonly int maximumBytes;
        private static int executing;
        private volatile bool armed = true;
        private int calls;
        private uint returnedDispatches;
        private bool returned, nullResponse, originalThrew;
        private DevelopmentPacket packet;
        private byte[] rawBytes;
        private string error;
        private DevelopmentCallbackCapture(object clientObject, object ownHandler, int opcode, string id, int sequence, Delegate originalDelegate, FieldInfo field, bool raw, Action<byte[]> guildObserver, bool timedCapture)
        {
            client = clientObject; handler = ownHandler; Opcode = opcode; OperationId = id; Sequence = sequence; original = originalDelegate; callbackField = field;
            expectedArg = SharedDevelopmentContext.Read(handler, "arg"); threadId = System.Threading.Thread.CurrentThread.ManagedThreadId; raw16 = raw; guildBody = guildObserver; maximumBytes = raw16 ? 1048576 : 65536;
            timed = timedCapture; persistent = opcode == 355 || timed && opcode == 322;
            forward = (DevelopmentNormalCallback)(original.Method.IsStatic ? Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Method) : Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Target, original.Method));
            wrapper = Delegate.CreateDelegate(original.GetType(), this, GetType().GetMethod("OnResponse", BindingFlags.NonPublic | BindingFlags.Instance));
        }
        internal static DevelopmentCallbackCapture Install(object client, object handler, int opcode, string id, int sequence, Delegate original, out string why)
        { return InstallCore(client, handler, opcode, id, sequence, original, false, null, out why); }
        internal static DevelopmentCallbackCapture InstallRaw16(object client, object handler, string id, int sequence, Delegate original, out string why)
        { return InstallCore(client, handler, 16, id, sequence, original, true, null, out why); }
        internal static DevelopmentCallbackCapture InstallDailyRaw16(object client, object handler, string id, int sequence, Delegate original, Action<byte[]> observe, out string why)
        { return InstallCore(client, handler, 16, id, sequence, original, true, observe, out why); }
        internal static DevelopmentCallbackCapture InstallDailyClock36(object client, object handler, Delegate original, Action<byte[]> observe, out string why)
        { return InstallCore(client, handler, 36, "00000000000000000000000000000024", 1, original, false, observe, out why); }
        internal static DevelopmentCallbackCapture InstallGuild(object client, object handler, int opcode, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            if (read == null || opcode != 301 && opcode != 302 && opcode != 355) { why = "guild_callback_opcode"; return null; }
            return InstallCore(client, handler, opcode, id, sequence, original, false, read, out why);
        }
        internal static DevelopmentCallbackCapture InstallArenaChest(object client, object handler, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            if (read == null) { why = "arena_chest_callback_observer"; return null; }
            return InstallCore(client, handler, 1122, id, sequence, original, false, read, out why);
        }
        internal static DevelopmentCallbackCapture InstallDailyReward(object client, object handler, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            Type activity = Model.GameType("ActivityUI");
            if (read == null || original == null || original.Target == null || original.Target.GetType() != activity ||
                original.Method.DeclaringType != activity || original.Method.Name != "sc_get_reward" || !original.Method.IsPrivate)
            { why = "daily_reward_callback_identity"; return null; }
            return InstallCore(client, handler, DailyRewardOpcode, id, sequence, original, false, read, out why);
        }
        internal static DevelopmentCallbackCapture InstallDailyStone(object client, object handler, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            Type panel = Model.FirstType("EnchantUI");
            if (read == null || original == null || original.Target == null || original.Target.GetType() != panel ||
                original.Method.DeclaringType != panel || original.Method.Name != "sc_enchant_dig_sliver" || !original.Method.IsPublic)
            { why = "daily_stone_callback_identity"; return null; }
            return InstallCore(client, handler, 825, id, sequence, original, false, read, out why, true);
        }
        internal static DevelopmentCallbackCapture InstallDailyMingshi(object client, object handler, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            Type panel = Model.GameType("DivinityUI");
            if (read == null || original == null || original.Target == null || original.Target.GetType() != panel ||
                original.Method.DeclaringType != panel || original.Method.Name != "sc_god_harvest_0x4B2" || !original.Method.IsPrivate)
            { why = "daily_mingshi_callback_identity"; return null; }
            return InstallCore(client, handler, 1202, id, sequence, original, false, read, out why, true);
        }
        internal static DevelopmentCallbackCapture InstallDailySoul(object client, object handler, int opcode, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            why = "daily_soul_callback_identity";
            if (opcode != 517 && opcode != 518 && opcode != 519 || handler == null || read == null || original == null || original.Target == null) return null;
            Type panel = Model.GameType("GeneralSoulGatherUI");
            string method = opcode == 517 ? "sc_refresh_gather_soul" : opcode == 518 ? "sc_harvest_soul" : "sc_grow_soul";
            if (original.Target.GetType() != panel || original.Method.DeclaringType != panel ||
                original.Method.Name != method || !original.Method.IsPrivate || original.Method.IsStatic) return null;
            // DailySoulModel.Bind has already checked the selected mode under
            // the client monitor. Pin that exact boxed stock arg; do not box a
            // replacement or reduce ordinary purple to the old blue-only value.
            object stockArgument = SharedDevelopmentContext.Read(handler, "arg");
            if (opcode == 517 ? !(stockArgument is uint) || (uint)stockArgument < 1u || (uint)stockArgument > 2u : stockArgument != null)
            { why = "daily_soul_callback_argument"; return null; }
            return InstallCore(client, handler, opcode, id, sequence, original, false, read, out why, true, stockArgument);
        }
        internal static DevelopmentCallbackCapture InstallDailyRefine(object client, object handler, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            Type panel = Model.FirstType("ArmyStarRefineUI");
            if (read == null || original == null || original.Target == null || original.Target.GetType() != panel ||
                original.Method.DeclaringType != panel || !original.Method.IsPublic || original.Method.IsStatic ||
                original.Method.Name != "sc_practice_money_exchange" && original.Method.Name != "sc_practice_exchange")
            { why = "daily_refine_callback_identity"; return null; }
            return InstallCore(client, handler, original.Method.Name == "sc_practice_exchange" ? 860 : 991,
                id, sequence, original, false, read, out why, true);
        }
        internal static DevelopmentCallbackCapture InstallDailyActivityPush(object client, object handler, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            if (!IsDailyActivityPush(handler, original) || read == null)
            { why = "daily_activity_callback_identity"; return null; }
            return InstallCore(client, handler, 355, id, sequence, original, false, read, out why, true);
        }
        internal static bool IsDailyActivityPush(object handler, Delegate original)
        {
            Type root = Model.FirstType("RootBase");
            return handler != null && handler.GetType() == Model.FirstType("game.net.tcp_base+Handler") &&
                original != null && original.Target == null && original.GetInvocationList().Length == 1 &&
                original.Method.IsStatic && original.Method.DeclaringType == root && original.Method.Name == "handelActvity" &&
                SharedDevelopmentContext.Read(handler, "arg") == null && (float)SharedDevelopmentContext.Read(handler, "life") == -1f;
        }
        internal static DevelopmentCallbackCapture InstallDailyTimed(object client, object handler, int opcode, string id, int sequence, Delegate original, Action<byte[]> read, out string why)
        {
            why = "daily_timed_callback_identity";
            if (read == null || original == null) return null;
            string method, type;
            switch (opcode)
            {
                case 312: method = "sc_startquiz"; type = "QuizUI"; break;
                case 323: method = "sc_refresh"; type = "BrawlUI"; break;
                case 1829: method = "sc_quizQuickAnwser"; type = "QuizUI"; break;
                case 322: method = "handleBrawlJoinName"; type = "RootBase"; break;
                case 355: method = "handelActvity"; type = "RootBase"; break;
                default: return null;
            }
            bool push = opcode == 322 || opcode == 355;
            Type declaring = opcode == 323 ? Model.GameType(type) : Model.FirstType(type);
            if (original.Method.Name != method || original.Method.DeclaringType != declaring ||
                (push ? !original.Method.IsStatic || original.Target != null : original.Method.IsStatic || original.Target == null || original.Target.GetType() != declaring)) return null;
            return InstallCore(client, handler, opcode, id, sequence, original, false, read, out why, true);
        }
        internal static bool AnyCallbackExecuting { get { return System.Threading.Interlocked.CompareExchange(ref executing, 0, 0) != 0; } }
        private static DevelopmentCallbackCapture InstallCore(object client, object handler, int opcode, string id, int sequence, Delegate original, bool raw, Action<byte[]> guildObserver, out string why, bool timed = false, object expectedTimedArgument = null)
        {
            why = "";
            if (client == null || handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") || (raw ? opcode != 16 : guildObserver == null && !DevelopmentNetwork.Tracked(opcode)) || !new DevelopmentCommand { OperationId = id }.ValidId() || sequence < 1 || original == null || original.GetInvocationList().Length != 1)
            { why = "callback_capture_identity"; return null; }
            if (!System.Threading.Monitor.TryEnter(client, 0)) { why = "callback_capture_client_busy"; return null; }
            try
            {
                IDictionary handlers = SharedDevelopmentContext.Read(client, "m_handlers") as IDictionary;
                FieldInfo field = handler.GetType().GetField("handler", SharedDevelopmentContext.Instance);
                if (handlers == null || !handlers.Contains((ushort)opcode) || !object.ReferenceEquals(handlers[(ushort)opcode], handler) || field == null || field.FieldType != original.GetType() || !object.ReferenceEquals(field.GetValue(handler), original))
                { why = "callback_capture_handler_changed"; return null; }
                MethodInfo invoke = field.FieldType.GetMethod("Invoke"); ParameterInfo[] args = invoke == null ? new ParameterInfo[0] : invoke.GetParameters();
                if (invoke == null || invoke.ReturnType != typeof(void) || args.Length != 2 || args[0].ParameterType != typeof(MemoryStream) || args[1].ParameterType != typeof(object)) { why = "callback_capture_delegate_schema"; return null; }
                if (timed)
                {
                    float life = (float)SharedDevelopmentContext.Read(handler, "life");
                    bool fixedOneShot = opcode == 517 || opcode == 518 || opcode == 825 || opcode == 991 || opcode == 1202;
                    object arg = SharedDevelopmentContext.Read(handler, "arg");
                    bool validArg = opcode == 517
                        ? expectedTimedArgument is uint && ((uint)expectedTimedArgument == 1u || (uint)expectedTimedArgument == 2u) &&
                            object.ReferenceEquals(arg, expectedTimedArgument)
                        : arg == null && expectedTimedArgument == null;
                    if (!validArg ||
                        (opcode == 322 || opcode == 355 ? life != -1f : fixedOneShot ? life != 10f : !(life > 0f)))
                    { why = "daily_timed_callback_lifetime_or_argument"; return null; }
                }
                DevelopmentCallbackCapture capture = new DevelopmentCallbackCapture(client, handler, opcode, id, sequence, original, field, raw, guildObserver, timed);
                field.SetValue(handler, capture.wrapper); return capture;
            }
            catch (Exception) { why = "callback_capture_install_failed"; return null; }
            finally { System.Threading.Monitor.Exit(client); }
        }
        private void OnResponse(MemoryStream stream, object arg)
        {
            System.Threading.Interlocked.Increment(ref executing);
            try { OnResponseCore(stream, arg); }
            finally { System.Threading.Interlocked.Decrement(ref executing); }
        }
        private void OnResponseCore(MemoryStream stream, object arg)
        {
            bool observe = armed;
            int dispatch = 0;
            if (observe)
            {
                try
                {
                    lock (stateLock)
                    {
                        if (!timed || calls < 33) calls++;
                        dispatch = calls;
                        returned = false;
                        if (persistent ? calls > 32 : calls != 1)
                            error = timed && persistent ? "daily_timed_callback_bound" : Opcode == 355 ? "guild_activity_callback_bound" : "normal_callback_duplicate";
                        else if (System.Threading.Thread.CurrentThread.ManagedThreadId != threadId) error = "normal_callback_thread_changed";
                        else if (!object.ReferenceEquals(arg, expectedArg)) error = "normal_callback_argument_changed";
                        else if (stream == null) nullResponse = true;
                        else if (Opcode == DailyRewardOpcode && !OneShotDequeued()) error = "daily_reward_callback_not_dequeued";
                        else if (timed && (persistent ? !TimedPushStillInstalled() : !OneShotDequeued())) error = "daily_timed_callback_registration_changed";
                        else if (stream.GetType() != typeof(MemoryStream) || !stream.CanRead || stream.Position < 0 || stream.Length > maximumBytes || stream.Position > stream.Length) error = "normal_callback_stream_bound";
                        else
                        {
                            long position = stream.Position, length = stream.Length; byte[] all = stream.ToArray();
                            if (all.Length != length || stream.Position != position) error = "normal_callback_stream_changed";
                            else
                            {
                                byte[] body = new byte[(int)(length - position)]; Buffer.BlockCopy(all, (int)position, body, 0, body.Length);
                                if (guildBody != null) guildBody(body);
                                if (raw16) rawBytes = body;
                                else if (guildBody == null) { packet = DevelopmentPacket.Decode(Opcode, body); packet.Token = this; if (packet.Error != null) error = packet.Error; }
                            }
                        }
                    }
                }
                catch (Exception) { RecordError("normal_callback_capture_failed"); }
            }
            // Direct typed invocation preserves the original exception object
            // and type. There is deliberately no DynamicInvoke or stream edit.
            try { forward(stream, arg); }
            catch
            {
                if (observe) { try { lock (stateLock) { originalThrew = true; error = "normal_callback_threw"; } } catch { } }
                throw;
            }
            if (observe)
            {
                try
                {
                    lock (stateLock)
                    {
                        returned = true;
                        if (timed && dispatch > 0 && dispatch <= 32) returnedDispatches |= 1u << (dispatch - 1);
                    }
                }
                catch { }
            }
        }
        private bool OneShotDequeued()
        {
            // The pinned tcp_base.Run dispatch removes the one-shot handler
            // and current message under the client monitor before this call.
            if (!System.Threading.Monitor.TryEnter(client, 0)) return false;
            try
            {
                IDictionary handlers = SharedDevelopmentContext.Read(client, "m_handlers") as IDictionary;
                return handlers != null && !handlers.Contains((ushort)Opcode);
            }
            finally { System.Threading.Monitor.Exit(client); }
        }
        private void RecordError(string why) { try { lock (stateLock) { error = why; } } catch { } }
        internal DevelopmentCallbackWitness Read()
        { lock (stateLock) { return new DevelopmentCallbackWitness { Calls = calls, Returned = returned, CallbackReturned = returned, NullResponse = nullResponse, OriginalThrew = originalThrew, Packet = packet, RawBytes = rawBytes == null ? null : (byte[])rawBytes.Clone(), Error = error, Armed = armed }; } }
        internal bool ReturnedForDispatch(int ordinal)
        {
            lock (stateLock)
                return timed && ordinal > 0 && ordinal <= 32 && (returnedDispatches & (1u << (ordinal - 1))) != 0;
        }
        internal bool StillInstalled()
        { return timed && persistent ? TimedPushStillInstalled() : object.ReferenceEquals(callbackField.GetValue(handler), wrapper); }
        private bool TimedPushStillInstalled()
        {
            if (!System.Threading.Monitor.TryEnter(client, 0)) return false;
            try
            {
                IDictionary handlers = SharedDevelopmentContext.Read(client, "m_handlers") as IDictionary;
                return handlers != null && object.ReferenceEquals(handlers[(ushort)Opcode], handler) &&
                    object.ReferenceEquals(callbackField.GetValue(handler), wrapper);
            }
            finally { System.Threading.Monitor.Exit(client); }
        }
        internal string DisarmAndRestore()
        {
            armed = false;
            if (!System.Threading.Monitor.TryEnter(client, 0)) return "callback_restore_busy_forward_only";
            try
            {
                IDictionary handlers = SharedDevelopmentContext.Read(client, "m_handlers") as IDictionary;
                if (handlers == null) return "callback_restore_schema_forward_only";
                if (!handlers.Contains((ushort)Opcode)) return "callback_handler_already_removed";
                if (!object.ReferenceEquals(handlers[(ushort)Opcode], handler)) return "foreign_handler_preserved";
                if (!object.ReferenceEquals(callbackField.GetValue(handler), wrapper)) return "foreign_callback_preserved";
                callbackField.SetValue(handler, original); return "callback_restored";
            }
            catch (Exception) { return "callback_restore_failed_forward_only"; }
            finally { System.Threading.Monitor.Exit(client); }
        }
    }
}

