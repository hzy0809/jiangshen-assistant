using System;
using System.Collections.Generic;
using System.IO;
using Ionic.Zlib;

// Hand-built protobuf wire frames grounded in pinned pbdata field numbers.
// The minimal battle schema and compression use pinned real dependencies.
// The armies, client, server and IPC are fake; no live acceptance is exercised.
internal static class BattleReplyPreflightWire
{
    internal static byte[] Join(params byte[][] parts)
    { List<byte> value = new List<byte>(); foreach (byte[] part in parts) value.AddRange(part); return value.ToArray(); }
    private static void Number(List<byte> bytes, ulong value)
    { while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; } bytes.Add((byte)value); }
    internal static byte[] N(int tag, ulong value)
    { List<byte> bytes = new List<byte>(); Number(bytes, (ulong)(tag << 3)); Number(bytes, value); return bytes.ToArray(); }
    internal static byte[] M(int tag, params byte[][] parts)
    {
        byte[] body = Join(parts); List<byte> bytes = new List<byte>();
        Number(bytes, (ulong)((tag << 3) | 2)); Number(bytes, (ulong)body.Length); bytes.AddRange(body); return bytes.ToArray();
    }
    internal static byte[] Envelope(ulong opponent, ulong report, bool name = true)
    { return M(5, M(1, N(1, opponent), N(2, report)), name ? M(2, new byte[] { 66 }) : new byte[0]); }
    internal static byte[] Compress(byte[] report)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (DeflateStream zip = new DeflateStream(stream, CompressionMode.Compress, true)) zip.Write(report, 0, report.Length);
            return stream.ToArray();
        }
    }
    internal static byte[] Report(uint result)
    {
        byte[] army = Join(M(1, new byte[] { 65 }), N(2, 2), N(3, 0), N(4, 0));
        return Compress(Join(M(1, army), M(2, army), N(3, result), N(7, 3)));
    }
    internal static byte[] Arena(ulong opponent = 271, ulong report = 987, uint gold = 0, bool win = true, bool goldPresent = true, byte[] compressed = null)
    {
        return Join(N(1, 0), win ? N(2, 1) : new byte[0], M(4, compressed ?? Report(win ? 1u : 2u)),
            Envelope(opponent, report), goldPresent ? N(8, gold) : new byte[0], N(10, 987));
    }
    internal static byte[] Point(ulong opponent = 271, ulong reportOpponent = 271, ulong report = 987, uint gold = 0, bool win = true, bool goldPresent = true, byte[] compressed = null)
    {
        return Join(N(1, 0), win ? N(2, 1) : new byte[0], M(4, compressed ?? Report(win ? 1u : 2u)),
            Envelope(reportOpponent, report), goldPresent ? N(8, gold) : new byte[0], N(10, opponent));
    }
}
