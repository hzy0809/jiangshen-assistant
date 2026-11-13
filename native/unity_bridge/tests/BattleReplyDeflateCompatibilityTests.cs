using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using ProtoBuf;
using game.net.data.StoC;
using Ionic.Zlib;
using JiangShen.AssistantRuntime;

// Differential protocol/library tests only. No Unity, Model, game, client or IPC.
internal static class BattleReplyDeflateCompatibilityTests
{
    private static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private sealed class Counts
    { internal int Cases, ExpectedValid, Compatibility, Safety, StockDifferent, ProducerFailures; }
    private sealed class BoundedOutput : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { if (Position + count > BattleReplyPreflight.MaxCompressedBytes) throw new FormatException("native_producer_output_bound"); base.Write(buffer, offset, count); }
        public override void WriteByte(byte value)
        { if (Position >= BattleReplyPreflight.MaxCompressedBytes) throw new FormatException("native_producer_output_bound"); base.WriteByte(value); }
    }
    private static string Hash(byte[] bytes)
    { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static string Error(Exception error)
    { while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException; return error.GetType().Name + ":" + error.Message; }
    private static object Codec(byte[] compressed)
    {
        byte[] input = new byte[compressed.Length + 1]; Buffer.BlockCopy(compressed, 0, input, 0, compressed.Length);
        ZlibCodec codec = new ZlibCodec(); int result = codec.InitializeInflate(false), calls = 0;
        int total = 0;
        try
        {
            codec.InputBuffer = input; codec.NextIn = 0; codec.AvailableBytesIn = input.Length;
            byte[] output = new byte[8192];
            while (result == 0 && calls++ < 8192)
            {
                int before = codec.AvailableBytesIn;
                codec.OutputBuffer = output; codec.NextOut = 0; codec.AvailableBytesOut = output.Length;
                result = codec.Inflate(FlushType.None); total += output.Length - codec.AvailableBytesOut;
                if (result != 0 || before == codec.AvailableBytesIn && codec.AvailableBytesOut == output.Length) break;
            }
            return new { result, calls, next_in = codec.NextIn, available_in = codec.AvailableBytesIn, total_in = codec.TotalBytesIn,
                output_bytes = total, input_bytes = compressed.Length };
        }
        catch (Exception error) { return new { error = Error(error), result, calls, next_in = codec.NextIn, available_in = codec.AvailableBytesIn, total_in = codec.TotalBytesIn }; }
        finally { codec.EndInflate(); }
    }
    private static void Probe(StreamWriter log, Counts counts, string name, string expected, string group, byte[] raw, byte[] compressed)
    {
            BattleReport reference;
            using (MemoryStream input = new MemoryStream(raw, false)) reference = Serializer.Deserialize<BattleReport>(input);
            bool stock = false, stockMatches = false, helper = false, helperMatches = false;
            string stockError = "", helperError = "";
            try
            {
                using (MemoryStream input = new MemoryStream(compressed, false))
                using (DeflateStream zip = new DeflateStream(input, CompressionMode.Decompress))
                {
                    BattleReport value = Serializer.Deserialize<BattleReport>(zip);
                    stock = true; stockMatches = BattleReplyPreflight.Matches(reference, value);
                }
            }
            catch (Exception error) { stockError = Error(error); }
            try { object value = BattleReplyPreflight.Validate(compressed); helper = true; helperMatches = BattleReplyPreflight.Matches(reference, value); }
            catch (Exception error) { helperError = Error(error); }
            bool compatibilityFailure = expected == "accept" && stockMatches && !helperMatches;
            bool safetyFailure = expected == "reject" && helper;
            if (expected == "accept") { counts.ExpectedValid++; if (!stockMatches) counts.StockDifferent++; }
            if (compatibilityFailure) counts.Compatibility++;
            if (safetyFailure) counts.Safety++;
            object record = new { @case = name, expected, group, raw_bytes = raw.Length, compressed_bytes = compressed.Length,
                compressed_sha256 = Hash(compressed), raw_sha256 = Hash(raw), stock_accepted = stock, stock_matches = stockMatches,
                stock_error = stockError, helper_accepted = helper, helper_matches = helperMatches, helper_error = helperError,
                compatibility_failure = compatibilityFailure, safety_failure = safetyFailure, codec = Codec(compressed) };
            log.WriteLine(json.Serialize(record)); counts.Cases++;
    }
    private static byte[] IonicCompress(byte[] raw, int level, CompressionStrategy strategy, FlushType flush, int buffer)
    {
        using (MemoryStream output = new BoundedOutput())
        {
            using (DeflateStream zip = new DeflateStream(output, CompressionMode.Compress, (CompressionLevel)level, true))
            {
                zip.Strategy = strategy; zip.BufferSize = buffer; zip.FlushMode = flush;
                int split = raw.Length / 2;
                zip.Write(raw, 0, split); zip.FlushMode = FlushType.None;
                zip.Write(raw, split, raw.Length - split);
            }
            return output.ToArray();
        }
    }
    private static int Main(string[] args)
    {
        Counts counts = new Counts(); Dictionary<string, byte[]> rawSamples = new Dictionary<string, byte[]>();
        using (StreamWriter log = new StreamWriter(args[1], false))
        using (StreamWriter native = new StreamWriter(args[2], false))
        {
            foreach (string line in File.ReadAllLines(args[0]))
            {
                string[] fields = line.Split('\t'); string name = fields[0], expected = fields[1], group = fields[2];
                byte[] raw = Convert.FromBase64String(fields[3]), compressed = Convert.FromBase64String(fields[4]);
                Probe(log, counts, name, expected, group, raw, compressed);
                if (expected == "accept" && group == "finished" && !rawSamples.ContainsKey(Hash(raw))) rawSamples.Add(Hash(raw), raw);
            }
            int index = 0;
            foreach (byte[] raw in rawSamples.Values)
            {
                foreach (int level in new int[] { 0, 1, 6, 9 })
                foreach (CompressionStrategy strategy in Enum.GetValues(typeof(CompressionStrategy)))
                foreach (FlushType flush in new FlushType[] { FlushType.None, FlushType.Partial, FlushType.Sync, FlushType.Full })
                foreach (int buffer in new int[] { 1024, 8192 })
                {
                    string name = "ionic-" + index + "-l" + level + "-s" + strategy + "-f" + flush + "-b" + buffer;
                    byte[] compressed;
                    try { compressed = IonicCompress(raw, level, strategy, flush, buffer); }
                    catch (Exception error)
                    {
                        counts.ProducerFailures++;
                        log.WriteLine(json.Serialize(new { @case = name, producer_failure = Error(error) }));
                        if (counts.ProducerFailures == 1) Console.WriteLine("Producer failure: " + name + " " + Error(error));
                        continue;
                    }
                    native.WriteLine(String.Join("\t", new string[] { name, "accept", "ionic-native", Convert.ToBase64String(raw), Convert.ToBase64String(compressed) }));
                    Probe(log, counts, name, "accept", "ionic-native", raw, compressed);
                }
                index++;
            }
        }
        Console.WriteLine(json.Serialize(new { status = counts.Compatibility == 0 && counts.Safety == 0 && counts.ProducerFailures == 0 ? "PASS" : "FAIL", cases = counts.Cases,
            expected_valid = counts.ExpectedValid, compatibility_failures = counts.Compatibility, safety_failures = counts.Safety,
            python_valid_but_stock_different = counts.StockDifferent, producer_failures = counts.ProducerFailures, game_access = false, network_access = false }));
        return counts.Compatibility == 0 && counts.Safety == 0 && counts.ProducerFailures == 0 ? 0 : 1;
    }
}
