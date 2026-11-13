using System;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal sealed class SpiritBeastUpgradeTarget
    {
        internal int DesiredLevel, MaxRequests;
        internal string OperationId = "";
        internal long MaxFood;
    }
    // This capability is spirit LEVEL development only. Energy, fruit-batch,
    // skill training, comprehension and purchases are separate domains.
    internal sealed class SpiritBeastUpgradeModel
    {
        private sealed class State
        {
            internal Component Ui, Rankup;
            internal object Spirit, ExpTemplate;
            internal Delegate RankupClose;
            internal int Level, Beast, FoodType = -1, Page = -1, Desired;
            internal uint Experience, NextExp;
            internal long Food, Gold;
            internal bool Refreshing;
            internal string Gate = "PASS", Reason = "", Action = "";
            internal string Json(DevelopmentOperation op)
            {
                return "{\"schema\":1,\"gate\":" + D.Q(D.GateKind(Gate)) + ",\"reason\":" + D.Q(D.GateReason(Gate, Reason)) + ",\"level\":" + Level + ",\"experience\":" + Experience + ",\"beast_id\":" + Beast + ",\"food\":" + Food + ",\"food_type\":" + FoodType + ",\"next_exp\":" + (ExpTemplate == null ? "null" : NextExp.ToString()) + ",\"desired_level\":" + Desired + ",\"refreshing\":" + D.B(Refreshing) + ",\"cost\":{\"food\":null,\"gold\":0,\"cost_status\":\"UNVERIFIED\"},\"ui\":{\"spirit\":" + D.UiJson(Ui) + ",\"rankup\":" + D.UiJson(Rankup) + ",\"page\":" + Page + "},\"available_action\":" + D.Q(Action) + ",\"operation\":" + op.Json() + "}";
            }
            internal string Stamp() { return Json(new DevelopmentOperation()) + ":" + Gold; }
        }
        private readonly DevelopmentSession session = new DevelopmentSession();
        private DevelopmentOperation operation = new DevelopmentOperation();
        private SharedDevelopmentContext context;
        private State state = new State(), requestBefore;
        private long spentFood;
        private bool validatedReceipt, rankupConfirmed;
        internal bool Pending { get { return operation.Pending; } }
        internal bool MutationAttempted { get { return operation.MutationAttempted; } }
        internal string OperationJson() { return operation.Json(); }
        internal bool Allows(Component ui) { return state != null && (ui == state.Ui || ui == state.Rankup); }
        internal void Capture(SharedDevelopmentContext ctx) { Capture(ctx, null); }
        internal void Capture(SharedDevelopmentContext ctx, SpiritBeastUpgradeTarget readTarget)
        {
            context = ctx; operation.Observe(ctx);
            SpiritBeastUpgradeTarget target = readTarget ?? (session.Goal == null ? null : Target(session.Goal));
            bool freshBudget = readTarget != null && new DevelopmentCommand { OperationId = readTarget.OperationId }.ValidId() && (session.Goal == null || readTarget.OperationId != session.Goal.OperationId);
            state = ReadState(ctx, target, freshBudget);
            if (!validatedReceipt && operation.Status == "OBSERVED" && operation.Receipt != null && (operation.Opcode == 1136 || operation.Opcode == 1139))
            { ValidateReceipt(); state = ReadState(ctx, target, freshBudget); }
            if (operation.Pending || operation.Status == "ERROR") { state.Gate = operation.Status; state.Reason = operation.Reason; state.Action = ""; }
        }
        private static SpiritBeastUpgradeTarget Target(DevelopmentCommand c)
        { return new SpiritBeastUpgradeTarget { DesiredLevel = c.DesiredLevel, OperationId = c.OperationId, MaxRequests = c.MaxRequests, MaxFood = c.MaxFood }; }
        private State ReadState(SharedDevelopmentContext ctx, SpiritBeastUpgradeTarget target) { return ReadState(ctx, target, false); }
        private State ReadState(SharedDevelopmentContext ctx, SpiritBeastUpgradeTarget target, bool freshBudget)
        {
            State s = new State(); s.Ui = ctx.Find("SpiritUI"); s.Rankup = ctx.Find("SpiritRankupUI"); s.Gate = ctx.Panels(s.Ui, s.Rankup);
            s.Spirit = D.Read(ctx.Character, "spirit"); s.Food = D.Long(D.Read(ctx.Character, "spirit_food")); s.Gold = D.Long(D.Read(ctx.Character, "gold"));
            if (s.Spirit == null) { s.Gate = "BLOCKED"; s.Reason = "spirit_not_unlocked"; return s; }
            s.Level = D.Int(D.Read(s.Spirit, "level")); s.Experience = D.UInt(D.Read(s.Spirit, "experience")); s.Beast = D.Int(D.Read(s.Spirit, "idspirit_beast")); s.ExpTemplate = D.Template("exp_require_spirit", (uint)s.Level);
            if (s.ExpTemplate != null) { if (D.Int(D.Read(s.ExpTemplate, "level")) != s.Level) throw new ProbeFailure("spirit_exp_template_identity"); s.NextExp = D.UInt(D.Read(s.ExpTemplate, "exp")); }
            if (D.Visible(s.Ui)) { s.FoodType = D.Int(D.Read(s.Ui, "food_type")); s.Refreshing = D.Bool(D.Read(s.Ui, "m_bRefreshing")); s.Page = D.Int(D.Read(s.Ui, "m_iPage")); }
            if (D.Visible(s.Rankup)) s.RankupClose = D.Read(s.Rankup, "OnClose") as Delegate;
            if (target == null) { s.Reason = "typed_target_required"; return s; }
            s.Desired = target.DesiredLevel;
            if (s.Gate != "PASS") { s.Reason = s.Gate; return s; }
            if (D.Loading(s.Ui) || D.Loading(s.Rankup) || (object.ReferenceEquals(ctx.SubUi, s.Ui) && !D.Visible(s.Ui) && !D.Visible(s.Rankup)))
            { s.Gate = "WAITING_UI"; s.Reason = "spirit_normal_ui_loading"; return s; }
            if (!ctx.Network.Idle) { s.Gate = "WAITING_NETWORK"; s.Reason = "spirit_network_not_idle"; return s; }
            if (D.Visible(s.Rankup))
            {
                if (!operation.ServerProgressObserved || operation.Receipt == null || operation.Receipt.Opcode != 1139 || requestBefore == null || rankupConfirmed)
                { s.Reason = "spirit_rankup_popup_not_owned_or_replayed"; return s; }
                if (CanConfirm(ctx, s)) s.Action = "spirit_rankup_confirm"; else s.Reason = "spirit_rankup_binding_or_callback_unreviewed";
                return s;
            }
            if (s.Level >= s.Desired) { s.Reason = "target_level_already_met"; if (CanClose(ctx, s)) s.Action = "spirit_close"; return s; }
            string why = TargetGate(ctx, s, target, freshBudget); if (why != "") { s.Gate = "BLOCKED"; s.Reason = why; return s; }
            if (!D.Visible(s.Ui))
            {
                if (ctx.SubUi != null) { s.Reason = "spirit_existing_subui"; return s; }
                if (s.Level >= 41) { s.Reason = "spirit_high_level_entry_unsupported"; return s; }
                bool unlocked = (bool)D.Method(Model.GameType("MainUI"), "checkSpirit", new Type[] { typeof(string) }, true, typeof(bool)).Invoke(null, new object[] { "army_spirit" });
                if (unlocked) s.Action = "spirit_open"; else s.Reason = "spirit_menu_locked"; return s;
            }
            // High-level nested parent closing/opening is deliberately not
            // synthesized. Read it faithfully, leave an explicit extension point.
            if (s.Level >= 41) { s.Reason = "spirit_high_level_parent_chain_unsupported"; return s; }
            if (!D.Enabled(s.Ui) || D.Loading(s.Ui) || s.Refreshing || !object.ReferenceEquals(ctx.SubUi, s.Ui) || !object.ReferenceEquals(D.Read(s.Ui, "m_spirit"), s.Spirit)) { s.Reason = "spirit_ui_binding_or_interaction"; return s; }
            if (s.Page != 0 || D.Static(s.Ui.GetType(), "my_Subui") != null) { s.Reason = "spirit_page_or_nested_ui_unsupported"; return s; }
            bool rank = s.Level % 10 == 0 && s.Experience >= s.NextExp;
            if (rank) s.Action = "spirit_rankup";
            else if (s.FoodType != 0) s.Reason = "spirit_premium_fallback_forbidden";
            else if (s.Food <= 0) s.Reason = "spirit_existing_food_insufficient";
            else if (target.MaxFood < s.Food + (freshBudget ? 0 : spentFood)) s.Reason = "strict_budget_unavailable";
            else s.Action = "spirit_train";
            return s;
        }
        private string TargetGate(SharedDevelopmentContext ctx, State s, SpiritBeastUpgradeTarget target, bool freshBudget)
        {
            if (D.Read(ctx.Root, "LevelID") as string != "Campaign") return "spirit_capital_actions_unsupported";
            if (target.DesiredLevel < 1 || target.DesiredLevel > 100 || target.MaxRequests < 1 || target.MaxRequests > 100 || target.MaxFood < 0) return "spirit_target_bounds";
            if (s.Level < 1 || s.Level >= 100 || s.ExpTemplate == null || s.NextExp == 0 || s.Food < 0 || s.Gold < 0) return "spirit_level_or_exp_template_unavailable";
            if (!freshBudget && operation.Requests >= target.MaxRequests) return "spirit_request_budget";
            return "";
        }
        private static bool CanConfirm(SharedDevelopmentContext ctx, State s)
        {
            if (ctx.Panels(s.Ui, s.Rankup) != "PASS" || !ctx.Network.Idle || !D.Enabled(s.Rankup) || D.Loading(s.Rankup) || !object.ReferenceEquals(D.Read(s.Rankup, "m_spirit"), s.Spirit)) return false;
            Delegate d = s.RankupClose; if (d == null || d.GetInvocationList().Length != 1 || d.Method.ReturnType != typeof(void) || d.Method.GetParameters().Length != 0) return false;
            if (s.Level == 41) return d.Target == null && d.Method.DeclaringType == Model.GameType("DelegateFuncUI") && d.Method.Name == "MainUI_spirit2";
            return object.ReferenceEquals(d.Target, s.Ui) && d.Method.DeclaringType == Model.FirstType("__BaseUI") && d.Method.Name == "EnableAll";
        }
        private static bool CanClose(SharedDevelopmentContext ctx, State s)
        {
            Delegate set = D.Static(Model.FirstType("DelegateFunc"), "MainUI_SetSubUI") as Delegate;
            return set != null && set.GetInvocationList().Length == 1 && set.Target == null && set.Method.DeclaringType == Model.GameType("DelegateFuncUI") && set.Method.Name == "MainUI_SetSubUI" &&
                ctx.Panels(s.Ui, s.Rankup) == "PASS" && ctx.Network.Idle && s.Level <= 40 && D.Enabled(s.Ui) && !D.Loading(s.Ui) && !s.Refreshing && object.ReferenceEquals(ctx.SubUi, s.Ui) &&
                object.ReferenceEquals(D.Read(s.Ui, "m_spirit"), s.Spirit) && D.Read(s.Ui, "OnClose") == null && D.Static(s.Ui.GetType(), "my_Subui") == null && !D.Visible(s.Rankup);
        }
        internal bool Queue(DevelopmentCommand command, out string why)
        {
            why = ""; if (context == null || command == null || command.DesiredLevel < 1 || command.DesiredLevel > 100 || command.MaxRequests < 1 || command.MaxRequests > 100 || command.MaxFood < 0 ||
                !context.Network.Idle || operation.Pending || operation.Status == "ERROR" || command.TargetId != 0 || command.Mode != "" || command.MaxMoney != 0 || command.MaxStone != 0) { why = "spirit_context_or_command_unavailable"; return false; }
            State s = ReadState(context, Target(command), session.Goal == null || session.Goal.OperationId != command.OperationId); bool close = command.Action == "spirit_close";
            if (close ? !CanClose(context, s) : s.Gate != "PASS" || s.Action != command.Action) { why = "spirit_action_unavailable:" + s.Reason; return false; }
            return session.Queue(command, context, s, s.Stamp(), out why);
        }
        internal bool ExecuteGui(DevelopmentCommand command, DevelopmentPublication publish, out string why)
        {
            object anchor; string stamp; if (!session.Take(command, context, out anchor, out stamp, out why)) return false;
            State before = anchor as State, now = ReadState(context, Target(command), session.Goal == null || session.Goal.OperationId != command.OperationId); bool close = command.Action == "spirit_close";
            if (before == null || now.Stamp() != stamp || !object.ReferenceEquals(before.Spirit, now.Spirit) || !object.ReferenceEquals(before.ExpTemplate, now.ExpTemplate) || !object.Equals(before.RankupClose, now.RankupClose) || (close ? !CanClose(context, now) : now.Gate != "PASS" || now.Action != command.Action))
            { why = "spirit_gui_target_or_resource_changed"; return false; }
            if (session.Goal == null || session.Goal.OperationId != command.OperationId) { operation = new DevelopmentOperation { OperationId = command.OperationId }; spentFood = 0; }
            int opcode = command.Action == "spirit_train" ? 1136 : command.Action == "spirit_rankup" ? 1139 : 0;
            if (!operation.Begin(context, command, opcode, command.MaxRequests, delegate { session.Commit(command); state = now; publish(); }, out why)) return false;
            validatedReceipt = false; if (opcode != 0) requestBefore = now; if (opcode == 1139) rankupConfirmed = false;
            try
            {
                if (command.Action == "spirit_open") D.MainBranch("_army_spirit");
                else if (opcode == 1136)
                {
                    // Exact fruit button branch. These arguments are NOT used
                    // as a cost gate: the pinned method ignores both of them.
                    if (!operation.InvokeNormal(context, now.Ui, "sc_spirit_train_0x470", delegate { D.Method(now.Ui.GetType(), "cs_spirit_train_0x470", new Type[] { typeof(bool), typeof(uint) }, false, typeof(void)).Invoke(now.Ui, new object[] { false, 1u }); }, out why)) return false;
                }
                else if (opcode == 1139)
                { if (!operation.InvokeNormal(context, now.Ui, "sc_spirit_levelup_0x473", delegate { D.Call(now.Ui, "cs_spirit_levelup_0x473"); }, out why)) return false; }
                else if (command.Action == "spirit_rankup_confirm") { rankupConfirmed = true; D.Call(now.Rankup, "Hide"); now.RankupClose.DynamicInvoke(new object[0]); }
                else if (close)
                {
                    D.Call(now.Ui, "Hide");
                    Delegate set = D.Static(Model.FirstType("DelegateFunc"), "MainUI_SetSubUI") as Delegate;
                    if (set == null || set.GetInvocationList().Length != 1 || set.Target != null || set.Method.DeclaringType != Model.GameType("DelegateFuncUI") || set.Method.Name != "MainUI_SetSubUI") throw new ProbeFailure("spirit_close_delegate_unreviewed");
                    set.DynamicInvoke(new object[] { null });
                }
                else throw new ProbeFailure("spirit_action_unsupported");
                if (opcode == 0) operation.Invoked(context, now.Ui, ""); return true;
            }
            catch (Exception) { operation.Unknown("spirit_normal_branch_threw"); why = operation.Reason; return false; }
        }
        private void ValidateReceipt()
        {
            validatedReceipt = true; DevelopmentPacket p = operation.Receipt; State b = requestBefore;
            if (b == null || !object.ReferenceEquals(b.Spirit, state.Spirit) || p.Level < b.Level || p.Level > 100 || state.Level != p.Level || state.Experience != p.Experience ||
                (p.Level == b.Level && p.Experience <= b.Experience) || p.Gold != 0 || state.Gold != b.Gold || state.Food != b.Food - p.Food || p.Food > b.Food || state.Refreshing)
            { operation.Unknown("spirit_response_identity_progress_or_resource_mismatch"); return; }
            if (operation.Opcode == 1139 && p.Level <= b.Level) { operation.Unknown("spirit_rankup_no_level_progress"); return; }
            spentFood += p.Food;
            operation.ServerProgressObserved = true;
        }
        internal void Stop(string reason) { session.Stop(); operation.Stop(reason); }
        internal string Json() { return state.Json(operation); }
    }
}





