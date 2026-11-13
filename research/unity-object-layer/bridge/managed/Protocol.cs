using System;
using System.Globalization;

namespace JiangShen.ManagedProbe
{
    // Numeric observation only; equality is independent of UI success and cannot
    // establish resource-consumption or network causality.
    internal sealed class ResourceObservation
    {
        private readonly string[] names;
        private readonly long[] before;
        private readonly bool[] equalAtEverySample;
        internal int Samples;
        internal ResourceObservation(string[] fieldNames, long[] baseline)
        {
            if (fieldNames == null || baseline == null || fieldNames.Length != baseline.Length) throw new ArgumentException("resource_schema");
            names = (string[])fieldNames.Clone(); before = (long[])baseline.Clone();
            equalAtEverySample = new bool[baseline.Length];
            for (int i = 0; i < baseline.Length; i++) equalAtEverySample[i] = true;
            Samples = 1;
        }
        internal void Observe(long[] current)
        {
            Require(current);
            for (int i = 0; i < before.Length; i++) if (before[i] != current[i]) equalAtEverySample[i] = false;
            Samples++;
        }
        internal bool FinalEqual(long[] current)
        {
            Require(current);
            for (int i = 0; i < before.Length; i++) if (before[i] != current[i]) return false;
            return true;
        }
        internal bool EverySampleEqual()
        {
            for (int i = 0; i < before.Length; i++) if (!equalAtEverySample[i]) return false;
            return true;
        }
        internal string Json(long[] current)
        {
            Require(current);
            System.Text.StringBuilder b = new System.Text.StringBuilder("{");
            for (int i = 0; i < before.Length; i++)
            {
                if (i != 0) b.Append(',');
                b.Append(Command.Quote(names[i])); b.Append(":{\"before\":"); b.Append(before[i].ToString(CultureInfo.InvariantCulture));
                b.Append(",\"after\":"); b.Append(current[i].ToString(CultureInfo.InvariantCulture));
                b.Append(",\"final_equal\":"); b.Append(before[i] == current[i] ? "true" : "false");
                b.Append(",\"every_sample_equal\":"); b.Append(equalAtEverySample[i] ? "true" : "false");
                b.Append('}');
            }
            b.Append('}'); return b.ToString();
        }
        private void Require(long[] current)
        {
            if (current == null || current.Length != before.Length) throw new ArgumentException("resource_schema");
        }
    }

    // Deliberately no JSON dependency and no free-form reflection/RPC parameters.
    internal sealed class Command
    {
        internal int Sequence;
        internal string Name;

        internal static bool ValidNonce(string nonce)
        {
            if (nonce == null || nonce.Length != 32) return false;
            for (int i = 0; i < nonce.Length; i++)
                if (!(nonce[i] >= '0' && nonce[i] <= '9') && !(nonce[i] >= 'a' && nonce[i] <= 'f')) return false;
            return true;
        }

        internal static Command Parse(string text, string nonce, int previous)
        {
            if (!ValidNonce(nonce) || text == null || text.Length > 256) return null;
            text = text.Replace("\r\n", "\n");
            if (text.EndsWith("\n")) text = text.Substring(0, text.Length - 1);
            string[] lines = text.Split(new char[] { '\n' });
            if (lines.Length != 4 || lines[0] != "version=1" || lines[1] != "nonce=" + nonce) return null;
            const string prefix = "sequence=";
            if (!lines[2].StartsWith(prefix)) return null;
            string digits = lines[2].Substring(prefix.Length);
            if (digits.Length == 0 || digits.Length > 9 || digits[0] == '0') return null;
            for (int i = 0; i < digits.Length; i++) if (digits[i] < '0' || digits[i] > '9') return null;
            int sequence = Int32.Parse(digits, CultureInfo.InvariantCulture);
            if (sequence <= previous || !lines[3].StartsWith("command=")) return null;
            string name = lines[3].Substring(8);
            if (name != "diagnose" && name != "snapshot" && name != "disable-restore" && name != "stop") return null;
            Command result = new Command();
            result.Sequence = sequence;
            result.Name = name;
            return result;
        }

        internal static string Quote(string value)
        {
            if (value == null) return "null";
            System.Text.StringBuilder b = new System.Text.StringBuilder("\"");
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\') { b.Append('\\'); b.Append(c); }
                else if (c < 32 || c > 126) b.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            b.Append('"');
            return b.ToString();
        }
    }
}
