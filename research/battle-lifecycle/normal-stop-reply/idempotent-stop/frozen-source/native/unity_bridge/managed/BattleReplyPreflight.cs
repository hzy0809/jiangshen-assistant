using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace JiangShen.AssistantRuntime
{
    // Detached, bounded checks only. No client, Unity object or stock callback
    // is touched. A report has no character/report ID: callers must still bind
    // its enclosing receipt and current request before admitting side effects.
    internal static class BattleReplyPreflight
    {
        internal const int MaxPacketBytes = 4 * 1024 * 1024;
        internal const int MaxCompressedBytes = 4 * 1024 * 1024;
        internal const int MaxInflatedBytes = 16 * 1024 * 1024;
        private const int MaxFields = 65536, MaxDepth = 32;
        private static readonly object sync = new object();
        private static readonly Dictionary<Assembly, bool> pinned = new Dictionary<Assembly, bool>();
        private static readonly Dictionary<Type, Schema> schemas = new Dictionary<Type, Schema>();
        private static readonly UTF8Encoding utf8 = new UTF8Encoding(false, true);

        internal static object Deserialize(byte[] body, string protocolTypeName)
        {
            if (body == null || body.Length == 0 || body.Length > MaxPacketBytes ||
                protocolTypeName == null || !protocolTypeName.StartsWith("game.net.data.StoC.", StringComparison.Ordinal))
                throw new FormatException("battle_reply_packet_bound_or_type");
            return Decode(body, ProtocolType(protocolTypeName));
        }

        internal static object Validate(byte[] compressed, bool expectedWin)
        {
            object report = Validate(compressed);
            int result = Convert.ToInt32(Value(report, "result_status"), CultureInfo.InvariantCulture);
            // Both callers pass isopponent:false. BattleUI.getBattleLevel maps
            // LEFT_WIN=1 to attacker victory, LEFT_LOSE=2 to defeat, DRAW=3 to
            // level 5. A draw is a valid non-win; an unknown value is not proof.
            if (expectedWin != (result == 1))
                throw new FormatException("battle_reply_report_result_changed");
            return report;
        }

        // Some native initial replies do not echo their optional outcome. The
        // report remains authoritative: validate one detached decode without
        // inventing an absent outer win/lose value or inflating it twice.
        internal static object Validate(byte[] compressed)
        {
            byte[] body = Inflate(compressed);
            object report = Decode(body, ProtocolType("game.net.data.StoC.BattleReport"));
            int result = Convert.ToInt32(Value(report, "result_status"), CultureInfo.InvariantCulture);
            if (result < 1 || result > 3) throw new FormatException("battle_reply_report_result_changed");
            return report;
        }

        internal static bool Matches(object expected, object actual)
        {
            if (expected == null || actual == null || expected.GetType() != actual.GetType()) return false;
            byte[] left = Canonical(expected), right = Canonical(actual);
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static Type ProtocolType(string name)
        {
            return Dependency("pbdata", "905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab").GetType(name, true);
        }

        private static Type SerializerType()
        {
            return Dependency("protobuf-net", "e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b").GetType("ProtoBuf.Serializer+NonGeneric", true);
        }

        private static Assembly Dependency(string name, string expected)
        {
            Assembly selected = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != name) continue;
                if (selected != null && !ReferenceEquals(selected, assembly)) throw new FormatException("battle_reply_dependency_ambiguous:" + name);
                selected = assembly;
            }
            // Normal CLR binding uses the installed game's managed libraries;
            // the selected file must still pass the exact pin before any decode.
            if (selected == null) selected = Assembly.Load(name);
            Pin(selected, name, expected); return selected;
        }

        private static void Pin(Assembly assembly, string name, string expected)
        {
            lock (sync)
            {
                if (pinned.ContainsKey(assembly)) return;
                if (assembly.GetName().Name != name || String.IsNullOrEmpty(assembly.Location))
                    throw new FormatException("battle_reply_dependency_changed");
                byte[] hash;
                using (FileStream stream = File.OpenRead(assembly.Location))
                using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(stream);
                string actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new FormatException("battle_reply_dependency_hash_changed:" + name);
                pinned.Add(assembly, true);
            }
        }

        private static object Decode(byte[] bytes, Type type)
        {
            if (bytes.Length == 0 || bytes.Length > MaxInflatedBytes) throw new FormatException("battle_reply_decoded_bound");
            int remaining = MaxFields;
            Scan(new Cursor(bytes, 0, bytes.Length), type, 0, ref remaining);
            MethodInfo deserialize = SerializerType().GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(Type), typeof(Stream) }, null);
            if (deserialize == null) throw new FormatException("battle_reply_serializer_changed");
            using (MemoryStream stream = new MemoryStream(bytes, false))
            {
                object value = deserialize.Invoke(null, new object[] { type, stream });
                if (value == null || value.GetType() != type || stream.Position != stream.Length)
                    throw new FormatException("battle_reply_detached_decode_incomplete");
                return value;
            }
        }

        private static byte[] Canonical(object value)
        {
            MethodInfo serialize = SerializerType().GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(Stream), typeof(object) }, null);
            if (serialize == null) throw new FormatException("battle_reply_serializer_changed");
            using (MemoryStream stream = new MemoryStream())
            {
                serialize.Invoke(null, new object[] { stream, value });
                if (stream.Length > MaxInflatedBytes) throw new FormatException("battle_reply_canonical_bound");
                return stream.ToArray();
            }
        }

        private static byte[] Inflate(byte[] compressed)
        {
            if (compressed == null || compressed.Length == 0 || compressed.Length > MaxCompressedBytes)
                throw new FormatException("battle_reply_compressed_bound");
            Type codecType = Dependency("Ionic.Zlib", "799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30").GetType("Ionic.Zlib.ZlibCodec", true);
            Type flushType = codecType.Assembly.GetType("Ionic.Zlib.FlushType", true);
            MethodInfo initialize = codecType.GetMethod("InitializeInflate", new Type[] { typeof(bool) });
            MethodInfo inflate = codecType.GetMethod("Inflate", new Type[] { flushType });
            MethodInfo end = codecType.GetMethod("EndInflate", Type.EmptyTypes);
            object codec = Activator.CreateInstance(codecType);
            // DeflateStream in BattleReportInterpreter2 uses raw DEFLATE, with
            // no RFC1950 wrapper. The codec additionally exposes STREAM_END,
            // so a truncated or trailing stream cannot pass as successful EOF.
            if (initialize == null || inflate == null || end == null ||
                (int)initialize.Invoke(codec, new object[] { false }) != 0)
                throw new FormatException("battle_reply_inflater_changed");
            try
            {
                // This pinned Ionic Huffman decoder can demand a full lookup
                // byte even when the final shorter EOB code is already present.
                // Supply one lookahead byte, then require it to remain entirely
                // unconsumed at STREAM_END. It cannot repair a truncated stream.
                byte[] input = new byte[compressed.Length + 1];
                Buffer.BlockCopy(compressed, 0, input, 0, compressed.Length);
                Set(codec, "InputBuffer", input); Set(codec, "NextIn", 0); Set(codec, "AvailableBytesIn", input.Length);
                byte[] buffer = new byte[8192]; object flush = Enum.Parse(flushType, "None");
                using (MemoryStream output = new MemoryStream())
                {
                    while (true)
                    {
                        int before = (int)Value(codec, "AvailableBytesIn");
                        Set(codec, "OutputBuffer", buffer); Set(codec, "NextOut", 0); Set(codec, "AvailableBytesOut", buffer.Length);
                        int result = (int)inflate.Invoke(codec, new object[] { flush });
                        int count = buffer.Length - (int)Value(codec, "AvailableBytesOut");
                        if (count < 0 || output.Length + count > MaxInflatedBytes) throw new FormatException("battle_reply_inflated_bound");
                        output.Write(buffer, 0, count);
                        int available = (int)Value(codec, "AvailableBytesIn");
                        if (result == 1)
                        {
                            if (available != 1 || (int)Value(codec, "NextIn") != compressed.Length ||
                                (long)Value(codec, "TotalBytesIn") != compressed.Length || output.Length == 0)
                                throw new FormatException("battle_reply_compressed_trailing_or_empty");
                            return output.ToArray();
                        }
                        if (result != 0 || before == available && count == 0)
                            throw new FormatException("battle_reply_compressed_invalid_or_truncated");
                    }
                }
            }
            finally { end.Invoke(codec, null); }
        }

        private static object Value(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null) throw new FormatException("battle_reply_schema_member_missing:" + name);
            return property.GetValue(instance, null);
        }
        private static void Set(object instance, string name, object value)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) throw new FormatException("battle_reply_codec_member_missing:" + name);
            field.SetValue(instance, value);
        }

        private sealed class Member
        {
            internal int Tag;
            internal bool Required, Repeated;
            internal string Format;
            internal Type Type;
        }
        private sealed class Schema
        {
            internal readonly Dictionary<int, Member> Members = new Dictionary<int, Member>();
        }
        private static Schema GetSchema(Type type)
        {
            lock (sync)
            {
                Schema schema;
                if (schemas.TryGetValue(type, out schema)) return schema;
                schema = new Schema();
                foreach (MemberInfo info in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
                {
                    PropertyInfo property = info as PropertyInfo; FieldInfo field = info as FieldInfo;
                    if (property == null && field == null) continue;
                    foreach (object attribute in info.GetCustomAttributes(false))
                    {
                        if (attribute.GetType().FullName != "ProtoBuf.ProtoMemberAttribute") continue;
                        Member member = new Member { Tag = (int)Value(attribute, "Tag"),
                            Required = (bool)Value(attribute, "IsRequired"), Format = Value(attribute, "DataFormat").ToString(),
                            Type = property == null ? field.FieldType : property.PropertyType };
                        if (member.Type != typeof(byte[]) && member.Type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(member.Type))
                        {
                            member.Repeated = true;
                            if (member.Type.IsArray) member.Type = member.Type.GetElementType();
                            else if (member.Type.IsGenericType && member.Type.GetGenericArguments().Length == 1)
                                member.Type = member.Type.GetGenericArguments()[0];
                            else throw new FormatException("battle_reply_collection_schema_changed");
                        }
                        if (schema.Members.ContainsKey(member.Tag)) throw new FormatException("battle_reply_schema_tag_duplicate");
                        schema.Members.Add(member.Tag, member);
                    }
                }
                if (schema.Members.Count == 0) throw new FormatException("battle_reply_schema_missing");
                schemas.Add(type, schema); return schema;
            }
        }

        private static bool Message(Type type)
        { return !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(byte[]); }
        private static int Wire(Member member)
        {
            Type type = member.Type;
            if (type == typeof(string) || type == typeof(byte[]) || Message(type)) return 2;
            if (type == typeof(float)) return 5;
            if (type == typeof(double)) return 1;
            if (member.Format == "FixedSize") return type == typeof(long) || type == typeof(ulong) ? 1 : 5;
            return 0;
        }
        private static void Tick(ref int remaining)
        { if (--remaining < 0) throw new FormatException("battle_reply_field_bound"); }

        private static void Scan(Cursor cursor, Type type, int depth, ref int remaining)
        {
            if (depth > MaxDepth) throw new FormatException("battle_reply_depth_bound");
            Schema schema = GetSchema(type); Dictionary<int, bool> seen = new Dictionary<int, bool>();
            while (!cursor.End)
            {
                Tick(ref remaining); ulong tag = cursor.Number();
                if (tag >> 3 == 0 || tag >> 3 > 536870911) throw new FormatException("battle_reply_bad_tag");
                Member member; int number = (int)(tag >> 3), wire = (int)(tag & 7);
                if (!schema.Members.TryGetValue(number, out member)) throw new FormatException("battle_reply_unknown_field");
                if (!member.Repeated && seen.ContainsKey(number)) throw new FormatException("battle_reply_duplicate_field");
                seen[number] = true;
                int expected = Wire(member);
                if (member.Repeated && expected != 2 && wire == 2)
                {
                    Cursor packed = cursor.Message();
                    while (!packed.End) { Tick(ref remaining); Scalar(packed, member, expected); }
                }
                else
                {
                    if (wire != expected) throw new FormatException("battle_reply_bad_wire");
                    if (Message(member.Type)) Scan(cursor.Message(), member.Type, depth + 1, ref remaining);
                    else if (member.Type == typeof(string))
                    { Cursor text = cursor.Message(); utf8.GetString(text.Bytes, text.Position, text.Stop - text.Position); }
                    else if (member.Type == typeof(byte[])) cursor.Message();
                    else Scalar(cursor, member, wire);
                }
            }
            foreach (Member member in schema.Members.Values)
                if (member.Required && !seen.ContainsKey(member.Tag)) throw new FormatException("battle_reply_required_field_missing");
        }

        private static void Scalar(Cursor cursor, Member member, int wire)
        {
            Type type = member.Type;
            if (wire == 1 || wire == 5)
            {
                int count = wire == 1 ? 8 : 4; cursor.Require(count);
                if (type == typeof(float))
                { float n = BitConverter.ToSingle(cursor.Bytes, cursor.Position); if (Single.IsNaN(n) || Single.IsInfinity(n)) throw new FormatException("battle_reply_invalid_float"); }
                if (type == typeof(double))
                { double n = BitConverter.ToDouble(cursor.Bytes, cursor.Position); if (Double.IsNaN(n) || Double.IsInfinity(n)) throw new FormatException("battle_reply_invalid_float"); }
                cursor.Position += count; return;
            }
            ulong value = cursor.Number();
            if (type == typeof(bool) && value > 1 || type == typeof(uint) && value > UInt32.MaxValue ||
                type == typeof(ushort) && value > UInt16.MaxValue || type == typeof(byte) && value > Byte.MaxValue ||
                type == typeof(int) && (member.Format == "ZigZag" ? value > UInt32.MaxValue : value > UInt32.MaxValue && value < 0xffffffff80000000UL))
                throw new FormatException("battle_reply_scalar_overflow");
            if (type.IsEnum)
            {
                if (value > Int32.MaxValue && value < 0xffffffff80000000UL ||
                    !Enum.IsDefined(type, Enum.ToObject(type, unchecked((int)value))))
                    throw new FormatException("battle_reply_unknown_enum");
            }
        }

        private sealed class Cursor
        {
            internal readonly byte[] Bytes;
            internal readonly int Stop;
            internal int Position;
            internal Cursor(byte[] bytes, int start, int stop) { Bytes = bytes; Position = start; Stop = stop; }
            internal bool End { get { return Position == Stop; } }
            internal void Require(int count)
            { if (count < 0 || count > Stop - Position) throw new FormatException("battle_reply_truncated"); }
            internal ulong Number()
            {
                ulong value = 0;
                for (int shift = 0; shift < 70; shift += 7)
                {
                    Require(1); byte current = Bytes[Position++];
                    if (shift == 63 && current > 1) throw new FormatException("battle_reply_varint_overflow");
                    value |= (ulong)(current & 127) << shift;
                    if ((current & 128) == 0) return value;
                }
                throw new FormatException("battle_reply_varint_overflow");
            }
            internal Cursor Message()
            {
                ulong size = Number();
                if (size > (ulong)(Stop - Position)) throw new FormatException("battle_reply_truncated");
                Cursor child = new Cursor(Bytes, Position, Position + (int)size); Position += (int)size; return child;
            }
        }
    }
}
