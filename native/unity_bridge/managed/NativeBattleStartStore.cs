using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace JiangShen.AssistantRuntime
{
    // A generation is one native launch, never an operation, Unity object or PID.
    // A report transfer only transfers this token; it does not resolve it.
    internal sealed class NativeBattleStartTicket
    {
        internal readonly ulong RoleId;
        internal readonly string Mode, Generation, OperationId, SourceSession, Epoch, StartHash, Directory;
        internal readonly int CommandSequence;
        internal NativeBattleStartTicket(ulong role, string mode, string generation, string operation, string session,
            string epoch, int sequence, string hash, string directory)
        { RoleId = role; Mode = mode; Generation = generation; OperationId = operation; SourceSession = session;
            Epoch = epoch; CommandSequence = sequence; StartHash = hash; Directory = directory; }
    }

    // Append-only immutable starts and confirmations, serialized by a Windows
    // exclusive Win32 handle per admission epoch and stable role/mode. Earlier
    // epochs remain immutable evidence and cannot block a newly started task.
    internal static class NativeBattleStartStore
    {
        internal const string Interface = "native-durable-battle-start-v1";
        private const string Magic = "JS_ASSISTANT_NATIVE_BATTLE_START_V1";
        private const string UnknownMagic = "JS_ASSISTANT_NATIVE_HISTORICAL_UNKNOWN_V1";
        private const string AnchorMagic = "JS_ASSISTANT_NATIVE_BATTLE_ANCHOR_V1";
        private const int MaximumRecords = 65536, MaximumAnchorBytes = 16 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool FlushFileBuffers(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] bytes, uint size, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool GetFileSizeEx(IntPtr handle, out long size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string existing, string destination, uint flags);
#if JSB_NATIVE_BATTLE_STORE_TEST
        // This override does not exist in the deployed payload. No environment
        // variable or command can redirect a production store to a clean root.
        internal static string TestRoot = Path.Combine(Path.GetTempPath(), "native-battle-store-test-" + Guid.NewGuid().ToString("N"));
        internal static string TestFault = "";
        private static string sessionNonce = "00000000000000000000000000000001";
#else
        private static string sessionNonce;
#endif
        internal static void InitializeSession(string nonce)
        { if (!Hex(nonce, 32)) throw new ProbeFailure("battle_durable_session_identity"); sessionNonce = nonce; }
        private sealed class Record
        {
            internal ulong Role;
            internal string Mode, Generation, Operation, Session, Epoch, State, StartHash, Proof, Hash;
            internal int Sequence;
        }
        private sealed class HistoricalUnknown
        {
            internal ulong Role;
            internal string Mode, Attempt, Session, StateHash, EventsHash, Hash;
            internal ushort ResponseOpcode;
        }
        private sealed class Ledger
        {
            internal readonly Hashtable Starts = new Hashtable(), Confirmed = new Hashtable();
            internal Record Active;
            internal bool HasHistoricalUnknown;
            internal Anchor Checkpoint;
        }
        private sealed class Anchor
        {
            internal ulong Role;
            internal string Mode;
            internal readonly Hashtable Files = new Hashtable();
        }
        private sealed class StoreLock : IDisposable
        {
            private IntPtr handle;
            internal readonly bool Created;
            internal StoreLock(IntPtr value, bool created) { handle = value; Created = created; }
            public void Dispose()
            {
                IntPtr value = handle; handle = IntPtr.Zero;
                if (value != IntPtr.Zero && !CloseHandle(value)) throw new IOException("battle_durable_unlock_failed");
            }
        }

        private static string Root()
        {
#if JSB_NATIVE_BATTLE_STORE_TEST
            string value = TestRoot;
#else
            string sessions = Path.GetFullPath(Entry.SessionsRoot);
            string data = Path.GetDirectoryName(sessions);
            if (String.IsNullOrEmpty(data) || Path.GetFileName(sessions) != "object_sessions" || !System.IO.Directory.Exists(data))
                throw new ProbeFailure("battle_durable_product_data_root_unavailable");
            string value = Path.Combine(data, "native-battle-starts-v1");
#endif
            if (String.IsNullOrEmpty(value) || !Path.IsPathRooted(value)) throw new ProbeFailure("battle_durable_root_invalid");
            return Path.GetFullPath(value);
        }
        private static bool Hex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }
        private static bool Mode(string value)
        {
            switch (value)
            {
                case "Campaign": case "NormalChallenge": case "EliteChallenge": case "Grave": case "Trial": case "TrialBoss": case "Divinity":
                case "TrainingMonster": case "OwnBuildingMonster": case "FriendBuildingMonster":
                case "Arena": case "PointRace": case "OwnDemon": case "WoodFast": return true;
                default: return false;
            }
        }
        private static void Identity(ulong role, string mode, string operation)
        {
            if (role == 0 || !Mode(mode) || !Hex(operation, 32)) throw new ProbeFailure("battle_durable_identity_invalid");
        }
        private static void PlainDirectory(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (System.IO.Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ProbeFailure("battle_durable_reparse_path");
        }
        private static string KeyDirectory(ulong role, string mode, string epoch)
        {
            string root = Root();
            if (epoch != null)
            {
                if (!Hex(epoch, 32)) throw new ProbeFailure("battle_durable_epoch_identity_invalid");
                root = Path.Combine(Path.Combine(root, "rounds"), epoch);
            }
            if (mode == "EliteChallenge")
            {
                Type global = Model.FirstType("Global");
                ulong user = Convert.ToUInt64(SharedDevelopmentContext.Static(global, "userid"), CultureInfo.InvariantCulture);
                uint zone = Convert.ToUInt32(SharedDevelopmentContext.Static(global, "zoneid"), CultureInfo.InvariantCulture);
                if (user == 0 || zone == 0) throw new ProbeFailure("battle_durable_account_identity_unconfirmed");
                root = Path.Combine(Path.Combine(root, "user-" + user.ToString(CultureInfo.InvariantCulture)), "zone-" + zone.ToString(CultureInfo.InvariantCulture));
            }
            string path = Path.Combine(Path.Combine(root, "role-" + role.ToString(CultureInfo.InvariantCulture)), "mode-" + mode);
            PlainDirectory(path);
            System.IO.Directory.CreateDirectory(path);
            PlainDirectory(path);
            return path;
        }
        private static StoreLock Lock(string path)
        {
            PlainDirectory(path);
            string file = Path.Combine(path, ".lock");
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new ProbeFailure("battle_durable_lock_reparse");
            // Distinguish an atomically created lock from an existing key.
            // An existing .lock without an anchor is evidence loss or an
            // interrupted initialization, never proof of an unused key.
            IntPtr handle = CreateFileW(file, 0xC0000000, 0, IntPtr.Zero, 1, 0x80000080, IntPtr.Zero);
            bool created = handle != IntPtr.Zero && handle != new IntPtr(-1);
            if (!created)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 80 && error != 183) throw new IOException("battle_durable_lock_unavailable");
                handle = CreateFileW(file, 0xC0000000, 0, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            }
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw new IOException("battle_durable_lock_unavailable");
            long size;
            if (GetFileSizeEx(handle, out size) && size == 0)
            {
                try
                {
                    if (created && !FlushFileBuffers(handle)) throw new IOException("battle_durable_lock_flush_failed");
                    if (created) Boundary("lock-created");
                    return new StoreLock(handle, created);
                }
                catch { CloseHandle(handle); throw; }
            }
            CloseHandle(handle);
            throw new ProbeFailure("battle_durable_lock_corrupt");
        }
        private static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        internal static string EvidenceHash(byte[] bytes)
        { if (bytes == null) throw new ProbeFailure("battle_durable_evidence_missing"); return Hash(bytes); }
        private static bool Proof(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 512) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == ':' || c == '.')) return false;
            return true;
        }
        private static byte[] Encode(Record value)
        {
            string payload = Magic + "\nrole=" + value.Role.ToString(CultureInfo.InvariantCulture) + "\nmode=" + value.Mode +
                "\ngeneration=" + value.Generation + "\noperation=" + value.Operation + "\nsession=" + value.Session +
                "\nepoch=" + value.Epoch + "\ncommand_seq=" + value.Sequence.ToString(CultureInfo.InvariantCulture) + "\nstate=" + value.State +
                "\nstart_sha256=" + value.StartHash + "\nproof=" + value.Proof + "\n";
            value.Hash = Hash(Utf8.GetBytes(payload));
            return Utf8.GetBytes(payload + "sha256=" + value.Hash + "\n");
        }
        private static byte[] ReadBytes(string path)
        { return ReadBytes(path, 2048); }
        private static byte[] ReadBytes(string path, int maximum)
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new ProbeFailure("battle_durable_record_not_plain");
            byte[] bytes;
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > maximum) throw new ProbeFailure("battle_durable_record_size");
                bytes = new byte[(int)input.Length]; int position = 0, count;
                while (position < bytes.Length && (count = input.Read(bytes, position, bytes.Length - position)) > 0) position += count;
                if (position != bytes.Length || input.ReadByte() != -1) throw new ProbeFailure("battle_durable_record_read");
            }
            return bytes;
        }
        private static Record Decode(string path, ulong role, string mode, string generation, bool confirmation)
        {
            byte[] bytes = ReadBytes(path);
            string[] lines = Utf8.GetString(bytes).Split('\n');
            if (lines.Length != 13 || lines[0] != Magic || lines[12] != "" ||
                lines[1] != "role=" + role.ToString(CultureInfo.InvariantCulture) || lines[2] != "mode=" + mode ||
                lines[3] != "generation=" + generation || !lines[4].StartsWith("operation=", StringComparison.Ordinal) ||
                !lines[5].StartsWith("session=", StringComparison.Ordinal) || !lines[6].StartsWith("epoch=", StringComparison.Ordinal) ||
                !lines[7].StartsWith("command_seq=", StringComparison.Ordinal) || lines[8] != "state=" + (confirmation ? "CONFIRMED" : "RESERVED") ||
                !lines[9].StartsWith("start_sha256=", StringComparison.Ordinal) || !lines[10].StartsWith("proof=", StringComparison.Ordinal) ||
                !lines[11].StartsWith("sha256=", StringComparison.Ordinal)) throw new ProbeFailure("battle_durable_record_schema");
            Record value = new Record { Role = role, Mode = mode, Generation = generation,
                Operation = lines[4].Substring(10), Session = lines[5].Substring(8), Epoch = lines[6].Substring(6),
                Sequence = Int32.Parse(lines[7].Substring(12), CultureInfo.InvariantCulture), State = confirmation ? "CONFIRMED" : "RESERVED",
                StartHash = lines[9].Substring(13), Proof = lines[10].Substring(6) };
            if (!Hex(value.Operation, 32) || !Hex(value.Session, 32) || value.Epoch != "-" && !Hex(value.Epoch, 32) ||
                value.Sequence < 0 || !Proof(value.Proof) ||
                (confirmation ? !Hex(value.StartHash, 64) : value.StartHash != "-" || value.Proof != "reserved"))
                throw new ProbeFailure("battle_durable_record_values");
            byte[] canonical = Encode(value);
            if (canonical.Length != bytes.Length) throw new ProbeFailure("battle_durable_record_checksum");
            for (int index = 0; index < bytes.Length; index++)
                if (bytes[index] != canonical[index]) throw new ProbeFailure("battle_durable_record_checksum");
            return value;
        }
        private static byte[] EncodeUnknown(HistoricalUnknown value)
        {
            string payload = UnknownMagic + "\nrole=" + value.Role.ToString(CultureInfo.InvariantCulture) + "\nmode=" + value.Mode +
                "\nsource_attempt=" + value.Attempt + "\nsource_session=" + value.Session +
                "\nresponse_opcode=" + value.ResponseOpcode.ToString(CultureInfo.InvariantCulture) + "\nstate_sha256=" + value.StateHash +
                "\nevents_sha256=" + value.EventsHash + "\nstate=UNKNOWN\n";
            value.Hash = Hash(Utf8.GetBytes(payload));
            return Utf8.GetBytes(payload + "sha256=" + value.Hash + "\n");
        }
        private static HistoricalUnknown DecodeUnknown(string path, ulong role, string mode)
        {
            byte[] bytes = ReadBytes(path);
            string[] lines = Utf8.GetString(bytes).Split('\n');
            if (lines.Length != 11 || lines[0] != UnknownMagic || lines[10] != "" ||
                lines[1] != "role=" + role.ToString(CultureInfo.InvariantCulture) || lines[2] != "mode=" + mode ||
                !lines[3].StartsWith("source_attempt=", StringComparison.Ordinal) || !lines[4].StartsWith("source_session=", StringComparison.Ordinal) ||
                !lines[5].StartsWith("response_opcode=", StringComparison.Ordinal) || !lines[6].StartsWith("state_sha256=", StringComparison.Ordinal) ||
                !lines[7].StartsWith("events_sha256=", StringComparison.Ordinal) || lines[8] != "state=UNKNOWN" ||
                !lines[9].StartsWith("sha256=", StringComparison.Ordinal)) throw new ProbeFailure("battle_durable_historical_schema");
            HistoricalUnknown value = new HistoricalUnknown { Role = role, Mode = mode, Attempt = lines[3].Substring(15),
                Session = lines[4].Substring(15), ResponseOpcode = UInt16.Parse(lines[5].Substring(16), CultureInfo.InvariantCulture),
                StateHash = lines[6].Substring(13), EventsHash = lines[7].Substring(14) };
            if (!Hex(value.Attempt, 32) || !Hex(value.Session, 32) || value.ResponseOpcode == 0 ||
                !Hex(value.StateHash, 64) || !Hex(value.EventsHash, 64)) throw new ProbeFailure("battle_durable_historical_identity");
            byte[] canonical = EncodeUnknown(value);
            if (Path.GetFileName(path) != "historical-" + value.Hash + ".unknown" || canonical.Length != bytes.Length)
                throw new ProbeFailure("battle_durable_historical_checksum");
            for (int index = 0; index < bytes.Length; index++)
                if (bytes[index] != canonical[index]) throw new ProbeFailure("battle_durable_historical_checksum");
            return value;
        }
        private static byte[] EncodeAnchor(Anchor value)
        {
            if (value.Files.Count > MaximumRecords) throw new ProbeFailure("battle_durable_anchor_capacity");
            string[] names = new string[value.Files.Count];
            value.Files.Keys.CopyTo(names, 0); Array.Sort(names, StringComparer.Ordinal);
            StringBuilder payload = new StringBuilder();
            payload.Append(AnchorMagic).Append("\nrole=").Append(value.Role.ToString(CultureInfo.InvariantCulture));
            payload.Append("\nmode=").Append(value.Mode).Append("\nrecords=").Append(names.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (string name in names) payload.Append("record=").Append(name).Append('|').Append((string)value.Files[name]).Append('\n');
            string text = payload.ToString();
            byte[] bytes = Utf8.GetBytes(text + "sha256=" + Hash(Utf8.GetBytes(text)) + "\n");
            if (bytes.Length > MaximumAnchorBytes) throw new ProbeFailure("battle_durable_anchor_capacity");
            return bytes;
        }
        private static bool DataName(string name)
        {
            if (name.EndsWith(".unknown", StringComparison.Ordinal))
                return name.Length == 83 && name.StartsWith("historical-", StringComparison.Ordinal) && Hex(name.Substring(11, 64), 64);
            bool confirmed = name.EndsWith(".confirmed", StringComparison.Ordinal), start = name.EndsWith(".start", StringComparison.Ordinal);
            int suffix = confirmed ? 10 : start ? 6 : 0;
            return suffix != 0 && name.Length == 32 + suffix && Hex(name.Substring(0, 32), 32);
        }
        private static Anchor DecodeAnchor(string path, ulong role, string mode)
        {
            byte[] bytes = ReadBytes(path, MaximumAnchorBytes);
            string[] lines = Utf8.GetString(bytes).Split('\n');
            if (lines.Length < 6 || lines[0] != AnchorMagic || lines[1] != "role=" + role.ToString(CultureInfo.InvariantCulture) ||
                lines[2] != "mode=" + mode || !lines[3].StartsWith("records=", StringComparison.Ordinal))
                throw new ProbeFailure("battle_durable_anchor_schema");
            int count = Int32.Parse(lines[3].Substring(8), CultureInfo.InvariantCulture);
            if (count < 0 || count > MaximumRecords || lines.Length != count + 6 || lines[count + 5] != "")
                throw new ProbeFailure("battle_durable_anchor_schema");
            Anchor value = new Anchor { Role = role, Mode = mode };
            for (int index = 0; index < count; index++)
            {
                string line = lines[index + 4];
                if (!line.StartsWith("record=", StringComparison.Ordinal)) throw new ProbeFailure("battle_durable_anchor_schema");
                int split = line.IndexOf('|');
                if (split < 8) throw new ProbeFailure("battle_durable_anchor_schema");
                string name = line.Substring(7, split - 7), hash = line.Substring(split + 1);
                if (!DataName(name) || !Hex(hash, 64) || value.Files.Contains(name)) throw new ProbeFailure("battle_durable_anchor_record_invalid");
                value.Files.Add(name, hash);
            }
            byte[] canonical = EncodeAnchor(value);
            if (canonical.Length != bytes.Length) throw new ProbeFailure("battle_durable_anchor_checksum");
            for (int index = 0; index < bytes.Length; index++)
                if (bytes[index] != canonical[index]) throw new ProbeFailure("battle_durable_anchor_checksum");
            return value;
        }
        private static Ledger Read(string directory, ulong role, string mode, bool mayInitialize)
        {
            string[] paths = System.IO.Directory.GetFileSystemEntries(directory);
            string anchorPath = Path.Combine(directory, ".anchor");
            if (!File.Exists(anchorPath))
            {
                // Only the owner that atomically created .lock may establish
                // an empty genesis. Legacy/incomplete keys remain blocked:
                // surviving files cannot prove no earlier record was lost.
                if (!mayInitialize || paths.Length != 1 || Path.GetFileName(paths[0]) != ".lock")
                    throw new ProbeFailure("battle_durable_anchor_missing_unknown");
                Anchor genesis = new Anchor { Role = role, Mode = mode };
                PublishBytes(directory, ".anchor", EncodeAnchor(genesis), false, "genesis");
                Boundary("genesis-published");
                paths = System.IO.Directory.GetFileSystemEntries(directory);
            }
            Anchor checkpoint = DecodeAnchor(anchorPath, role, mode);
            Ledger ledger = new Ledger { Checkpoint = checkpoint };
            Hashtable present = new Hashtable();
            foreach (string path in paths)
            {
                string name = Path.GetFileName(path);
                if (name == ".lock" || name == ".anchor") continue;
                if (!DataName(name)) throw new ProbeFailure("battle_durable_unfinished_or_unknown_record");
                string expected = checkpoint.Files[name] as string;
                if (expected == null || Hash(ReadBytes(path)) != expected)
                    throw new ProbeFailure("battle_durable_anchor_extra_or_changed_record");
                present.Add(name, true);
                if (name.EndsWith(".unknown", StringComparison.Ordinal))
                { DecodeUnknown(path, role, mode); ledger.HasHistoricalUnknown = true; continue; }
                bool confirmed = name.EndsWith(".confirmed", StringComparison.Ordinal);
                bool start = name.EndsWith(".start", StringComparison.Ordinal);
                string generation = confirmed ? name.Substring(0, name.Length - 10) : start ? name.Substring(0, name.Length - 6) : "";
                if (!Hex(generation, 32)) throw new ProbeFailure("battle_durable_unfinished_or_unknown_record");
                Record value = Decode(path, role, mode, generation, confirmed);
                (confirmed ? ledger.Confirmed : ledger.Starts).Add(generation, value);
            }
            if (present.Count != checkpoint.Files.Count) throw new ProbeFailure("battle_durable_anchor_missing_record");
            foreach (Record end in ledger.Confirmed.Values)
            {
                Record start = ledger.Starts[end.Generation] as Record;
                if (start == null || start.Hash != end.StartHash || start.Operation != end.Operation ||
                    start.Session != end.Session || start.Epoch != end.Epoch || start.Sequence != end.Sequence)
                    throw new ProbeFailure("battle_durable_orphan_or_mismatched_confirmation");
            }
            foreach (Record start in ledger.Starts.Values)
                if (!ledger.Confirmed.Contains(start.Generation))
                {
                    if (ledger.Active != null) throw new ProbeFailure("battle_durable_multiple_active");
                    ledger.Active = start;
                }
            return ledger;
        }
        private static void Append(string directory, Anchor checkpoint, string name, byte[] bytes, string boundary)
        {
            if (!DataName(name) || checkpoint.Files.Contains(name) || checkpoint.Files.Count >= MaximumRecords)
                throw new ProbeFailure("battle_durable_anchor_append_invalid_or_capacity");
            // Precompute and validate the next complete inventory before any
            // record publication. A data record can authorize no stock action
            // until its separately flushed/published anchor also validates.
            checkpoint.Files.Add(name, Hash(bytes));
            byte[] next = EncodeAnchor(checkpoint);
            PublishBytes(directory, name, bytes, false, boundary);
            Boundary(boundary + "-published");
            PublishBytes(directory, ".anchor", next, true, boundary + "-anchor");
            Boundary(boundary + "-anchor-published");
            Read(directory, checkpoint.Role, checkpoint.Mode, false);
        }
        private static void Boundary(string name)
        {
#if JSB_NATIVE_BATTLE_STORE_TEST
            if (TestFault == "fail:" + name) throw new IOException("test_failure_at_" + name);
            if (TestFault == "pause:" + name)
            { Console.WriteLine("BARRIER " + name); Console.Out.Flush(); Console.ReadLine(); }
#endif
        }
        private static void PublishBytes(string directory, string name, byte[] bytes, bool replace, string boundary)
        {
            string stage = Path.Combine(directory, name + ".tmp"), destination = Path.Combine(directory, name);
#if JSB_NATIVE_BATTLE_STORE_TEST
            if (TestFault == "before-write") throw new IOException("test_write_failure");
#endif
            // Use a native handle, independently of Unity Mono's FileStream
            // implementation. CREATE_NEW + WRITE_THROUGH; never replace data.
            IntPtr output = CreateFileW(stage, 0x40000000, 0, IntPtr.Zero, 1, 0x80000080, IntPtr.Zero);
            if (output == IntPtr.Zero || output == new IntPtr(-1)) throw new IOException("battle_durable_create_failed");
            bool closed;
            try
            {
                uint written;
                if (!WriteFile(output, bytes, (uint)bytes.Length, out written, IntPtr.Zero) || written != bytes.Length)
                    throw new IOException("battle_durable_write_failed");
                if (!FlushFileBuffers(output)) throw new IOException("battle_durable_flush_failed");
            }
            finally { closed = CloseHandle(output); }
            if (!closed) throw new IOException("battle_durable_close_failed");
#if JSB_NATIVE_BATTLE_STORE_TEST
            if (TestFault == "after-flush") throw new IOException("test_publish_failure");
#endif
            Boundary(boundary + "-staged");
            // Data generations remain immutable. Only the complete checkpoint
            // may atomically replace its previous, fully validated inventory.
            if (!MoveFileExW(stage, destination, replace ? 9u : 8u)) throw new IOException("battle_durable_atomic_publish_failed");
        }
        internal static NativeBattleStartTicket Reserve(ulong role, string mode, string operation, string epoch, int sequence)
        {
            try
            {
                Identity(role, mode, operation);
                if (!Hex(sessionNonce, 32) || epoch != null && !Hex(epoch, 32) || sequence < 0)
                    throw new ProbeFailure("battle_durable_start_lineage_invalid");
                string directory = KeyDirectory(role, mode, epoch);
                using (StoreLock held = Lock(directory))
                {
                    Ledger ledger = Read(directory, role, mode, held.Created);
                    if (ledger.HasHistoricalUnknown) throw new ProbeFailure("battle_durable_historical_unknown");
                    if (ledger.Active != null) throw new ProbeFailure("battle_durable_prior_pending_or_unknown");
                    Record record = new Record { Role = role, Mode = mode, Generation = Guid.NewGuid().ToString("N"),
                        Operation = operation, Session = sessionNonce, Epoch = epoch ?? "-", Sequence = sequence,
                        State = "RESERVED", StartHash = "-", Proof = "reserved" };
                    Append(directory, ledger.Checkpoint, record.Generation + ".start", Encode(record), "start");
                    return new NativeBattleStartTicket(role, mode, record.Generation, operation, record.Session,
                        record.Epoch, record.Sequence, record.Hash, directory);
                }
            }
            catch (ProbeFailure) { throw; }
            catch (Exception) { throw new ProbeFailure("battle_durable_reservation_unverifiable_no_send"); }
        }
        private static bool Match(Record record, NativeBattleStartTicket ticket)
        {
            return record != null && record.Role == ticket.RoleId && record.Mode == ticket.Mode && record.Generation == ticket.Generation &&
                record.Operation == ticket.OperationId && record.Session == ticket.SourceSession && record.Epoch == ticket.Epoch &&
                record.Sequence == ticket.CommandSequence && record.Hash == ticket.StartHash;
        }
        internal static void RequirePending(NativeBattleStartTicket ticket)
        {
            try
            {
                if (ticket == null) throw new ProbeFailure("battle_durable_ticket_missing");
                using (StoreLock held = Lock(ticket.Directory))
                {
                    Ledger ledger = Read(ticket.Directory, ticket.RoleId, ticket.Mode, false);
                    if (ledger.HasHistoricalUnknown || !Match(ledger.Active, ticket))
                        throw new ProbeFailure("battle_durable_ticket_no_longer_pending");
                }
            }
            catch (ProbeFailure) { throw; }
            catch (Exception) { throw new ProbeFailure("battle_durable_pending_unverifiable_no_send"); }
        }
        internal static void Confirm(NativeBattleStartTicket ticket, string proof)
        {
            // Null is reserved for already-owned manual reports with no launch.
            if (ticket == null) return;
            try
            {
                if (!Proof(proof)) throw new ProbeFailure("battle_durable_confirmation_proof_invalid");
                using (StoreLock held = Lock(ticket.Directory))
                {
                    Ledger ledger = Read(ticket.Directory, ticket.RoleId, ticket.Mode, false);
                    if (ledger.HasHistoricalUnknown) throw new ProbeFailure("battle_durable_historical_unknown");
                    if (!Match(ledger.Starts[ticket.Generation] as Record, ticket)) throw new ProbeFailure("battle_durable_confirmation_ticket_mismatch");
                    Record previous = ledger.Confirmed[ticket.Generation] as Record;
                    if (previous != null)
                    {
                        if (previous.Proof != proof) throw new ProbeFailure("battle_durable_confirmation_proof_changed");
                        return; // Never touches a later active generation.
                    }
                    if (!Match(ledger.Active, ticket)) throw new ProbeFailure("battle_durable_confirmation_not_current");
                    Record record = new Record { Role = ticket.RoleId, Mode = ticket.Mode, Generation = ticket.Generation,
                        Operation = ticket.OperationId, Session = ticket.SourceSession, Epoch = ticket.Epoch, Sequence = ticket.CommandSequence,
                        State = "CONFIRMED", StartHash = ticket.StartHash, Proof = proof };
                    Append(ticket.Directory, ledger.Checkpoint, ticket.Generation + ".confirmed", Encode(record), "confirmation");
                }
            }
            catch (ProbeFailure) { throw; }
            catch (Exception) { throw new ProbeFailure("battle_durable_confirmation_unverifiable_no_retry"); }
        }
#if JSB_NATIVE_BATTLE_STORE_TEST
        internal static NativeBattleStartTicket Reserve(ulong role, string mode, string operation)
        { return Reserve(role, mode, operation, null, 0); }
        // Operator-only offline importer. The production payload has the reader
        // but no command/API for creating or resolving historical UNKNOWNs.
        internal static string ImportHistoricalUnknown(ulong role, string mode, string attempt, string session,
            ushort responseOpcode, string stateHash, string eventsHash)
        {
            try
            {
                Identity(role, mode, attempt);
                if (!Hex(session, 32) || responseOpcode == 0 || !Hex(stateHash, 64) || !Hex(eventsHash, 64))
                    throw new ProbeFailure("battle_durable_historical_import_identity");
                HistoricalUnknown value = new HistoricalUnknown { Role = role, Mode = mode, Attempt = attempt, Session = session,
                    ResponseOpcode = responseOpcode, StateHash = stateHash, EventsHash = eventsHash };
                byte[] bytes = EncodeUnknown(value);
                string directory = KeyDirectory(role, mode, null), name = "historical-" + value.Hash + ".unknown";
                using (StoreLock held = Lock(directory))
                {
                    Ledger ledger = Read(directory, role, mode, held.Created);
                    string path = Path.Combine(directory, name);
                    if (!File.Exists(path)) Append(directory, ledger.Checkpoint, name, bytes, "historical");
                    DecodeUnknown(path, role, mode);
                    return path;
                }
            }
            catch (ProbeFailure) { throw; }
            catch (Exception) { throw new ProbeFailure("battle_durable_historical_import_unverifiable"); }
        }
        internal static void ValidateHistoricalUnknownFile(string path, ulong role, string mode)
        { DecodeUnknown(path, role, mode); }
#endif
    }
}
