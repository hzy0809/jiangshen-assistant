using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate D DailyRefineContextProvider();
    internal delegate bool DailyRefineCommitCheck();
    internal delegate bool DailyRefineInvocationGate(DailyRefineCommitCheck prepare, DailyRefineCommitCheck check);
    internal delegate DevelopmentCallbackCapture DailyRefineCallbackInstall(object client, object handler,
        string operationId, int sequence, Delegate original, Action<byte[]> observe, out string why);

    internal sealed class DailyRefineReceipt
    {
        internal const ushort Opcode = 991;
        internal const ushort ExperienceOpcode = 860;
        internal uint Result, Cost;
        internal int Practice;

        // The request is empty. The reply requires result, cost and Treasure,
        // whose param1 is an int32 encoded as ZigZag.
        internal static DailyRefineReceipt Decode(byte[] body)
        {
            if (body == null || body.Length > 48) throw new FormatException("refine_reply_bound");
            DailyRefineReceipt reply = new DailyRefineReceipt();
            int offset = 0, seen = 0;
            while (offset < body.Length)
            {
                int tag = body[offset++], field = tag == 8 ? 1 : tag == 16 ? 2 : tag == 26 ? 3 : 0;
                if (field == 0 || (seen & 1 << field) != 0) throw new FormatException("refine_reply_field");
                seen |= 1 << field;
                uint value = ReadUInt(body, ref offset, body.Length);
                if (field == 1) reply.Result = value;
                else if (field == 2) reply.Cost = value;
                else
                {
                    if (value > body.Length - offset) throw new FormatException("refine_treasure_bound");
                    int end = offset + (int)value;
                    reply.Practice = ReadTreasure(body, ref offset, end);
                }
            }
            if (seen != 14 || reply.Result == 0 && (reply.Cost == 0 || reply.Cost > Int32.MaxValue || reply.Practice <= 0))
                throw new FormatException("refine_reply_values");
            return reply;
        }

        internal static DailyRefineReceipt DecodeExperience(byte[] body, int practice)
        {
            if (body == null || body.Length > 12) throw new FormatException("refine_experience_reply_bound");
            DailyRefineReceipt reply = new DailyRefineReceipt();
            int offset = 0, seen = 0;
            uint critical = 0;
            while (offset < body.Length)
            {
                int tag = body[offset++], field = tag == 8 ? 1 : tag == 16 ? 2 : 0;
                if (field == 0 || (seen & 1 << field) != 0) throw new FormatException("refine_experience_reply_field");
                seen |= 1 << field;
                uint value = ReadUInt(body, ref offset, body.Length);
                if (field == 1) reply.Result = value;
                else critical = value;
            }
            if (seen != 6 || reply.Result > 1 || critical > 2 || practice <= 0)
                throw new FormatException("refine_experience_reply_values");
            if (reply.Result == 0)
                reply.Practice = critical == 1 ? checked(practice * 3) / 2 : critical == 2 ? checked(practice * 2) : practice;
            return reply;
        }

        private static int ReadTreasure(byte[] body, ref int offset, int end)
        {
            int seen = 0, practice = 0;
            while (offset < end)
            {
                int tag = body[offset++], field = tag >> 3;
                if ((tag & 7) != 0 || field < 1 || field > 5 || (seen & 1 << field) != 0)
                    throw new FormatException("refine_treasure_field");
                seen |= 1 << field;
                uint value = ReadUInt(body, ref offset, end);
                if (field == 3) practice = (int)(value >> 1) ^ -(int)(value & 1);
                if (field == 5 && value > 1) throw new FormatException("refine_treasure_bool");
            }
            if ((seen & 30) != 30) throw new FormatException("refine_treasure_values");
            return practice;
        }

        private static uint ReadUInt(byte[] body, ref int offset, int end)
        {
            uint value = 0;
            for (int shift = 0; shift <= 28 && offset < end; shift += 7)
            {
                byte part = body[offset++];
                if (shift == 28 && part > 15) break;
                value |= (uint)(part & 127) << shift;
                if ((part & 128) == 0 && (shift == 0 || part != 0)) return value;
                if ((part & 128) == 0) break;
            }
            throw new FormatException("refine_reply_scalar");
        }
    }

    internal sealed class DailyRefineModel
    {
        private delegate Frame CommitCall();
        internal const int TaskId = 21;
        private const int CoinLimit = 20;
        private const uint WaitMs = 18000;
        private const ushort ActivityOpcode = 355;
        private static readonly Type[] ReplyArgs = { typeof(MemoryStream), typeof(object) };

        private sealed class Frame
        {
            internal D Context;
            internal Component Panel;
            internal DailyTransport Transport;
            internal object Sub, Effects, Rows, Row, Template, Rewards, Reward, ListedRows, Listed;
            internal object Prices, QuoteKey, Quote, Rates, RateQuote, Handler, ActivityHandler;
            internal Delegate ActivityOriginal;
            internal ulong Role;
            internal uint Now, Cycle, Next, GoldConsumed, Cost;
            internal long Money;
            internal int Current, Required, Refresh, ListedCurrent, ListedRefresh, Gold, Bound, Experience, Practice,
                Level, Coins, XpExchanges, GoldExchanges, EffectCount, EffectPractice, QueuedCoinReplies, PriceCount,
                ExperienceCost, BasePractice, ExchangeLevel, MaxLevel, Rate, RateCount;
            internal byte ActivityCategory;
            internal DateTime VisibleAt;
            internal bool Selected, Visible, Enabled, Loading, Idle, Sending, ConflictingExchange, HasClose, CoinTab, Unlocked,
                ExperienceRoute, FeatureUnlocked;
            internal string Error = "", Overlay = "", QuoteGate = "";
            internal ushort Opcode { get { return ExperienceRoute ? DailyRefineReceipt.ExperienceOpcode : DailyRefineReceipt.Opcode; } }
            internal bool MatchingTab { get { return CoinTab != ExperienceRoute; } }
            internal bool HasExperienceQuote { get { return ExperienceRoute && Prices != null && Quote != null &&
                Rates != null && RateQuote != null && QuoteGate == ""; } }

            internal bool SameOwner(Frame before)
            {
                return before != null && Context != null && Context.SameOwnership(before.Context) &&
                    Role != 0 && Role == before.Role && Panel != null && ReferenceEquals(Panel, before.Panel) &&
                    ReferenceEquals(Effects, before.Effects) && Transport != null && Transport.Same(before.Transport) &&
                    Cycle != 0 && Cycle == before.Cycle && Next == before.Next && Now >= before.Now;
            }

            internal bool SameWindow(Frame before)
            { return Selected && before.Selected && VisibleAt == before.VisibleAt && !HasClose && !before.HasClose; }

            internal bool SameTask(Frame before)
            {
                return ReferenceEquals(Rows, before.Rows) && ReferenceEquals(Rewards, before.Rewards) &&
                    ReferenceEquals(Template, before.Template) && ReferenceEquals(Reward, before.Reward) &&
                    ReferenceEquals(ListedRows, before.ListedRows) && Required == before.Required && ActivityCategory == before.ActivityCategory;
            }

            internal bool SameProgress(Frame before)
            {
                return SameTask(before) && ReferenceEquals(Row, before.Row) && ReferenceEquals(Listed, before.Listed) &&
                    Current == before.Current && Refresh == before.Refresh &&
                    ListedCurrent == before.ListedCurrent && ListedRefresh == before.ListedRefresh;
            }

            internal bool SameProtectedResources(Frame before)
            {
                return Gold == before.Gold && Bound == before.Bound && GoldConsumed == before.GoldConsumed &&
                    Level == before.Level && GoldExchanges == before.GoldExchanges &&
                    (before.ExperienceRoute ? Money == before.Money && Coins == before.Coins :
                        Experience == before.Experience && XpExchanges == before.XpExchanges);
            }

            internal bool SameResources(Frame before)
            { return SameProtectedResources(before) && Money == before.Money && Coins == before.Coins && Practice == before.Practice &&
                Experience == before.Experience && XpExchanges == before.XpExchanges; }

            internal bool SameQuote(Frame before)
            {
                return ReferenceEquals(Prices, before.Prices) && ReferenceEquals(Quote, before.Quote) &&
                    object.Equals(QuoteKey, before.QuoteKey) && PriceCount == before.PriceCount &&
                    Cost == before.Cost && QuoteGate == before.QuoteGate && Unlocked == before.Unlocked &&
                    ExperienceRoute == before.ExperienceRoute && FeatureUnlocked == before.FeatureUnlocked &&
                    ExperienceCost == before.ExperienceCost && BasePractice == before.BasePractice &&
                    ExchangeLevel == before.ExchangeLevel && MaxLevel == before.MaxLevel && Rate == before.Rate &&
                    ReferenceEquals(Rates, before.Rates) && ReferenceEquals(RateQuote, before.RateQuote) && RateCount == before.RateCount;
            }

            internal bool SameAdmission(Frame before)
            {
                return Error == "" && before.Error == "" && SameOwner(before) && SameProgress(before) && SameResources(before) &&
                    SameQuote(before) && ReferenceEquals(Sub, before.Sub) && Selected == before.Selected &&
                    VisibleAt == before.VisibleAt && Visible == before.Visible && Enabled == before.Enabled && Loading == before.Loading &&
                    Overlay == before.Overlay && CoinTab == before.CoinTab && HasClose == before.HasClose &&
                    Sending == before.Sending && Idle == before.Idle && ConflictingExchange == before.ConflictingExchange &&
                    EffectCount == before.EffectCount && EffectPractice == before.EffectPractice && QueuedCoinReplies == before.QueuedCoinReplies &&
                    ReferenceEquals(Handler, before.Handler) && ReferenceEquals(ActivityHandler, before.ActivityHandler) &&
                    ReferenceEquals(ActivityOriginal, before.ActivityOriginal);
            }
        }

        private sealed class Intent
        {
            internal Frame Baseline;
            internal string Id, Action;
            internal int Sequence;
        }

        private readonly DailyRefineCallbackInstall install, installActivity;
        private readonly DailyRefreshAdmission refresh;
        private readonly Hashtable used = new Hashtable();
        private Frame frame = new Frame { Error = "refine_unobserved" }, origin, opened, sent;
        private Intent queued;
        private DevelopmentCallbackCapture callback, activity;
        private DailyRefineReceipt receipt;
        private object handler;
        private int revision, lastSequence, since, activityDispatch, activityRefresh, expectedPractice;
        private bool stopped, uiHeld, attempted, returned, progressObserved, closed, creditObserved, guiInvoked;
        private string operationId = "", status = "IDLE", reason = "refine_unobserved", stamp = "";

        internal DailyRefineModel(DailyRefineCallbackInstall installer, DailyRefineCallbackInstall activityInstaller)
            : this(installer, activityInstaller, DailyRefreshGate.Read) { }

#if JSB_DAILY_REFINE_TEST
        internal
#else
        private
#endif
        DailyRefineModel(DailyRefineCallbackInstall installer, DailyRefineCallbackInstall activityInstaller, DailyRefreshAdmission refreshAdmission)
        { install = installer; installActivity = activityInstaller; refresh = refreshAdmission; }

        internal int Revision { get { return revision; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool GuiInvocationAttempted { get { return guiInvoked; } }
        internal bool UiOwnershipHeld { get { return uiHeld; } }
        internal bool PanelClosed { get { return closed; } }
        internal bool Rejected { get { return status == "REJECTED"; } }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal bool Unresolved { get { return OutcomeUnknown || status == "PENDING"; } }
        internal bool Busy { get { return uiHeld || queued != null || callback != null || activity != null || Unresolved ||
            status == "OPENING" || status == "OPEN" || status == "SETTLED"; } }

        internal void Capture(D context)
        {
            if (stopped || OutcomeUnknown) { Detach(); return; }
            if (status != "PENDING") Detach();
            Frame previous = frame;
            try
            {
                frame = Read(context, false);
                if (frame.Error != "refine_client_busy" || Expired())
                {
                    if (status == "OPENING") ObserveOpen();
                    else if (status == "OPEN" && (!StableOpen(frame, opened) || !frame.SameWindow(opened) ||
                        !frame.Visible || !frame.Enabled || frame.Loading || frame.CoinTab != opened.CoinTab || Expired()))
                        Finish("REJECTED", "refine_owned_panel_changed");
                    else if (status == "PENDING") ObserveReply();
                    else if (status == "SETTLED" && (!CleanupBoundary(frame) || Expired()))
                        Finish("COMPLETED", "refine_cleanup_relinquished");
                    if (status == "REJECTED" || status == "COMPLETED") ReleaseHiddenPanel(frame);
                }
            }
            catch (Exception error)
            {
                frame = new Frame { Error = error is ProbeFailure ? error.Message : "refine_observation_failed" };
                if (Busy) Finish(progressObserved ? "COMPLETED" : attempted ? "UNKNOWN" : "REJECTED", frame.Error);
            }
            PublishRevision(previous);
        }

        internal bool Queue(int taskId, ulong role, string id, int sequence, int expectedRevision, string action, out string why)
        {
            why = "refine_admission";
            if (taskId != TaskId || role == 0 || role != frame.Role || sequence <= lastSequence ||
                expectedRevision != revision || queued != null || stopped || !new DevelopmentCommand { OperationId = id }.ValidId() ||
                action != "open" && action != "experience" && action != "refine" && action != "close") return false;
            if (action == "open" ? Busy || used.Count >= 4096 || used.Contains(id) : operationId != id) return false;
            why = Gate(frame, action);
            if (why != "") return false;
            lastSequence = sequence;
            queued = new Intent { Baseline = frame, Id = id, Action = action, Sequence = sequence };
            if (action == "open")
            {
                used.Add(id, true);
                operationId = id; status = "IDLE"; reason = "";
                attempted = returned = progressObserved = creditObserved = closed = false;
                origin = opened = sent = null; handler = null; receipt = null;
                activityDispatch = activityRefresh = expectedPractice = 0;
            }
            PublishRevision(frame);
            return true;
        }

        internal bool ExecuteGui(DailyRefineContextProvider freshContext, DevelopmentPublication publish,
            DailyRefineInvocationGate begin, out string why)
        {
            guiInvoked = false;
            why = "refine_not_queued";
            Intent intent = queued;
            if (intent == null || stopped) return false;
            if (!D.Gui || freshContext == null || publish == null || begin == null) return Reject("refine_gui_required", out why);
            object client = intent.Baseline.Context.Client;
            if (!Monitor.TryEnter(client, 0)) return Reject("refine_client_busy", out why);
            bool publishing = false;
            try
            {
                if (!Admit(intent, Read(freshContext(), false), out why)) return Reject(why, out why);
                publishing = true;
                publish();
                publishing = false;
                if (stopped || queued != intent) { why = "refine_cancelled_before_call"; return false; }
                Frame current = Read(freshContext(), false);
                if (!D.Gui) return Reject("refine_gui_required", out why);
                if (!Admit(intent, current, out why)) return Reject(why, out why);
                bool began = false;
                CommitCall start = delegate
                {
                    Frame committed = Commit(intent, freshContext, begin);
                    began = committed != null;
                    return committed;
                };
                if (intent.Action == "open") Open(start);
                else if (intent.Action == "experience") SelectExperience(start);
                else if (intent.Action == "refine") Refine(current, intent, start);
                else Close(start);
                why = !began || status == "UNKNOWN" || status == "REJECTED" ? reason : "";
                PublishRevision(frame);
                return began && why == "";
            }
            catch (Exception)
            {
                if (publishing) throw;
                return Reject(attempted && !progressObserved ? "refine_send_or_binding_failed" : "refine_gui_call_failed", out why);
            }
            finally { Monitor.Exit(client); }
        }

        private bool Admit(Intent intent, Frame current, out string why)
        {
            if (intent.Action != "open" && Expired()) { why = "refine_owned_action_timed_out"; return false; }
            why = Gate(current, intent.Action);
            if (why != "") return false;
            if (current.SameAdmission(intent.Baseline)) return true;
            why = "refine_gui_baseline_changed";
            return false;
        }

        private Frame Commit(Intent intent, DailyRefineContextProvider freshContext, DailyRefineInvocationGate begin)
        {
            Frame committed = null;
            string failure = "refine_cancelled_before_call";
            bool prepared = false, checkedLive = false;
            DailyRefineCommitCheck prepare = delegate
            {
                if (prepared) { committed = null; return false; }
                prepared = true;
                if (stopped || queued != intent) return false;
                if (!D.Gui) { failure = "refine_gui_required"; return false; }
                if (intent.Action != "open" && Expired()) { failure = "refine_owned_action_timed_out"; return false; }
                Frame sampled = Read(freshContext(), false, intent.Action == "refine" ? intent : null);
                if (!D.Gui) { failure = "refine_gui_required"; return false; }
                if (!Admit(intent, sampled, out failure)) return false;
                committed = sampled;
                return true;
            };
            // The controller rereads the exact wire command between these
            // checks, then verifies its lease before returning.
            DailyRefineCommitCheck check = delegate
            {
                if (checkedLive || committed == null || stopped || queued != intent ||
                    !D.Gui || !committed.Context.SameIdentity(committed.Context) ||
                    !SameClock(committed) ||
                    !committed.Transport.Same(DailyTransport.Read(committed.Context.Client)) ||
                    !ReferenceEquals(committed.Context.SubUi, committed.Sub) ||
                    !ReferenceEquals(committed.Context.GlobalUi.gameObject.GetComponent("ArmyStarRefineUI"), committed.Panel) ||
                    D.Visible(committed.Panel) != committed.Visible || D.Enabled(committed.Panel) != committed.Enabled ||
                    D.Loading(committed.Panel) != committed.Loading || Field<bool>(committed.Panel, "sended") != committed.Sending ||
                    committed.Selected && (Feature() != "star_refine" || Field<DateTime>(committed.Panel, "visible_time") != committed.VisibleAt) ||
                    !SameCommitFields(committed, intent) ||
                    intent.Action != "open" && Expired())
                {
                    committed = null;
                    failure = "refine_commit_boundary_changed";
                    return false;
                }
                return checkedLive = true;
            };
            if (stopped || queued != intent || !begin(prepare, check) || committed == null || !checkedLive || stopped || queued != intent)
            {
                string ignored;
                Reject(failure == "" ? "refine_cancelled_before_call" : failure, out ignored);
                return null;
            }

            queued = null; since = Environment.TickCount;
            return committed;
        }

        private bool SameClock(Frame before)
        {
            object role = before.Context.Character;
            uint now, day, cycle;
            return refresh(role, out now, out day, out cycle) == "" && cycle == before.Cycle && now >= before.Now &&
                Field<uint>(role, "next_resource_time") == before.Next;
        }

        private bool SameCommitFields(Frame before, Intent intent)
        {
            object role = before.Context.Character;
            Frame resources = new Frame();
            ReadResources(resources, role);
            // These are the stock template getters' backing tables. The
            // final check keeps the prepared quote and reads only its entry.
            Type loader = before.Template.GetType().Assembly.GetType("Database.db_loader", true);
            IDictionary tables = D.Static(loader, "Global") as IDictionary;
            if (!resources.SameResources(before) || tables == null || !SameTaskRows(before, tables) ||
                intent.Action != "close" && !SameQuoteEntry(before, tables) ||
                (D.Read(before.Panel, "OnClose") != null) != before.HasClose || CoinTab(before.Panel) != before.CoinTab ||
                !ReferenceEquals(D.Read(before.Panel, "fly_effects"), before.Effects) ||
                ((IDictionary)before.Effects).Count != before.EffectCount)
                return false;

            IDictionary handlers = Handlers(before.Context.Client);
            object push = handlers[ActivityOpcode];
            if (!ReferenceEquals(push, before.ActivityHandler) || handlers.Contains(before.Opcode) || Conflicts(handlers, before.Opcode))
                return false;
            if (intent.Action == "refine") return PristineActivityWrapper(push, intent);
            Delegate original = D.Read(push, "handler") as Delegate;
            return ReferenceEquals(original, before.ActivityOriginal) && DevelopmentCallbackCapture.IsDailyActivityPush(push, original);
        }

        private static bool SameTaskRows(Frame f, IDictionary tables)
        {
            IDictionary tasks = tables["activity"] as IDictionary;
            object role = f.Context.Character;
            return tasks != null && ReferenceEquals(tasks[(uint)TaskId], f.Template) &&
                Field<byte>(f.Template, "id") == TaskId && Field<bool>(f.Template, "active") &&
                Field<int>(f.Template, "cond_param") == f.Required && Field<byte>(f.Template, "idreward") == f.ActivityCategory &&
                ReferenceEquals(D.Read(role, "acts"), f.Rows) && ReferenceEquals(D.Read(role, "act_rewards"), f.Rewards) &&
                ReferenceEquals(((IDictionary)f.Rewards)[(uint)f.ActivityCategory], f.Reward) &&
                Field<byte>(f.Reward, "idact_reward") == f.ActivityCategory && ReferenceEquals(D.Read(f.Reward, "acts"), f.ListedRows) &&
                SameTaskRow(((IDictionary)f.Rows)[(uint)TaskId], f.Row, f.Template, f.Current, f.Refresh) &&
                SameTaskRow(((IDictionary)f.ListedRows)[(uint)TaskId], f.Listed, f.Template, f.ListedCurrent, f.ListedRefresh);
        }

        private static bool SameTaskRow(object row, object before, object template, int count, int refreshTime)
        {
            return ReferenceEquals(row, before) && Field<byte>(row, "idactivity") == TaskId &&
                ReferenceEquals(D.Read(row, "temp"), template) &&
                Field<byte>(row, "condition_param") == count && Field<int>(row, "refresh_time") == refreshTime;
        }

        private static bool SameQuoteEntry(Frame f, IDictionary tables)
        {
            object role = f.Context.Character;
            if ((bool)D.Method(role.GetType(), "checkUI", new[] { typeof(string) }, false, typeof(bool))
                    .Invoke(role, new object[] { "star_refine" }) != f.FeatureUnlocked ||
                (bool)D.Method(role.GetType(), "checkUI", new[] { typeof(uint) }, false, typeof(bool))
                    .Invoke(role, new object[] { 91u }) != f.Unlocked)
                return false;
            if (f.ExperienceRoute)
            {
                IDictionary experiencePrices = tables["practice_exchange"] as IDictionary;
                IDictionary rates = tables["practice_exchange_rate"] as IDictionary;
                return ReferenceEquals(experiencePrices, f.Prices) && experiencePrices != null && experiencePrices.Count == f.PriceCount &&
                    ReferenceEquals(experiencePrices[(uint)f.ExchangeLevel], f.Quote) &&
                    Field<ushort>(f.Quote, "level") == f.ExchangeLevel &&
                    Field<uint>(f.Quote, "practice") == f.BasePractice &&
                    ReferenceEquals(rates, f.Rates) && rates != null && rates.Count == f.RateCount &&
                    ReferenceEquals(rates[(uint)(f.XpExchanges + f.GoldExchanges + 1)], f.RateQuote) &&
                    Field<sbyte>(f.RateQuote, "id") == f.XpExchanges + f.GoldExchanges + 1 &&
                    Field<int>(f.RateQuote, "rate") == f.Rate &&
                    Field<uint>(f.Quote, "exp") <= Int32.MaxValue &&
                    Field<uint>(f.Quote, "exp") * (long)f.Rate <= Int32.MaxValue &&
                    Field<uint>(f.Quote, "exp") * (long)f.Rate / 100 == f.ExperienceCost &&
                    (int)D.Static(Model.FirstType("Global"), "PLAYER_LEVEL_MAX") == f.MaxLevel &&
                    (!f.Selected || Field<uint>(f.Panel, "exchangeLevel") == f.ExchangeLevel);
            }
            IDictionary prices = tables["practice_money_exchange"] as IDictionary;
            return ReferenceEquals(prices, f.Prices) && prices != null && prices.Count == f.PriceCount &&
                ReferenceEquals(prices[f.QuoteKey], f.Quote) && Field<ushort>(f.Quote, "level") == f.Level &&
                Field<ushort>(f.Quote, "times") == f.Coins + 1 && Field<uint>(f.Quote, "money") == f.Cost;
        }

        private void Open(CommitCall commit)
        {
            MethodInfo open = D.Method(Model.GameType("MainUI"), "_starRefine", Type.EmptyTypes, true, typeof(void));
            Frame current = commit();
            if (current == null) return;
            origin = current; uiHeld = true; guiInvoked = true;
            open.Invoke(null, null);
            if (stopped) return;
            if (!ReferenceEquals(current.Context.SubUi, current.Panel) || Feature() != "star_refine")
                Finish("REJECTED", "refine_loader_did_not_take_ownership");
            else { status = "OPENING"; reason = "refine_loader_waiting"; }
        }

        private void Refine(Frame current, Intent intent, CommitCall commit)
        {
            Type type = current.Panel.GetType();
            MethodInfo send = D.Method(type, current.ExperienceRoute ? "cs_practice_exchange" : "cs_practice_money_exchange", Type.EmptyTypes, false, typeof(void));
            MethodInfo response = D.Method(type, current.ExperienceRoute ? "sc_practice_exchange" : "sc_practice_money_exchange", ReplyArgs, false, typeof(void));
            if (install == null || installActivity == null || send.DeclaringType != type || !send.IsPublic ||
                response.DeclaringType != type || !response.IsPublic)
            { Finish(progressObserved ? "COMPLETED" : "REJECTED", "refine_stock_handler_unavailable"); return; }
            string why;
            activity = installActivity(current.Context.Client, current.ActivityHandler, intent.Id, intent.Sequence,
                current.ActivityOriginal, ObserveActivityBody, out why);
            if (activity == null) { Finish(progressObserved ? "COMPLETED" : "REJECTED", why); return; }
            if (!CaptureMatches(activity, ActivityOpcode, intent))
            { Finish(progressObserved ? "COMPLETED" : "REJECTED", "refine_activity_callback_identity"); return; }
            current = commit();
            if (current == null) return;

            // This checklist owns only the ordinary experience sender (860).
            // A settled exchange may reuse this exact panel. Its receipt has
            // already been published under the preceding refine sequence;
            // reset round witnesses only after the next quote is committed.
            returned = progressObserved = creditObserved = closed = false;
            receipt = null; handler = null;
            activityDispatch = activityRefresh = expectedPractice = 0;
            sent = current; attempted = true; guiInvoked = true;
            send.Invoke(current.Panel, null);
            if (stopped) return;
            if (!current.Transport.Same(DailyTransport.Read(current.Context.Client)))
            { Finish("UNKNOWN", "refine_transport_changed_after_send"); return; }
            IDictionary handlers = Handlers(current.Context.Client);
            handler = handlers[current.Opcode];
            Delegate original = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") ||
                Field<float>(handler, "life") != 10f || D.Read(handler, "arg") != null || original == null ||
                original.GetInvocationList().Length != 1 || original.Method != response ||
                !ReferenceEquals(original.Target, current.Panel) || !Field<bool>(current.Panel, "sended") || Conflicts(handlers, current.Opcode))
            { Finish("UNKNOWN", "refine_stock_send_unobserved"); return; }
            callback = install(current.Context.Client, handler, intent.Id, intent.Sequence, original, ObserveReplyBody, out why);
            if (callback == null) { Finish("UNKNOWN", why); return; }
            if (!CaptureMatches(callback, current.Opcode, intent)) { Finish("UNKNOWN", "refine_callback_identity"); return; }
            status = "PENDING"; reason = "refine_waiting_stock_callback";
        }

        private static bool CaptureMatches(DevelopmentCallbackCapture capture, int opcode, Intent intent)
        { return capture != null && capture.Opcode == opcode && capture.OperationId == intent.Id && capture.Sequence == intent.Sequence && capture.StillInstalled(); }

        private void SelectExperience(CommitCall commit)
        {
            Frame current = commit();
            if (current == null) return;
            // Stock Web_OnGUI's experience-tab button performs exactly this
            // assignment. It sends no request and does not enter the gold route.
            FieldInfo tab = current.Panel.GetType().GetField("m_refinetype", D.Instance);
            Type type = current.Panel.GetType().GetNestedType("RefineType");
            if (tab == null || type == null || tab.FieldType != type || !type.IsEnum)
            { Finish("REJECTED", "refine_stock_tab_schema"); return; }
            guiInvoked = true;
            tab.SetValue(current.Panel, Enum.Parse(type, "jingyan"));
            Frame selected = Read(current.Context, false);
            if (!StableOpen(selected, current) || !selected.SameWindow(current) || !selected.MatchingTab)
            { Finish("REJECTED", "refine_experience_tab_unconfirmed"); return; }
            frame = opened = selected; reason = ""; since = Environment.TickCount;
        }

        private bool PristineActivityWrapper(object handler, Intent intent)
        {
            if (!CaptureMatches(activity, ActivityOpcode, intent) || Field<float>(handler, "life") != -1f ||
                D.Read(handler, "arg") != null) return false;
            DevelopmentCallbackWitness witness = activity.Read();
            return witness.Armed && witness.Calls == 0 && !WitnessFailed(witness);
        }

        private void Close(CommitCall commit)
        {
            MethodInfo close = D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void));
            Frame current = commit();
            if (current == null) return;
            guiInvoked = true;
            close.Invoke(null, new object[] { null });
            if (stopped) return;
            closed = ReleaseHiddenPanel(Read(current.Context, false));
            Finish("COMPLETED", closed ? "refine_progress_observed" : "refine_close_unconfirmed");
        }

        private bool ReleaseHiddenPanel(Frame f)
        {
            // A rejected open or failed close can leave the exact stock panel
            // selected. Keep the hold until a clean same-owner state is seen.
            if (!uiHeld || callback != null || activity != null || f.Error != "" || !f.SameOwner(origin) ||
                f.Sub != null || f.Visible || f.Loading || f.Sending || f.EffectCount != 0 ||
                f.Handler != null || f.ConflictingExchange || f.QueuedCoinReplies != 0 || f.ActivityOriginal == null)
                return false;
            uiHeld = false;
            return true;
        }

        private static bool StableOpen(Frame f, Frame before)
        {
            return f.Error == "" && f.SameOwner(before) && f.SameProgress(before) && f.SameResources(before) &&
                f.SameQuote(before) && f.ActivityOriginal != null && ReferenceEquals(f.ActivityHandler, before.ActivityHandler) &&
                ReferenceEquals(f.ActivityOriginal, before.ActivityOriginal) && f.Handler == null && !f.ConflictingExchange &&
                !f.Sending && !f.HasClose && f.EffectCount == 0;
        }

        private void ObserveOpen()
        {
            if (!StableOpen(frame, origin) || !frame.Selected || Expired())
            { Finish("REJECTED", frame.Error != "" ? frame.Error : "refine_loader_changed_or_timed_out"); return; }
            if (frame.Loading || !frame.Visible || !frame.Enabled) return;
            if (frame.VisibleAt <= origin.VisibleAt)
            { Finish("REJECTED", "refine_loader_generation_or_tab_unproven"); return; }
            if (frame.ExperienceRoute && Field<uint>(frame.Panel, "exchangeLevel") != frame.ExchangeLevel) return;
            opened = frame; status = "OPEN"; reason = ""; since = Environment.TickCount;
        }

        private bool PendingBoundary(Frame f)
        {
            return f.Error == "" && f.SameOwner(sent) && f.SameTask(sent) && f.SameWindow(sent) &&
                f.Visible && !f.Loading && f.MatchingTab && f.ExperienceRoute == sent.ExperienceRoute &&
                !f.ConflictingExchange && f.SameProtectedResources(sent) &&
                f.Current >= sent.Current && f.Current <= Math.Min(sent.Current + 1, sent.Required) &&
                f.ListedCurrent >= sent.ListedCurrent && f.ListedCurrent <= Math.Min(sent.ListedCurrent + 1, sent.Required);
        }

        private void ObserveActivityBody(byte[] body)
        {
            if (!attempted || sent == null || !PendingBoundary(Read(sent.Context, true)))
                throw new FormatException("refine_activity_boundary_changed");
            int refreshTime;
            int? count = GuildReceipt.Progress(body, TaskId, out refreshTime);
            if (!count.HasValue) return;
            count = Math.Min(count.Value, sent.Required); // Stock Activity constructor clamps this counter.
            if (count == sent.Current && refreshTime == sent.Refresh && activityDispatch == 0) return;
            if (count != Math.Min(sent.Current + 1, sent.Required) || activityDispatch != 0 && refreshTime != activityRefresh)
                throw new FormatException("refine_activity_progress_conflict");
            if (activityDispatch == 0)
            {
                activityRefresh = refreshTime;
                activityDispatch = activity.Read().Calls;
            }
        }

        private void ObserveReplyBody(byte[] body)
        {
            Frame observed = Read(sent.Context, true);
            if (!PendingBoundary(observed) || !BeforeDebit(observed) || observed.Handler != null || observed.QueuedCoinReplies != 0 ||
                !observed.Sending || observed.EffectCount != 0)
                throw new FormatException("refine_callback_boundary_changed");
            if (sent.ExperienceRoute)
            {
                Type loader = sent.Template.GetType().Assembly.GetType("Database.db_loader", true);
                IDictionary tables = D.Static(loader, "Global") as IDictionary;
                if (tables == null || !SameQuoteEntry(sent, tables))
                    throw new FormatException("refine_experience_callback_quote_changed");
            }
            receipt = sent.ExperienceRoute ? DailyRefineReceipt.DecodeExperience(body, sent.BasePractice) : DailyRefineReceipt.Decode(body);
            if (receipt.Result != 0) return;
            if (receipt.Cost != sent.Cost) throw new FormatException("refine_receipt_cost_changed");
            expectedPractice = checked(sent.Practice + receipt.Practice);
        }

        private static bool WitnessFailed(DevelopmentCallbackWitness witness)
        { return witness.Error != null || witness.NullResponse || witness.OriginalThrew; }

        private void ObserveReply()
        {
            if (Expired()) { Finish("UNKNOWN", "refine_settlement_timed_out"); return; }
            if (!PendingBoundary(frame))
            { Finish("UNKNOWN", frame.Error != "" ? frame.Error : "refine_pending_boundary_changed"); return; }
            DevelopmentCallbackWitness witness = callback.Read(), push = activity.Read();
            if (WitnessFailed(witness) || WitnessFailed(push) || witness.Calls > 1 || !activity.StillInstalled())
            { Finish("UNKNOWN", witness.Error ?? push.Error ?? "refine_stock_callback_failed"); return; }
            if (witness.Calls == 0)
            {
                if (!ReferenceEquals(frame.Handler, handler) || !callback.StillInstalled() || !frame.Sending ||
                    !BeforeDebit(frame) || frame.EffectCount != 0)
                    Finish("UNKNOWN", "refine_stock_callback_lost_or_conflicting");
                return;
            }
            if (!witness.Returned) return;
            returned = true;
            if (receipt == null || frame.Handler != null || frame.Sending || frame.QueuedCoinReplies != 0)
            { Finish("UNKNOWN", "refine_stock_readback_conflict"); return; }
            if (receipt.Result != 0)
            {
                bool unchanged = BeforeDebit(frame) && frame.SameProgress(sent) && frame.EffectCount == 0 && activityDispatch == 0;
                Finish(unchanged ? "REJECTED" : "UNKNOWN", unchanged ? "refine_server_rejected" : "refine_rejection_conflicted");
                return;
            }
            if (!ExpectedDebit(frame))
            { Finish("UNKNOWN", "refine_resource_readback_conflict"); return; }
            if (frame.EffectCount == 1 && frame.EffectPractice == receipt.Practice &&
                frame.Practice == sent.Practice && !creditObserved)
                reason = "refine_waiting_practice_credit";
            else if (frame.EffectCount == 0 && frame.Practice == expectedPractice)
                creditObserved = true;
            else
            { Finish("UNKNOWN", "refine_practice_readback_conflict"); return; }
            if (creditObserved && ProgressAdvanced(frame) &&
                (sent.Current == sent.Required && activityDispatch == 0 ||
                 activityDispatch != 0 && activity.ReturnedForDispatch(activityDispatch)))
            {
                progressObserved = true; status = "SETTLED"; reason = "refine_progress_observed"; since = Environment.TickCount;
                Detach();
                // This frame was sampled while PENDING. Populate the next
                // ordinal's live quote before publishing the first SETTLED offer.
                ReadQuote(frame, frame.Context.Character);
            }
            else if (creditObserved) reason = "refine_waiting_activity_progress";
        }

        private bool BeforeDebit(Frame f)
        {
            return sent.ExperienceRoute ? f.SameProtectedResources(sent) && f.Experience == sent.Experience &&
                f.XpExchanges == sent.XpExchanges && f.Practice == sent.Practice : f.SameResources(sent);
        }

        private bool ExpectedDebit(Frame f)
        // Any overlapping experience credit makes the debit ambiguous. Stop
        // instead of inferring a spend from a net balance and a callback alone.
        { return sent.ExperienceRoute ? f.Experience == sent.Experience - sent.ExperienceCost && f.XpExchanges == sent.XpExchanges + 1 :
            f.Money == sent.Money - sent.Cost && f.Coins == sent.Coins + 1; }

        private bool ProgressAdvanced(Frame f)
        {
            return f.Current == Math.Min(sent.Current + 1, sent.Required) && f.ListedCurrent == f.Current &&
                f.ListedRefresh == f.Refresh && f.Refresh == (activityDispatch == 0 ? sent.Refresh : activityRefresh);
        }

        private bool CleanupBoundary(Frame f)
        {
            return PendingBoundary(f) && f.Handler == null && !f.Sending && f.EffectCount == 0 && f.QueuedCoinReplies == 0 &&
                ExpectedDebit(f) && f.Practice == expectedPractice && ProgressAdvanced(f);
        }

        private string Gate(Frame f, string action)
        {
            if (f.Error != "") return f.Error;
            if (f.Overlay != "PASS") return f.Overlay;
            if (!f.Idle) return "refine_network_busy";
            if (action == "close")
                return status == "SETTLED" && CleanupBoundary(f) && f.Enabled && f.ActivityOriginal != null
                    ? "" : "refine_cleanup_unavailable";
            if (f.Current != f.ListedCurrent || f.Refresh != f.ListedRefresh) return "refine_activity_panel_stale";
            // checkUI("star_refine") controls the main-menu entry. The stock
            // experience button in an already owned panel uses its live
            // quota and quote; it does not require that entry to remain listed.
            if (action == "open" && !f.FeatureUnlocked) return "refine_feature_locked";
            if (!f.ExperienceRoute) return "refine_coin_route_not_authorized";
            if (f.ExperienceRoute)
            {
                if (f.XpExchanges + f.GoldExchanges >= 10) return "refine_experience_limit";
                if (f.QuoteGate != "") return f.QuoteGate;
                if (!f.HasExperienceQuote) return "refine_experience_quote_missing";
                if (f.Experience < f.ExperienceCost) return "refine_insufficient_experience";
            }
            else
            {
                if (f.Coins >= CoinLimit) return "refine_coin_limit";
                if (!f.Unlocked) return "refine_coin_feature_locked";
                if (f.Cost == 0 || f.Money < f.Cost) return "refine_insufficient_coins";
            }
            if (f.QuoteGate != "") return f.QuoteGate;
            if (f.ActivityOriginal == null) return "refine_activity_callback_unbound";
            if (action == "open")
                return f.Sub == null && !f.Visible && !f.Loading && !f.Sending && f.EffectCount == 0 && !f.HasClose
                    ? "" : "refine_clean_stock_entry_required";
            if (action == "experience")
                return status == "OPEN" && StableOpen(f, opened) && f.SameWindow(opened) &&
                    f.Visible && f.Enabled && !f.Loading && f.CoinTab ? "" : "refine_experience_tab_unavailable";
            bool initial = status == "OPEN" && StableOpen(f, opened) && f.SameWindow(opened);
            bool next = status == "SETTLED" && progressObserved && returned && CleanupBoundary(f) &&
                callback == null && (activity == null || queued != null && PristineActivityWrapper(f.ActivityHandler, queued));
            return (initial || next) && f.Visible && f.Enabled && !f.Loading && f.MatchingTab
                ? "" : "refine_owned_idle_panel_required";
        }

        private Frame Read(D context, bool insideCallback, Intent armed = null)
        {
            Frame f = new Frame { Context = context };
            object load = D.Static(Model.FirstType("BaseAsset"), "lds");
            if (context == null || context.Paused || !context.SameOwnership(context) ||
                context.Character.GetType() != Model.FirstType("Database.Character") ||
                load == null || !ReferenceEquals(D.Read(load, "currmg"), context.Root))
            { f.Error = "refine_role_or_root_unavailable"; return f; }
            if (!Monitor.TryEnter(context.Client, 0)) { f.Error = "refine_client_busy"; return f; }
            try
            {
                object role = context.Character;
                f.Role = Field<ulong>(role, "id");
                f.Transport = DailyTransport.Read(context.Client);
                if (f.Transport == null) { f.Error = "refine_transport_unavailable"; return f; }
                uint day;
                f.Error = refresh(role, out f.Now, out day, out f.Cycle);
                if (f.Error != "") return f;
                f.Next = Field<uint>(role, "next_resource_time");
                ReadTask(f);
                ReadResources(f, role);
                if (status == "PENDING" || status == "SETTLED") f.ExperienceRoute = sent.ExperienceRoute;
                if (!insideCallback && status != "PENDING") ReadQuote(f, role);
                IDictionary handlers = Handlers(context.Client);
                f.Handler = handlers[f.Opcode];
                f.ConflictingExchange = Conflicts(handlers, f.Opcode);
                IList messages = QueueItems(context.Client, "messages");
                foreach (object message in messages)
                {
                    ushort opcode = Field<ushort>(message, "op");
                    if (opcode != f.Opcode && (opcode == 860 || opcode == 861 || opcode == 991)) f.ConflictingExchange = true;
                    if (opcode == f.Opcode) f.QueuedCoinReplies++;
                }
                if (f.QueuedCoinReplies > 1) f.ConflictingExchange = true;
                if (!insideCallback)
                {
                    f.Idle = ReadIdle(context.Client, handlers, messages.Count) && !f.ConflictingExchange && f.Handler == null;
                    f.ActivityHandler = handlers[ActivityOpcode];
                    Delegate push = f.ActivityHandler == null ? null : D.Read(f.ActivityHandler, "handler") as Delegate;
                    if (armed == null)
                    {
                        if (DevelopmentCallbackCapture.IsDailyActivityPush(f.ActivityHandler, push)) f.ActivityOriginal = push;
                    }
                    else if (ReferenceEquals(f.ActivityHandler, armed.Baseline.ActivityHandler) &&
                        PristineActivityWrapper(f.ActivityHandler, armed))
                    {
                        // Only this operation's uncalled wrapper substitutes
                        // for the stock registration during final admission.
                        f.ActivityOriginal = armed.Baseline.ActivityOriginal;
                    }
                }
                ReadPanel(f, insideCallback);
                if (!f.Transport.Same(DailyTransport.Read(context.Client)) || !context.SameOwnership(context))
                    f.Error = "refine_boundary_changed_during_sample";
                return f;
            }
            finally { Monitor.Exit(context.Client); }
        }

        private static void ReadTask(Frame f)
        {
            object role = f.Context.Character;
            IDictionary rows = Map(D.Read(role, "acts"), 255), rewards = Map(D.Read(role, "act_rewards"), 255);
            f.Rows = rows; f.Rewards = rewards; f.Row = rows[(uint)TaskId]; f.Template = D.Template("activity", TaskId);
            if (f.Template == null || f.Template.GetType().FullName != "Database._activity" ||
                Field<byte>(f.Template, "id") != TaskId || !Field<bool>(f.Template, "active") || !TaskRow(f.Row, f.Template))
                throw new ProbeFailure("refine_activity_identity");
            f.Required = Field<int>(f.Template, "cond_param");
            f.Current = Field<byte>(f.Row, "condition_param"); f.Refresh = Field<int>(f.Row, "refresh_time");
            byte category = f.ActivityCategory = Field<byte>(f.Template, "idreward");
            f.Reward = rewards[(uint)category];
            if (f.Reward == null || f.Reward.GetType() != Model.FirstType("Database.Activity_Reward") ||
                Field<byte>(f.Reward, "idact_reward") != category || f.Required <= 0 || f.Required > 255 || f.Current > f.Required)
                throw new ProbeFailure("refine_activity_counter_schema");
            IDictionary listed = Map(D.Read(f.Reward, "acts"), 255);
            f.ListedRows = listed; f.Listed = listed[(uint)TaskId];
            if (!TaskRow(f.Listed, f.Template)) throw new ProbeFailure("refine_activity_listing");
            f.ListedCurrent = Field<byte>(f.Listed, "condition_param"); f.ListedRefresh = Field<int>(f.Listed, "refresh_time");
            if (f.ListedCurrent > f.Required) throw new ProbeFailure("refine_activity_listing");
        }

        private static bool TaskRow(object row, object template)
        {
            return row != null && row.GetType() == Model.FirstType("Database.Activity") &&
                Field<byte>(row, "idactivity") == TaskId && ReferenceEquals(D.Read(row, "temp"), template);
        }

        private static void ReadResources(Frame f, object role)
        {
            f.Money = D.Money(role);
            f.Gold = Field<int>(role, "gold"); f.Bound = Field<int>(role, "bind_gold");
            f.GoldConsumed = Field<uint>(role, "gold_consume"); f.Experience = Field<int>(role, "experience");
            f.Practice = Field<int>(role, "practice"); f.Level = Field<short>(role, "level");
            f.Coins = Field<int>(role, "practice_money_exchange_times");
            f.XpExchanges = Field<short>(role, "practice_exchange"); f.GoldExchanges = Field<short>(role, "practice_exchange_gold");
            if (f.Money < 0 || f.Bound < 0 || f.Experience < 0 || f.Practice < 0 || f.Level <= 0 ||
                f.Coins < 0 || f.Coins > CoinLimit || f.XpExchanges < 0 || f.GoldExchanges < 0 ||
                f.XpExchanges + f.GoldExchanges > 10 ||
                D.Bool(D.Static(Model.FirstType("BaseAsset"), "_PHONEUI")))
                throw new ProbeFailure("refine_resource_schema");
        }

        private void ReadQuote(Frame f, object role)
        {
            f.FeatureUnlocked = (bool)D.Method(role.GetType(), "checkUI", new[] { typeof(string) }, false, typeof(bool))
                .Invoke(role, new object[] { "star_refine" });
            f.Unlocked = (bool)D.Method(role.GetType(), "checkUI", new[] { typeof(uint) }, false, typeof(bool))
                .Invoke(role, new object[] { 91u });
            // The authorized checklist is the shared ten experience exchanges;
            // the separate twenty coin opportunities are not authorized.
            f.ExperienceRoute = true;
            if (f.ExperienceRoute) { ReadExperienceQuote(f); return; }
            if (!f.Unlocked) return;
            Type global = Model.FirstType("Global"), rowType = null;
            IDictionary table = Map(D.Method(global, "getTemplates", new[] { typeof(string) }, true, typeof(Hashtable))
                .Invoke(null, new object[] { "practice_money_exchange" }), 16384);
            f.Prices = table; f.PriceCount = table.Count;
            FieldInfo level = null, times = null;
            foreach (DictionaryEntry pair in table)
            {
                object row = pair.Value;
                if (rowType == null && row != null && row.GetType().FullName == "Database._practice_money_exchange")
                {
                    rowType = row.GetType();
                    level = rowType.GetField("level"); times = rowType.GetField("times");
                    if (level == null || times == null || level.FieldType != typeof(ushort) || times.FieldType != typeof(ushort))
                        throw new ProbeFailure("refine_quote_schema");
                }
                if (row == null || row.GetType() != rowType) throw new ProbeFailure("refine_quote_schema");
                if ((ushort)level.GetValue(row) != f.Level || (ushort)times.GetValue(row) != f.Coins + 1) continue;
                if (f.Quote != null) { f.QuoteGate = "refine_ambiguous_quote"; return; }
                f.Quote = row; f.QuoteKey = pair.Key;
            }
            if (f.Quote == null) { f.QuoteGate = "refine_quote_missing"; return; }
            f.Cost = Field<uint>(f.Quote, "money");
            if (f.Cost == 0 || f.Cost > Int32.MaxValue) { f.Cost = 0; f.QuoteGate = "refine_quote_cost"; }
        }

        private static void ReadExperienceQuote(Frame f)
        {
            if (f.XpExchanges + f.GoldExchanges >= 10) return;
            Type global = Model.FirstType("Global");
            f.MaxLevel = (int)D.Static(global, "PLAYER_LEVEL_MAX");
            if (f.MaxLevel < 60) throw new ProbeFailure("refine_experience_level_schema");
            f.ExchangeLevel = Math.Max(60, Math.Min(f.Level, f.MaxLevel));
            MethodInfo templates = D.Method(global, "getTemplates", new[] { typeof(string) }, true, typeof(Hashtable));
            IDictionary prices = Map(templates.Invoke(null, new object[] { "practice_exchange" }), 16384);
            IDictionary rates = Map(templates.Invoke(null, new object[] { "practice_exchange_rate" }), 32);
            f.Prices = prices; f.PriceCount = prices.Count; f.QuoteKey = (uint)f.ExchangeLevel;
            f.Quote = prices[f.QuoteKey]; f.Rates = rates; f.RateCount = rates.Count;
            int ordinal = f.XpExchanges + f.GoldExchanges + 1;
            f.RateQuote = rates[(uint)ordinal];
            if (f.Quote == null || f.RateQuote == null) { f.QuoteGate = "refine_experience_quote_missing"; return; }
            if (f.Quote.GetType().FullName != "Database._practice_exchange" ||
                f.RateQuote.GetType().FullName != "Database._practice_exchange_rate" ||
                Field<ushort>(f.Quote, "level") != f.ExchangeLevel || Field<sbyte>(f.RateQuote, "id") != ordinal)
                throw new ProbeFailure("refine_experience_quote_schema");
            uint experience = Field<uint>(f.Quote, "exp"), practice = Field<uint>(f.Quote, "practice");
            f.Rate = Field<int>(f.RateQuote, "rate");
            // Match the client's int32 arithmetic without admitting its overflow cases.
            long product = experience * (long)f.Rate;
            if (f.Rate <= 0 || experience > Int32.MaxValue || product < 0 || product > Int32.MaxValue ||
                practice == 0 || practice > Int32.MaxValue / 3)
            { f.QuoteGate = "refine_experience_quote_cost"; return; }
            f.ExperienceCost = (int)(product / 100); f.BasePractice = (int)practice;
        }

        private static void ReadPanel(Frame f, bool insideCallback)
        {
            D context = f.Context;
            Type type = Model.FirstType("ArmyStarRefineUI");
            // The legacy Unity Type overload references a different mscorlib.
            f.Panel = context.GlobalUi.gameObject.GetComponent("ArmyStarRefineUI");
            if (f.Panel == null || f.Panel.GetType() != type || !ReferenceEquals(D.Read(f.Panel, "rmg"), context.UiRoot) ||
                f.Panel.transform.root != context.UiRoot.transform) throw new ProbeFailure("refine_stock_panel_identity");
            if (!insideCallback)
            {
                int count = 0;
                foreach (UnityEngine.Object item in context.All)
                {
                    Component c = item as Component;
                    if (c != null && c.GetType() == type && c.gameObject == context.GlobalUi.gameObject) count++;
                }
                if (count != 1) throw new ProbeFailure("refine_stock_panel_unique");
            }
            f.Sub = context.SubUi; f.Selected = ReferenceEquals(f.Sub, f.Panel) && Feature() == "star_refine";
            f.Visible = D.Visible(f.Panel); f.Enabled = D.Enabled(f.Panel); f.Loading = D.Loading(f.Panel);
            f.VisibleAt = Field<DateTime>(f.Panel, "visible_time"); f.Sending = Field<bool>(f.Panel, "sended");
            f.HasClose = D.Read(f.Panel, "OnClose") != null;
            f.CoinTab = CoinTab(f.Panel);
            IDictionary effects = Map(D.Read(f.Panel, "fly_effects"), 16);
            f.Effects = effects; f.EffectCount = effects.Count;
            if (effects.Count == 1)
            {
                Type group = type.GetNestedType("FlyEffectGroup", BindingFlags.NonPublic);
                foreach (DictionaryEntry pair in effects)
                {
                    if (!(pair.Key is int) || (int)pair.Key <= 0 || pair.Value == null || pair.Value.GetType() != group)
                        throw new ProbeFailure("refine_stock_effect_schema");
                    f.EffectPractice = Field<int>(pair.Value, "practice");
                }
            }
            if (!insideCallback) f.Overlay = f.Selected ? context.Panels(f.Panel) : context.Panels();
        }

        private static bool CoinTab(Component panel)
        {
            object tab = D.Read(panel, "m_refinetype");
            if (tab.GetType() != panel.GetType().GetNestedType("RefineType")) throw new ProbeFailure("refine_stock_tab_schema");
            if (tab.ToString() != "qianbi" && tab.ToString() != "jingyan") throw new ProbeFailure("refine_stock_tab_schema");
            return tab.ToString() == "qianbi";
        }

        private static string Feature() { return (string)D.Static(Model.FirstType("__BaseUI"), "subui"); }
        private static IDictionary Handlers(object client) { return Map(D.Read(client, "m_handlers"), 512); }
        private static bool Conflicts(IDictionary handlers, ushort expected)
        { return handlers.Contains((ushort)861) || expected != 860 && handlers.Contains((ushort)860) ||
            expected != 991 && handlers.Contains((ushort)991); }

        private static IList QueueItems(object client, string name)
        {
            IList items = D.Read(client, name) as IList;
            if (items == null || items.Count > 64) throw new ProbeFailure("refine_network_bound");
            return items;
        }

        private static bool ReadIdle(object client, IDictionary handlers, int queuedMessages)
        {
            if (queuedMessages != 0 || QueueItems(client, "m_sendDatas").Count != 0 ||
                DevelopmentCallbackCapture.AnyCallbackExecuting) return false;
            Type handlerType = Model.FirstType("game.net.tcp_base+Handler");
            foreach (DictionaryEntry pair in handlers)
            {
                if (!(pair.Key is ushort) || pair.Value == null || pair.Value.GetType() != handlerType)
                    throw new ProbeFailure("refine_network_handler_schema");
                float life = Field<float>(pair.Value, "life");
                if (Single.IsNaN(life) || Single.IsInfinity(life)) throw new ProbeFailure("refine_network_handler_life");
                if (life > 0f) return false;
            }
            return true;
        }

        private static IDictionary Map(object value, int bound)
        {
            IDictionary map = value as IDictionary;
            if (map == null || map.Count > bound) throw new ProbeFailure("refine_cache_bound");
            return map;
        }

        private static T Field<T>(object target, string name)
        {
            object value = D.Read(target, name);
            if (!(value is T)) throw new ProbeFailure("refine_field:" + name);
            return (T)value;
        }

        private bool Expired() { return unchecked((uint)(Environment.TickCount - since)) >= WaitMs; }

        private bool Reject(string failure, out string why)
        {
            if (!stopped) Finish(progressObserved ? "COMPLETED" : attempted ? "UNKNOWN" : "REJECTED", failure);
            PublishRevision(frame);
            why = failure;
            return false;
        }

        private void Finish(string outcome, string detail)
        { status = outcome; reason = detail; queued = null; Detach(); }

        private void Detach() { Detach(ref callback); Detach(ref activity); }

        private static void Detach(ref DevelopmentCallbackCapture capture)
        {
            if (capture != null && !capture.DisarmAndRestore().EndsWith("_forward_only", StringComparison.Ordinal)) capture = null;
        }

        internal void NotInvoked(string detail)
        {
            if (queued == null) return;
            string ignored;
            Reject(detail, out ignored);
        }

        internal void Stop()
        {
            stopped = true;
            if (status != "COMPLETED" && status != "REJECTED" && !OutcomeUnknown)
                Finish(progressObserved ? "COMPLETED" : attempted ? "UNKNOWN" : "REJECTED", "refine_stopped");
            else Detach();
            PublishRevision(frame);
        }

        private string NextAction()
        {
            if (stopped || queued != null) return "";
            if (status == "OPEN") return frame.CoinTab ? Gate(frame, "experience") == "" ? "experience" : "" :
                Gate(frame, "refine") == "" ? "refine" : "";
            if (status == "SETTLED") return Gate(frame, "refine") == "" ? "refine" :
                Gate(frame, "close") == "" ? "close" : "";
            return !Busy && Gate(frame, "open") == "" ? "open" : "";
        }

        private void PublishRevision(Frame previous)
        {
            string next = ViewJson() + OperationJson();
            if (next == stamp && (ReferenceEquals(frame, previous) || frame.SameAdmission(previous) ||
                frame.Error != "" && frame.Error == previous.Error)) return;
            if (revision == 999999999) throw new ProbeFailure("refine_revision_exhausted");
            stamp = next; revision++;
        }

        internal string ViewJson()
        {
            string action = NextAction();
            string gate = frame.Error != "" ? frame.Error : action != "" ? "" : status == "OPEN" ? Gate(frame, "refine") :
                status == "SETTLED" ? Gate(frame, "close") : Busy ? reason : Gate(frame, "open");
            return "{\"task_id\":" + TaskId + ",\"role_id\":" + D.Q(frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"cycle\":" + frame.Cycle + ",\"current\":" + frame.Current + ",\"required\":" + frame.Required +
                ",\"money\":" + frame.Money + ",\"money_cost\":" + (frame.HasExperienceQuote ? "0" : frame.Cost == 0 ? "null" : frame.Cost.ToString(CultureInfo.InvariantCulture)) +
                ",\"mode\":" + D.Q(frame.ExperienceRoute ? "experience" : "coin") +
                ",\"experience\":" + frame.Experience + ",\"experience_exchanges\":" + frame.XpExchanges +
                ",\"practice\":" + frame.Practice + ",\"experience_limit\":10,\"remaining_exchanges\":" + (10 - frame.XpExchanges - frame.GoldExchanges) +
                ",\"gold_exchanges\":" + frame.GoldExchanges + ",\"experience_cost\":" +
                (frame.HasExperienceQuote ? frame.ExperienceCost.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"coin_exchanges\":" + frame.Coins + ",\"gold_cost\":" + (action == "refine" ? "0" : "null") +
                ",\"next_action\":" + D.Q(action) + ",\"reason\":" + D.Q(gate) +
                ",\"continuation_reason\":" + D.Q(status == "SETTLED" ? Gate(frame, "refine") : "") + "}";
        }

        internal string OperationJson()
        {
            return "{\"status\":" + D.Q(queued != null ? "QUEUED" : status) + ",\"reason\":" + D.Q(reason) +
                ",\"operation_id\":" + D.Q(operationId) + ",\"sequence\":" + lastSequence +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"stock_callback_returned\":" + D.B(returned) +
                ",\"account_progress_observed\":" + D.B(progressObserved) + ",\"panel_closed\":" + D.B(closed) +
                ",\"ui_ownership_held\":" + D.B(uiHeld) +
                ",\"mode\":" + D.Q((sent ?? origin ?? frame).ExperienceRoute ? "experience" : "coin") +
                ",\"practice_credit\":" + (progressObserved ? receipt.Practice : 0) + ",\"coins_spent\":" + (progressObserved ? sent.Cost : 0) +
                ",\"experience_spent\":" + (progressObserved && sent.ExperienceRoute ? sent.ExperienceCost : 0) + "}";
        }
    }
}
