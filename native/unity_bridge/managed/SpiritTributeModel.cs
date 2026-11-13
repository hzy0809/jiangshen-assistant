using System;
using System.Collections;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // The normal 上贡 branch, independent of the desired-level capability.
    // The pinned game ignores cs_spirit_train's bool/count arguments and sends
    // food_type. Never invoke it unless that exact field is zero (peaches).
    internal sealed class SpiritTributeModel
    {
        private sealed class State
        {
            internal Component Ui, Parent, Parent2, Parent3, Rankup;
            internal object Spirit, ExpTemplate;
            internal Delegate Close, RankupClose;
            internal string Gate = "PASS", Reason = "", Action = "", CleanupAction = "", ParentKind = "none", Head = "", Head2 = "";
            internal bool Done, Refreshing, NestedOwned;
            internal int Level, DesiredLevel, TributeRequests, FoodType = -1, Page = -1;
            internal uint Experience, NextExp;
            internal long Food, Gold, Spent;
            internal string Json(DevelopmentOperation operation)
            {
                return "{\"schema\":1,\"gate\":" + D.Q(D.GateKind(Gate)) + ",\"reason\":" + D.Q(D.GateReason(Gate, Reason)) + ",\"done\":" + D.B(Done) +
                    ",\"level\":" + Level + ",\"desired_level\":" + DesiredLevel + ",\"tribute_requests\":" + TributeRequests + ",\"experience\":" + Experience + ",\"next_exp\":" + (ExpTemplate == null ? "null" : NextExp.ToString()) + ",\"food\":" + Food + ",\"gold\":" + Gold + ",\"food_type\":" + FoodType + ",\"spent_food\":" + Spent +
                    ",\"parent_kind\":" + D.Q(ParentKind) + ",\"refreshing\":" + D.B(Refreshing) + ",\"cost\":{\"gold\":0,\"food\":null,\"cost_status\":\"UNVERIFIED\"},\"ui\":{\"spirit\":" + D.UiJson(Ui) + ",\"parent\":" + D.UiJson(Parent) + ",\"rankup\":" + D.UiJson(Rankup) + ",\"page\":" + Page + ",\"nested_owned\":" + D.B(NestedOwned) + "},\"available_action\":" + D.Q(Action) + ",\"cleanup_action\":" + D.Q(CleanupAction) + ",\"operation\":" + operation.Json() + "}";
            }
            internal string Stamp() { return Json(new DevelopmentOperation()) + ":" + Head + ":" + Head2; }
        }
        private readonly DevelopmentSession session = new DevelopmentSession();
        private DevelopmentOperation operation = new DevelopmentOperation();
        private DevelopmentCommand target;
        private SharedDevelopmentContext context;
        private SharedDevelopmentContext openedContext;
        private Component openedUi, openedParent;
        private State state = new State(), requestBefore;
        private long spentFood;
        private int tributeRequests;
        private bool validatedReceipt, rankupConfirmed;
        internal bool Pending { get { return operation.Pending; } }
        internal bool MutationAttempted { get { return operation.MutationAttempted; } }
        internal void ObserveTarget(DevelopmentCommand command) { target = command.Copy(); }
        private bool Fresh(DevelopmentCommand command) { return command != null && (session.Goal == null || session.Goal.OperationId != command.OperationId); }
        internal void Capture(SharedDevelopmentContext ctx)
        {
            context = ctx; operation.Observe(ctx);
            DevelopmentCommand goal = target ?? session.Goal;
            state = ReadState(ctx, goal, Fresh(goal));
            if (!validatedReceipt && operation.Status == "OBSERVED" && (operation.Opcode == 1136 || operation.Opcode == 1139) && operation.Receipt != null)
            { ValidateReceipt(); state = ReadState(ctx, goal, Fresh(goal)); }
            if (operation.Pending || operation.Status == "ERROR") { state.Gate = operation.Status; state.Reason = operation.Reason; state.Action = ""; state.Done = false; }
            state.CleanupAction = OwnedCleanup(ctx, state, goal);
        }
        internal void CaptureFailed(SharedDevelopmentContext ctx, Exception error)
        {
            // A description/schema failure belongs to this capability. Keep a
            // separately assessed normal close available for our own panels.
            DevelopmentCommand goal = target ?? session.Goal;
            try { state = ReadState(ctx, goal, Fresh(goal), false); state.CleanupAction = OwnedCleanup(ctx, state, goal); }
            catch (Exception) { state = new State(); }
            state.Gate = "BLOCKED"; state.Reason = error is ProbeFailure ? error.Message : error.GetType().Name;
            state.Action = ""; state.Done = false;
        }
        private static bool Valid(DevelopmentCommand command)
        { return command != null && command.ValidId() && command.TargetId == 0 && command.DesiredLevel >= 0 && command.DesiredLevel <= 100 && command.Mode == "fruit" && command.MaxRequests >= 1 && command.MaxMoney == 0 && command.MaxStone == 0 && command.MaxFood >= 0 && command.MaxFood <= Int32.MaxValue; }
        private State ReadState(SharedDevelopmentContext ctx, DevelopmentCommand command, bool fresh, bool descriptions = true)
        {
            State s = new State();
            s.Ui = ctx.Find("SpiritUI"); s.Parent2 = ctx.Find("SpiritUI_2"); s.Parent3 = ctx.Find("SpiritUI_3"); s.Rankup = ctx.Find("SpiritRankupUI");
            s.Gate = ctx.Panels(s.Ui, s.Parent2, s.Parent3, s.Rankup, ctx.FindCapitalDemon());
            s.Spirit = D.Read(ctx.Character, "spirit"); s.Food = D.Long(D.Read(ctx.Character, "spirit_food")); s.Gold = D.Long(D.Read(ctx.Character, "gold")); s.Spent = fresh ? 0 : spentFood;
            if (s.Spirit == null) { s.Gate = "BLOCKED"; s.Reason = "tribute_spirit_not_unlocked"; return s; }
            s.Level = D.Int(D.Read(s.Spirit, "level")); s.Experience = D.UInt(D.Read(s.Spirit, "experience"));
            s.ExpTemplate = D.Template("exp_require_spirit", (uint)s.Level);
            if (s.ExpTemplate != null) { if (D.Int(D.Read(s.ExpTemplate, "level")) != s.Level) throw new ProbeFailure("tribute_exp_template_identity"); s.NextExp = D.UInt(D.Read(s.ExpTemplate, "exp")); }
            if (D.Visible(s.Parent3)) { s.Parent = s.Parent3; s.ParentKind = "spirit3"; }
            if (D.Visible(s.Parent2)) { if (s.Parent != null) { s.Gate = "BLOCKED"; s.Reason = "tribute_multiple_parents"; return s; } s.Parent = s.Parent2; s.ParentKind = "spirit2"; }
            if (D.Visible(s.Ui)) { s.FoodType = D.Int(D.Read(s.Ui, "food_type")); s.Page = D.Int(D.Read(s.Ui, "m_iPage")); s.Refreshing = D.Bool(D.Read(s.Ui, "m_bRefreshing")); s.Close = D.Read(s.Ui, "OnClose") as Delegate; if (s.Parent == null) s.ParentKind = "direct"; }
            if (D.Visible(s.Rankup)) s.RankupClose = D.Read(s.Rankup, "OnClose") as Delegate;
            s.NestedOwned = s.Parent != null && object.ReferenceEquals(D.Static(s.Parent.GetType(), "my_Subui"), s.Ui) && object.ReferenceEquals(ctx.SubUi, s.Parent);
            if (!Valid(command)) { s.Gate = "BLOCKED"; s.Reason = "tribute_typed_fruit_target_required"; return s; }
            s.DesiredLevel = command.DesiredLevel; s.TributeRequests = fresh ? 0 : tributeRequests;
            if (s.Gate != "PASS") { s.Reason = s.Gate; return s; }
            if (s.Food < 0 || s.Gold < 0 || s.Level < 1 || s.Level > 100) { s.Gate = "BLOCKED"; s.Reason = "tribute_resource_or_level_invalid"; return s; }
            if (!ctx.Network.Idle) { s.Gate = "WAITING_NETWORK"; s.Reason = "tribute_network_not_idle"; return s; }
            if ((D.Visible(s.Ui) && D.Loading(s.Ui)) || D.Loading(s.Rankup) || (s.Parent != null && D.Loading(s.Parent)) ||
                (object.ReferenceEquals(ctx.SubUi, s.Ui) && !D.Visible(s.Ui) && !D.Visible(s.Rankup)) || (object.ReferenceEquals(ctx.SubUi, s.Parent2) && !D.Visible(s.Parent2)) || (object.ReferenceEquals(ctx.SubUi, s.Parent3) && !D.Visible(s.Parent3)) ||
                (s.Parent != null && object.ReferenceEquals(D.Static(s.Parent.GetType(), "my_Subui"), s.Ui) && !D.Visible(s.Ui)))
            { s.Gate = "WAITING_UI"; s.Reason = "tribute_normal_ui_loading"; return s; }
            if (s.Refreshing) { s.Gate = "WAITING_UI"; s.Reason = "tribute_refreshing"; return s; }
            // Check exhaustion before food_type: the normal response may switch
            // the UI to premium when the last peach was consumed.
            if (s.DesiredLevel > 0 && s.Level >= s.DesiredLevel) { s.Done = true; s.Reason = "tribute_target_reached"; }
            else if (s.Food == 0) { s.Done = true; s.Reason = "tribute_peaches_exhausted"; }
            else if (s.TributeRequests >= command.MaxRequests) { s.Done = true; s.Reason = "tribute_request_budget_reached"; }
            else if (s.Level == 100) { s.Done = true; s.Reason = "tribute_level_cap_reached"; }
            else if (s.ExpTemplate == null || s.NextExp == 0) { s.Gate = "BLOCKED"; s.Reason = "tribute_exp_template_unavailable"; return s; }
            // An owned popup must be acknowledged even if its response reached
            // the target. Confirmation executes the game's original callback.
            if (D.Visible(s.Rankup))
            {
                if (!operation.ServerProgressObserved || operation.Receipt == null || operation.Receipt.Opcode != 1139 || requestBefore == null || !object.ReferenceEquals(requestBefore.Spirit, s.Spirit) || !object.ReferenceEquals(requestBefore.Parent, s.Parent) || rankupConfirmed || !CanConfirm(ctx, s))
                { s.Gate = "BLOCKED"; s.Reason = "tribute_rankup_popup_not_owned_or_bound"; s.Done = false; return s; }
                s.Action = "tribute_rankup_confirm"; return s;
            }
            if (s.Done) { if (CanClose(ctx, s)) s.Action = "tribute_close"; else if (CanCloseParent(ctx, s)) s.Action = "tribute_parent_close"; return s; }
            if (command.MaxFood < s.Food + s.Spent) { s.Gate = "BLOCKED"; s.Reason = "tribute_food_budget_unavailable"; return s; }
            if (D.Visible(s.Ui))
            {
                if (!CanUse(ctx, s)) { s.Gate = "BLOCKED"; s.Reason = "tribute_ui_binding_or_interaction"; return s; }
                if (s.Level % 10 == 0 && s.Experience >= s.NextExp) { s.Action = "tribute_rankup"; return s; }
                if (s.FoodType != 0) { s.Gate = "BLOCKED"; s.Reason = "tribute_premium_mode_forbidden"; return s; }
                s.Action = "tribute_train"; return s;
            }
            if (s.Parent != null)
            {
                if (!ParentReady(ctx, s)) { s.Gate = "BLOCKED"; s.Reason = "tribute_parent_binding_or_interaction"; return s; }
                if (descriptions && !ReadDescriptions(s))
                { s.Gate = "WAITING_UI"; s.Reason = "tribute_dragon_descriptions_initializing"; return s; }
                s.Action = "tribute_panel_open"; return s;
            }
            if (ctx.SubUi != null) { s.Gate = "BLOCKED"; s.Reason = "tribute_existing_subui"; return s; }
            s.ParentKind = MainCheck("checkSpirit3", "spirit3") ? "spirit3" : MainCheck("checkSpirit2", "spirit2") ? "spirit2" : MainCheck("checkSpirit", "army_spirit") ? "direct" : "none";
            if (s.ParentKind == "none") { s.Gate = "BLOCKED"; s.Reason = "tribute_menu_locked"; return s; }
            s.Action = "tribute_open"; return s;
        }
        private static bool MainCheck(string method, string menu)
        { return (bool)D.Method(Model.GameType("MainUI"), method, new Type[] { typeof(string) }, true, typeof(bool)).Invoke(null, new object[] { menu }); }
        private static bool ReadDescriptions(State s)
        {
            IList head = D.Read(s.Parent, "DragonHead") as IList, head2 = D.Read(s.Parent, "DragonHead2") as IList;
            if (head == null || head2 == null || head.Count != head2.Count || (head.Count != 0 && head.Count != 8))
                throw new ProbeFailure("tribute_dragon_descriptions_schema");
            // SpiritUI_2 fills these lists in Update(), after Show() has made
            // the parent visible. Wait for that first update, without opening
            // the nested panel with incomplete descriptions.
            if (head.Count == 0) return false;
            s.Head = Descriptions(head); s.Head2 = Descriptions(head2); return true;
        }
        private static string Descriptions(IList values)
        {
            string result = "";
            foreach (object value in values)
            {
                object description = D.Read(value, "specdesc");
                if (description != null && !(description is string)) throw new ProbeFailure("tribute_dragon_description_schema");
                string text = description as string;
                // Locked DragonData entries leave specdesc null. The stock
                // tribute button concatenates those entries as empty strings.
                if (text != null && text.Length > 4096) throw new ProbeFailure("tribute_dragon_description_bound");
                result += text;
            }
            return result;
        }
        private string OwnedCleanup(SharedDevelopmentContext ctx, State s, DevelopmentCommand goal)
        {
            if (goal == null || session.Goal == null || goal.OperationId != session.Goal.OperationId ||
                operation.OperationId != goal.OperationId || operation.Pending ||
                (operation.Status != "OBSERVED" && operation.Status != "ERROR") || !ctx.SameOwnership(openedContext)) return "";
            if (object.ReferenceEquals(openedUi, s.Ui) && CanClose(ctx, s)) return "tribute_close";
            if (object.ReferenceEquals(openedParent, s.Parent) && openedParent != null && CanCloseParent(ctx, s)) return "tribute_parent_close";
            return "";
        }
        private static bool ParentReady(SharedDevelopmentContext ctx, State s)
        {
            if (s.Parent == null || !D.Enabled(s.Parent) || D.Loading(s.Parent) || !object.ReferenceEquals(ctx.SubUi, s.Parent) || !object.ReferenceEquals(D.Read(s.Parent, "m_spirit"), s.Spirit) || D.Static(s.Parent.GetType(), "my_Subui") != null || D.Read(s.Parent, "OnClose") != null) return false;
            if (s.ParentKind == "spirit3") return !D.Bool(D.Read(s.Parent, "subwindow"));
            return !D.Bool(D.Read(s.Parent, "showskill")) && !D.Bool(D.Read(s.Parent, "flying"));
        }
        private static bool CloseBound(State s)
        { return s.Close != null && s.Close.GetInvocationList().Length == 1 && object.ReferenceEquals(s.Close.Target, s.Parent) && s.Close.Method.DeclaringType == s.Parent.GetType() && s.Close.Method.Name == "UpClosed" && s.Close.Method.ReturnType == typeof(void) && s.Close.Method.GetParameters().Length == 0; }
        private static bool CanUse(SharedDevelopmentContext ctx, State s)
        {
            if (D.Visible(s.Rankup) || !D.Enabled(s.Ui) || D.Loading(s.Ui) || s.Refreshing || !object.ReferenceEquals(D.Read(s.Ui, "m_spirit"), s.Spirit) || D.Static(s.Ui.GetType(), "my_Subui") != null) return false;
            if (s.Parent == null) return s.Level <= 40 && s.Page == 0 && object.ReferenceEquals(ctx.SubUi, s.Ui) && s.Close == null;
            return s.Level >= 41 && s.NestedOwned && !D.Enabled(s.Parent) && !D.Loading(s.Parent) && object.ReferenceEquals(D.Read(s.Parent, "m_spirit"), s.Spirit) && CloseBound(s);
        }
        private static bool CanClose(SharedDevelopmentContext ctx, State s) { return ctx.Panels(s.Ui, s.Parent2, s.Parent3, s.Rankup, ctx.FindCapitalDemon()) == "PASS" && ctx.Network.Idle && CanUse(ctx, s); }
        private static bool CanCloseParent(SharedDevelopmentContext ctx, State s) { return !D.Visible(s.Ui) && !D.Visible(s.Rankup) && ctx.Panels(s.Ui, s.Parent2, s.Parent3, s.Rankup, ctx.FindCapitalDemon()) == "PASS" && ctx.Network.Idle && ParentReady(ctx, s); }
        private static bool CanConfirm(SharedDevelopmentContext ctx, State s)
        {
            if (ctx.Panels(s.Ui, s.Parent2, s.Parent3, s.Rankup, ctx.FindCapitalDemon()) != "PASS" || !ctx.Network.Idle || !D.Enabled(s.Rankup) || D.Loading(s.Rankup) || !object.ReferenceEquals(D.Read(s.Rankup, "m_spirit"), s.Spirit)) return false;
            Delegate close = s.RankupClose;
            if (close == null || close.GetInvocationList().Length != 1 || close.Method.ReturnType != typeof(void) || close.Method.GetParameters().Length != 0) return false;
            // DelegateFuncUI.Set() binds MainUI_spirit2 to MainUI._spirit2.
            if (s.Level == 41) return s.Parent == null && object.ReferenceEquals(ctx.SubUi, s.Ui) && !D.Visible(s.Ui) && close.Target == null && close.Method.DeclaringType == Model.GameType("MainUI") && close.Method.Name == "_spirit2";
            bool bound = s.Parent == null ? s.Level <= 40 && object.ReferenceEquals(ctx.SubUi, s.Ui) && s.Page == 0 && s.Close == null : s.NestedOwned && CloseBound(s);
            return bound && D.Visible(s.Ui) && !D.Loading(s.Ui) && object.ReferenceEquals(D.Read(s.Ui, "m_spirit"), s.Spirit) && object.ReferenceEquals(close.Target, s.Ui) && close.Method.DeclaringType == Model.FirstType("__BaseUI") && close.Method.Name == "EnableAll";
        }
        internal bool Queue(DevelopmentCommand command, out string why)
        {
            why = ""; if (context == null || !Valid(command) || operation.Pending || operation.Status == "ERROR" || !context.Network.Idle) { why = "tribute_context_or_command_unavailable"; return false; }
            State s = ReadState(context, command, Fresh(command), command.Action != "tribute_close" && command.Action != "tribute_parent_close");
            bool allowed = command.Action == "tribute_close" ? CanClose(context, s) : command.Action == "tribute_parent_close" ? CanCloseParent(context, s) : s.Gate == "PASS" && s.Action == command.Action;
            if (!allowed) { why = "tribute_action_unavailable:" + s.Reason; return false; }
            return session.Queue(command, context, s, s.Stamp(), out why);
        }
        internal bool ExecuteGui(DevelopmentCommand command, DevelopmentPublication publish, out string why)
        {
            object anchor; string stamp; if (!session.Take(command, context, out anchor, out stamp, out why)) return false;
            State before = anchor as State, now = ReadState(context, command, Fresh(command), command.Action != "tribute_close" && command.Action != "tribute_parent_close");
            bool allowed = command.Action == "tribute_close" ? CanClose(context, now) : command.Action == "tribute_parent_close" ? CanCloseParent(context, now) : now.Gate == "PASS" && now.Action == command.Action;
            if (before == null || now.Stamp() != stamp || !object.ReferenceEquals(before.Spirit, now.Spirit) || !object.ReferenceEquals(before.Parent, now.Parent) || !object.Equals(before.Close, now.Close) || !object.Equals(before.RankupClose, now.RankupClose) || !allowed) { why = "tribute_gui_context_or_resource_changed"; return false; }
            if (Fresh(command)) { operation = new DevelopmentOperation { OperationId = command.OperationId }; spentFood = 0; tributeRequests = 0; openedContext = null; openedUi = null; openedParent = null; }
            int opcode = command.Action == "tribute_train" ? 1136 : command.Action == "tribute_rankup" ? 1139 : 0;
            // MaxRequests limits peach tributes, while operation.Requests keeps
            // its original meaning: every network request, including rankups.
            if (!operation.Begin(context, command, opcode, Int32.MaxValue, delegate { session.Commit(command); target = command.Copy(); if (opcode == 1136) { tributeRequests++; now.TributeRequests = tributeRequests; } state = now; publish(); }, out why)) return false;
            if (opcode != 0) { validatedReceipt = false; requestBefore = now; }
            if (opcode == 1139) rankupConfirmed = false;
            try
            {
                if (command.Action == "tribute_open")
                {
                    D.MainBranch(now.ParentKind == "spirit3" ? "_spirit3" : now.ParentKind == "spirit2" ? "_spirit2" : "_army_spirit");
                    openedContext = context;
                    openedParent = now.ParentKind == "spirit3" ? now.Parent3 : now.ParentKind == "spirit2" ? now.Parent2 : null;
                    openedUi = openedParent == null ? now.Ui : null;
                }
                else if (command.Action == "tribute_panel_open") { OpenPanel(now); openedContext = context; openedUi = now.Ui; }
                else if (opcode == 1136)
                {
                    // Re-read the exact serialized selector immediately before
                    // invoking; false/count alone cannot prohibit gold here.
                    if (D.UInt(D.Read(now.Ui, "food_type")) != 0 || D.Long(D.Read(context.Character, "spirit_food")) <= 0) { operation.Unknown("tribute_premium_or_no_peaches_before_send"); why = operation.Reason; return false; }
                    return operation.InvokeNormal(context, now.Ui, "sc_spirit_train_0x470", delegate
                    {
                        if (D.UInt(D.Read(now.Ui, "food_type")) != 0 || D.Long(D.Read(context.Character, "spirit_food")) != now.Food || D.Long(D.Read(context.Character, "gold")) != now.Gold)
                            throw new ProbeFailure("tribute_selector_or_resources_changed_before_send");
                        D.Method(now.Ui.GetType(), "cs_spirit_train_0x470", new Type[] { typeof(bool), typeof(uint) }, false, typeof(void)).Invoke(now.Ui, new object[] { false, 1u });
                    }, out why);
                }
                else if (opcode == 1139)
                {
                    return operation.InvokeNormal(context, now.Ui, "sc_spirit_levelup_0x473", delegate
                    {
                        if ((command.DesiredLevel > 0 && command.DesiredLevel <= now.Level) || D.Long(D.Read(context.Character, "spirit_food")) != now.Food || D.Long(D.Read(context.Character, "gold")) != now.Gold || D.Int(D.Read(now.Spirit, "level")) != now.Level || D.UInt(D.Read(now.Spirit, "experience")) != now.Experience)
                            throw new ProbeFailure("tribute_rankup_target_or_resources_changed_before_send");
                        D.Call(now.Ui, "cs_spirit_levelup_0x473");
                    }, out why);
                }
                else if (command.Action == "tribute_rankup_confirm") { rankupConfirmed = true; D.Call(now.Rankup, "Hide"); now.RankupClose.DynamicInvoke(new object[0]); }
                else if (command.Action == "tribute_close")
                {
                    D.Call(now.Ui, "Hide");
                    if (now.Parent == null) SetMainNull();
                    else { now.Close.DynamicInvoke(new object[0]); SetNestedNull(Model.GameType("SpiritUI_2")); SetNestedNull(Model.FirstType("SpiritUI_3")); }
                    openedUi = null;
                }
                else if (command.Action == "tribute_parent_close") { D.Call(now.Parent, "Hide"); SetMainNull(); SetNestedNull(now.ParentKind == "spirit3" ? Model.FirstType("SpiritUI_3") : Model.GameType("SpiritUI_2")); openedParent = null; }
                else throw new ProbeFailure("tribute_action_unsupported");
                operation.Invoked(context, now.Ui, ""); return true;
            }
            catch (Exception) { operation.Unknown("tribute_normal_branch_threw"); why = operation.Reason; return false; }
        }
        private static void OpenPanel(State s)
        {
            Type baseUi = Model.FirstType("__BaseUI"), callback = Model.FirstType("__BaseUI+delegate_OnClose");
            Delegate close = Delegate.CreateDelegate(callback, s.Parent, D.Method(s.Parent.GetType(), "UpClosed", Type.EmptyTypes, false, typeof(void)));
            bool open = (bool)D.Method(s.Parent.GetType(), "handle_sub_ui", new Type[] { baseUi }, true, typeof(bool)).Invoke(null, new object[] { s.Ui });
            if (!open) throw new ProbeFailure("tribute_normal_nested_open_failed");
            D.Call(s.Parent, "Disable");
            D.Method(s.Ui.GetType(), "MyLoading", new Type[] { callback, typeof(string), typeof(string) }, false, typeof(void)).Invoke(s.Ui, new object[] { close, s.Head, s.Head2 });
        }
        private static void SetNestedNull(Type parent)
        { D.Method(parent, "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null }); }
        private static void SetMainNull()
        {
            Delegate set = D.Static(Model.FirstType("DelegateFunc"), "MainUI_SetSubUI") as Delegate;
            if (set == null || set.GetInvocationList().Length != 1 || set.Target != null || set.Method.DeclaringType != Model.GameType("DelegateFuncUI") || set.Method.Name != "MainUI_SetSubUI") throw new ProbeFailure("tribute_close_delegate_binding");
            set.DynamicInvoke(new object[] { null });
        }
        private void ValidateReceipt()
        {
            validatedReceipt = true; DevelopmentPacket p = operation.Receipt; State b = requestBefore;
            if (b == null || !object.ReferenceEquals(b.Spirit, state.Spirit) || p.Result != 0 || p.Food > b.Food || p.Gold != 0 || state.Gold != b.Gold || state.Food != b.Food - p.Food || p.Level < b.Level || p.Level > 100 || state.Level != p.Level || state.Experience != p.Experience || (p.Level == b.Level && p.Experience <= b.Experience) || state.Refreshing)
            { operation.Unknown("tribute_response_progress_or_resource_mismatch"); return; }
            if (operation.Opcode == 1136 ? p.Food == 0 : p.Food != 0 || p.Level != b.Level + 1 || b.Level % 10 != 0)
            { operation.Unknown("tribute_response_cost_or_rankup_mismatch"); return; }
            spentFood += p.Food; operation.ServerProgressObserved = true;
        }
        internal void Stop(string why) { session.Stop(); operation.Stop(why); }
        internal string Json() { return state.Json(operation); }
    }
}
