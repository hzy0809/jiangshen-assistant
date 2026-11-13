using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;
using M = JiangShen.AssistantRuntime.DailyDivinityBattleModel;
using F = JiangShen.AssistantRuntime.DailyDivinityBattleModel.Frame;

internal static class DailyDivinityBattleTests
{
    private const string Operation = "00000000000000000000000000000008";

    private sealed class Message
    {
        internal readonly ushort Opcode;
        internal readonly MemoryStream Stream;
        internal readonly Action<MemoryStream> Callback;

        internal Message(ushort opcode, byte[] body, Action<MemoryStream> callback)
        { Opcode = opcode; Stream = body == null ? null : new MemoryStream(body, false); Callback = callback; }
    }

    private sealed class Stock
    {
        internal sealed class Slot
        {
            internal DevelopmentNormalCallback Callback;
            internal readonly object Arg;
            internal readonly DevelopmentNormalCallback Original;
            internal int Calls;
            internal Slot(Stock stock, ushort opcode)
            {
                Arg = opcode == M.FormationOpcode ? (object)true : null;
                Original = Callback = delegate(MemoryStream stream, object arg)
                {
                    Check(ReferenceEquals(Arg, arg), "stock argument preserved");
                    Calls++;
                    if (stock.active.Callback != null) stock.active.Callback(stream);
                };
            }
        }

        internal readonly Dictionary<ushort, Slot> Handlers = new Dictionary<ushort, Slot>();
        internal readonly List<Message> Messages = new List<Message>();
        private Message active;

        internal Slot Register(ushort opcode)
        {
            Slot handler = new Slot(this, opcode);
            Handlers.Add(opcode, handler);
            return handler;
        }

        internal object Lookup(ushort opcode)
        { Slot live; return Handlers.TryGetValue(opcode, out live) ? live : null; }

        internal DailyDivinityReplyTap Attach(ushort opcode, DailyDivinityAdmissionCheck before, Action<byte[]> after, Action<byte[]> preflight)
        {
            Slot slot;
            if (!Handlers.TryGetValue(opcode, out slot)) return null;
            FieldInfo callback = typeof(Slot).GetField("Callback", BindingFlags.Instance | BindingFlags.NonPublic);
            return DailyDivinityReplyTap.Attach(this, opcode == M.ActivityOpcode, slot, callback, slot.Arg,
                delegate { return Lookup(opcode); }, before, after, preflight);
        }

        internal Message Add(ushort opcode, byte[] bytes, Action<MemoryStream> callback)
        {
            Message message = new Message(opcode, bytes, callback);
            Messages.Add(message);
            return message;
        }

        internal void Dispatch(Message message)
        {
            lock (this)
            {
                Check(Messages.Remove(message), "fixture dequeue");
                Slot slot;
                if (!Handlers.TryGetValue(message.Opcode, out slot)) return;
                if (message.Opcode != M.ActivityOpcode) Handlers.Remove(message.Opcode);
                active = message;
                slot.Callback(message.Stream, slot.Arg);
                if (message.Stream != null) message.Stream.Close();
            }
        }

        internal void Sample(F frame)
        {
            foreach (KeyValuePair<ushort, Slot> pair in Handlers) frame.Handlers.Add(pair.Key, pair.Value);
            frame.NetworkIdle = Handlers.Count == 1 && Messages.Count == 0;
        }
    }

    private sealed class NaturalWitness : IDailyDivinityNaturalWitness
    {
        internal readonly DailyDivinityNaturalScope Scope;
        internal readonly object RestoreLock = new object(), Owned = new object(), Original = new object();
        internal DailyDivinityNaturalEvidence Evidence;
        internal object Installed;
        internal bool RefuseRestore, ThrowOnRestore, InFlight;
        internal int RestoreCalls;
        internal NaturalWitness(DailyDivinityNaturalScope scope) { Scope = scope; Armed = true; Installed = Owned; }
        public bool Armed { get; private set; }
        public DailyDivinityNaturalEvidence Read() { return Evidence; }
        public bool TryDisarmAndRestore()
        {
            Armed = false;
            RestoreCalls++;
            if (!Monitor.TryEnter(RestoreLock, 0)) return false;
            try
            {
                if (ThrowOnRestore) throw new InvalidOperationException("fixture provider restoration failed");
                if (RefuseRestore || InFlight) return false;
                if (ReferenceEquals(Installed, Owned)) Installed = Original;
                return true;
            }
            finally { Monitor.Exit(RestoreLock); }
        }
    }

    private sealed class Fixture
    {
        internal readonly M Model;
        internal readonly Stock Net = new Stock();
        internal NaturalWitness Witness;
        internal readonly object HomeRoot = new object(), MainUi = new object(), Scene = new object(), Panel = new object(),
            Formation = new object(), Form = new object(), OnAttack = new object(), Character = new object(), Client = new object(),
            Loader = new object(), Global = new object(), Rows = new object(), Listed = new object(), Reward = new object(),
            TaskTemplate = new object(), RewardTemplate = new object(), Troops = new object(), Troop = new object(), TroopData = new object(),
            Battle = new object(), BattleRoot = new object(), ReportToken = new object();
        internal object Row = new object(), Socket = new object();
        internal readonly int Target;
        internal ulong Role = 78, ReportId;
        internal uint Now = 19500, Next = 104400, Cycle = 18000, Cooldown;
        internal int Tick = 10, Seq = 0, Page = 0, Count = 0, Refresh = 3, Category = M.Category;
        internal bool CacheMismatch, Battling, BattleVisible, Fighting, WaitEnd, UseEnd, Killed;

        internal Fixture(int target, bool naturalAvailable = true)
        {
            Target = target;
            Net.Register(M.ActivityOpcode);
            Model = naturalAvailable ? new M(delegate(DailyDivinityNaturalScope scope)
            {
                object socket = Socket;
                Model.TestCallbackOwner = delegate { return Role == scope.Role && Cycle == scope.Cycle && ReferenceEquals(Socket, socket); };
                Witness = new NaturalWitness(scope);
                return Witness;
            }) : new M();
            Model.TestAttach = Net.Attach;
            Model.TestHandler = Net.Lookup;
            Model.TestBattle = delegate { return Battling ? new DailyDivinityBattleAnchor(Battle, BattleRoot, ReportToken) : null; };
        }

        internal F Read()
        {
            F f = new F
            {
                Character = Character, Client = Client, Loader = Loader, GlobalRoot = Global, SocketWitness = Socket, Role = Role,
                Tick = Tick, Now = Now, Next = Next, Cycle = Cycle, Root = Page == 0 ? HomeRoot : Scene,
                Home = Page == 0, PanelReady = Page == 1, FormReady = Page == 2, HomeMain = Page == 0 ? MainUi : null,
                Scene = Scene, Panel = Panel, Formation = Formation, OnAttack = Page == 2 ? OnAttack : null,
                Form = Page == 2 ? Form : null, FormId = Page == 2 ? 3 : 0,
                Task = M.TaskId, TaskCategory = Category, Required = 1, Count = Count, Refresh = Refresh,
                TaskTemplate = TaskTemplate, RewardTemplate = RewardTemplate,
                Rows = Rows, Listed = Listed, Reward = Reward, Row = Row, ListedRow = CacheMismatch ? new object() : Row,
                Selected = Troop, SelectedData = TroopData, SelectedIdentity = true, SelectedOwned = Page >= 2, Troops = Troops,
                Target = Target, KilledMask = (1 << Target) - 1 | (Killed ? 1 << Target : 0), Cooldown = Cooldown, SelectedCooldown = Cooldown,
                Battle = Battle, BattleRoot = BattleRoot, BattleOwned = Battling, Battling = Battling, BattleVisible = BattleVisible,
                Fighting = Fighting, WaitEnd = WaitEnd, UseEnd = UseEnd, SelectedKilled = Killed,
                ReportId = ReportId, ReportToken = Battling ? ReportToken : null
            };
            Net.Sample(f);
            return f;
        }

        internal void Capture()
        { Tick++; Model.Observe(Read()); }

        internal bool Queue(string action, int target)
        {
            string why;
            return Model.Queue(M.TaskId, Role, Operation, ++Seq, Model.Revision, action, target, out why);
        }

        internal void Prepare()
        {
            Capture();
            Check(Queue("open", Target) && Model.BeginQueuedForTest(Read(), null), "fixture opens exact panel");
            Page = 1; Capture();
            Check(Model.Status == "OPEN", "fixture open observed");
            Check(Queue("select", Target) && Model.BeginQueuedForTest(Read(), null), "fixture selects first surviving troop");
            Page = 2; Capture();
            Check(Model.Status == "READY", "fixture observes exact formation");
        }

        internal void Commit()
        {
            Prepare();
            Check(Queue("start", Target), "fixture admits 298");
            F baseline = Read();
            Check(Model.BeginQueuedForTest(baseline, delegate { Net.Register(M.FormationOpcode); }), "fixture starts 298");
            Check(Model.MutationAttempted && Model.Busy && Model.Status == "PENDING", "298 immediately owns UI");
        }

        internal Message QueueFormation()
        {
            return Net.Add(M.FormationOpcode, new byte[] { 8, 1 }, delegate(MemoryStream stream)
            {
                Check(stream.Position == 0 && stream.ReadByte() == 8, "original 298 callback receives original stream");
                Net.Register(M.BattleOpcode);
                Page = 3;
            });
        }

        internal void CompleteFormation()
        {
            Message formation = QueueFormation();
            Capture();
            Net.Dispatch(formation);
            Capture();
            Check(Model.Status == "PENDING" && Model.BattleWon == null, "298 returned without settlement");
        }

        internal void CompleteBattleStart(uint result)
        {
            Message battle = Net.Add(M.BattleOpcode, BattleStart(Target, result), delegate(MemoryStream stream)
            {
                if (result != 0) return;
                Battling = WaitEnd = UseEnd = true; Cooldown = 90;
            });
            Capture();
            Net.Dispatch(battle);
            Capture();
            if (result == 0)
            {
                Check(Model.Status == "PENDING" && Model.BattleWon == null && !Model.TaskConfirmed,
                    "1201 starts the battle but cannot settle it");
                BattleVisible = true; Fighting = true; Capture();
            }
        }

        internal Message QueueEnd(int type, int target, bool win, bool viewAll = true)
        {
            Stock.Slot handler = Net.Register(M.EndOpcode);
            Fighting = false;
            byte[] body = End(type, target, win, 700);
            Witness.Evidence = new DailyDivinityNaturalEvidence
            {
                Scope = Witness.Scope, Battle = Witness.Scope.Battle, RegisteredHandler = handler,
                ViewAllAtRegistration = viewAll, NaturalRegistration = true, ReportAtRegistration = 0, Body = body
            };
            Message message = Net.Add(M.EndOpcode, body, delegate(MemoryStream stream)
            {
                Check(stream.CanRead && stream.Position == 0, "original 1032 stream unchanged");
                WaitEnd = UseEnd = false;
                ReportId = 700;
                Killed = win;
                if (win) Cooldown = 0;
            });
            Capture();
            return message;
        }

        internal void CompleteEnd(int type, int target, bool win, bool viewAll = true)
        {
            Message message = QueueEnd(type, target, win, viewAll);
            Stock.Slot handler = Net.Handlers[M.EndOpcode];
            Net.Dispatch(message);
            Witness.Evidence.Calls = 1;
            Witness.Evidence.DispatchedHandler = handler;
            Witness.Evidence.CallbackReturned = true;
            Capture();
        }

        internal void Push(int task, int count, int refresh)
        {
            Message message = Net.Add(M.ActivityOpcode,
                new byte[] { 10, 6, 8, (byte)task, 16, (byte)count, 24, (byte)(refresh * 2) }, delegate
                {
                    if (task != M.TaskId) return;
                    Count = count; Refresh = refresh; Row = new object();
                });
            Capture();
            Net.Dispatch(message);
            Capture();
        }

        internal void ManualReturn()
        {
            Page = 0; Battling = BattleVisible = false; Capture();
        }
    }

    private static byte[] BattleStart(int target, uint result)
    {
        if (target == 0) return new byte[] { 8, (byte)result, 34, 1, 99, 56, 90 };
        return new byte[] { 8, (byte)result, 16, (byte)target, 34, 1, 99, 56, 90 };
    }

    private static byte[] End(int type, int target, bool win, ulong id)
    {
        List<byte> wire = new List<byte>();
        wire.Add(8); wire.Add(0);
        wire.Add(16); wire.Add((byte)type);
        if (target != 0) { wire.Add(40); wire.Add((byte)target); }
        if (win) { wire.Add(48); wire.Add(1); }
        wire.Add(136); wire.Add(1);
        while (id >= 128) { wire.Add((byte)((id & 127) | 128)); id >>= 7; }
        wire.Add((byte)id);
        return wire.ToArray();
    }

    private static void TestAdmission()
    {
        Fixture first = new Fixture(0);
        first.Capture();
        Check(!first.Queue("open", 4), "stock ordered selection must not skip runtime troop zero");
        first.Cooldown = first.Now + 1; first.Capture();
        Check(!first.Queue("open", 0), "server-clock cooldown blocks");
        first.Cooldown = first.Now; first.Category = 3; first.Capture();
        Check(!first.Queue("open", 0), "task8 must be in category2");
        first.Category = M.Category; first.Capture();
        string why;
        Check(!first.Model.Queue(7, first.Role, Operation, 10, first.Model.Revision, "open", 0, out why), "task7 cannot admit");
        Check(!first.Model.Queue(M.TaskId, first.Role, Operation, 10, first.Model.Revision - 1, "open", 0, out why), "stale revision cannot admit");
        Check(first.Queue("open", 0), "equality at cooldown threshold permits the first target");
        first.Tick += 1500;
        Check(!first.Model.BeginQueuedForTest(first.Read(), null), "GUI expiry blocks stock call");
        first.Model.NotInvoked("test_expiry");
        Check(!first.Model.Busy && !first.Model.MutationAttempted, "pre-open refusal has no hold");

        Fixture selected = new Fixture(4);
        selected.Prepare();
        Check(selected.Queue("start", 4), "ready formation queues start");
        selected.Cooldown = selected.Now + 1;
        Check(!selected.Model.BeginQueuedForTest(selected.Read(), null), "fresh target changes before start");
        selected.Model.Stop();
        Check(!selected.Model.MutationAttempted && selected.Model.Busy, "precommit Stop retains owned formation");
        selected.ManualReturn();
        Check(!selected.Model.Busy, "precommit hold releases on witnessed stock return");

        Fixture unsupported = new Fixture(4, false);
        unsupported.Prepare();
        Check(unsupported.Queue("start", 4), "candidate without witness may be prepared");
        bool sent = false;
        Check(!unsupported.Model.BeginQueuedForTest(unsupported.Read(), delegate { sent = true; }) &&
            !sent && !unsupported.Model.MutationAttempted && unsupported.Model.Reason == "divinity_synchronous_witness_unavailable",
            "missing synchronous natural witness makes zero 298 calls");
    }

    private static void TestStopAndMissedDispatch()
    {
        Fixture stopped = new Fixture(0);
        stopped.Commit();
        Stock.Slot stoppedHandler = stopped.Net.Handlers[M.FormationOpcode];
        Message accepted = stopped.QueueFormation();
        stopped.Capture();
        stopped.Model.Stop();
        Check(stopped.Model.Unresolved && stopped.Model.Busy, "stop after 298 retains unresolved ownership");
        stopped.Net.Dispatch(accepted);
        stopped.Capture();
        Check(stopped.Net.Handlers.ContainsKey(M.BattleOpcode) && stoppedHandler.Calls == 1 &&
            ReferenceEquals(stoppedHandler.Callback, stoppedHandler.Original) && !stopped.Witness.Armed,
            "stop restores the original and leaves its 1201 send intact");
        stopped.ManualReturn();
        Check(stopped.Model.Status == "UNKNOWN" && stopped.Model.Busy, "manual return cannot erase an uncertain commit");

        Fixture chained = new Fixture(4);
        chained.Commit();
        Message request = chained.QueueFormation();
        chained.Net.Dispatch(request);
        Stock.Slot immediate = chained.Net.Handlers[M.BattleOpcode];
        Check(!ReferenceEquals(immediate.Callback, immediate.Original), "298 binds 1201 before returning");
        Message start = chained.Net.Add(M.BattleOpcode, BattleStart(4, 0), delegate
        {
            chained.Battling = chained.WaitEnd = chained.UseEnd = chained.BattleVisible = true; chained.Cooldown = 90;
        });
        chained.Net.Dispatch(start);
        chained.Capture();
        Check(chained.Model.Status == "PENDING" && chained.Model.Reason == "divinity_waiting_natural_1032" &&
            chained.Witness.Scope.Battle != null && immediate.Calls == 1, "same-frame stock callbacks need no intermediate sample");

        Fixture replaced = new Fixture(4);
        replaced.Commit();
        Stock.Slot owned = replaced.Net.Handlers[M.FormationOpcode];
        Message originalReceipt = replaced.QueueFormation();
        replaced.Net.Handlers.Remove(M.FormationOpcode);
        Stock.Slot foreign = replaced.Net.Register(M.FormationOpcode);
        replaced.Net.Dispatch(originalReceipt);
        replaced.Capture();
        Check(!originalReceipt.Stream.CanRead && owned.Calls == 0 && foreign.Calls == 1 &&
            ReferenceEquals(foreign.Callback, foreign.Original) && replaced.Model.Status == "UNKNOWN",
            "a replacement handler may close the stream but cannot establish stock callback return");

        Fixture unrelated = new Fixture(4);
        unrelated.Commit();
        Stock.Slot priorBattle = unrelated.Net.Register(M.BattleOpcode);
        Message formationOnly = unrelated.Net.Add(M.FormationOpcode, new byte[] { 8, 1 }, null);
        unrelated.Net.Dispatch(formationOnly); unrelated.Capture();
        Check(unrelated.Model.Status == "UNKNOWN" && ReferenceEquals(priorBattle.Callback, priorBattle.Original),
            "298 cannot borrow a 1201 registration that already existed before the original callback");

        Fixture rejected = new Fixture(4);
        rejected.Commit(); rejected.CompleteFormation(); rejected.CompleteBattleStart(3);
        Check(rejected.Model.Status == "UNKNOWN" && rejected.Model.BattleWon == null, "1201 rejected after 298 is unresolved");

        Fixture missingBattle = new Fixture(4);
        missingBattle.Commit();
        Message confirmed = missingBattle.Net.Add(M.FormationOpcode, new byte[] { 8, 1 }, null);
        missingBattle.Capture(); missingBattle.Net.Dispatch(confirmed); missingBattle.Capture();
        Check(missingBattle.Model.Status == "UNKNOWN" && missingBattle.Model.Busy, "accepted 298 without chained 1201 is unresolved");

        Fixture expired = new Fixture(4);
        expired.Commit(); expired.Tick += 25000; expired.Capture();
        Check(expired.Model.Status == "UNKNOWN" && expired.Model.Busy, "a pending 298 timeout preserves ownership");
    }

    private static Fixture Playing(int target)
    {
        Fixture fixture = new Fixture(target);
        fixture.Commit(); fixture.CompleteFormation(); fixture.CompleteBattleStart(0);
        return fixture;
    }

    private static void TestResultAndProgress()
    {
        Fixture type0 = Playing(4);
        type0.CompleteEnd(0, 4, true);
        Check(type0.Model.Status == "UNKNOWN" && type0.Model.BattleWon == null, "type0 is not a divinity settlement");

        Fixture wrongTarget = Playing(4);
        wrongTarget.CompleteEnd(5, 0, true);
        Check(wrongTarget.Model.Status == "UNKNOWN" && wrongTarget.Model.BattleWon == null, "1032 target must match");

        Fixture skipped = Playing(4);
        skipped.CompleteEnd(5, 4, true, false);
        Check(skipped.Model.Status == "UNKNOWN" && skipped.Model.BattleWon == null,
            "manual result/skip can share flags but has no m_bViewAll at registration");

        Fixture loss = Playing(4);
        loss.CompleteEnd(5, 4, false);
        Check(loss.Model.Status == "LOST" && loss.Model.BattleWon == false && !loss.Model.TaskConfirmed && loss.Model.Busy,
            "verified loss keeps manual result ownership");
        loss.ManualReturn();
        Check(!loss.Model.Busy, "known loss releases after exact clean home");

        Fixture prerequisite = Playing(0);
        prerequisite.CompleteEnd(5, 0, true);
        Check(prerequisite.Model.Status == "PREREQUISITE_WON" && prerequisite.Model.BattleWon == true &&
            !prerequisite.Model.TaskConfirmed && prerequisite.Model.Busy, "first troop win is not task8");
        Check(!prerequisite.Queue("open", 4), "result hold blocks another fight");
        prerequisite.ManualReturn();
        string why;
        Check(!prerequisite.Model.Queue(M.TaskId, prerequisite.Role, "10000000000000000000000000000008",
            10, prerequisite.Model.Revision, "open", 0, out why) && why == "divinity_fresh_stock_target_required",
            "manual return alone cannot reissue against the stale defeated target");
        F next = prerequisite.Read();
        next.Target = 1; next.Selected = new object(); next.SelectedData = new object(); next.SelectedKilled = false;
        prerequisite.Model.Observe(next);
        Check(prerequisite.Model.CandidateTarget == 1 && prerequisite.Model.ActivityCount == 0 &&
            prerequisite.Model.Queue(M.TaskId, prerequisite.Role, "10000000000000000000000000000008",
                11, prerequisite.Model.Revision, "open", 1, out why), "fresh first surviving target and task count permit a new command");
        prerequisite.Model.NotInvoked("fixture_next_command_cancelled");
        F stale = prerequisite.Read();
        stale.KilledMask = 0; stale.SelectedKilled = false;
        prerequisite.Model.Observe(stale);
        Check(prerequisite.Model.Status == "BLOCKED" && !prerequisite.Model.Queue(M.TaskId, prerequisite.Role,
            "20000000000000000000000000000008", 12, prerequisite.Model.Revision, "open", 0, out why) &&
            why == "divinity_fresh_stock_target_required", "NotInvoked cannot erase a previously verified defeat");

        Fixture endFirst = Playing(4);
        endFirst.CompleteEnd(5, 4, true);
        Check(endFirst.Model.Status == "PENDING" && endFirst.Model.BattleWon == true && !endFirst.Model.TaskConfirmed,
            "reported and applied win is separate from activity");
        endFirst.Push(9, 1, 6);
        Check(!endFirst.Model.TaskConfirmed, "355 for another task is irrelevant");
        endFirst.ManualReturn();
        Check(endFirst.Model.Status == "PENDING" && endFirst.Model.BattleWon == true && endFirst.Model.Busy,
            "manual return before 355 preserves verified win and blocks another battle");
        endFirst.Push(8, 1, 7);
        Check(endFirst.Model.Status == "SUCCEEDED" && endFirst.Model.TaskConfirmed && !endFirst.Model.Busy,
            "late same-cycle 355 and both caches verify after manual return");

        Fixture activityFirst = Playing(4);
        activityFirst.Push(8, 1, 7);
        Check(activityFirst.Model.Status == "PENDING" && activityFirst.Model.BattleWon == null && !activityFirst.Model.TaskConfirmed,
            "355 before 1032 does not by itself prove the task");
        activityFirst.CompleteEnd(5, 4, true);
        Check(activityFirst.Model.Status == "SUCCEEDED" && activityFirst.Model.Busy,
            "355 before 1032 can corroborate the exact target and preserve the result UI hold");
        activityFirst.ManualReturn();
        Check(!activityFirst.Model.Busy, "known win releases only after verified manual return");

        Fixture cacheMismatch = Playing(4);
        cacheMismatch.CompleteEnd(5, 4, true);
        cacheMismatch.CacheMismatch = true;
        cacheMismatch.Push(8, 1, 7);
        Check(cacheMismatch.Model.Status == "UNKNOWN" && cacheMismatch.Model.BattleWon == true && !cacheMismatch.Model.TaskConfirmed,
            "both category2 caches are necessary and known win survives uncertainty");

        Fixture simultaneousMismatch = Playing(4);
        simultaneousMismatch.CacheMismatch = true;
        simultaneousMismatch.CompleteEnd(5, 4, true);
        Check(simultaneousMismatch.Model.Status == "UNKNOWN" && simultaneousMismatch.Model.BattleWon == true &&
            !simultaneousMismatch.Model.TaskConfirmed, "activity mismatch in the same sample cannot erase independent battle proof");
    }

    private static void TestStockReturnAndOwnership()
    {
        Fixture dropped = Playing(4);
        Message response = dropped.QueueEnd(5, 4, true);
        dropped.Net.Handlers.Remove(M.EndOpcode);
        dropped.Net.Dispatch(response);
        dropped.Capture();
        Check(response.Stream.CanRead && dropped.Model.BattleWon == null, "queue disappearance without stock callback return is insufficient");
        dropped.Tick += 300000; dropped.Capture();
        Check(dropped.Model.Status == "UNKNOWN", "missing natural callback expires unknown");

        Fixture thrown = new Fixture(4);
        thrown.Commit();
        Message message = thrown.Net.Add(M.FormationOpcode, new byte[] { 8, 1 }, delegate { throw new InvalidOperationException("stock"); });
        thrown.Capture();
        try { thrown.Net.Dispatch(message); throw new Exception("stock throw was lost"); }
        catch (InvalidOperationException) { }
        thrown.Capture();
        Check(message.Stream.CanRead && thrown.Model.Status == "UNKNOWN", "stock throw propagates and cannot qualify evidence");

        Fixture nullReply = new Fixture(4);
        nullReply.Commit();
        bool forwardedNull = false;
        Message timeout = nullReply.Net.Add(M.FormationOpcode, null, delegate(MemoryStream stream) { forwardedNull = stream == null; });
        nullReply.Net.Dispatch(timeout); nullReply.Capture();
        Check(forwardedNull && nullReply.Model.Status == "UNKNOWN", "null response is forwarded and cannot qualify evidence");

        Fixture role = Playing(4);
        role.Role++; role.Capture();
        Check(role.Model.Status == "UNKNOWN" && role.Model.Busy, "role change after commit");
        Fixture cycle = Playing(4);
        cycle.Cycle += 86400; cycle.Next += 86400; cycle.Now += 86400; cycle.Capture();
        Check(cycle.Model.Status == "UNKNOWN" && cycle.Model.Busy, "05:00 boundary after commit");
        Fixture socket = Playing(4);
        socket.Socket = new object(); socket.Capture();
        Check(socket.Model.Status == "UNKNOWN" && socket.Model.Busy, "socket replacement after commit");

        Fixture oldCycleProgress = Playing(4);
        oldCycleProgress.CompleteEnd(5, 4, true); oldCycleProgress.ManualReturn();
        oldCycleProgress.Cycle += 86400; oldCycleProgress.Now += 86400; oldCycleProgress.Next += 86400;
        oldCycleProgress.Push(8, 1, 7);
        Check(oldCycleProgress.Model.Status == "UNKNOWN" && oldCycleProgress.Model.BattleWon == true &&
            !oldCycleProgress.Model.TaskConfirmed, "late 355 in another cycle cannot erase a win or confirm the old task");
    }

    private static void StopWhileLocked(Fixture fixture, object monitor, DevelopmentPublication whileReleasing)
    {
        using (ManualResetEvent entered = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            Exception failure = null;
            Thread worker = new Thread(delegate()
            {
                try
                {
                    lock (monitor)
                    {
                        entered.Set();
                        Check(release.WaitOne(5000, false), "fixture releases the held monitor");
                        if (whileReleasing != null) whileReleasing();
                    }
                }
                catch (Exception error) { failure = error; }
            });
            worker.IsBackground = true;
            worker.Start();
            try
            {
                Check(entered.WaitOne(5000, false), "fixture monitor held by another thread");
                fixture.Model.Stop();
                Check(fixture.Model.CleanupPending && fixture.Model.Busy && fixture.Model.Unresolved &&
                    !fixture.Model.RetryCleanup(), "contention retains teardown ownership");
            }
            finally
            {
                release.Set();
                Check(worker.Join(5000) && failure == null, "fixture worker exits cleanly");
            }
        }
    }

    private static void TestContendedTeardown()
    {
        Fixture owned = new Fixture(4);
        owned.Commit();
        Stock.Slot activity = owned.Net.Handlers[M.ActivityOpcode];
        Stock.Slot formation = owned.Net.Handlers[M.FormationOpcode];
        StopWhileLocked(owned, owned.Net, null);
        Check(!ReferenceEquals(activity.Callback, activity.Original) && activity.Calls == 0,
            "cleanup remains pending with the exact owned 355 wrapper installed and no response");
        owned.Model.Capture(null);
        Check(!owned.Model.CleanupPending && owned.Model.Status == "UNKNOWN" &&
            ReferenceEquals(activity.Callback, activity.Original) && ReferenceEquals(formation.Callback, formation.Original),
            "Update restores exact delegates without needing a current context or 355 response");
        Message ordinary = owned.Net.Add(M.ActivityOpcode, new byte[] { 8, 1 }, null);
        owned.Net.Dispatch(ordinary);
        Check(activity.Calls == 1, "restored stock callback runs once");

        Fixture replaced = new Fixture(4);
        replaced.Commit();
        Stock.Slot obsolete = replaced.Net.Handlers[M.ActivityOpcode];
        DevelopmentNormalCallback obsoleteWrapper = obsolete.Callback;
        Stock.Slot foreign = null;
        StopWhileLocked(replaced, replaced.Net, delegate
        {
            replaced.Net.Handlers.Remove(M.ActivityOpcode);
            foreign = replaced.Net.Register(M.ActivityOpcode);
        });
        Check(replaced.Model.RetryCleanup() && !replaced.Model.CleanupPending &&
            ReferenceEquals(replaced.Net.Handlers[M.ActivityOpcode], foreign) && ReferenceEquals(foreign.Callback, foreign.Original) &&
            ReferenceEquals(obsolete.Callback, obsoleteWrapper), "cleanup proves replacement without touching either foreign or obsolete slot");

        Fixture removed = new Fixture(4);
        removed.Commit();
        StopWhileLocked(removed, removed.Net, delegate { removed.Net.Handlers.Remove(M.ActivityOpcode); });
        Check(removed.Model.RetryCleanup() && !removed.Model.CleanupPending && !removed.Net.Handlers.ContainsKey(M.ActivityOpcode),
            "cleanup proves removal without re-registering the stock callback");

        Fixture changed = new Fixture(4);
        changed.Commit();
        Stock.Slot changedSlot = changed.Net.Handlers[M.ActivityOpcode];
        DevelopmentNormalCallback foreignDelegate = delegate { };
        StopWhileLocked(changed, changed.Net, delegate { changedSlot.Callback = foreignDelegate; });
        Check(changed.Model.RetryCleanup() && !changed.Model.CleanupPending && ReferenceEquals(changedSlot.Callback, foreignDelegate),
            "cleanup preserves a foreign delegate in the same slot");
    }

    private static void TestTeardownDuringCallback()
    {
        Fixture running = new Fixture(4);
        running.Commit();
        Stock.Slot slot = running.Net.Handlers[M.ActivityOpcode];
        DevelopmentNormalCallback wrapper = slot.Callback;
        Message message = running.Net.Add(M.ActivityOpcode, new byte[] { 10, 6, 8, 9, 16, 1, 24, 6 }, delegate(MemoryStream stream)
        {
            Check(stream.Position == 0, "in-flight stock callback gets its unchanged stream");
            running.Model.Stop();
            Check(running.Model.CleanupPending && running.Model.Busy && !running.Model.RetryCleanup() &&
                ReferenceEquals(slot.Callback, wrapper), "reentrant Stop holds cleanup until stock callback returns");
        });
        running.Net.Dispatch(message);
        Check(running.Model.CleanupPending && slot.Calls == 1, "the in-flight original returned exactly once");
        Check(running.Model.RetryCleanup() && !running.Model.CleanupPending && ReferenceEquals(slot.Callback, slot.Original),
            "teardown retry restores exact delegate after in-flight stock callback");
    }

    private static void TestNaturalProviderTeardown()
    {
        Fixture won = Playing(0);
        NaturalWitness owned = won.Witness;
        owned.RefuseRestore = true;
        won.CompleteEnd(5, 0, true);
        won.ManualReturn();
        Check(won.Model.Status == "PREREQUISITE_WON" && won.Model.CleanupPending && won.Model.Busy && won.Model.Unresolved &&
            !owned.Armed && ReferenceEquals(owned.Installed, owned.Owned),
            "unverified provider cleanup alone prevents handoff after a known result and manual return");
        owned.RefuseRestore = false;
        owned.InFlight = true;
        Check(!won.Model.RetryCleanup() && won.Model.CleanupPending, "in-flight provider callback retains ownership");
        owned.InFlight = false;
        won.Model.Capture(null);
        Check(!won.Model.CleanupPending && !won.Model.Busy && ReferenceEquals(owned.Installed, owned.Original),
            "Update retries the retained provider and releases a clean handoff");

        Fixture throwing = new Fixture(4);
        throwing.Commit();
        NaturalWitness failed = throwing.Witness;
        failed.ThrowOnRestore = true;
        throwing.Model.Stop();
        Check(throwing.Model.CleanupPending && !throwing.Model.RetryCleanup() && failed.RestoreCalls == 2 &&
            ReferenceEquals(failed.Installed, failed.Owned), "throwing restoration keeps the same provider for retry");
        failed.ThrowOnRestore = false;
        Check(throwing.Model.RetryCleanup() && !throwing.Model.CleanupPending &&
            ReferenceEquals(failed.Installed, failed.Original), "provider restores after its transient failure clears");

        Fixture contended = new Fixture(4);
        contended.Commit();
        NaturalWitness replaced = contended.Witness;
        Stock.Slot activity = contended.Net.Handlers[M.ActivityOpcode];
        object foreign = new object();
        StopWhileLocked(contended, replaced.RestoreLock, delegate { replaced.Installed = foreign; });
        Check(ReferenceEquals(activity.Callback, activity.Original) && contended.Model.CleanupPending,
            "provider contention keeps recovery pending after the fixed taps have been restored");
        Check(contended.Model.RetryCleanup() && !contended.Model.CleanupPending && ReferenceEquals(replaced.Installed, foreign),
            "provider proves exact independent replacement and preserves the foreign registration");
    }

    private static void TestWire()
    {
        Check(DailyDivinityWire.Decode(M.BattleOpcode, BattleStart(0, 0)).Target == 0, "proto default runtime troop zero");
        Check(DailyDivinityWire.Decode(M.EndOpcode, End(5, 4, true, UInt64.MaxValue)).ReportId == UInt64.MaxValue,
            "full unsigned report id");
        try
        {
            DailyDivinityWire.Decode(M.EndOpcode, new byte[] { 8, 0, 16, 5, 16, 0 });
            throw new Exception("duplicate type field accepted");
        }
        catch (FormatException) { }
    }

    private static void Check(bool condition, string test)
    { if (!condition) throw new Exception(test); }

    internal static int Main()
    {
        TestWire();
        TestAdmission();
        TestStopAndMissedDispatch();
        TestResultAndProgress();
        TestStockReturnAndOwnership();
        TestContendedTeardown();
        TestTeardownDuringCallback();
        TestNaturalProviderTeardown();
        Console.WriteLine("DailyDivinityBattleTests passed");
        return 0;
    }
}
