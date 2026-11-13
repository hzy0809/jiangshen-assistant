using System;
using System.IO;
using JiangShen.AssistantRuntime;
using W = BattleReplyPreflightWire;

internal static partial class PointRaceBattleAdapterTests
{
    private static void AssertPointReplyBlocked(string name, byte[] packet, Action<Fixture> change = null)
    {
        Fixture f = new Fixture(); PointRaceChallenger other = new PointRaceChallenger { id = 272, pos = 2, battle_score = 200 };
        Global.character_.pointrace.challengers.Add(other);
        game.net.tcp_base.Handler slot = f.Start(); DevelopmentNormalCallback retained = slot.handler;
        if (change != null) change(f);
        f.Client.Dispatch(0x465, packet);
        for (int i = 0; i < 3; i++) f.Capture();
        string observed = name + ": callbacks=" + f.Race.CallbackCalls + ", applied=" + f.Race.Applied +
            ", gold=" + Global.character_.gold + ", bought=" + Global.character_.pointrace.challenge_buy +
            ", otherKilled=" + other.killed + ", state=" + f.Model.Json();
        Check(f.Race.CallbackCalls == 0 && f.Race.Applied == 0 && Global.character_.pointrace.challenge_count == 0 &&
            Global.character_.gold == 100 && Global.character_.pointrace.challenge_buy == 0 && !other.killed &&
            !((PointRaceChallenger)Global.character_.pointrace.challengers[0]).killed, observed);
        Check(f.Model.Json().Contains("\"status\":\"UNKNOWN\"") && f.Battle.OpenCalls == 0 && f.Race.Returns == 0 &&
            f.Client.Sent.Count == 1 && f.Client.EndRequests == 0, "invalid PointRace reply stays UNKNOWN without presentation/return/reissue: " + name);
        retained(new MemoryStream(Receipt(true), false), null); f.Capture();
        Check(f.Race.CallbackCalls == 0 && !f.CanQueue("challenge") && !f.CanQueue("skip"),
            "invalid PointRace reply cannot be repaired by a later callback or retry: " + name);
    }
    private static void PointReplyWrongOpponent() { AssertPointReplyBlocked("well-formed other opponent", W.Point(272, 272)); }
    private static void PointReplyPremium() { AssertPointReplyBlocked("well-formed premium", W.Point(271, 271, 987, 5)); }
    private static void PointReplyNegatives()
    {
        AssertPointReplyBlocked("mismatched report opponent", W.Point(271, 272));
        AssertPointReplyBlocked("mismatched top opponent", W.Point(272));
        AssertPointReplyBlocked("unknown result", W.N(1, 99));
        AssertPointReplyBlocked("missing identity", W.Join(W.N(1, 0), W.M(4, new byte[] { 8, 1 })));
        AssertPointReplyBlocked("zero top identity", W.Point(0));
        AssertPointReplyBlocked("zero report identity", W.Point(271, 0));
        AssertPointReplyBlocked("zero report ID", W.Point(271, 271, 0));
        AssertPointReplyBlocked("duplicate result", W.Join(W.Point(), W.N(1, 0)));
        AssertPointReplyBlocked("duplicate gold", W.Join(W.Point(), W.N(8, 0)));
        AssertPointReplyBlocked("duplicate opponent", W.Join(W.Point(), W.N(10, 271)));
        AssertPointReplyBlocked("duplicate envelope", W.Join(W.Point(), W.Envelope(271, 987)));
        AssertPointReplyBlocked("duplicate nested report", W.Join(W.N(1, 0), W.M(4, new byte[] { 8, 1 }),
            W.M(5, W.M(1, W.N(1, 271), W.N(2, 987), W.N(2, 988)), W.M(2, new byte[] { 66 })), W.N(10, 271)));
        AssertPointReplyBlocked("truncated field", W.Join(W.Point(), new byte[] { 64, 128 }));
        AssertPointReplyBlocked("bad known wire", W.Join(W.Point(), W.M(8, new byte[] { 0 })));
        AssertPointReplyBlocked("unknown field", W.Join(W.Point(), W.N(11, 0)));
        AssertPointReplyBlocked("missing required envelope name", W.Join(W.N(1, 0), W.M(4, new byte[] { 8, 1 }), W.Envelope(271, 987, false), W.N(10, 271)));
        AssertPointReplyBlocked("malformed nested cooldown", W.Join(W.Point(), W.M(6, new byte[] { 128 })));
        AssertPointReplyBlocked("oversize", new byte[BattleReplyPreflight.MaxPacketBytes + 1]);
        AssertPointReplyBlocked("in-place target identity changed", W.Point(), delegate(Fixture f) { ((PointRaceChallenger)Global.character_.pointrace.challengers[0]).id = 273; });
        AssertPointReplyBlocked("bad compressed report", W.Point(271, 271, 987, 0, true, true, new byte[] { 8, 1 }));
        byte[] good = W.Report(1), cut = new byte[W.Report(1).Length - 1]; Array.Copy(good, cut, cut.Length);
        AssertPointReplyBlocked("truncated compressed report", W.Point(271, 271, 987, 0, true, true, cut));
        AssertPointReplyBlocked("compressed report trailing bytes", W.Point(271, 271, 987, 0, true, true, W.Join(good, new byte[] { 0 })));
        AssertPointReplyBlocked("report result conflicts with win", W.Point(271, 271, 987, 0, true, true, W.Report(2)));
        AssertPointReplyBlocked("unknown report result", W.Point(271, 271, 987, 0, true, true, W.Report(99)));
        AssertPointReplyBlocked("missing report army", W.Point(271, 271, 987, 0, true, true, W.Compress(W.N(3, 1))));
        AssertPointReplyBlocked("mutated callback arg", W.Point(), delegate(Fixture f) { ((game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x465]).arg = new object(); });
    }
    private static void PointReplyReportMutation()
    {
        Fixture f = new Fixture(); f.Start(); f.Client.Dispatch(0x465, W.Point());
        ((game.net.data.StoC.BattleReport)f.Battle.interpreter.m_report).result_status = game.net.data.StoC.BattleReport.ResultStatus.LEFT_LOSE;
        f.Capture();
        Check(f.Model.Json().Contains("\"status\":\"UNKNOWN\"") && f.Battle.OpenCalls == 0 && f.Race.Returns == 0 && f.Race.Applied == 1,
            "same report object mutation after a valid callback cannot present a conflicting result");
    }
    private static void PointReplyValidVariants()
    {
        foreach (bool win in new bool[] { true, false })
        {
            Fixture f = new Fixture(); game.net.tcp_base.Handler slot = f.Start();
            MemoryStream stream = new MemoryStream(W.Point(271, 271, 987, 0, win, false), false);
            f.Client.m_handlers.Remove((ushort)0x465); slot.handler(stream, null); f.Capture();
            Check(ReferenceEquals(stream, f.Race.CallbackStream) && f.Race.CallbackArg == null && f.Race.CallbackPosition == 0,
                "PointRace preflight retains original stream, arg and cursor");
            Check(f.Race.CallbackCalls == 1 && f.Race.Applied == 1 && Global.character_.gold == 100 &&
                ((PointRaceChallenger)Global.character_.pointrace.challengers[0]).killed == win &&
                f.Model.Json().Contains("\"won\":" + (win ? "true" : "false")), "PointRace omitted zero/default false retain actual result and target effect");
            f.Action("skip"); f.Capture(); f.Action("battle_close");
            Check(f.Client.EndRequests == 0 && f.Race.Returns == 1 && ReferenceEquals(BaseAsset.lds.currmg, f.Scene),
                "PointRace valid callback retains original mode return without 0x408");
        }
    }
}
