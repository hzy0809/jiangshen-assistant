// Offline executable only: do not include this file in JSUnityProbe.dll.
using System;
namespace JiangShen.ManagedProbe
{
    internal static class ProtocolTests
    {
        private static int checks;
        private static void Check(bool pass, string name)
        {
            checks++;
            if (!pass) throw new Exception("FAIL " + name);
        }
        public static int Main()
        {
            try { return Run(); }
            catch (Exception e) { Console.WriteLine(e.Message); return 1; }
        }
        private static int Run()
        {
            const string nonce = "0123456789abcdef0123456789abcdef";
            string prefix = "version=1\nnonce=" + nonce + "\nsequence=1\ncommand=";
            Check(Command.ValidNonce(nonce), "nonce");
            Check(!Command.ValidNonce(nonce.ToUpperInvariant()), "upper_nonce_rejected");
            Check(!Command.ValidNonce(""), "empty_nonce_rejected");
            string[] commands = new string[] { "diagnose", "snapshot", "disable-restore", "stop" };
            for (int i = 0; i < commands.Length; i++) Check(Command.Parse(prefix + commands[i], nonce, 0).Name == commands[i], "whitelist_" + commands[i]);
            Check(Command.Parse(prefix + "snapshot\n", nonce, 0) != null, "one_trailing_newline");
            Check(Command.Parse((prefix + "snapshot\n").Replace("\n", "\r\n"), nonce, 0) != null, "crlf");
            Check(Command.Parse(prefix + "snapshot", nonce, 1) == null, "replay_rejected");
            Check(Command.Parse(prefix + "snapshot", nonce, 2) == null, "old_sequence_rejected");
            Check(Command.Parse(prefix.Replace("sequence=1", "sequence=2") + "snapshot", nonce, 1) != null, "next_sequence");
            Check(Command.Parse(prefix + "arbitrary-reflection", nonce, 0) == null, "unknown_command");
            Check(Command.Parse(prefix + "disable-restore\nmethod=Send", nonce, 0) == null, "extra_argument");
            Check(Command.Parse(prefix + "toggle-restore", nonce, 0) == null, "retired_luoshu_command_rejected");
            Check(Command.Parse(prefix.Replace("version=1", "version=2") + "snapshot", nonce, 0) == null, "wrong_version");
            Check(Command.Parse(prefix.Replace(nonce, "ffffffffffffffffffffffffffffffff") + "snapshot", nonce, 0) == null, "wrong_nonce");
            string[] bad = new string[] { "0", "01", "-1", "+1", " 1", "1 ", "1.0", "1000000000", "2147483648", "", "\r1" };
            for (int i = 0; i < bad.Length; i++) Check(Command.Parse(prefix.Replace("sequence=1", "sequence=" + bad[i]) + "snapshot", nonce, 0) == null, "bad_sequence_" + i);
            Check(Command.Parse(prefix + "snapshot\n\n", nonce, 0) == null, "extra_line");
            Check(Command.Parse(new string('a', 257), nonce, 0) == null, "max_length");
            Check(Command.Parse(null, nonce, 0) == null, "null");
            Check(Command.Quote("\"\\\n\u4e2d") == "\"\\\"\\\\\\u000a\\u4e2d\"", "json_escape");
            ResourceObservation resources = new ResourceObservation(new string[] { "gold", "practice", "money64" }, new long[] { 450, 45, 56926588 });
            resources.Observe(new long[] { 450, 45, 56926588 });
            Check(resources.FinalEqual(new long[] { 450, 45, 56926588 }) && resources.EverySampleEqual(), "resources_unchanged");
            resources.Observe(new long[] { 450, 46, 56926588 });
            Check(!resources.FinalEqual(new long[] { 450, 46, 56926588 }) && !resources.EverySampleEqual(), "practice_accrual_is_separate_equality_failure");
            Check(resources.Json(new long[] { 450, 46, 56926588 }).Contains("\"practice\":{\"before\":45,\"after\":46,\"final_equal\":false,\"every_sample_equal\":false}"), "perfield_delta_visible");
            resources.Observe(new long[] { 450, 45, 56926588 });
            Check(resources.FinalEqual(new long[] { 450, 45, 56926588 }) && !resources.EverySampleEqual(), "temporary_change_not_hidden_by_reversion");
            Check(resources.Json(new long[] { 450, 45, 56926588 }).Contains("\"gold\":{\"before\":450,\"after\":450,\"final_equal\":true,\"every_sample_equal\":true}"), "unchanged_fields_explicit");
            ResourceObservation extremes = new ResourceObservation(new string[] { "money64" }, new long[] { Int64.MinValue });
            extremes.Observe(new long[] { Int64.MaxValue });
            Check(!extremes.FinalEqual(new long[] { Int64.MaxValue }) && extremes.Json(new long[] { Int64.MaxValue }).Contains(Int64.MaxValue.ToString()), "long_values_no_delta_overflow");
            bool rejected = false;
            try { resources.Observe(new long[0]); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "resource_schema_rejected");
            Console.WriteLine("PASS " + checks + " protocol/resource checks; no game code loaded or Unity API called");
            return 0;
        }
    }
}
