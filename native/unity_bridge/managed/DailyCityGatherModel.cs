using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // Bounded collection only. Neither route can enter a battle, change a
    // production setting, accept overflow, or wait for a future target.
    internal sealed class DailyCityGatherModel
    {
        internal const int TreeTask = 306, MonsterTask = 307;
        private const int TargetLimit = 64, WaitMs = 20000;
        private sealed class Target
        {
            internal int Id, Level, Addition, MaximumCredit;
            internal object Value, Product, Data;
            internal Component Source;
            internal GameObject Mesh;
            internal uint Start, Duration, ManaOutput;
            internal ulong Killer;
            internal bool Lucky, Ready, Overflow;
            internal bool Same(Target old)
            {
                return old != null && Id == old.Id && ReferenceEquals(Value, old.Value) &&
                    ReferenceEquals(Product, old.Product) && ReferenceEquals(Data, old.Data) &&
                    ReferenceEquals(Source, old.Source) && ReferenceEquals(Mesh, old.Mesh) &&
                    Start == old.Start && Duration == old.Duration && ManaOutput == old.ManaOutput &&
                    Killer == old.Killer && Level == old.Level && Addition == old.Addition &&
                    Lucky == old.Lucky && Ready == old.Ready && Overflow == old.Overflow;
            }
        }
        private sealed class Frame
        {
            internal D Context;
            internal DailyTransport Transport;
            internal object Character, Client, Sub;
            internal Component Main, Reward, Demon, Loader;
            internal ulong Role;
            internal uint Now, Day, Cycle;
            internal int Mana, Capacity, Gold, Bound;
            internal uint GoldConsumed;
            internal string Scene = "", Error = "";
            internal bool Loading, NetworkBusy, Home;
            internal readonly List<Target> Targets = new List<Target>();
            internal bool SameConnection(Frame old)
            {
                return old != null && Role != 0 && Role == old.Role && Day == old.Day && Cycle == old.Cycle && Now >= old.Now &&
                    ReferenceEquals(Character, old.Character) && ReferenceEquals(Client, old.Client) &&
                    Transport != null && Transport.Same(old.Transport);
            }
            internal bool SameOwner(Frame old)
            { return SameConnection(old) && Context.SameOwnership(old.Context) && ReferenceEquals(Main, old.Main); }
            internal bool SameQuote(Frame old)
            {
                if (!SameOwner(old) || Mana != old.Mana || Capacity != old.Capacity || Gold != old.Gold ||
                    Bound != old.Bound || GoldConsumed != old.GoldConsumed || Scene != old.Scene || Home != old.Home ||
                    Loading != old.Loading || NetworkBusy != old.NetworkBusy || !ReferenceEquals(Sub, old.Sub) ||
                    !ReferenceEquals(Reward, old.Reward) || Targets.Count != old.Targets.Count) return false;
                for (int i = 0; i < Targets.Count; i++) if (!Targets[i].Same(old.Targets[i])) return false;
                return true;
            }
            internal Target Find(int id)
            { foreach (Target target in Targets) if (target.Id == id) return target; return null; }
        }
        private sealed class Intent
        {
            internal string Action, Id;
            internal Frame Quote;
            internal int Tick;
        }
        private sealed class Receipt
        {
            internal Target Target;
            internal Frame Sent;
            internal uint Result, ReplyId, Start;
            internal int Attempt, CommandSequence;
            internal long RequestObservation, CleanupObservation;
            internal bool HasResult, Matched, Returned, Confirmed, RewardOwned, RewardClosed, CloseInvoked, CloseReturned;
            internal bool CleanupConfirmed, ObjectRemoved, StillCollectible;
            internal bool ResponseReceived, AssumedSuccess, NullCleanupInvoked, NullCleanupReturned;
            internal bool Submitted, PageConfirmed, NullTimeout;
            internal Component Reward;
            internal Delegate Close, Stock, Wrapper;
            internal object Bonuses, Argument, Handler;
            internal DateTime Shown;
        }

        private Model model;
        private Frame frame, origin;
        private Intent queued;
        private DailyDivinityReplyTap tap;
        private Receipt pending;
        private readonly List<Target> initial = new List<Target>();
        private readonly List<Receipt> receipts = new List<Receipt>();
        private readonly List<int> issued = new List<int>();
        private int taskId, sequence, tick, index, initialObserved, skipped, overflowSkipped, unavailableSkipped;
        private long observation;
        private string operationId = "", lastAction = "", nextAction = "", status = "IDLE", reason = "not_observed", stamp;
        private bool blocked, stopped, holding, begun, attempted, closed, unsafeState;
        internal int Revision { get; private set; }
        internal bool Busy { get { return holding || queued != null || tap != null; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool GuiInvocationAttempted { get; private set; }

        internal void Capture(Model current, int task, bool admissionBlocked)
        {
            if (task != TreeTask && task != MonsterTask) throw new ArgumentOutOfRangeException("task");
            if (taskId != 0 && task != taskId && Busy) throw new ProbeFailure("city_gather_route_owned");
            taskId = task; model = current; blocked = admissionBlocked; nextAction = ""; observation++;
            try
            {
                frame = Read();
                if (frame.Error != "") { if (holding) Unknown(frame.Error); else Set("BLOCKED", frame.Error); return; }
                if (holding && !(begun ? frame.SameOwner(origin) : frame.SameConnection(origin)))
                { Unknown("city_gather_owner_or_game_day_changed"); return; }
                if (holding && taskId == TreeTask && (frame.Gold != origin.Gold || frame.Bound != origin.Bound || frame.GoldConsumed != origin.GoldConsumed))
                { Unknown("city_tree_premium_balance_changed"); return; }
                if (stopped) { Set("STOPPED", "user_stop"); return; }
                if (pending != null && pending.Submitted) ObservePage(pending);
                if (pending != null && pending.Submitted && !pending.ResponseReceived &&
                    (pending.NullTimeout || tap != null && tap.Error == "divinity_stock_callback_null"))
                {
                    // User policy: an absent response is processed as success,
                    // without claiming receipt or server confirmation.
                    pending.NullTimeout = true;
                    pending.AssumedSuccess = !pending.PageConfirmed;
                    if (!pending.NullCleanupReturned)
                    {
                        if (!stopped && !blocked && NoReplyCleanupOwned(frame, pending)) Offer("cleanup_no_reply");
                        else if (Elapsed() > WaitMs) Unknown("city_gather_no_reply_cleanup_unavailable");
                        else Set("PENDING", "city_gather_no_reply_cleanup_pending");
                        return;
                    }
                    if (Handler(frame.Client, taskId == TreeTask ? (ushort)1154 : (ushort)1166) != null || !frame.Home || frame.NetworkBusy)
                    {
                        // Workflow may move on under the user's rule; retained
                        // native ownership remains explicit for the controller.
                        if (Elapsed() > WaitMs) Unknown("city_gather_no_reply_release_unverified");
                        else Set("PENDING", "city_gather_no_reply_release_pending");
                        return;
                    }
                    if (!ReleaseTap(pending)) { Set("PENDING", "city_gather_callback_release_pending"); return; }
                    pending.CleanupConfirmed = true; pending.CleanupObservation = observation;
                    Target currentTarget = frame.Find(pending.Target.Id);
                    pending.StillCollectible = currentTarget != null && currentTarget.Ready;
                    pending = null;
                    index = taskId == MonsterTask && initial.Count > 0 ? (index + 1) % initial.Count : index + 1;
                }
                if (tap != null)
                {
                    if (tap.Error != null) { Unknown(tap.Error); return; }
                    if (!tap.Returned)
                    {
                        if (!tap.Attached || Elapsed() > WaitMs) Unknown("city_gather_sent_receipt_unverified_no_replay");
                        else Set(stopped ? "STOPPED" : "PENDING", stopped ? "user_stop" : "city_gather_reply_pending");
                        return;
                    }
                    if (!ReleaseTap(pending)) { Set("PENDING", "city_gather_callback_release_pending"); return; }
                }
                if (unsafeState) { Set("UNKNOWN", reason); return; }
                if (stopped) { Set("STOPPED", "user_stop"); return; }
                if (!holding)
                {
                    if (frame.Loading || frame.NetworkBusy) { Set("BLOCKED", "city_gather_loading_or_network_busy"); return; }
                    string gate = frame.Context.Panels(frame.Demon);
                    if (gate != "PASS") { Set("BLOCKED", gate); return; }
                    if (frame.Sub != null || !D.Enabled(frame.Main)) { Set("BLOCKED", "city_gather_home_required"); return; }
                    if (closed)
                    {
                        // Preserve this operation's terminal status while
                        // quoting a possible explicitly new run.
                        nextAction = frame.Scene == "Capital" ? "begin" : "open";
                        Set(status, reason); return;
                    }
                    Offer(frame.Scene == "Capital" ? "begin" : "open"); return;
                }
                if (!begun)
                {
                    if (frame.Home) Offer("begin");
                    else if (Elapsed() > WaitMs) Unknown("city_gather_capital_open_unverified");
                    else Set("PENDING", "city_gather_opening_capital");
                    return;
                }
                if (pending != null)
                {
                    if (!pending.Returned || !pending.Matched) { Unknown("city_gather_receipt_unverified_no_replay"); return; }
                    if (taskId == MonsterTask)
                    {
                        if (pending.CloseInvoked)
                        {
                            if (!pending.CloseReturned) { Unknown("city_monster_close_callback_return_unverified"); return; }
                            if (!ReferenceEquals(frame.Reward, pending.Reward) || RewardPending(frame.Reward) || !frame.Home || frame.NetworkBusy)
                            { if (Elapsed() > WaitMs) Unknown("city_monster_reward_cleanup_unverified"); else Set("PENDING", "city_monster_waiting_home"); return; }
                            object remaining = ((IDictionary)D.Read(frame.Character, "building_monsters"))[(byte)pending.Target.Id];
                            Target currentTarget = frame.Find(pending.Target.Id);
                            pending.ObjectRemoved = !ReferenceEquals(remaining, pending.Target.Value);
                            pending.StillCollectible = currentTarget != null && currentTarget.Ready;
                            pending.RewardClosed = pending.CleanupConfirmed = true;
                            // Removal is a separate local observation. A
                            // matching receipt and released stock UI permit a
                            // fresh attempt even when this id is still real
                            // and collectible; neither proves server success.
                            pending.Confirmed = pending.ObjectRemoved;
                        }
                        else
                        {
                            if (!OwnReward(frame, pending)) { Unknown("city_monster_reward_owner_changed"); return; }
                            if (!RewardReady(frame.Reward) || frame.Loading)
                            { if (Elapsed() > WaitMs) Unknown("city_monster_reward_not_ready"); else Set("PENDING", "city_monster_reward_loading"); return; }
                            string gate = frame.Context.RewardPanels(frame.Demon, frame.Reward);
                            if (gate != "PASS") { Unknown(gate); return; }
                            Offer("close_reward"); return;
                        }
                    }
                    else if (!pending.Confirmed && pending.Result == 0)
                    { Unknown("city_tree_stock_credit_unverified"); return; }
                    if (!frame.Home || frame.NetworkBusy)
                    { if (Elapsed() > WaitMs) Unknown("city_gather_home_cleanup_unverified"); else Set("PENDING", "city_gather_waiting_home"); return; }
                    pending.CleanupConfirmed = true;
                    pending.CleanupObservation = observation;
                    pending = null;
                    index = taskId == MonsterTask && initial.Count > 0 ? (index + 1) % initial.Count : index + 1;
                }
                if (!frame.Home || frame.NetworkBusy)
                { Unknown("city_gather_home_or_network_changed"); return; }
                if (taskId == MonsterTask)
                {
                    // Give the other initial targets a turn before returning
                    // to a still-collectible id. Never wait for a spawn.
                    if (receipts.Count < TargetLimit)
                        for (int scanned = 0; scanned < initial.Count; scanned++)
                        {
                            Target candidate = frame.Find(initial[index].Id);
                            if (candidate != null && candidate.Ready && !UnchangedAssumedTarget(candidate)) { Offer("collect"); return; }
                            index = (index + 1) % initial.Count;
                        }
                    skipped = unavailableSkipped = 0;
                    foreach (Target target in initial)
                    {
                        Target currentTarget = frame.Find(target.Id);
                        if (!issued.Contains(target.Id) && (currentTarget == null || !currentTarget.Ready))
                        { skipped++; unavailableSkipped++; }
                    }
                }
                else while (index < initial.Count)
                {
                    Target candidate = frame.Find(initial[index].Id);
                    // A replacement with the same numeric id is not a member of this run.
                    if (candidate == null || !ReferenceEquals(candidate.Value, initial[index].Value) || !candidate.Ready)
                    { skipped++; unavailableSkipped++; index++; continue; }
                    if (taskId == TreeTask && candidate.Overflow)
                    { skipped++; overflowSkipped++; index++; continue; }
                    Offer("collect"); return;
                }
                holding = false; closed = true;
                bool rejection = false, assumption = false;
                foreach (Receipt receipt in receipts)
                { if (taskId == TreeTask && receipt.HasResult && receipt.Result != 0) rejection = true; assumption = assumption || receipt.AssumedSuccess; }
                bool allConfirmed = receipts.Count > 0;
                foreach (Receipt item in receipts) allConfirmed = allConfirmed && item.Confirmed;
                Set(assumption ? "PROCESSED" : taskId == MonsterTask && receipts.Count > 0 && !allConfirmed ? "PARTIAL" : rejection ? "REJECTED" :
                    initialObserved == 0 ? "NO_CANDIDATES" : skipped > 0 || initialObserved > initial.Count ? "PARTIAL" : "COMPLETED",
                    assumption ? taskId == MonsterTask ? "city_monster_no_reply_assumed_success" : "city_tree_submitted_no_reply" :
                    taskId == MonsterTask && receipts.Count >= TargetLimit ? "city_monster_request_limit_result_unknown" :
                    taskId == MonsterTask && receipts.Count > 0 ? "city_monster_result_semantics_unverified" : rejection ? "city_tree_server_rejected" :
                    initialObserved == 0 ? "city_gather_no_current_targets" : overflowSkipped > 0 ? "city_tree_overflow_skipped" :
                    initialObserved > initial.Count ? "city_gather_initial_target_bound" : "city_gather_initial_targets_finished");
            }
            catch (Exception error) { if (holding || attempted) Unknown(error.Message); else Set("BLOCKED", error.Message); }
        }

        internal bool Queue(int task, ulong character, string id, int seq, int revision, string action, out string why)
        {
            why = "city_gather_command_unavailable";
            bool start = !holding && (action == "open" || action == "begin");
            if (task != taskId || stopped || blocked || unsafeState || frame == null || frame.Error != "" ||
                character == 0 || character != frame.Role || !Command.ValidNonce(id) || seq <= sequence || queued != null ||
                (start ? Busy || id == operationId : !holding || id != operationId)) return false;
            if (revision != Revision) { why = "city_gather_queue_quote_changed"; return false; }
            if (action == "" || action != nextAction) { why = "city_gather_queue_offer_changed"; return false; }
            queued = new Intent { Action = action, Id = id, Quote = frame, Tick = Environment.TickCount };
            sequence = seq; lastAction = action; GuiInvocationAttempted = false; Revision++; why = ""; return true;
        }

        internal bool ExecuteGui(DevelopmentPublication publish, DailyMonsterInvocationGate admission, out string why)
        {
            why = "city_gather_not_queued"; Intent intent = queued;
            if (intent == null) return false;
            object client = intent.Quote.Client;
            if (client == null || !Monitor.TryEnter(client, 0)) { NotInvoked("city_gather_client_busy"); why = "city_gather_client_busy"; return false; }
            try
            {
                publish(); Capture(model, taskId, blocked);
                if (stopped || blocked || !D.Gui || frame.Error != "" || !frame.SameQuote(intent.Quote) || intent.Action != nextAction ||
                    unchecked((uint)(Environment.TickCount - intent.Tick)) > 1500 || !admission())
                { why = "city_gather_gui_admission_changed"; NotInvoked(why); return false; }
                queued = null; tick = Environment.TickCount;
                if (intent.Action == "open" || intent.Action == "begin")
                {
                    if (!holding)
                    {
                        origin = frame; operationId = intent.Id; holding = true; closed = false; begun = attempted = false;
                        initial.Clear(); receipts.Clear(); issued.Clear(); pending = null;
                        index = initialObserved = skipped = overflowSkipped = unavailableSkipped = 0;
                    }
                    if (intent.Action == "open") { GuiInvocationAttempted = true; D.MainBranch("_capital"); }
                    else
                    {
                        if (begun || !frame.Home) throw new ProbeFailure("city_gather_begin_not_home");
                        origin = frame; begun = true; initialObserved = frame.Targets.Count;
                        for (int i = 0; i < Math.Min(TargetLimit, frame.Targets.Count); i++) initial.Add(frame.Targets[i]);
                    }
                }
                else if (intent.Action == "collect") Collect();
                else if (intent.Action == "close_reward")
                {
                    if (pending == null || !pending.Returned || !pending.Matched || pending.CloseInvoked ||
                        !OwnReward(frame, pending) || !RewardReady(frame.Reward)) throw new ProbeFailure("city_monster_reward_close_not_owned");
                    // This is the stock close button sequence, retaining the exact callback.
                    pending.CloseInvoked = true; GuiInvocationAttempted = true;
                    D.Call(pending.Reward, "Hide"); pending.Close.DynamicInvoke(null); pending.CloseReturned = true;
                }
                else if (intent.Action == "cleanup_no_reply")
                {
                    if (pending == null || !pending.Submitted || !pending.NullTimeout || pending.ResponseReceived ||
                        pending.NullCleanupInvoked || !NoReplyCleanupOwned(frame, pending)) throw new ProbeFailure("city_monster_no_reply_cleanup_not_owned");
                    pending.NullCleanupInvoked = true; GuiInvocationAttempted = true;
                    // Invoke only this request's captured stock null-stream
                    // branch. It hides the loader and enables input; it neither
                    // creates a reward nor removes a handler/target.
                    pending.Stock.DynamicInvoke(new object[] { null, pending.Argument });
                    pending.NullCleanupReturned = true;
                }
                else throw new ProbeFailure("city_gather_action_out_of_scope");
                nextAction = ""; Set("PENDING", "city_gather_stock_action_invoked"); why = ""; return true;
            }
            catch (Exception error) { queued = null; Unknown(error.Message); why = reason; return false; }
            finally { Monitor.Exit(client); }
        }

        private void Collect()
        {
            if (!begun || index >= initial.Count || pending != null || frame.NetworkBusy || !frame.Home)
                throw new ProbeFailure("city_gather_collect_not_admitted");
            Target target = frame.Find(initial[index].Id);
            if (target == null || !target.Ready || target.Overflow || receipts.Count >= TargetLimit ||
                taskId == TreeTask && (!ReferenceEquals(target.Value, initial[index].Value) || issued.Contains(target.Id)))
                throw new ProbeFailure("city_gather_initial_target_changed_no_replay");
            ushort opcode = taskId == TreeTask ? (ushort)1154 : (ushort)1166;
            if (Handler(frame.Client, opcode) != null) throw new ProbeFailure("city_gather_opcode_already_owned");
            pending = new Receipt { Target = target, Sent = frame, Attempt = receipts.Count + 1,
                CommandSequence = sequence, RequestObservation = observation };
            receipts.Add(pending); issued.Add(target.Id); attempted = true; GuiInvocationAttempted = true;
            if (taskId == TreeTask)
                D.Method(Model.GameType("BuildingHarvestUI"), "wantHarvest", new[] { target.Value.GetType(), Model.FirstType("__BaseUI+delegate_OnClose"), typeof(bool) }, true, typeof(void))
                    .Invoke(null, new object[] { target.Value, null, true });
            else D.Call(target.Source, "MonsterAction");
            pending.Submitted = true;
            object handler = Handler(frame.Client, opcode);
            Delegate stock = handler == null ? null : D.Read(handler, "handler") as Delegate;
            Type owner = taskId == TreeTask ? Model.GameType("BuildingHarvestUI") : target.Source.GetType();
            MethodInfo method = D.Method(owner, taskId == TreeTask ? "sc_building_harvest_0x482" : "sc_building_monster_clear_0x48E",
                new[] { typeof(MemoryStream), typeof(object) }, taskId == TreeTask, typeof(void));
            if (stock == null || stock.GetInvocationList().Length != 1 || stock.Method != method ||
                !ReferenceEquals(stock.Target, taskId == TreeTask ? null : target.Source))
                throw new ProbeFailure("city_gather_stock_registration_unverified_no_replay");
            Receipt request = pending;
            request.Stock = stock; request.Argument = D.Read(handler, "arg"); request.Handler = handler;
            tap = DailyDivinityReplyTap.Attach(frame.Client, false, handler, handler.GetType().GetField("handler", D.Instance), D.Read(handler, "arg"),
                delegate { return Handler(request.Sent.Client, opcode); }, delegate { return CallbackOwner(request); },
                delegate(byte[] body) { Returned(request); }, delegate(byte[] body) { Preflight(request, body); });
            if (tap == null || !tap.Attached) throw new ProbeFailure("city_gather_reply_guard_unavailable_no_replay");
            request.Wrapper = D.Read(handler, "handler") as Delegate;
        }

        private void ObservePage(Receipt request)
        {
            if (!frame.SameOwner(request.Sent)) return;
            IDictionary values = D.Read(frame.Character, taskId == TreeTask ? "buildings" : "building_monsters") as IDictionary;
            object current = values == null ? null : values[(byte)request.Target.Id];
            if (taskId == TreeTask)
                request.PageConfirmed = ReferenceEquals(current, request.Target.Value) &&
                    UInt(current, "start_time") > request.Target.Start && frame.Mana >= request.Sent.Mana;
            else
            {
                request.ObjectRemoved = !ReferenceEquals(current, request.Target.Value);
                request.PageConfirmed = request.ObjectRemoved;
            }
            if (request.PageConfirmed) { request.Confirmed = true; request.AssumedSuccess = false; }
        }

        private bool UnchangedAssumedTarget(Target target)
        {
            foreach (Receipt request in receipts)
                if (request.AssumedSuccess && target.Same(request.Target)) return true;
            return false;
        }

        private bool ReleaseTap(Receipt request)
        {
            if (tap == null) return true;
            if (request == null || request.Handler == null || request.Stock == null || request.Wrapper == null) return false;
            object client = request.Sent.Client;
            if (!Monitor.TryEnter(client, 0)) return false;
            try
            {
                tap.StopObserving(); if (!tap.TryRestore()) return false;
                ushort opcode = taskId == TreeTask ? (ushort)1154 : (ushort)1166;
                if (ReferenceEquals(Handler(client, opcode), request.Handler))
                {
                    object current = D.Read(request.Handler, "handler");
                    if (ReferenceEquals(current, request.Wrapper))
                    {
                        FieldInfo field = request.Handler.GetType().GetField("handler", D.Instance);
                        field.SetValue(request.Handler, request.Stock);
                        if (!ReferenceEquals(D.Read(request.Handler, "handler"), request.Stock)) return false;
                    }
                }
                tap = null; return true;
            }
            finally { Monitor.Exit(client); }
        }

        private bool CallbackOwner(Receipt request)
        {
            if (model == null || request == null || !ReferenceEquals(request, pending) || !request.Sent.Transport.StillCurrent() ||
                !ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), request.Sent.Character) ||
                !ReferenceEquals(model.CurrentRoot(), request.Sent.Context.Root) || request.Sent.Context.OwnershipGate() != "PASS") return false;
            uint now, day, cycle;
            if (DailyRefreshGate.ReadClock(request.Sent.Character, out now, out day, out cycle) != "" ||
                now < request.Sent.Now || day != request.Sent.Day || cycle != request.Sent.Cycle) return false;
            IDictionary dictionary = (IDictionary)D.Read(request.Sent.Character, taskId == TreeTask ? "buildings" : "building_monsters");
            return ReferenceEquals(dictionary[(byte)request.Target.Id], request.Target.Value) &&
                (taskId == TreeTask ? UInt(request.Target.Value, "start_time") == request.Target.Start :
                ULong(request.Target.Value, "idkiller") == request.Target.Killer && request.Target.Killer != 0 &&
                ReferenceEquals(D.Read(request.Target.Source, "Monster"), request.Target.Value) && !D.Bool(D.Read(request.Target.Source, "m_bFriend")));
        }

        private void Preflight(Receipt request, byte[] body)
        {
            request.ResponseReceived = true;
            object packet = BattleReplyPreflight.Deserialize(body, taskId == TreeTask ?
                "game.net.data.StoC.SCBuildingHarvest_0x482" : "game.net.data.StoC.SCBuildingMonsterClear_0x48E");
            request.Result = Convert.ToUInt32(Property(packet, "result"), CultureInfo.InvariantCulture); request.HasResult = true;
            request.ReplyId = Convert.ToUInt32(Property(packet, "id"), CultureInfo.InvariantCulture);
            // A tree rejection may omit its optional id. A success and every
            // monster reply must echo this exact target before stock runs.
            if ((taskId == MonsterTask || request.Result == 0 || request.ReplyId != 0) && request.ReplyId != request.Target.Id)
                throw new ProbeFailure("city_gather_reply_target_mismatch_no_replay");
            if (taskId == TreeTask)
            {
                request.Start = Convert.ToUInt32(Property(packet, "start_time"), CultureInfo.InvariantCulture);
                if (request.Result == 0 && request.Start <= request.Target.Start) throw new ProbeFailure("city_tree_reply_time_unverified");
            }
            else
            {
                // 1166 stock dereferences this optional field unconditionally.
                // Missing bonuses cannot safely be treated as success/empty.
                request.Bonuses = Property(packet, "bonuses");
                if (request.Bonuses == null) throw new ProbeFailure("city_monster_optional_bonus_absent_unknown");
            }
            request.Matched = true;
        }

        private void Returned(Receipt request)
        {
            request.Returned = true;
            Frame after = Read();
            if (after.Error != "" || !after.SameOwner(request.Sent)) throw new ProbeFailure("city_gather_callback_owner_changed");
            if (taskId == TreeTask)
            {
                if (request.Result != 0) return;
                Target target = request.Target;
                float fraction = request.Start < (long)target.Start + target.Duration ?
                    (float)(request.Start - target.Start) / target.Duration : 1f;
                fraction *= target.Lucky ? 1.5f : 1f;
                fraction *= (float)target.Addition / 100f + 1f;
                int credit = Mathf.RoundToInt((float)target.ManaOutput * fraction);
                if (credit <= 0 || credit > target.MaximumCredit || after.Mana != (long)request.Sent.Mana + credit ||
                    after.Mana > after.Capacity || after.Capacity != request.Sent.Capacity ||
                    UInt(target.Value, "start_time") != request.Start || !ReferenceEquals(D.Read(target.Value, "product"), target.Product))
                    throw new ProbeFailure("city_tree_stock_credit_mismatch");
                request.Confirmed = true;
            }
            else
            {
                request.Reward = after.Reward;
                request.Close = request.Reward == null ? null : D.Read(request.Reward, "OnClose") as Delegate;
                request.Bonuses = request.Reward == null ? null : D.Read(request.Reward, "m_vctBonuses");
                if (!OwnReward(after, request)) throw new ProbeFailure("city_monster_stock_reward_unowned");
                request.Shown = (DateTime)D.Read(request.Reward, "visible_time");
                request.RewardOwned = true;
            }
        }

        private bool OwnReward(Frame f, Receipt request)
        {
            Delegate close = f.Reward == null ? null : D.Read(f.Reward, "OnClose") as Delegate;
            return request != null && request.Matched && request.Returned && f.SameOwner(request.Sent) &&
                f.Reward != null && ReferenceEquals(f.Reward, request.Reward) &&
                ReferenceEquals(f.Reward, D.Static(Model.FirstType("__BaseUI"), "battle_result_ui")) &&
                close != null && close.GetInvocationList().Length == 1 && ReferenceEquals(close, request.Close) &&
                ReferenceEquals(close.Target, request.Target.Source) && close.Method == D.Method(request.Target.Source.GetType(), "BonusClose", Type.EmptyTypes, false, typeof(void)) &&
                ReferenceEquals(D.Read(f.Reward, "m_vctBonuses"), request.Bonuses) &&
                ReferenceEquals(D.Read(request.Target.Source, "Monster"), request.Target.Value) &&
                ReferenceEquals(((IDictionary)D.Read(f.Character, "building_monsters"))[(byte)request.Target.Id], request.Target.Value);
        }

        private bool NoReplyCleanupOwned(Frame f, Receipt request)
        {
            if (request == null || !request.Submitted || !request.NullTimeout || request.Stock == null || request.ResponseReceived || !f.SameOwner(request.Sent) ||
                f.Scene != "Capital" || !ReferenceEquals(f.Sub, request.Sent.Sub) || !ReferenceEquals(f.Loader, request.Sent.Loader) || RewardPending(f.Reward) ||
                !request.Sent.Transport.StillCurrent()) return false;
            object handler = Handler(f.Client, taskId == TreeTask ? (ushort)1154 : (ushort)1166);
            if (handler != null) return false;
            // Assess every visible panel without treating our own stock
            // loader/input-disable state as a foreign modal. Unknown panels
            // and another owner's reward remain untouched.
            foreach (UnityEngine.Object item in f.Context.All)
            {
                Behaviour ui = item as Behaviour;
                if (ui == null || !D.Visible(ui) || ui == f.Context.Root || ui == f.Context.UiRoot ||
                    ui == f.Context.GlobalUi || ui == f.Main || ui == f.Demon || ui == f.Loader) continue;
                if (f.Context.Overlay(ui) != null) return false;
            }
            return true;
        }

        private Frame Read()
        {
            Frame f = new Frame();
            try
            {
                f.Context = DevelopmentContextReader.DailyWood(model, false); f.Character = f.Context.Character;
                f.Client = DailyTransport.CurrentClient(f.Character); f.Transport = DailyTransport.Read(f.Client);
                if (f.Transport == null || f.Context.OwnershipGate() != "PASS") throw new ProbeFailure("city_gather_character_connection_unavailable");
                f.Role = ULong(f.Character, "id");
                string clock = DailyRefreshGate.ReadClock(f.Character, out f.Now, out f.Day, out f.Cycle);
                if (clock != "") throw new ProbeFailure(clock);
                f.Scene = (string)D.Read(f.Context.Root, "LevelID");
                f.Main = f.Context.Root.GetComponentInChildren(Model.GameType("MainUI"));
                if (f.Main == null || !ReferenceEquals(D.Read(f.Main, "rmg"), f.Context.Root)) throw new ProbeFailure("city_gather_main_owner_unavailable");
                f.Sub = f.Context.SubUi; f.Reward = f.Context.GrowthRewardDialog(); f.Demon = f.Context.FindCapitalDemon();
                object loader = D.Static(Model.FirstType("BaseAsset"), "lds");
                f.Loader = loader as Component;
                f.Loading = loader == null || ((Behaviour)loader).enabled || D.Bool(D.Read(loader, "isBattling")) ||
                    D.Bool(D.Read(f.Context.Root, "bLoading")) || D.Int(D.Read(f.Context.Root, "ExpectingCount")) != 0;
                f.Mana = D.Int(D.Read(f.Character, "mana")); f.Capacity = D.Int(D.Read(f.Character, "capacityMax"));
                f.Gold = D.Int(D.Read(f.Character, "gold")); f.Bound = D.Int(D.Read(f.Character, "bind_gold")); f.GoldConsumed = UInt(f.Character, "gold_consume");
                if (f.Mana < 0 || f.Capacity < 0) throw new ProbeFailure("city_tree_stock_capacity_schema");
                f.NetworkBusy = ((IList)D.Read(f.Client, "messages")).Count != 0 || ((IList)D.Read(f.Client, "m_sendDatas")).Count != 0;
                foreach (DictionaryEntry item in (IDictionary)D.Read(f.Client, "m_handlers"))
                    if (Convert.ToSingle(D.Read(item.Value, "life"), CultureInfo.InvariantCulture) > 0f) { f.NetworkBusy = true; break; }
                if (taskId == TreeTask && D.Bool(D.Static(Model.GameType("BuildingHarvestUI"), "_bHarvesting"))) f.NetworkBusy = true;
                f.Home = f.Scene == "Capital" && !f.Loading && f.Sub == null && D.Enabled(f.Main) &&
                    !RewardPending(f.Reward) && f.Context.Panels(f.Demon) == "PASS";
                if (f.Scene == "Capital") ReadTargets(f);
            }
            catch (Exception error) { f.Error = error.Message; }
            return f;
        }

        private void ReadTargets(Frame f)
        {
            IDictionary dictionary = (IDictionary)D.Read(f.Character, taskId == TreeTask ? "buildings" : "building_monsters");
            foreach (DictionaryEntry item in dictionary)
            {
                object value = item.Value; Target target = new Target { Value = value, Id = D.Int(D.Read(value, "id")) };
                if (target.Id < 0 || target.Id > 255 || D.Int(item.Key) != target.Id) throw new ProbeFailure("city_gather_target_key_schema");
                target.Mesh = D.Read(value, "mesh") as GameObject;
                if (taskId == TreeTask)
                {
                    target.Data = D.Read(value, "data"); target.Product = D.Read(value, "product");
                    if (D.Int(D.Read(target.Data, "type")) != 4 || D.Int(D.Read(target.Data, "automatic")) != 3 || target.Product == null ||
                        UInt(target.Product, "mana_output") == 0 || !(bool)D.Method(value.GetType(), "canHarvest", Type.EmptyTypes, false, typeof(bool)).Invoke(value, null)) continue;
                    target.Start = UInt(value, "start_time"); target.Duration = UInt(target.Product, "time");
                    target.ManaOutput = UInt(target.Product, "mana_output"); target.Level = D.Int(D.Read(value, "level")); target.Lucky = D.Bool(D.Read(value, "lucky"));
                    if (target.Duration == 0) throw new ProbeFailure("city_tree_product_time_schema");
                    uint templateId = UInt(value, "idbuilding");
                    IDictionary upgrades = (IDictionary)D.Method(Model.FirstType("Global"), "getTemplates", new[] { typeof(string) }, true, typeof(Hashtable)).Invoke(null, new object[] { "building_upgrade" });
                    foreach (DictionaryEntry upgrade in upgrades)
                        if (UInt(upgrade.Value, "idbuilding") == templateId && D.Int(D.Read(upgrade.Value, "level")) == target.Level)
                        { target.Addition = D.Int(D.Read(upgrade.Value, "product_addition")); break; }
                    if (target.Addition < 0) throw new ProbeFailure("city_tree_product_addition_schema");
                    // Bound a full lucky cycle, including the stock confirmation
                    // estimate. This deliberately skips some partial yields
                    // that might fit, avoiding clock/rounding overflow races.
                    float multiplier = (target.Lucky ? 1.5f : 1f) * ((float)target.Addition / 100f + 1f);
                    float confirmation = (float)Convert.ToUInt32(Property(target.Product, "Output"), CultureInfo.InvariantCulture);
                    double upper = Math.Ceiling(Math.Max((float)target.ManaOutput * multiplier,
                        confirmation * (float)target.Addition / 100f + confirmation));
                    if (upper < 1 || upper > Int32.MaxValue) throw new ProbeFailure("city_tree_output_bound_schema");
                    target.MaximumCredit = (int)upper; target.Overflow = (long)f.Mana + target.MaximumCredit > f.Capacity;
                    target.Ready = target.Mesh != null && target.Mesh.activeInHierarchy && target.Mesh.transform.root == f.Context.Root.transform;
                }
                else
                {
                    target.Killer = ULong(value, "idkiller"); if (target.Killer == 0) continue;
                    target.Source = target.Mesh == null ? null : target.Mesh.GetComponent("BuildingMonsterScript");
                    Component parent = target.Source == null ? null : D.Read(target.Source, "ParentUI") as Component;
                    target.Ready = target.Source != null && target.Mesh.activeInHierarchy && target.Mesh.transform.root == f.Context.Root.transform &&
                        ReferenceEquals(D.Read(target.Source, "rmg"), f.Context.Root) && ReferenceEquals(D.Read(target.Source, "Monster"), value) &&
                        !D.Bool(D.Read(target.Source, "m_bFriend")) && D.Enabled(target.Source) && !D.Loading(target.Source) &&
                        parent != null && parent.GetType() == Model.GameType("CapitalUI") && ReferenceEquals(D.Read(parent, "rmg"), f.Context.Root);
                }
                f.Targets.Add(target);
            }
            f.Targets.Sort(delegate(Target a, Target b) { return a.Id.CompareTo(b.Id); });
        }

        private static object Property(object value, string name)
        {
            PropertyInfo property = value == null ? null : value.GetType().GetProperty(name, D.Instance);
            if (property == null || property.GetIndexParameters().Length != 0) throw new ProbeFailure("city_gather_property_schema:" + name);
            return property.GetValue(value, null);
        }
        private static uint UInt(object value, string field) { return Convert.ToUInt32(D.Read(value, field), CultureInfo.InvariantCulture); }
        private static ulong ULong(object value, string field) { return Convert.ToUInt64(D.Read(value, field), CultureInfo.InvariantCulture); }
        private static object Handler(object client, ushort opcode) { return ((IDictionary)D.Read(client, "m_handlers"))[opcode]; }
        private static bool RewardPending(Component reward) { return reward != null && (D.Visible(reward) || D.Loading(reward)); }
        private static bool RewardReady(Component reward)
        {
            // This owned BattleResultUI inherits BaseAsset's loading contract.
            return reward != null && D.Visible(reward) && D.Enabled(reward) &&
                !D.Bool(D.Method(reward.GetType(), "IsLoading", Type.EmptyTypes, false, typeof(bool)).Invoke(reward, new object[0])) && !D.Loading(reward);
        }
        private int Elapsed() { return unchecked((int)((uint)(Environment.TickCount - tick))); }
        private void Offer(string action) { nextAction = action; Set("READY", ""); }
        private void Unknown(string why)
        {
            unsafeState = true; queued = null; nextAction = "";
            // Preserve the exact pending stock callback under its existing
            // owner/schema guard. Never install/restore another owner's slot.
            if (tap != null) { tap.StopObserving(true); if (tap.TryRestore()) tap = null; }
            Set("UNKNOWN", why);
        }
        internal void Stop(string why)
        {
            stopped = true; queued = null; nextAction = "";
            if (tap != null) { tap.StopObserving(true); if (tap.TryRestore()) tap = null; }
            Set("STOPPED", why);
        }
        internal void NotInvoked(string why)
        { queued = null; GuiInvocationAttempted = false; nextAction = ""; Set(status, why); }
        private void Set(string value, string why)
        { status = value; reason = why; string body = Body(); if (body != stamp) { stamp = body; Revision++; } }
        private string Body()
        {
            StringBuilder ids = new StringBuilder("["), rows = new StringBuilder("[");
            for (int i = 0; i < initial.Count; i++) { if (i != 0) ids.Append(','); ids.Append(initial[i].Id); } ids.Append(']');
            int confirmed = 0, returned = 0, cleaned = 0, collectible = 0;
            bool assumed = false;
            List<int> unique = new List<int>();
            foreach (Target target in initial)
            {
                Target currentTarget = frame == null ? null : frame.Find(target.Id);
                if (currentTarget != null && currentTarget.Ready) collectible++;
            }
            foreach (Receipt receipt in receipts)
            {
                if (rows.Length > 1) rows.Append(',');
                if (receipt.Confirmed) confirmed++; if (receipt.Returned) returned++;
                if (receipt.CleanupConfirmed) cleaned++; if (!unique.Contains(receipt.Target.Id)) unique.Add(receipt.Target.Id);
                assumed = assumed || receipt.AssumedSuccess;
                rows.Append("{\"attempt_id\":").Append(receipt.Attempt).Append(",\"command_sequence\":").Append(receipt.CommandSequence)
                    .Append(",\"request_observation_seq\":").Append(receipt.RequestObservation).Append(",\"cleanup_observation_seq\":").Append(receipt.CleanupObservation)
                    .Append(",\"status\":").Append(D.Q(receipt.AssumedSuccess ? "ASSUMED_SUCCESS" : taskId == MonsterTask ? "UNKNOWN" : receipt.Confirmed ? "COMPLETED" : receipt.Returned && receipt.Result != 0 ? "REJECTED" : "PENDING"))
                    .Append(",\"response_received\":").Append(D.B(receipt.ResponseReceived)).Append(",\"assumed_success\":").Append(D.B(receipt.AssumedSuccess))
                    .Append(",\"request_submitted\":").Append(D.B(receipt.Submitted)).Append(",\"page_confirmed\":").Append(D.B(receipt.PageConfirmed))
                    .Append(",\"no_reply_cleanup_returned\":").Append(D.B(receipt.NullCleanupReturned))
                    .Append(",\"target_id\":").Append(receipt.Target.Id).Append(",\"raw_result\":").Append(receipt.HasResult ? receipt.Result.ToString(CultureInfo.InvariantCulture) : "null")
                    .Append(",\"reply_target_id\":").Append(receipt.ReplyId).Append(",\"receipt_matched\":").Append(D.B(receipt.Matched))
                    .Append(",\"stock_callback_returned\":").Append(D.B(receipt.Returned)).Append(",\"reward_owned\":").Append(D.B(receipt.RewardOwned))
                    .Append(",\"reward_closed\":").Append(D.B(receipt.RewardClosed)).Append(",\"local_effect_confirmed\":").Append(D.B(receipt.Confirmed))
                    .Append(",\"close_callback_returned\":").Append(D.B(receipt.CloseReturned)).Append(",\"cleanup_confirmed\":").Append(D.B(receipt.CleanupConfirmed))
                    .Append(",\"object_removed\":").Append(D.B(receipt.ObjectRemoved)).Append(",\"target_still_collectible\":").Append(D.B(receipt.StillCollectible))
                    .Append(",\"server_success\":").Append(taskId == MonsterTask || !receipt.HasResult ? "null" : D.B(receipt.Result == 0 && receipt.Confirmed)).Append('}');
            }
            rows.Append(']');
            bool continuationSafe = closed && !Busy && frame != null && frame.Error == "" && frame.Home && !frame.NetworkBusy && !frame.Loading;
            return "\"task_id\":" + taskId + ",\"route\":" + D.Q(taskId == TreeTask ? "city_tree" : "city_monster_collect") +
                ",\"role_id\":" + D.Q(frame == null ? "0" : frame.Role.ToString(CultureInfo.InvariantCulture)) + ",\"refresh_cycle\":" + (frame == null ? 0 : frame.Cycle) +
                ",\"operation_id\":" + D.Q(operationId) + ",\"sequence\":" + sequence + ",\"last_action\":" + D.Q(lastAction) +
                ",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) + ",\"next_action\":" + D.Q(nextAction) +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"busy\":" + D.B(Busy) + ",\"closed\":" + D.B(closed) +
                ",\"cleanup_pending\":" + D.B(Busy) + ",\"home_ready\":" + D.B(frame != null && frame.Home) + ",\"initial_frozen\":" + D.B(begun) +
                ",\"continuation_safe\":" + D.B(continuationSafe) + ",\"semantic_unknown_clean\":" + D.B(taskId == MonsterTask && receipts.Count > 0 && continuationSafe) +
                ",\"assumed_success\":" + D.B(assumed) +
                ",\"processed_without_reply\":" + D.B(assumed) + ",\"page_confirmed\":" + D.B(receipts.Count > 0 && confirmed == receipts.Count) +
                ",\"mana_before\":" + (origin == null ? 0 : origin.Mana) + ",\"mana_after\":" + (frame == null ? 0 : frame.Mana) +
                ",\"in_flight\":" + D.B(queued != null || pending != null && !pending.Returned) +
                ",\"target_limit\":" + TargetLimit + ",\"initial_target_ids\":" + ids + ",\"targets_initial\":" + initialObserved +
                ",\"targets_attempted\":" + receipts.Count + ",\"targets_confirmed\":" + confirmed + ",\"callbacks_returned\":" + returned +
                ",\"request_limit\":" + TargetLimit + ",\"request_limit_reached\":" + D.B(receipts.Count >= TargetLimit) +
                ",\"unique_targets_attempted\":" + unique.Count + ",\"attempts_cleaned\":" + cleaned + ",\"targets_currently_collectible\":" + collectible +
                ",\"targets_skipped\":" + skipped + ",\"overflow_skipped\":" + overflowSkipped + ",\"unavailable_skipped\":" + unavailableSkipped +
                ",\"targets_unprocessed\":" + (taskId == MonsterTask ? Math.Max(0, initialObserved - initial.Count) + collectible : Math.Max(0, initialObserved - receipts.Count - skipped)) +
                ",\"attempt_id\":" + (pending != null ? pending.Attempt : receipts.Count + 1) +
                ",\"target_id\":" + (pending != null ? pending.Target.Id : begun && index < initial.Count ? initial[index].Id : -1) +
                ",\"mana\":" + (frame == null ? 0 : frame.Mana) + ",\"capacity\":" + (frame == null ? 0 : frame.Capacity) +
                ",\"result_semantics_verified\":" + D.B(taskId == TreeTask) + ",\"receipts\":" + rows;
        }
        internal string Json()
        { return "{\"schema\":1,\"revision\":" + Revision + ",\"observation_seq\":" + observation + "," + Body() + "}"; }
    }
}
