using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    // 312, 323 and 1829 are one-shot. 322 and 355 require bounded multi-dispatch observation.
    // Both persistent captures expose the dispatch ordinal before forwarding
    // the original unchanged. A later callback cannot qualify an earlier body.
    internal delegate DevelopmentCallbackCapture DailyTimedCallbackInstall(object client, object handler, int opcode,
        string operationId, int sequence, Delegate original, Action<byte[]> observe, out string why);
    internal delegate bool DailyTimedInvocationGate();

    internal sealed class DailyTimedModel
    {
        private const int Quiz = 12, Brawl = 13, QuizStart = 312, QuizQuick = 1829,
            BrawlJoin = 321, BrawlJoined = 322, BrawlRefresh = 323, Activity = 355;
        private readonly Model model;
        private readonly DailyTimedCallbackInstall install;
        private readonly List<string> used = new List<string>();
        private Frame frame = new Frame(), admitted, origin, before, quizClose;
        private int revision, sequence, task, tick, opcode, lastSequence, progressDispatch, joinDispatch;
        private string operation = "", status = "IDLE", reason = "", signature = "", evidenceError;
        private bool blocked, queued, open, close, invoked, progressReturned, joinReturned, brawlRefreshVerified;
        private DevelopmentCallbackCapture response, activity;
        private Component brawlRefreshUi;
        private DailyTimedWire.Join join;
        private DailyTimedWire.Refresh brawlRefresh;
        private DailyTimedWire.Question question;
        private DailyTimedWire.QuickQuiz quickQuiz;
        private object quickRuntime;
        private DailyTimedWire.Progress progress;
        private int? confirmed;

        internal DailyTimedModel(Model source, DailyTimedCallbackInstall callbackInstall)
        { model = source; install = callbackInstall; }

        internal bool Busy
        { get { return status == "QUEUED" || status == "OPENING" || status == "READY" || status == "PENDING" || status == "CLOSING" ||
                quizClose != null || Unresolved || response != null || activity != null; } }
        internal bool Unresolved { get { return status == "UNKNOWN"; } }
        internal int Revision { get { return revision; } }

        internal void Capture(bool otherOwner)
        {
            blocked = otherOwner;
            frame = Read();
            string refresh = RefreshConflict(frame, admitted);
            if (status == "QUEUED" && refresh != "") End("REJECTED", refresh);
            else if (status == "OPENING" || status == "READY" || status == "PENDING" || status == "CLOSING")
            {
                try { Settle(); }
                catch (Exception) { End("UNKNOWN", "daily_timed_readback_failed"); }
            }
            else if (status != "QUEUED")
            {
                Detach();
                if (quizClose != null && (!frame.SameQuizResult(quizClose) || Expired() ||
                    frame.SubUi != null && !object.ReferenceEquals(frame.SubUi, quizClose.QuizUi) ||
                    frame.Gate == "" && frame.Page == "MAIN" && !frame.QuizVisible)) quizClose = null;
            }
            RefreshRevision();
        }

        private void RefreshRevision()
        {
            string next = frame.Stamp() + ":" + View(Quiz) + ":" + View(Brawl) + ":" + OperationJson();
            if (next == signature) return;
            if (revision == 999999999) throw new ProbeFailure("daily_timed_revision_exhausted");
            signature = next;
            revision++;
        }

        internal bool Queue(int taskId, ulong characterId, string operationId, int commandSequence,
            int expectedRevision, string action, out string why)
        {
            why = "daily_timed_admission";
            if ((taskId != Quiz && taskId != Brawl) || characterId == 0 || characterId != frame.CharacterId ||
                commandSequence <= lastSequence || expectedRevision != revision || queued ||
                !new DevelopmentCommand { OperationId = operationId }.ValidId()) return false;
            bool openView = action == "open", closeView = action == "close";
            if (!openView && action != "participate" && !(closeView && taskId == Quiz)) return false;
            bool continuing = status == "READY" && action == "participate" && taskId == task && operationId == operation &&
                frame.SameOwner(origin) && frame.SameUi(before, task);
            bool ownClose = closeView && status == "SUCCEEDED" && taskId == task && operationId == operation && quizClose != null;
            if (!continuing && !ownClose && (Busy || used.Count >= 64 || used.Contains(operationId))) return false;
            why = closeView ? CloseGate(frame) : Gate(frame, taskId, openView, continuing);
            if (why != "") return false;
            if (!continuing && !ownClose)
            {
                used.Add(operationId); origin = frame;
                brawlRefresh = null; brawlRefreshUi = null; brawlRefreshVerified = false;
            }
            admitted = frame; task = taskId; operation = operationId; sequence = commandSequence;
            lastSequence = commandSequence; open = openView; close = closeView; queued = true; invoked = false;
            before = null; question = null; quickQuiz = null; quickRuntime = null; join = null; progress = null; confirmed = null; evidenceError = null;
            progressDispatch = 0; progressReturned = false; joinDispatch = 0; joinReturned = false;
            opcode = open || close ? 0 : task == Quiz ? frame.Level >= 60 ? QuizQuick : QuizStart : BrawlJoin;
            status = "QUEUED"; reason = "";
            RefreshRevision();
            return true;
        }

        internal bool ExecuteGui(DevelopmentPublication publish, DailyTimedInvocationGate begin, out string why)
        {
            why = "daily_timed_not_queued";
            if (!queued) return false;
            queued = false;
            Frame current = Read();
            string refresh = RefreshConflict(current, admitted);
            if (refresh != "") return Reject(refresh, out why);
            if (!D.Gui || !current.SameStep(admitted, task))
                return Reject("daily_timed_gui_or_baseline_changed", out why);
            if (!Monitor.TryEnter(current.Client, 0))
                return Reject("daily_timed_client_busy", out why);
            try
            {
                // Controller polls stop around publication. Nothing has been
                // invoked or installed if publication cancels this GUI turn.
                publish();
                if (status != "QUEUED") { why = reason; return false; }
                current = Read();
                refresh = RefreshConflict(current, admitted);
                if (refresh != "") return Reject(refresh, out why);
                string gate = close ? CloseGate(current) : Gate(current, task, open, !open);
                if (!D.Gui || !current.SameStep(admitted, task) || gate != "")
                    return Reject(gate == "" ? "daily_timed_baseline_changed_before_call" : gate, out why);
                try
                {
                    if (close)
                    {
                        // Stock MainUI.SetSubUI(null) hides the exact current
                        // panel and restores scene input. QuizUI inherits Hide
                        // without an override that changes the quiz record.
                        var hide = D.Method(Model.GameType("MainUI"), "SetSubUI",
                            new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void));
                        if (!begin()) return Reject("daily_timed_gui_final_admission_changed", out why);
                        before = current; tick = Environment.TickCount; invoked = true;
                        status = "CLOSING"; reason = "daily_timed_quiz_closing";
                        hide.Invoke(null, new object[] { null });
                        why = reason; return true;
                    }
                    if (!open)
                    {
                        activity = Bind(current, Activity, "handelActvity", null, ReadProgress);
                        if (activity == null) return Reject("daily_timed_activity_push_unbound", out why);
                        if (task == Brawl)
                        {
                            response = Bind(current, BrawlJoined, "handleBrawlJoinName", null, ReadJoin);
                            if (response == null) return Reject("daily_timed_join_push_unbound", out why);
                        }
                    }
                    uint now, day, cycle;
                    refresh = DailyRefreshGate.Read(current.Role, out now, out day, out cycle);
                    if (refresh != "") return Reject(refresh, out why);
                    if (cycle != current.Cycle) return Reject("daily_timed_refresh_cycle_changed", out why);
                    if (!open && !current.SamePremiumNow())
                        return Reject("daily_timed_premium_balance_changed_before_call", out why);
                    if (!open && !Counter.Same(current.CounterFor(task), ReadCounter(current.Role, task)))
                        return Reject("daily_timed_counter_changed_before_call", out why);
                    if (!open && task == Quiz && Field<short>(current.Role, "level") != current.Level)
                        return Reject("daily_timed_quiz_route_changed_before_call", out why);
                    if (!begin())
                        return Reject("daily_timed_gui_final_admission_changed", out why);
                    before = current;
                    tick = Environment.TickCount;
                    invoked = true;
                    status = open ? "OPENING" : "PENDING";
                    reason = "daily_timed_normal_call";
                    if (open)
                    {
                        if (task == Quiz) D.Call(current.QuizUi, "SubLoading");
                        else
                        {
                            // Native Show runs during loading and can finish
                            // synchronously or on a later frame. Its own error
                            // or phase-0 handling may close and send stock 324.
                            D.Call(current.GlobalUi, "brawl_enter");
                            BindBrawlRefresh(Read());
                        }
                    }
                    else if (task == Quiz)
                    {
                        if (opcode == QuizQuick)
                        {
                            // The normal level>=60 button disables the panel
                            // then sends 1829. Its stock callback enables it
                            // even on null; the paid 384 button is separate.
                            D.Call(current.QuizUi, "Disable");
                            D.Call(current.QuizUi, "cs_quizQuickAnwser");
                            response = Bind(current, QuizQuick, "sc_quizQuickAnwser", current.QuizUi, ReadQuickQuiz);
                        }
                        else
                        {
                            D.Call(current.QuizUi, "cs_startquiz");
                            response = Bind(current, QuizStart, "sc_startquiz", current.QuizUi, ReadQuestion);
                        }
                        if (response == null) End("UNKNOWN", "daily_timed_quiz_response_unbound");
                    }
                    else D.Call(current.BrawlUi, "cs_join");
                }
                catch (Exception)
                { End(invoked ? "UNKNOWN" : "REJECTED", invoked ? "daily_timed_normal_call_uncertain" : "daily_timed_callback_bind_failed"); }
                why = reason;
                return invoked;
            }
            finally { Monitor.Exit(admitted.Client); }
        }

        private bool Reject(string why, out string output)
        { output = why; End("REJECTED", why); return false; }

        internal void NotInvoked(string why)
        { if (status == "QUEUED") End("REJECTED", why); }

        internal void Stop(string why)
        {
            quizClose = null;
            if (status == "PENDING" || status == "OPENING" || status == "CLOSING") End("UNKNOWN", why);
            else if (status == "QUEUED" || status == "READY") End("REJECTED", why);
            else Detach();
        }

        private void End(string outcome, string why)
        {
            if (close || outcome != "SUCCEEDED") quizClose = null;
            queued = false; status = outcome; reason = why;
            Detach();
            RefreshRevision();
        }

        private bool Expired()
        { return unchecked((uint)(Environment.TickCount - tick)) >= 18000; }

        private string CloseGate(Frame current)
        {
            if (blocked || quizClose == null || Expired() || !current.SameQuizResult(quizClose) ||
                !object.ReferenceEquals(current.SubUi, quizClose.QuizUi))
                return "daily_timed_quiz_close_owner_or_result_changed";
            if (response != null || activity != null || !current.NetworkIdle)
                return "daily_timed_network_not_idle";
            return current.Gate == "" && current.QuizOwned && current.StockCloseLoad ?
                "" : "daily_timed_quiz_close_view_unavailable";
        }

        private void Detach()
        {
            if (response != null && !response.DisarmAndRestore().EndsWith("forward_only", StringComparison.Ordinal)) response = null;
            if (activity != null && !activity.DisarmAndRestore().EndsWith("forward_only", StringComparison.Ordinal)) activity = null;
        }

        private void Settle()
        {
            string refresh = RefreshConflict(frame, origin);
            if (refresh != "")
            { End(status == "READY" ? "REJECTED" : "UNKNOWN", refresh); return; }
            if (!frame.SameOwner(origin))
            { End("UNKNOWN", "daily_timed_character_or_client_changed"); return; }
            if (!frame.SamePremium(origin))
            { End("UNKNOWN", "daily_timed_premium_balance_changed"); return; }
            bool expired = Expired();
            if (status == "CLOSING")
            {
                if (!frame.SameQuizResult(before) || frame.SubUi != null && !object.ReferenceEquals(frame.SubUi, before.QuizUi))
                    End("UNKNOWN", "daily_timed_quiz_close_context_changed");
                else if (!frame.QuizVisible && frame.SubUi == null && frame.Page == "MAIN" && frame.Gate == "" && frame.NetworkIdle)
                    End("SUCCEEDED", "daily_timed_quiz_closed_main_restored");
                else if (expired) End("UNKNOWN", "daily_timed_quiz_close_unconfirmed");
                return;
            }
            if (status == "READY")
            {
                if (!frame.SameUi(before, task) || frame.Page != (task == Quiz ? "QUIZ" : "BRAWL") || expired)
                    End("REJECTED", "daily_timed_ready_view_lost_or_expired");
                return;
            }
            if (status == "OPENING")
            {
                if (task == Brawl && origin.BrawlInit && !brawlRefreshVerified)
                {
                    BindBrawlRefresh(frame);
                    if (status != "OPENING") return;
                    if (response != null && (evidenceError != null || CaptureFailed(response)))
                    { End("UNKNOWN", evidenceError ?? "daily_timed_brawl_refresh_unconfirmed"); return; }
                    if (response != null && DispatchReturned(response, 1))
                    {
                        if (brawlRefresh == null || !OwnBrawlOpening(frame) || !frame.BrawlInit ||
                            !object.ReferenceEquals(frame.BrawlUi, brawlRefreshUi) ||
                            frame.Joined != brawlRefresh.Joined || frame.Phase != brawlRefresh.Phase || frame.BrawlNextTime != brawlRefresh.NextTime)
                        { End("UNKNOWN", "daily_timed_brawl_refresh_projection_mismatch"); return; }
                        brawlRefreshVerified = true;
                    }
                }
                if (task == Brawl && brawlRefreshVerified && response != null) Detach();
                string gate = Gate(frame, task, false, true);
                if (gate == "brawl_closed" || gate == "brawl_already_joined" || gate == "brawl_progress_already_present" ||
                    gate == "daily_timed_already_complete")
                { End("REJECTED", gate); return; }
                if (gate == "" && response == null && (task == Quiz ?
                    object.ReferenceEquals(frame.Root, origin.Root) && object.ReferenceEquals(frame.QuizUi, origin.QuizUi) :
                    OwnBrawlOpening(frame)))
                { before = frame; tick = Environment.TickCount; status = "READY"; reason = "daily_timed_normal_view_ready"; return; }
                if (expired) End("UNKNOWN", task == Brawl && origin.BrawlInit && !brawlRefreshVerified ?
                    "daily_timed_brawl_refresh_unconfirmed" : "daily_timed_open_unconfirmed");
                return;
            }
            if (!frame.SameUi(before, task))
            { End("UNKNOWN", "daily_timed_participation_view_changed"); return; }
            if (evidenceError != null || CaptureFailed(response) || CaptureFailed(activity))
            { End("UNKNOWN", evidenceError ?? "daily_timed_callback_unconfirmed"); return; }
            if (!activity.StillInstalled() || (task == Brawl && !response.StillInstalled()))
            { End("UNKNOWN", "daily_timed_persistent_callback_changed"); return; }

            Counter now = frame.CounterFor(task), initial = before.CounterFor(task);
            if (progress != null && !progressReturned)
            {
                progressReturned = DispatchReturned(activity, progressDispatch);
            }
            bool counterChanged = progressReturned && now != null && initial != null &&
                object.ReferenceEquals(now.Template, initial.Template) && now.SharedRow &&
                !object.ReferenceEquals(now.Row, initial.Row) && now.Required == initial.Required &&
                progress.Count > initial.Count && now.Count == Math.Min(progress.Count, now.Required) &&
                now.Count > initial.Count && now.Refresh == progress.Refresh;
            if (join != null && !joinReturned) joinReturned = DispatchReturned(response, joinDispatch);
            bool participation = task == Quiz ? QuizParticipated() :
                joinReturned && frame.Joined && frame.BrawlerId == before.CharacterId && frame.BrawlerServer == before.Server &&
                    frame.BrawlPosition == join.Position && join.Round >= before.Round;
            if (counterChanged && participation && frame.PremiumQueueIdle)
            {
                confirmed = now.Count;
                if (task == Quiz)
                {
                    quizClose = frame;
                    tick = Environment.TickCount;
                }
                // 321 has no own callback or nonce. An earlier manual request
                // can produce both of these witnesses; they confirm account state.
                End("SUCCEEDED", task == Brawl ? "account_progress_observed_request_attribution_unknown" :
                    "daily_timed_server_progress_and_participation");
            }
            else if (expired) End("UNKNOWN", "daily_timed_participation_or_progress_unconfirmed");
        }

        private bool QuizParticipated()
        {
            if (!DispatchReturned(response, 1)) return false;
            if (opcode == QuizStart) return question != null && frame.MatchesQuestion(question);
            if (quickQuiz == null || quickQuiz.Result != 0 || !frame.QuizHas || frame.QuizRefresh != quickQuiz.Refresh ||
                frame.QuizBonus != quickQuiz.Bonus || frame.QuizRank != quickQuiz.Rank ||
                !frame.QuizOwned || Field<int>(frame.QuizUi, "end_phase") != 0) return false;
            if (quickQuiz.Question == null) return frame.QuizInfo == null && frame.QuizFinished;
            if (!frame.MatchesRuntime(quickQuiz.Question)) return false;
            if (quickRuntime == null) quickRuntime = frame.QuizInfo;
            return object.ReferenceEquals(quickRuntime, frame.QuizInfo);
        }

        private static string RefreshConflict(Frame current, Frame earlier)
        {
            if (current.RefreshGate != "") return current.RefreshGate;
            return earlier != null && earlier.Cycle != current.Cycle ? "daily_timed_refresh_cycle_changed" : "";
        }

        private static bool DispatchReturned(DevelopmentCallbackCapture capture, int ordinal)
        {
            return capture.ReturnedForDispatch(ordinal);
        }

        private static bool CaptureFailed(DevelopmentCallbackCapture capture)
        {
            if (capture == null) return true;
            DevelopmentCallbackWitness w = capture.Read();
            return w.Error != null || w.NullResponse || w.OriginalThrew || !w.Armed;
        }

        private DevelopmentCallbackCapture Bind(Frame f, int op, string name, Component receiver, Action<byte[]> observe)
        {
            if (install == null) return null;
            IDictionary handlers = D.Read(f.Client, "m_handlers") as IDictionary;
            object handler = handlers == null ? null : handlers[(ushort)op];
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") || D.Read(handler, "arg") != null)
                return null;
            Delegate original = D.Read(handler, "handler") as Delegate;
            Type declaring = receiver == null ? Model.FirstType("RootBase") : receiver.GetType();
            if (original == null || !object.ReferenceEquals(original.Target, receiver) ||
                original.Method.DeclaringType != declaring || original.Method.Name != name ||
                original.Method.IsStatic != (receiver == null)) return null;
            float life = Field<float>(handler, "life");
            if (receiver == null ? life != -1f : life <= 0f) return null;
            string why;
            DevelopmentCallbackCapture capture = install(f.Client, handler, op, operation, sequence, original, observe, out why);
            if (capture == null) return null;
            if (capture.OperationId != operation || capture.Sequence != sequence || capture.Opcode != op)
            {
                capture.DisarmAndRestore();
                return null;
            }
            return capture;
        }

        private bool OwnBrawlOpening(Frame current)
        {
            return current.SameOwner(origin) && object.ReferenceEquals(current.OldRoot, origin.Root) &&
                object.ReferenceEquals(current.Root, origin.BrawlRoot) && current.BrawlUi != null &&
                object.ReferenceEquals(current.BrawlState, origin.BrawlState) &&
                object.ReferenceEquals(D.Read(current.BrawlUi, "mybrawlinfo"), current.BrawlState);
        }

        private void BindBrawlRefresh(Frame current)
        {
            if (status != "OPENING" || task != Brawl || !origin.BrawlInit || brawlRefreshVerified || response != null ||
                !OwnBrawlOpening(current) || !D.Visible(current.BrawlUi) || !Monitor.TryEnter(current.Client, 0)) return;
            try
            {
                // Opening admission excluded an existing 323 handler or
                // queued reply. Bind only the exact callback of the newly
                // owned page. A response dispatched before binding is unknown.
                IDictionary handlers = D.Read(current.Client, "m_handlers") as IDictionary;
                if (handlers == null) { End("UNKNOWN", "daily_timed_brawl_refresh_unbound"); return; }
                if (!handlers.Contains((ushort)BrawlRefresh)) return;
                response = Bind(current, BrawlRefresh, "sc_refresh", current.BrawlUi, ReadBrawlRefresh);
                if (response == null) End("UNKNOWN", "daily_timed_brawl_refresh_unbound");
                else brawlRefreshUi = current.BrawlUi;
            }
            finally { Monitor.Exit(current.Client); }
        }

        private void ReadBrawlRefresh(byte[] body)
        {
            if (!DailyTimedWire.ReadRefresh(body, out brawlRefresh)) evidenceError = "daily_timed_brawl_refresh_schema";
        }

        private void ReadProgress(byte[] body)
        {
            DailyTimedWire.Progress value;
            if (!DailyTimedWire.ReadProgress(body, task, out value)) evidenceError = "daily_timed_activity_push_schema";
            else if (value != null)
            {
                progress = value;
                progressDispatch = activity.Read().Calls;
                progressReturned = false;
            }
        }

        private void ReadJoin(byte[] body)
        {
            DailyTimedWire.Join value;
            if (!DailyTimedWire.ReadJoin(body, out value)) evidenceError = "daily_timed_join_push_schema";
            else if (value.Id == before.CharacterId && value.Server == before.Server)
            {
                if (join != null || (value.Phase != 2 && value.Phase != 3))
                    evidenceError = "daily_timed_self_join_mismatch";
                else
                {
                    join = value;
                    joinDispatch = response.Read().Calls;
                }
            }
        }

        private void ReadQuestion(byte[] body)
        {
            if (!DailyTimedWire.ReadQuestion(body, out question)) evidenceError = "daily_timed_quiz_response_schema";
        }

        private void ReadQuickQuiz(byte[] body)
        {
            if (!DailyTimedWire.ReadQuickQuiz(body, out quickQuiz)) evidenceError = "daily_timed_quiz_quick_response_schema";
            else if (quickQuiz.Result != 0) evidenceError = "daily_timed_quiz_quick_result_unconfirmed";
        }

        private static readonly ushort[] TimedRequests = { 311, 312, 313, 314, 315, 316, 317, 319, 321, 323, 324, 326, 329, 330, 332, 339, 341, 357, 358, 384, 1829 };
        private static bool NetworkIdle(object client, out bool premiumIdle)
        {
            premiumIdle = false;
            if (!Monitor.TryEnter(client, 0)) return false;
            try
            {
                IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
                IList messages = D.Read(client, "messages") as IList;
                if (handlers == null || messages == null || messages.Count > 64) return false;
                bool idle = true;
                premiumIdle = true;
                foreach (ushort op in TimedRequests) if (handlers.Contains(op)) idle = false;
                foreach (object msg in messages)
                {
                    ushort op = Field<ushort>(msg, "op");
                    if (op == 28 || op == 856 || op == 867) premiumIdle = false;
                    if (op == Activity || op == BrawlJoined || op == 325 ||
                        Array.IndexOf(TimedRequests, op) >= 0) idle = false;
                }
                return idle && premiumIdle;
            }
            finally { Monitor.Exit(client); }
        }

        private string Gate(Frame f, int id, bool opening, bool ownedView)
        {
            if (blocked || f.Client == null || f.CharacterId == 0)
                return "daily_timed_owner_or_clock_unavailable";
            if (f.RefreshGate != "") return f.RefreshGate;
            Counter counter = f.CounterFor(id);
            if (counter == null) return "daily_timed_counter_unavailable";
            if (counter.Count >= counter.Required) return "daily_timed_already_complete";
            if (id == Quiz)
            {
                string quizGate = QuizGate(f.QuizState != null, f.QuizInfo != null, f.QuizFinished, f.Level, f.QuizWindow);
                if (quizGate != "") return quizGate;
            }
            else
            {
                if (!f.BrawlObserved) return "brawl_phase_unknown";
                if (f.Phase != 2 && f.Phase != 3) return "brawl_closed";
                if (f.Joined) return "brawl_already_joined";
                if (counter.Count != 0) return "brawl_progress_already_present";
            }
            if (!f.NetworkIdle) return "daily_timed_network_not_idle";
            if (f.Gate != "") return f.Gate;
            if (opening) return f.Page == "MAIN" && (id == Quiz ? f.QuizUi != null : f.BrawlRoot != null) ? "" : "daily_timed_main_required";
            if (id == Quiz) return ownedView && f.Page == "QUIZ" ? "" : "daily_timed_quiz_view_required";
            if (!ownedView || f.Page != "BRAWL") return "daily_timed_owned_brawl_view_required";
            // A new record is applied by sc_init. The existing-record route
            // requires the captured sc_refresh return. nexttime may be zero.
            return f.BrawlInit && (!origin.BrawlInit ||
                (brawlRefreshVerified && object.ReferenceEquals(f.BrawlUi, brawlRefreshUi))) ? "" : "daily_timed_brawl_initialization_unconfirmed";
        }

        internal static string QuizGate(bool hasQuiz, bool inProgress, bool finished, int level, string window)
        {
            if (!hasQuiz) return "quiz_state_unknown";
            if (finished) return "quiz_finished";
            if (inProgress) return "quiz_already_in_progress";
            if (window != "") return window;
            if (level < 1) return "quiz_level_unknown";
            return "";
        }

        internal static string QuizWindow(uint now, uint day, int start, int end)
        {
            if (day == 0 || now < day || now >= day + 86400L || start < 0 || start >= end || end > 24)
                return "quiz_window_unknown";
            return now > day + start * 3600L && now < day + end * 3600L ? "" : "quiz_closed";
        }

        private Frame Read()
        {
            Frame f = new Frame();
            try { ReadInto(f); }
            catch (Exception error) { f.Gate = error is ProbeFailure ? error.Message : "daily_timed_capture_failed"; }
            return f;
        }

        private void ReadInto(Frame f)
        {
            Type global = Model.FirstType("Global"), rootUiType = Model.GameType("RootUI");
            f.Role = D.Static(global, "character_");
            if (f.Role == null || f.Role.GetType() != Model.FirstType("Database.Character") || Field<bool>(f.Role, "gm")) return;
            f.CharacterId = Field<ulong>(f.Role, "id");
            f.Server = Field<uint>(f.Role, "SID");
            f.Client = D.Static(rootUiType, "m_client");
            if (f.CharacterId == 0 || f.Client == null || !object.ReferenceEquals(f.Client, D.Static(global, "m_client")) ||
                D.Bool(D.Static(global, "relogin")) || Field<bool>(f.Client, "m_bConnectLost") ||
                D.Static(Model.FirstType("game.net.tcp_base"), "connection_state").ToString() != "OK")
            { f.Client = null; return; }
            Type clientType = f.Client.GetType();
            if (clientType == Model.FirstType("game.net.tcp_client"))
            {
                f.Session = D.Read(f.Client, "m_client");
                f.SessionStream = D.Read(f.Client, "m_stream");
                if (f.SessionStream == null) return;
            }
            else if (clientType == Model.FirstType("game.net.tcp_socket")) f.Session = D.Read(f.Client, "m_socket");
            if (f.Session == null) return;

            uint now, day;
            f.RefreshGate = DailyRefreshGate.Read(f.Role, out now, out day, out f.Cycle);
            if (f.RefreshGate != "") return;
            f.Gold = Field<int>(f.Role, "gold");
            f.BoundGold = Field<int>(f.Role, "bind_gold");
            f.GoldConsumed = Field<uint>(f.Role, "gold_consume");
            if (f.Gold < 0 || f.BoundGold < 0) return;
            f.QuizCounter = ReadCounter(f.Role, Quiz);
            f.BrawlCounter = ReadCounter(f.Role, Brawl);
            f.Level = Field<short>(f.Role, "level");
            f.QuizState = D.Read(f.Role, "quiz");
            if (f.QuizState != null && f.QuizState.GetType() != Model.FirstType("Database.Quiz")) throw new ProbeFailure("quiz_state_schema");
            if (f.QuizState != null)
            {
                f.QuizBonus = Field<bool>(f.QuizState, "bonus");
                f.QuizRefresh = Field<uint>(f.QuizState, "quiz_refresh");
                f.QuizRank = Field<uint>(f.QuizState, "rank");
                f.QuizHas = Field<bool>(f.QuizState, "hasquiz");
                f.QuizFinished = f.QuizBonus || f.QuizRefresh > now;
                f.QuizInfo = D.Read(f.QuizState, "info");
                if (f.QuizInfo != null)
                {
                    if (f.QuizInfo.GetType() != Model.FirstType("Database.QuizRunTime")) throw new ProbeFailure("quiz_info_schema");
                    IList questions = D.Read(f.QuizInfo, "questions") as IList;
                    f.QuestionIndex = Field<uint>(f.QuizInfo, "cur_question");
                    f.QuizRightCount = Field<uint>(f.QuizInfo, "right_count");
                    f.QuizWrongCount = Field<uint>(f.QuizInfo, "wrong_count");
                    f.QuizFilter = Field<int>(f.QuizInfo, "cur_filter");
                    f.QuizWaitState = Field<bool>(f.QuizInfo, "waitstate");
                    f.QuizEnded = Field<bool>(f.QuizInfo, "end");
                    f.QuizTimer = D.Read(f.QuizInfo, "checktime");
                    if (questions != null && questions.Count <= 64 && f.QuestionIndex < questions.Count && questions[(int)f.QuestionIndex] is uint)
                    { f.QuestionCount = questions.Count; f.QuestionId = (uint)questions[(int)f.QuestionIndex]; }
                }
            }
            object start = D.Template("event_timer", 1002u), end = D.Template("event_timer", 1003u);
            if (start != null && end != null && Field<short>(start, "id") == 1002 && Field<short>(end, "id") == 1003)
                f.QuizWindow = QuizWindow(now, day, Field<byte>(start, "hour"), Field<byte>(end, "hour"));
            object brawl = D.Read(f.Role, "brawlinfo");
            if (brawl != null && brawl.GetType() == Model.FirstType("Database.BrawlInfo"))
            {
                f.BrawlState = brawl;
                f.Phase = Field<uint>(brawl, "phase");
                f.Round = Field<uint>(brawl, "round");
                f.Joined = Field<bool>(brawl, "joined");
                f.BrawlPosition = Field<uint>(brawl, "mypos");
                object self = D.Read(brawl, "brawler_self");
                if (self != null && self.GetType() == Model.FirstType("Database.BrawlInfo+Brawler"))
                {
                    f.BrawlerId = Field<ulong>(self, "id");
                    f.BrawlerServer = Field<uint>(self, "sid");
                }
                object record = D.Read(f.Role, "brawl");
                f.BrawlInit = record != null && record.GetType() == Model.FirstType("Database.Brawl") && Field<bool>(record, "hasBrawl");
                f.BrawlNextTime = (uint)D.Static(brawl.GetType(), "nexttime");
            }
            ReadUi(f);
        }

        private void ReadUi(Frame f)
        {
            f.Root = model.CurrentRoot();
            if (f.Root == null) return;
            GameObject host = model.GlobalUiObject();
            GameObject brawlHost = GameObject.Find("/Brawl");
            if (host == null) return;
            Component load = D.Static(Model.FirstType("BaseAsset"), "lds") as Component;
            if (load == null || load.GetType() != Model.FirstType("LoadScreen") || !load.gameObject.activeInHierarchy) return;
            GameObject stockLoad = GameObject.Find("/LoadUI");
            f.StockCloseLoad = stockLoad != null && object.ReferenceEquals(stockLoad.GetComponent("LoadScreen"), load);
            f.OldRoot = D.Read(load, "oldrmg") as Component;
            UObject[] all = model.AllUi();
            f.GlobalUi = UniqueUi(all, host, Model.GameType("RootUI"));
            if (f.GlobalUi == null) return;
            object cached = D.Static(Model.GameType("BaseUI"), "rootUI");
            if (cached != null && !object.ReferenceEquals(cached, f.GlobalUi)) return;
            Component uiRoot = D.Read(f.GlobalUi, "rmg") as Component;
            if (uiRoot == null || uiRoot.GetType() != model.RootMessengerType ||
                f.GlobalUi.transform.root != uiRoot.transform || !uiRoot.gameObject.activeInHierarchy) return;
            f.QuizUi = UniqueUi(all, host, Model.FirstType("QuizUI"));
            if (f.QuizUi != null && !object.ReferenceEquals(D.Read(f.QuizUi, "rmg"), uiRoot)) f.QuizUi = null;
            f.QuizVisible = D.Visible(f.QuizUi);
            f.SubUi = D.Static(Model.GameType("MainUI"), "m_subui");
            if (brawlHost != null)
            {
                Component candidate = brawlHost.GetComponent("RootMessenger") as Component;
                if (candidate != null && candidate.GetType() == model.RootMessengerType &&
                    candidate.transform.root == candidate.transform && Field<string>(candidate, "LevelID") == "brawl")
                {
                    f.BrawlRoot = candidate;
                    f.BrawlUi = UniqueUi(all, GameObject.Find("/Brawl/BrawlUI"), Model.GameType("BrawlUI"));
                    if (f.BrawlUi != null && (f.BrawlUi.transform.root != candidate.transform ||
                        !object.ReferenceEquals(D.Read(f.BrawlUi, "rmg"), candidate))) f.BrawlUi = null;
                }
            }
            f.NetworkIdle = NetworkIdle(f.Client, out f.PremiumQueueIdle);
            if (!f.Root.gameObject.activeInHierarchy || ((Behaviour)load).enabled || Field<bool>(load, "isBattling") ||
                Field<bool>(f.Root, "bLoading") || Field<int>(f.Root, "ExpectingCount") != 0 ||
                !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")))
            { f.Gate = "daily_timed_loading_or_input_disabled"; return; }
            if (D.Bool(D.Static(Model.GameType("BuildingBuildUI"), "m_bBuildVisible")) ||
                D.Bool(D.Static(Model.GameType("BuildingBehaviourScript"), "m_bEditVisible")) ||
                D.Bool(D.Static(Model.FirstType("PopupUI"), "popup")))
            { f.Gate = "daily_timed_modal_active"; return; }

            Component allowed = null;
            object sub = f.SubUi;
            string level = Field<string>(f.Root, "LevelID");
            if (level == "Campaign" || level == "Capital")
            {
                Snapshot main = model.Capture();
                if (!object.ReferenceEquals(main.Root, f.Root) || !object.ReferenceEquals(main.Character, f.Role)) return;
                if (main.Gate != "PASS" && main.Gate != "subui_present" && main.Gate != "visible_ui_count" &&
                    main.Gate != "visible_unknown_ui" && main.Gate != "main_interaction_disabled") return;
                if (sub == null && main.InteractionEnabled) f.Page = "MAIN";
                else if (f.QuizUi != null && object.ReferenceEquals(sub, f.QuizUi) && Usable(f.QuizUi) &&
                    object.ReferenceEquals(D.Read(f.QuizUi, "myquiz"), f.QuizState))
                {
                    // DrawInQuiz calls prep_question at phase 0, then the
                    // normal GUI advances to phases 1/2 before 355 can arrive.
                    // Those phases still belong to this panel, but cannot send.
                    f.QuizOwned = true;
                    f.Page = Field<int>(f.QuizUi, "tip_phase") == 0 && Field<int>(f.QuizUi, "inquiz_phase") == 0 ? "QUIZ" : "QUIZ_ACTIVE";
                    allowed = f.QuizUi;
                }
            }
            else if (f.Root == f.BrawlRoot && sub == null && Usable(f.BrawlUi) &&
                object.ReferenceEquals(D.Read(f.BrawlUi, "mybrawlinfo"), f.BrawlState) &&
                Field<int>(f.BrawlUi, "battle_state") == 0 && Field<string>(f.BrawlUi, "subwindow") == "" &&
                !D.Bool(D.Static(Model.GameType("BrawlUI"), "toClose")))
            { f.Page = "BRAWL"; allowed = f.BrawlUi; }
            if (f.Page == "") return;
            foreach (UObject item in all)
            {
                Behaviour ui = item as Behaviour;
                if (ui == null || ui == f.GlobalUi || ui == allowed || !D.Visible(ui)) continue;
                Type type = ui.GetType();
                if ((type == Model.GameType("MainUI") || type == Model.GameType("CampaignUI")) &&
                    ui.transform.root == f.Root.transform && object.ReferenceEquals(D.Read(ui, "rmg"), f.Root)) continue;
                string overlay = model.OverlayGate(ui);
                if (overlay != null) { f.Gate = "daily_timed_overlay:" + overlay; return; }
            }
            f.Gate = "";
        }

        private static bool Usable(Component ui)
        { return D.Enabled(ui) && !D.Loading(ui) && !Field<bool>(ui, "first"); }

        private static Component UniqueUi(UObject[] all, GameObject owner, Type type)
        {
            if (owner == null) return null;
            Component found = null;
            foreach (UObject item in all)
            {
                Component ui = item as Component;
                if (ui == null || ui.gameObject != owner || ui.GetType() != type) continue;
                if (found != null) return null;
                found = ui;
            }
            return found;
        }

        private sealed class Counter
        {
            internal object Row, Template;
            internal int Count, Required, Refresh;
            internal bool SharedRow;
            internal static bool Same(Counter a, Counter b)
            {
                return a != null && b != null && object.ReferenceEquals(a.Row, b.Row) && object.ReferenceEquals(a.Template, b.Template) &&
                    a.Count == b.Count && a.Required == b.Required && a.Refresh == b.Refresh;
            }
        }

        private static Counter ReadCounter(object role, int id)
        {
            IDictionary latest = D.Read(role, "acts") as IDictionary, rewards = D.Read(role, "act_rewards") as IDictionary;
            if (latest == null || latest.Count > 255 || rewards == null || rewards.Count > 6) return null;
            object row = latest[(uint)id];
            Type activityType = Model.FirstType("Database.Activity");
            if (row == null || row.GetType() != activityType || Field<byte>(row, "idactivity") != id) return null;
            object template = D.Read(row, "temp");
            if (template == null || !Field<bool>(template, "active") || Field<byte>(template, "id") != id) return null;
            uint category = Field<byte>(template, "idreward");
            object reward = rewards[category];
            if (category < 1 || category > 6 || reward == null || reward.GetType() != Model.FirstType("Database.Activity_Reward") ||
                Field<byte>(reward, "idact_reward") != category) return null;
            IDictionary listed = D.Read(reward, "acts") as IDictionary;
            if (listed == null || listed.Count > 255) return null;
            object panel = listed[(uint)id];
            int count = Field<byte>(row, "condition_param"), required = Field<int>(template, "cond_param");
            int refresh = Field<int>(row, "refresh_time");
            if (panel == null || panel.GetType() != activityType || !object.ReferenceEquals(template, D.Read(panel, "temp")) ||
                Field<byte>(panel, "idactivity") != id || Field<byte>(panel, "condition_param") != count ||
                Field<int>(panel, "refresh_time") != refresh || required < 1 || required > 255 || count > required) return null;
            return new Counter { Row = row, Template = template, Count = count, Required = required, Refresh = refresh, SharedRow = object.ReferenceEquals(row, panel) };
        }

        private static T Field<T>(object owner, string name)
        {
            object value = D.Read(owner, name);
            if (!(value is T)) throw new ProbeFailure("daily_timed_field_schema:" + name);
            return (T)value;
        }

        private sealed class Frame
        {
            internal object Role, Client, Session, SessionStream, QuizState, QuizInfo, BrawlState, SubUi, QuizTimer;
            internal Component Root, GlobalUi, QuizUi, BrawlUi, BrawlRoot, OldRoot;
            internal ulong CharacterId, BrawlerId;
            internal uint Server, Cycle, GoldConsumed, Phase, Round, BrawlNextTime, BrawlPosition, BrawlerServer, QuestionId, QuestionIndex;
            internal uint QuizRefresh, QuizRank, QuizRightCount, QuizWrongCount;
            internal int Gold, BoundGold, Level, QuestionCount, QuizFilter;
            internal bool QuizFinished, Joined, NetworkIdle, BrawlInit, PremiumQueueIdle;
            internal bool QuizHas, QuizBonus, QuizOwned, QuizVisible, QuizWaitState, QuizEnded, StockCloseLoad;
            internal bool BrawlObserved { get { return BrawlState != null; } }
            internal string Gate = "daily_timed_ui_unavailable", RefreshGate = "daily_timed_owner_or_clock_unavailable", Page = "", QuizWindow = "quiz_window_unknown";
            internal Counter QuizCounter, BrawlCounter;
            internal Counter CounterFor(int id) { return id == Quiz ? QuizCounter : BrawlCounter; }
            internal bool SamePremium(Frame f) { return Gold == f.Gold && BoundGold == f.BoundGold && GoldConsumed == f.GoldConsumed; }
            internal bool SamePremiumNow()
            {
                return Gold == Field<int>(Role, "gold") && BoundGold == Field<int>(Role, "bind_gold") &&
                    GoldConsumed == Field<uint>(Role, "gold_consume");
            }
            internal bool MatchesQuestion(DailyTimedWire.Question q)
            { return QuizInfo != null && QuestionCount == q.Count && QuestionIndex == q.Index && QuestionId == q.Id; }
            internal bool MatchesRuntime(DailyTimedWire.Question q)
            {
                if (!MatchesQuestion(q) || Field<uint>(QuizInfo, "right_count") != q.RightCount ||
                    Field<uint>(QuizInfo, "wrong_count") != q.WrongCount || Field<int>(QuizInfo, "cur_filter") != q.CurrentFilter ||
                    Field<bool>(QuizInfo, "waitstate") != q.WaitState) return false;
                IList questions = D.Read(QuizInfo, "questions") as IList;
                bool[] filters = D.Read(QuizInfo, "filters") as bool[];
                if (questions == null || questions.Count != q.Ids.Length || filters == null || filters.Length != 4) return false;
                for (int i = 0; i < q.Ids.Length; i++)
                    if (!(questions[i] is uint) || (uint)questions[i] != q.Ids[i]) return false;
                for (int i = 0; i < filters.Length; i++)
                    if (filters[i] != ((q.FilterMask & (1 << i)) != 0)) return false;
                return true;
            }
            internal bool SameOwner(Frame f)
            {
                return f != null && Client != null && CharacterId != 0 && Cycle != 0 && CharacterId == f.CharacterId &&
                    Server == f.Server && Cycle == f.Cycle && object.ReferenceEquals(Role, f.Role) &&
                    object.ReferenceEquals(Client, f.Client) && Session != null && object.ReferenceEquals(Session, f.Session) &&
                    object.ReferenceEquals(SessionStream, f.SessionStream) &&
                    GlobalUi != null && object.ReferenceEquals(GlobalUi, f.GlobalUi);
            }
            internal bool SameUi(Frame f, int id)
            {
                if (f == null || Root == null || !object.ReferenceEquals(Root, f.Root)) return false;
                return id == Quiz ? QuizUi != null && object.ReferenceEquals(QuizUi, f.QuizUi) && object.ReferenceEquals(QuizState, f.QuizState) :
                    BrawlRoot != null && object.ReferenceEquals(BrawlRoot, f.BrawlRoot) && object.ReferenceEquals(BrawlUi, f.BrawlUi) &&
                    object.ReferenceEquals(OldRoot, f.OldRoot) && object.ReferenceEquals(BrawlState, f.BrawlState);
            }
            internal bool SameQuizResult(Frame f)
            {
                return SameOwner(f) && SameUi(f, Quiz) && SamePremium(f) && QuizRefresh == f.QuizRefresh &&
                    QuizHas == f.QuizHas && QuizBonus == f.QuizBonus && QuizRank == f.QuizRank &&
                    object.ReferenceEquals(QuizInfo, f.QuizInfo) && QuestionCount == f.QuestionCount &&
                    QuestionIndex == f.QuestionIndex && QuestionId == f.QuestionId &&
                    QuizRightCount == f.QuizRightCount && QuizWrongCount == f.QuizWrongCount &&
                    QuizFilter == f.QuizFilter && QuizWaitState == f.QuizWaitState && QuizEnded == f.QuizEnded &&
                    object.ReferenceEquals(QuizTimer, f.QuizTimer) && Counter.Same(QuizCounter, f.QuizCounter);
            }
            internal bool SameStep(Frame f, int id)
            {
                if (!SameOwner(f) || !SamePremium(f) || !SameUi(f, id) || TaskStamp(id) != f.TaskStamp(id)) return false;
                return Counter.Same(CounterFor(id), f.CounterFor(id));
            }
            internal string Stamp()
            { return TaskStamp(Quiz) + ":" + TaskStamp(Brawl); }
            private string TaskStamp(int id)
            {
                string context = CharacterId + ":" + Server + ":" + Cycle + ":" + Gold + ":" + BoundGold + ":" + GoldConsumed +
                    ":" + Page + ":" + Gate + ":" + RefreshGate + ":" + NetworkIdle + ":" + CounterStamp(CounterFor(id)) + ":" + UiId(Root);
                return id == Quiz ? context + ":" + Level + ":" + QuizWindow + ":" + QuizFinished + ":" + QuizRefresh + ":" + QuizRank +
                    ":" + QuizHas + ":" + (QuizInfo != null) + ":" + UiId(QuizUi) :
                    context + ":" + Phase + ":" + Round + ":" + Joined + ":" + BrawlInit + ":" + BrawlNextTime + ":" + UiId(BrawlRoot) + ":" + UiId(BrawlUi);
            }
            private static int UiId(Component c) { return c == null ? 0 : c.GetInstanceID(); }
            private static string CounterStamp(Counter c)
            { return c == null ? "" : c.Count + "/" + c.Required + "/" + c.Refresh; }
        }

        private string View(int id)
        {
            bool ownView = status == "READY" && task == id;
            string openWhy = Gate(frame, id, true, false), goWhy = Gate(frame, id, false, ownView);
            Counter count = frame.CounterFor(id);
            bool ownsClose = id == Quiz && status == "SUCCEEDED" && quizClose != null;
            string closeWhy = ownsClose ? CloseGate(frame) : "";
            string action = ownsClose && closeWhy == "" ? "close" : ownView && goWhy == "" ? "participate" : !Busy && openWhy == "" ? "open" : "";
            string why = action != "" ? "" : openWhy == "" ? "daily_timed_other_operation" : openWhy == "daily_timed_main_required" ? goWhy : openWhy;
            if (ownsClose && closeWhy != "") why = closeWhy;
            return "{\"action\":" + D.Q(action) + ",\"reason\":" + D.Q(why) +
                ",\"current\":" + Number(count == null ? (int?)null : count.Count) + ",\"required\":" + Number(count == null ? (int?)null : count.Required) + "}";
        }

        private static string Number(int? n) { return n.HasValue ? n.Value.ToString(CultureInfo.InvariantCulture) : "null"; }

        private string OperationJson()
        {
            Counter initial = before == null || close ? null : before.CounterFor(task);
            return "{\"operation_id\":" + D.Q(operation) + ",\"task_id\":" + task + ",\"sequence\":" + sequence +
                ",\"stage\":" + D.Q(operation == "" ? "" : open ? "open" : close ? "close" : "participate") + ",\"status\":" + D.Q(status) +
                ",\"invoked\":" + D.B(invoked) + ",\"opcode\":" + opcode + ",\"reason\":" + D.Q(reason) +
                ",\"request_attribution\":" + (invoked && (opcode == BrawlJoin || opcode == QuizQuick) ? D.Q("UNKNOWN") : "null") +
                ",\"counter_before\":" + Number(initial == null ? (int?)null : initial.Count) + ",\"counter_after\":" + Number(confirmed) + "}";
        }

        internal string Json()
        {
            return "{\"schema\":1,\"revision\":" + revision + ",\"character_id\":" + D.Q(frame.CharacterId.ToString(CultureInfo.InvariantCulture)) +
                ",\"tasks\":{\"12\":" + View(Quiz) + ",\"13\":" + View(Brawl) + "},\"operation\":" + OperationJson() + "}";
        }
    }

    // Read only the bounded fields used for result evidence. Forwarding,
    // deserialization and application of the packet remain game-owned.
    internal sealed class DailyTimedWire
    {
        internal sealed class Progress { internal int Count, Refresh; }
        internal sealed class Join { internal ulong Id; internal uint Server, Position, Round, Phase; }
        internal sealed class Refresh { internal bool Joined; internal uint Phase, NextTime; }
        internal sealed class Question
        {
            internal uint Index, Id, RightCount, WrongCount;
            internal int Count, CurrentFilter, FilterMask;
            internal bool WaitState;
            internal uint[] Ids;
        }
        internal sealed class QuickQuiz
        {
            internal uint Result, Refresh, Rank;
            internal bool Bonus;
            internal Question Question;
        }

        internal static bool ReadProgress(byte[] bytes, int task, out Progress progress)
        {
            progress = null;
            try
            {
                DailyTimedWire packet = new DailyTimedWire(bytes);
                int field, wire, rows = 0;
                while (packet.Field(out field, out wire))
                {
                    if (field != 1) { packet.Skip(wire); continue; }
                    if (++rows > 255) throw new FormatException();
                    DailyTimedWire row = packet.Nested(wire);
                    uint[] values = row.Scalars(3);
                    if (values[1] == task)
                    {
                        if (progress != null || values[2] > 255) throw new FormatException();
                        uint raw = values[3];
                        progress = new Progress { Count = (int)values[2], Refresh = (int)(raw >> 1) ^ -(int)(raw & 1u) };
                    }
                }
                return true;
            }
            catch (FormatException) { progress = null; return false; }
        }

        internal static bool ReadJoin(byte[] bytes, out Join join)
        {
            join = null;
            try
            {
                DailyTimedWire packet = new DailyTimedWire(bytes);
                int field, wire, seen = 0;
                ulong[] values = new ulong[10];
                while (packet.Field(out field, out wire))
                {
                    if (field >= values.Length) { packet.Skip(wire); continue; }
                    Mark(ref seen, field);
                    if (field == 2) { packet.Nested(wire); continue; }
                    if (wire != 0) throw new FormatException();
                    values[field] = packet.Varint();
                    if (field != 1 && values[field] > UInt32.MaxValue) throw new FormatException();
                }
                if ((seen & 510) != 510 || values[1] == 0) return false;
                join = new Join { Id = values[1], Position = (uint)values[3], Round = (uint)values[6], Server = (uint)values[9], Phase = (uint)values[7] };
                return true;
            }
            catch (FormatException) { return false; }
        }

        internal static bool ReadRefresh(byte[] bytes, out Refresh refresh)
        {
            refresh = null;
            try
            {
                DailyTimedWire packet = new DailyTimedWire(bytes);
                int field, wire, seen = 0;
                uint[] values = new uint[8];
                while (packet.Field(out field, out wire))
                {
                    if (field != 1 && field != 3 && field != 6 && field != 7) { packet.Skip(wire); continue; }
                    Mark(ref seen, field);
                    values[field] = packet.UInt(wire);
                }
                const int required = (1 << 1) | (1 << 3) | (1 << 6) | (1 << 7);
                if (seen != required || values[1] > 1 || values[3] > 4 || values[7] > 1) return false;
                refresh = new Refresh { Joined = values[1] != 0, Phase = values[3], NextTime = values[6] };
                return true;
            }
            catch (FormatException) { return false; }
        }

        internal static bool ReadQuestion(byte[] bytes, out Question question)
        {
            question = null;
            try
            {
                DailyTimedWire start = new DailyTimedWire(bytes), quiz = start.Message(1);
                // A quiz runtime without its required account quiz record is
                // an incomplete start response even if its question ID fits.
                quiz.Message(1);
                question = ReadRuntime(quiz.Message(2));
                return true;
            }
            catch (FormatException) { return false; }
        }

        internal static bool ReadQuickQuiz(byte[] bytes, out QuickQuiz receipt)
        {
            receipt = null;
            try
            {
                DailyTimedWire packet = new DailyTimedWire(bytes);
                uint[] result = packet.RequiredScalars(1 << 1, 1);
                if (result[1] > 1) return false;
                DailyTimedWire quiz = packet.Message(2);
                uint[] header = quiz.RequiredScalars((1 << 3) | (1 << 5), 5);
                uint[] record = quiz.Message(1).Scalars(8);
                if (header[5] > 1 || record[7] > 1) return false;
                DailyTimedWire runtime = quiz.Message(2, false);
                receipt = new QuickQuiz { Result = result[1], Refresh = record[1], Rank = header[3],
                    Bonus = record[7] != 0, Question = runtime == null ? null : ReadRuntime(runtime, true) };
                return true;
            }
            catch (FormatException) { return false; }
        }

        private static Question ReadRuntime(DailyTimedWire info, bool quick = false)
        {
            const int required = (1 << 2) | (1 << 3) | (1 << 4) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 10);
            int field, wire, seen = 0;
            uint[] values = new uint[11];
            List<uint> questions = new List<uint>();
            List<uint> filters = new List<uint>();
            while (info.Field(out field, out wire))
            {
                if (field == 1) info.RepeatedValues(wire, questions);
                else if (quick && field == 9) info.RepeatedValues(wire, filters);
                else if (field == 7 || (quick && field <= 10 && (required & (1 << field)) != 0))
                {
                    Mark(ref seen, field);
                    values[field] = info.UInt(wire);
                }
                else info.Skip(wire);
            }
            uint index = values[7];
            if ((seen & (1 << 7)) == 0 || index >= questions.Count || questions.Contains(0)) throw new FormatException();
            int filterMask = 0;
            if (quick)
            {
                if ((seen & required) != required || filters.Count < 4 || values[10] > 1) throw new FormatException();
                for (int i = 0; i < filters.Count; i++)
                {
                    if (filters[i] > 1) throw new FormatException();
                    if (i < 4) filterMask |= (int)filters[i] << i;
                }
            }
            uint filter = values[8];
            return new Question { Index = index, Count = questions.Count, Id = questions[(int)index], Ids = questions.ToArray(),
                RightCount = values[3], WrongCount = values[4], CurrentFilter = (int)(filter >> 1) ^ -(int)(filter & 1u),
                FilterMask = filterMask, WaitState = values[10] != 0 };
        }

        private void RepeatedValues(int wire, List<uint> values)
        {
            if (wire == 0) { AddValue(values, UInt()); return; }
            DailyTimedWire packed = Nested(wire);
            while (packed.pos < packed.end) AddValue(values, packed.UInt());
        }

        private static void AddValue(List<uint> values, uint value)
        {
            if (values.Count == 64) throw new FormatException();
            values.Add(value);
        }

        private readonly byte[] bytes;
        private readonly int end;
        private int pos;
        private DailyTimedWire(byte[] data)
        {
            if (data == null || data.Length > 65536) throw new FormatException();
            bytes = data; end = data.Length;
        }
        private DailyTimedWire(byte[] data, int start, int limit)
        { bytes = data; pos = start; end = limit; }

        private uint[] Scalars(int maximum)
        { return ReadScalars(maximum, (1 << (maximum + 1)) - 2, 0); }

        private uint[] RequiredScalars(int fields, int maximum)
        {
            int saved = pos;
            uint[] values = ReadScalars(maximum, fields, fields);
            pos = saved;
            return values;
        }

        private uint[] ReadScalars(int maximum, int fields, int required)
        {
            int field, wire, seen = 0;
            uint[] values = new uint[maximum + 1];
            while (Field(out field, out wire))
            {
                if (field > maximum || (fields & (1 << field)) == 0) { Skip(wire); continue; }
                Mark(ref seen, field);
                values[field] = UInt(wire);
            }
            if ((seen & required) != required) throw new FormatException();
            return values;
        }
        private static void Mark(ref int seen, int field)
        {
            int mask = 1 << field;
            if ((seen & mask) != 0) throw new FormatException();
            seen |= mask;
        }
        private DailyTimedWire Message(int target, bool required = true)
        {
            int field, wire, start = pos;
            DailyTimedWire value = null;
            while (Field(out field, out wire))
            {
                if (field != target) { Skip(wire); continue; }
                if (value != null) throw new FormatException();
                value = Nested(wire);
            }
            if (required && value == null) throw new FormatException();
            pos = start;
            return value;
        }
        private bool Field(out int field, out int wire)
        {
            field = 0; wire = 0;
            if (pos == end) return false;
            uint key = UInt();
            field = (int)(key >> 3);
            wire = (int)(key & 7);
            if (field == 0) throw new FormatException();
            return true;
        }
        private DailyTimedWire Nested(int wire)
        {
            if (wire != 2) throw new FormatException();
            uint size = UInt();
            int start = pos;
            Advance(size);
            return new DailyTimedWire(bytes, start, pos);
        }
        private void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Advance(8); break;
                case 2: Advance(UInt()); break;
                case 5: Advance(4); break;
                default: throw new FormatException();
            }
        }
        private void Advance(uint size)
        {
            if (size > end - pos) throw new FormatException();
            pos += (int)size;
        }
        private uint UInt(int wire) { if (wire != 0) throw new FormatException(); return UInt(); }
        private uint UInt()
        {
            ulong value = Varint();
            if (value > UInt32.MaxValue) throw new FormatException();
            return (uint)value;
        }
        private ulong Varint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (pos == end) throw new FormatException();
                byte b = bytes[pos++];
                if (shift == 63 && b > 1) throw new FormatException();
                value |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new FormatException();
        }
    }
}
