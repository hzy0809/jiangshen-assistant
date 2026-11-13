using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal sealed class EquipmentUpgradeTarget
    {
        internal int EquipmentId, DesiredLevel, MaxRequests;
        internal string Mode = "enchant";
        internal string OperationId = "";
        internal long MaxMoney, MaxStone;
    }
    // Reusable equipment development. No quest ID is read or written here.
    // Every mutation is a normal fixed UI handler or its complete button branch.
    internal sealed class EquipmentUpgradeModel
    {
        private sealed class State
        {
            internal Component EquipmentUi, Reinforce;
            internal object Equipment, Price, Owner;
            internal string Candidates = "[]", Gate = "PASS", Reason = "", Action = "";
            internal int Id, Desired, OwnerId = 65535, Slot = -1, Level, Main, Next, Up, Page = -1, UiOwner = -1, UiSlot = -1, Bound;
            internal long Money, Stone, HaveMoney, HaveStone;
            internal bool Sent;
            internal string Mode = "";
            internal string Json(DevelopmentOperation op)
            {
                return "{\"schema\":1,\"gate\":" + D.Q(D.GateKind(Gate)) + ",\"reason\":" + D.Q(D.GateReason(Gate, Reason)) + ",\"target_id\":" + Id + ",\"desired_level\":" + Desired + ",\"mode\":" + D.Q(Mode) +
                    ",\"available_money\":" + HaveMoney + ",\"available_stone\":" + HaveStone + ",\"candidates\":" + Candidates + ",\"ui\":{\"equipment\":" + D.UiJson(EquipmentUi) + ",\"reinforce\":" + D.UiJson(Reinforce) + ",\"owner_id\":" + UiOwner + ",\"slot\":" + UiSlot + ",\"page\":" + Page + ",\"sended\":" + D.B(Sent) + ",\"bound_equipment_id\":" + Bound + "},\"available_action\":" + D.Q(Action) + ",\"operation\":" + op.Json() + "}";
            }
            internal string Stamp()
            { return Json(new DevelopmentOperation()) + ":" + HaveMoney + ":" + HaveStone; }
        }
        private readonly DevelopmentSession session = new DevelopmentSession();
        private DevelopmentOperation operation = new DevelopmentOperation();
        private SharedDevelopmentContext context;
        private State state = new State();
        private long spentMoney, spentStone;
        private int previousLevel, previousId;
        private State requestBefore;
        private bool validatedReceipt;
        internal bool Pending { get { return operation.Pending; } }
        internal bool MutationAttempted { get { return operation.MutationAttempted; } }
        internal string OperationJson() { return operation.Json(); }
        internal bool Allows(Component ui) { return state != null && (ui == state.EquipmentUi || ui == state.Reinforce); }
        internal void Capture(SharedDevelopmentContext ctx) { Capture(ctx, null); }
        internal void Capture(SharedDevelopmentContext ctx, EquipmentUpgradeTarget readTarget)
        {
            context = ctx; operation.Observe(ctx);
            DevelopmentCommand goal = session.Goal;
            EquipmentUpgradeTarget target = readTarget ?? (goal == null ? null : Target(goal));
            bool freshBudget = readTarget != null && ValidOperationId(readTarget.OperationId) && (goal == null || readTarget.OperationId != goal.OperationId);
            state = ReadState(ctx, target, freshBudget);
            if (!validatedReceipt && operation.Status == "OBSERVED" && operation.Opcode == 1104 && operation.Receipt != null)
            {
                validatedReceipt = true; DevelopmentPacket p = operation.Receipt;
                if (p.Id != previousId || p.Level <= previousLevel || p.Level > 255 || state.Id != previousId || state.Level != p.Level || state.Sent || requestBefore == null ||
                    !object.ReferenceEquals(state.Equipment, requestBefore.Equipment) || !object.ReferenceEquals(state.Owner, requestBefore.Owner) || state.OwnerId != requestBefore.OwnerId || state.Slot != requestBefore.Slot ||
                    state.HaveMoney != requestBefore.HaveMoney - requestBefore.Money || state.HaveStone != requestBefore.HaveStone - requestBefore.Stone || !PartsMatch(requestBefore, state))
                    operation.Unknown("equipment_response_identity_or_progress_mismatch");
                else operation.ServerProgressObserved = true;
                // The pinned normal callback ignores result. Exact ID + server
                // level progress is retained separately; result semantics remain
                // explicitly UNVERIFIED, never inferred from local flags.
            }
            if (operation.Pending || operation.Status == "ERROR") { state.Gate = operation.Status; state.Reason = operation.Reason; state.Action = ""; }
        }
        private static EquipmentUpgradeTarget Target(DevelopmentCommand c)
        { return new EquipmentUpgradeTarget { EquipmentId = c.TargetId, DesiredLevel = c.DesiredLevel, Mode = c.Mode, OperationId = c.OperationId, MaxRequests = c.MaxRequests, MaxMoney = c.MaxMoney, MaxStone = c.MaxStone }; }
        private static bool ValidOperationId(string id) { return new DevelopmentCommand { OperationId = id }.ValidId(); }
        private State ReadState(SharedDevelopmentContext ctx, EquipmentUpgradeTarget target) { return ReadState(ctx, target, false); }
        private State ReadState(SharedDevelopmentContext ctx, EquipmentUpgradeTarget target, bool freshBudget)
        {
            State s = new State(); s.EquipmentUi = ctx.Find("EquipmentUI"); s.Reinforce = ctx.Find("EquipmentReinforceUI");
            s.Gate = ctx.Panels(s.EquipmentUi, s.Reinforce); s.HaveMoney = D.Money(ctx.Character); s.HaveStone = D.Long(D.Read(ctx.Character, "enchant_stone"));
            IDictionary equipments = D.Read(ctx.Character, "equipments") as IDictionary;
            if (equipments == null || equipments.Count > 255) throw new ProbeFailure("equipment_collection_schema_or_bound");
            StringBuilder candidates = new StringBuilder("["); bool comma = false;
            for (int id = 1; id <= 255; id++)
            {
                if (!equipments.Contains((byte)id)) continue; object e = equipments[(byte)id];
                if (e == null || D.Int(D.Read(e, "id")) != id) throw new ProbeFailure("equipment_dictionary_identity");
                int level = D.Int(D.Read(e, "enchant_level")), owner = D.Int(D.Read(e, "idgeneral_template")); object main = D.Read(e, "main_");
                int kind = D.Int(D.Read(main, "idequipment_type")); object price = D.Template("enchant_price", (uint)(level + 1));
                int mainId = D.Int(D.Read(e, "idequipment_main")), next = D.Int(D.Read(main, "next_id"));
                if (D.Int(D.Read(main, "id")) != mainId) throw new ProbeFailure("equipment_main_identity");
                long money = price == null ? 0 : D.Long(D.Read(price, kind == 1 ? "weapon" : "armor")); long stone = price == null ? 0 : D.Long(D.Read(price, "enchant")); int up = price == null ? -1 : D.Int(D.Read(price, "up"));
                if (price != null && D.Int(D.Read(price, "level")) != level + 1) throw new ProbeFailure("equipment_price_identity");
                int slot = kind == 1 ? 0 : kind == 2 ? 1 : -1;
                bool inForm = CandidateInForm(ctx.Character, owner), ownerMatches = CandidateOwnerMatches(ctx.Character, owner, slot, e);
                if (comma) candidates.Append(','); comma = true;
                candidates.Append("{\"equipment_id\":" + id + ",\"owner_id\":" + owner + ",\"slot\":" + slot + ",\"level\":" + level + ",\"main_id\":" + mainId + ",\"next_main_id\":" + next + ",\"in_current_formation\":" + D.B(inForm) + ",\"owner_slot_matches\":" + D.B(ownerMatches) + ",\"cost\":{\"money\":" + (price == null ? "null" : money.ToString()) + ",\"stone\":" + (price == null ? "null" : stone.ToString()) + ",\"up\":" + (price == null ? "null" : up.ToString()) + ",\"cost_status\":" + D.Q(price == null ? "UNVERIFIED" : "OBSERVED") + "}}");
                if (target != null && target.EquipmentId == id)
                { s.Equipment = e; s.Price = price; s.Id = id; s.Level = level; s.Main = mainId; s.Next = next; s.OwnerId = owner; s.Slot = slot; s.Money = money; s.Stone = stone; s.Up = up; }
            }
            candidates.Append(']'); s.Candidates = candidates.ToString();
            if (target != null && s.Equipment != null)
            { IDictionary generals = D.Read(ctx.Character, "form_generals_") as IDictionary; if (generals != null && generals.Contains((ushort)s.OwnerId)) s.Owner = generals[(ushort)s.OwnerId]; }
            if (D.Visible(s.Reinforce))
            {
                s.UiOwner = D.Int(D.Read(s.Reinforce, "m_curgen")); s.UiSlot = D.Int(D.Read(s.Reinforce, "m_curItem")); s.Page = D.Int(D.Read(s.Reinforce, "m_curEquipPage")); s.Sent = D.Bool(D.Read(s.Reinforce, "sended"));
                object bound = D.Method(s.Reinforce.GetType(), "getCurEquip", Type.EmptyTypes, false, EquipmentType()).Invoke(s.Reinforce, new object[0]);
                if (bound != null) s.Bound = D.Int(D.Read(bound, "id"));
            }
            if (target == null) { s.Reason = "typed_target_required"; return s; }
            s.Id = target.EquipmentId; s.Desired = target.DesiredLevel; s.Mode = target.Mode;
            // The normal tutorial opens NewbieQuestUI as soon as the final
            // enchant completes. Recognize the completed goal without issuing
            // another enchant or trying to close the now hidden gear panels.
            if (s.Equipment != null && s.Level >= s.Desired && !D.Visible(s.EquipmentUi) && !D.Visible(s.Reinforce) &&
                ctx.Panels(ctx.Find("NewbieQuestUI")) == "PASS" &&
                D.Visible(ctx.Find("NewbieQuestUI")) && TargetGate(ctx, s, target, freshBudget) == "")
            { s.Gate = ctx.Network.Idle ? "PASS" : "WAITING_NETWORK"; s.Reason = "target_level_met_tutorial_task_opened"; return s; }
            if (s.Gate != "PASS") { s.Reason = s.Gate; return s; }
            if (D.Loading(s.EquipmentUi) || D.Loading(s.Reinforce) || ((object.ReferenceEquals(ctx.SubUi, s.EquipmentUi) || object.ReferenceEquals(ctx.SubUi, s.Reinforce)) && !D.Visible(ctx.SubUi as Component)))
            { s.Gate = "WAITING_UI"; s.Reason = "equipment_normal_ui_loading"; return s; }
            if (!ctx.Network.Idle) { s.Gate = "WAITING_NETWORK"; s.Reason = "equipment_network_not_idle"; return s; }
            string why = TargetGate(ctx, s, target, freshBudget); if (why != "") { s.Gate = "BLOCKED"; s.Reason = why; return s; }
            if (s.Level >= s.Desired) { s.Reason = "target_level_already_met"; if (CanClose(ctx, s)) s.Action = "equipment_close"; return s; }
            if (!D.Visible(s.EquipmentUi) && !D.Visible(s.Reinforce))
            { if (ctx.SubUi == null && MainUnlocked("general_person")) s.Action = "equipment_open"; else s.Reason = "equipment_existing_subui_or_locked"; return s; }
            if (D.Visible(s.EquipmentUi))
            { if (object.ReferenceEquals(ctx.SubUi, s.EquipmentUi) && D.Enabled(s.EquipmentUi) && !D.Loading(s.EquipmentUi)) s.Action = "reinforce_open"; else s.Reason = "equipment_page_not_interactive"; return s; }
            if (!object.ReferenceEquals(ctx.SubUi, s.Reinforce) || !D.Enabled(s.Reinforce) || D.Loading(s.Reinforce) || s.Sent || s.UiOwner != s.OwnerId || s.UiSlot != s.Slot || s.Bound != s.Id)
            { s.Reason = "reinforce_binding_or_interaction"; return s; }
            if (s.Page < 0 || s.Page > 2) { s.Reason = "reinforce_page_unsupported"; return s; }
            s.Action = s.Page == 0 ? "equipment_enchant" : "enchant_tab"; return s;
        }
        private static bool CandidateInForm(object character, int owner)
        {
            if (owner < 0 || owner >= 65535) return false;
            IDictionary generals = D.Read(character, "form_generals_") as IDictionary;
            if (generals == null || !generals.Contains((ushort)owner) || D.Int(D.Read(generals[(ushort)owner], "idgeneral_template")) != owner) return false;
            IDictionary forms = D.Read(character, "formations_") as IDictionary; int cur = D.Int(D.Read(character, "cur_form"));
            if (forms == null || !forms.Contains(cur)) return false; IList spots = D.Read(forms[cur], "spotgen") as IList;
            if (spots == null || spots.Count > 8) return false; foreach (object id in spots) if (D.Int(id) == owner) return true; return false;
        }
        private static bool CandidateOwnerMatches(object character, int owner, int slot, object equipment)
        {
            IDictionary generals = D.Read(character, "form_generals_") as IDictionary;
            if (owner < 0 || owner >= 65535 || slot < 0 || slot > 1 || generals == null || !generals.Contains((ushort)owner)) return false;
            object gen = generals[(ushort)owner]; IList slots = D.Read(gen, "equipments") as IList;
            return D.Int(D.Read(gen, "idgeneral_template")) == owner && slots != null && slots.Count == 2 && object.ReferenceEquals(slots[slot], equipment);
        }
        private string TargetGate(SharedDevelopmentContext ctx, State s, EquipmentUpgradeTarget target, bool freshBudget)
        {
            if (D.Read(ctx.Root, "LevelID") as string != "Campaign") return "equipment_capital_actions_unsupported";
            if (target.EquipmentId < 1 || target.EquipmentId > 255 || target.DesiredLevel < 1 || target.DesiredLevel > 255 || target.MaxRequests < 1 || target.MaxRequests > 100 || target.MaxMoney < 0 || target.MaxStone < 0 || (target.Mode != "enchant" && target.Mode != "evolve")) return "equipment_target_bounds";
            if (s.Equipment == null || s.Price == null || s.Slot < 0 || s.OwnerId == 65535) return "equipment_target_or_cost_unavailable";
            IDictionary generals = D.Read(ctx.Character, "form_generals_") as IDictionary, forms = D.Read(ctx.Character, "formations_") as IDictionary;
            int cur = D.Int(D.Read(ctx.Character, "cur_form"));
            if (generals == null || forms == null || !generals.Contains((ushort)s.OwnerId) || !forms.Contains(cur)) return "equipment_owner_or_form_unavailable";
            s.Owner = generals[(ushort)s.OwnerId]; IList slots = D.Read(s.Owner, "equipments") as IList, spots = D.Read(forms[cur], "spotgen") as IList;
            if (D.Int(D.Read(s.Owner, "idgeneral_template")) != s.OwnerId || slots == null || slots.Count != 2 || !object.ReferenceEquals(slots[s.Slot], s.Equipment) || spots == null || spots.Count > 8) return "equipment_owner_slot_identity";
            bool inForm = false; foreach (object spot in spots) if (D.Int(spot) == s.OwnerId) inForm = true;
            if (!inForm || !(bool)D.Method(ctx.Character.GetType(), "checkUI", new Type[] { typeof(uint) }, false, typeof(bool)).Invoke(ctx.Character, new object[] { 11u })) return "equipment_owner_not_in_form_or_locked";
            if (D.Int(D.Read(ctx.Character, "equipment_forge")) != 0) return "equipment_pending_forge_dialog";
            if (s.Level >= target.DesiredLevel) return "";
            if (s.Up != 0 && s.Up != 1) return "equipment_price_up_unsupported";
            if ((s.Up == 1) != (target.Mode == "evolve")) return s.Up == 1 ? "equipment_mode_evolve_required" : "equipment_mode_enchant_required";
            if (s.Up == 1 && (s.Next <= 0 || D.Template("equipment_main", (uint)s.Next) == null)) return "equipment_evolve_template_unavailable";
            if (s.Level >= D.Int(D.Read(ctx.Character, "level"))) return "equipment_character_level_cap";
            if (s.Money < 0 || s.Stone < 0 || s.HaveMoney < s.Money || s.HaveStone < s.Stone) return "equipment_existing_resource_insufficient";
            if ((freshBudget ? 0 : spentMoney) + s.Money > target.MaxMoney || (freshBudget ? 0 : spentStone) + s.Stone > target.MaxStone) return "equipment_resource_budget";
            if (!freshBudget && operation.Requests >= target.MaxRequests) return "equipment_request_budget";
            return "";
        }
        private static bool PartsMatch(State before, State after)
        {
            object expected = D.Template("equipment_main", (uint)(before.Up == 1 ? before.Next : before.Main));
            return expected != null && after.Main == (before.Up == 1 ? before.Next : before.Main) && D.Int(D.Read(expected, "id")) == after.Main &&
                (before.Up != 1 || (D.Int(D.Read(after.Equipment, "idequipment_acc1")) == D.Int(D.Read(expected, "accessory1")) && D.Int(D.Read(after.Equipment, "idequipment_acc2")) == D.Int(D.Read(expected, "accessory2"))));
        }
        private static Type EquipmentType() { return Model.FirstType("Database.Equipment"); }
        private static bool MainUnlocked(string name)
        { return (bool)D.Method(Model.GameType("MainUI"), "checkUI", new Type[] { typeof(string) }, true, typeof(bool)).Invoke(null, new object[] { name }); }
        internal bool Queue(DevelopmentCommand command, out string why)
        {
            why = ""; if (context == null || command == null || command.TargetId < 1 || command.TargetId > 255 || command.DesiredLevel < 1 || command.DesiredLevel > 255 || command.MaxRequests < 1 || command.MaxRequests > 100 ||
                (command.Mode != "enchant" && command.Mode != "evolve") || command.MaxMoney < 0 || command.MaxStone < 0 || command.MaxFood != 0 || !context.Network.Idle || operation.Pending || operation.Status == "ERROR") { why = "equipment_context_or_operation_unavailable"; return false; }
            State next = ReadState(context, Target(command), session.Goal == null || session.Goal.OperationId != command.OperationId);
            bool closing = command.Action == "equipment_close";
            if (closing ? !CanClose(context, next) : next.Gate != "PASS" || next.Action != command.Action) { why = "equipment_action_unavailable:" + next.Reason; return false; }
            return session.Queue(command, context, next, next.Stamp(), out why);
        }
        internal bool ExecuteGui(DevelopmentCommand command, DevelopmentPublication publish, out string why)
        {
            object anchor; string stamp;
            if (!session.Take(command, context, out anchor, out stamp, out why)) return false;
            State before = anchor as State, now = ReadState(context, Target(command), session.Goal == null || session.Goal.OperationId != command.OperationId); bool closing = command.Action == "equipment_close";
            if (before == null || now.Stamp() != stamp || !object.ReferenceEquals(before.Equipment, now.Equipment) || !object.ReferenceEquals(before.Price, now.Price) || !object.ReferenceEquals(before.Owner, now.Owner) || (closing ? !CanClose(context, now) : now.Action != command.Action))
            { why = "equipment_gui_target_or_cost_changed"; return false; }
            if (session.Goal == null || session.Goal.OperationId != command.OperationId) { operation = new DevelopmentOperation { OperationId = command.OperationId }; spentMoney = spentStone = 0; }
            int opcode = command.Action == "equipment_enchant" ? 1104 : 0;
            if (!operation.Begin(context, command, opcode, command.MaxRequests, delegate { session.Commit(command); state = now; publish(); }, out why)) return false;
            validatedReceipt = false;
            try
            {
                if (command.Action == "equipment_open") D.MainBranch("_general_person");
                else if (command.Action == "reinforce_open")
                {
                    D.Method(now.EquipmentUi.GetType(), "selectItem", new Type[] { EquipmentType() }, false, typeof(void)).Invoke(now.EquipmentUi, new object[] { now.Equipment });
                    Type baseUi = Model.FirstType("__BaseUI"), page = D.Read(now.Reinforce, "m_curItem").GetType();
                    if (!(bool)D.Method(Model.GameType("MainUI"), "handle_sub_ui", new Type[] { baseUi, typeof(string) }, true, typeof(bool)).Invoke(null, new object[] { now.Reinforce, "" })) throw new ProbeFailure("equipment_normal_open_refused");
                    D.Method(now.Reinforce.GetType(), "MyLoading", new Type[] { typeof(ushort), page }, false, typeof(void)).Invoke(now.Reinforce, new object[] { (ushort)now.OwnerId, Enum.ToObject(page, now.Slot) });
                    Type callbackType = Model.FirstType("__BaseUI+delegate_OnClose");
                    Delegate callback = Delegate.CreateDelegate(callbackType, D.Method(Model.GameType("MainUI"), "_general_person", Type.EmptyTypes, true, typeof(void)));
                    D.Method(now.Reinforce.GetType(), "setOnClose", new Type[] { callbackType }, false, typeof(void)).Invoke(now.Reinforce, new object[] { callback });
                }
                else if (command.Action == "enchant_tab")
                { D.Selection(now.Reinforce, "m_curEquipPage", Enum.ToObject(D.Read(now.Reinforce, "m_curEquipPage").GetType(), 0)); D.Call(now.Reinforce, "ClearRune"); }
                else if (opcode == 1104)
                {
                    requestBefore = now; previousId = now.Id; previousLevel = now.Level; spentMoney += now.Money; spentStone += now.Stone;
                    if (!operation.InvokeNormal(context, now.Reinforce, "sc_equipment_enchant_0x450", delegate { D.Call(now.Reinforce, "callEnchant"); }, out why)) return false;
                }
                else if (closing) Close(context, now);
                else throw new ProbeFailure("equipment_action_unsupported");
                if (opcode == 0) operation.Invoked(context, now.Reinforce, "sc_equipment_enchant_0x450"); return true;
            }
            catch (Exception) { operation.Unknown("equipment_normal_branch_threw"); why = operation.Reason; return false; }
        }
        private static bool CanClose(SharedDevelopmentContext ctx, State s)
        {
            if (!ctx.Network.Idle || ctx.Panels(s.EquipmentUi, s.Reinforce) != "PASS") return false;
            if (D.Visible(s.Reinforce))
            {
                Delegate d = D.Read(s.Reinforce, "OnClose") as Delegate;
                return D.Enabled(s.Reinforce) && !D.Loading(s.Reinforce) && !s.Sent && object.ReferenceEquals(ctx.SubUi, s.Reinforce) && d != null && d.GetInvocationList().Length == 1 && d.Target == null && d.Method.DeclaringType == Model.GameType("MainUI") && d.Method.Name == "_general_person";
            }
            return D.Enabled(s.EquipmentUi) && !D.Loading(s.EquipmentUi) && object.ReferenceEquals(ctx.SubUi, s.EquipmentUi) && D.Read(s.EquipmentUi, "OnClose") == null;
        }
        private static void Close(SharedDevelopmentContext ctx, State s)
        {
            Component ui = D.Visible(s.Reinforce) ? s.Reinforce : s.EquipmentUi; Delegate close = D.Read(ui, "OnClose") as Delegate;
            D.Call(ui, "Hide"); if (close != null) close.DynamicInvoke(new object[0]);
            if (ui == s.Reinforce) { FieldInfo f = Field(ui.GetType(), "OnClose"); f.SetValue(ui, null); }
            else D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
        }
        private static FieldInfo Field(Type t, string name)
        { for (; t != null; t = t.BaseType) { FieldInfo f = t.GetField(name, D.Instance); if (f != null) return f; } throw new ProbeFailure("equipment_close_schema"); }
        internal void Stop(string reason) { session.Stop(); operation.Stop(reason); }
        internal string Json() { return state.Json(operation); }
    }
}





