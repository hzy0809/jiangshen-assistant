using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using ProtoBuf;
using JiangShen.AssistantRuntime;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

internal static partial class BattleLifecycleTests
{
    private const string Operation = "00000000000000000000000000000019";
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static object Field(object owner, string name)
    {
        FieldInfo field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) throw new Exception("fixture production field missing: " + name);
        return field.GetValue(owner);
    }
    private static void SetField(object owner, string name, object value)
    { owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(owner, value); }
    private static BattleLifecycle State(Fixture fixture)
    {
        object attempt = Field(fixture.Owner, "attempt");
        return attempt == null ? null : (BattleLifecycle)Field(attempt, "State");
    }
    private static void Throws(Action action, string fragment)
    {
        try { action(); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Check(error.Message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0,
                "expected '" + fragment + "' but received '" + error.Message + "'");
            return;
        }
        throw new Exception("expected failure containing " + fragment);
    }
    private static byte[] End(bool win, uint result) { return End(win, result, BattleMode.Campaign); }
    private static byte[] End(bool win, uint result, BattleMode mode)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            Serializer.Serialize(stream, new EndPacket { _result = result, _idcampaign = 12, _idtroop = 7,
                _type = mode == BattleMode.Trial ? 2U : mode == BattleMode.Grave ? 4U : mode == BattleMode.TrainingMonster ? 7U : 0U,
                _idbattle_report = result == 0 ? 987UL : 0UL, _iswin = win });
            return stream.ToArray();
        }
    }
    private static string Json(string value) { return JiangShen.AssistantRuntime.Command.Quote(value); }
    private static byte[] CampaignEnd(uint campaign, uint troop, uint mode)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            Serializer.Serialize(stream, new EndPacket { _result = 0, _type = mode, _idcampaign = campaign,
                _idtroop = troop, _iswin = true, _idbattle_report = 987, _star_level = 3 });
            return stream.ToArray();
        }
    }

    private sealed class Fixture
    {
        internal readonly BattleSettlement Owner;
        internal readonly RootMessenger Previous, BattleRoot;
        internal readonly BattleUI Battle;
        internal readonly __BaseUI Source;
        internal readonly game.net.tcp_client Client;
        internal readonly object Target, Receipt = new object();
        internal readonly object Report;
        internal readonly List<string> Events = new List<string>();
        internal readonly BattleMode Mode;
        internal bool Admission = true, ReceiptVerified = true;
        internal Fixture(BattleMode mode, uint reportStatus = 1)
        {
            Mode = mode;
            // Each fixture represents a fresh detached game process. Production
            // static ownership is explicitly retained in SameProcessOwnership.
            typeof(BattleSettlement).GetField("active", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
            BattleLaunch.RejectReport = false;
            BattleLaunch.Ticket = null;
            BattleFixtureTrace.Events.Clear(); UnityEngine.GameObject.Paths.Clear();
            Global.character_ = new Database.Character(); Global.relogin = false;
            Global.extern_enabled = true; game.net.tcp_base.connection_state = 3;
            Client = new game.net.tcp_client(); RootUI.m_client = Global.m_client = Client;
            Previous = new RootMessenger { LevelID = mode == BattleMode.PointRace ? "Pointrace" : "Campaign" };
            Previous.gameObject.name = Previous.LevelID;
            BattleRoot = new RootMessenger { LevelID = "Battle" }; BattleRoot.gameObject.name = "Battle";
            BattleRoot.gameObject.Attach(BattleRoot);
            UnityEngine.GameObject.Paths["/Battle"] = BattleRoot.gameObject;
            UnityEngine.GameObject.Paths["Battle"] = BattleRoot.gameObject;
            if (mode == BattleMode.Arena) Source = new ArenaUI();
            else if (mode == BattleMode.PointRace) Source = new PointraceUI();
            else if (mode == BattleMode.OwnDemon) Source = new DemonScript();
            else if (mode == BattleMode.Trial) Source = new TrialLevelUI();
            else if (mode == BattleMode.Grave) Source = new GraveLevelUI();
            else if (mode == BattleMode.TrainingMonster) Source = new SealMonsterUI();
            else Source = new CampaignUI();
            Target = mode == BattleMode.Campaign ? (object)new CampaignTroop() :
                mode == BattleMode.Trial || mode == BattleMode.Grave || mode == BattleMode.TrainingMonster ? new OrdinaryTroop() : new object();
            if (Source is CampaignUI) ((CampaignUI)Source).m_troops[(byte)7] = Target;
            if (Source is TrialLevelUI) ((TrialLevelUI)Source).m_troop = Target;
            if (Source is GraveLevelUI) ((GraveLevelUI)Source).m_troop = Target;
            if (Source is SealMonsterUI) ((SealMonsterUI)Source).m_troop = Target;
            Source.rmg = Previous; Source.transform.parent = Previous.transform;
            BaseAsset.lds = new Loader { currmg = Previous };
            Battle = new BattleUI { rmg = BattleRoot, m_prevUI = Previous };
            Battle.interpreter.m_report = new game.net.data.StoC.BattleReport
            {
                left_army = new game.net.data.StoC.BattleReport.ArmyInfo { name = "fixture owner", level = 1, npc = false, initiative = 0 },
                right_army = new game.net.data.StoC.BattleReport.ArmyInfo { name = "fixture target", level = 1, npc = false, initiative = 0 },
                result_status = (game.net.data.StoC.BattleReport.ResultStatus)reportStatus
            };
            Battle.gameObject.name = "BattleUI"; Battle.transform.parent = BattleRoot.transform;
            Battle.gameObject.Attach(Battle);
            UnityEngine.GameObject.Paths["/Battle/BattleUI"] = Battle.gameObject;
            UnityEngine.GameObject.Paths["Battle/BattleUI"] = Battle.gameObject;
            if (mode == BattleMode.Arena)
            {
                Battle.OnClose = ((ArenaUI)Source).BattleClose;
                Battle.bUseBattleEnd = Battle.bNeedWaitForBattleEnd = false; Battle.m_reportid = 987;
            }
            else if (mode == BattleMode.PointRace)
            {
                Battle.OnClose = ((PointraceUI)Source).BattleClose;
                Battle.bUseBattleEnd = Battle.bNeedWaitForBattleEnd = false; Battle.m_reportid = 987;
            }
            else if (mode == BattleMode.OwnDemon)
            { Battle.bUseBattleEnd = Battle.bNeedWaitForBattleEnd = false; Battle.m_reportid = 0; Battle.OnClose = null; }
            else
            {
                Battle.m_parentUI = Source;
                if (Source is TrialLevelUI) Battle.OnClose = ((TrialLevelUI)Source).BonusOpen;
                else if (Source is GraveLevelUI) Battle.OnClose = ((GraveLevelUI)Source).BonusOpen;
                else if (Source is SealMonsterUI) Battle.OnClose = ((SealMonsterUI)Source).BonusOpen;
                else Battle.OnClose = ((CampaignUI)Source).BonusOpen;
            }
            Report = Battle.interpreter.m_report;
            Owner = new BattleSettlement(delegate(string name, string fields)
            { Events.Add(name); BattleFixtureTrace.Add(name); }, delegate { return Admission; });
        }
        internal void Advance()
        {
            if (State(this) == null) Owner.AdvanceOwned(Battle, Ownership());
            else Owner.Advance(Battle);
        }
        internal BattlePresentationOwnership Ownership()
        { return new BattlePresentationOwnership(Mode, Operation, Source, Target, Mode == BattleMode.Campaign ? "12:7" : "7", delegate { return ReceiptVerified; }); }
        internal void Start()
        {
            Advance();
            Check(Client.EndRequests == 1 && Battle.interpreter.FinalStateCalls == 1, "one real request and one presentation");
            Check(BattleSettlement.OwnsPending(Client, Battle, Report), "common adapter owns exact installed request");
            Check(State(this) != null && State(this).Presented && !State(this).Applied, "production Attempt uses live lifecycle state");
        }
        internal bool PresentSettled()
        { return BattleSettlement.PresentSettled(Battle, Source, Mode, Operation, Target, "271", Receipt,
            Mode == BattleMode.Arena ? (ushort)0x460 : Mode == BattleMode.PointRace ? (ushort)0x465 : (ushort)0x492,
            Mode == BattleMode.OwnDemon ? 0UL : 987UL, delegate { return ReceiptVerified; }); }
        internal void ReturnToOrigin()
        {
            Delegate close = Battle.OnClose;
            Check(ReferenceEquals(Battle.m_prevUI, Previous) && ReferenceEquals(close.Target, Source), "exact origin roots and close target retained");
            Source.enabled = false;
            Battle.battle_exit();
            Check(ReferenceEquals(BaseAsset.lds.currmg, Previous) && !BaseAsset.lds.isBattling && Source.enabled,
                "stock exit returns exact originating mode page");
            Check(Battle.ExitCalls == 1 && Previous.Enables == 1 && Battle.OnClose == null, "return callback is consumed once");
        }
    }

    private static void RequestSuccess()
    {
        foreach (bool win in new bool[] { true, false })
        {
            Fixture f = new Fixture(BattleMode.Campaign, win ? 1u : 2u); f.Start();
            for (int i = 0; i < 4; i++) f.Advance();
            Check(f.Client.EndRequests == 1 && f.Battle.OpenCalls == 0, "observations cannot reissue pending end or open early");
            byte[] bytes = End(win, 0);
            f.Client.Dispatch(0x408, bytes);
            Check(f.Battle.CallbackCalls == 1 && f.Battle.CallbackPosition == 0 && f.Battle.CallbackArg == null,
                "stock callback receives unchanged stream and argument exactly once");
            Check(f.Battle.BattleEndCalls == 1 && ((CampaignUI)f.Source).Applied == 1 && ((CampaignUI)f.Source).LastWin == win,
                "stock campaign applies actual protobuf win/loss exactly once");
            Check(State(f).Applied && State(f).SettledReportId == 987 && f.Battle.OpenCalls == 0,
                "common proof is committed after stock callback and before result opening");
            Check(Convert.ToBase64String(BattleSettlement.AppliedResponse(f.Battle, f.Report)) == Convert.ToBase64String(bytes),
                "authoritative raw body preserved for real campaign observer");
            for (int i = 0; i < 5; i++) f.Advance();
            Check(f.Battle.OpenCalls == 1 && f.Battle.ResultCalls == 1 && State(f).Opened && f.Client.EndRequests == 1,
                "duplicate observations cannot reopen the result or send another end");
            string trace = String.Join(",", BattleFixtureTrace.Events.ToArray());
            Check(trace.IndexOf("final_armies") < trace.IndexOf("send_0x408") &&
                trace.IndexOf("stock_0x408_returned") < trace.IndexOf("battle_settlement_applied") &&
                trace.IndexOf("battle_settlement_applied") < trace.IndexOf("open_result"), "stock callback ordering grounded in raw game source");
            f.ReturnToOrigin();
            Check(((CampaignUI)f.Source).Returns == 1 && f.Owner.Stop(), "resolved campaign returns once and cleans up");
        }
    }
    private static void UntrustedResponse(string problem)
    {
        Fixture f = new Fixture(BattleMode.Campaign);
        CampaignUI origin = (CampaignUI)f.Source;
        CampaignTroop target = (CampaignTroop)f.Target, victim = new CampaignTroop { idtroop = 8 };
        origin.m_troops[(byte)8] = victim;
        f.Start();
        game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
        DevelopmentNormalCallback wrapper = slot.handler;
        byte[] body = CampaignEnd(problem == "campaign" ? 99U : 12U, problem == "target" ? 8U : 7U,
            problem == "mode" ? 4U : 0U);
        if (problem == "role") Global.character_ = new Database.Character();
        if (problem == "stream")
        {
            f.Client.m_handlers.Remove((ushort)0x408);
            using (MemoryStream shifted = new MemoryStream(body))
            { shifted.Position = 1; try { wrapper(shifted, null); } catch (Exception) { } }
        }
        else { try { f.Client.Dispatch(0x408, body); } catch (Exception) { } }
        Check(origin.Applied == 0 && target.star_level == 0 && target.finish_count == 0 && !target.killed &&
            victim.star_level == 0 && victim.finish_count == 0 && !victim.killed && origin.m_troopWin == null,
            "untrusted " + problem + " touched native business state: callbacks=" + f.Battle.CallbackCalls +
            ", target_finish=" + target.finish_count + ", victim_finish=" + victim.finish_count + ", victim_star=" + victim.star_level);
        Check(f.Battle.CallbackCalls == 0 && f.Battle.BattleEndCalls == 0 && f.Battle.bUseBattleEnd &&
            f.Battle.bNeedWaitForBattleEnd && f.Battle.m_reportid == 0,
            "untrusted " + problem + " must be rejected before stock callback clears settlement flags");
        string firstError = State(f).Error;
        Check(firstError != "" && !State(f).Applied && f.Owner.BlocksGuiInvocation &&
            BattleSettlement.AppliedResponse(f.Battle, f.Report) == null, "untrusted " + problem + " remains owned UNKNOWN");
        using (MemoryStream late = new MemoryStream(End(true, 0))) wrapper(late, null);
        f.Battle.NativeResultGui();
        Throws(f.Advance, firstError); Throws(f.Advance, firstError);
        Check(f.Battle.CallbackCalls == 0 && f.Battle.OpenCalls == 0 && f.Battle.ExitCalls == 0 &&
            f.Client.EndRequests == 1 && !State(f).Applied && !f.Owner.Stop(),
            "duplicate/late reply cannot replay unknown " + problem + " callback, retry or show a result");
    }
    private static void TrustedResponseWritesOnlyOwner()
    {
        Fixture f = new Fixture(BattleMode.Campaign);
        CampaignUI origin = (CampaignUI)f.Source;
        CampaignTroop target = (CampaignTroop)f.Target, victim = new CampaignTroop { idtroop = 8 };
        origin.m_troops[(byte)8] = victim;
        f.Start(); byte[] body = CampaignEnd(12, 7, 0);
        game.net.tcp_base.Handler slot = f.Client.Dispatch(0x408, body);
        using (MemoryStream duplicate = new MemoryStream(body)) slot.handler(duplicate, null);
        Check(origin.Applied == 1 && target.star_level == 3 && target.finish_count == 1 && target.killed &&
            ReferenceEquals(origin.m_troopWin, target) && victim.star_level == 0 && victim.finish_count == 0 && !victim.killed,
            "trusted stock callback applies authoritative result only to exact owned target");
        Check(f.Battle.CallbackCalls == 1 && f.Battle.BattleEndCalls == 1 &&
            ReferenceEquals(f.Battle.CallbackStream, f.Client.DispatchedStream) && f.Battle.CallbackPosition == 0 &&
            f.Battle.CallbackArg == null && State(f).Applied && State(f).Error == "",
            "trusted success forwards original stream/arg exactly once");
        f.Advance(); Check(f.Battle.OpenCalls == 1 && f.Client.EndRequests == 1 && f.Owner.Stop(),
            "trusted success opens once without altering rejection/retry contract");
    }
    private sealed class ForeignMemoryStream : MemoryStream
    { internal ForeignMemoryStream(byte[] body) : base(body) { } }
    private static void UntrustedCallbackValidation()
    {
        foreach (string problem in new[] { "closed", "subclass", "empty", "oversize", "argument", "missing_report", "client_lock" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start();
            game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
            f.Client.m_handlers.Remove((ushort)0x408);
            byte[] body = CampaignEnd(12, 7, 0);
            if (problem == "missing_report")
            {
                using (MemoryStream incomplete = new MemoryStream())
                {
                    Serializer.Serialize(incomplete, new EndPacket { _result = 0, _idcampaign = 12, _idtroop = 7, _iswin = true });
                    body = incomplete.ToArray();
                }
            }
            MemoryStream incoming = problem == "subclass" ? new ForeignMemoryStream(body) :
                new MemoryStream(problem == "empty" ? new byte[0] : problem == "oversize" ? new byte[65537] : body);
            if (problem == "closed") incoming.Close();
            ManualResetEvent held = null, release = null; Thread holder = null;
            try
            {
                if (problem == "client_lock")
                {
                    held = new ManualResetEvent(false); release = new ManualResetEvent(false);
                    holder = new Thread(delegate() { lock (f.Client) { held.Set(); release.WaitOne(); } });
                    holder.Start(); Check(held.WaitOne(2000), "fixture independently holds callback client lock");
                }
                slot.handler(incoming, problem == "argument" ? new object() : null);
            }
            finally
            {
                if (release != null) { release.Set(); holder.Join(); held.Close(); release.Close(); }
                incoming.Close();
            }
            Check(f.Battle.CallbackCalls == 0 && f.Battle.BattleEndCalls == 0 && ((CampaignUI)f.Source).Applied == 0 &&
                ((CampaignTroop)f.Target).finish_count == 0 && f.Battle.bUseBattleEnd && f.Battle.bNeedWaitForBattleEnd,
                "invalid " + problem + " is rejected before native callback/business/settlement-flag changes");
            string firstError = State(f).Error;
            Check(firstError != "" && !State(f).Applied && f.Owner.BlocksGuiInvocation,
                "invalid " + problem + " retains sticky UNKNOWN report ownership");
            using (MemoryStream duplicate = new MemoryStream(body)) slot.handler(duplicate, null);
            f.Battle.NativeResultGui(); Throws(f.Advance, firstError);
            Check(f.Client.EndRequests == 1 && f.Battle.CallbackCalls == 0 && f.Battle.OpenCalls == 0 &&
                f.Battle.ExitCalls == 0 && !f.Owner.Stop(), "invalid " + problem + " never retries, forwards late, opens or exits");
        }
    }
    private static void NativeFirstSuccess()
    {
        foreach (BattleMode mode in new[] { BattleMode.Campaign, BattleMode.Trial, BattleMode.Grave })
        foreach (bool win in new[] { true, false })
        {
            Fixture f = new Fixture(mode, win ? 1u : 2u);
            f.Battle.bNeedWaitForBattleEnd = mode == BattleMode.Campaign;
            f.Battle.NativeFinish();
            game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
            Check(f.Client.EndRequests == 1 && !f.Battle.bSendBattleEnd, "stock finished and sent before first helper GUI");
            f.Owner.ObserveOwned(f.Battle, f.Ownership());
            Check(State(f) != null && f.Owner.BlocksGuiInvocation && BattleSettlement.OwnsPending(f.Client, f.Battle, f.Report),
                "native-first request is retained and observed by exact common owner: " + mode);
            Check(f.Battle.interpreter.FinalStateCalls == 0 && f.Battle.CallbackCalls == 0,
                "Update observation does not present or apply native results: " + mode);
            f.Advance();
            Check(f.Battle.interpreter.FinalStateCalls == 1, "GUI presents only the already-observed native report: " + mode);
            f.Battle.NativeResultGui();
            Check(f.Battle.OpenCalls == 0 && f.Battle.bNeedWaitForBattleEnd && f.Client.EndRequests == 1,
                "Trial/Grave wait=false cannot open while native settlement is unproven: " + mode);
            byte[] body = End(win, 0, mode);
            f.Client.Dispatch(0x408, body);
            using (MemoryStream repeat = new MemoryStream(body)) slot.handler(repeat, null);
            Check(f.Battle.CallbackCalls == 1 && ReferenceEquals(f.Battle.CallbackStream, f.Client.DispatchedStream) &&
                f.Battle.CallbackPosition == 0 && f.Battle.CallbackArg == null &&
                f.Battle.BattleEndCalls == 1 && State(f).Applied && State(f).Error == "",
                "native-first stock callback receives unchanged original once and provides settlement proof: " + mode);
            Check(mode == BattleMode.Campaign ? ((CampaignUI)f.Source).Applied == 1 && ((CampaignUI)f.Source).LastWin == win :
                mode == BattleMode.Trial ? ((TrialLevelUI)f.Source).Applied == 1 && ((TrialLevelUI)f.Source).LastWin == win :
                ((GraveLevelUI)f.Source).Applied == 1 && ((GraveLevelUI)f.Source).LastWin == win,
                "real stock origin receives the authoritative win/loss exactly once: " + mode);
            f.Advance(); f.Advance();
            Check(f.Client.EndRequests == 1 && f.Battle.OpenCalls == 1 && State(f).Opened && !f.Owner.BlocksGuiInvocation,
                "native-first report opens only after exact callback without another send: " + mode);
            f.ReturnToOrigin();
        }
    }
    private static void NativeFirstBindNegatives()
    {
        foreach (BattleMode mode in new[] { BattleMode.Campaign, BattleMode.Trial, BattleMode.Grave })
        foreach (string problem in new[] { "missing", "foreign", "expired", "argument", "queued", "queued_duplicate", "applied_before_observation" })
        {
            Fixture f = new Fixture(mode); f.Battle.bNeedWaitForBattleEnd = mode == BattleMode.Campaign;
            f.Battle.NativeFinish();
            game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
            if (problem == "missing") f.Client.m_handlers.Remove((ushort)0x408);
            else if (problem == "foreign") f.Client.m_handlers[(ushort)0x408] = new game.net.tcp_base.Handler { handler = delegate { } };
            else if (problem == "expired") slot.life = 0;
            else if (problem == "argument") slot.arg = new object();
            else if (problem == "applied_before_observation") f.Client.Dispatch(0x408, End(true, 0, mode));
            else
            {
                f.Client.messages.Add(new game.net.QueueMessage { op = 0x408, stream = new MemoryStream(End(true, 0, mode)) });
                if (problem == "queued_duplicate") f.Client.messages.Add(new game.net.QueueMessage { op = 0x408, stream = new MemoryStream(End(true, 0, mode)) });
            }
            object remaining = f.Client.m_handlers[(ushort)0x408];
            Delegate callback = slot.handler;
            Throws(delegate { f.Owner.ObserveOwned(f.Battle, f.Ownership()); }, "battle_");
            string firstError = State(f).Error;
            Check(firstError != "" && !State(f).Applied && f.Owner.BlocksGuiInvocation && f.Owner.NeedsAdvance,
                "missing native proof retains exact report as UNKNOWN: " + mode + "/" + problem);
            Check(ReferenceEquals(f.Client.m_handlers[(ushort)0x408], remaining) && ReferenceEquals(slot.handler, callback),
                "failed adoption does not overwrite a native or foreign callback: " + mode + "/" + problem);
            Throws(f.Advance, firstError);
            Check(f.Client.EndRequests == 1 && f.Battle.OpenCalls == 0 && f.Battle.ExitCalls == 0 &&
                f.Battle.interpreter.FinalStateCalls == 0 && !f.Owner.Stop(),
                "UNKNOWN native attempt does not resend, present, open or exit: " + mode + "/" + problem);
        }
    }
    private static void NativeFirstPendingChanges()
    {
        foreach (BattleMode mode in new[] { BattleMode.Campaign, BattleMode.Trial, BattleMode.Grave })
        foreach (string change in new[] { "missing", "replacement", "wrapper", "timeout" })
        {
            Fixture f = new Fixture(mode); f.Battle.bNeedWaitForBattleEnd = false; f.Battle.NativeFinish();
            f.Owner.ObserveOwned(f.Battle, f.Ownership());
            game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
            DevelopmentNormalCallback wrapper = slot.handler;
            if (change == "missing") f.Client.m_handlers.Remove((ushort)0x408);
            else if (change == "replacement") f.Client.m_handlers[(ushort)0x408] = new game.net.tcp_base.Handler { handler = delegate { } };
            else if (change == "wrapper") slot.handler = delegate { };
            else SetField(Field(Field(f.Owner, "attempt"), "Pending"), "Tick", unchecked(Environment.TickCount - 12000));
            Throws(f.Owner.ValidateOwnedAttempt, "unknown");
            string firstError = State(f).Error;
            f.Battle.NativeResultGui();
            Check(f.Battle.bNeedWaitForBattleEnd && f.Battle.OpenCalls == 0 && f.Owner.BlocksGuiInvocation,
                "pending native " + change + " holds wait=false result and GUI ownership: " + mode);
            // Late/duplicate notifications cannot cross the stock mutation
            // boundary after this owner has already become UNKNOWN.
            using (MemoryStream late = new MemoryStream(End(true, 0, mode))) wrapper(late, null);
            using (MemoryStream duplicate = new MemoryStream(End(true, 0, mode))) wrapper(duplicate, null);
            Throws(f.Advance, firstError);
            Check(State(f).Error == firstError && !State(f).Applied && f.Battle.CallbackCalls == 0 && f.Battle.BattleEndCalls == 0 &&
                BattleSettlement.AppliedResponse(f.Battle, f.Report) == null && f.Client.EndRequests == 1 &&
                f.Battle.OpenCalls == 0 && f.Battle.ExitCalls == 0 && !f.Owner.Stop(),
                "late duplicate after " + change + " cannot forward, prove, retry or exit: " + mode);
        }
    }
    private static void NativeFirstVisibleResult()
    {
        foreach (BattleMode mode in new[] { BattleMode.Trial, BattleMode.Grave })
        foreach (bool proofAvailable in new[] { true, false })
        {
            Fixture f = new Fixture(mode); f.Battle.bNeedWaitForBattleEnd = false; f.Battle.NativeFinish();
            f.Battle.NativeResultGui();
            Check(f.Battle.ResultCalls == 1 && f.Battle.CallbackCalls == 0, "stock wait=false result was already visible before helper observed " + mode);
            if (!proofAvailable) f.Client.m_handlers.Remove((ushort)0x408);
            try { f.Owner.ObserveOwned(f.Battle, f.Ownership()); } catch (ProbeFailure) { }
            Check(f.Owner.BlocksGuiInvocation && !State(f).Opened && f.Battle.ExitCalls == 0,
                "already-visible native result is not an authorization to exit or infer a loss: " + mode);
            if (proofAvailable)
            {
                f.Client.Dispatch(0x408, End(true, 0, mode)); f.Advance();
                Check(State(f).Applied && State(f).Opened && f.Battle.ResultCalls == 1 && f.Client.EndRequests == 1,
                    "exact native receipt releases already-visible result without recreating it: " + mode);
                f.ReturnToOrigin();
            }
            else
            {
                Throws(f.Advance, State(f).Error);
                Check(!State(f).Applied && f.Battle.OpenCalls == 1 && f.Battle.ExitCalls == 0 && f.Client.EndRequests == 1 && !f.Owner.Stop(),
                    "unproved already-visible result remains UNKNOWN without another GUI effect: " + mode);
            }
        }
    }
    private static void NativeFirstCallbackBeforeGui()
    {
        foreach (BattleMode mode in new[] { BattleMode.Campaign, BattleMode.Trial, BattleMode.Grave })
        {
            Fixture f = new Fixture(mode); f.Battle.NativeFinish(); f.Owner.ObserveOwned(f.Battle, f.Ownership());
            f.Client.Dispatch(0x408, End(true, 0, mode));
            Check(State(f).Applied && !State(f).Presented && !State(f).Opened && f.Owner.BlocksGuiInvocation,
                "Update callback commits native proof but cannot present from Update: " + mode);
            f.Advance(); f.Advance();
            Check(State(f).Presented && State(f).Opened && f.Battle.interpreter.FinalStateCalls == 1 &&
                f.Battle.OpenCalls == 1 && f.Battle.CallbackCalls == 1 && f.Client.EndRequests == 1,
                "first later GUI completes observed native report exactly once: " + mode);
        }
    }
    private static void NativeFirstLockDeadline()
    {
        Fixture f = new Fixture(BattleMode.Trial); f.Battle.bNeedWaitForBattleEnd = false; f.Battle.NativeFinish();
        ManualResetEvent locked = new ManualResetEvent(false), release = new ManualResetEvent(false);
        Thread holder = new Thread(delegate()
        { lock (f.Client) { locked.Set(); release.WaitOne(); } });
        holder.Start();
        try
        {
            Check(locked.WaitOne(2000), "fixture holds client queue lock independently");
            f.Owner.ObserveOwned(f.Battle, f.Ownership());
            Check(State(f) != null && f.Owner.BlocksGuiInvocation && Field(Field(f.Owner, "attempt"), "Pending") == null &&
                f.Battle.bNeedWaitForBattleEnd && f.Client.EndRequests == 1,
                "unavailable queue lock retains native report and closes native wait=false path without another send");
            SetField(Field(f.Owner, "attempt"), "NativeObservedTick", unchecked(Environment.TickCount - 12000));
            Throws(f.Owner.ValidateOwnedAttempt, "native_handler_unobserved_unknown");
            Check(f.Owner.BlocksGuiInvocation && !State(f).Applied && f.Battle.OpenCalls == 0 && !f.Owner.Stop(),
                "unobserved native handler expires instead of waiting forever");
        }
        finally { release.Set(); holder.Join(); locked.Close(); release.Close(); }
    }
    private static void FirstTrainingSkip()
    {
        foreach (string path in new[] { "request", "native_request", "embedded" })
        {
            Fixture f = new Fixture(BattleMode.TrainingMonster);
            if (path == "native_request") f.Battle.NativeFinish();
            else if (path == "embedded")
            {
                f.Battle.bUseBattleEnd = false;
                f.Battle.m_pktBattleEnd = new PendingBattleEnd { pkt = new EndPacket { _result = 0, _type = 7, _idbattle_report = 987, _iswin = true } };
            }
            Check(!BattleSettlement.PresentOwned(f.Battle) && State(f) == null,
                "legacy unbound training skip reproduces owner-unavailable before first GUI: " + path);
            Check(!BattleSettlement.PresentOwned(f.Battle, null) && State(f) == null,
                "first training skip cannot bind without exact adapter ownership: " + path);
            Check(BattleSettlement.PresentOwned(f.Battle, f.Ownership()),
                "first queued training skip binds its exact adapter on the action: " + path);
            if (path != "embedded") f.Client.Dispatch(0x408, End(true, 0, BattleMode.TrainingMonster));
            f.Advance();
            Check(State(f).Mode == BattleMode.TrainingMonster && State(f).Applied && State(f).Opened &&
                ((SealMonsterUI)f.Source).Applied == 1 && f.Battle.BattleEndCalls == 1 && f.Battle.interpreter.FinalStateCalls == 1 &&
                f.Client.EndRequests == (path == "embedded" ? 0 : 1),
                "training skip uses actual type-7 stock settlement semantics and never duplicates native sends: " + path);
            f.ReturnToOrigin();
        }
        foreach (string mismatch in new[] { "source", "target", "mode", "operation", "unverified" })
        {
            Fixture f = new Fixture(BattleMode.TrainingMonster);
            BattlePresentationOwnership ownership = new BattlePresentationOwnership(
                mismatch == "mode" ? BattleMode.Trial : BattleMode.TrainingMonster,
                mismatch == "operation" ? "" : Operation, mismatch == "source" ? new SealMonsterUI() : f.Source,
                mismatch == "target" ? new OrdinaryTroop() : f.Target, "7", delegate { return mismatch != "unverified"; });
            Check(!BattleSettlement.PresentOwned(f.Battle, ownership) && State(f) == null && f.Client.EndRequests == 0 &&
                f.Battle.interpreter.FinalStateCalls == 0 && f.Battle.OpenCalls == 0,
                "first skip refuses arbitrary visible battle with changed " + mismatch);
        }
    }
    private static void LaunchReportTransferFailure()
    {
        Fixture f = new Fixture(BattleMode.TrainingMonster); BattleLaunch.RejectReport = true;
        Throws(delegate { BattleSettlement.PresentOwned(f.Battle, f.Ownership()); }, "launch receipt unavailable");
        Check(State(f) != null && State(f).Error.Contains("launch_report_unverified_unknown") && f.Owner.BlocksGuiInvocation &&
            f.Client.EndRequests == 0 && f.Battle.interpreter.FinalStateCalls == 0 && !f.Owner.Stop(),
            "failed launch-to-report transfer is sticky UNKNOWN before any settlement effect");
        BattleLaunch.RejectReport = false;
        Throws(f.Advance, "launch_report_unverified_unknown");
        Check(f.Client.EndRequests == 0 && f.Battle.OpenCalls == 0, "later guard recovery cannot erase failed report transfer");
    }
    private static void InitialReceipt()
    {
        foreach (BattleMode mode in new BattleMode[] { BattleMode.Arena, BattleMode.PointRace })
        {
            Fixture f = new Fixture(mode);
            // These business changes precede battle playback in the actual
            // 0x460 / 0x465 stock callbacks; common presentation must not replay them.
            if (mode == BattleMode.Arena) ((ArenaUI)f.Source).ApplyInitialReceipt();
            else ((PointraceUI)f.Source).ApplyInitialReceipt();
            for (int i = 0; i < 6; i++) Check(f.PresentSettled(), "verified initial receipt presents exact mode");
            Check(f.Client.EndRequests == 0 && f.Battle.CallbackCalls == 0 && f.Battle.BattleEndCalls == 0,
                "settled initial reply never sends or fabricates 0x408");
            Check(f.Battle.interpreter.FinalStateCalls == 1 && f.Battle.OpenCalls == 1 && f.Battle.ResultCalls == 1,
                "initial receipt presentation and opening each run once");
            Check(State(f).Mode == mode && State(f).Path == BattleSettlementPath.SettledResponse && State(f).Applied && State(f).Opened,
                "actual common Attempt records settled-response source mode");
            string trace = String.Join(",", BattleFixtureTrace.Events.ToArray());
            Check(trace.IndexOf(mode == BattleMode.Arena ? "arena_initial_applied" : "pointrace_initial_applied") < trace.IndexOf("final_armies") &&
                trace.IndexOf("final_armies") < trace.IndexOf("battle_settlement_applied") &&
                trace.IndexOf("battle_settlement_applied") < trace.IndexOf("open_result"),
                "initial stock settlement precedes common presentation/apply/open");
            Check(BattleSettlement.AppliedResponse(f.Battle, f.Report) == null, "initial receipt is not mislabeled an end response");
            f.ReturnToOrigin();
            Check(mode == BattleMode.Arena ? ((ArenaUI)f.Source).Applied == 1 && ((ArenaUI)f.Source).Returns == 1 :
                ((PointraceUI)f.Source).Applied == 1 && ((PointraceUI)f.Source).Returns == 1,
                "initial business settlement and mode return remain single");
            Check(f.Owner.Stop(), "settled-response owner cleans up");
        }
    }
    private static void EmbeddedPacket()
    {
        Fixture f = new Fixture(BattleMode.Campaign);
        f.Battle.bUseBattleEnd = false;
        f.Battle.m_pktBattleEnd = new PendingBattleEnd { pkt = new EndPacket { _result = 0, _idcampaign = 12,
            _idtroop = 7, _idbattle_report = 987, _iswin = true } };
        for (int i = 0; i < 5; i++) f.Advance();
        Check(f.Client.EndRequests == 0 && f.Battle.BattleEndCalls == 1 && ((CampaignUI)f.Source).Applied == 1,
            "real embedded packet uses stock BattleEnd once without network end");
        Check(State(f).Path == BattleSettlementPath.EmbeddedEnd && State(f).Applied && State(f).Opened &&
            f.Battle.interpreter.FinalStateCalls == 1 && f.Battle.OpenCalls == 1, "embedded apply/presentation/open lifecycle is one-shot");
        f.ReturnToOrigin(); Check(f.Owner.Stop(), "embedded owner releases after return");
    }
    private static void DuplicateCallback()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
        f.Client.Dispatch(0x408, End(true, 0));
        using (MemoryStream duplicate = new MemoryStream(End(true, 0))) slot.handler(duplicate, null);
        Check(f.Battle.CallbackCalls == 1 && f.Battle.BattleEndCalls == 1, "duplicate callback cannot apply stock settlement twice");
        Check(State(f).Error == "" && State(f).Applied, "duplicate notification preserves original proof");
        f.Advance(); f.Advance();
        Check(f.Battle.OpenCalls == 1 && f.Client.EndRequests == 1 && f.Owner.Stop(), "duplicate notification does not reopen or retry");
    }
    private static void RejectionThenSuccess()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        f.Client.Dispatch(0x408, End(false, 1));
        Check(f.Battle.CallbackCalls == 0 && f.Battle.bUseBattleEnd && f.Battle.bNeedWaitForBattleEnd &&
            !State(f).Applied && f.Battle.OpenCalls == 0, "explicit rejection cannot mutate stock into a false outcome");
        f.Advance(); Check(f.Client.EndRequests == 1, "rejection observes backoff");
        object a = Field(f.Owner, "attempt");
        SetField(a, "RetryTick", unchecked(Environment.TickCount - 60000)); f.Advance();
        Check(f.Client.EndRequests == 2 && f.Battle.interpreter.FinalStateCalls == 1, "only exact rejection allows a bounded end retry");
        f.Client.Dispatch(0x408, End(true, 0)); f.Advance();
        Check(State(f).Applied && f.Battle.CallbackCalls == 1 && f.Battle.OpenCalls == 1, "later authoritative success applies once");
    }
    private static void RejectionLimit()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        for (int request = 1; request <= 6; request++)
        {
            Check(f.Client.EndRequests == request, "exact request count before explicit rejection " + request);
            f.Client.Dispatch(0x408, End(false, 1));
            Check(!State(f).Applied && f.Battle.CallbackCalls == 0 && f.Battle.OpenCalls == 0,
                "every explicit rejection remains uncommitted " + request);
            if (request < 6)
            {
                SetField(Field(f.Owner, "attempt"), "RetryTick", unchecked(Environment.TickCount - 60000));
                f.Advance();
            }
        }
        Throws(f.Advance, "retry_bound"); Throws(f.Advance, "retry_bound");
        Check(f.Client.EndRequests == 6 && f.Battle.interpreter.FinalStateCalls == 1 && !f.Owner.Stop(),
            "six explicit rejections retain unknown ownership with no seventh send");
    }
    private static void DuplicateRejection()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        game.net.tcp_base.Handler slot = f.Client.Dispatch(0x408, End(false, 1));
        using (MemoryStream repeat = new MemoryStream(End(false, 1))) slot.handler(repeat, null);
        SetField(Field(f.Owner, "attempt"), "RetryTick", unchecked(Environment.TickCount - 60000));
        f.Advance(); Check(f.Client.EndRequests == 2 && f.Battle.CallbackCalls == 0 && State(f).Error == "",
            "duplicate rejection is idempotent and does not create an extra retry or poison exact reply proof");
        f.Client.Dispatch(0x408, End(true, 0)); f.Advance();
        Check(State(f).Applied && f.Battle.OpenCalls == 1 && f.Battle.BattleEndCalls == 1,
            "one later authoritative result settles after duplicate rejection");
    }
    private static void CallbackBeforeDequeue()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
        using (MemoryStream premature = new MemoryStream(End(true, 0))) slot.handler(premature, null);
        Check(f.Battle.CallbackCalls == 0 && !State(f).Applied && State(f).Error.Contains("unknown"),
            "callback from an installed one-shot slot is not accepted as a dequeued native response");
        f.Client.Dispatch(0x408, End(true, 0));
        Check(f.Battle.CallbackCalls == 0 && f.Battle.BattleEndCalls == 0, "later dispatch cannot forward a wrapper whose order is unknown");
        Throws(f.Advance, "unknown"); Check(f.Client.EndRequests == 1 && f.Battle.OpenCalls == 0,
            "invalid callback ordering retains unknown without resend or result");
    }
    private static void UnknownReplies()
    {
        foreach (byte[] body in new byte[][] { null, new byte[] { 8, 2 }, new byte[] { 128 }, new byte[] { 16, 0 },
            new byte[] { 8, 0, 8, 0 } })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start();
            try { f.Client.Dispatch(0x408, body); } catch (Exception) { }
            Check(f.Battle.CallbackCalls == 0 && !State(f).Applied && f.Battle.OpenCalls == 0 &&
                f.Battle.bUseBattleEnd && f.Battle.bNeedWaitForBattleEnd,
                "null/malformed/missing/duplicate/unexpected result cannot forward or clear native settlement flags");
            Throws(f.Advance, "battle_settlement");
            Check(f.Client.EndRequests == 1 && !f.Owner.Stop(), "unknown reply neither retries nor releases ownership");
        }
    }
    private static void Timeout()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        object pending = Field(Field(f.Owner, "attempt"), "Pending");
        SetField(pending, "Tick", unchecked(Environment.TickCount - 13000));
        Throws(f.Advance, "unknown"); Throws(f.Advance, "unknown");
        Check(f.Client.EndRequests == 1 && f.Battle.OpenCalls == 0 && !State(f).Applied && !f.Owner.Stop(),
            "response timeout is unresolved and cannot resend or show a result");
    }
    private static void RejectionIdentityGate()
    {
        foreach (string change in new string[] { "surrender", "use_end", "replay", "reset" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start();
            game.net.tcp_base.Handler rejected = f.Client.Dispatch(0x408, End(false, 1));
            SetField(Field(f.Owner, "attempt"), "RetryTick", unchecked(Environment.TickCount - 60000));
            if (change == "surrender") f.Battle.bBattle_surrended = true;
            else if (change == "use_end") f.Battle.bUseBattleEnd = false;
            else if (change == "replay") f.Battle.m_viewType = enumBattleViewType.viewReport;
            else f.Battle.bBattle_started = false;
            Throws(f.Owner.ValidateOwnedAttempt, change == "reset" ? "owned_battle_reset_unknown" : "owned_identity_changed");
            string firstError = State(f).Error;
            Check(firstError != "" && f.Owner.NeedsAdvance && f.Owner.BlocksGuiInvocation && !State(f).Applied,
                "rejected battle with changed " + change + " retains unknown GUI ownership");
            // A later normal-looking frame must not erase the earlier unknown.
            f.Battle.bBattle_surrended = false; f.Battle.bUseBattleEnd = true;
            f.Battle.m_viewType = enumBattleViewType.viewVipSkip; f.Battle.bBattle_started = true;
            Throws(f.Advance, firstError);
            Throws(delegate { f.Owner.Advance(null); }, firstError);
            using (MemoryStream late = new MemoryStream(End(true, 0))) rejected.handler(late, null);
            Check(State(f).Error == firstError && !State(f).Applied && f.Battle.CallbackCalls == 0 &&
                f.Battle.BattleEndCalls == 0 && f.Battle.OpenCalls == 0 && f.Client.EndRequests == 1 &&
                BattleSettlement.AppliedResponse(f.Battle, f.Report) == null && !f.Owner.Stop(),
                "sticky unknown after " + change + " never resends, applies or opens another settlement");
            Throws(delegate { new BattleSettlement(delegate { }, delegate { return true; }); }, "prior_owner_unresolved");
        }
    }
    private static void SetUnavailable(Fixture f, string kind, bool unavailable)
    {
        if (kind == "hidden") f.Battle.enabled = !unavailable;
        else
        {
            f.Battle.loading = unavailable;
            // Loading may briefly reset the started flag without changing any
            // role/report/root ownership. A reset outside loading is unknown.
            f.Battle.bBattle_started = !unavailable;
        }
    }
    private static void RemoveFreshBattleLookup(Fixture f)
    {
        UnityEngine.GameObject.Paths.Remove("/Battle/BattleUI");
        UnityEngine.GameObject.Paths.Remove("Battle/BattleUI");
        Check(UnityEngine.GameObject.Find("/Battle/BattleUI") == null &&
            ReferenceEquals(f.Battle.interpreter.m_report, f.Report), "fresh lookup is absent while the owned report reference remains exact");
    }
    private static void AvailabilityRecovers()
    {
        foreach (string kind in new string[] { "hidden", "loading" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start();
            // A received rejection has no pending 12-second response deadline;
            // this isolates the independent 25-second availability deadline.
            f.Client.Dispatch(0x408, End(false, 1));
            RemoveFreshBattleLookup(f); SetUnavailable(f, kind, true);
            f.Owner.Advance(null);
            object a = Field(f.Owner, "attempt");
            Check((bool)Field(a, "Unavailable") && State(f).Error == "" && f.Owner.BlocksGuiInvocation,
                "temporary " + kind + " is retained without losing exact settlement ownership");
            SetField(a, "UnavailableTick", unchecked(Environment.TickCount - 24000));
            SetField(a, "RetryTick", unchecked(Environment.TickCount - 60000));
            f.Owner.AdvanceOwnedAttempt();
            Check(State(f).Error == "" && f.Client.EndRequests == 1 && f.Battle.OpenCalls == 0,
                "under 25 seconds " + kind + " pauses retry without creating an unknown outcome");
            SetUnavailable(f, kind, false); f.Owner.ValidateOwnedAttempt();
            Check(!(bool)Field(a, "Unavailable") && State(f).Error == "", "same owned report recovers after temporary " + kind);
            f.Owner.AdvanceOwnedAttempt();
            Check(f.Client.EndRequests == 2 && f.Battle.interpreter.FinalStateCalls == 1,
                "restored " + kind + " resumes only the already-owned explicit-rejection retry");
            f.Client.Dispatch(0x408, End(true, 0)); f.Owner.AdvanceOwnedAttempt();
            Check(State(f).Applied && State(f).Opened && f.Battle.BattleEndCalls == 1 && f.Battle.OpenCalls == 1 &&
                !f.Owner.BlocksGuiInvocation, "recovered " + kind + " finishes the original report once");
        }
    }
    private static void AvailabilityDeadline()
    {
        foreach (string kind in new string[] { "hidden", "loading" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start(); f.Client.Dispatch(0x408, End(false, 1));
            RemoveFreshBattleLookup(f); SetUnavailable(f, kind, true); f.Owner.Advance(null);
            object a = Field(f.Owner, "attempt");
            SetField(a, "UnavailableTick", unchecked(Environment.TickCount - 25000));
            SetField(a, "RetryTick", unchecked(Environment.TickCount - 60000));
            Throws(delegate { f.Owner.Advance(null); }, "owned_ui_unavailable_unknown");
            string firstError = State(f).Error;
            SetUnavailable(f, kind, false);
            Throws(f.Owner.AdvanceOwnedAttempt, firstError);
            Check(State(f).Error == firstError && f.Owner.NeedsAdvance && f.Owner.BlocksGuiInvocation &&
                !State(f).Applied && f.Client.EndRequests == 1 && f.Battle.BattleEndCalls == 0 &&
                f.Battle.OpenCalls == 0 && !f.Owner.Stop(), "25-second " + kind + " deadline is sticky and cannot resume settlement");
        }
    }
    private static void MissingLookupResponseDeadline()
    {
        foreach (string kind in new string[] { "lookup_missing", "hidden", "loading" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start(); RemoveFreshBattleLookup(f);
            if (kind != "lookup_missing") SetUnavailable(f, kind, true);
            object pending = Field(Field(f.Owner, "attempt"), "Pending");
            SetField(pending, "Tick", unchecked(Environment.TickCount - 11000));
            f.Owner.Advance(null);
            Check(State(f).Error == "" && f.Owner.BlocksGuiInvocation && f.Client.EndRequests == 1,
                "missing fresh UI lookup does not falsely expire an 11-second pending response: " + kind);
            SetField(pending, "Tick", unchecked(Environment.TickCount - 12000));
            Throws(delegate { f.Owner.Advance(null); }, "response_unknown_no_retry");
            string firstError = State(f).Error;
            Throws(f.Owner.ValidateOwnedAttempt, firstError);
            Check(State(f).Error == firstError && f.Owner.NeedsAdvance && f.Owner.BlocksGuiInvocation &&
                !State(f).Applied && f.Client.EndRequests == 1 && f.Battle.CallbackCalls == 0 && f.Battle.OpenCalls == 0,
                "12-second pending response deadline is enforced independently of fresh lookup/visibility: " + kind);
        }
    }
    private static void AppliedCleanupGate()
    {
        foreach (string kind in new string[] { "hidden", "loading" })
        {
            Fixture f = new Fixture(BattleMode.Campaign);
            Check(!f.Owner.BlocksGuiInvocation && !f.Owner.NeedsAdvance, "empty core has no GUI invocation exclusion");
            f.Start(); Check(f.Owner.BlocksGuiInvocation && f.Owner.NeedsAdvance, "pending core excludes another GUI invocation");
            f.Client.Dispatch(0x408, End(true, 0));
            Check(State(f).Applied && !State(f).Opened && f.Owner.BlocksGuiInvocation,
                "applied native response retains GUI exclusion until its result has opened");
            RemoveFreshBattleLookup(f); SetUnavailable(f, kind, true);
            f.Owner.Advance(null); f.Owner.AdvanceOwnedAttempt();
            Check(State(f).Applied && State(f).Opened && State(f).Error == "" && f.Battle.OpenCalls == 1 &&
                !f.Owner.NeedsAdvance && !f.Owner.BlocksGuiInvocation,
                "owned applied result opens while " + kind + " and releases GUI exclusion exactly once");
            SetUnavailable(f, kind, false); f.ReturnToOrigin();
            // The ordinary close consumes OnClose and changes visible roots.
            // Those expected cleanup changes cannot invalidate an already
            // applied/opened report or block the mode's next legitimate action.
            f.Owner.ValidateOwnedAttempt(); f.Owner.Advance(null); f.Owner.AdvanceOwnedAttempt();
            Check(State(f).Error == "" && State(f).Applied && State(f).Opened && f.Owner.Stop() &&
                !f.Owner.BlocksGuiInvocation && !f.Owner.NeedsAdvance && f.Client.EndRequests == 1 &&
                f.Battle.OpenCalls == 1 && ((CampaignUI)f.Source).Returns == 1,
                "completed result permits original mode cleanup without another settlement or a false identity failure");
        }
    }
    private static void IdentityChanges()
    {
        string[] cases = { "role", "role_id", "socket", "stream", "loader", "previous", "parent", "root", "interpreter", "report", "report_id" };
        foreach (string change in cases)
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start();
            if (change == "role") Global.character_ = new Database.Character();
            else if (change == "role_id") Global.character_.id++;
            else if (change == "socket") f.Client.m_client = new game.net.FixtureSocket();
            else if (change == "stream") f.Client.m_stream = new MemoryStream();
            else if (change == "loader") BaseAsset.lds = new Loader { currmg = f.Previous };
            else if (change == "previous") f.Battle.m_prevUI = new RootMessenger();
            else if (change == "parent") f.Battle.m_parentUI = new CampaignUI();
            else if (change == "root") f.Battle.rmg = new RootMessenger();
            else if (change == "interpreter") f.Battle.interpreter = new BattleInterpreter();
            else if (change == "report") f.Battle.interpreter.m_report = new object();
            else f.Battle.m_reportid = 55;
            f.Client.Dispatch(0x408, End(true, 0));
            Check(f.Battle.CallbackCalls == 0 && f.Battle.BattleEndCalls == 0 && !State(f).Applied &&
                BattleSettlement.AppliedResponse(f.Battle, f.Report) == null,
                "changed " + change + " cannot reach stock mutation or prove the original battle");
            Throws(f.Advance, "battle_settlement");
            Check(f.Battle.OpenCalls == 0 && f.Client.EndRequests == 1 && !f.Owner.Stop(), "identity mismatch remains owned and cannot retry: " + change);
        }
    }
    private static void IdentityDuringStockCallback()
    {
        foreach (string change in new string[] { "role_id", "socket", "previous", "root", "target" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start();
            f.Battle.DuringStockCallback = delegate
            {
                if (change == "role_id") Global.character_.id++;
                else if (change == "socket") f.Client.m_client = new game.net.FixtureSocket();
                else if (change == "previous") f.Battle.m_prevUI = new RootMessenger();
                else if (change == "root") f.Battle.rmg = new RootMessenger();
                else ((CampaignUI)f.Source).m_troops[(byte)7] = new CampaignTroop();
            };
            try { f.Client.Dispatch(0x408, End(true, 0)); } catch (ProbeFailure) { }
            Check(f.Battle.CallbackCalls == 1 && !State(f).Applied && State(f).Error != "" &&
                BattleSettlement.AppliedResponse(f.Battle, f.Report) == null,
                "identity changing inside stock callback cannot certify stale owner: " + change);
            Throws(f.Advance, "battle_settlement");
            Check(f.Client.EndRequests == 1 && f.Battle.OpenCalls == 0, "mid-callback identity mismatch cannot reopen or retry: " + change);
        }
    }
    private static void IdentityBeforeOpen()
    {
        foreach (string change in new string[] { "role_id", "source_root", "close", "proof" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start(); f.Client.Dispatch(0x408, End(true, 0));
            if (change == "role_id") Global.character_.id++;
            else if (change == "source_root") f.Source.transform.parent = new RootMessenger().transform;
            else if (change == "close") f.Battle.OnClose = new CampaignUI().BonusOpen;
            else f.ReceiptVerified = false;
            Throws(f.Advance, "battle_settlement");
            Check(f.Battle.OpenCalls == 0 && State(f).Error != "" && f.Client.EndRequests == 1,
                "opening cannot cross changed owner after stock returned: " + change);
        }
    }
    private static void PauseAndStop()
    {
        Fixture paused = new Fixture(BattleMode.Campaign); paused.Admission = false;
        paused.Advance(); Check(paused.Client.EndRequests == 0 && paused.Battle.interpreter.FinalStateCalls == 0,
            "pause before admission causes no side effects");
        paused.Admission = true; paused.Start(); paused.Admission = false;
        paused.Client.Dispatch(0x408, End(true, 0)); paused.Advance();
        Check(paused.Battle.CallbackCalls == 1 && paused.Client.EndRequests == 1 && paused.Battle.OpenCalls == 1 && State(paused).Applied,
            "pause permits the current owned battle to settle without a new battle");

        Fixture stopped = new Fixture(BattleMode.Campaign); Check(stopped.Owner.Stop(), "stop before request is clean");
        stopped.Advance(); Check(stopped.Client.EndRequests == 0 && stopped.Battle.interpreter.FinalStateCalls == 0,
            "stopped owner never begins a battle effect");

        Fixture pending = new Fixture(BattleMode.Campaign); pending.Start();
        game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)pending.Client.m_handlers[(ushort)0x408];
        Check(!pending.Owner.Stop(), "stop with unresolved request retains unknown ownership");
        Check(slot.handler.Target != pending.Battle && slot.handler.Method.Name == "Response",
            "stop retains the exact pending wrapper until its owned native callback resolves");
        pending.Client.Dispatch(0x408, End(true, 0)); pending.Advance();
        Check(pending.Battle.CallbackCalls == 1 && pending.Client.EndRequests == 1 && pending.Battle.OpenCalls == 1 && State(pending).Applied,
            "late stock callback after stop completes only current battle without resubmission");
    }
    private static void ForeignHandler()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        game.net.tcp_base.Handler foreign = new game.net.tcp_base.Handler { handler = delegate { } };
        f.Client.m_handlers[(ushort)0x408] = foreign;
        Check(!f.Owner.Stop() && ReferenceEquals(f.Client.m_handlers[(ushort)0x408], foreign),
            "stop preserves another contributor's replacement handler");
    }
    private static void UnknownReplacement()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        f.Battle.interpreter.m_report = new object();
        Throws(f.Advance, "owned_identity_changed");
        Check(f.Client.EndRequests == 1 && !State(f).Applied && !f.Owner.Stop(),
            "an unresolved report cannot be replaced with a newly observed fight");
    }
    private static void OwnershipChanges()
    {
        foreach (string change in new string[] { "operation", "mode", "target", "target_id", "source" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start();
            BattlePresentationOwnership ownership = new BattlePresentationOwnership(change == "mode" ? BattleMode.NormalChallenge : BattleMode.Campaign,
                change == "operation" ? new string('b', 32) : Operation,
                change == "source" ? new CampaignUI() : f.Source,
                change == "target" ? new CampaignTroop() : f.Target,
                change == "target_id" ? "12:8" : "12:7", delegate { return true; });
            Throws(delegate { f.Owner.AdvanceOwned(f.Battle, ownership); }, "operation_or_receipt_changed");
            Check(f.Client.EndRequests == 1 && f.Battle.OpenCalls == 0 && State(f).Error != "",
                "active operation cannot be rebound by changed " + change);
        }
    }
    private static void SameProcessOwnership()
    {
        Fixture unresolved = new Fixture(BattleMode.Campaign); unresolved.Start();
        Throws(delegate { new BattleSettlement(delegate { }, delegate { return true; }); }, "prior_owner_unresolved");
        Check(BattleSettlement.OwnsPending(unresolved.Client, unresolved.Battle, unresolved.Report),
            "rejected constructor leaves exact prior pending request owned");
        unresolved.Client.Dispatch(0x408, End(true, 0)); unresolved.Advance();
        BattleSettlement replacement = new BattleSettlement(delegate { }, delegate { return true; });
        Throws(delegate { replacement.AdvanceOwned(unresolved.Battle, new BattlePresentationOwnership(BattleMode.Campaign,
            Operation, unresolved.Source, unresolved.Target, "12:7", delegate { return true; })); }, "previous_report_or_receipt_reused");
        Check(unresolved.Client.EndRequests == 1 && unresolved.Battle.OpenCalls == 1,
            "new owner retains history and cannot replay a completed report");
    }
    private static void InitialIdentityAndProof()
    {
        foreach (string problem in new string[] { "unverified", "report", "close", "source_root", "unknown_operation" })
        {
            Fixture f = new Fixture(BattleMode.Arena);
            if (problem == "unverified") f.ReceiptVerified = false;
            else if (problem == "report") f.Battle.m_reportid++;
            else if (problem == "close") f.Battle.OnClose = new ArenaUI().BattleClose;
            else if (problem == "source_root") f.Source.transform.parent = new RootMessenger().transform;
            bool accepted = false;
            try
            {
                accepted = problem == "unknown_operation" ? BattleSettlement.PresentSettled(f.Battle, f.Source, BattleMode.Arena,
                    "", f.Target, "271", f.Receipt, 0x460, 987, delegate { return true; }) : f.PresentSettled();
            }
            catch (ProbeFailure) { }
            catch (ArgumentException) { }
            Check(!accepted && f.Client.EndRequests == 0 && f.Battle.interpreter.FinalStateCalls == 0 && f.Battle.OpenCalls == 0,
                "initial settlement proof refuses " + problem + " before side effects");
        }
    }
    private static void UnprovedEmbedded()
    {
        foreach (string problem in new string[] { "missing", "rejected", "wrong_mode", "wrong_target" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Battle.bUseBattleEnd = false;
            if (problem != "missing") f.Battle.m_pktBattleEnd = new PendingBattleEnd { pkt = new EndPacket { _result = problem == "rejected" ? 1U : 0U,
                _type = problem == "wrong_mode" ? 5U : 0U, _idcampaign = 12, _idtroop = problem == "wrong_target" ? 8U : 7U, _idbattle_report = 987 } };
            try { f.Advance(); } catch (ProbeFailure) { }
            Check(f.Client.EndRequests == 0 && f.Battle.BattleEndCalls == 0 && f.Battle.OpenCalls == 0 && f.Battle.interpreter.FinalStateCalls == 0,
                "false useEnd without exact matching packet cannot settle: " + problem);
        }
    }
    private static void OriginalCallbackFailure()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start(); f.Battle.ThrowStockCallback = true;
        Throws(delegate { f.Client.Dispatch(0x408, End(true, 0)); }, "fixture stock callback failed");
        Check(!State(f).Applied && f.Battle.CallbackCalls == 1 && f.Battle.OpenCalls == 0,
            "throwing stock callback cannot certify outcome");
        Throws(f.Advance, "original_callback_threw"); Check(f.Client.EndRequests == 1, "throwing stock callback is never retried");
    }
    private static void OpenFailure()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start(); f.Client.Dispatch(0x408, End(true, 0));
        f.Battle.ThrowOpen = true;
        Throws(f.Advance, "fixture result opening failed");
        try { f.Advance(); } catch (ProbeFailure) { }
        Check(f.Battle.OpenCalls == 1 && State(f).Error != "" && !State(f).Opened && f.Client.EndRequests == 1,
            "unknown result-opening side effect is retained without a second invocation");
    }
    private static void PresentationFailure()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Battle.interpreter.ThrowFinal = true;
        Throws(f.Advance, "fixture final state failed");
        Throws(f.Advance, "unknown");
        Check(f.Battle.interpreter.FinalStateCalls == 1 && f.Client.EndRequests == 0 && f.Battle.OpenCalls == 0 &&
            State(f).PresentationStarted && !State(f).Presented && State(f).Error != "",
            "unknown presentation side effect is not repeated or mistaken for settlement");
    }
    private static void MissingStockApplication()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start(); f.Battle.SkipStockApply = true;
        Throws(delegate { f.Client.Dispatch(0x408, End(true, 0)); }, "application_unverified");
        Check(f.Battle.CallbackCalls == 1 && !State(f).Applied && f.Battle.OpenCalls == 0,
            "a returned callback without independent report/flag readback is not settlement proof");
        Throws(f.Advance, "application_unverified"); Check(f.Client.EndRequests == 1, "unproved stock application cannot retry");
    }
    private static void ReentrantStop()
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        f.Battle.DuringStockCallback = delegate { Check(!f.Owner.Stop(), "in-flight stock callback cannot be handed off"); };
        f.Client.Dispatch(0x408, End(true, 0)); f.Advance();
        Check(f.Battle.CallbackCalls == 1 && State(f).Applied && State(f).Opened && f.Client.EndRequests == 1 && f.Owner.Stop(),
            "stop inside native callback finishes only the current owned battle");
    }
    private static void AssemblyBoundary()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = assembly.GetName().Name;
            Check(name != "UnityEngine" && name != "CSharp" && name != "Assembly-CSharp" && name != "Assembly-CSharp-firstpass" && name != "dbdata",
                "no Unity or game logic assembly is loaded; pinned pbdata protocol is allowed: " + name);
        }
    }

    public static int Main(string[] args)
    {
        KeyValuePair<string, Action>[] scenarios = {
            new KeyValuePair<string, Action>("durable-request-success-and-old-callback", DurableRequestSuccess),
            new KeyValuePair<string, Action>("durable-embedded-success", DurableEmbeddedSuccess),
            new KeyValuePair<string, Action>("durable-settled-response-success", DurableSettledSuccess),
            new KeyValuePair<string, Action>("durable-confirmation-fault-sticky-unknown", DurableConfirmationFaults),
            new KeyValuePair<string, Action>("durable-unknown-preserves-start", DurableUnknowns),
            new KeyValuePair<string, Action>("settlement-report-loss-blocks-win", delegate { SettlementReportMismatch(2, true); }),
            new KeyValuePair<string, Action>("settlement-report-draw-blocks-win", delegate { SettlementReportMismatch(3, true); }),
            new KeyValuePair<string, Action>("settlement-report-win-blocks-loss", delegate { SettlementReportMismatch(1, false); }),
            new KeyValuePair<string, Action>("settlement-report-unknown-blocks-success", SettlementReportUnknown),
            new KeyValuePair<string, Action>("settlement-empty-required-treasure-blocked", SettlementEmptyTreasure),
            new KeyValuePair<string, Action>("settlement-success-full-schema-negatives", SettlementSchemaNegatives),
            new KeyValuePair<string, Action>("settlement-optional-defaults-accepted", SettlementOptionalDefaults),
            new KeyValuePair<string, Action>("settlement-embedded-report-consistency", SettlementEmbeddedConsistency),
            new KeyValuePair<string, Action>("settlement-matching-win-loss-draw", SettlementMatchingResults),
            new KeyValuePair<string, Action>("settlement-explicit-rejection-nonwin-retry", SettlementNonwinRejectionRetry),
            new KeyValuePair<string, Action>("settlement-rejection-empty-required-treasure-blocked", SettlementMalformedRejectionTreasure),
            new KeyValuePair<string, Action>("settlement-rejection-full-schema-negatives", SettlementMalformedRejectionSchema),
            new KeyValuePair<string, Action>("settlement-rejection-after-retry-blocked", SettlementMalformedRejectionAfterRetry),
            new KeyValuePair<string, Action>("settlement-rejection-minimal-and-optional-defaults", SettlementRejectionDefaults),
            new KeyValuePair<string, Action>("untrusted-response-target", delegate { UntrustedResponse("target"); }),
            new KeyValuePair<string, Action>("untrusted-response-mode", delegate { UntrustedResponse("mode"); }),
            new KeyValuePair<string, Action>("untrusted-response-campaign", delegate { UntrustedResponse("campaign"); }),
            new KeyValuePair<string, Action>("untrusted-response-role", delegate { UntrustedResponse("role"); }),
            new KeyValuePair<string, Action>("untrusted-response-stream", delegate { UntrustedResponse("stream"); }),
            new KeyValuePair<string, Action>("trusted-response-owner-only", TrustedResponseWritesOnlyOwner),
            new KeyValuePair<string, Action>("untrusted-callback-validation", UntrustedCallbackValidation),
            new KeyValuePair<string, Action>("native-first-success", NativeFirstSuccess),
            new KeyValuePair<string, Action>("native-first-bind-negatives", NativeFirstBindNegatives),
            new KeyValuePair<string, Action>("native-first-pending-changes", NativeFirstPendingChanges),
            new KeyValuePair<string, Action>("native-first-visible-result", NativeFirstVisibleResult),
            new KeyValuePair<string, Action>("native-first-callback-before-gui", NativeFirstCallbackBeforeGui),
            new KeyValuePair<string, Action>("native-first-lock-deadline", NativeFirstLockDeadline),
            new KeyValuePair<string, Action>("first-training-skip", FirstTrainingSkip),
            new KeyValuePair<string, Action>("launch-report-transfer-failure", LaunchReportTransferFailure),
            new KeyValuePair<string, Action>("request-success", RequestSuccess),
            new KeyValuePair<string, Action>("initial-receipt", InitialReceipt),
            new KeyValuePair<string, Action>("embedded-packet", EmbeddedPacket),
            new KeyValuePair<string, Action>("duplicate-callback", DuplicateCallback),
            new KeyValuePair<string, Action>("rejection-then-success", RejectionThenSuccess),
            new KeyValuePair<string, Action>("rejection-limit", RejectionLimit),
            new KeyValuePair<string, Action>("duplicate-rejection", DuplicateRejection),
            new KeyValuePair<string, Action>("callback-before-dequeue", CallbackBeforeDequeue),
            new KeyValuePair<string, Action>("unknown-replies", UnknownReplies),
            new KeyValuePair<string, Action>("timeout", Timeout),
            new KeyValuePair<string, Action>("rejection-identity-gate", RejectionIdentityGate),
            new KeyValuePair<string, Action>("availability-recovers", AvailabilityRecovers),
            new KeyValuePair<string, Action>("availability-deadline", AvailabilityDeadline),
            new KeyValuePair<string, Action>("missing-lookup-response-deadline", MissingLookupResponseDeadline),
            new KeyValuePair<string, Action>("applied-cleanup-gate", AppliedCleanupGate),
            new KeyValuePair<string, Action>("identity-changes", IdentityChanges),
            new KeyValuePair<string, Action>("identity-during-callback", IdentityDuringStockCallback),
            new KeyValuePair<string, Action>("identity-before-open", IdentityBeforeOpen),
            new KeyValuePair<string, Action>("pause-stop", PauseAndStop),
            new KeyValuePair<string, Action>("foreign-handler", ForeignHandler),
            new KeyValuePair<string, Action>("unknown-replacement", UnknownReplacement),
            new KeyValuePair<string, Action>("ownership-changes", OwnershipChanges),
            new KeyValuePair<string, Action>("same-process-ownership", SameProcessOwnership),
            new KeyValuePair<string, Action>("initial-proof-negatives", InitialIdentityAndProof),
            new KeyValuePair<string, Action>("unproved-embedded", UnprovedEmbedded),
            new KeyValuePair<string, Action>("stock-callback-failure", OriginalCallbackFailure),
            new KeyValuePair<string, Action>("open-failure", OpenFailure),
            new KeyValuePair<string, Action>("presentation-failure", PresentationFailure),
            new KeyValuePair<string, Action>("missing-stock-application", MissingStockApplication),
            new KeyValuePair<string, Action>("reentrant-stop", ReentrantStop),
            new KeyValuePair<string, Action>("assembly-boundary", AssemblyBoundary)
        };
        string requested = args.Length == 0 ? "all" : args[0];
        int failures = 0, ran = 0;
        foreach (KeyValuePair<string, Action> scenario in scenarios)
        {
            if (requested != "all" && requested != scenario.Key) continue;
            ran++; int before = checks;
            try
            {
                scenario.Value();
                Console.WriteLine("{\"status\":\"PASS\",\"scenario\":" + Json(scenario.Key) + ",\"checks\":" + (checks - before) + "}");
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("{\"status\":\"FAIL\",\"scenario\":" + Json(scenario.Key) + ",\"checks\":" + (checks - before) +
                    ",\"error\":" + Json(error.ToString()) + ",\"trace\":" + Json(String.Join(",", BattleFixtureTrace.Events.ToArray())) + "}");
            }
        }
        if (ran == 0) { Console.WriteLine("{\"status\":\"FAIL\",\"error\":\"unknown scenario\"}"); return 2; }
        Console.WriteLine("{\"status\":" + Json(failures == 0 ? "PASS" : "FAIL") + ",\"scenarios\":" + ran + ",\"failures\":" + failures +
            ",\"checks\":" + checks + ",\"scope\":\"actual production BattleSettlement/BattleLifecycle/transport; detached game boundary\",\"game_access\":false}");
        return failures == 0 ? 0 : 1;
    }
}
