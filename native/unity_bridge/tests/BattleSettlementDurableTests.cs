using System;
using System.IO;
using JiangShen.AssistantRuntime;

internal static partial class BattleLifecycleTests
{
    private static NativeBattleStartTicket Durable(Fixture f)
    {
        NativeBattleStartStore.TestRoot = Path.Combine(Path.GetTempPath(), "settlement-durable-" + Guid.NewGuid().ToString("N"));
        NativeBattleStartStore.TestFault = "";
        NativeBattleStartStore.InitializeSession("00000000000000000000000000000052");
        NativeBattleStartTicket ticket = NativeBattleStartStore.Reserve(78, f.Mode.ToString(), Operation);
        BattleLaunch.Ticket = ticket;
        return ticket;
    }
    private static void Pending(NativeBattleStartTicket ticket)
    {
        NativeBattleStartStore.RequirePending(ticket);
        Throws(delegate { NativeBattleStartStore.Reserve(ticket.RoleId, ticket.Mode, ticket.OperationId); }, "prior_pending");
        Check(!File.Exists(Path.Combine(ticket.Directory, ticket.Generation + ".confirmed")), "pending has no fabricated confirmation");
    }
    private static NativeBattleStartTicket Next(NativeBattleStartTicket previous)
    {
        Check(File.Exists(Path.Combine(previous.Directory, previous.Generation + ".confirmed")), "verified callback durably confirmed exact generation");
        NativeBattleStartTicket next = NativeBattleStartStore.Reserve(previous.RoleId, previous.Mode, previous.OperationId);
        Check(next.Generation != previous.Generation && next.OperationId == previous.OperationId, "same operation creates a new launch generation");
        return next;
    }
    private static void Embed(Fixture f)
    {
        f.Battle.bUseBattleEnd = false;
        f.Battle.m_pktBattleEnd = new PendingBattleEnd { pkt = new EndPacket { _result = 0, _idcampaign = 12,
            _idtroop = 7, _idbattle_report = 987, _iswin = true } };
    }
    private static void DurableRequestSuccess()
    {
        foreach (bool stop in new bool[] { false, true })
        {
            Fixture f = new Fixture(BattleMode.Campaign); NativeBattleStartTicket ticket = Durable(f); f.Start();
            Pending(ticket); // ReportBound is a transfer, never a terminal proof.
            game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
            if (stop) Check(!f.Owner.Stop(), "pending native request retained through Stop");
            f.Client.Dispatch(0x408, End(true, 0));
            NativeBattleStartTicket next = Next(ticket);
            using (MemoryStream duplicate = new MemoryStream(End(true, 0))) slot.handler(duplicate, null);
            NativeBattleStartStore.RequirePending(next);
            Throws(delegate { NativeBattleStartStore.Reserve(78, "Campaign", Operation); }, "prior_pending");
            Check(f.Battle.CallbackCalls == 1 && State(f).Error == "", "old duplicate callback cannot modify later native generation");
        }
    }
    private static void DurableEmbeddedSuccess()
    {
        Fixture f = new Fixture(BattleMode.Campaign); NativeBattleStartTicket ticket = Durable(f); Embed(f);
        f.Advance(); Next(ticket);
        Check(State(f).Applied && f.Battle.BattleEndCalls == 1 && f.Client.EndRequests == 0, "only verified embedded stock application confirms");
    }
    private static void DurableSettledSuccess()
    {
        foreach (BattleMode mode in new BattleMode[] { BattleMode.Arena, BattleMode.PointRace, BattleMode.OwnDemon })
        {
            Fixture f = new Fixture(mode); NativeBattleStartTicket ticket = Durable(f);
            Check(f.PresentSettled(), "exact initial settled mode bound"); Next(ticket);
            Check(State(f).Applied && f.Client.EndRequests == 0, "settled initial receipt confirms without an end request");
        }
    }
    private static void DurableConfirmationFaults()
    {
        foreach (string path in new string[] { "request", "embedded", "arena", "point", "demon" })
        foreach (string fault in new string[] { "before-write", "after-flush" })
        {
            BattleMode mode = path == "arena" ? BattleMode.Arena : path == "point" ? BattleMode.PointRace :
                path == "demon" ? BattleMode.OwnDemon : BattleMode.Campaign;
            Fixture f = new Fixture(mode); NativeBattleStartTicket ticket = Durable(f);
            if (path == "request") f.Start(); else if (path == "embedded") Embed(f);
            NativeBattleStartStore.TestFault = fault;
            Throws(delegate
            {
                if (path == "request") f.Client.Dispatch(0x408, End(true, 0));
                else if (path == "embedded") f.Advance(); else f.PresentSettled();
            }, "durable_confirmation");
            NativeBattleStartStore.TestFault = "";
            Check(State(f).Error == "battle_durable_confirmation_unverifiable_no_retry", "confirmation failure is sticky UNKNOWN after stock return");
            Check(f.Owner.BlocksGuiInvocation && !f.Owner.Stop() && f.Battle.OpenCalls == 0, "failed durable confirmation prevents GUI/result opening");
            int sent = f.Client.EndRequests;
            Throws(f.Advance, "durable_confirmation");
            Throws(delegate { new BattleSettlement(delegate(string name, string fields) { }, delegate { return true; }); }, "prior_owner_unresolved");
            Throws(delegate { NativeBattleStartStore.Reserve(78, mode.ToString(), Operation); },
                fault == "after-flush" ? "unfinished_or_unknown" : "prior_pending");
            Check(f.Client.EndRequests == sent && f.Battle.OpenCalls == 0 &&
                !File.Exists(Path.Combine(ticket.Directory, ticket.Generation + ".confirmed")), "UNKNOWN has no additional send, open, or confirmed marker");
        }
    }
    private static void DurableUnknowns()
    {
        foreach (string kind in new string[] { "timeout", "bad-packet", "stock-did-not-apply", "stock-throws" })
        {
            Fixture f = new Fixture(BattleMode.Campaign); NativeBattleStartTicket ticket = Durable(f); f.Start();
            if (kind == "stock-did-not-apply") f.Battle.SkipStockApply = true;
            if (kind == "stock-throws") f.Battle.ThrowStockCallback = true;
            try { f.Client.Dispatch(0x408, kind == "timeout" ? null : kind == "bad-packet" ? new byte[] { 8, 7 } : End(true, 0)); }
            catch (Exception) { }
            Pending(ticket); Check(f.Battle.OpenCalls == 0 && State(f).Error != "", "unknown native result never confirms or opens");
        }
    }
}
