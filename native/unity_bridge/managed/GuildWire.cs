using System;

namespace JiangShen.AssistantRuntime
{
    // Pinned pbdata: 0x12d has required bool and six signed ZigZag ints;
    // 0x12e has a required uint reward ID, with 255 meaning rejection.
    internal sealed class GuildReceipt
    {
        internal int Result, Money, Gold;
        internal bool Accepted;

        internal static GuildReceipt Read(int opcode, byte[] body)
        {
            Cursor c = new Cursor(body, 0, body.Length);
            int field, wire, seen = 0;
            uint[] values = new uint[8];
            int max = opcode == 301 ? 7 : 1;
            while (c.Tag(out field, out wire))
            {
                if (field <= max)
                {
                    if (wire != 0 || (seen & 1 << field) != 0) throw new FormatException("guild_receipt_field");
                    values[field] = c.Uint(); seen |= 1 << field;
                }
                else c.Skip(wire);
            }
            if (opcode == 301 && seen == 254 && values[1] <= 1)
                return new GuildReceipt { Result = (int)values[1], Accepted = values[1] == 1, Money = Signed(values[5]), Gold = Signed(values[6]) };
            if (opcode == 302 && seen == 2 && values[1] <= Int32.MaxValue)
                return new GuildReceipt { Result = (int)values[1], Accepted = values[1] != 255 };
            throw new FormatException("guild_receipt_schema");
        }

        internal bool ExactQuote(int type)
        { return Money == (type == 1 ? 1000 : type == 2 ? 100000 : 0) && Gold == (type == 3 ? 80 : 0); }

        private static int Signed(uint zigzag) { return (int)(zigzag >> 1) ^ -((int)zigzag & 1); }

        // 0x163.acts is repeated field 1; in each row ID is field 1 and
        // condition_param is field 2 (omitted when zero). Ignore other rows.
        internal static int? Progress(byte[] body, int target)
        {
            int ignored;
            return Progress(body, target, false, out ignored);
        }

        internal static int? Progress(byte[] body, int target, out int refresh)
        { return Progress(body, target, true, out refresh); }

        private static int? Progress(byte[] body, int target, bool readRefresh, out int refresh)
        {
            Cursor c = new Cursor(body, 0, body.Length);
            int field, wire, rows = 0;
            int? progress = null;
            refresh = 0;
            while (c.Tag(out field, out wire))
            {
                if (field != 1) { c.Skip(wire); continue; }
                if (wire != 2 || ++rows > 255) throw new FormatException("guild_progress_bound");
                Cursor row = c.Child();
                uint id = 0, value = 0, rowRefresh = 0;
                int seen = 0;
                while (row.Tag(out field, out wire))
                {
                    if (field != 1 && field != 2 && (!readRefresh || field != 3)) { row.Skip(wire); continue; }
                    if (wire != 0 || (seen & 1 << field) != 0) throw new FormatException("guild_progress_field");
                    if (field == 1) id = row.Uint();
                    else if (field == 2) value = row.Uint();
                    else rowRefresh = row.Uint();
                    seen |= 1 << field;
                }
                if (id == target)
                {
                    if (progress.HasValue || value > 255) throw new FormatException("guild_progress_target");
                    progress = (int)value;
                    refresh = Signed(rowRefresh);
                }
            }
            return progress;
        }

        private sealed class Cursor
        {
            private readonly byte[] bytes;
            private readonly int end;
            private int position;
            internal Cursor(byte[] body, int start, int limit) { bytes = body; position = start; end = limit; }
            internal bool Tag(out int field, out int wire)
            {
                field = wire = 0;
                if (position == end) return false;
                uint tag = Uint(); field = (int)(tag >> 3); wire = (int)(tag & 7);
                if (field == 0) throw new FormatException("guild_packet_tag");
                return true;
            }
            internal uint Uint() { return checked((uint)Varint()); }
            private ulong Varint()
            {
                ulong value = 0;
                for (int shift = 0; shift <= 63; shift += 7)
                {
                    if (position >= end) break;
                    byte b = bytes[position++];
                    if (shift == 63 && b > 1) break;
                    value |= (ulong)(b & 127) << shift;
                    if ((b & 128) == 0) return value;
                }
                throw new FormatException("guild_packet_varint");
            }
            private int Advance(int length)
            {
                if (length < 0 || length > end - position) throw new FormatException("guild_packet_length");
                int start = position; position += length; return start;
            }
            internal Cursor Child() { int size = checked((int)Uint()); return new Cursor(bytes, Advance(size), position); }
            internal void Skip(int wire)
            {
                if (wire == 0) Varint();
                else if (wire == 1) Advance(8);
                else if (wire == 2) Advance(checked((int)Uint()));
                else if (wire == 5) Advance(4);
                else throw new FormatException("guild_packet_wire");
            }
        }
    }
}
