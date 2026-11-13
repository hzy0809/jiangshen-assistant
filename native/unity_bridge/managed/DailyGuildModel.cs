using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal sealed class DailyGuildModel
    {
        private sealed class Frame
        {
            internal SharedDevelopmentContext Context;
            internal Component Ui, Premium, Reward;
            internal object Guild, Member;
            internal ulong CharacterId, GuildId;
            internal uint Reset, DonationRefresh, Contribution;
            internal long Coins;
            internal int Gold, BoundGold, Draws;
            internal int? Donate, Lottery;
            internal int DonateRequired, LotteryRequired, DonateTaskRefresh, LotteryTaskRefresh;
            internal string DonateTaskReason, LotteryTaskReason;
            internal string Phase = "WAITING", Gate = "BLOCKED", Reason = "guild_unobserved";
            internal string RefreshGate;
            internal DailyTransport Connection;
            internal bool SkipPremiumPrompt;
            internal bool DonationButtonWaiting;
            internal int[] Prizes;
            internal int? Progress(int task) { return task == 18 ? Lottery : Donate; }
            internal int Required(int task) { return task == 18 ? LotteryRequired : DonateRequired; }
            internal int TaskRefresh(int task) { return task == 18 ? LotteryTaskRefresh : DonateTaskRefresh; }
            internal string TaskReason(int task) { return task == 18 ? LotteryTaskReason : DonateTaskReason; }
            internal string Stamp()
            {
                return CharacterId + ":" + GuildId + ":" + Reset + ":" + DonationRefresh + ":" +
                    Contribution + ":" + Coins + ":" + Gold + ":" + BoundGold + ":" + Draws + ":" +
                    Donate + ":" + Lottery + ":" + DonateRequired + ":" + LotteryRequired + ":" + DonateTaskRefresh + ":" + LotteryTaskRefresh + ":" +
                    DonateTaskReason + ":" + LotteryTaskReason + ":" +
                    SkipPremiumPrompt + ":" + DonationButtonWaiting + ":" + Phase + ":" + Gate + ":" + Reason + ":" + PrizeStamp();
            }
            private string PrizeStamp()
            {
                if (Prizes == null) return "";
                string[] ids = new string[Prizes.Length];
                for (int i = 0; i < ids.Length; i++) ids[i] = Prizes[i].ToString(CultureInfo.InvariantCulture);
                return String.Join(",", ids);
            }
            internal bool SameOwner(Frame previous)
            {
                return previous != null && Context != null && Context.SameOwnership(previous.Context) &&
                    CharacterId == previous.CharacterId && GuildId == previous.GuildId && Reset == previous.Reset &&
                    Guild != null && Member != null && object.ReferenceEquals(Ui, previous.Ui) &&
                    Connection != null && Connection.Same(previous.Connection);
            }
        }

        private Frame frame = new Frame(), admitted, owned, baseline;
        private Command queued, goal;
        private readonly Dictionary<string, bool> used = new Dictionary<string, bool>();
        private int revision, requestTick, opcode, lastRequestSequence;
        private int capturedFrame = -1;
        private string signature = "", status = "", operationReason = "";
        private bool invoked, ownedPremium, closed, rewardConfirmed;
        private bool requestIssued, replyReceived, pageConfirmed, processedWithoutReply;
        private int? handledProgress;
        private int? progress;
        private GuildReceipt receipt;
        private object responseHandler, progressHandler;
        private DevelopmentCallbackCapture responseCapture, progressCapture;
        private readonly DailyRefreshAdmission refresh;

        internal DailyGuildModel() : this(DailyRefreshGate.Read) { }
        internal DailyGuildModel(DailyRefreshAdmission refreshAdmission) { refresh = refreshAdmission; }

        internal bool Busy { get { return queued != null || goal != null && !closed || responseCapture != null || progressCapture != null; } }
        internal bool MutationAttempted { get { return invoked; } }
        internal bool Unresolved { get { return invoked && (status == "PENDING" || status == "UNKNOWN") || ownedPremium && !invoked; } }
        internal int Revision { get { return revision; } }

        internal void Capture(SharedDevelopmentContext context)
        {
            Frame previous = frame;
            try
            {
                frame = Read(context);
                capturedFrame = Time.frameCount;
                if (status == "PENDING") Settle();
                else if (status == "UNKNOWN") Detach();
                else if (status == "COMPLETED" || status == "PROCESSED" || status == "REJECTED")
                {
                    Detach();
                    if (goal != null && !closed && frame.SameOwner(owned) && frame.Phase == "MAIN" &&
                        frame.Gate == "PASS" && frame.Context.SubUi == null && !D.Visible(frame.Ui)) closed = true;
                }
                else if (goal != null && !closed && frame.Phase == "MAIN" && status != "UNKNOWN" && (!ownedPremium || invoked))
                {
                    if (!frame.SameOwner(owned) || invoked) Unknown("guild_external_close_unverified");
                    else { closed = true; status = "REJECTED"; operationReason = "guild_panel_closed_without_request"; }
                }
                else if (ownedPremium && !invoked && status == "OPEN" && (frame.Phase == "DONATE" || frame.Phase == "MAIN"))
                    Unknown("guild_premium_dialog_closed_without_owned_request");
            }
            catch (Exception error)
            {
                frame = new Frame { Reason = error is ProbeFailure ? error.Message : "guild_capture_failed" };
                if (status == "PENDING") Unknown("guild_capture_failed_after_request");
            }
            string next = frame.Stamp() + ":" + String.Join(",", Actions()) + ":" + OperationJson();
            if (next != signature || !object.ReferenceEquals(previous.Guild, frame.Guild) || !object.ReferenceEquals(previous.Member, frame.Member) ||
                (previous.Connection == null ? frame.Connection != null : !previous.Connection.Same(frame.Connection)))
            {
                if (revision == 999999999) throw new ProbeFailure("guild_revision_exhausted");
                revision++; signature = next;
            }
        }

        private Frame Read(SharedDevelopmentContext ctx)
        {
            Frame f = new Frame { Context = ctx };
            if (ctx == null || ctx.Character == null) return f;
            f.CharacterId = Field<ulong>(ctx.Character, "id");
            f.GuildId = Field<ulong>(ctx.Character, "idguild");
            uint now, day;
            f.RefreshGate = refresh(ctx.Character, out now, out day, out f.Reset);
            if (f.RefreshGate != "") { f.Reason = f.RefreshGate; return f; }
            f.Connection = DailyTransport.Read(ctx.Client);
            if (f.Connection == null) { f.Reason = "guild_transport_unavailable"; return f; }
            if (f.GuildId == 0) { f.Reason = "guild_membership_required"; return f; }
            if (!Membership(ctx.Character, f.CharacterId, f.GuildId, out f.Guild, out f.Member))
            { f.Reason = "guild_membership_unverified"; return f; }

            f.Coins = D.Money(ctx.Character);
            f.Gold = Field<int>(ctx.Character, "gold");
            f.BoundGold = Field<int>(ctx.Character, "bind_gold");
            f.DonationRefresh = Field<uint>(f.Member, "donate_refresh");
            // DrawDonate's legacy NextOClock is strictly greater than 05:00.
            // The new cycle starts at equality, but the old button can still
            // be hidden during that second. Wait for the normal UI then.
            f.DonationButtonWaiting = now == f.Reset && f.DonationRefresh > (long)f.Reset - 86400;
            f.Contribution = Field<uint>(f.Member, "contribution");
            f.Draws = Field<byte>(f.Member, "guild_lottery_count");
            IDictionary tips = D.Read(ctx.Character, "consume_tips_") as IDictionary;
            if (tips == null) throw new ProbeFailure("guild_consume_tips_schema");
            f.SkipPremiumPrompt = tips.Contains(123u);
            f.Donate = TaskProgress(ctx.Character, 20, out f.DonateRequired, out f.DonateTaskRefresh, out f.DonateTaskReason);
            f.Lottery = TaskProgress(ctx.Character, 18, out f.LotteryRequired, out f.LotteryTaskRefresh, out f.LotteryTaskReason);
            f.Ui = ctx.Find("GuildUI_3");
            f.Premium = ctx.Find("ConsumeUI");
            f.Reward = ctx.GrowthRewardDialog();

            if (!D.Visible(f.Ui) && ctx.SubUi == null)
            { f.Phase = "MAIN"; f.Reason = ctx.Panels(); }
            else if (!object.ReferenceEquals(ctx.SubUi, f.Ui))
                f.Reason = "guild_foreign_panel";
            else if (!D.Visible(f.Ui) || D.Loading(f.Ui))
            { f.Gate = "WAITING_UI"; f.Reason = "guild_loading"; }
            else if (Field<bool>(f.Ui, "show_list") || Field<bool>(f.Ui, "need_close") ||
                Field<string>(f.Ui, "popup_type") != "" || D.Read(f.Ui, "m_guild_tree") as GameObject != null)
                f.Reason = "guild_foreign_subpage";
            else if (!object.ReferenceEquals(D.Read(f.Ui, "char_guild"), f.Guild) ||
                !object.ReferenceEquals(D.Read(f.Ui, "char_member"), f.Member))
            { f.Gate = "WAITING_UI"; f.Reason = "guild_panel_cache_pending"; }
            else
            {
                string page = Field<string>(f.Ui, "page"), subwindow = Field<string>(f.Ui, "subwindow");
                if (D.Visible(f.Premium) && page == "donate" && subwindow == "")
                { f.Phase = "PREMIUM"; f.Reason = ctx.RewardPanels(f.Ui, f.Premium); }
                else if (D.Visible(f.Reward) && (page == "donate" && subwindow == "" || page == "" && subwindow == "lottery"))
                { f.Phase = "REWARD"; f.Reason = ctx.RewardPanels(f.Ui, f.Reward); }
                else if (!D.Enabled(f.Ui) || Field<bool>(f.Ui, "lotting") || Field<bool>(f.Ui, "lotend"))
                { f.Gate = "WAITING_UI"; f.Reason = "guild_button_unavailable"; }
                else if (page == "" && subwindow == "")
                { f.Phase = "GUILD"; f.Reason = ctx.Panels(f.Ui); }
                else if (page == "donate" && subwindow == "")
                { f.Phase = "DONATE"; f.Reason = ctx.Panels(f.Ui); }
                else if (page == "" && subwindow == "lottery" && Field<int>(f.Ui, "cur_highlight") == -1)
                {
                    f.Phase = "LOTTERY";
                    // The request keeps its original board. A later guild
                    // level update changes the cache, not the ability to close.
                    if (!invoked) f.Prizes = ReadPrizes(f);
                    f.Reason = ctx.Panels(f.Ui);
                }
            }
            if (f.Reason == "PASS") { f.Gate = "PASS"; f.Reason = ""; }
            return f;
        }

        private static bool Membership(object character, ulong characterId, ulong guildId, out object guild, out object member)
        {
            guild = member = null;
            Type guildType = Model.FirstType("Database.Guild"), memberType = Model.FirstType("Database.GuildMember");
            if (guildId == 0 || Field<ulong>(character, "id") != characterId || Field<ulong>(character, "idguild") != guildId ||
                D.Bool(D.Static(guildType, "kicked"))) return false;
            IDictionary guilds = D.Read(character, "guilds") as IDictionary, members = D.Read(character, "members") as IDictionary;
            if (guilds == null || members == null) return false;
            guild = guilds[guildId]; member = members[characterId];
            return guild != null && guild.GetType() == guildType && Field<ulong>(guild, "id") == guildId && Field<bool>(guild, "active") &&
                member != null && member.GetType() == memberType && Field<ulong>(member, "id") == characterId && Field<ulong>(member, "idguild") == guildId;
        }

        private static T Field<T>(object owner, string name)
        {
            object value = D.Read(owner, name);
            if (!(value is T)) throw new ProbeFailure("guild_field_schema:" + name);
            return (T)value;
        }

        private static int? TaskProgress(object role, int task, out int required, out int refreshTime, out string why)
        {
            required = 0;
            refreshTime = 0;
            string prefix = "guild_daily_task_unverified:" + task + ":";
            why = prefix + "activity_dictionary";
            IDictionary acts = D.Read(role, "acts") as IDictionary, rewards = D.Read(role, "act_rewards") as IDictionary;
            if (acts == null || rewards == null || acts.Count > 255 || rewards.Count > 6) return null;
            object reward = rewards[5u];
            why = prefix + "category_5";
            if (reward == null || reward.GetType() != Model.FirstType("Database.Activity_Reward") || Field<byte>(reward, "idact_reward") != 5) return null;
            IDictionary listed = D.Read(reward, "acts") as IDictionary;
            why = prefix + "category_tasks";
            if (listed == null || listed.Count > 255) return null;
            object latest = acts[(uint)task], panel = listed[(uint)task];
            Type activity = Model.FirstType("Database.Activity");
            why = prefix + "latest_activity";
            if (latest == null || latest.GetType() != activity || Field<byte>(latest, "idactivity") != task) return null;
            why = prefix + "category_activity";
            if (panel == null || panel.GetType() != activity || Field<byte>(panel, "idactivity") != task) return null;
            object template = D.Read(latest, "temp");
            why = prefix + "activity_template";
            if (template == null || !object.ReferenceEquals(template, D.Read(panel, "temp")) ||
                Field<byte>(template, "id") != task || Field<byte>(template, "idreward") != 5 || !Field<bool>(template, "active")) return null;
            // ActivityUI routes with oneact.idactivity (18/20), not the
            // template's id_uibutton; the donation template uses button 22.
            required = Field<int>(template, "cond_param");
            int current = Field<byte>(latest, "condition_param");
            int latestRefresh = Field<int>(latest, "refresh_time");
            why = prefix + "required:" + required;
            if (required < 1 || required > 255) return null;
            why = prefix + "progress_cache:" + current + ":" + Field<byte>(panel, "condition_param");
            if (current != Field<byte>(panel, "condition_param")) return null;
            why = prefix + "refresh_cache:" + latestRefresh + ":" + Field<int>(panel, "refresh_time");
            if (latestRefresh != Field<int>(panel, "refresh_time")) return null;
            refreshTime = latestRefresh;
            why = "";
            return current;
        }

        private static int[] ReadPrizes(Frame f)
        {
            IList ids = D.Read(f.Ui, "ids") as IList, prizes = D.Read(f.Ui, "lots") as IList, qualities = D.Read(f.Ui, "lot_quality") as IList;
            if (ids == null || prizes == null || qualities == null || ids.Count != 8 || prizes.Count != 8 || qualities.Count < 8 ||
                Field<int>(f.Ui, "guild_level") != Field<ushort>(f.Guild, "level")) return null;
            int[] result = new int[8];
            for (int i = 0; i < result.Length; i++)
            {
                if (!(ids[i] is int) || (int)ids[i] < 0 || (int)ids[i] == 255 || prizes[i] == null ||
                    !(qualities[i] is uint) || (uint)qualities[i] > 6 || Array.IndexOf(result, (int)ids[i], 0, i) >= 0) return null;
                result[i] = (int)ids[i];
            }
            return result;
        }

        private static string Available(Frame f, int task, int type)
        {
            int? current = f.Progress(task);
            if (!current.HasValue) return f.TaskReason(task) ?? "guild_daily_task_unverified";
            if (current.Value >= f.Required(task)) return "guild_daily_task_complete";
            if (task == 18) return f.Draws <= 0 ? "guild_draws_exhausted" : f.Contribution < 400 ? "guild_contribution_insufficient" : "";
            if (f.DonationRefresh > f.Reset) return "guild_already_donated";
            if (f.DonationButtonWaiting) return "guild_donate_button_waiting_reset";
            if (type == 3) return f.Gold < 80 ? "guild_premium_insufficient" : "";
            return f.Coins < (type == 1 ? 1000 : 100000) ? "guild_coins_insufficient" : "";
        }

        private string[] Actions()
        {
            Frame f = frame;
            if (HandledCloseReady(f)) return new string[] { "close" };
            if (f.Gate != "PASS" || status == "UNKNOWN") return new string[0];
            if (goal == null || closed) return f.Phase == "MAIN" ? new string[] { "open" } : new string[0];
            if (!f.SameOwner(owned)) return new string[0];
            if (f.Phase == "REWARD" && !rewardConfirmed && ValidReward(f))
                return new string[] { "reward_confirm" };
            if (f.Phase == "PREMIUM" && ownedPremium && !invoked && BoundPremium(f) && Available(f, 20, 3) == "")
                return new string[] { "premium_confirm" };
            if (status == "PENDING" || ownedPremium && !invoked) return new string[0];
            if (f.Phase == "GUILD" && !invoked)
                return f.Progress(goal.GuildTaskId).HasValue ?
                    new string[] { goal.GuildTaskId == 18 ? "lottery_panel" : "donate_panel", "close" } :
                    new string[] { "close" };
            if (f.Phase == "DONATE" || f.Phase == "LOTTERY")
            {
                if (!invoked && Available(f, goal.GuildTaskId, goal.DonationType) == "" &&
                    (f.Phase == "DONATE" ? goal.GuildTaskId == 20 : goal.GuildTaskId == 18 && f.Prizes != null))
                    return new string[] { f.Phase == "DONATE" ? "donate" : "lottery", "close" };
                return new string[] { "close" };
            }
            return f.Phase == "GUILD" ? new string[] { "close" } : new string[0];
        }

        private static bool Callback(Delegate d, object target, Type declaring, string name, Type[] types)
        {
            return d != null && d.GetInvocationList().Length == 1 && object.ReferenceEquals(d.Target, target) &&
                d.Method.DeclaringType == declaring && d.Method == D.Method(declaring, name, types, target == null, typeof(void));
        }

        private bool ValidReward(Frame f)
        {
            return invoked && (pageConfirmed || receipt != null && receipt.Accepted &&
                (opcode == 301 ? receipt.ExactQuote(goal.DonationType) : baseline.Prizes != null && Array.IndexOf(baseline.Prizes, receipt.Result) >= 0)) &&
                D.Enabled(f.Reward) && !D.Loading(f.Reward) && Field<string>(f.Reward, "mine_ui") == "" && !Field<bool>(f.Reward, "autoClose") &&
                object.ReferenceEquals(D.Read(f.Reward, "m_prevUI"), f.Context.UiRoot) &&
                Callback(D.Read(f.Reward, "OnClose") as Delegate, f.Ui, f.Ui.GetType(), opcode == 301 ? "donate_bonus" : "ResumeUI", Type.EmptyTypes);
        }

        private static bool BoundPremium(Frame f)
        {
            object tip = D.Read(f.Premium, "tip");
            return D.Enabled(f.Premium) && !D.Loading(f.Premium) && Field<int>(f.Premium, "gold") == 80 &&
                !Field<bool>(f.Premium, "notip") && !f.SkipPremiumPrompt && D.Read(f.Premium, "OnOK_objs") == null &&
                tip != null && object.ReferenceEquals(tip, D.Template("consume_tip", 123u)) &&
                Callback(D.Read(f.Premium, "OnOK") as Delegate, f.Ui, f.Ui.GetType(), "GoldDonateOK", PremiumArgs);
        }
        private static readonly Type[] PremiumArgs = new Type[] { typeof(bool), typeof(object[]) };
        private static readonly Type[] ResponseArgs = new Type[] { typeof(MemoryStream), typeof(object) };

        private string LiveGate(Frame f, bool exactCaches = false)
        {
            if (!f.Context.SameOwnership(f.Context)) return "guild_role_or_client_changed";
            object guild, member;
            if (!Membership(f.Context.Character, f.CharacterId, f.GuildId, out guild, out member)) return "guild_membership_changed";
            if (exactCaches && (!object.ReferenceEquals(guild, f.Guild) || !object.ReferenceEquals(member, f.Member)))
                return "guild_gui_binding_changed";
            uint now, day, cycle;
            string gate = refresh(f.Context.Character, out now, out day, out cycle);
            if (gate != "") return gate;
            if (cycle != f.Reset) return "guild_refresh_cycle_changed";
            return f.Connection.Same(DailyTransport.Read(f.Context.Client)) ? "" : "guild_transport_changed";
        }

        private bool PublishCurrent(Frame f, DevelopmentPublication publish, out string why, int task = 0)
        {
            publish();
            why = LiveGate(f, true);
            if (why != "") return false;
            // The admitted frame predates ownership publication. A stock
            // activity callback can replace one cache before the GUI branch.
            if (task != 0)
            {
                int required, refreshTime;
                int? current = TaskProgress(f.Context.Character, task, out required, out refreshTime, out why);
                if (!current.HasValue) return false;
                if (current != f.Progress(task) || required != f.Required(task) || refreshTime != f.TaskRefresh(task))
                { why = "guild_daily_task_unverified:" + task + ":activity_changed_before_gui"; return false; }
            }
            return true;
        }

        private string Validate(Command c)
        {
            if (!Command.GuildActionAllowed(c) || c.ExpectedRevision != revision || c.GuildCharacterId != frame.CharacterId ||
                c.GuildId != frame.GuildId || frame.Gate != "PASS" && !(c.GuildAction == "close" && HandledCloseReady(frame))) return "guild_revision_role_or_gate_changed";
            if (c.GuildAction == "open")
            {
                if (used.ContainsKey(c.OperationId) || Busy) return "guild_operation_busy_or_replayed";
                string unavailable = Available(frame, c.GuildTaskId, c.DonationType);
                if (unavailable != "") return unavailable;
            }
            else if (goal == null || c.OperationId != goal.OperationId || c.GuildTaskId != goal.GuildTaskId ||
                c.DonationType != goal.DonationType || c.MaxPremium != goal.MaxPremium) return "guild_operation_choice_changed";
            return Array.IndexOf(Actions(), c.GuildAction) < 0 ? "guild_action_unavailable" : "";
        }

        internal bool Queue(Command command, out string why)
        {
            why = queued != null ? "guild_command_pending" : Validate(command);
            if (why != "") return false;
            queued = command; admitted = frame; return true;
        }

        internal bool ExecuteGui(Command command, DevelopmentPublication publish, out string why)
        {
            Command saved = queued; queued = null;
            why = Validate(command);
            Frame f = frame;
            if (why != "") return false;
            if (!object.ReferenceEquals(saved, command) || !f.SameOwner(admitted) || f.Stamp() != admitted.Stamp() ||
                !object.ReferenceEquals(f.Guild, admitted.Guild) || !object.ReferenceEquals(f.Member, admitted.Member) || !D.Gui)
            { why = "guild_gui_binding_changed"; return false; }

            switch (command.GuildAction)
            {
                case "open":
                    if (!PublishCurrent(f, publish, out why, command.GuildTaskId)) return false;
                    goal = command; owned = f; used.Add(command.OperationId, true);
                    closed = invoked = ownedPremium = rewardConfirmed = false;
                    status = "OPEN"; operationReason = ""; receipt = null; opcode = 0; baseline = null; progress = null;
                    requestIssued = replyReceived = pageConfirmed = processedWithoutReply = false; handledProgress = null;
                    D.MainBranch("_guild");
                    return true;
                case "donate_panel":
                    if (!PublishCurrent(f, publish, out why, command.GuildTaskId)) return false;
                    D.Call(f.Context.Character, "make_member_sort");
                    D.Selection(f.Ui, "subpage", "event");
                    D.Selection(f.Ui, "page", "donate");
                    D.Selection(f.Ui, "subwindow", "");
                    return true;
                case "lottery_panel":
                    if (!PublishCurrent(f, publish, out why, command.GuildTaskId)) return false;
                    D.Selection(f.Ui, "subpage", "event");
                    D.Selection(f.Ui, "subwindow", "lottery");
                    D.Call(f.Ui, "make_lottery");
                    return true;
                case "donate":
                    if (command.DonationType != 3 || f.SkipPremiumPrompt)
                        return Request(command, f, publish, out why);
                    // Exact DrawDonate branch. The tip cache cannot silently
                    // enable the callback without the receipt observer armed.
                    if (D.Template("consume_tip", 123u) == null || D.Visible(f.Premium))
                    { why = "guild_premium_quote_unavailable"; return false; }
                    if (!PublishCurrent(f, publish, out why, command.GuildTaskId)) return false;
                    if (((IDictionary)D.Read(f.Context.Character, "consume_tips_")).Contains(123u))
                    { why = "guild_premium_prompt_changed"; return false; }
                    D.Call(f.Ui, "Disable");
                    OpenPremium(f);
                    ownedPremium = true;
                    return true;
                case "lottery":
                case "premium_confirm":
                    return Request(command, f, publish, out why);
                case "reward_confirm":
                    Delegate close = D.Read(f.Reward, "OnClose") as Delegate;
                    if (!PublishCurrent(f, publish, out why)) return false;
                    D.Call(f.Reward, "Hide");
                    close.DynamicInvoke(new object[0]);
                    rewardConfirmed = true;
                    return true;
                case "close":
                    if (!PublishCurrent(f, publish, out why)) return false;
                    if (f.Phase == "DONATE" || HandledCloseReady(f) && goal.GuildTaskId == 20) D.Selection(f.Ui, "page", "");
                    if (f.Phase == "LOTTERY" || HandledCloseReady(f) && goal.GuildTaskId == 18) D.Selection(f.Ui, "subwindow", "");
                    // Normal outer close. GuildUI_3.Hide's tree cleanup sends
                    // no-cost 0x17b without registering a response handler.
                    D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                    Frame afterClose = Read(f.Context);
                    closed = afterClose.SameOwner(owned) && afterClose.Phase == "MAIN" && afterClose.Gate == "PASS" &&
                        !D.Visible(afterClose.Ui) && afterClose.Context.SubUi == null;
                    if (!closed) { Unknown("guild_close_unverified"); why = operationReason; return false; }
                    if (status == "OPEN") { status = "REJECTED"; operationReason = "guild_closed_without_request"; }
                    if (status == "COMPLETED" || status == "PROCESSED" || status == "REJECTED") Detach();
                    return true;
            }
            why = "guild_action_unavailable"; return false;
        }

        private static void OpenPremium(Frame f)
        {
            Type callback = Model.FirstType("__BaseUI+delegate_OnOK");
            Delegate ok = Delegate.CreateDelegate(callback, f.Ui, D.Method(f.Ui.GetType(), "GoldDonateOK", PremiumArgs, false, typeof(void)));
            D.Method(f.Premium.GetType(), "setConsume", new Type[] { typeof(int), typeof(byte), callback }, false, typeof(void)).Invoke(f.Premium, new object[] { 80, (byte)123, ok });
        }

        private bool Request(Command command, Frame f, DevelopmentPublication publish, out string why)
        {
            why = "";
            object client = f.Context.Client;
            if (!Monitor.TryEnter(client, 0)) { why = "guild_client_busy"; return false; }
            try
            {
                why = LiveGate(f);
                if (why != "") return false;
                IDictionary handlers;
                if (!ReadyNetwork(client, out handlers) || !DevelopmentNetwork.Sample(client).Idle)
                { why = "guild_network_busy_or_unverified"; return false; }
                progressHandler = handlers[(ushort)355];
                Delegate push = D.Read(progressHandler, "handler") as Delegate;
                if (!Callback(push, null, Model.FirstType("RootBase"), "handelActvity", ResponseArgs) ||
                    D.Read(progressHandler, "arg") != null || Field<float>(progressHandler, "life") != -1f)
                { why = "guild_progress_handler_unbound"; return false; }
                int requestedOpcode = command.GuildTaskId == 18 ? 302 : 301;
                progress = null;
                progressCapture = DevelopmentCallbackCapture.InstallGuild(client, progressHandler, 355, command.OperationId, command.Sequence, push,
                    delegate(byte[] body)
                    {
                        RequireLiveCallback(f);
                        int? value = GuildReceipt.Progress(body, command.GuildTaskId);
                        if (!value.HasValue) return;
                        if (value == f.Progress(command.GuildTaskId) && !progress.HasValue) return;
                        if (value != f.Progress(command.GuildTaskId) + 1 || progress.HasValue && progress != value)
                            throw new FormatException("guild_progress_unexpected");
                        progress = value;
                    }, out why);
                if (progressCapture == null) return false;
                try { if (!PublishCurrent(f, publish, out why, command.GuildTaskId)) { Detach(); return false; } }
                catch { Detach(); throw; }
                baseline = f;
                opcode = requestedOpcode; lastRequestSequence = command.Sequence; invoked = true; status = "PENDING";
                operationReason = "guild_request_pending"; requestTick = Environment.TickCount;
                if (requestedOpcode == 302)
                {
                    D.Call(f.Ui, "Disable");
                    D.Call(f.Ui, "cs_lottery");
                }
                else if (command.DonationType != 3)
                {
                    D.Call(f.Ui, "Disable");
                    D.Method(f.Ui.GetType(), "cs_donate", new Type[] { typeof(int) }, false, typeof(void)).Invoke(f.Ui, new object[] { command.DonationType });
                }
                else if (command.GuildAction == "premium_confirm")
                {
                    D.Call(f.Premium, "Hide");
                    D.Method(f.Ui.GetType(), "GoldDonateOK", PremiumArgs, false, typeof(void)).Invoke(f.Ui, new object[] { true, null });
                }
                else
                {
                    D.Call(f.Ui, "Disable");
                    OpenPremium(f); // setConsume's pinned opt-out branch calls GoldDonateOK.
                }
                responseHandler = handlers[(ushort)opcode];
                Delegate callback = responseHandler == null ? null : D.Read(responseHandler, "handler") as Delegate;
                object arg = responseHandler == null ? null : D.Read(responseHandler, "arg");
                if (!Callback(callback, f.Ui, f.Ui.GetType(), opcode == 301 ? "sc_donate" : "sc_lottery", ResponseArgs) ||
                    (opcode == 301 ? !(arg is int) || (int)arg != command.DonationType : arg != null))
                { Unknown("guild_own_response_unbound"); why = operationReason; return false; }
                responseCapture = DevelopmentCallbackCapture.InstallGuild(client, responseHandler, opcode, command.OperationId, command.Sequence, callback,
                    delegate(byte[] body)
                    {
                        RequireLiveCallback(f);
                        if (QueuedResponse(client, requestedOpcode)) throw new ProbeFailure("guild_duplicate_response_queued");
                        replyReceived = true;
                        receipt = GuildReceipt.Read(requestedOpcode, body);
                    }, out why);
                if (responseCapture == null) { Unknown(why); return false; }
                requestIssued = true;
                return true;
            }
            finally { Monitor.Exit(client); }
        }

        private void RequireLiveCallback(Frame baseline)
        {
            // Preserve the normal callback on a changed connection or day,
            // but do not interpret its payload as this operation's evidence.
            string gate = LiveGate(baseline);
            if (gate != "") throw new ProbeFailure(gate);
        }

        private static bool ReadyNetwork(object client, out IDictionary handlers)
        {
            handlers = D.Read(client, "m_handlers") as IDictionary;
            if (handlers == null || handlers[(ushort)355] == null ||
                handlers.Contains((ushort)301) || handlers.Contains((ushort)302)) return false;
            return !QueuedResponse(client, 0);
        }

        private static bool QueuedResponse(object client, int opcode)
        {
            IList messages = D.Read(client, "messages") as IList;
            if (messages == null || messages.Count > 64) return true;
            foreach (object message in messages)
            {
                ushort op = Field<ushort>(message, "op");
                if (opcode == 0 ? op == 301 || op == 302 || op == 355 : op == opcode) return true;
            }
            return false;
        }

        private void Settle()
        {
            Frame f = frame;
            if (f.RefreshGate != null && (f.RefreshGate != "" || f.Reset != baseline.Reset))
            { Unknown("guild_refresh_changed_after_request"); return; }
            if (f.RefreshGate == "" && (f.Connection == null || !f.Connection.Same(baseline.Connection)))
            { Unknown("guild_transport_changed_after_request"); return; }
            if (!f.SameOwner(baseline) || !object.ReferenceEquals(f.Context.SubUi, f.Ui))
            { Unknown("guild_context_changed_after_request"); return; }
            if (responseCapture == null || progressCapture == null ||
                responseCapture.Sequence != lastRequestSequence || progressCapture.Sequence != lastRequestSequence)
            { Unknown("guild_capture_unavailable"); return; }
            object client = f.Context.Client;
            if (!Monitor.TryEnter(client, 0)) return;
            try { SettleLocked(f, client); }
            finally { Monitor.Exit(client); }
        }

        private void SettleLocked(Frame f, object client)
        {
            string gate = LiveGate(baseline);
            if (gate != "") { Unknown(gate); return; }
            DevelopmentCallbackWitness response = responseCapture.Read(), daily = progressCapture.Read();
            if (response.Error != null || daily.Error != null ||
                response.OriginalThrew || daily.OriginalThrew || !progressCapture.StillInstalled())
            { Unknown("guild_callback_failed"); return; }
            IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
            bool responseGone = handlers != null && handlers[(ushort)opcode] == null;
            if (handlers == null || !object.ReferenceEquals(handlers[(ushort)355], progressHandler) ||
                !response.Returned && !responseGone &&
                    (!responseCapture.StillInstalled() || !object.ReferenceEquals(handlers[(ushort)opcode], responseHandler)))
            { Unknown("guild_callback_replaced"); return; }
            if (receipt != null && !receipt.Accepted)
            { status = "REJECTED"; operationReason = "guild_server_rejected"; handledProgress = f.Progress(goal.GuildTaskId); Detach(); return; }
            if (receipt != null && (opcode == 301 && !receipt.ExactQuote(goal.DonationType) ||
                opcode == 302 && (baseline.Prizes == null || Array.IndexOf(baseline.Prizes, receipt.Result) < 0)))
            { Unknown("guild_receipt_quote_or_prize_mismatch"); return; }
            if (requestIssued && PageAdvanced(f))
            {
                pageConfirmed = true; handledProgress = f.Progress(goal.GuildTaskId);
                status = "COMPLETED"; operationReason = "guild_current_page_confirmed"; Detach(); return;
            }
            if (requestIssued && receipt == null &&
                (response.NullResponse || responseGone || unchecked((uint)(Environment.TickCount - requestTick)) >= 12000) &&
                QuiescentOwnedPage(f))
            {
                processedWithoutReply = true; handledProgress = f.Progress(goal.GuildTaskId);
                status = "PROCESSED"; operationReason = "guild_request_submitted_no_reply"; Detach(); return;
            }
            if (!response.Returned || receipt == null) return;
            int coins = opcode == 301 && goal.DonationType != 3 ? goal.DonationType == 1 ? 1000 : 100000 : 0;
            int premium = opcode == 301 && goal.DonationType == 3 ? 80 : 0;
            if (opcode == 301 && !receipt.ExactQuote(goal.DonationType) ||
                opcode == 302 && Array.IndexOf(baseline.Prizes, receipt.Result) < 0)
            { Unknown("guild_receipt_quote_or_prize_mismatch"); return; }
            if (f.Coins != baseline.Coins - coins || (long)f.Gold != (long)baseline.Gold - premium ||
                f.BoundGold != baseline.BoundGold || opcode == 302 && (f.Draws != baseline.Draws - 1 || f.Contribution != baseline.Contribution - 400))
            { Unknown("guild_resource_readback_mismatch"); return; }
            if (!progress.HasValue || !daily.Returned) return;
            if (f.Progress(goal.GuildTaskId) != progress)
            { Unknown("guild_activity_progress_readback_mismatch"); return; }
            status = "COMPLETED"; operationReason = "guild_receipt_and_daily_progress_verified"; Detach();
        }

        private bool PageAdvanced(Frame f)
        {
            int? before = baseline.Progress(goal.GuildTaskId), after = f.Progress(goal.GuildTaskId);
            bool activity = before.HasValue && after.HasValue && after.Value > before.Value && after.Value <= f.Required(goal.GuildTaskId);
            if (goal.GuildTaskId == 20)
            {
                int coins = goal.DonationType == 1 ? 1000 : goal.DonationType == 2 ? 100000 : 0;
                int gold = goal.DonationType == 3 ? 80 : 0;
                bool wallet = receipt != null && receipt.Accepted && receipt.ExactQuote(goal.DonationType) &&
                    responseCapture != null && responseCapture.Read().Returned &&
                    f.Coins == baseline.Coins - coins && (long)f.Gold == (long)baseline.Gold - gold && f.BoundGold == baseline.BoundGold;
                return activity || f.DonationRefresh > baseline.DonationRefresh && f.DonationRefresh > f.Reset || wallet;
            }
            return activity || baseline.Draws > 0 && baseline.Contribution >= 400 &&
                f.Draws == baseline.Draws - 1 && f.Contribution == baseline.Contribution - 400;
        }

        private bool QuiescentOwnedPage(Frame f)
        {
            if (f == null || baseline == null || goal == null || !f.SameOwner(baseline) || LiveGate(baseline) != "" ||
                !object.ReferenceEquals(f.Context.SubUi, f.Ui) || !D.Visible(f.Ui) || D.Loading(f.Ui) ||
                D.Visible(f.Premium) || D.Visible(f.Reward) || Field<bool>(f.Ui, "show_list") ||
                Field<bool>(f.Ui, "need_close") || Field<string>(f.Ui, "popup_type") != "" ||
                D.Read(f.Ui, "m_guild_tree") as GameObject != null || Field<bool>(f.Ui, "lotting") || Field<bool>(f.Ui, "lotend") ||
                f.Context.Panels(f.Ui) != "PASS") return false;
            string page = Field<string>(f.Ui, "page"), subwindow = Field<string>(f.Ui, "subwindow");
            if (goal.GuildTaskId == 20 ? page != "donate" || subwindow != "" : page != "" || subwindow != "lottery") return false;
            object client = f.Context.Client;
            IList messages = D.Read(client, "messages") as IList, sends = D.Read(client, "m_sendDatas") as IList;
            IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
            if (messages == null || messages.Count != 0 || sends == null || sends.Count != 0 || handlers == null ||
                D.Bool(D.Read(client, "m_bSending")) || DevelopmentCallbackCapture.AnyCallbackExecuting) return false;
            foreach (DictionaryEntry entry in handlers)
                if ((ushort)entry.Key != (ushort)opcode && (ushort)entry.Key != 355 && Field<float>(entry.Value, "life") > 0f) return false;
            return true;
        }

        private bool HandledCloseReady(Frame f)
        {
            return !closed && requestIssued && (status == "COMPLETED" || status == "PROCESSED" || status == "REJECTED") &&
                QuiescentOwnedPage(f);
        }

        private void Detach()
        {
            if (responseCapture != null && !responseCapture.DisarmAndRestore().EndsWith("forward_only", StringComparison.Ordinal)) responseCapture = null;
            if (progressCapture != null && !progressCapture.DisarmAndRestore().EndsWith("forward_only", StringComparison.Ordinal)) progressCapture = null;
            if (responseCapture == null) responseHandler = null;
            if (progressCapture == null) progressHandler = null;
        }
        internal void NotInvoked() { queued = null; }
        internal void Failed(string reason) { queued = null; if (goal != null && !closed) Unknown(reason); }
        internal void Stop(string reason) { queued = null; if (status == "PENDING" || ownedPremium && !invoked && status == "OPEN") Unknown(reason); else Detach(); }
        private void Unknown(string reason) { status = "UNKNOWN"; operationReason = reason; Detach(); }

        private bool RetryUnstarted()
        {
            try
            {
                if (frame.Context == null || capturedFrame != Time.frameCount || frame.CharacterId == 0 || frame.GuildId == 0 ||
                    frame.RefreshGate != "" || frame.Connection == null || queued != null || goal != null ||
                    admitted != null || owned != null || baseline != null || lastRequestSequence != 0 || Busy || Unresolved || invoked || ownedPremium ||
                    responseCapture != null || progressCapture != null || receipt != null || progress.HasValue || status == "UNKNOWN")
                    return false;
                object role = frame.Context.Character;
                uint now, day, cycle;
                object loader = D.Static(Model.FirstType("BaseAsset"), "lds");
                return object.ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), role) &&
                    Field<ulong>(role, "id") == frame.CharacterId && Field<ulong>(role, "idguild") == frame.GuildId &&
                    object.ReferenceEquals(DailyTransport.CurrentClient(role), frame.Context.Client) && frame.Connection.StillCurrent() &&
                    DailyRefreshGate.ReadClock(role, out now, out day, out cycle) == "" && cycle == frame.Reset &&
                    object.ReferenceEquals(D.Read(loader, "currmg"), frame.Context.Root) &&
                    frame.Context.GlobalUi != null && object.ReferenceEquals(D.Read(frame.Context.GlobalUi, "level_rmg"), frame.Context.Root);
            }
            catch (Exception) { return false; }
        }

        internal string Json()
        {
            string[] actions = Actions();
            for (int i = 0; i < actions.Length; i++) actions[i] = D.Q(actions[i]);
            return "{\"schema\":1,\"revision\":" + revision + ",\"character_id\":" + D.Q(frame.CharacterId.ToString(CultureInfo.InvariantCulture)) +
                ",\"guild_id\":" + D.Q(frame.GuildId.ToString(CultureInfo.InvariantCulture)) + ",\"phase\":" + D.Q(frame.Phase) +
                ",\"gate\":" + D.Q(status == "UNKNOWN" ? "UNKNOWN" : HandledCloseReady(frame) ? "PASS" : status == "PENDING" && actions.Length == 0 ? "WAITING_NETWORK" : frame.Gate) +
                ",\"reason\":" + D.Q(status == "UNKNOWN" ? operationReason : frame.Reason) +
                ",\"actions\":[" + String.Join(",", actions) + "],\"operation\":" + OperationJson() +
                ",\"retry_unstarted\":" + D.B(RetryUnstarted()) + "}";
        }
        private string OperationJson()
        {
            if (goal == null) return "null";
            return "{\"operation_id\":" + D.Q(goal.OperationId) + ",\"task_id\":" + goal.GuildTaskId + ",\"donation_type\":" + goal.DonationType +
                ",\"max_premium\":" + goal.MaxPremium + ",\"status\":" + D.Q(status) + ",\"invoked\":" + D.B(invoked) +
                ",\"closed\":" + D.B(closed) + ",\"receipt_opcode\":" + (receipt == null ? 0 : opcode) + ",\"receipt_result\":" + Number(receipt == null ? null : (int?)receipt.Result) +
                ",\"progress_before\":" + Number(baseline == null ? null : baseline.Progress(goal.GuildTaskId)) + ",\"progress_after\":" + Number(progress) +
                ",\"reason\":" + D.Q(operationReason) +
                ",\"request_issued\":" + D.B(requestIssued) + ",\"reply_received\":" + D.B(replyReceived) +
                ",\"page_confirmed\":" + D.B(pageConfirmed) + ",\"processed_without_reply\":" + D.B(processedWithoutReply) +
                ",\"wrappers_released\":" + D.B(responseCapture == null && progressCapture == null) +
                ",\"observed_progress\":" + Number(handledProgress) + "}";
        }
        private static string Number(int? value) { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null"; }
    }
}
