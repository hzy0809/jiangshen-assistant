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
    internal delegate D DailyFlowerContextProvider();
    internal delegate bool DailyFlowerInvocationCheck();
    internal delegate bool DailyFlowerInvocationGate(DailyFlowerInvocationCheck sample, DailyFlowerInvocationCheck check);

    // An optional observer can add request provenance checks. The normal
    // script route uses the stock opcode callbacks without an observer.
    internal interface DailyFlowerEpoch
    {
        bool CanInvoke(ushort opcode);
        bool OwnsHandler(ushort opcode, object handler);
        bool OwnsReply(ushort opcode, object handler);
    }

    // A stable epoch identity for the exact role, client, socket and stream.
    // Return null for an unobserved, unclean or no longer exclusive epoch.
    internal delegate DailyFlowerEpoch DailyFlowerEpochProvider(object role, object client, DailyTransport transport);

    internal sealed class DailyFlowerModel
    {
        internal const int TaskId = 11;
        private const ushort ListOp = 798, EventsOp = 806, InfoOp = 808, SendOp = 809, ActivityOp = 355;
        private const uint WaitMs = 18000;
        private static readonly Type[] CallbackArgs = { typeof(MemoryStream), typeof(object) };

        private sealed class Window
        {
            internal Component Ui;
            internal DateTime Shown, Loaded;
            internal bool Visible, Enabled, Loading;
            internal Window(Component ui, bool panel)
            {
                Ui = ui;
                Shown = (DateTime)D.Read(ui, "visible_time");
                if (panel) Loaded = (DateTime)D.Read(ui, "m_LoadOverTime");
                Visible = D.Visible(ui);
                Enabled = D.Enabled(ui);
                Loading = D.Loading(ui);
            }
            internal bool Ready { get { return Visible && Enabled && !Loading && Loaded <= DateTime.Now; } }
            internal bool Hidden { get { return !Visible && !Loading; } }
            internal bool SameGeneration(Window w) { return ReferenceEquals(Ui, w.Ui) && Shown == w.Shown && Loaded == w.Loaded; }
            internal bool Same(Window w) { return SameGeneration(w) && Visible == w.Visible && Enabled == w.Enabled && Loading == w.Loading; }
        }

        private sealed class Prompt
        {
            internal Window Window;
            internal object Content, Root;
            internal string Text;
            internal bool Plain;
            internal Prompt(Component ui)
            {
                Window = new Window(ui, false);
                Content = D.Read(ui, "Text");
                Root = D.Read(ui, "old_rmg");
                Text = (string)D.Read(ui, "Message");
                Plain = (string)D.Read(ui, "Title") == "" && D.Read(ui, "OnOK") == null && D.Read(ui, "OnOK_objs") == null &&
                    D.Read(ui, "OnClose") == null && D.Read(ui, "Tex") == null && Flag(ui, "bNoButton") && !Flag(ui, "bCancel") &&
                    !Flag(ui, "bModal") && !Flag(ui, "bGoButton") && !Flag(ui, "isPicMode") && !Flag(ui, "isGold") &&
                    !Flag(ui, "m_bWantClose") && !Flag(ui, "m_bMouseDown");
            }
            internal bool SameContent(Prompt p)
            {
                return Window.SameGeneration(p.Window) && ReferenceEquals(Content, p.Content) &&
                    ReferenceEquals(Root, p.Root) && Text == p.Text && Plain == p.Plain;
            }
            internal bool Same(Prompt p) { return SameContent(p) && Window.Same(p.Window); }
            internal bool Created(Prompt old, object root, string expected)
            {
                return old.Window.Hidden && Window.Ready && Plain && ReferenceEquals(Window.Ui, old.Window.Ui) &&
                    ReferenceEquals(Root, root) && Content != null && !ReferenceEquals(Content, old.Content) &&
                    Window.Shown > old.Window.Shown && !String.IsNullOrEmpty(expected) && Text == expected && IsCached();
            }
            internal bool IsCached() { return ReferenceEquals(Window.Ui, D.Static(Model.FirstType("__BaseUI"), "msgBox")); }
        }

        private sealed class Frame
        {
            internal D Context;
            internal DailyTransport Transport;
            internal DailyFlowerEpoch Epoch;
            internal Window Buddy, Flower;
            internal Prompt Box;
            internal object Sub, Template, Rows, Category, ListedRows, ActivityHandler;
            internal string Feature, Name, Overlay;
            internal bool NetworkIdle;
            internal ulong Role, Recipient;
            internal uint Now, Cycle, Next, Reputation, GoldConsumed;
            internal int Count, Listed, Refresh, ListedRefresh, Required, Gold, Bound;

            internal bool SameOwner(Frame f)
            {
                return f != null && Context.SameOwnership(f.Context) && Role != 0 && Role == f.Role &&
                    Transport.Same(f.Transport) && ReferenceEquals(Epoch, f.Epoch) && Cycle == f.Cycle && Next == f.Next && Now >= f.Now &&
                    ReferenceEquals(Buddy.Ui, f.Buddy.Ui) && ReferenceEquals(Flower.Ui, f.Flower.Ui) &&
                    ReferenceEquals(Box.Window.Ui, f.Box.Window.Ui);
            }
            internal bool SameTask(Frame f)
            {
                return ReferenceEquals(Template, f.Template) && Required == f.Required && ReferenceEquals(Rows, f.Rows) &&
                    ReferenceEquals(Category, f.Category) && ReferenceEquals(ListedRows, f.ListedRows);
            }
            internal bool SameCounters(Frame f)
            { return SameTask(f) && Count == f.Count && Listed == f.Listed && Refresh == f.Refresh && ListedRefresh == f.ListedRefresh; }
            internal bool SameGold(Frame f) { return Gold == f.Gold && Bound == f.Bound && GoldConsumed == f.GoldConsumed; }
            internal bool SameUi(Frame f)
            {
                return ReferenceEquals(Sub, f.Sub) && Feature == f.Feature && Recipient == f.Recipient && Name == f.Name &&
                    Buddy.Same(f.Buddy) && Flower.Same(f.Flower) && Box.Same(f.Box);
            }
            internal bool BuddySelected { get { return ReferenceEquals(Sub, Buddy.Ui) && Feature == "buddy"; } }
            internal bool FlowerSelected { get { return ReferenceEquals(Sub, Flower.Ui) && Feature == "buddyflower"; } }
            internal bool NoPanel { get { return Sub == null && Buddy.Hidden && Flower.Hidden; } }
        }

        private sealed class Intent
        {
            internal string Action;
            internal Frame Baseline;
        }

        private readonly DailyRefreshAdmission refresh;
        private readonly DailyFlowerEpochProvider provenance;
        private readonly Dictionary<ushort, Watch> watches = new Dictionary<ushort, Watch>();
        private Frame frame, origin, probe, sent;
        private Window buddyLoadGeneration, buddyGeneration, flowerGeneration;
        private Prompt ownedPrompt;
        private DailyFlowerWire.Friends friends;
        private List<DailyFlowerWire.Friend> candidates;
        private DailyFlowerWire.Friend target;
        private Intent queued;
        private int revision, sequence, since, candidateIndex, pushRefresh, pushCalls, notInvokedSequence;
        private volatile bool stopped;
        private bool touched, attempted, stockReturned, pushObserved, closed, observationAvailable;
        private string status = "IDLE", reason = "", operationId, stamp, observationReason = "flower_observation_unavailable";
        private string notInvokedOperationId = "";

        internal DailyFlowerModel() : this(null, DailyRefreshGate.Read) { }
        internal DailyFlowerModel(DailyFlowerEpochProvider observer) : this(observer, DailyRefreshGate.Read) { }
#if JSB_DAILY_FLOWER_TEST
        internal
#else
        private
#endif
        DailyFlowerModel(DailyFlowerEpochProvider observer, DailyRefreshAdmission readRefresh)
        { provenance = observer; refresh = readRefresh; }
        internal int Revision { get { return revision; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal bool Unresolved { get { return status == "PENDING" || OutcomeUnknown; } }
        internal bool Busy { get { return queued != null || touched || watches.Count != 0 || Unresolved; } }

        internal bool Queue(int taskId, ulong role, string id, int seq, int expectedRevision, string action, out string why)
        {
            why = "flower_admission";
            if (stopped || queued != null || !observationAvailable || frame == null || taskId != TaskId || role == 0 || role != frame.Role ||
                seq <= sequence || expectedRevision != revision || !new DevelopmentCommand { OperationId = id }.ValidId() ||
                (action == "open" ? operationId != null : operationId == null || operationId != id))
                return false;
            object client = frame.Context.Client;
            if (!Monitor.TryEnter(client, 0)) return false;
            try
            {
                why = Admit(frame, action);
                if (why != "") return false;
                queued = new Intent { Action = action, Baseline = frame };
                operationId = id;
                sequence = seq;
                notInvokedSequence = 0; notInvokedOperationId = "";
                PublishRevision();
                return true;
            }
            catch (Exception) { return false; }
            finally { Monitor.Exit(client); }
        }

        internal bool ExecuteGui(DailyFlowerContextProvider fresh, DevelopmentPublication publish,
            DailyFlowerInvocationGate begin, out string why)
        {
            why = "flower_not_queued";
            Intent intent = queued;
            if (intent == null || stopped) return false;
            object client = intent.Baseline.Context.Client;
            if (!D.Gui || fresh == null || publish == null || begin == null || !Monitor.TryEnter(client, 0))
                return Reject("flower_gui_or_client_busy", out why);
            bool publishing = false;
            try
            {
                Frame current = Read(fresh(), null);
                why = Recheck(intent, current);
                if (why != "") return Reject(why, out why);
                publishing = true;
                publish();
                publishing = false;
                if (stopped || queued != intent) return Reject("flower_cancelled_before_call", out why);
                current = Read(fresh(), null);
                why = Recheck(intent, current);
                if (why != "") return Reject(why, out why);
                MethodInfo method = StockMethod(intent.Action, current);
                MethodInfo select = intent.Action == "inspect" ? D.Method(Model.GameType("MainUI"), "handle_sub_ui",
                    new Type[] { Model.FirstType("__BaseUI"), typeof(string) }, true, typeof(bool)) : null;
                if (intent.Action == "send") Bind(current, ActivityOp, null, "handelActvity", null);

                Frame accepted = null;
                string failure = "";
                bool began = begin(delegate
                {
                    if (queued != intent || stopped || !D.Gui) return false;
                    Frame sample = Read(fresh(), null);
                    failure = Recheck(intent, sample);
                    if (failure != "" || !Boundary(intent, sample)) return false;
                    accepted = sample;
                    return true;
                }, delegate { return accepted != null && Boundary(intent, accepted); });
                if (!began || accepted == null || !Boundary(intent, accepted))
                    return Reject(failure == "" ? "flower_cancelled_before_call" : failure, out why);
                queued = null;
                since = Environment.TickCount;
                observationAvailable = true; observationReason = "";
                Execute(intent.Action, accepted, method, select);
                why = stopped ? "flower_stopped_during_call" : "";
                PublishRevision();
                return why == "";
            }
            catch (Exception error)
            {
                if (publishing) throw;
                return Reject(error is ProbeFailure ? error.Message : "flower_stock_call_or_binding_failed", out why);
            }
            finally { Monitor.Exit(client); }
        }

        private void Execute(string action, Frame f, MethodInfo method, MethodInfo select)
        {
            if (action == "open")
            {
                origin = f;
                touched = true;
                Set("OPENING", "flower_waiting_buddy_list");
                method.Invoke(null, null);
                if (!ReferenceEquals(f.Context.SubUi, f.Buddy.Ui) || Feature() != "buddy")
                    throw new ProbeFailure("flower_buddy_selection");
                if (!stopped) BindBuddy(f);
            }
            else if (action == "inspect")
            {
                target = candidates[candidateIndex];
                probe = f;
                Set("INSPECTING", "flower_waiting_info");
                if (!(bool)select.Invoke(null, new object[] { f.Flower.Ui, "buddyflower" }) ||
                    !ReferenceEquals(f.Context.SubUi, f.Flower.Ui) || Feature() != "buddyflower")
                    throw new ProbeFailure("flower_info_selection");
                if (stopped) return;
                if (!D.Gui || !CurrentRoot(f.Context) || !f.Context.SameOwnership(f.Context) ||
                    !f.Transport.StillCurrent() || !ReferenceEquals(f.Epoch, ReadEpoch(f.Context, f.Transport)) ||
                    !Permitted(f.Epoch, action) || !Idle(f.Context.Client) || Expired())
                    throw new ProbeFailure("flower_info_owner_changed_during_selection");
                method.Invoke(f.Flower.Ui, new object[] { target.Id });
                if (!stopped) Bind(f, InfoOp, f.Flower.Ui, "sc_buddy_flower_info", null);
            }
            else if (action == "send")
            {
                sent = f;
                attempted = true;
                Set("PENDING", "flower_waiting_free_reply_and_activity");
                method.Invoke(f.Flower.Ui, new object[] { 0 });
                if (!stopped) Bind(f, SendOp, f.Flower.Ui, "sc_buddy_send_flower", 0);
            }
            else Close(f, method);
        }

        private void Close(Frame f, MethodInfo method)
        {
            bool prompt = ownedPrompt != null;
            method.Invoke(prompt ? f.Box.Window.Ui : null, new object[] { prompt ? (object)false : null });
            Frame after = Read(f.Context, f);
            if (!after.SameOwner(f) || !after.NoPanel || !after.Box.Window.Hidden || !after.Box.SameContent(f.Box) ||
                !after.Buddy.SameGeneration(f.Buddy) || !after.Flower.SameGeneration(f.Flower) ||
                !after.SameCounters(f) || !after.SameGold(f) || after.Reputation != f.Reputation || Expired())
                throw new ProbeFailure("flower_close_unconfirmed");
            frame = after;
            frame.Overlay = "PASS";
            ownedPrompt = null;
            if (status == "SKIP_PROMPT" && ++candidateIndex < candidates.Count) Set("CANDIDATE", "");
            else if (status == "RESULT_PROMPT")
            {
                closed = true;
                Finish("COMPLETED", "flower_free_reply_and_correlated_activity_observed", true);
            }
            else if (status == "ERROR_PROMPT") Finish("REJECTED", "flower_server_rejected", true);
            else Finish("NO_CANDIDATES", status == "EMPTY" ? "flower_no_eligible_friends" : "flower_all_candidates_already_gifted", true);
        }

        internal void Capture(D context)
        {
            if (stopped || status == "UNKNOWN" || status == "REJECTED" || status == "COMPLETED" || status == "NO_CANDIDATES")
            { Detach(); return; }
            observationAvailable = false;
            object client = context == null ? null : context.Client;
            if (client == null)
            {
                frame = null;
                observationReason = "flower_owner_unavailable";
                if (touched) Finish(attempted ? "UNKNOWN" : "REJECTED", "flower_owner_unavailable", false);
                else reason = observationReason;
                PublishRevision();
                return;
            }
            if (!Monitor.TryEnter(client, 0))
            {
                observationReason = "flower_client_busy";
                if (!touched) reason = observationReason;
                PublishRevision(); return;
            }
            try
            {
                frame = Read(context, null);
                observationAvailable = true; observationReason = "";
                if (!touched) reason = "";
                if (touched && !Owned(frame)) throw new ProbeFailure("flower_owner_cycle_resource_or_deadline_changed");
                if (touched && !attempted && (!frame.SameCounters(origin) || frame.Reputation != origin.Reputation))
                    throw new ProbeFailure("flower_baseline_changed");
                if (status == "OPENING") ObserveList(frame);
                else if (status == "INSPECTING" || status == "LOADING") ObserveInfo(frame);
                else if (status == "PENDING") ObserveSend(frame);
                else if (ownedPrompt != null && !ownedPrompt.Same(frame.Box))
                    throw new ProbeFailure("flower_owned_prompt_changed");
                else if (status == "READY" && !Ready(frame))
                    throw new ProbeFailure("flower_owned_recipient_window_changed");
            }
            catch (Exception e)
            {
                frame = null;
                observationAvailable = false;
                observationReason = e is ProbeFailure ? e.Message : "flower_observation_failed";
                if (touched) Finish(attempted ? "UNKNOWN" : "REJECTED", observationReason, false);
                else reason = observationReason;
            }
            finally { Monitor.Exit(client); }
            PublishRevision();
        }

        private void ObserveList(Frame f)
        {
            if (!f.BuddySelected || !f.Flower.Hidden || !f.Box.Same(origin.Box))
                throw new ProbeFailure("flower_buddy_window_changed");
            BindBuddy(f);
            if (buddyLoadGeneration == null) return;
            Watch list = Witness(f, ListOp), events = Witness(f, EventsOp);
            if (list.Returned ? buddyGeneration == null || !f.Buddy.Same(buddyGeneration) : !f.Buddy.Same(buddyLoadGeneration))
                throw new ProbeFailure("flower_buddy_generation_changed");
            if (!list.Returned || !events.Returned || !f.Buddy.Ready || f.Overlay != "PASS") return;
            friends = (DailyFlowerWire.Friends)list.Value;
            candidates = friends.Candidates(f.Role);
            if (!friends.Matches(f.Buddy.Ui)) throw new ProbeFailure("flower_friend_list_readback");
            Detach();
            Set(candidates.Count == 0 ? "EMPTY" : "CANDIDATE", "");
        }

        private void ObserveInfo(Frame f)
        {
            Watch info = Witness(f, InfoOp);
            if (!info.Returned)
            {
                if (!f.FlowerSelected || !f.Flower.Same(probe.Flower) || !f.Box.Same(probe.Box))
                    throw new ProbeFailure("flower_info_pending_window_changed");
                return;
            }
            DailyFlowerWire.Info reply = (DailyFlowerWire.Info)info.Value;
            if (reply.When >= probe.Cycle)
            {
                if (!f.NoPanel || info.Result == null || !info.Result.Same(f.Box))
                    throw new ProbeFailure("flower_already_sent_prompt_changed");
                ownedPrompt = info.Result;
                Detach();
                Set("SKIP_PROMPT", "flower_candidate_already_gifted");
                return;
            }
            if (!f.FlowerSelected || !f.Buddy.Hidden || !f.Box.Same(probe.Box) || f.Recipient != target.Id || f.Name != target.Name)
                throw new ProbeFailure("flower_info_recipient_or_window_changed");
            if (!f.Flower.Visible)
            {
                if (!f.Flower.Loading) throw new ProbeFailure("flower_owned_loader_not_observed");
                Set("LOADING", "flower_waiting_recipient_window");
                return;
            }
            if (f.Flower.Shown <= probe.Flower.Shown || f.Flower.Loaded <= probe.Flower.Loaded ||
                flowerGeneration != null && !f.Flower.SameGeneration(flowerGeneration) ||
                info.After.Flower.Visible && !f.Flower.SameGeneration(info.After.Flower))
                throw new ProbeFailure("flower_recipient_generation_changed");
            flowerGeneration = f.Flower;
            if (f.Flower.Ready && f.Overlay == "PASS")
            {
                Detach();
                Set("READY", "");
            }
        }

        private void ObserveSend(Frame f)
        {
            Watch reply = Witness(f, SendOp);
            Witness(f, ActivityOp);
            if (!reply.Returned)
            {
                if (!Ready(f) || f.Reputation != sent.Reputation)
                    throw new ProbeFailure("flower_send_pending_window_or_reputation_changed");
                return;
            }
            if (!f.NoPanel || reply.Result == null || !reply.Result.Same(f.Box))
                throw new ProbeFailure("flower_result_prompt_changed");
            stockReturned = true;
            bool ok = (bool)reply.Value;
            if (f.Reputation != (ok ? checked(sent.Reputation + 10) : sent.Reputation))
                throw new ProbeFailure("flower_reputation_readback_changed");
            if (!ok && (pushObserved || !f.SameCounters(sent)))
                throw new ProbeFailure("flower_error_conflicting_account_evidence");
            if (ok && (!pushObserved || !Settled(f))) return;
            ownedPrompt = reply.Result;
            Detach();
            Set(ok ? "RESULT_PROMPT" : "ERROR_PROMPT", ok ? "flower_correlated_activity_observed" : "flower_server_rejected");
        }

        private bool Settled(Frame f)
        { return f.Count == sent.Count + 1 && f.Listed == f.Count && f.Refresh == pushRefresh && f.ListedRefresh == pushRefresh; }
        private bool Ready(Frame f)
        {
            return flowerGeneration != null && f.FlowerSelected && f.Flower.Ready && f.Flower.SameGeneration(flowerGeneration) &&
                f.Buddy.Hidden && f.Box.Same(probe.Box) && f.Recipient == target.Id && f.Name == target.Name;
        }
        private bool Owned(Frame f)
        { return f.SameOwner(origin) && f.SameTask(origin) && f.SameGold(origin) && !Expired(); }

        private string Admit(Frame f, string action)
        {
            if (f == null || stopped) return "flower_observation_unavailable";
            if (!Permitted(f.Epoch, action)) return "flower_clean_transport_provenance_unavailable";
            if (f.Overlay != "PASS") return f.Overlay;
            if (touched && !Owned(f)) return "flower_owner_cycle_resource_or_deadline_changed";
            if (!Idle(f.Context.Client)) return "flower_network_busy";
            if (action == "close")
            {
                if (status == "EMPTY" && f.SameCounters(origin) && f.BuddySelected && f.Buddy.Ready &&
                    f.Buddy.SameGeneration(buddyGeneration) && f.Flower.Hidden && f.Box.Same(origin.Box) &&
                    friends.Matches(f.Buddy.Ui)) return "";
                if ((status == "RESULT_PROMPT" || status == "ERROR_PROMPT" || status == "SKIP_PROMPT") &&
                    f.NoPanel && ownedPrompt != null && ownedPrompt.Same(f.Box) && f.Box.IsCached() &&
                    (status == "RESULT_PROMPT" ? Settled(f) && f.Reputation == checked(sent.Reputation + 10) :
                        f.SameCounters(origin) && f.Reputation == origin.Reputation)) return "";
                return "flower_owned_close_required";
            }
            if (f.Count != f.Listed || f.Refresh != f.ListedRefresh || f.Count >= f.Required)
                return "flower_activity_stale_or_complete";
            if (!ActivityReady(f)) return "flower_activity_callback_unavailable";
            if (action == "open" && status == "IDLE" && f.NoPanel && f.Box.Window.Hidden) return "";
            if (!touched || attempted || !f.SameCounters(origin) || f.Reputation != origin.Reputation ||
                friends == null || !friends.Matches(f.Buddy.Ui)) return "flower_friend_snapshot_or_baseline_changed";
            if (action == "inspect" && status == "CANDIDATE" && f.Flower.Hidden && f.Box.Window.Hidden &&
                (candidateIndex == 0 ? f.BuddySelected && f.Buddy.Ready && f.Buddy.SameGeneration(buddyGeneration) : f.NoPanel)) return "";
            if (action == "send" && status == "READY" && Ready(f) && f.Reputation <= UInt32.MaxValue - 10) return "";
            return "flower_phase_or_owned_window_required";
        }

        private string Recheck(Intent intent, Frame f)
        {
            Frame before = intent.Baseline;
            if (queued != intent || stopped || !D.Gui) return "flower_gui_admission_changed";
            if (!f.SameOwner(before) || !f.SameCounters(before) || !f.SameGold(before) || f.Reputation != before.Reputation ||
                !f.SameUi(before) || !ReferenceEquals(f.ActivityHandler, before.ActivityHandler)) return "flower_gui_baseline_changed";
            return Admit(f, intent.Action);
        }

        private bool Boundary(Intent intent, Frame f)
        {
            object client = f.Context.Client;
            if (queued != intent || stopped || !D.Gui || touched && Expired() ||
                !ReferenceEquals(client, DailyTransport.CurrentClient(f.Context.Character)) || !f.Transport.StillCurrent() ||
                (ulong)D.Read(f.Context.Character, "id") != f.Role ||
                !ReferenceEquals(f.Epoch, ReadEpoch(f.Context, f.Transport)) || !Permitted(f.Epoch, intent.Action) ||
                !CurrentRoot(f.Context) || !ReferenceEquals(f.Sub, f.Context.SubUi) || f.Feature != Feature() ||
                !GlobalInput() || !Idle(client) || !f.Buddy.Same(new Window(f.Buddy.Ui, true)) ||
                !f.Flower.Same(new Window(f.Flower.Ui, true)) || !f.Box.Same(new Prompt(f.Box.Window.Ui)) ||
                f.Box.Window.Visible && !f.Box.IsCached() || (ulong)D.Read(f.Flower.Ui, "m_BuddyId") != f.Recipient ||
                (string)D.Read(f.Flower.Ui, "m_BuddyName") != f.Name || !ReferenceEquals(Handlers(client)[ActivityOp], f.ActivityHandler) ||
                intent.Action != "close" && !ActivityReady(f)) return false;
            uint now, day, cycle;
            return refresh(f.Context.Character, out now, out day, out cycle) == "" && now >= f.Now && cycle == f.Cycle &&
                f.Next == (uint)D.Read(f.Context.Character, "next_resource_time") && (!touched || !Expired());
        }

        private DailyFlowerEpoch ReadEpoch(D context, DailyTransport transport)
        { return provenance == null ? null : provenance(context.Character, context.Client, transport); }

        private static bool Permitted(DailyFlowerEpoch epoch, string action)
        {
            if (epoch == null) return true;
            if (action == "open") return epoch.CanInvoke(ListOp) && epoch.CanInvoke(EventsOp);
            if (action == "inspect") return epoch.CanInvoke(InfoOp);
            if (action == "send") return epoch.CanInvoke(SendOp);
            return action == "close";
        }

        private static MethodInfo StockMethod(string action, Frame f)
        {
            if (action == "open") return D.Method(Model.GameType("MainUI"), "_social_friend", Type.EmptyTypes, true, typeof(void));
            if (action == "inspect") return D.Method(f.Flower.Ui.GetType(), "cs_buddy_flower_info", new Type[] { typeof(ulong) }, false, typeof(void));
            if (action == "send") return D.Method(f.Flower.Ui.GetType(), "sendflower", new Type[] { typeof(int) }, false, typeof(void));
            if (f.Box.Window.Visible) return D.Method(f.Box.Window.Ui.GetType(), "ButtonOK", new Type[] { typeof(bool) }, false, typeof(void));
            return D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void));
        }

        // The stock callbacks already run on the client monitor. Use their
        // bound components directly, without enumerating a new Unity inventory.
        private Frame Read(D context, Frame known)
        {
            if (context == null) throw new ProbeFailure("flower_context_unavailable");
            if (context.Paused) throw new ProbeFailure("flower_context_paused");
            if (!context.SameOwnership(context)) throw new ProbeFailure("flower_" + context.OwnershipGate());
            if (!CurrentRoot(context)) throw new ProbeFailure("flower_root_unavailable");
            if (context.Character.GetType() != Model.FirstType("Database.Character"))
                throw new ProbeFailure("flower_character_type_unavailable");
            if (FlagStatic(Model.FirstType("BaseAsset"), "_PHONEUI"))
                throw new ProbeFailure("flower_phone_ui_active");
            DailyTransport transport = DailyTransport.Read(context.Client);
            uint now, day, cycle;
            string clock = refresh(context.Character, out now, out day, out cycle);
            if (transport == null || clock != "" || cycle == 0)
                throw new ProbeFailure(transport == null ? "flower_transport_unavailable" : clock != "" ? clock : "flower_refresh_cycle_unavailable");
            Component buddy = known == null ? Find(context, "BuddyUI") : known.Buddy.Ui;
            Component flower = known == null ? Find(context, "BuddyFlowerUI") : known.Flower.Ui;
            Component box = known == null ? Find(context, "MessageBox") : known.Box.Window.Ui;
            if (!Belongs(context, buddy) || !Belongs(context, flower) || !Belongs(context, box))
                throw new ProbeFailure("flower_stock_ui_ownership");
            object role = context.Character;
            Frame f = new Frame { Context = context, Transport = transport, Now = now, Cycle = cycle, Next = (uint)D.Read(role, "next_resource_time"),
                Role = (ulong)D.Read(role, "id"), Buddy = new Window(buddy, true), Flower = new Window(flower, true),
                Box = new Prompt(box), Sub = context.SubUi, Feature = Feature(), Recipient = (ulong)D.Read(flower, "m_BuddyId"),
                Name = (string)D.Read(flower, "m_BuddyName"), Gold = (int)D.Read(role, "gold"), Bound = (int)D.Read(role, "bind_gold"),
                GoldConsumed = (uint)D.Read(role, "gold_consume"), Reputation = (uint)D.Read(role, "reputation"),
                ActivityHandler = Handlers(context.Client)[ActivityOp], Epoch = ReadEpoch(context, transport),
                NetworkIdle = Idle(context.Client) };
            ReadTask(f);
            if (known == null)
            {
                Component allowed = f.BuddySelected ? buddy : f.FlowerSelected ? flower :
                    ownedPrompt != null && ownedPrompt.Same(f.Box) ? box : null;
                f.Overlay = context.Panels(allowed, FindReminder(context), context.FindCapitalDemon());
                if (f.Overlay == "PASS" && (!GlobalInput() || f.Sub != null && !f.BuddySelected && !f.FlowerSelected ||
                    f.Buddy.Visible && !f.BuddySelected || f.Flower.Visible && !f.FlowerSelected ||
                    f.Box.Window.Visible && allowed != box)) f.Overlay = "flower_ui_overlay";
            }
            return f;
        }

        private static void ReadTask(Frame f)
        {
            object role = f.Context.Character;
            IDictionary rows = Map(D.Read(role, "acts"), 255), rewards = Map(D.Read(role, "act_rewards"), 6);
            f.Rows = rows;
            f.Template = D.Template("activity", TaskId);
            if (f.Template == null || f.Template.GetType().FullName != "Database._activity" ||
                f.Template.GetType().Assembly.GetName().Name != "dbdata" ||
                (byte)D.Read(f.Template, "id") != TaskId || !Flag(f.Template, "active"))
                throw new ProbeFailure("flower_activity_template");
            f.Required = (int)D.Read(f.Template, "cond_param");
            uint category = (byte)D.Read(f.Template, "idreward");
            f.Category = rewards[category];
            if (category == 0 || f.Category == null || f.Category.GetType() != Model.FirstType("Database.Activity_Reward") ||
                (byte)D.Read(f.Category, "idact_reward") != category || f.Required < 1 || f.Required > 255)
                throw new ProbeFailure("flower_activity_category");
            IDictionary listed = Map(D.Read(f.Category, "acts"), 255);
            f.ListedRows = listed;
            f.Count = Row(f, rows[(uint)TaskId], out f.Refresh);
            f.Listed = Row(f, listed[(uint)TaskId], out f.ListedRefresh);
        }

        private static int Row(Frame f, object row, out int refreshTime)
        {
            if (row == null || row.GetType() != Model.FirstType("Database.Activity") ||
                (byte)D.Read(row, "idactivity") != TaskId || !ReferenceEquals(D.Read(row, "temp"), f.Template))
                throw new ProbeFailure("flower_activity_row");
            refreshTime = (int)D.Read(row, "refresh_time");
            int count = (byte)D.Read(row, "condition_param");
            if (count > f.Required) throw new ProbeFailure("flower_activity_count");
            return count;
        }

        private void BindBuddy(Frame f)
        {
            if (buddyLoadGeneration != null) return;
            if (!ReferenceEquals(f.Context.SubUi, f.Buddy.Ui) || Feature() != "buddy" ||
                !ReferenceEquals(f.Epoch, ReadEpoch(f.Context, f.Transport)))
                throw new ProbeFailure("flower_buddy_loader_owner_changed");
            Window actual = new Window(f.Buddy.Ui, true);
            if (actual.Visible || actual.Shown != origin.Buddy.Shown)
                throw new ProbeFailure("flower_buddy_initial_hidden_generation_missed");
            if (actual.Loaded <= origin.Buddy.Loaded || actual.Loading) return;
            IDictionary handlers = Handlers(f.Context.Client);
            // Show advances m_LoadOverTime and registers both requests while
            // hidden. Only the successful 798 callback makes the page visible.
            if (handlers[ListOp] == null || handlers[EventsOp] == null)
                throw new ProbeFailure("flower_buddy_initial_handlers_missed");
            buddyLoadGeneration = actual;
            Bind(f, ListOp, f.Buddy.Ui, "sc_buddy_load", null);
            Bind(f, EventsOp, f.Buddy.Ui, "sc_buddy_load_event", 1);
        }

        private bool ActivityReady(Frame f)
        {
            Watch watch;
            if (watches.TryGetValue(ActivityOp, out watch))
                return ReferenceEquals(f.ActivityHandler, watch.Handler) && watch.StillInstalled();
            object handler = f.ActivityHandler;
            return handler != null && handler.GetType() == Model.FirstType("game.net.tcp_base+Handler") &&
                (float)D.Read(handler, "life") == -1f && D.Read(handler, "arg") == null &&
                Exact(D.Read(handler, "handler") as Delegate, null, D.Method(Model.FirstType("RootBase"), "handelActvity", CallbackArgs, true, typeof(void)));
        }

        private void Bind(Frame f, ushort opcode, Component receiver, string name, object expectedArg)
        {
            if (!f.Transport.StillCurrent() || watches.ContainsKey(opcode)) throw new ProbeFailure("flower_callback_transport_or_duplicate");
            object handler = Handlers(f.Context.Client)[opcode];
            Type type = receiver == null ? Model.FirstType("RootBase") : receiver.GetType();
            MethodInfo method = D.Method(type, name, CallbackArgs, receiver == null, typeof(void));
            if (method.DeclaringType != type || handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") ||
                opcode != ActivityOp && f.Epoch != null && !f.Epoch.OwnsHandler(opcode, handler))
                throw new ProbeFailure("flower_stock_handler_missing");
            object arg = D.Read(handler, "arg");
            Delegate original = D.Read(handler, "handler") as Delegate;
            float life = (float)D.Read(handler, "life");
            bool list = opcode == ListOp || opcode == EventsOp;
            if (!Exact(original, receiver, method) || !object.Equals(arg, expectedArg) ||
                (opcode == ActivityOp ? life != -1f : list ? !(life > 0f && life <= 10f) : life != 10f))
                throw new ProbeFailure("flower_stock_callback_identity");
            watches.Add(opcode, new Watch(this, f, opcode, handler, original, arg));
        }

        private static bool Exact(Delegate callback, object receiver, MethodInfo method)
        { return callback != null && ReferenceEquals(callback.Target, receiver) && callback.GetInvocationList().Length == 1 && callback.Method == method; }

        private Watch Witness(Frame f, ushort opcode)
        {
            Watch watch;
            if (!watches.TryGetValue(opcode, out watch))
            {
                if (opcode == ListOp || opcode == EventsOp) return null;
                throw new ProbeFailure("flower_callback_missing");
            }
            if (watch.Error != null) throw new ProbeFailure(watch.Error);
            if ((opcode == ActivityOp || !watch.Returned ? !watch.StillInstalled() : Handlers(f.Context.Client).Contains(opcode)))
                throw new ProbeFailure("flower_callback_failed_or_replaced");
            return watch;
        }

        private object BeforeReply(Watch watch, Frame before, byte[] body)
        {
            ushort op = watch.Opcode;
            Frame owner = watch.Owner;
            if (!before.SameOwner(owner) || !before.SameTask(owner) || !before.SameGold(owner) || Expired() ||
                op != ActivityOp && before.Epoch != null && !before.Epoch.OwnsReply(op, watch.Handler))
                throw new ProbeFailure("flower_callback_owner_boundary");
            if (op == ActivityOp)
            {
                if (!attempted || sent == null || ++pushCalls > 32) throw new ProbeFailure("flower_activity_dispatch_boundary");
                int changedRefresh;
                int? count = GuildReceipt.Progress(body, TaskId, out changedRefresh);
                if (!count.HasValue || count == sent.Count && changedRefresh == sent.Refresh && !pushObserved) return null;
                if (count != sent.Count + 1 || pushObserved || !before.SameCounters(sent)) throw new ProbeFailure("flower_activity_progress_conflict");
                return changedRefresh;
            }
            if (op == ListOp || op == EventsOp)
            {
                Window generation = op == ListOp || buddyGeneration == null ? buddyLoadGeneration : buddyGeneration;
                if (status != "OPENING" || !before.BuddySelected || generation == null ||
                    !before.Buddy.Same(generation) || !before.SameCounters(origin) || !before.Flower.Hidden ||
                    before.Reputation != origin.Reputation || !before.Box.Same(origin.Box))
                    throw new ProbeFailure("flower_list_callback_boundary");
                if (op == ListOp) return DailyFlowerWire.ReadFriends(body);
                DailyFlowerWire.ReadEvents(body);
                return null;
            }
            if (op == InfoOp)
            {
                if (status != "INSPECTING" || !before.FlowerSelected || !before.Flower.Same(probe.Flower) ||
                    !before.Box.Same(probe.Box) || !before.SameCounters(probe))
                    throw new ProbeFailure("flower_info_callback_boundary");
                DailyFlowerWire.Info info = DailyFlowerWire.ReadInfo(body);
                if (info.Id != target.Id || info.Name != target.Name || info.When > before.Now)
                    throw new ProbeFailure("flower_info_reply_recipient_or_time");
                return info;
            }
            if (status != "PENDING" || !Ready(before) || !before.Flower.SameGeneration(sent.Flower) ||
                before.Reputation != sent.Reputation || !(before.SameCounters(sent) || pushObserved && Settled(before)))
                throw new ProbeFailure("flower_send_callback_boundary");
            return DailyFlowerWire.ReadSend(body);
        }

        private void AfterReply(Watch watch, Frame before, Frame after)
        {
            if (!after.SameOwner(before) || !after.SameTask(before) || !after.SameGold(before) || Expired())
                throw new ProbeFailure("flower_callback_readback_owner");
            if (watch.Opcode == ActivityOp)
            {
                if (watch.Value == null) return;
                pushRefresh = (int)watch.Value;
                if (!Settled(after)) throw new ProbeFailure("flower_activity_cache_readback");
                pushObserved = true;
                return;
            }
            if (watch.Opcode == ListOp || watch.Opcode == EventsOp)
            {
                if (!after.BuddySelected || !after.Flower.Same(before.Flower) || !after.Box.Same(before.Box) ||
                    after.Recipient != before.Recipient || after.Name != before.Name ||
                    !after.SameCounters(before) || after.Reputation != before.Reputation)
                    throw new ProbeFailure("flower_buddy_callback_readback");
                if (watch.Opcode == ListOp)
                {
                    if (!before.Buddy.Hidden || !after.Buddy.Visible || !after.Buddy.Enabled || after.Buddy.Loading ||
                        after.Buddy.Loaded != before.Buddy.Loaded || after.Buddy.Shown <= before.Buddy.Shown ||
                        !((DailyFlowerWire.Friends)watch.Value).Matches(after.Buddy.Ui))
                        throw new ProbeFailure("flower_buddy_hidden_to_visible_unproven");
                    buddyGeneration = after.Buddy;
                }
                else if (!after.Buddy.Same(before.Buddy)) throw new ProbeFailure("flower_events_changed_buddy_generation");
                return;
            }
            string expected = null;
            if (watch.Opcode == InfoOp)
            {
                DailyFlowerWire.Info info = (DailyFlowerWire.Info)watch.Value;
                if (after.Recipient != target.Id || after.Name != target.Name || !after.SameCounters(before) ||
                    after.Reputation != before.Reputation || !after.Buddy.Hidden)
                    throw new ProbeFailure("flower_info_stock_readback");
                if (info.When >= after.Cycle) expected = LocalText(1000);
                else if (!after.FlowerSelected || !after.Box.Same(before.Box) || !after.Flower.Loading && !after.Flower.Visible)
                    throw new ProbeFailure("flower_owned_loader_not_observed");
            }
            else
            {
                bool ok = (bool)watch.Value;
                expected = ok ? LocalText(20125, target.Name) : LocalText(20128);
                if (after.Recipient != target.Id || after.Name != target.Name || !after.SameCounters(before) ||
                    after.Reputation != (ok ? checked(before.Reputation + 10) : before.Reputation))
                    throw new ProbeFailure("flower_free_reply_readback");
            }
            if (expected == null) return;
            if (!after.NoPanel || !after.Flower.SameGeneration(before.Flower) ||
                !after.Box.Created(before.Box, after.Context.UiRoot, expected))
                throw new ProbeFailure("flower_stock_prompt_unproven");
            watch.Result = after.Box;
        }

        private sealed class Watch
        {
            internal readonly ushort Opcode;
            internal readonly Frame Owner;
            internal readonly object Handler;
            private readonly DailyFlowerModel model;
            private readonly object argument;
            private readonly Delegate original, wrapper;
            private readonly DevelopmentNormalCallback forward;
            private readonly FieldInfo field;
            private readonly int threadId;
            private volatile bool armed = true;
            internal bool Returned;
            internal string Error;
            internal object Value;
            internal Frame After;
            internal Prompt Result;

            internal Watch(DailyFlowerModel model, Frame owner, ushort opcode, object handler, Delegate original, object argument)
            {
                this.model = model; Owner = owner; Opcode = opcode; Handler = handler;
                this.original = original; this.argument = argument;
                threadId = Thread.CurrentThread.ManagedThreadId;
                field = handler.GetType().GetField("handler", D.Instance);
                if (field == null || field.FieldType != original.GetType() || !ReferenceEquals(field.GetValue(handler), original))
                    throw new ProbeFailure("flower_callback_schema");
                forward = (DevelopmentNormalCallback)(original.Target == null ?
                    Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Method) :
                    Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Target, original.Method));
                wrapper = Delegate.CreateDelegate(original.GetType(), this, D.Method(GetType(), "OnResponse", CallbackArgs, false, typeof(void)));
                field.SetValue(handler, wrapper);
            }

            private void OnResponse(MemoryStream stream, object arg)
            {
                Frame before = null;
                bool locked = armed && Monitor.TryEnter(Owner.Context.Client, 0);
                try
                {
                    if (armed)
                    {
                        try
                        {
                            if (!locked || model.stopped || Error != null || Opcode != ActivityOp && Returned ||
                                Thread.CurrentThread.ManagedThreadId != threadId || !ReferenceEquals(arg, argument) ||
                                !ReferenceEquals(field.GetValue(Handler), wrapper) || !ReferenceEquals(D.Read(Handler, "arg"), argument) ||
                                (Opcode == ActivityOp ? !StillInstalled() : Handlers(Owner.Context.Client).Contains(Opcode) ||
                                    Queued(Owner.Context.Client, Opcode)) || stream == null || stream.GetType() != typeof(MemoryStream) ||
                                !stream.CanRead || stream.Position != 0 || Opcode != ActivityOp && stream.Length < 1 || stream.Length > 65536)
                                throw new ProbeFailure("flower_callback_dispatch_identity");
                            before = model.Read(Owner.Context, Owner);
                            Value = model.BeforeReply(this, before, stream.ToArray());
                        }
                        catch (Exception error) { Error = error is ProbeFailure ? error.Message : "flower_callback_schema"; }
                    }
                    // The original always receives its own stream and argument.
                    try { forward(stream, arg); }
                    catch { if (armed) Error = "flower_original_callback_failed"; throw; }
                    if (armed && !model.stopped && Error == null && before != null)
                    {
                        try
                        {
                            After = model.Read(Owner.Context, Owner);
                            model.AfterReply(this, before, After);
                            Returned = true;
                        }
                        catch (Exception error) { Error = error is ProbeFailure ? error.Message : "flower_callback_schema"; }
                    }
                }
                finally { if (locked) Monitor.Exit(Owner.Context.Client); }
            }

            internal bool StillInstalled()
            {
                return armed && ReferenceEquals(Handlers(Owner.Context.Client)[Opcode], Handler) &&
                    ReferenceEquals(field.GetValue(Handler), wrapper) && ReferenceEquals(D.Read(Handler, "arg"), argument) &&
                    (Opcode != ActivityOp || (float)D.Read(Handler, "life") == -1f);
            }

            internal bool Restore()
            {
                armed = false;
                object client = Owner.Context.Client;
                if (!Monitor.TryEnter(client, 0)) return false;
                try
                {
                    if (ReferenceEquals(field.GetValue(Handler), wrapper)) field.SetValue(Handler, original);
                    return true;
                }
                catch (Exception) { return false; }
                finally { Monitor.Exit(client); }
            }
        }

        private static string LocalText(uint id, string name = null)
        {
            Type type = Model.FirstType("__BaseUI");
            if (name == null)
                return (string)D.Method(type, id == 1000 ? "get_string" : "load_str", new Type[] { typeof(uint) }, true, typeof(string))
                    .Invoke(null, new object[] { id });
            return (string)D.Method(type, "load_str", new Type[] { typeof(uint), typeof(object[]) }, true, typeof(string))
                .Invoke(null, new object[] { id, new object[] { name } });
        }

        private static Component FindReminder(D context)
        {
            // The pinned stock UI only draws a fading label and calls
            // EndGUI(hasClose: false, hasBlock: false). It owns no dialog.
            Type type = Model.GameType("RemindTitleMsg");
            Component found = null;
            foreach (UnityEngine.Object item in context.All)
            {
                Component candidate = item as Component;
                if (candidate == null || candidate.GetType() != type || !Belongs(context, candidate)) continue;
                if (found != null) throw new ProbeFailure("flower_reminder_ui_duplicate");
                found = candidate;
            }
            return found;
        }

        private static Component Find(D context, string name)
        {
            Type type = name == "BuddyUI" ? Model.GameType(name) : Model.FirstType(name);
            Component found = null;
            foreach (UnityEngine.Object item in context.All)
            {
                Component candidate = item as Component;
                if (candidate == null || candidate.GetType() != type || candidate.gameObject != context.GlobalUi.gameObject) continue;
                if (found != null) throw new ProbeFailure("flower_stock_ui_duplicate");
                found = candidate;
            }
            if (found == null || !ReferenceEquals(found, context.GlobalUi.gameObject.GetComponent(name)))
                throw new ProbeFailure("flower_stock_ui_missing");
            return found;
        }

        private static bool Belongs(D context, Component panel)
        {
            return panel != null && panel.gameObject.activeInHierarchy && panel.gameObject == context.GlobalUi.gameObject &&
                panel.transform.root == context.UiRoot.transform && ReferenceEquals(D.Read(panel, "rmg"), context.UiRoot);
        }

        private static bool CurrentRoot(D context)
        {
            object load = D.Static(Model.FirstType("BaseAsset"), "lds");
            return load != null && ReferenceEquals(D.Read(load, "currmg"), context.Root);
        }

        private static bool GlobalInput()
        {
            return FlagStatic(Model.FirstType("Global"), "extern_enabled") &&
                !FlagStatic(Model.FirstType("PopupUI"), "popup") && !FlagStatic(Model.GameType("BuildingBuildUI"), "m_bBuildVisible") &&
                !FlagStatic(Model.GameType("BuildingBehaviourScript"), "m_bEditVisible");
        }

        private static string Feature() { return (string)D.Static(Model.FirstType("__BaseUI"), "subui"); }
        private static bool Flag(object item, string name) { return D.Bool(D.Read(item, name)); }
        private static bool FlagStatic(Type type, string name) { return D.Bool(D.Static(type, name)); }
        private static IDictionary Handlers(object client) { return Map(D.Read(client, "m_handlers"), 512); }
        private static IDictionary Map(object value, int max)
        {
            IDictionary map = value as IDictionary;
            if (map == null || map.Count > max) throw new ProbeFailure("flower_map_schema");
            return map;
        }
        private static IList List(object value)
        {
            IList list = value as IList;
            if (list == null || list.Count > 128) throw new ProbeFailure("flower_queue_schema");
            return list;
        }
        private static bool Idle(object client)
        {
            if (List(D.Read(client, "messages")).Count != 0 || List(D.Read(client, "m_sendDatas")).Count != 0 ||
                Flag(client, "m_bSending") || DevelopmentCallbackCapture.AnyCallbackExecuting) return false;
            foreach (DictionaryEntry pair in Handlers(client))
            {
                if (!(pair.Key is ushort) || pair.Value == null || pair.Value.GetType() != Model.FirstType("game.net.tcp_base+Handler") ||
                    (float)D.Read(pair.Value, "life") != -1f) return false;
                // 807 is the stock permanent BuddyUI.handleNewMessage push.
                // Only the four request callbacks belong to this operation.
                ushort opcode = (ushort)pair.Key;
                if (opcode == ListOp || opcode == EventsOp || opcode == InfoOp || opcode == SendOp) return false;
            }
            return true;
        }
        private static bool Queued(object client, ushort opcode)
        {
            foreach (object item in List(D.Read(client, "messages"))) if ((ushort)D.Read(item, "op") == opcode) return true;
            return false;
        }

        private bool Expired() { return unchecked((uint)(Environment.TickCount - since)) >= WaitMs; }
        private void Set(string state, string detail) { status = state; reason = detail; }
        private bool Reject(string detail, out string why)
        {
            why = detail;
            if (!stopped) Finish(attempted ? "UNKNOWN" : "REJECTED", detail, !touched);
            PublishRevision();
            return false;
        }

        private void Finish(string state, string detail, bool released)
        {
            queued = null;
            if (released) { touched = false; closed = true; }
            Set(state, detail);
            Detach();
        }

        private void Detach()
        {
            List<ushort> restored = new List<ushort>();
            foreach (KeyValuePair<ushort, Watch> entry in watches)
                if (entry.Value.Restore()) restored.Add(entry.Key);
            foreach (ushort opcode in restored) watches.Remove(opcode);
        }

        internal void NotInvoked(string detail)
        {
            if (queued == null || stopped) return;
            if (detail == "scenario_gui_callback_unavailable" && queued.Action == "open" && !touched && !attempted)
            {
                // Probe cancelled this exact queued command before any GUI call.
                // Retain its witness without reserving an unexecuted operation.
                notInvokedSequence = sequence; notInvokedOperationId = operationId;
                queued = null; operationId = null;
                Set("IDLE", detail);
                PublishRevision();
                return;
            }
            string ignored;
            Reject(detail, out ignored);
        }

        internal void Stop()
        {
            stopped = true;
            if (status != "COMPLETED" && status != "NO_CANDIDATES")
                Finish(attempted ? "UNKNOWN" : "REJECTED", touched ? "flower_stopped_ui_unverified" : "flower_stopped", false);
            else Detach();
            PublishRevision();
        }

        private void PublishRevision()
        {
            string view = ViewJson() + OperationJson();
            if (stamp != view) { stamp = view; revision++; }
        }

        internal string ViewJson()
        {
            bool observed = observationAvailable && frame != null;
            return "{\"task_id\":11,\"role_id\":" + D.Q(!observed ? "" : frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"observation_available\":" + D.B(observed) +
                ",\"current\":" + (!observed ? -1 : frame.Count) + ",\"required\":" + (!observed ? -1 : frame.Required) +
                ",\"refresh_cycle\":" + (!observed ? 0 : frame.Cycle) +
                ",\"recipient_id\":" + D.Q(target == null ? "" : target.Id.ToString(CultureInfo.InvariantCulture)) +
                ",\"next_action\":" + D.Q(NextAction()) +
                ",\"gate\":" + D.Q(!observed ? observationReason : frame.Overlay != "PASS" ? frame.Overlay :
                    !frame.NetworkIdle ? "flower_network_busy" : "PASS") +
                ",\"ui_ownership_unreleased\":" + D.B(touched) + "}";
        }

        private string NextAction()
        {
            if (stopped || queued != null || !observationAvailable || frame == null || !frame.NetworkIdle) return "";
            if (status == "IDLE" && frame.Overlay == "PASS" && frame.Count < frame.Required) return "open";
            if (status == "CANDIDATE") return "inspect";
            if (status == "READY") return "send";
            if (status == "EMPTY" || status == "RESULT_PROMPT" || status == "ERROR_PROMPT" || status == "SKIP_PROMPT") return "close";
            return "";
        }

        internal string Json()
        { return "{\"schema\":1,\"revision\":" + revision + ",\"view\":" + ViewJson() + ",\"operation\":" + OperationJson() + "}"; }

        internal string OperationJson()
        {
            return "{\"id\":" + D.Q(operationId ?? "") + ",\"sequence\":" + sequence +
                ",\"gui_not_invoked_sequence\":" + notInvokedSequence +
                ",\"gui_not_invoked_operation_id\":" + D.Q(notInvokedOperationId) +
                ",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) + ",\"queued\":" + D.B(queued != null) +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"stock_callback_returned\":" + D.B(stockReturned) +
                ",\"progress_before\":" + (origin == null ? -1 : origin.Count) +
                ",\"progress_after\":" + (frame == null ? -1 : frame.Count) +
                ",\"account_progress_correlated\":" + D.B(pushObserved && stockReturned && (status == "RESULT_PROMPT" || status == "COMPLETED")) +
                ",\"closed\":" + D.B(closed) + "}";
        }
    }

    internal static class DailyFlowerWire
    {
        internal sealed class Info
        {
            internal ulong Id;
            internal string Name;
            internal uint When;
        }

        internal sealed class Friend
        {
            internal ulong Id;
            internal string Name;
            internal uint Online, Fan, Monster;
            internal bool Special;
            internal int Order { get { return Special ? (Online == 1 ? 0 : 1) : Fan == 1 ? (Online == 1 ? 2 : 3) : (Online == 1 ? 4 : 5); } }
        }

        internal sealed class Friends
        {
            internal readonly List<Friend> Ordered = new List<Friend>(), Blocked = new List<Friend>();

            internal List<Friend> Candidates(ulong role)
            {
                Dictionary<ulong, int> ids = new Dictionary<ulong, int>();
                Dictionary<string, int> names = new Dictionary<string, int>(StringComparer.Ordinal);
                Dictionary<ulong, bool> blocked = new Dictionary<ulong, bool>();
                foreach (Friend f in Ordered)
                {
                    int count;
                    ids.TryGetValue(f.Id, out count);
                    ids[f.Id] = count + 1;
                    names.TryGetValue(f.Name, out count);
                    names[f.Name] = count + 1;
                }
                foreach (Friend f in Blocked) blocked[f.Id] = true;
                List<Friend> result = new List<Friend>();
                foreach (Friend f in Ordered)
                    if (f.Id != 0 && f.Id != role && f.Monster == 0 && !String.IsNullOrEmpty(f.Name) &&
                        ids[f.Id] == 1 && names[f.Name] == 1 && !blocked.ContainsKey(f.Id)) result.Add(f);
                return result;
            }

            internal bool Matches(object buddy)
            { return MatchesList(D.Read(buddy, "Buddys"), Ordered, "Buddy") && MatchesList(D.Read(buddy, "Blacklist"), Blocked, "Black"); }

            private static bool MatchesList(object stock, List<Friend> wire, string kind)
            {
                IList list = stock as IList;
                Type type = Model.FirstType("Buddy");
                if (list == null || list.Count != wire.Count) return false;
                for (int i = 0; i < wire.Count; i++)
                {
                    object row = list[i];
                    Friend f = wire[i];
                    if (row == null || row.GetType() != type || (ulong)D.Read(row, "id") != f.Id ||
                        (string)D.Read(row, "name") != f.Name || D.Read(row, "type").ToString() != kind ||
                        (bool)D.Read(row, "isonline") != (f.Online == 1)) return false;
                    if (kind == "Buddy" && ((bool)D.Read(row, "isspecial") != f.Special ||
                        (bool)D.Read(row, "isfan") != (f.Fan == 1) || (bool)D.Read(row, "ismonster") != (f.Monster == 1))) return false;
                }
                return true;
            }
        }

        internal static bool ReadSend(byte[] body)
        {
            // SCBuddySendFlower_0x329 is one required enum with OK=0, Error=1.
            // It contains no recipient or request identifier.
            if (body.Length != 2 || body[0] != 8 || body[1] > 1) throw new FormatException("flower_send_schema");
            return body[1] == 0;
        }

        internal static Info ReadInfo(byte[] body)
        {
            Reader r = new Reader(body);
            Info info = new Info();
            int field, wire, seen = 0;
            while (r.Tag(out field, out wire))
            {
                Require(field <= 5 && (seen & 1 << field) == 0 && wire == (field == 2 ? 2 : 0));
                seen |= 1 << field;
                if (field == 1) info.Id = r.Varint();
                else if (field == 2) info.Name = r.String();
                else if (field == 5) info.When = r.UInt();
                else r.UInt();
            }
            Require(seen == 62 && info.Id != 0 && info.Name.Length > 0);
            return info;
        }

        internal static Friends ReadFriends(byte[] body)
        {
            Reader r = new Reader(body);
            Friends result = new Friends();
            List<Friend> unordered = new List<Friend>();
            int field, wire, seen = 0;
            uint outcome = 1;
            while (r.Tag(out field, out wire))
            {
                Require(field <= 10);
                if (field >= 2 && field <= 6)
                {
                    Require(wire == 2);
                    Reader child = r.Child();
                    if (field == 2) { Require(unordered.Count < 100); unordered.Add(ReadFriend(child, false)); }
                    else if (field == 4) { Require(result.Blocked.Count < 100); result.Blocked.Add(ReadFriend(child, true)); }
                }
                else
                {
                    Require(wire == 0 && (seen & 1 << field) == 0);
                    seen |= 1 << field;
                    uint value = r.UInt();
                    if (field == 1) outcome = value;
                }
            }
            Require(seen == (2 | 128 | 256 | 512 | 1024) && outcome == 0);
            for (int group = 0; group < 6; group++)
                foreach (Friend f in unordered)
                    if (f.Order == group) result.Ordered.Add(f);
            return result;
        }

        internal static void ReadEvents(byte[] body)
        {
            Reader r = new Reader(body);
            int field, wire, seen = 0;
            uint outcome = 1;
            while (r.Tag(out field, out wire))
            {
                Require(field <= 6);
                if (field == 2) { Require(wire == 2); r.Child(); }
                else
                {
                    Require(wire == 0 && (seen & 1 << field) == 0);
                    seen |= 1 << field;
                    uint value = r.UInt();
                    if (field == 1) outcome = value;
                }
            }
            Require(seen == 122 && outcome == 0);
        }

        private static Friend ReadFriend(Reader r, bool blocked)
        {
            Friend f = new Friend();
            int field, wire, seen = 0;
            const int onlineField = 4;
            int fields = blocked ? 7 : 10;
            while (r.Tag(out field, out wire))
            {
                Require(field <= fields && (seen & 1 << field) == 0 && wire == (field == 2 ? 2 : 0));
                seen |= 1 << field;
                if (field == 1) f.Id = r.Varint();
                else if (field == 2) f.Name = r.String();
                else
                {
                    uint value = r.UInt();
                    if (field == onlineField) f.Online = value;
                    else if (!blocked && field == 5) f.Fan = value;
                    else if (!blocked && field == 9) f.Monster = value;
                    else if (!blocked && field == 10) { Require(value <= 1); f.Special = value == 1; }
                }
            }
            Require(seen == (1 << (fields + 1)) - 2 && f.Online <= 1 && f.Fan <= 1 && f.Monster <= 1);
            return f;
        }

        private static void Require(bool condition) { if (!condition) throw new FormatException("flower_wire_schema"); }

        private sealed class Reader
        {
            private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
            private readonly byte[] bytes;
            private readonly int end;
            private int position;

            internal Reader(byte[] body) : this(body, 0, body.Length) { Require(body.Length <= 65536); }
            private Reader(byte[] bytes, int start, int end) { this.bytes = bytes; position = start; this.end = end; }
            internal bool Tag(out int field, out int wire)
            {
                field = wire = 0;
                if (position == end) return false;
                uint tag = UInt();
                field = (int)(tag >> 3);
                wire = (int)(tag & 7);
                Require(field != 0);
                return true;
            }
            internal uint UInt() { return checked((uint)Varint()); }
            internal ulong Varint()
            {
                ulong value = 0;
                for (int shift = 0; shift <= 63 && position < end; shift += 7)
                {
                    byte b = bytes[position++];
                    Require(shift != 63 || b <= 1);
                    value |= (ulong)(b & 127) << shift;
                    if ((b & 128) == 0)
                    {
                        Require(shift == 0 || b != 0);
                        return value;
                    }
                }
                throw new FormatException("flower_wire_varint");
            }
            internal Reader Child()
            {
                int size = checked((int)UInt());
                Require(size <= end - position);
                int start = position;
                position += size;
                return new Reader(bytes, start, position);
            }
            internal string String()
            {
                Reader child = Child();
                int length = child.end - child.position;
                Require(length <= 256);
                return Utf8.GetString(bytes, child.position, length);
            }
        }
    }
}
