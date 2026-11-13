using System;
using System.IO;
using JiangShen.AssistantRuntime;
using W = BattleReplyPreflightWire;

internal static partial class BattleModeAdapterTests
{
    private static void AssertArenaReplyBlocked(string name, byte[] packet, Action<Fixture> change = null)
    {
        Fixture f = new Fixture(false); game.net.tcp_base.Handler slot = f.StartArena();
        DevelopmentNormalCallback retained = slot.handler;
        if (change != null) change(f);
        f.Client.Dispatch(0x460, packet);
        for (int i = 0; i < 3; i++) f.CaptureArena();
        string observed = name + ": callbacks=" + f.Arena.CallbackCalls + ", applied=" + f.Arena.Applied +
            ", gold=" + Global.character_.gold + ", bought=" + Global.character_.arena.challenge_buy +
            ", reportOpponent=" + f.Arena.LastReportOpponent + ", state=" + f.ArenaModel.Json();
        Check(f.Arena.CallbackCalls == 0 && f.Arena.Applied == 0 && Global.character_.arena.challenge_count == 0 &&
            Global.character_.gold == 100 && Global.character_.arena.challenge_buy == 0, observed);
        Check(f.ArenaModel.OutcomeUnknown && f.Battle.OpenCalls == 0 && f.Arena.Returns == 0 &&
            f.Client.Sent.Count == 2 && f.Client.EndRequests == 0, "invalid Arena reply stays UNKNOWN without presentation/return/reissue: " + name);
        retained(new MemoryStream(ArenaReceipt(), false), null); f.CaptureArena();
        string why;
        Check(f.Arena.CallbackCalls == 0 && f.ArenaModel.OutcomeUnknown &&
            !f.ArenaModel.Queue(9, 78, Operation, 99, f.ArenaModel.Revision, "challenge", out why) &&
            !f.ArenaModel.Queue(9, 78, Operation, 100, f.ArenaModel.Revision, "skip", out why),
            "invalid Arena reply cannot be repaired by a later callback or retry: " + name);
    }
    private static void ArenaReplyWrongOpponent() { AssertArenaReplyBlocked("well-formed other opponent", W.Arena(272)); }
    private static void ArenaReplyPremium() { AssertArenaReplyBlocked("well-formed premium", W.Arena(271, 987, 5)); }
    private static void ArenaReplyNegatives()
    {
        AssertArenaReplyBlocked("conflicting report IDs", W.Arena(271, 988));
        AssertArenaReplyBlocked("unknown result", W.Join(W.N(1, 99), W.N(8, 0)));
        AssertArenaReplyBlocked("unverified rejection may replace challengers", W.N(1, 1));
        AssertArenaReplyBlocked("missing identity", W.Join(W.N(1, 0), W.M(4, new byte[] { 8, 1 }), W.N(10, 987)));
        AssertArenaReplyBlocked("zero identity", W.Arena(0));
        AssertArenaReplyBlocked("zero envelope report", W.Arena(271, 0));
        AssertArenaReplyBlocked("duplicate gold", W.Join(W.Arena(), W.N(8, 0)));
        AssertArenaReplyBlocked("duplicate result", W.Join(W.Arena(), W.N(1, 0)));
        AssertArenaReplyBlocked("duplicate report", W.Join(W.Arena(), W.N(10, 987)));
        AssertArenaReplyBlocked("duplicate envelope", W.Join(W.Arena(), W.Envelope(271, 987)));
        AssertArenaReplyBlocked("duplicate nested identity", W.Join(W.N(1, 0), W.M(4, new byte[] { 8, 1 }),
            W.M(5, W.M(1, W.N(1, 271), W.N(1, 272), W.N(2, 987)), W.M(2, new byte[] { 66 })), W.N(10, 987)));
        AssertArenaReplyBlocked("truncated field", W.Join(W.Arena(), new byte[] { 64, 128 }));
        AssertArenaReplyBlocked("bad known wire", W.Join(W.Arena(), W.M(8, new byte[] { 0 })));
        AssertArenaReplyBlocked("unknown field", W.Join(W.Arena(), W.N(11, 0)));
        AssertArenaReplyBlocked("missing required envelope name", W.Join(W.N(1, 0), W.M(4, new byte[] { 8, 1 }), W.Envelope(271, 987, false), W.N(10, 987)));
        AssertArenaReplyBlocked("malformed nested cooldown", W.Join(W.Arena(), W.M(6, new byte[] { 128 })));
        AssertArenaReplyBlocked("oversize", new byte[BattleReplyPreflight.MaxPacketBytes + 1]);
        AssertArenaReplyBlocked("in-place target identity changed", W.Arena(), delegate(Fixture f) { ((ArenaChallenger)Global.character_.arena.challengers[0]).Id = 272; });
        AssertArenaReplyBlocked("bad compressed report", W.Arena(271, 987, 0, true, true, new byte[] { 8, 1 }));
        byte[] good = W.Report(1), cut = new byte[W.Report(1).Length - 1]; Array.Copy(good, cut, cut.Length);
        AssertArenaReplyBlocked("truncated compressed report", W.Arena(271, 987, 0, true, true, cut));
        AssertArenaReplyBlocked("compressed report trailing bytes", W.Arena(271, 987, 0, true, true, W.Join(good, new byte[] { 0 })));
        AssertArenaReplyBlocked("report result conflicts with win", W.Arena(271, 987, 0, true, true, W.Report(2)));
        AssertArenaReplyBlocked("unknown report result", W.Arena(271, 987, 0, true, true, W.Report(99)));
        AssertArenaReplyBlocked("missing report army", W.Arena(271, 987, 0, true, true, W.Compress(W.N(3, 1))));
        AssertArenaReplyBlocked("mutated callback arg", W.Arena(), delegate(Fixture f) { ((game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x460]).arg = new object(); });
    }
    private static void ArenaReplyReportMutation()
    {
        Fixture f = new Fixture(false); f.StartArena(); f.Client.Dispatch(0x460, W.Arena());
        ((game.net.data.StoC.BattleReport)f.Battle.interpreter.m_report).result_status = game.net.data.StoC.BattleReport.ResultStatus.LEFT_LOSE;
        f.CaptureArena();
        Check(f.ArenaModel.OutcomeUnknown && f.Battle.OpenCalls == 0 && f.Arena.Returns == 0 && f.Arena.Applied == 1,
            "same report object mutation after a valid callback cannot present a conflicting result");
    }
    private static void ArenaReplyValidVariants()
    {
        foreach (bool win in new bool[] { true, false })
        {
            Fixture f = new Fixture(false); game.net.tcp_base.Handler slot = f.StartArena();
            byte[] rewards = W.M(9, W.M(1, W.N(1, 4), W.N(2, 0), W.N(3, 200), W.N(4, 200)),
                W.M(1, W.N(1, 18), W.N(2, 0), W.N(3, 20), W.N(4, 20)));
            MemoryStream stream = new MemoryStream(W.Join(W.Arena(271, 987, 0, win, false), rewards), false);
            f.Client.m_handlers.Remove((ushort)0x460); slot.handler(stream, null); f.CaptureArena();
            Check(ReferenceEquals(stream, f.Arena.CallbackStream) && f.Arena.CallbackArg == null && f.Arena.CallbackPosition == 0,
                "Arena preflight retains exact stream, arg and cursor for stock callback");
            Check(f.Arena.CallbackCalls == 1 && f.Arena.Applied == 1 && Global.character_.gold == 100 &&
                Global.character_.arena.money == 100 && Global.character_.arena.reputation == 10 &&
                f.ArenaModel.Json().Contains("\"won\":" + (win ? "true" : "false")), "Arena optional zero defaults, result and rewards preserved");
            f.ArenaAction("skip", true); f.CaptureArena(); f.ArenaAction("battle_close", true); f.CaptureArena();
            f.CheckOrigin(f.Arena);
            Check(f.Client.EndRequests == 0 && f.Arena.Returns == 1 && f.Arena.CallbackCalls == 1, "valid Arena reply retains original return without 0x408");
        }
    }
}
