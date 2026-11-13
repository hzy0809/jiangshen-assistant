using System;
using System.Collections;
using System.IO;
using JiangShen.AssistantRuntime;
using Frame = JiangShen.AssistantRuntime.DailyWoodModel.Frame;

internal static partial class DailyWoodTests
{
    private const ushort Info = DailyWoodModel.Info, Fast = DailyWoodModel.Fast, Push = DailyWoodModel.Activity;
    private const string Id = "1234567890abcdef1234567890abcdef";
    private static int assertions;

    private sealed class StockPanel
    {
        private void _fast_finish_close() { }
        internal DevelopmentPublication OwnedClose() { return _fast_finish_close; }
        internal void ForeignClose() { }
    }

    private sealed class Slot
    {
        internal DevelopmentNormalCallback handler;
        internal readonly DevelopmentNormalCallback Original;
        internal Slot(DevelopmentNormalCallback callback) { Original = handler = callback; }
    }

    private sealed class Fixture
    {
        internal readonly DailyWoodModel Model = new DailyWoodModel();
        internal readonly Hashtable Slots = new Hashtable(), Registrations = new Hashtable(), Calls = new Hashtable();
        internal readonly Frame Home;
        internal Frame Current;
        internal Frame ReplyState;
        internal DevelopmentPublication OriginalHook;
        internal int OpenCalls, FastCalls, CloseResultCalls, ClosePanelCalls;
        internal int Sequence;
        internal string Why;

        internal Fixture()
        {
            NativeBattleStartStore.TestRoot = Path.Combine(Path.GetTempPath(), "wood-durable-test-" + Guid.NewGuid().ToString("N"));
            Slot push = Register(Push);
            Home = new Frame
            {
                Character = new object(), Client = new object(), Root = new object(), GlobalRoot = new object(), Loader = new object(),
                Main = new object(), Panel = new StockPanel(), Result = new object(), Battle = new object(), PushHandler = push,
                Rows = new object(), CategoryCache = new object(), CategoryRows = new object(), Row = new object(),
                ListedRow = new object(), TaskTemplate = new object(), StreamIdentity = new object(), Role = 14, Sid = 7,
                Cycle = 10u * 86400 + 18000, Epoch = 9u * 86400 + 72000, Now = 10u * 86400 + 36000,
                Next = 11u * 86400 + 18000, Gold = 40, BoundGold = 8, GoldConsumed = 3,
                Valid = true, TaskValid = true, Idle = true, Home = true, PanelHidden = true, ResultHidden = true
            };
            Current = Home.Copy();
            Observe();
        }

        internal Slot Register(ushort opcode)
        {
            Slot slot = new Slot(delegate(MemoryStream stream, object arg)
            {
                Calls[opcode] = Called(opcode) + 1;
                if (OriginalHook != null) OriginalHook();
                if (ReplyState != null) { Current = ReplyState; ReplyState = null; }
            });
            Registrations[opcode] = Slots[opcode] = slot;
            return slot;
        }

        internal int Called(ushort opcode) { return (int)(Calls[opcode] ?? 0); }

        internal bool Restored(ushort opcode)
        {
            Slot slot = Registrations[opcode] as Slot;
            return slot != null && ReferenceEquals(slot.handler, slot.Original);
        }

        internal void Observe()
        {
            Current.Tick = Environment.TickCount;
            Model.Observe(Current.Copy());
        }

        internal bool Queue(string action, string operation = Id)
        { return Model.Queue(22, 14, operation, ++Sequence, Model.Revision, action, out Why); }

        internal bool Execute(string action, DailyWoodAdmission admit = null, DailyWoodModel.StockGate live = null)
        {
            return Model.ExecuteForTest(delegate
            {
                Current.Tick = Environment.TickCount;
                return Current.Copy();
            }, delegate
            {
                Frame after = Current.Copy();
                if (action == "open")
                {
                    OpenCalls++;
                    after.Home = after.Idle = after.PanelReady = false;
                    after.Loading = true; after.Sub = after.Panel;
                    after.InfoHandler = Register(Info);
                }
                else if (action == "fast")
                {
                    FastCalls++;
                    after.Idle = after.PanelReady = false;
                    after.FastHandler = Register(Fast);
                }
                else if (action == "close_result")
                { CloseResultCalls++; after.ResultReady = false; after.ResultHidden = true; }
                else if (action == "close_panel")
                {
                    ClosePanelCalls++;
                    after.Home = after.PanelHidden = true; after.PanelReady = false; after.Sub = null;
                }
                Current = after;
            }, live ?? delegate { return true; }, delegate(Frame f, ushort op, DailyWoodModel.TestReply callback)
            {
                Slot slot = Slots[op] as Slot;
                return DailyWoodTap.Attach(f.Client, Slots, op, slot, slot.Original,
                    delegate(byte[] body) { callback(body, Current.Copy()); });
            }, admit ?? delegate(DailyWoodAdmissionCheck prepare, DailyWoodAdmissionCheck check) { return prepare() && check(); },
                out Why);
        }

        internal void Open()
        { Check(Queue("open") && Execute("open") && OpenCalls == 1, Why); }

        internal void FreshInfo(uint quota = 1, bool canTake = false)
        {
            Dispatch(Info, InfoPacket(quota, canTake), NewInfo(quota, canTake));
            Observe();
        }

        internal Frame NewInfo(uint quota, bool canTake = false)
        {
            Frame after = Current.Copy();
            after.Player = Player(quota, canTake);
            after.StatusCache = new object(); after.PlayerCache = new object(); after.FinishTimestamp = 9;
            after.PanelShownAt = new DateTime(2019, 1, 1);
            after.Idle = after.PanelReady = true; after.PanelHidden = after.Loading = false;
            return after;
        }

        internal void Start()
        {
            Open(); FreshInfo();
            Check(Model.Status == "READY" && Restored(Info), "612 result and restoration");
            Check(Queue("fast") && Execute("fast") && FastCalls == 1, Why);
        }

        internal void Dispatch(ushort opcode, byte[] bytes, Frame after, bool remove = true)
        {
            Slot slot = (Slot)Slots[opcode];
            if (opcode != Push && remove) Slots.Remove(opcode);
            after.Tick = Environment.TickCount;
            after.InfoHandler = Slots[Info]; after.FastHandler = Slots[Fast];
            ReplyState = after;
            lock (Current.Client)
                slot.handler(bytes == null ? null : new MemoryStream(bytes, false), null);
        }

        internal Frame NewFast(uint quota, bool finished = false)
        {
            Frame after = Current.Copy();
            after.Player = Player(quota, true);
            after.StatusCache = new object(); after.PlayerCache = new object();
            after.ResultPlayer = Player(0, false, 0); after.ResultCache = new object();
            after.ResultClose = ((StockPanel)Current.Panel).OwnedClose();
            after.ResultFinished = finished;
            after.ResultShownAt = new DateTime(2019, 1, 2);
            after.Idle = after.ResultReady = after.PanelReady = true; after.ResultHidden = false;
            return after;
        }

        internal void FastOk(bool finished = false)
        { Dispatch(Fast, FastPacket(1, finished), NewFast(0, finished)); Observe(); }

        internal void Activity(bool mismatch = false)
        {
            Frame after = Current.Copy();
            after.Row = after.ListedRow = new object();
            after.Count = 1; after.Listed = mismatch ? 0 : 1;
            after.Refresh = after.ListedRefresh = 17;
            Dispatch(Push, Bytes(10, 6, 8, 22, 16, 1, 24, 34), after);
            Observe();
        }

        internal void ActivityRow(int count, int refresh, int listedRefresh)
        {
            Frame after = Current.Copy();
            after.Row = after.ListedRow = new object();
            after.Count = after.Listed = count;
            after.Refresh = refresh; after.ListedRefresh = listedRefresh;
            byte[] body = count == 0 && refresh == 0 ? Bytes(10, 2, 8, 22) :
                Bytes(10, 6, 8, 22, 16, (byte)count, 24, (byte)(2 * refresh));
            Dispatch(Push, body, after);
            Observe();
        }
    }

    private static void Check(bool condition, string what)
    {
        assertions++;
        if (condition) return;
        Console.Error.WriteLine(what);
        throw new Exception(what);
    }

    private static byte[] Bytes(params byte[] values) { return values; }
    private static byte[] With(byte[] first, byte[] second)
    {
        byte[] all = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, all, 0, first.Length);
        Buffer.BlockCopy(second, 0, all, first.Length, second.Length);
        return all;
    }
    private static byte[] Nested(byte tag, byte[] body) { return With(Bytes(tag, (byte)body.Length), body); }
    private static byte[] PlayerWire(uint quota, bool canTake, uint sid)
    { return Bytes(8, 1, 16, 2, 24, 3, 32, 4, 40, 5, 48, 6, 56, 7, 80, (byte)(canTake ? 1 : 0), 88, (byte)quota, 96, (byte)sid); }
    private static DailyWoodPlayer Player(uint quota, bool canTake, uint sid = 7)
    { return new DailyWoodPlayer { Rank = 1, Level = 2, Wave = 3, Enemy = 4, Damage = 5, Next = 6, RoleLevel = 7, Sid = sid, Quota = quota, CanTake = canTake }; }
    internal static byte[] InfoPacket(uint quota, bool canTake = false, uint sid = 7)
    { return With(Nested(10, Nested(10, PlayerWire(quota, canTake, sid))), Bytes(16, 9)); }
    internal static byte[] FastPacket(byte state, bool finished = false)
    {
        byte[] head = Bytes(32, state);
        if (state == 2) return head;
        head = With(head, Nested(10, Nested(10, PlayerWire(state == 1 ? 0u : 1u, true, 7))));
        if (state != 1) return head;
        return With(With(head, Nested(18, PlayerWire(0, false, 0))), Bytes(24, (byte)(finished ? 1 : 0)));
    }

    private static void Happy(bool pushFirst)
    {
        Fixture f = new Fixture(); f.Start();
        if (pushFirst) f.Activity(); else f.FastOk();
        Check(f.Model.Status == "PENDING" && !f.Model.TaskConfirmed, "requires both callbacks");
        if (pushFirst) f.FastOk(); else f.Activity();
        Check(f.Model.Status == "RESULT" && f.Model.TaskConfirmed && f.Model.Busy, "result awaits owned close");
        Check(f.Restored(Fast) && f.Restored(Push), "stock delegates restored after valid evidence");
        Check(f.Queue("close_result") && f.Execute("close_result"), f.Why);
        f.Observe();
        Check(f.Model.Status == "CLOSING" && f.Model.Busy, "still owns main wood panel");
        Check(f.Queue("close_panel") && f.Execute("close_panel"), f.Why);
        f.Observe();
        Check(f.Model.Status == "COMPLETED" && f.Model.TaskConfirmed && !f.Model.Busy &&
            f.CloseResultCalls == 1 && f.ClosePanelCalls == 1 && f.FastCalls == 1, "return to stock home");
    }

    private static void InfoRefusals()
    {
        Check(DailyWoodPlayer.MatchesRole("role", 7, "role", 7) && !DailyWoodPlayer.MatchesRole("other", 7, "role", 7) &&
            !DailyWoodPlayer.MatchesRole("role", 0, "role", 0), "player requires exact realm and nonempty name");
        Fixture f = new Fixture(); f.Open();
        f.Dispatch(Info, null, f.Current.Copy(), false); f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Called(Info) == 1 && !f.Queue("fast") && f.FastCalls == 0 && f.Restored(Info), "612 null forwarded");

        f = new Fixture(); f.Open();
        Frame stale = f.NewInfo(1);
        stale.StatusCache = f.Home.StatusCache;
        f.Dispatch(Info, InfoPacket(1), stale); f.Observe();
        Check(f.Model.Status == "UNKNOWN" && !f.Queue("fast"), "612 with stale cache");

        f = new Fixture(); f.Open();
        f.Dispatch(Info, InfoPacket(1, false, 8), f.NewInfo(1)); f.Observe();
        Check(f.Model.Status == "UNKNOWN" && !f.Queue("fast"), "612 wrong player sid");

        foreach (bool canTake in new[] { false, true })
        {
            f = new Fixture(); f.Open(); f.FreshInfo(canTake ? 1u : 0u, canTake);
            Check(f.Model.Status == "BLOCKED" && f.Model.Busy && !f.Queue("fast"), "612 no free action");
            Check(f.Queue("close_panel") && f.Execute("close_panel"), f.Why); f.Observe();
            Check(f.Model.Status == "BLOCKED" && !f.Model.Busy && f.FastCalls == 0, "clean no-action outcome");
        }
    }

    private static void AdmissionRefusals()
    {
        Fixture f = new Fixture(); f.Open(); f.FreshInfo();
        Check(f.Queue("fast"), f.Why);
        Check(!f.Execute("fast", delegate(DailyWoodAdmissionCheck prepare, DailyWoodAdmissionCheck check)
        {
            Check(prepare(), "prepare must bind activity before command comparison");
            return false;
        }) && f.FastCalls == 0 && f.Restored(Push), "caller exact command changed");

        Check(f.Queue("fast"), f.Why);
        Check(!f.Execute("fast", delegate(DailyWoodAdmissionCheck prepare, DailyWoodAdmissionCheck check) { return prepare(); }) &&
            f.FastCalls == 0 && f.Restored(Push), "a caller that skips the final live check cannot invoke stock");

        Check(f.Queue("fast"), f.Why);
        Check(!f.Execute("fast", null, delegate { return false; }) && f.FastCalls == 0 && f.Restored(Push),
            "cheap live gate drift cannot invoke stock");

        Check(f.Queue("fast"), f.Why);
        f.Current.Epoch += 86400;
        Check(!f.Execute("fast") && f.FastCalls == 0, "20:00 rollover rejects queued fast action");
        f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Model.CleanupPending, "owned stale panel held");

        f = new Fixture(); f.Open(); f.FreshInfo();
        f.Current.Cycle += 86400; f.Current.Next += 86400; f.Observe();
        Check(f.Model.Status == "UNKNOWN" && !f.Queue("fast"), "05:00 rollover independent from quota");
        Check(DailyWoodModel.QuotaEpoch(200000 + 71999, 200000) == 185600 &&
            DailyWoodModel.QuotaEpoch(200000 + 72000, 200000) == 272000 &&
            DailyWoodModel.QuotaEpoch(286400, 286400) == 272000, "quota epoch at 20:00 and midnight");

        f = new Fixture(); f.Open(); f.FreshInfo();
        Check(f.Queue("fast"), f.Why);
        Check(!f.Execute("fast", delegate(DailyWoodAdmissionCheck prepare, DailyWoodAdmissionCheck check)
        {
            Check(prepare(), "fast observer bound before Stop");
            f.Model.Stop();
            return check();
        }) && f.FastCalls == 0 && f.Restored(Push) && f.Model.CleanupPending, "Stop between prepare and commit");
    }

    private static void FastRefusals()
    {
        Fixture f = new Fixture(); f.Start();
        f.Dispatch(Fast, FastPacket(3), f.NewInfo(1, true)); f.Observe();
        Check(f.Model.Status == "BLOCKED" && f.Model.Reason == "wood_reward_already_pending" &&
            !f.Model.TaskConfirmed && f.Restored(Push), "616 REWARD not completed");
        Check(f.Queue("close_panel") && f.Execute("close_panel"), f.Why); f.Observe();
        Check(!f.Queue("open", "22222222222222222222222222222222"), "cannot retry 616 this cycle");

        f = new Fixture(); f.Start();
        f.Dispatch(Fast, FastPacket(2), f.Current.Copy()); f.Observe();
        Check(f.Model.Status == "BLOCKED" && !f.Model.TaskConfirmed && f.Restored(Fast), "616 FAILED");

        f = new Fixture(); f.Start();
        Frame unchangedQuota = f.NewFast(1);
        f.Dispatch(Fast, FastPacket(1), unchangedQuota); f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.FastCalls == 1, "616 OK without quota decrease");

        foreach (int currency in new[] { 0, 1, 2 })
        {
            f = new Fixture(); f.Start();
            Frame paid = f.NewFast(0);
            if (currency == 0) paid.Gold--; else if (currency == 1) paid.BoundGold--; else paid.GoldConsumed++;
            f.Dispatch(Fast, FastPacket(1), paid); f.Observe();
            Check(f.Model.Status == "UNKNOWN" && !f.Model.TaskConfirmed, "616 premium currency changed");
        }

        f = new Fixture(); f.Start(); f.FastOk(); f.Activity(true);
        Check(f.Model.Status == "UNKNOWN" && !f.Model.TaskConfirmed, "355 and category cache disagree");

        f = new Fixture(); f.Start(); f.FastOk(); f.Activity();
        f.Current.ResultClose = (DevelopmentPublication)((StockPanel)f.Current.Panel).ForeignClose;
        f.Observe();
        Check(f.Model.Status == "UNKNOWN" && !f.Queue("close_result") && f.CloseResultCalls == 0, "preserve replaced close delegate");

        f = new Fixture(); f.Start(); f.FastOk(true); f.Activity();
        Check(f.Model.Status == "UNKNOWN" && f.Model.TaskConfirmed && f.Model.CleanupPending &&
            f.Model.Reason == "wood_result_prompt_requires_user" && !f.Queue("close_result"), "preserve promotion/no-target result");
    }

    private static void NoOpActivity()
    {
        foreach (bool beforeFastReply in new[] { true, false })
        {
            Fixture f = new Fixture(); f.Start();
            if (!beforeFastReply) f.FastOk();
            f.ActivityRow(0, 0, 0);
            Check(f.Model.Status == "PENDING" && !f.Model.TaskConfirmed && f.Called(Push) == 1,
                "coherent 355 with unchanged defaults stays pending");
            if (beforeFastReply) f.FastOk();
            Check(f.Model.Status == "PENDING" && !f.Model.TaskConfirmed, "no-op never satisfies the activity witness");
            f.Activity();
            Check(f.Model.Status == "RESULT" && f.Model.TaskConfirmed && f.Called(Push) == 2 && f.Restored(Push),
                "first true increment satisfies the witness after a no-op");
        }

        Fixture repeated = new Fixture(); repeated.Start();
        repeated.Activity();
        repeated.ActivityRow(1, 17, 17);
        Check(repeated.Model.Status == "PENDING" && !repeated.Model.TaskConfirmed, "identical repeat after +1 still waits for 616");
        repeated.FastOk();
        Check(repeated.Model.Status == "RESULT" && repeated.Model.TaskConfirmed, "identical repeat preserves the original +1 witness");

        Fixture fBad = new Fixture(); fBad.Start();
        fBad.ActivityRow(0, 0, 1);
        Check(fBad.Model.Status == "UNKNOWN" && !fBad.Model.TaskConfirmed, "no-op with mismatched cache refresh blocks");

        fBad = new Fixture(); fBad.Start();
        fBad.ActivityRow(0, 1, 1);
        Check(fBad.Model.Status == "UNKNOWN" && !fBad.Model.TaskConfirmed, "changed refresh alone is no-op conflict");

        fBad = new Fixture(); fBad.Start();
        fBad.ActivityRow(2, 17, 17);
        Check(fBad.Model.Status == "UNKNOWN" && !fBad.Model.TaskConfirmed, "unexpected +2 blocks even with coherent caches");

        fBad = new Fixture(); fBad.Start();
        fBad.Activity(); fBad.ActivityRow(0, 17, 17);
        Check(fBad.Model.Status == "UNKNOWN" && !fBad.Model.TaskConfirmed, "regression after +1 blocks");
    }

    private static Fixture ConfirmedResult()
    {
        Fixture f = new Fixture(); f.Start(); f.FastOk(); f.Activity();
        Check(f.Model.Status == "RESULT" && f.Model.TaskConfirmed && f.Restored(Push), "sealed increment with stock push restored");
        return f;
    }

    private static void CleanupActivity()
    {
        Fixture f = ConfirmedResult();
        int revision = f.Model.Revision;
        object oldRow = f.Current.Row;
        f.ActivityRow(1, 17, 17);
        Check(f.Model.Status == "RESULT" && f.Model.TaskConfirmed && f.Model.Revision == revision &&
            !ReferenceEquals(f.Current.Row, oldRow), "stock no-op at RESULT preserves verified result and revision");
        Check(f.Queue("close_result"), f.Why);
        f.ActivityRow(1, 17, 17);
        Check(f.Execute("close_result"), "same stock values remain valid between result-close queue and commit");
        f.Observe();
        f.ActivityRow(1, 17, 17);
        Check(f.Model.Status == "CLOSING" && f.Model.TaskConfirmed, "stock no-op between owned closes");
        Check(f.Queue("close_panel"), f.Why);
        f.ActivityRow(1, 17, 17);
        Check(f.Execute("close_panel"), "same stock values remain valid between panel-close queue and commit");
        f.ActivityRow(1, 17, 17);
        Check(f.Model.Status == "COMPLETED" && !f.Model.Busy && f.FastCalls == 1 &&
            f.CloseResultCalls == 1 && f.ClosePanelCalls == 1, "same stock values at final home");

        f = ConfirmedResult();
        f.ActivityRow(1, 17, 18);
        Check(f.Model.Status == "UNKNOWN" && f.Model.TaskConfirmed && !f.Queue("close_result"), "cache disagreement blocks result close");

        f = ConfirmedResult();
        f.Current.TaskTemplate = new object();
        f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Model.TaskConfirmed, "template replacement blocks result close");

        f = ConfirmedResult();
        f.Current.Role++;
        f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Model.TaskConfirmed, "role switch blocks result close");

        f = ConfirmedResult();
        f.Current.Cycle += 86400; f.Current.Next += 86400;
        f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Model.TaskConfirmed, "cycle rollover blocks result close");

        f = ConfirmedResult();
        f.Current.ListedRow = new object();
        f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Model.TaskConfirmed, "dual cache no longer shares the stock task row");

        f = ConfirmedResult();
        Check(f.Queue("close_result") && f.Execute("close_result"), f.Why);
        f.Observe();
        Check(f.Queue("close_panel") && f.Execute("close_panel"), f.Why);
        f.ActivityRow(1, 18, 18);
        Check(f.Model.Status == "UNKNOWN" && f.Model.TaskConfirmed, "refresh drift at home prevents final completion");
    }

    private static void LifecycleRefusals()
    {
        Fixture f = new Fixture(); f.Start();
        f.Model.Stop();
        Check(f.Model.Status == "UNKNOWN" && f.Model.CleanupPending && f.Restored(Push) && f.Restored(Fast), "Stop detaches observers but holds UI");
        f.Dispatch(Fast, FastPacket(1), f.NewFast(0)); f.Observe();
        Check(f.Called(Fast) == 1 && f.Model.Busy && !f.Model.TaskConfirmed && f.FastCalls == 1, "forward late stock callback without claiming");
        f.Current = f.Home.Copy(); f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Model.Unresolved && !f.Model.CleanupPending,
            "same-session stock home clears only cleanup hold");

        f = new Fixture(); f.Start();
        Frame late = f.Current.Copy();
        late.Tick = unchecked(Environment.TickCount + 26000);
        f.Model.Observe(late);
        Check(f.Model.Status == "UNKNOWN" && f.Model.Reason == "wood_settlement_timeout" && f.Restored(Fast), "timeout is terminal");

        f = new Fixture(); f.Start();
        f.Current.StreamIdentity = new object(); f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Model.CleanupPending && f.Restored(Push) && !f.Queue("fast"), "changed socket or stream");

        f = new Fixture(); f.Start();
        Slot replacement = f.Register(Push);
        f.Current.PushHandler = replacement; f.Observe();
        Check(f.Model.Status == "UNKNOWN" && ReferenceEquals(replacement.handler, replacement.Original), "never touch foreign registration");

        f = new Fixture(); f.Start();
        f.OriginalHook = delegate { f.Model.Stop(); Check(f.Model.CleanupPending && !f.Restored(Fast), "cannot detach in-flight"); };
        f.Dispatch(Fast, FastPacket(1), f.NewFast(0)); f.Observe();
        Check(f.Model.Status == "UNKNOWN" && f.Restored(Fast), "after stock callback returns restoration retries");
    }

    internal static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "durable") { DurablePaths(); Console.WriteLine("DailyWood durable passed " + assertions + " assertions."); return 0; }
        Happy(false); Happy(true); InfoRefusals(); AdmissionRefusals(); FastRefusals(); NoOpActivity(); CleanupActivity(); LifecycleRefusals();
        Console.WriteLine("DailyWood passed " + assertions + " assertions.");
        return 0;
    }
    public static int Main() { return Run(new string[0]); }
}
