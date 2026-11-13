using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ProtoBuf;
using game.net.data.StoC;
using JiangShen.AssistantRuntime;
using W = BattleReplyPreflightWire;

// Pure protocol/library tests: no Model/Unity/client/guard substitute is linked.
internal static class BattleReplyPreflightDecoderTests
{
    private static int checks;
    private static void Check(bool condition, string why) { checks++; if (!condition) throw new Exception(why); }
    private static void Reject(Action action, string reason)
    {
        try { action(); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Check(reason == "" || error.Message.Contains(reason), "expected " + reason + ", got " + error); return;
        }
        throw new Exception("invalid input accepted: " + reason);
    }
    private static byte[] Army()
    { return W.Join(W.M(1, new byte[] { 65 }), W.N(2, 2), W.N(3, 0), W.N(4, 0)); }
    private static byte[] Report(uint status)
    { return W.Join(W.M(1, Army()), W.M(2, Army()), W.N(3, status), W.N(7, 3)); }
    private static BattleReport Stock(byte[] compressed)
    {
        using (MemoryStream input = new MemoryStream(compressed, false))
        using (Ionic.Zlib.DeflateStream zip = new Ionic.Zlib.DeflateStream(input, Ionic.Zlib.CompressionMode.Decompress))
            return Serializer.Deserialize<BattleReport>(zip);
    }
    private static void ValidReports()
    {
        foreach (uint status in new uint[] { 1, 2, 3 })
        {
            byte[] compressed = W.Compress(Report(status));
            object detached = BattleReplyPreflight.Validate(compressed, status == 1);
            BattleReport stock = Stock(compressed);
            Check(detached is BattleReport && (uint)stock.result_status == status &&
                BattleReplyPreflight.Matches(detached, stock), "real client inflater and detached decode agree on win/loss/draw");
            Check(BattleReplyPreflight.Matches(BattleReplyPreflight.Validate(compressed), stock),
                "absent outer outcome accepts the report's own valid win/loss/draw");
            Reject(delegate { BattleReplyPreflight.Validate(compressed, status != 1); }, "report_result_changed");
        }
        SCArenaChallenge_0x460 arena = (SCArenaChallenge_0x460)BattleReplyPreflight.Deserialize(W.Arena(271, 987, 0, false, false), "game.net.data.StoC.SCArenaChallenge_0x460");
        SCPointraceChallenge_0x465 point = (SCPointraceChallenge_0x465)BattleReplyPreflight.Deserialize(W.Point(271, 271, 987, 0, false, false), "game.net.data.StoC.SCPointraceChallenge_0x465");
        Check(!arena.iswin && arena.gold_consume == 0 && !point.iswin && point.gold_consume == 0, "optional native false/zero defaults preserved");
    }
    private static void NoOuterOutcome()
    {
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(Report(99))); }, "unknown_enum");
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(W.M(1, Army()), W.M(2, Army())))); }, "required_field_missing");
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(Report(1), W.N(3, 2)))); }, "duplicate_field");
        byte[] compressed = W.Report(1), cut = new byte[compressed.Length - 1];
        Array.Copy(compressed, cut, cut.Length);
        Reject(delegate { BattleReplyPreflight.Validate(cut); }, "");
        Reject(delegate { BattleReplyPreflight.Validate(W.Join(compressed, new byte[] { 0 })); }, "compressed_trailing_or_empty");
        Reject(delegate { BattleReplyPreflight.Validate(new byte[BattleReplyPreflight.MaxCompressedBytes + 1]); }, "compressed_bound");
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(new byte[BattleReplyPreflight.MaxInflatedBytes + 1])); }, "inflated_bound");
    }
    private static void RequiredFields()
    {
        byte[][] fields = { W.M(1, Army()), W.M(2, Army()), W.N(3, 1) };
        for (int missing = 0; missing < fields.Length; missing++)
        {
            List<byte> body = new List<byte>(); for (int i = 0; i < fields.Length; i++) if (i != missing) body.AddRange(fields[i]);
            byte[] compressed = W.Compress(body.ToArray());
            Reject(delegate { BattleReplyPreflight.Validate(compressed, true); }, "required_field_missing");
        }
        byte[][] army = { W.M(1, new byte[] { 65 }), W.N(2, 2), W.N(3, 0), W.N(4, 0) };
        for (int missing = 0; missing < army.Length; missing++)
        {
            List<byte> body = new List<byte>(); for (int i = 0; i < army.Length; i++) if (i != missing) body.AddRange(army[i]);
            byte[] compressed = W.Compress(W.Join(W.M(1, body.ToArray()), W.M(2, Army()), W.N(3, 1)));
            Reject(delegate { BattleReplyPreflight.Validate(compressed, true); }, "required_field_missing");
        }
    }
    private static void SchemaNegatives()
    {
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(Report(1), W.N(3, 1))), true); }, "duplicate_field");
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(Report(1), W.N(99, 0))), true); }, "unknown_field");
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(Report(1), W.M(3, new byte[] { 1 }))), true); }, "duplicate_field");
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(W.M(1, Army()), W.M(2, Army()), W.M(3, new byte[] { 1 }))), true); }, "bad_wire");
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(Report(99)), true); }, "unknown_enum");
        byte[] badBoolArmy = W.Join(W.M(1, new byte[] { 65 }), W.N(2, 2), W.N(3, 2), W.N(4, 0));
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(W.M(1, badBoolArmy), W.M(2, Army()), W.N(3, 1))), true); }, "scalar_overflow");
        byte[] overflowArmy = W.Join(W.M(1, new byte[] { 65 }), W.N(2, 0x100000000UL), W.N(3, 0), W.N(4, 0));
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(W.M(1, overflowArmy), W.M(2, Army()), W.N(3, 1))), true); }, "scalar_overflow");
        byte[] invalidUtf8 = W.Join(W.M(1, new byte[] { 255 }), W.N(2, 2), W.N(3, 0), W.N(4, 0));
        Reject(delegate { BattleReplyPreflight.Validate(W.Compress(W.Join(W.M(1, invalidUtf8), W.M(2, Army()), W.N(3, 1))), true); }, "");
        Reject(delegate { BattleReplyPreflight.Deserialize(W.Join(W.N(1, 0), W.N(8, 0x100000000UL)), "game.net.data.StoC.SCArenaChallenge_0x460"); }, "scalar_overflow");
        Reject(delegate { BattleReplyPreflight.Deserialize(new byte[] { 8, 128 }, "game.net.data.StoC.SCArenaChallenge_0x460"); }, "truncated");
        Reject(delegate { BattleReplyPreflight.Deserialize(W.Join(W.N(1, 0), W.M(5, W.M(1, W.N(1, 271), W.N(2, 987)))), "game.net.data.StoC.SCArenaChallenge_0x460"); }, "required_field_missing");
        Reject(delegate { BattleReplyPreflight.Deserialize(W.N(1, 0), "BattleReport"); }, "packet_bound_or_type");
        // Repeated turns are legitimate; singular duplicate rejection must not
        // accidentally reject the protocol's repeated collection semantics.
        object repeated = BattleReplyPreflight.Validate(W.Compress(W.Join(Report(1), W.M(4), W.M(4))), true);
        Check(((BattleReport)repeated).game_turns.Count == 2, "repeated messages remain valid");
    }
    private static void CompressionBoundaries()
    {
        // Independently captured from pinned Ionic.DeflateStream.Compress in the
        // Demon fixture. Its final EOB needs unused lookup lookahead in Ionic.
        string[] hex = "E3-12-E0-E2-C8-2F-CF-D3-2D-CA-CF-49-15-B0-91-60-50-60-12-12-E4-E2-04-89-A4-A4-E6-E6-E7-01-85-18-15-98-24-18-01".Split('-');
        byte[] vector = new byte[hex.Length]; for (int i = 0; i < vector.Length; i++) vector[i] = Convert.ToByte(hex[i], 16);
        Check(BattleReplyPreflight.Matches(BattleReplyPreflight.Validate(vector, true), Stock(vector)), "pinned client EOB lookahead vector retains exact decoded content");
        for (int length = 0; length < vector.Length; length++)
        {
            byte[] prefix = new byte[length]; Array.Copy(vector, prefix, length);
            Reject(delegate { BattleReplyPreflight.Validate(prefix, true); }, "");
        }
        Reject(delegate { BattleReplyPreflight.Validate(W.Join(vector, new byte[] { 0 }), true); }, "compressed_trailing_or_empty");
        Reject(delegate { BattleReplyPreflight.Validate(W.Join(vector, vector), true); }, "compressed_trailing_or_empty");
        Reject(delegate { BattleReplyPreflight.Validate(new byte[BattleReplyPreflight.MaxCompressedBytes + 1], true); }, "compressed_bound");
        byte[] bomb = W.Compress(new byte[BattleReplyPreflight.MaxInflatedBytes + 1]);
        Reject(delegate { BattleReplyPreflight.Validate(bomb, true); }, "inflated_bound");
        List<byte> excessive = new List<byte>(Report(1)); for (int i = 0; i < 65536; i++) excessive.AddRange(W.M(4));
        byte[] excessiveCompressed = W.Compress(excessive.ToArray());
        Reject(delegate { BattleReplyPreflight.Validate(excessiveCompressed, true); }, "field_bound");
    }
    private static void CanonicalReadback()
    {
        object expected = BattleReplyPreflight.Validate(W.Report(1), true);
        BattleReport actual = Stock(W.Report(1));
        Check(BattleReplyPreflight.Matches(expected, actual), "separate native objects match canonically");
        actual.result_status = BattleReport.ResultStatus.LEFT_LOSE;
        Check(!BattleReplyPreflight.Matches(expected, actual), "same actual object mutation breaks proof");
        Check(!BattleReplyPreflight.Matches(expected, new object()) && !BattleReplyPreflight.Matches(expected, null), "wrong/null native objects cannot match");
    }
    private static int Main()
    {
        KeyValuePair<string, Action>[] cases = {
            new KeyValuePair<string, Action>("real_schema_win_loss_draw_and_defaults", ValidReports),
            new KeyValuePair<string, Action>("report_without_outer_outcome_remains_bounded", NoOuterOutcome),
            new KeyValuePair<string, Action>("root_and_nested_required_presence", RequiredFields),
            new KeyValuePair<string, Action>("wire_scalar_enum_utf8_duplicate_unknown", SchemaNegatives),
            new KeyValuePair<string, Action>("native_ionic_eob_truncation_trailing_bombs", CompressionBoundaries),
            new KeyValuePair<string, Action>("native_canonical_readback", CanonicalReadback)
        };
        int failures = 0;
        foreach (KeyValuePair<string, Action> test in cases)
        {
            int before = checks;
            try { test.Value(); Console.WriteLine("{\"scenario\":\"" + test.Key + "\",\"status\":\"PASS\",\"checks\":" + (checks - before) + "}"); }
            catch (Exception error) { failures++; Console.WriteLine("{\"scenario\":\"" + test.Key + "\",\"status\":\"FAIL\",\"error\":\"" + error.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"}"); }
        }
        Console.WriteLine("{\"status\":\"" + (failures == 0 ? "PASS" : "FAIL") + "\",\"scenarios\":" + cases.Length + ",\"checks\":" + checks + ",\"failed\":" + failures + ",\"game_access\":false,\"network_access\":false}");
        return failures == 0 ? 0 : 1;
    }
}
