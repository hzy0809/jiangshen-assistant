using System;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;
using JiangShen.AssistantRuntime;
using NativeEnd = game.net.data.StoC.SCBattleEnd_0x408;
using NativeReport = game.net.data.StoC.BattleReport;

internal static partial class BattleLifecycleTests
{
    private static byte[] NativeSettlement(bool win, uint result = 0, BattleMode mode = BattleMode.Campaign)
    {
        NativeEnd packet = new NativeEnd { result = result, type = mode == BattleMode.Trial ? 2u : mode == BattleMode.Grave ? 4u :
            mode == BattleMode.TrainingMonster ? 7u : 0u, idcampaign = 12, idtroop = 7, iswin = win,
            idbattle_report = result == 0 ? 987UL : 0UL, star_level = 3 };
        using (MemoryStream stream = new MemoryStream()) { Serializer.Serialize(stream, packet); return stream.ToArray(); }
    }
    private static byte[] JoinSettlement(params byte[][] parts)
    { List<byte> bytes = new List<byte>(); foreach (byte[] part in parts) bytes.AddRange(part); return bytes.ToArray(); }
    private static void AssertSettlementBlocked(Fixture f, byte[] body, string why)
    {
        f.Start();
        game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
        f.Client.Dispatch(0x408, body);
        CampaignTroop target = (CampaignTroop)f.Target; CampaignUI source = (CampaignUI)f.Source;
        Check(f.Battle.CallbackCalls == 0 && f.Battle.BattleEndCalls == 0 && source.Applied == 0 &&
            target.star_level == 0 && target.finish_count == 0 && !target.killed && source.m_troopWin == null,
            why + ": stock=" + f.Battle.CallbackCalls + ", apply=" + source.Applied + ", stars=" + target.star_level +
            ", finished=" + target.finish_count + ", killed=" + target.killed + ", state=" + State(f).Error);
        Check(!State(f).Applied && State(f).Error != "" && f.Battle.bUseBattleEnd && f.Battle.bNeedWaitForBattleEnd &&
            BattleSettlement.AppliedResponse(f.Battle, f.Report) == null, "unproved success cannot become authoritative: " + why);
        string originalError = State(f).Error;
        using (MemoryStream later = new MemoryStream(NativeSettlement(true), false)) slot.handler(later, null);
        Throws(f.Advance, originalError);
        Check(State(f).Error == originalError && f.Battle.CallbackCalls == 0 && f.Client.EndRequests == 1 &&
            f.Battle.OpenCalls == 0 && f.Battle.ExitCalls == 0 && !f.Owner.Stop(), "unproved success remains sticky UNKNOWN without retry/open/return: " + why);
    }
    private static void SettlementReportMismatch(uint reportResult, bool packetWin)
    {
        Fixture f = new Fixture(BattleMode.Campaign, reportResult);
        byte[] body = NativeSettlement(packetWin);
        NativeEnd native;
        using (MemoryStream input = new MemoryStream(body, false)) native = Serializer.Deserialize<NativeEnd>(input);
        Check(native.result == 0 && native.iswin == packetWin && native.idcampaign == 12 && native.idtroop == 7 && native.idbattle_report == 987,
            "real pinned protobuf packet is shaped for the exact current owner");
        AssertSettlementBlocked(f, body, "report result " + reportResult + " conflicts with packet win=" + packetWin);
    }
    private static void SettlementReportUnknown()
    {
        foreach (uint reportResult in new uint[] { 0, 99 })
            AssertSettlementBlocked(new Fixture(BattleMode.Campaign, reportResult), NativeSettlement(false), "unknown report result " + reportResult);
    }
    private static void SettlementEmptyTreasure()
    {
        // SCBattleEnd tag10=bonuses -> Treasures tag1=Treasure. Empty Treasure
        // lacks required idtreasure_type/param0/param1/total_value. The pinned
        // native protobuf parser accepts it, so object construction is no proof.
        byte[] body = JoinSettlement(NativeSettlement(true), new byte[] { 82, 2, 10, 0 });
        NativeEnd loose;
        using (MemoryStream input = new MemoryStream(body, false)) loose = Serializer.Deserialize<NativeEnd>(input);
        Check(loose.bonuses != null && loose.bonuses.bonuses.Count == 1 && loose.bonuses.bonuses[0].idtreasure_type == 0,
            "native pinned parser demonstrably accepts an empty required Treasure");
        AssertSettlementBlocked(new Fixture(BattleMode.Campaign), body, "empty required Treasure");
    }
    private static void SettlementSchemaNegatives()
    {
        byte[][] invalid = {
            new byte[] { 56, 1, 56, 2 }, // duplicate optional exp_get
            new byte[] { 50, 1, 1 }, // singular iswin with wrong wire
            new byte[] { 48, 2 }, // native bool accepts nonzero; pinned strict schema does not
            new byte[] { 152, 6, 0 }, // unknown field99
            new byte[] { 82, 1, 128 } // truncated nested bonuses
        };
        for (int i = 0; i < invalid.Length; i++)
            AssertSettlementBlocked(new Fixture(BattleMode.Campaign), JoinSettlement(NativeSettlement(true), invalid[i]), "full success schema negative " + i);
    }
    private static void SettlementOptionalDefaults()
    {
        // db_character_treasure differs from Treasure: all fields are optional.
        // Its empty nested object is schema-valid and must not be over-rejected.
        byte[] body = JoinSettlement(NativeSettlement(false), new byte[] { 90, 0 });
        NativeEnd native;
        using (MemoryStream input = new MemoryStream(body, false)) native = Serializer.Deserialize<NativeEnd>(input);
        Check(!native.iswin && native.treasures.Count == 1 && native.treasures[0].idtreasure_type == 0,
            "native optional iswin and nested db defaults remain false/zero");
        Fixture f = new Fixture(BattleMode.Campaign, 3); f.Start(); f.Client.Dispatch(0x408, body); f.Advance();
        Check(f.Battle.CallbackCalls == 1 && State(f).Applied && State(f).Error == "" && f.Battle.OpenCalls == 1 &&
            !((CampaignUI)f.Source).LastWin, "valid optional defaults and draw are forwarded once");
    }
    private static void SettlementEmbeddedConsistency()
    {
        foreach (uint status in new uint[] { 1, 2, 3 })
        {
            Fixture f = new Fixture(BattleMode.Campaign, status); f.Battle.bUseBattleEnd = false;
            f.Battle.m_pktBattleEnd = new PendingBattleEnd { pkt = new EndPacket { _result = 0, _type = 0, _idcampaign = 12,
                _idtroop = 7, _idbattle_report = 987, _iswin = status != 1, _star_level = 3 } };
            try { f.Advance(); } catch (ProbeFailure) { }
            CampaignTroop target = (CampaignTroop)f.Target;
            Check(f.Battle.BattleEndCalls == 0 && ((CampaignUI)f.Source).Applied == 0 && target.star_level == 0 &&
                target.finish_count == 0 && !target.killed && f.Client.EndRequests == 0 && f.Battle.OpenCalls == 0 &&
                f.Battle.interpreter.FinalStateCalls == 0 && State(f) != null && State(f).Error != "",
                "embedded semantic mismatch is blocked before every effect: report=" + status + ", apply=" + ((CampaignUI)f.Source).Applied);
        }
        foreach (uint status in new uint[] { 1, 2, 3 })
        {
            Fixture f = new Fixture(BattleMode.Campaign, status); f.Battle.bUseBattleEnd = false;
            f.Battle.m_pktBattleEnd = new PendingBattleEnd { pkt = new EndPacket { _result = 0, _idcampaign = 12,
                _idtroop = 7, _idbattle_report = 987, _iswin = status == 1 } };
            f.Advance(); f.Advance();
            Check(f.Battle.BattleEndCalls == 1 && ((CampaignUI)f.Source).Applied == 1 && ((CampaignUI)f.Source).LastWin == (status == 1) &&
                f.Client.EndRequests == 0 && f.Battle.OpenCalls == 1 && State(f).Applied, "matching embedded win/loss/draw applies once without wire-presence assumptions");
        }
    }
    private static void SettlementMatchingResults()
    {
        foreach (BattleMode mode in new BattleMode[] { BattleMode.Campaign, BattleMode.Trial, BattleMode.Grave, BattleMode.TrainingMonster })
        foreach (uint status in new uint[] { 1, 2, 3 })
        {
            Fixture f = new Fixture(mode, status); f.Start();
            byte[] body = NativeSettlement(status == 1, 0, mode);
            using (MemoryStream input = new MemoryStream(body, false))
                Check(Serializer.Deserialize<NativeEnd>(input).iswin == (status == 1), "real native default false represents loss/draw");
            f.Client.Dispatch(0x408, body);
            Check(f.Battle.CallbackCalls == 1 && ReferenceEquals(f.Battle.CallbackStream, f.Client.DispatchedStream) &&
                f.Battle.CallbackPosition == 0 && f.Battle.CallbackArg == null && f.Battle.BattleEndCalls == 1 &&
                State(f).Applied && State(f).Error == "", "matching native win/loss/draw retains unchanged callback exactly once: " + mode);
            f.Advance(); f.Advance();
            Check(f.Client.EndRequests == 1 && f.Battle.OpenCalls == 1 && State(f).Opened, "matching result opens once without extra end request");
            f.ReturnToOrigin();
        }
    }
    private static void SettlementNonwinRejectionRetry()
    {
        foreach (uint status in new uint[] { 2, 3 })
        {
            Fixture f = new Fixture(BattleMode.Campaign, status); f.Start();
            // Required result=1 only. All optional fields retain native defaults.
            f.Client.Dispatch(0x408, new byte[] { 8, 1 }); f.Advance();
            Check(f.Battle.CallbackCalls == 0 && !State(f).Applied && State(f).Error == "" && f.Client.EndRequests == 1 &&
                f.Battle.bUseBattleEnd && f.Battle.bNeedWaitForBattleEnd, "explicit rejection stays pending/backed off for a non-win report");
            SetField(Field(f.Owner, "attempt"), "RetryTick", unchecked(Environment.TickCount - 60000)); f.Advance();
            Check(f.Client.EndRequests == 2, "only explicit result1 permits the existing bounded retry");
            f.Client.Dispatch(0x408, NativeSettlement(false)); f.Advance();
            Check(f.Battle.CallbackCalls == 1 && f.Battle.BattleEndCalls == 1 && !((CampaignUI)f.Source).LastWin &&
                State(f).Applied && State(f).Error == "" && f.Battle.OpenCalls == 1, "later matching loss/draw settles once with omitted optional iswin");
        }
    }
    private static void AssertMalformedRejectionBlocked(byte[] body, string why, bool afterRetry = false)
    {
        Fixture f = new Fixture(BattleMode.Campaign); f.Start();
        if (afterRetry)
        {
            f.Client.Dispatch(0x408, new byte[] { 8, 1 });
            SetField(Field(f.Owner, "attempt"), "RetryTick", unchecked(Environment.TickCount - 60000)); f.Advance();
            Check(f.Client.EndRequests == 2 && State(f).Error == "", "valid first rejection permits only its next request");
        }
        game.net.tcp_base.Handler slot = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x408];
        f.Client.Dispatch(0x408, body);
        object attempt = Field(f.Owner, "attempt"); bool authorized = (bool)Field(attempt, "Retry");
        string error = State(f).Error;
        SetField(attempt, "RetryTick", unchecked(Environment.TickCount - 60000));
        string advanceError = "";
        try { f.Advance(); } catch (ProbeFailure failure) { advanceError = failure.Message; }
        int expectedRequests = afterRetry ? 2 : 1;
        Check(!authorized && error != "" && advanceError == error && f.Client.EndRequests == expectedRequests &&
            f.Battle.CallbackCalls == 0 && f.Battle.BattleEndCalls == 0 && !State(f).Applied,
            why + ": retry_authorized=" + authorized + ", requests=" + f.Client.EndRequests +
            ", stock=" + f.Battle.CallbackCalls + ", state=" + error);
        using (MemoryStream duplicate = new MemoryStream(new byte[] { 8, 1 }, false)) slot.handler(duplicate, null);
        Check(State(f).Error == error && !(bool)Field(attempt, "Retry") && f.Battle.OpenCalls == 0 && f.Battle.ExitCalls == 0 &&
            !f.Owner.Stop() && BattleSettlement.AppliedResponse(f.Battle, f.Report) == null,
            "malformed rejection remains UNKNOWN without retry/stock/presentation/return: " + why);
    }
    private static void SettlementMalformedRejectionTreasure()
    {
        // Independent review's exact counterexample: result1 followed by an
        // empty Treasure missing all four required fields. Native accepts it.
        byte[] body = { 0x08, 0x01, 0x52, 0x02, 0x0a, 0x00 };
        NativeEnd native;
        using (MemoryStream stream = new MemoryStream(body, false)) native = Serializer.Deserialize<NativeEnd>(stream);
        Check(native.result == 1 && native.bonuses != null && native.bonuses.bonuses.Count == 1,
            "real pinned native protobuf accepts the malformed rejection Treasure");
        AssertMalformedRejectionBlocked(body, "080152020A00 missing required Treasure");
    }
    private static void SettlementMalformedRejectionSchema()
    {
        byte[][] packets = {
            new byte[] { 8, 1, 56, 0, 56, 0 }, // duplicate non-identity exp_get
            new byte[] { 8, 1, 82, 0, 82, 0 }, // duplicate optional bonuses envelope
            new byte[] { 8, 1, 48, 2 }, // bool outside native schema
            new byte[] { 8, 1, 152, 6, 0 }, // unknown field99
            new byte[] { 8, 1, 82, 1, 128 }, // truncated nested protobuf
            new byte[] { 8, 1, 16, 128, 128, 128, 128, 16 }, // uint32 type overflow
            new byte[] { 8, 1, 8, 1 }, // duplicate required result
            new byte[] { 8, 1, 18, 0 }, // wrong wire for type
            new byte[] { 8, 2 }, // unknown result is not rejection
            new byte[] { 8, 255, 255, 255, 255, 15 }, // UINT_MAX result is not rejection
            new byte[65537] // callback response byte bound
        };
        for (int i = 0; i < packets.Length; i++) AssertMalformedRejectionBlocked(packets[i], "rejection schema negative " + i);
    }
    private static void SettlementMalformedRejectionAfterRetry()
    { AssertMalformedRejectionBlocked(new byte[] { 8, 1, 82, 2, 10, 0 }, "malformed rejection after a valid first retry", true); }
    private static void SettlementRejectionDefaults()
    {
        byte[][] packets = {
            new byte[] { 8, 1 }, // required result only
            new byte[] { 8, 1, 16, 0, 24, 0, 32, 0, 40, 0, 48, 0, 136, 1, 0 }, // explicit optional zero defaults
            new byte[] { 8, 1, 82, 0 }, // empty optional Treasures list (no Treasure)
            new byte[] { 8, 1, 90, 0 } // db_character_treasure has no required fields
        };
        foreach (byte[] body in packets)
        {
            Fixture f = new Fixture(BattleMode.Campaign); f.Start(); f.Client.Dispatch(0x408, body); f.Advance();
            object attempt = Field(f.Owner, "attempt");
            Check((bool)Field(attempt, "Retry") && State(f).Error == "" && !State(f).Applied &&
                f.Client.EndRequests == 1 && f.Battle.CallbackCalls == 0 && f.Battle.bUseBattleEnd && f.Battle.bNeedWaitForBattleEnd,
                "valid schema rejection authorizes only the existing bounded backoff without optional identity/win presence");
            SetField(attempt, "RetryTick", unchecked(Environment.TickCount - 60000)); f.Advance();
            Check(f.Client.EndRequests == 2 && f.Battle.CallbackCalls == 0, "valid minimal/default rejection retains one authorized retry");
            f.Client.Dispatch(0x408, NativeSettlement(true)); f.Advance();
            Check(f.Battle.CallbackCalls == 1 && State(f).Applied && f.Battle.OpenCalls == 1 && State(f).Error == "",
                "later authoritative matching success settles normally after a schema-valid rejection");
        }
    }
}
