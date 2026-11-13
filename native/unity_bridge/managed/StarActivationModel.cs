using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // Only the sequential normal activation request (514), never exchange/refine.
    internal sealed class StarActivationModel
    {
        private sealed class State
        {
            internal Component Ui, Special, Formation;
            internal object Next;
            internal string Gate = "PASS", Reason = "", Action = "", RewardKind = "";
            internal bool Done, Animating, LightingMarked, Sliding, FadingIn, FadingOut;
            internal int Level, Page = -1, Selected = -1, RewardId;
            internal long Practice, Gold, Cost, Spent;
            internal string Json(DevelopmentOperation op)
            {
                return "{\"schema\":1,\"gate\":" + D.Q(D.GateKind(Gate)) + ",\"reason\":" + D.Q(D.GateReason(Gate, Reason)) + ",\"done\":" + D.B(Done) +
                    ",\"level\":" + Level + ",\"practice\":" + Practice + ",\"gold\":" + Gold + ",\"next_cost\":" + (Next == null ? "null" : Cost.ToString()) + ",\"spent_practice\":" + Spent +
                    ",\"available_action\":" + D.Q(Action) + ",\"ui\":{\"star\":" + D.UiJson(Ui) + ",\"special\":" + D.UiJson(Special) + ",\"formation\":" + D.UiJson(Formation) + ",\"page\":" + Page + ",\"selected\":" + Selected + ",\"animating\":" + D.B(Animating) + ",\"lighting_marker_set\":" + D.B(LightingMarked) + ",\"zodiac_sliding\":" + D.B(Sliding) + ",\"fadein_marker_set\":" + D.B(FadingIn) + ",\"fadeout_marker_set\":" + D.B(FadingOut) + "},\"operation\":" + op.Json() + "}";
            }
            internal string Stamp() { return Json(new DevelopmentOperation()) + ":" + RewardKind + ":" + RewardId; }
        }
        private readonly DevelopmentSession session = new DevelopmentSession();
        private DevelopmentOperation operation = new DevelopmentOperation();
        private DevelopmentCommand target;
        private SharedDevelopmentContext context;
        private State state = new State(), requestBefore, closeBefore;
        private SharedDevelopmentContext closeContext;
        private DevelopmentCommand closeCommand;
        private long spent;
        private bool validated, rewardDismissed;
        internal bool Pending { get { return operation.Pending; } }
        internal bool MutationAttempted { get { return operation.MutationAttempted; } }
        internal void ObserveTarget(DevelopmentCommand command) { target = command.Copy(); }
        private bool Fresh(DevelopmentCommand c) { return c != null && (session.Goal == null || session.Goal.OperationId != c.OperationId); }
        private static bool Valid(DevelopmentCommand c)
        { return c != null && c.ValidId() && c.TargetId == 0 && c.DesiredLevel == 0 && c.Mode == "practice" && c.MaxRequests >= 1 && c.MaxRequests <= 196 && c.MaxPractice >= 0 && c.MaxPractice <= Int32.MaxValue && c.MaxFood == 0 && c.MaxStone == 0 && c.MaxMoney == 0; }
        internal void Capture(SharedDevelopmentContext ctx)
        {
            context = ctx; operation.Observe(ctx);
            DevelopmentCommand goal = target ?? session.Goal;
            state = ReadState(ctx, goal, Fresh(goal));
            if (!validated && operation.Status == "OBSERVED" && operation.Opcode == 514 && operation.Receipt != null)
            {
                validated = true; DevelopmentPacket p = operation.Receipt; State b = requestBefore;
                if (b == null || !object.ReferenceEquals(b.Ui, state.Ui) || p.Level != b.Level + 1 || state.Level != p.Level || state.Practice != p.Practice || state.Practice != b.Practice - b.Cost || state.Gold != b.Gold)
                    operation.Unknown("star_response_progress_or_resource_mismatch");
                else { spent += b.Cost; operation.ServerProgressObserved = true; }
                state = ReadState(ctx, goal, Fresh(goal));
            }
            if (operation.Pending || operation.Status == "ERROR") { state.Gate = operation.Status; state.Reason = operation.Reason; state.Done = false; state.Action = ""; }
        }
        private State ReadState(SharedDevelopmentContext ctx, DevelopmentCommand c, bool fresh)
        {
            State s = new State();
            s.Ui = ctx.Find("StarTechUI"); s.Special = ctx.Find("SpecialGetUI"); s.Formation = ctx.Find("FormationUI");
            s.Level = D.Int(D.Read(ctx.Character, "star_tech")); s.Practice = D.Long(D.Read(ctx.Character, "practice")); s.Gold = D.Long(D.Read(ctx.Character, "gold")); s.Spent = fresh ? 0 : spent;
            if (!Valid(c)) { s.Gate = "BLOCKED"; s.Reason = "star_typed_practice_target_required"; return s; }
            if (s.Level < 0 || s.Level > 196 || s.Practice < 0 || s.Gold < 0) { s.Gate = "BLOCKED"; s.Reason = "star_resource_or_level_invalid"; return s; }
            s.Next = D.Template("star_tech", (uint)(s.Level + 1));
            if (s.Next != null) { if (D.Int(D.Read(s.Next, "id")) != s.Level + 1) throw new ProbeFailure("star_template_identity"); s.Cost = D.Long(D.Read(s.Next, "require_practice")); }
            if (D.Visible(s.Ui))
            {
                s.Page = D.Int(D.Read(s.Ui, "ui_type")); s.Selected = D.Int(D.Read(s.Ui, "current_tech"));
                s.LightingMarked = Convert.ToSingle(D.Read(s.Ui, "new_tech_lighting_up")) >= 0;
                s.Sliding = D.Bool(D.Read(s.Ui, "zodiac_sliding"));
                s.FadingIn = Convert.ToSingle(D.Read(s.Ui, "fadeinStartTime")) >= 0;
                s.FadingOut = Convert.ToSingle(D.Read(s.Ui, "fadeoutStartTime")) >= 0;
                // Hide cancels light_star without clearing its lighting marker.
                // Show restores selection; the normal activation button uses
                // Selected == Level, not that potentially stale cosmetic marker.
                s.Animating = (s.LightingMarked && s.Selected != s.Level) || s.Sliding || s.FadingIn || s.FadingOut;
            }
            // Completion is an observed postcondition of our own close, not
            // permission for another action. A leftover hover must not undo it.
            if (CompletedOwnClose(ctx, c, s)) { s.Done = true; s.Reason = closeBefore.Reason; return s; }
            // Cap notification belongs to the game. Never acknowledge into GeniusUI.
            if (s.Level == 196) { s.Done = true; s.Reason = "star_maxed"; return s; }
            s.Gate = ctx.Panels(s.Ui, s.Special, PassiveTooltip(ctx, s));
            if (s.Gate != "PASS") { s.Reason = s.Gate; return s; }
            if (!ctx.Network.Idle) { s.Gate = "WAITING_NETWORK"; s.Reason = "star_network_not_idle"; return s; }
            if (D.Loading(s.Ui) || D.Loading(s.Special)) { s.Gate = "WAITING_UI"; s.Reason = "star_normal_ui_loading"; return s; }
            if (D.Visible(s.Special))
            {
                s.RewardKind = D.Read(s.Special, "showtype") as string; s.RewardId = D.Int(D.Read(s.Special, "tempid"));
                if (!OwnedReward(s)) { s.Gate = "BLOCKED"; s.Reason = "star_reward_not_owned"; return s; }
                s.Action = "star_reward_confirm"; return s;
            }
            if (s.Animating) { s.Gate = "WAITING_UI"; s.Reason = "star_activation_animation"; return s; }
            if (s.Next == null) { s.Done = true; s.Reason = "star_no_next_activation"; }
            else if (s.Practice < s.Cost) { s.Done = true; s.Reason = "star_insufficient_practice"; }
            else if (!fresh && operation.Requests >= c.MaxRequests) { s.Done = true; s.Reason = "star_request_budget_reached"; }
            else if (s.Cost > c.MaxPractice - s.Spent) { s.Done = true; s.Reason = "star_practice_budget_reached"; }
            if (s.Done) { if (CanUse(ctx, s)) s.Action = "star_close"; return s; }
            if (D.Visible(s.Ui))
            {
                if (!CanUse(ctx, s)) { s.Gate = "BLOCKED"; s.Reason = "star_ui_binding_or_selection"; return s; }
                s.Action = "star_activate"; return s;
            }
            if (ctx.SubUi != null) { s.Gate = "BLOCKED"; s.Reason = "star_existing_subui"; return s; }
            bool unlocked = (bool)D.Method(Model.GameType("MainUI"), "checkStarTechUI", new Type[] { typeof(string) }, true, typeof(bool)).Invoke(null, new object[] { "army_star" });
            if (!unlocked) { s.Done = true; s.Reason = "star_no_next_activation"; return s; }
            s.Action = "star_open"; return s;
        }
        private static bool CanUse(SharedDevelopmentContext ctx, State s)
        { return D.Enabled(s.Ui) && !D.Loading(s.Ui) && !s.Animating && !D.Visible(s.Special) && object.ReferenceEquals(ctx.SubUi, s.Ui) && s.Page == 2 && s.Selected == s.Level && !D.Bool(D.Read(s.Ui, "m_bFromGuide")); }
        private bool CompletedOwnClose(SharedDevelopmentContext ctx, DevelopmentCommand c, State s)
        {
            return closeBefore != null && closeBefore.Done && closeBefore.Action == "star_close" && closeCommand != null &&
                !ctx.Paused && ctx.SameOwnership(closeContext) && session.Goal != null &&
                session.Goal.GoalKey() == c.GoalKey() && closeCommand.GoalKey() == c.GoalKey() &&
                operation.Status == "OBSERVED" && operation.Opcode == 0 && operation.Action == "star_close" && operation.OperationId == c.OperationId &&
                operation.Sequence == closeCommand.Sequence && operation.ExpectedRevision == closeCommand.ExpectedRevision &&
                object.ReferenceEquals(s.Ui, closeBefore.Ui) && object.ReferenceEquals(s.Special, closeBefore.Special) &&
                !D.Visible(s.Ui) && !D.Visible(s.Special) && !D.Visible(s.Formation) && ctx.SubUi == null &&
                s.Level == closeBefore.Level && s.Practice == closeBefore.Practice && s.Gold == closeBefore.Gold && s.Spent == closeBefore.Spent;
        }
        private static Component PassiveTooltip(SharedDevelopmentContext ctx, State s)
        {
            // Hover text produced by this exact star page does not disable its
            // normal activation button. Other owners and overlays stay gated.
            if (!D.Visible(s.Ui) || !object.ReferenceEquals(ctx.SubUi, s.Ui)) return null;
            Component tooltip = ctx.Find("TooltipUI");
            if (!D.Visible(tooltip) || D.Read(tooltip, "ttowner") as string != "StarTechUI") return null;
            Type type = Model.FirstType("TooltipUI");
            object owner = D.Static(type, "ownerUI");
            if (!object.ReferenceEquals(D.Static(type, "last_ownerUI"), s.Ui) ||
                (owner != null && !object.ReferenceEquals(owner, s.Ui))) return null;
            string flag = D.Read(tooltip, "TooltipFlag") as string, title = D.Read(tooltip, "tttitle") as string;
            if (!PassiveTooltipKind(flag) || !PassiveTooltipKind(title) || (flag == "" && title == "")) return null;
            if ((flag == "Content" || title == "Content") && !PassiveContentTooltip(tooltip)) return null;
            return tooltip;
        }
        private static bool PassiveTooltipKind(string kind)
        { return kind == "" || kind == "Skill" || kind == "Army" || kind == "StarTechSum" || kind == "Content"; }
        private static bool PassiveContentTooltip(Component tooltip)
        {
            // Content tooltips can contain clickable game links. Only the
            // plain parsed label path is passive; do not trust the text tag.
            object renderer = D.Read(tooltip, "m_tdContent");
            if (renderer == null || renderer.GetType() != Model.FirstType("TooltipDraw_Content")) return false;
            object section = D.Read(renderer, "text");
            if (section == null || section.GetType() != Model.FirstType("ScenarioSection")) return false;
            Type itemType = Model.FirstType("ContentText");
            IList rows = D.Read(section, "texts") as IList;
            if (rows == null || rows.GetType() != typeof(List<>).MakeGenericType(itemType) || rows.Count > 128) return false;
            foreach (object row in rows)
                if (row == null || row.GetType() != itemType || D.Int(D.Read(row, "type")) != 0 ||
                    (D.Int(D.Read(row, "flag")) & 4) != 0 || !(D.Read(row, "text") is string)) return false;
            return true;
        }
        private bool OwnedReward(State s)
        {
            if (rewardDismissed || !operation.ServerProgressObserved || requestBefore == null || !object.ReferenceEquals(requestBefore.Ui, s.Ui) || !D.Enabled(s.Special) || D.Loading(s.Special) || operation.Receipt == null || operation.Receipt.Level != s.Level) return false;
            object template = D.Template("star_tech", (uint)s.Level);
            // AddStarTech's pinned source uses bonus3_type again for slot4.
            for (int i = 1; i <= 4; i++)
            {
                string type = D.Read(template, "bonus" + (i == 4 ? 3 : i) + "_type") as string;
                if ((type == "army_type" && s.RewardKind == "army" || type == "skill" && s.RewardKind == "skill") && D.Int(D.Read(template, "bonus" + i + "_param")) == s.RewardId) return true;
            }
            return false;
        }
        internal bool Queue(DevelopmentCommand c, out string why)
        {
            why = "";
            if (context == null || !Valid(c) || operation.Pending || operation.Status == "ERROR") { why = "star_context_or_command_unavailable"; return false; }
            State s = ReadState(context, c, Fresh(c));
            if (s.Gate != "PASS" || s.Action != c.Action) { why = "star_action_unavailable:" + s.Reason; return false; }
            return session.Queue(c, context, s, s.Stamp(), out why);
        }
        internal bool ExecuteGui(DevelopmentCommand c, DevelopmentPublication publish, out string why)
        {
            object anchor; string stamp;
            if (!session.Take(c, context, out anchor, out stamp, out why)) return false;
            State b = anchor as State, now = ReadState(context, c, Fresh(c));
            if (b == null || stamp != now.Stamp() || !object.ReferenceEquals(b.Ui, now.Ui) || !object.ReferenceEquals(b.Special, now.Special) || now.Gate != "PASS" || now.Action != c.Action) { why = "star_gui_context_or_resources_changed"; return false; }
            if (Fresh(c)) { operation = new DevelopmentOperation { OperationId = c.OperationId }; spent = 0; }
            int opcode = c.Action == "star_activate" ? 514 : 0;
            if (!operation.Begin(context, c, opcode, c.MaxRequests, delegate { session.Commit(c); target = c.Copy(); state = now; publish(); }, out why)) return false;
            closeBefore = null; closeContext = null; closeCommand = null;
            try
            {
                if (opcode == 514)
                {
                    requestBefore = now; validated = false; rewardDismissed = false;
                    return operation.InvokeNormal(context, now.Ui, "sc_upgrade_startech", delegate {
                        if (!CanUse(context, now) || D.Int(D.Read(context.Character, "star_tech")) != now.Level || D.Long(D.Read(context.Character, "practice")) != now.Practice || D.Long(D.Read(context.Character, "gold")) != now.Gold || now.Cost > c.MaxPractice - spent) throw new ProbeFailure("star_resources_changed_before_send");
                        D.Call(now.Ui, "cs_upgrade_startech");
                    }, out why);
                }
                if (c.Action == "star_open") D.MainBranch("_army_star");
                else if (c.Action == "star_close")
                {
                    closeBefore = now; closeContext = context; closeCommand = c.Copy();
                    D.Call(now.Ui, "Hide"); D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                }
                else if (c.Action == "star_reward_confirm") { rewardDismissed = true; D.Call(now.Special, "Hide"); }
                else throw new ProbeFailure("star_action_unsupported");
                operation.Invoked(context, now.Ui, ""); return true;
            }
            catch (Exception) { operation.Unknown("star_normal_branch_threw"); why = operation.Reason; return false; }
        }
        internal void Stop(string why) { session.Stop(); operation.Stop(why); }
        internal string Json() { return state.Json(operation); }
    }
}
