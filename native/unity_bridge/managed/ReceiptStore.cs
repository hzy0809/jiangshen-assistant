using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

namespace JiangShen.AssistantRuntime
{
    // Keep the failed boundary with the original exception; callers must never
    // infer a lock holder or replay a command from an I/O failure.
    internal sealed class ReceiptIoFailure : IOException
    {
        internal readonly string Stage, ReceiptPath;
        internal readonly int Win32Error;
        internal ReceiptIoFailure(string stage, string path, int nativeError, Exception inner)
            : base("receipt_io_" + stage, inner)
        { Stage = stage; ReceiptPath = path; Win32Error = nativeError; }
    }

    internal sealed class ReceiptStore
    {
        internal const int MaximumEventBytes = 512 * 1024;
        private readonly string directory;
        private StreamWriter events;
        private int eventBytes;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string existing, string replacement, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool ReplaceFileW(string replaced, string replacement, string backup, int flags, IntPtr exclude, IntPtr reserved);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] bytes, uint size, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern bool FlushFileBuffers(IntPtr handle);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool CloseHandle(IntPtr handle);

        internal ReceiptStore(string path)
        {
            directory = path;
            events = OpenEvents(FileMode.CreateNew);
        }
        private StreamWriter OpenEvents(FileMode mode)
        {
            string path = Path.Combine(directory, "probe.events.jsonl");
            try { return new StreamWriter(new FileStream(path, mode,
                FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Utf8); }
            catch (IOException error) { throw new ReceiptIoFailure("event_open", path, -1, error); }
            catch (UnauthorizedAccessException error) { throw new ReceiptIoFailure("event_open", path, -1, error); }
        }
        internal void Event(string json)
        {
            string path = Path.Combine(directory, "probe.events.jsonl"), stage = "event_validate";
            try
            {
                if (events == null || json == null || json.Length > 32768) throw new IOException("receipt_unavailable");
                int bytes = Utf8.GetByteCount(json) + 2;
                if (eventBytes + bytes > MaximumEventBytes)
                {
                    stage = "event_close"; events.Close(); events = null;
                    string current = path, previous = Path.Combine(directory, "probe.events.jsonl.1");
                    path = previous; stage = "event_delete_previous";
                    if (File.Exists(previous)) File.Delete(previous);
                    path = current; stage = "event_rotate"; File.Move(current, previous);
                    events = OpenEvents(FileMode.CreateNew); eventBytes = 0;
                }
                stage = "event_write"; events.WriteLine(json);
                stage = "event_flush"; events.Flush(); eventBytes += bytes;
            }
            catch (ReceiptIoFailure) { throw; }
            catch (IOException error) { throw new ReceiptIoFailure(stage, path, -1, error); }
            catch (UnauthorizedAccessException error) { throw new ReceiptIoFailure(stage, path, -1, error); }
        }
        internal void Write(string name, string json)
        {
            string path = directory, stage = "receipt_validate";
            try
            {
                if (name != "state.json" && name != "ack.json" && name != "recovery_unknown.json") throw new IOException("receipt_name");
                int limit = name == "state.json" || name == "ack.json" ? 2 * 1024 * 1024 : 32768;
                if (json == null || json.Length > limit) throw new IOException("receipt_size");
                string destination = Path.Combine(directory, name), temporary = destination + ".tmp";
                path = temporary; stage = "temporary_write";
                using (StreamWriter writer = new StreamWriter(new FileStream(temporary, FileMode.Create,
                    FileAccess.Write, FileShare.Read), Utf8)) { writer.Write(json); writer.Write('\n'); writer.Flush(); }
                // MoveFileEx(REPLACE_EXISTING) can reject an open destination even
                // when its reader shares DELETE. ReplaceFile preserves old readers'
                // complete file object while atomically publishing the new version.
                // The first publish uses a non-replacing move; never delete or write
                // through the destination, and never sleep/retry on Unity's thread.
                path = destination;
                bool replacing = File.Exists(destination); stage = replacing ? "replace_file" : "move_file";
                bool published = replacing ? ReplaceFileW(destination, temporary, null, 0, IntPtr.Zero, IntPtr.Zero)
                    : MoveFileExW(temporary, destination, 8);
                int nativeError = published ? 0 : Marshal.GetLastWin32Error();
                if (!published) throw new ReceiptIoFailure(stage, path, nativeError, null);
            }
            catch (ReceiptIoFailure) { throw; }
            catch (IOException error) { throw new ReceiptIoFailure(stage, path, -1, error); }
            catch (UnauthorizedAccessException error) { throw new ReceiptIoFailure(stage, path, -1, error); }
        }
        internal void Close() { if (events != null) { events.Close(); events = null; } }
        internal static void WriteRecoveryStatus(string directory, string json)
        {
            // A separate diagnostic file never constitutes a recovery grant.
            if (json == null || json.Length > 32768) throw new IOException("recovery_status_size");
            string destination = Path.Combine(directory, "same_process_recovery.json"), temporary = destination + ".tmp";
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0 ||
                File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0) throw new IOException("recovery_status_reparse");
            using (StreamWriter writer = new StreamWriter(new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read), Utf8))
            { writer.Write(json); writer.Write('\n'); writer.Flush(); }
            bool published = File.Exists(destination) ? ReplaceFileW(destination, temporary, null, 0, IntPtr.Zero, IntPtr.Zero) : MoveFileExW(temporary, destination, 8);
            if (!published) throw new ReceiptIoFailure("recovery_status_publish", destination, Marshal.GetLastWin32Error(), null);
        }
        internal static string WriteRecoveryResolution(string directory, string json)
        {
            if (json == null || json.Length > 32768) throw new IOException("recovery_resolution_size");
            string path = Path.Combine(directory, "same_process_resolution.json"); byte[] bytes = Utf8.GetBytes(json + "\n");
            // CREATE_NEW plus WRITE_THROUGH and FlushFileBuffers. A partial file
            // remains evidence of failure and can never be overwritten/retried.
            IntPtr file = CreateFileW(path, 0x40000000, 1, IntPtr.Zero, 1, 0x80000080, IntPtr.Zero);
            if (file == new IntPtr(-1)) throw new ReceiptIoFailure("recovery_resolution_create_new", path, Marshal.GetLastWin32Error(), null);
            try
            {
                uint written;
                if (!WriteFile(file, bytes, (uint)bytes.Length, out written, IntPtr.Zero) || written != bytes.Length)
                    throw new ReceiptIoFailure("recovery_resolution_write", path, Marshal.GetLastWin32Error(), null);
                if (!FlushFileBuffers(file)) throw new ReceiptIoFailure("recovery_resolution_flush", path, Marshal.GetLastWin32Error(), null);
            }
            finally { CloseHandle(file); }
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
