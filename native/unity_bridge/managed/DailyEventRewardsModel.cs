using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate bool DailyEventRewardsInvocationGate();

    // Only fixed, audited stock claim methods are callable. New server pages
    // appear in the report but do not gain execution permission automatically.
    internal sealed class DailyEventRewardsModel
    {
        // 201 retains the original bundle. These two variants carry a fixed
        // authorization boundary through observation, queueing and invocation.
        internal const int FreeEventsTask = 204, OnlineOnlyTask = 205;
        private bool PremiumRoute { get { return task == 201 || task == FreeEventsTask || task == OnlineOnlyTask; } }
        private sealed class Row
        {
            internal string Key, Name, State, Reason, Method, Callback, Kind, Visibility = "UNOBSERVED";
            internal uint Id;
            internal int Opcode;
            internal int Current = -1, Limit = -1;
            internal int ResourceType = -1, ResourceCost = -1;
            internal long ResourceAvailable = -1;
            internal uint Remaining;
            internal Component Owner;
            internal object Data;
            internal string Marker;
            internal Dictionary<uint, int> QuestStates;
            internal string Json()
            { return "{\"key\":" + D.Q(Key) + ",\"name\":" + D.Q(Name) + ",\"status\":" + D.Q(State) +
                ",\"reason\":" + D.Q(Reason ?? "") + ",\"visibility\":" + D.Q(Visibility) + ",\"current\":" + Current + ",\"limit\":" + Limit + ",\"remaining_seconds\":" + Remaining +
                ",\"resource_type\":" + ResourceType + ",\"resource_cost\":" + ResourceCost + ",\"resource_available\":" + ResourceAvailable + ",\"gold_cost\":0}"; }
        }

        // Observe the exact handler installed by the stock request, forwarding
        // the stream unchanged. Settlement also requires its target marker.
        private sealed class Reply
        {
            internal object Handler;
            internal Delegate Original, Wrapper;
            internal FieldInfo Field;
            internal bool Received, Returned, Failed;
            internal byte[] Bytes;
            internal void Receive(MemoryStream stream, object arg)
            {
                try
                {
                    if (stream != null && stream.Length <= 1048576) { Bytes = stream.ToArray(); Received = true; }
                    Original.DynamicInvoke(new object[] { stream, arg });
                    Returned = stream != null && Bytes != null;
                }
                catch { Failed = true; throw; }
                finally { Restore(); }
            }
            internal void Restore()
            { if (Field != null && System.Object.ReferenceEquals(Field.GetValue(Handler), Wrapper)) Field.SetValue(Handler, Original); }
        }

        private Model model;
        private Component panel, premium, vip, timed, reward, box, root, main, special;
        private object character, client, socket, stream;
        private readonly List<Row> rows = new List<Row>();
        private readonly Hashtable completed = new Hashtable();
        private readonly Hashtable completedProgress = new Hashtable();
        private readonly Hashtable receipts = new Hashtable();
        private readonly Hashtable processedOnline = new Hashtable();
        private readonly Hashtable visiblePremiumPages = new Hashtable();
        private bool premiumPagesObserved;
        private Row target;
        private Reply reply;
        private string queued, queuedTarget, operationId = "", status = "IDLE", reason = "", gate = "", stamp = "";
        private int task, sequence, started, gold, boundGold, notInvoked, claimCount;
        private uint cycle;
        private ulong role;
        private bool hold, attempted, stopped, blocked, opened, closed, confirmed, targetConfirmed;
        private bool onlineInspected, stockPageClosed;
        private bool requestIssued, replyReceived, stockReturned, processedWithoutReply, pageStateConfirmed;
        private bool entryUnconfirmed, claimRequestIssued;
        private DateTime rewardShown, boxShown, specialShown;
        private object ownedReward, ownedBox, ownedSpecial;
        private string receiptBase64 = "";

        internal int Revision { get; private set; }
        internal bool Busy { get { return hold || queued != null; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal bool GuiInvocationAttempted { get; private set; }

        internal void Capture(Model value, int requested, bool admissionBlocked)
        {
            if (requested < 201 || requested > OnlineOnlyTask) throw new ArgumentOutOfRangeException("requested");
            if (hold && requested != task) return;
            model = value; blocked = admissionBlocked;
            if (!hold && task != requested)
            { task = requested; stopped = attempted = opened = closed = confirmed = false; status = "IDLE"; reason = ""; operationId = ""; completed.Clear(); completedProgress.Clear(); processedOnline.Clear(); claimCount = 0;
                requestIssued = replyReceived = stockReturned = processedWithoutReply = pageStateConfirmed = false;
                entryUnconfirmed = claimRequestIssued = false; }
            try
            {
                object observedCharacter = D.Static(Model.FirstType("Global"), "character_");
                object observedClient = D.Static(Model.FirstType("Global"), "m_client");
                ulong observedRole = Convert.ToUInt64(D.Read(observedCharacter, "id"), CultureInfo.InvariantCulture);
                uint now, day, observedCycle;
                gate = DailyRefreshGate.ReadClock(observedCharacter, out now, out day, out observedCycle);
                object observedSocket = D.Read(observedClient, "m_client"), observedStream = D.Read(observedClient, "m_stream");
                if (hold && (!System.Object.ReferenceEquals(character, observedCharacter) || role != observedRole || cycle != observedCycle ||
                    !System.Object.ReferenceEquals(client, observedClient) || !System.Object.ReferenceEquals(socket, observedSocket) || !System.Object.ReferenceEquals(stream, observedStream)))
                    throw new ProbeFailure("event_rewards_owner_or_cycle_changed");
                character = observedCharacter; client = observedClient; socket = observedSocket; stream = observedStream; role = observedRole; cycle = observedCycle;
                root = model.CurrentRoot();
                main = root == null ? null : root.GetComponentInChildren(Model.GameType("MainUI"));
                premium = vip = timed = special = null;
                foreach (UnityEngine.Object item in model.AllUi())
                {
                    Component ui = item as Component;
                    if (ui == null) continue;
                    if (ui.GetType() == Model.GameType("PremiumUI")) premium = ui;
                    if (ui.GetType() == Model.GameType("VipWealUI")) vip = ui;
                    if (ui.GetType() == Model.GameType("TimeLimitMessageUI")) timed = ui;
                    if (ui.GetType() == Model.FirstType("SpecialGetUI")) special = ui;
                }
                panel = PremiumRoute ? premium : task == 202 ? vip : timed;
                reward = D.Static(Model.FirstType("__BaseUI"), "battle_result_ui") as Component;
                box = D.Static(Model.FirstType("__BaseUI"), "msgBox") as Component;
                if (panel == null || premium == null || role == 0 || root == null || main == null) gate = "event_rewards_ui_unavailable";
                if (hold && (Int(character, "gold") < gold || Int(character, "bind_gold") < boundGold))
                    throw new ProbeFailure("event_rewards_premium_balance_decreased");
                if (hold && unchecked((uint)(Environment.TickCount - started)) > 240000)
                    throw new ProbeFailure("event_rewards_operation_timeout");
                if (hold && target != null && reply != null)
                {
                    if (reply.Failed) throw new ProbeFailure("event_rewards_stock_callback_failed");
                    if (target.Kind == "online" || target.Kind == "online_enter") ObserveOnline();
                    else if (target.Kind == "zhaoyun") ObserveZhaoyun();
                    else if (reply.Returned && !targetConfirmed && !D.Loading(panel) && (reward == null || !D.Loading(reward)) && (box == null || !D.Loading(box)))
                    {
                        if (!MarkerChanged(target)) throw new ProbeFailure("event_rewards_claim_reply_not_confirmed");
                        targetConfirmed = true; confirmed = true;
                        receiptBase64 = Convert.ToBase64String(reply.Bytes);
                        RecordClaim(target);
                        if (Pending(reward))
                        {
                            if ((DateTime)D.Read(reward, "visible_time") <= rewardShown) throw new ProbeFailure("event_rewards_reward_generation");
                            Delegate close = D.Read(reward, "OnClose") as Delegate;
                            if (close == null || !System.Object.ReferenceEquals(close.Target, target.Owner)) throw new ProbeFailure("event_rewards_reward_callback_owner");
                            ownedReward = reward;
                        }
                        if (Pending(box))
                        {
                            bool newbie = target.Kind == "newbie";
                            if ((DateTime)D.Read(box, "visible_time") <= boxShown || (!newbie && target.Kind != "temp") ||
                                D.Read(box, "OnOK") != null || D.Read(box, "OnClose") != null ||
                                (newbie ? !System.Object.ReferenceEquals(D.Read(box, "old_rmg"), D.Read(premium, "rmg")) : D.Read(box, "old_rmg") != null))
                                throw new ProbeFailure("event_rewards_unowned_prompt");
                            if (newbie && !SuccessField(reply.Bytes)) throw new ProbeFailure("event_rewards_newbie_reply_rejected");
                            string expected = (string)D.Method(Model.FirstType("__BaseUI"), newbie ? "load_str" : "get_string", new[] { typeof(uint) }, true, typeof(string)).Invoke(null, new object[] { newbie ? (uint)486 : (uint)334 });
                            if ((string)D.Read(box, "Message") != expected) throw new ProbeFailure("event_rewards_prompt_content");
                            ownedBox = box;
                        }
                        status = "CLAIMED";
                    }
                    if (target != null && targetConfirmed && !Pending(reward) && !Pending(box) && !Pending(special))
                    { target = null; reply.Restore(); reply = null; ownedReward = ownedBox = ownedSpecial = null; status = "READY"; }
                    else if (reply != null && !reply.Returned && target != null &&
                        target.Kind != "online" && target.Kind != "online_enter" && unchecked((uint)(Environment.TickCount - started)) > 18000)
                        throw new ProbeFailure("event_rewards_reply_timeout");
                }
                rows.Clear();
                if (panel != null)
                {
                    if (task == OnlineOnlyTask) { ReadPremiumVisibility(); ReadOnline(); }
                    else if (PremiumRoute) ReadPremium(now);
                    else if (task == 202) ReadVip();
                    else ReadTimed(now);
                }
                if (hold && opened && target == null && status != "UNKNOWN" && ReadyPanel()) status = "READY";
                Publish();
            }
            catch (Exception error)
            { gate = error is ProbeFailure ? error.Message : "event_rewards_observation_unavailable"; if (hold) { status = "UNKNOWN"; reason = gate; } Publish(); }
        }

        private void ReadVip()
        {
            int level = PropertyInt(character, "VIPLEVEL");
            Add("vip.daily", "VIP每日礼包", "vipdaily", 0, vip, "cs_vip_daily_gift_get", "sc_vip_daily_gift_get", 1820,
                Bool(character, "vip_gift_today") ? "ALREADY_CLAIMED" : "AVAILABLE", "", null);
            IDictionary templates = Templates("vip_rewards_new");
            for (uint n = 1; n <= 10; n++)
            {
                bool found = false, unclaimed = false, special = false;
                foreach (DictionaryEntry entry in templates)
                {
                    object t = entry.Value;
                    if (UInt(t, "vip_level") != n) continue;
                    found = true;
                    if (!((IList)D.Read(character, "viprewards")).Contains(UInt(t, "id"))) unclaimed = true;
                    if (Int(t, "idtreasure_type") == 0 || Int(t, "idtreasure_type") == 44) special = true;
                }
                if (found) Add("vip.level." + n, "VIP等级礼包 " + n, "viplevel", n, premium, "cs_vip_get_reward_new", "sc_vip_get_reward_new", 939,
                    !unclaimed ? "ALREADY_CLAIMED" : n > level ? "NOT_ELIGIBLE" : special ? "UNSUPPORTED" : "AVAILABLE",
                    special && unclaimed ? "event_rewards_special_presentation_unsupported" : "", null);
            }
            Add("vip.shop", "VIP特权商店", "", 0, vip, "", "", 0, "EXCLUDED", "event_rewards_purchase_excluded", null);
        }

        private void ReadPremiumVisibility()
        {
            if (D.Visible(premium) && !D.Loading(premium))
            {
                IDictionary index = (IDictionary)D.Read(premium, "pageidx");
                IList pages = (IList)D.Read(premium, "pageList");
                if (index.Count == 54)
                {
                    visiblePremiumPages.Clear();
                    foreach (DictionaryEntry entry in index) if (pages.Contains(entry.Value)) visiblePremiumPages[entry.Key] = true;
                    premiumPagesObserved = true;
                }
            }
        }

        private void ReadPremium(uint now)
        {
            ReadPremiumVisibility();
            string[] names = Enum.GetNames(Model.GameType("PremiumUI+premiumPage"));
            for (int n = 0; n < 54; n++)
            {
                if (n == 4 || n == 11 || n == 33 || n == 36 || n == 46 || n == 48 || PageVisibility(n) == "HIDDEN") continue;
                Add("premium.page." + n, names[n], "", (uint)n, premium, "", "", 0,
                    premiumPagesObserved ? "UNSUPPORTED" : "UNOBSERVED", "event_rewards_page_route_unsupported", null);
            }
            Add("premium.newbie", "新手卡", "newbie", 0, premium, "fixed_newbie_gift", "sc_newbie_gift", 50,
                Bool(character, "newbie_gift") ? "ALREADY_CLAIMED" : "AVAILABLE", "", null);
            object newbie = D.Read(character, "newbieinfo");
            int awarded = Int(newbie, "award_level"), level = Int(character, "level");
            bool next = false;
            foreach (DictionaryEntry entry in Templates("level_award"))
            {
                uint id = Convert.ToUInt32(entry.Key, CultureInfo.InvariantCulture);
                if (id <= awarded) continue;
                if (!next || id < nextLevel) { next = true; nextLevel = id; }
            }
            Add("premium.level." + (next ? nextLevel : (uint)awarded), "升级奖励", "level", next ? nextLevel : (uint)awarded, premium,
                "cs_level_award", "sc_level_award", 815, !next ? "ALREADY_CLAIMED" : nextLevel > level ? "NOT_ELIGIBLE" : "AVAILABLE", "", null);
            IList daily = (IList)D.Read(character, "everydaylogintemp");
            if (daily.Count == 0) Add("premium.login", "每日登录", "", 0, premium, "", "", 0, "EVENT_CLOSED", "event_rewards_event_closed", null);
            foreach (object item in daily)
            {
                uint id = UInt(item, "dayid");
                bool open = UInt(item, "start_time") <= now && now <= UInt(item, "end_time");
                Add("premium.login." + id, "每日登录 " + id, "login", id, premium, "cs_everyday_login_get", "sc_everyday_login_get", 2063,
                    ((IDictionary)D.Read(character, "everydaylogins")).Contains(id) ? "ALREADY_CLAIMED" : !open ? "EVENT_CLOSED" :
                    UInt(character, "everyday_login_days") < id ? "NOT_ELIGIBLE" : "AVAILABLE", "", item);
            }
            object seven = D.Read(character, "sevendayslogin");
            bool sevenOpen = seven != null && Bool(character, "isSevenDaysLoginOpen");
            bool newer = D.Bool(D.Static(Model.FirstType("Global"), "isNewNewbieActivity"));
            for (uint day = 1; day <= 7; day++)
            {
                object template = D.Template(newer ? "seven_days_login_bonus_new" : "seven_days_login_bonus", day);
                bool special = template != null && (Int(template, "bonus1_type") == 0 || Int(template, "bonus2_type") == 0);
                bool got = seven != null && InvokeBool(seven, "hasGotLoginBonus", (int)day);
                Add("premium.seven." + day, "七日登录 " + day, "seven", day, premium,
                    newer ? "cs_seven_days_login_bonus_get_new_0x721" : "cs_seven_days_login_bonus_get_0x71A",
                    newer ? "sc_seven_days_login_bonus_get_new_0x721" : "sc_seven_days_login_bonus_get_0x71A", newer ? 1825 : 1818,
                    got ? "ALREADY_CLAIMED" : !sevenOpen ? "EVENT_CLOSED" : template == null ? "UNSUPPORTED" : special ? "UNSUPPORTED" :
                    !InvokeBool(seven, "canGetBonus", (int)day) ? "NOT_ELIGIBLE" : "AVAILABLE", special ? "event_rewards_special_presentation_unsupported" : "", seven);
            }
            ReadZhaoyun();
            if (task != FreeEventsTask) ReadOnline();
        }
        private uint nextLevel;

        private void ReadZhaoyun()
        {
            int step = Int(character, "quest69"), vipLevel = PropertyInt(character, "VIPLEVEL");
            bool active = D.Bool(D.Static(Model.FirstType("Global"), "isNewerZhaoYunQuest"));
            bool newer = D.Bool(D.Static(Model.FirstType("Global"), "isNewNewbieActivity"));
            object generalQuest = D.Template("premium_general_quest", 69);
            object cost = generalQuest == null || step < 0 || step > 4 ? null :
                D.Template(newer ? "premium_quest_new" : "premium_quest", checked((uint)(Int(generalQuest, "quest_startid") + step)));
            int type = vipLevel >= 7 ? 0 : cost == null ? -1 : Int(cost, "need_treasure");
            int amount = vipLevel >= 7 ? 0 : cost == null ? -1 : Int(cost, "need_amount");
            long available = type == 4 ? D.Long(D.Read(character, "money")) : type == 10 ? D.Long(D.Read(character, "enchant_stone")) :
                type == 39 ? D.Long(D.Read(character, "generalsoul_purple")) : type == 40 ? D.Long(D.Read(character, "generalsoul_orange")) : amount == 0 ? 0 : -1;
            string state = "AVAILABLE", detail = "";
            if (!active) { state = "EVENT_CLOSED"; detail = "event_rewards_zhaoyun_event_closed"; }
            else if (step >= 5) state = "ALREADY_CLAIMED";
            else if (step < 0 || generalQuest == null || (vipLevel < 7 && cost == null) || amount < 0 ||
                (vipLevel < 7 && step < 4 && D.Template("premium_quest", checked((uint)(Int(generalQuest, "quest_startid") + step))) == null))
            { state = "UNSUPPORTED"; detail = "event_rewards_zhaoyun_cost_unknown"; }
            else if (!D.Bool(D.Method(Model.GameType("MainUI"), "checkUI", new[] { typeof(string) }, true, typeof(bool)).Invoke(null, new object[] { "army_recruit" })))
            { state = "LOCKED"; detail = "event_rewards_zhaoyun_recruit_locked"; }
            else if (amount > 0 && type == 7)
            { state = "EXCLUDED"; detail = "event_rewards_zhaoyun_premium_cost_excluded"; }
            else if (amount > 0 && type != 4 && type != 10 && type != 39 && type != 40)
            { state = "UNSUPPORTED"; detail = "event_rewards_zhaoyun_cost_type_unknown"; }
            else if (amount > 0 && available < amount)
            { state = "NOT_ELIGIBLE"; detail = "event_rewards_zhaoyun_resources_insufficient"; }
            else if (vipLevel < 7 && (step == 0 && Int(character, "level") < 70 ||
                step == 1 && !D.Bool(D.Method(premium.GetType(), "doHaveGeneral", new[] { typeof(uint) }, false, typeof(bool)).Invoke(premium, new object[] { (uint)69 })) ||
                (step == 2 || step == 3) && !D.Bool(D.Method(premium.GetType(), "checkQuestNeed", new[] { typeof(int) }, false, typeof(bool))
                    .Invoke(premium, new object[] { step + Int(generalQuest, "quest_startid") }))))
            { state = "NOT_ELIGIBLE"; detail = "event_rewards_zhaoyun_stock_requirement_unmet"; }
            Add("premium.zhaoyun." + step, "七进七出", "zhaoyun", checked((uint)Math.Max(0, step)), premium,
                "cs_zhaoyun_quest_new", "sc_zhaoyun_quest_new", 948, state, detail, cost);
            Row row = rows[rows.Count - 1]; row.Current = step; row.Limit = 5; row.ResourceType = type; row.ResourceCost = amount; row.ResourceAvailable = available;
            // No account budget exists for this event's resource exchange.
            // A zero-cost current template is the only selected-mode offer;
            // a checkbox never authorizes spending money, stones or souls.
            if (task == FreeEventsTask && row.State == "AVAILABLE" && amount != 0)
            { row.State = "UNAVAILABLE"; row.Reason = "event_rewards_resource_spend_not_authorized"; }
        }

        private void ObserveZhaoyun()
        {
            if (!reply.Returned) return;
            uint receiptStep, specialType, specialId;
            ReadZhaoyunReceipt(reply.Bytes, out receiptStep, out specialType, out specialId);
            if (receiptStep != target.Id + 1 || receiptStep > 5 || UInt(character, "quest69") != receiptStep)
                throw new ProbeFailure("event_rewards_zhaoyun_step_unconfirmed");
            if (Pending(box)) throw new ProbeFailure("event_rewards_zhaoyun_unexpected_prompt");
            if (specialType != UInt32.MaxValue && special != null && D.Visible(special) && !D.Loading(special))
            {
                if ((DateTime)D.Read(special, "visible_time") <= specialShown || UInt(special, "tempid") != specialId ||
                    (string)D.Read(special, "showtype") != (specialType == 0 ? "general" : "artifact"))
                    throw new ProbeFailure("event_rewards_zhaoyun_special_owner");
                ownedSpecial = special;
            }
            if (Pending(reward) && reward != null && D.Visible(reward) && !D.Loading(reward))
            {
                Delegate close = D.Read(reward, "OnClose") as Delegate;
                if ((DateTime)D.Read(reward, "visible_time") <= rewardShown || close == null || !System.Object.ReferenceEquals(close.Target, premium))
                    throw new ProbeFailure("event_rewards_zhaoyun_reward_owner");
                ownedReward = reward;
            }
            if (specialType != UInt32.MaxValue && ownedSpecial == null && (special == null || D.Loading(special))) return;
            if ((special != null && D.Loading(special)) || (reward != null && D.Loading(reward))) return;
            if (!targetConfirmed)
            {
                targetConfirmed = confirmed = true; receiptBase64 = Convert.ToBase64String(reply.Bytes);
                RecordClaim(target);
            }
            status = "CLAIMED";
        }

        private static void ReadZhaoyunReceipt(byte[] bytes, out uint step, out uint specialType, out uint specialId)
        {
            if (!SuccessField(bytes)) throw new ProbeFailure("event_rewards_zhaoyun_reply_rejected");
            step = UInt32.MaxValue; specialType = UInt32.MaxValue; specialId = 0; int position = 0;
            while (position < bytes.Length)
            {
                ulong tag = Varint(bytes, ref position); int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field == 2 && wire == 0) { if (step != UInt32.MaxValue) throw new ProbeFailure("event_rewards_zhaoyun_receipt_step"); step = checked((uint)Varint(bytes, ref position)); }
                else if (field == 8 && wire == 2)
                {
                    int length = checked((int)Varint(bytes, ref position)), end = checked(position + length);
                    if (end > bytes.Length) throw new ProbeFailure("event_rewards_zhaoyun_receipt_bound");
                    uint kind = UInt32.MaxValue, id = 0;
                    while (position < end)
                    {
                        ulong nested = Varint(bytes, ref position); int nestedField = (int)(nested >> 3), nestedWire = (int)(nested & 7);
                        if (nestedWire == 0)
                        {
                            ulong value = Varint(bytes, ref position);
                            if (nestedField == 1) kind = checked((uint)value);
                            if (nestedField == 2) { if ((value & 1) != 0) throw new ProbeFailure("event_rewards_zhaoyun_special_id"); id = checked((uint)(value >> 1)); }
                        }
                        else SkipWire(bytes, ref position, nestedWire);
                    }
                    if (position != end) throw new ProbeFailure("event_rewards_zhaoyun_receipt_bound");
                    if (kind == 0 || kind == 36)
                    {
                        if (specialType != UInt32.MaxValue || id == 0) throw new ProbeFailure("event_rewards_zhaoyun_multiple_special_rewards");
                        specialType = kind; specialId = id;
                    }
                }
                else SkipWire(bytes, ref position, wire);
            }
            if (step == UInt32.MaxValue) throw new ProbeFailure("event_rewards_zhaoyun_receipt_step");
        }
        private static void SkipWire(byte[] bytes, ref int position, int wire)
        {
            if (wire == 0) { Varint(bytes, ref position); return; }
            ulong size = wire == 1 ? 8u : wire == 5 ? 4u : wire == 2 ? Varint(bytes, ref position) : UInt64.MaxValue;
            if (size > (ulong)(bytes.Length - position)) throw new ProbeFailure("event_rewards_zhaoyun_receipt_bound");
            position += (int)size;
        }

        private void ReadOnline()
        {
            object online = D.Read(character, "onlinelottery");
            int count = Int(online, "lottery_today"), limit = Templates("online_lottery_template").Count;
            uint remaining = D.UInt(D.Method(online.GetType(), "getcdtime", Type.EmptyTypes, false, typeof(uint)).Invoke(online, null));
            object template = count < limit ? D.Template("online_lottery_template", checked((uint)(count + 1))) : null;
            string state = count < 0 || limit == 0 ? "UNSUPPORTED" : count >= limit ? "ALREADY_CLAIMED" :
                template == null ? "UNSUPPORTED" : remaining > 0 ? "COOLDOWN" : "AVAILABLE";
            string detail = state == "COOLDOWN" ? "event_rewards_online_cooldown" : state == "UNSUPPORTED" ? "event_rewards_online_template_unavailable" : "";
            Add("premium.online." + Math.Min(count + 1, limit), "在线奖励", onlineInspected ? "online" : "online_enter", checked((uint)Math.Max(0, count + 1)), premium,
                onlineInspected ? "cs_online_lottery_draw" : "cs_online_lottery_enter", onlineInspected ? "sc_online_lotteyr_draw" : "sc_online_lottery_enter", onlineInspected ? 1823 : 1861,
                state, detail, online);
            Row row = rows[rows.Count - 1]; row.Current = count; row.Limit = limit; row.Remaining = remaining;
            if (entryUnconfirmed)
            { row.State = "UNAVAILABLE"; row.Reason = "event_rewards_online_entry_unconfirmed_claim_not_issued"; }
            if (processedOnline.Contains(row.Key) && row.State == "AVAILABLE")
            { row.State = "PROCESSED"; row.Reason = "event_rewards_online_no_reply_processed"; }
        }

        private void ObserveOnline()
        {
            replyReceived |= reply.Received; stockReturned |= reply.Returned;
            // A definite negative response is never the no-reply policy case.
            if (reply.Received && !SuccessField(reply.Bytes)) throw new ProbeFailure("event_rewards_online_reply_rejected");
            if (target.Kind == "online_enter")
            {
                IDictionary items = (IDictionary)D.Read(D.Read(character, "onlinelottery"), "m_onlineLotteryItems");
                if (items.Count > 12) throw new ProbeFailure("event_rewards_online_items_unavailable");
                if (!reply.Received && requestIssued && Idle() &&
                    (items.Count == 0 || Bool(premium, "sended")) &&
                    !Pending(reward) && !Pending(box) && !Pending(special))
                {
                    // This was only an entry/refresh request. A cached item
                    // list is not proof that a reward claim was issued.
                    entryUnconfirmed = true;
                    reply.Restore(); reply = null; target = null; status = "READY";
                    reason = "event_rewards_online_entry_unconfirmed_claim_not_issued";
                    return;
                }
                if (items.Count > 0 && Idle())
                { onlineInspected = true; target = null; reply.Restore(); reply = null; status = "READY"; return; }
                if (reply.Returned && items.Count == 0) throw new ProbeFailure("event_rewards_online_items_unavailable");
                return;
            }
            object pending = D.Read(premium, "m_col");
            bool ownProjection = pending != null && Int(pending, "lottery_today") == target.Id && Int(premium, "target_lottery") > 0;
            if (reply.Returned && !ownProjection)
                throw new ProbeFailure("event_rewards_online_receipt_target");
            if (ownProjection && reward != null && D.Visible(reward) && !D.Loading(reward))
            {
                Delegate close = D.Read(reward, "OnClose") as Delegate;
                if ((DateTime)D.Read(reward, "visible_time") <= rewardShown || close == null || close.Method.Name != "recall" ||
                    !System.Object.ReferenceEquals(close.Target, premium)) throw new ProbeFailure("event_rewards_online_reward_owner");
                ownedReward = reward;
            }
            // The normal roulette animation presents the reward. Its stock
            // recall callback applies m_col to the character only on close.
            int current = Int(D.Read(character, "onlinelottery"), "lottery_today");
            if (current < target.Id)
            {
                if (!reply.Received && requestIssued && Idle() && !D.Loading(panel) &&
                    !Bool(premium, "m_lotting") && Int(premium, "m_cur_highlight") == -1 &&
                    !Pending(reward) && !Pending(box) && !Pending(special))
                    ProcessOnlineWithoutReply();
                else if (reply.Returned && unchecked((uint)(Environment.TickCount - started)) > 30000)
                    throw new ProbeFailure("event_rewards_online_animation_or_settlement_timeout");
                return;
            }
            if (Bool(premium, "m_lotting") || Int(premium, "m_cur_highlight") != -1 || !Idle()) return;
            if (!targetConfirmed)
            {
                targetConfirmed = confirmed = pageStateConfirmed = true;
                receiptBase64 = reply.Returned && reply.Bytes != null ? Convert.ToBase64String(reply.Bytes) : "";
                RecordClaim(target);
            }
            if (Pending(box))
            {
                Delegate close = D.Read(box, "OnClose") as Delegate;
                string expected = (string)D.Method(Model.FirstType("__BaseUI"), "load_str", new[] { typeof(uint) }, true, typeof(string)).Invoke(null, new object[] { (uint)21646 });
                if ((DateTime)D.Read(box, "visible_time") <= boxShown || (string)D.Read(box, "Message") != expected ||
                    D.Read(box, "OnOK") != null || close == null || close.Method.Name != "_return" || !System.Object.ReferenceEquals(close.Target, premium))
                    throw new ProbeFailure("event_rewards_online_completion_prompt_owner");
                ownedBox = box;
            }
            status = "CLAIMED";
        }

        private void ProcessOnlineWithoutReply()
        {
            if (target == null || target.Kind != "online" || !requestIssued || !claimRequestIssued)
                throw new ProbeFailure("event_rewards_online_claim_not_issued");
            // Keep the current client counters unchanged and suppress only
            // this already-issued claim tier within this operation.
            processedWithoutReply = true; pageStateConfirmed = false;
            processedOnline[target.Key] = true;
            reply.Restore(); reply = null; target = null;
            status = "READY"; reason = "event_rewards_online_no_reply_processed";
        }

        // Both pinned online responses have required field 1 = Operate.OK (0).
        // Decode this independently of the stock callback's cached UI fields.
        private static bool SuccessField(byte[] bytes)
        {
            int position = 0; bool found = false;
            while (position < bytes.Length)
            {
                ulong tag = Varint(bytes, ref position); int wire = (int)(tag & 7); ulong field = tag >> 3;
                if (field == 0) throw new ProbeFailure("event_rewards_online_receipt_schema");
                if (wire == 0)
                {
                    ulong value = Varint(bytes, ref position);
                    if (field == 1) { if (found || value != 0) return false; found = true; }
                }
                else
                {
                    if (field == 1) return false;
                    ulong size = wire == 1 ? 8u : wire == 5 ? 4u : wire == 2 ? Varint(bytes, ref position) : UInt64.MaxValue;
                    if (size > (ulong)(bytes.Length - position)) throw new ProbeFailure("event_rewards_online_receipt_bound");
                    position += (int)size;
                }
            }
            return found;
        }
        private static ulong Varint(byte[] bytes, ref int position)
        {
            ulong value = 0;
            for (int shift = 0; shift <= 63 && position < bytes.Length; shift += 7)
            {
                byte part = bytes[position++];
                if (shift == 63 && part > 1) break;
                value |= (ulong)(part & 127) << shift;
                if ((part & 128) == 0) return value;
            }
            throw new ProbeFailure("event_rewards_online_receipt_varint");
        }

        private void ReadTimed(uint now)
        {
            IList messages = (IList)D.Static(Model.FirstType("Global"), "tempevent_messages");
            foreach (object msg in messages)
            {
                uint id = UInt(msg, "id");
                bool open = UInt(msg, "start_time") < now && now < UInt(msg, "end_time");
                Add("timed.message." + id, "限时活动 " + id, "temp", id, timed, "cs_tempeventmessage_bonus_0x4E2", "sc_tempeventmessage_bonus_0x4E2", 1250,
                    !open ? "EVENT_CLOSED" : InvokeBool(msg, "finished") ? "ALREADY_CLAIMED" : InvokeBool(msg, "canget") ? "AVAILABLE" : "NOT_ELIGIBLE", "", msg);
            }
            foreach (object msg in (IList)D.Static(Model.FirstType("Global"), "timelimit_messages"))
                Add("timed.notice." + UInt(msg, "id"), "限时公告 " + UInt(msg, "id"), "", 0, timed, "", "", 0, "NO_REWARD", "event_rewards_information_only", msg);
        }

        private void Add(string key, string name, string kind, uint id, Component owner, string method, string callback, int opcode, string state, string detail, object data)
        {
            Row row = new Row { Key = key, Name = name, Kind = kind, Id = id, Owner = owner, Method = method, Callback = callback, Opcode = opcode, State = state, Reason = detail, Data = data };
            if (PremiumRoute)
            {
                int page = kind == "newbie" ? 11 : kind == "level" ? 4 : kind == "login" ? 48 : kind == "seven" ? 33 : kind == "zhaoyun" ? 46 :
                    kind == "online" || kind == "online_enter" ? 36 : key.StartsWith("premium.page.") ? (int)id : -1;
                if (page >= 0) row.Visibility = PageVisibility(page);
                if (row.Visibility == "HIDDEN" && row.State == "AVAILABLE") { row.State = "NOT_VISIBLE"; row.Reason = "event_rewards_page_hidden"; }
            }
            if (kind == "temp") row.QuestStates = TempQuestStates(data);
            row.Marker = Marker(row); rows.Add(row);
        }
        private string PageVisibility(int page)
        { return !premiumPagesObserved ? "UNOBSERVED" : visiblePremiumPages.Contains(page) ? "VISIBLE" : "HIDDEN"; }
        private string Marker(Row row)
        {
            if (row.Kind == "vipdaily") return Bool(character, "vip_gift_today").ToString();
            if (row.Kind == "newbie") return Bool(character, "newbie_gift").ToString();
            if (row.Kind == "viplevel")
            {
                StringBuilder value = new StringBuilder();
                foreach (DictionaryEntry entry in Templates("vip_rewards_new"))
                    if (UInt(entry.Value, "vip_level") == row.Id) value.Append(UInt(entry.Value, "id")).Append(':').Append(((IList)D.Read(character, "viprewards")).Contains(UInt(entry.Value, "id"))).Append(';');
                return value.ToString();
            }
            if (row.Kind == "level") return Int(D.Read(character, "newbieinfo"), "award_level").ToString(CultureInfo.InvariantCulture);
            if (row.Kind == "login") return ((IDictionary)D.Read(character, "everydaylogins")).Contains(row.Id).ToString();
            if (row.Kind == "seven") return InvokeBool(D.Read(character, "sevendayslogin"), "hasGotLoginBonus", (int)row.Id).ToString();
            if (row.Kind == "temp")
            {
                StringBuilder value = new StringBuilder();
                foreach (object quest in (IList)Value(row.Data, "quests")) value.Append(UInt(quest, "id")).Append(':').Append(Bool(quest, "isfinished")).Append(':').Append(Bool(quest, "isgot")).Append(';');
                return value.ToString();
            }
            return "";
        }
        private bool MarkerChanged(Row row)
        {
            string current = Marker(row);
            if (current == row.Marker) return false;
            if (row.Kind == "vipdaily" || row.Kind == "newbie" || row.Kind == "login" || row.Kind == "seven") return current == "True";
            if (row.Kind == "level") return UInt(D.Read(character, "newbieinfo"), "award_level") == row.Id;
            if (row.Kind == "viplevel")
            {
                foreach (DictionaryEntry entry in Templates("vip_rewards_new"))
                    if (UInt(entry.Value, "vip_level") == row.Id && !((IList)D.Read(character, "viprewards")).Contains(UInt(entry.Value, "id"))) return false;
                return true;
            }
            if (row.Kind == "temp") return TempClaimChanged(row);
            return false;
        }

        private static Dictionary<uint, int> TempQuestStates(object data)
        {
            Dictionary<uint, int> states = new Dictionary<uint, int>();
            foreach (object quest in (IList)Value(data, "quests"))
            {
                uint id = UInt(quest, "id");
                if (states.ContainsKey(id)) throw new ProbeFailure("event_rewards_temp_quest_identity");
                states.Add(id, (Bool(quest, "isfinished") ? 1 : 0) | (Bool(quest, "isgot") ? 2 : 0));
            }
            return states;
        }

        private static string ProgressKey(Row row)
        {
            if (row.Kind != "temp") return row.Key;
            List<uint> ids = new List<uint>();
            foreach (KeyValuePair<uint, int> quest in row.QuestStates) if (quest.Value == 1) ids.Add(quest.Key);
            ids.Sort();
            StringBuilder value = new StringBuilder(row.Key + ".quests");
            foreach (uint id in ids) value.Append('.').Append(id.ToString(CultureInfo.InvariantCulture));
            return value.ToString();
        }

        private void RecordClaim(Row row)
        {
            string key = ProgressKey(row);
            if (completedProgress.Contains(key)) throw new ProbeFailure("event_rewards_duplicate_claim_progress");
            completedProgress[key] = true; completed[row.Key] = true; receipts[key] = receiptBase64; claimCount++;
        }

        private bool TempClaimChanged(Row row)
        {
            if (!SuccessField(reply.Bytes)) throw new ProbeFailure("event_rewards_temp_reply_rejected");
            uint message = UInt32.MaxValue; int position = 0;
            // Response field 2 is the updated message, whose required field 1
            // binds the exact message id passed to the stock request.
            while (position < reply.Bytes.Length)
            {
                ulong tag = Varint(reply.Bytes, ref position);
                if ((tag >> 3) == 2 && (tag & 7) == 2)
                {
                    int length = checked((int)Varint(reply.Bytes, ref position));
                    int end = checked(position + length);
                    if (end > reply.Bytes.Length) throw new ProbeFailure("event_rewards_temp_receipt_bound");
                    while (position < end)
                    {
                        ulong nested = Varint(reply.Bytes, ref position);
                        if ((nested >> 3) == 1 && (nested & 7) == 0)
                        {
                            if (message != UInt32.MaxValue) throw new ProbeFailure("event_rewards_temp_receipt_identity");
                            message = checked((uint)Varint(reply.Bytes, ref position));
                        }
                        else SkipWire(reply.Bytes, ref position, (int)(nested & 7));
                    }
                    if (position != end) throw new ProbeFailure("event_rewards_temp_receipt_bound");
                }
                else SkipWire(reply.Bytes, ref position, (int)(tag & 7));
            }
            if (message != row.Id) throw new ProbeFailure("event_rewards_temp_receipt_identity");
            Dictionary<uint, int> current = TempQuestStates(row.Data);
            if (current.Count != row.QuestStates.Count) throw new ProbeFailure("event_rewards_temp_quests_changed");
            bool advanced = false;
            foreach (KeyValuePair<uint, int> before in row.QuestStates)
            {
                int after;
                if (!current.TryGetValue(before.Key, out after) || ((before.Value & 2) != 0 && (after & 2) == 0))
                    throw new ProbeFailure("event_rewards_temp_quests_changed");
                if (before.Value == 1 && after == 3) advanced = true;
            }
            // One response may settle a subset. Keep the message eligible for
            // another claim only after a previously eligible quest became got.
            return advanced;
        }

        internal bool Queue(int requested, ulong characterId, string id, int seq, int revision, string action, out string why)
        {
            why = "event_rewards_admission";
            if (blocked || stopped || queued != null || requested != task || characterId == 0 || characterId != role || revision != Revision || seq <= sequence || !Command.ValidNonce(id)) return false;
            if (action != Offer() || action == "" || hold && operationId != id) return false;
            if (!hold) { operationId = id; sequence = seq; completed.Clear(); completedProgress.Clear(); receipts.Clear(); processedOnline.Clear(); claimCount = 0; attempted = confirmed = opened = closed = onlineInspected = stockPageClosed = false;
                requestIssued = replyReceived = stockReturned = processedWithoutReply = pageStateConfirmed = false;
                entryUnconfirmed = claimRequestIssued = false; started = Environment.TickCount; }
            sequence = seq; notInvoked = 0; queued = action; queuedTarget = Candidate() == null ? "" : Candidate().Key;
            Publish(); why = ""; return true;
        }

        internal bool ExecuteGui(DevelopmentPublication publish, DailyEventRewardsInvocationGate begin, out string why)
        {
            GuiInvocationAttempted = false; why = "event_rewards_not_queued";
            string action = queued, key = queuedTarget;
            if (action == null) return false;
            queued = null;
            Capture(model, task, blocked);
            if (!D.Gui || action != Offer() || (action == "claim" && (Candidate() == null || Candidate().Key != key))) { why = "event_rewards_action_changed"; Publish(); return false; }
            publish();
            if (stopped || blocked || !begin()) { why = "event_rewards_cancelled"; Publish(); return false; }
            GuiInvocationAttempted = true;
            try
            {
                if (action == "open")
                {
                    if (Pending(reward) || Pending(box) || Pending(special)) throw new ProbeFailure("event_rewards_preexisting_overlay");
                    if (!hold) { gold = Int(character, "gold"); boundGold = Int(character, "bind_gold"); }
                    hold = true; stockPageClosed = false; status = "OPENING";
                    // handle_sub_ui toggles an already selected page closed.
                    // Adopt a ready existing target; stock navigation switches
                    // only when a different known event page is selected.
                    if (!System.Object.ReferenceEquals(D.Static(Model.GameType("MainUI"), "m_subui"), panel))
                    {
                        if (PremiumRoute) D.MainBranch("_premium");
                        else if (task == 203) D.MainBranch("_timelimit");
                        else D.Call(main, "_vipweal");
                    }
                    opened = true;
                }
                else if (action == "claim")
                {
                    target = Candidate(); targetConfirmed = false;
                    if (target == null || !InScope(target)) throw new ProbeFailure("event_rewards_claim_out_of_scope");
                    bool onlineRequest = target.Kind == "online" || target.Kind == "online_enter";
                    object previousHandler = onlineRequest ? ((IDictionary)D.Read(client, "m_handlers"))[(ushort)target.Opcode] : null;
                    rewardShown = reward == null ? DateTime.MinValue : (DateTime)D.Read(reward, "visible_time");
                    boxShown = box == null ? DateTime.MinValue : (DateTime)D.Read(box, "visible_time");
                    specialShown = special == null ? DateTime.MinValue : (DateTime)D.Read(special, "visible_time");
                    if (target.Kind != "online_enter") attempted = true;
                    requestIssued = replyReceived = stockReturned = false;
                    started = Environment.TickCount; status = "PENDING";
                    if (target.Kind == "newbie") SendNewbie();
                    else if (target.Kind == "zhaoyun") D.Method(target.Owner.GetType(), target.Method, new[] { typeof(bool) }, false, typeof(void))
                        .Invoke(target.Owner, new object[] { D.Bool(D.Static(Model.FirstType("Global"), "isNewNewbieActivity")) });
                    else if (target.Kind == "online_enter") D.Method(target.Owner.GetType(), target.Method, new[] { typeof(bool) }, false, typeof(void)).Invoke(target.Owner, new object[] { false });
                    else if (target.Kind == "vipdaily" || target.Kind == "level" || target.Kind == "online") D.Call(target.Owner, target.Method);
                    else D.Method(target.Owner.GetType(), target.Method, new[] { typeof(uint) }, false, typeof(void)).Invoke(target.Owner, new object[] { target.Id });
                    Bind(target);
                    requestIssued = !onlineRequest || !System.Object.ReferenceEquals(previousHandler, reply.Handler) &&
                        Convert.ToSingle(D.Read(reply.Handler, "life"), CultureInfo.InvariantCulture) > 0;
                    if (onlineRequest && !requestIssued) throw new ProbeFailure("event_rewards_online_request_not_issued");
                    if (target.Kind == "online" && requestIssued) claimRequestIssued = true;
                }
                else if (action == "close_reward")
                {
                    if (special != null && System.Object.ReferenceEquals(ownedSpecial, special) && D.Visible(special))
                    {
                        // Authorized non-button cleanup: SpecialGetUI.Hide
                        // only hides UI and resets add_to_treasury; the player
                        // buttons would enter formation/equipment instead.
                        D.Call(special, "Hide");
                        if (Pending(special)) throw new ProbeFailure("event_rewards_special_close_unconfirmed");
                        ownedSpecial = null;
                    }
                    else
                    {
                        Delegate close = (Delegate)D.Read(reward, "OnClose");
                        D.Call(reward, "Hide"); close.DynamicInvoke(null); ownedReward = null;
                    }
                }
                else if (action == "close_message")
                {
                    Delegate stockClose = D.Read(box, "OnClose") as Delegate;
                    bool onlineReturn = target != null && target.Kind == "online" && targetConfirmed && stockClose != null &&
                        stockClose.Method.Name == "_return" && System.Object.ReferenceEquals(stockClose.Target, premium);
                    D.Method(box.GetType(), "ButtonOK", new[] { typeof(bool) }, false, typeof(void)).Invoke(box, new object[] { false }); ownedBox = null;
                    stockPageClosed = onlineReturn && D.Static(Model.GameType("MainUI"), "m_subui") == null && !Pending(panel);
                }
                else if (action == "close")
                {
                    if (task == OnlineOnlyTask || processedWithoutReply) D.Call(premium, "_return");
                    else D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                    if (D.Static(Model.GameType("MainUI"), "m_subui") != null || Pending(panel) || Pending(special)) throw new ProbeFailure("event_rewards_close_unconfirmed");
                    hold = false; closed = true; status = entryUnconfirmed ? "UNAVAILABLE" : processedWithoutReply ? "PROCESSED" : "COMPLETED";
                    reason = entryUnconfirmed ? "event_rewards_online_entry_unconfirmed_claim_not_issued" : processedWithoutReply ? "event_rewards_online_no_reply_processed" :
                        pageStateConfirmed ? "event_rewards_online_current_state_confirmed" : "event_rewards_supported_claims_processed";
                }
                Publish(); why = ""; return true;
            }
            catch (Exception error) { status = "UNKNOWN"; reason = error is ProbeFailure ? error.Message : "event_rewards_stock_call_failed"; why = reason; Publish(); return false; }
        }

        private void Bind(Row row)
        {
            object handler = ((IDictionary)D.Read(client, "m_handlers"))[(ushort)row.Opcode];
            if (handler == null) throw new ProbeFailure("event_rewards_handler_missing");
            FieldInfo field = handler.GetType().GetField("handler", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Delegate original = field == null ? null : field.GetValue(handler) as Delegate;
            object argument = D.Read(handler, "arg");
            bool correctArgument = row.Kind == "online_enter" ? argument is bool && !(bool)argument : argument == null;
            if (original == null || original.GetInvocationList().Length != 1 || !System.Object.ReferenceEquals(original.Target, row.Owner) || original.Method.Name != row.Callback ||
                original.Method.DeclaringType != row.Owner.GetType() || !correctArgument) throw new ProbeFailure("event_rewards_handler_identity");
            reply = new Reply { Handler = handler, Field = field, Original = original };
            reply.Wrapper = Delegate.CreateDelegate(original.GetType(), reply, typeof(Reply).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic));
            field.SetValue(handler, reply.Wrapper);
        }

        private void SendNewbie()
        {
            // Exact no-argument free branch of DrawXinshouka. Calling
            // LoginUI.getNewbieCode would log the account and generated code;
            // no code, login data or CashGift path is accessed here.
            if (Bool(character, "newbie_gift") || Bool(premium, "xinshoukasent")) throw new ProbeFailure("event_rewards_newbie_not_available");
            Type packetType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "pbdata") packetType = assembly.GetType("game.net.data.CtoS.CSNewbieGift_0x32", false);
            if (packetType == null) throw new ProbeFailure("event_rewards_newbie_packet_missing");
            Type callbackType = Model.FirstType("game.net.tcp_base+delegate_handler");
            Delegate callback = Delegate.CreateDelegate(callbackType, premium, D.Method(premium.GetType(), "sc_newbie_gift", new[] { typeof(MemoryStream), typeof(object) }, false, typeof(void)));
            MethodInfo send = null;
            foreach (MethodInfo method in Model.FirstType("game.net.tcp_base").GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] p = method.GetParameters();
                if (method.Name != "Send" || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1 || p.Length != 4 ||
                    p[0].ParameterType != typeof(ushort) || !p[1].ParameterType.IsGenericParameter || p[2].ParameterType != callbackType ||
                    p[3].ParameterType != typeof(object) || method.ReturnType != typeof(void)) continue;
                if (send != null) throw new ProbeFailure("event_rewards_newbie_send_ambiguous");
                send = method.MakeGenericMethod(packetType);
            }
            if (send == null) throw new ProbeFailure("event_rewards_newbie_send_missing");
            send.Invoke(client, new object[] { (ushort)50, Activator.CreateInstance(packetType), callback, null });
            D.Selection(premium, "xinshoukasent", true);
        }

        private string Offer()
        {
            if (blocked || stopped || queued != null || gate != "" || status == "UNKNOWN") return "";
            if (!hold) return Home() && Idle() ? "open" : "";
            if (System.Object.ReferenceEquals(ownedSpecial, special) && special != null && D.Enabled(special) && !D.Loading(special)) return "close_reward";
            if (System.Object.ReferenceEquals(ownedReward, reward) && reward != null && D.Enabled(reward) && !D.Loading(reward)) return "close_reward";
            if (System.Object.ReferenceEquals(ownedBox, box) && box != null && D.Enabled(box) && !D.Loading(box)) return "close_message";
            if (target != null || Pending(reward) || Pending(box) || Pending(special) || !Idle()) return "";
            // OnlineLottery.recall can close the event page at the daily cap.
            if (opened && D.Static(Model.GameType("MainUI"), "m_subui") == null && !Pending(panel))
                return stockPageClosed && Candidate() != null ? "open" : "close";
            if (!ReadyPanel()) return "";
            return Candidate() == null ? "close" : "claim";
        }
        private Row Candidate()
        { if (processedWithoutReply || entryUnconfirmed) return null;
            foreach (Row row in rows) if (InScope(row) && row.State == "AVAILABLE" && row.Method != "" &&
            !completedProgress.Contains(ProgressKey(row)) && !processedOnline.Contains(row.Key)) return row; return null; }
        private bool InScope(Row row)
        {
            bool online = row.Kind == "online" || row.Kind == "online_enter";
            if (task == OnlineOnlyTask) return online;
            if (task == FreeEventsTask) return !online && (row.Kind != "zhaoyun" || row.ResourceCost == 0);
            return true;
        }
        private bool ReadyPanel()
        { return panel != null && System.Object.ReferenceEquals(D.Static(Model.GameType("MainUI"), "m_subui"), panel) && D.Enabled(panel) && !D.Loading(panel); }
        private bool Home()
        { return EntryGate() == ""; }
        private string EntryGate()
        {
            if (root == null || !root.gameObject.activeInHierarchy || (string)D.Read(root, "LevelID") != "Capital") return "event_rewards_capital_required";
            if (main == null || !D.Enabled(main)) return "event_rewards_main_not_ready";
            if (Pending(reward)) return "event_rewards_existing_reward";
            if (Pending(box)) return "event_rewards_existing_message";
            if (Pending(special)) return "event_rewards_existing_special_reward";
            object selected = D.Static(Model.GameType("MainUI"), "m_subui");
            if (selected == null) return "";
            if (!System.Object.ReferenceEquals(selected, premium) && !System.Object.ReferenceEquals(selected, vip) && !System.Object.ReferenceEquals(selected, timed))
                return "event_rewards_unknown_subui:" + selected.GetType().Name;
            Component known = selected as Component;
            return known != null && D.Enabled(known) && !D.Loading(known) ? "" : "event_rewards_existing_panel_not_ready";
        }
        private bool Idle()
        {
            if (client == null || D.Static(Model.FirstType("game.net.tcp_base"), "connection_state").ToString() != "OK" || Bool(client, "m_bConnectLost") || D.Bool(D.Static(Model.FirstType("Global"), "relogin"))) return false;
            if (((IList)D.Read(client, "messages")).Count != 0 || ((IList)D.Read(client, "m_sendDatas")).Count != 0) return false;
            foreach (DictionaryEntry entry in (IDictionary)D.Read(client, "m_handlers")) if (Convert.ToSingle(D.Read(entry.Value, "life"), CultureInfo.InvariantCulture) > 0) return false;
            return true;
        }
        private static bool Pending(Component ui) { return ui != null && (D.Visible(ui) || D.Loading(ui)); }
        private static IDictionary Templates(string name)
        { return (IDictionary)D.Method(Model.FirstType("Global"), "getTemplates", new[] { typeof(string) }, true, typeof(Hashtable)).Invoke(null, new object[] { name }); }
        private static int PropertyInt(object value, string name)
        { return D.Int(D.Method(value.GetType(), "get_" + name, Type.EmptyTypes, false, typeof(byte)).Invoke(value, null)); }
        private static bool InvokeBool(object value, string name)
        { return D.Bool(D.Method(value.GetType(), name, Type.EmptyTypes, false, typeof(bool)).Invoke(value, null)); }
        private static bool InvokeBool(object value, string name, int arg)
        { return D.Bool(D.Method(value.GetType(), name, new[] { typeof(int) }, false, typeof(bool)).Invoke(value, new object[] { arg })); }
        // dbdata models expose fields; protobuf messages expose properties.
        private static object Value(object value, string name)
        {
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(value);
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(value, null);
            }
            throw new ProbeFailure("event_rewards_field_schema:" + name);
        }
        private static int Int(object value, string name) { return D.Int(Value(value, name)); }
        private static uint UInt(object value, string name) { return D.UInt(Value(value, name)); }
        private static bool Bool(object value, string name) { return D.Bool(Value(value, name)); }

        internal void Stop(string detail)
        { stopped = true; queued = null; status = attempted && target != null ? "UNKNOWN" : "STOPPED"; reason = detail; if (reply != null) reply.Restore(); Publish(); }
        internal void NotInvoked(string detail)
        { queued = null; notInvoked = sequence; GuiInvocationAttempted = false; reason = detail; Publish(); }
        private void Publish()
        { string value = Json().Replace("\"revision\":" + Revision + ",", "\"revision\":0,"); if (stamp != value) { stamp = value; Revision++; } }
        internal string Json()
        {
            StringBuilder list = new StringBuilder();
            foreach (Row row in rows) { if (list.Length != 0) list.Append(','); list.Append(row.Json()); }
            StringBuilder claimed = new StringBuilder(), receiptList = new StringBuilder(), processed = new StringBuilder();
            foreach (DictionaryEntry entry in completed) { if (claimed.Length != 0) claimed.Append(','); claimed.Append(D.Q((string)entry.Key)); }
            foreach (DictionaryEntry entry in receipts) { if (receiptList.Length != 0) receiptList.Append(','); receiptList.Append("{\"key\":").Append(D.Q((string)entry.Key)).Append(",\"body_base64\":").Append(D.Q((string)entry.Value)).Append('}'); }
            foreach (DictionaryEntry entry in processedOnline) { if (processed.Length != 0) processed.Append(','); processed.Append(D.Q((string)entry.Key)); }
            return "{\"schema\":1,\"revision\":" + Revision + ",\"task_id\":" + task + ",\"role_id\":" + D.Q(role.ToString(CultureInfo.InvariantCulture)) +
                ",\"cycle\":" + cycle + ",\"status\":" + D.Q(queued == null ? status : "QUEUED") + ",\"reason\":" + D.Q(reason) + ",\"gate\":" + D.Q(gate) +
                ",\"next_action\":" + D.Q(Offer()) + ",\"entry_gate\":" + D.Q(EntryGate()) + ",\"network_idle\":" + D.B(gate == "" && Idle()) + ",\"operation_id\":" + D.Q(operationId) + ",\"sequence\":" + sequence + ",\"mutation_attempted\":" + D.B(attempted) +
                ",\"gui_invocation_attempted\":" + D.B(GuiInvocationAttempted) + ",\"gui_not_invoked_sequence\":" + notInvoked + ",\"task_confirmed\":" + D.B(confirmed) +
                ",\"stock_callback_returned\":" + D.B(stockReturned || reply != null && reply.Returned) + ",\"cleanup_pending\":" + D.B(hold) + ",\"reward_closed\":" + D.B(closed) +
                ",\"processed_without_reply\":" + D.B(processedWithoutReply) +
                ",\"page_state_confirmed\":" + D.B(pageStateConfirmed && !processedWithoutReply) +
                ",\"request_issued\":" + D.B(requestIssued) + ",\"reply_received\":" + D.B(replyReceived || reply != null && reply.Received) +
                ",\"entry_unconfirmed\":" + D.B(entryUnconfirmed) + ",\"claim_request_issued\":" + D.B(claimRequestIssued) +
                ",\"processed_keys\":[" + processed + "]" +
                ",\"target_key\":" + D.Q(target == null ? "" : target.Key) + ",\"receipt_base64\":" + D.Q(receiptBase64) + ",\"claim_count\":" + claimCount + ",\"claimed_keys\":[" + claimed + "],\"receipts\":[" + receiptList + "],\"rows\":[" + list + "],\"gold_cost\":0}";
        }
    }
}
