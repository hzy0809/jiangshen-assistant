using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    internal delegate bool TitleStartLease();
    // A title-only capability: no player, transport, MainUI or business model.
    // The pinned stock OnGUI branch is !startflag_2 && (timer < 0 || skipflag),
    // followed by startflag_2=true; Global.ExternalCall("ClickStart"). remindOK
    // only chooses a texture. StartGameLevel remains owned by stock Update.
    internal sealed class TitleStartModel
    {
        internal const string Interface = "scoped-title-start-v1";
        private readonly string directory, ownerPath, descriptor, operation, epoch, session;
        private readonly int nativeThread, managedThread;
        private readonly long creation;
        private readonly uint pid;
        private readonly IntPtr hwnd;
        private readonly Type titleType;
        private readonly FieldInfo started, timer, skip;
        private readonly MethodInfo find, external;
        private IntPtr mutex, cancelled;
        private bool consumed, stopped;
        private int instance;
        private string outcome = "NOT_INVOKED", reason = "not_requested", gate = "not_observed";
        [DllImport("user32.dll", ExactSpelling = true)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool GetProcessTimes(IntPtr process, out long start, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenMutexW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool ReleaseMutex(IntPtr handle);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] bytes, uint size, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool FlushFileBuffers(IntPtr handle);

        internal TitleStartModel(string path, string nonce, int native, int managed, long born)
        {
            directory = path; session = nonce; nativeThread = native; managedThread = managed; creation = born; pid = Entry.GetCurrentProcessId();
            descriptor = File.ReadAllText(Path.Combine(path, "title-session.txt"));
            string[] lines = descriptor.Split(new char[] { '\n' });
            long window;
            if (lines.Length != 10 || lines[0] != Interface || lines[1] != nonce || !Command.ValidNonce(lines[2]) ||
                !Command.ValidNonce(lines[3]) || lines[4] != pid.ToString(CultureInfo.InvariantCulture) ||
                lines[5] != born.ToString(CultureInfo.InvariantCulture) ||
                !Int64.TryParse(lines[6], NumberStyles.None, CultureInfo.InvariantCulture, out window) || window <= 0 || window > UInt32.MaxValue ||
                lines[7] != native.ToString(CultureInfo.InvariantCulture) || lines[8] != "exclusive" || lines[9] != "")
                throw new ProbeFailure("title_session_schema");
            operation = lines[2]; epoch = lines[3]; hwnd = new IntPtr(unchecked((int)window));
            ownerPath = Path.Combine(Path.Combine(Entry.SessionsRoot, "leases"), lines[4] + "-" + lines[5] + ".title");
            Pin(typeof(object).Assembly, "mscorlib", "3F34C90AB282E8F3546946263087FB5002CF96EF9F412DD4011DAB3F0F7CD8A4");
            Pin(typeof(UObject).Assembly, "UnityEngine", "D479BEFC03AFB0F2071FFCF108847F06A0D560075EB8A76980F0AE929115ED73");
            Assembly game = Loaded("Assembly-CSharp"), first = Loaded("Assembly-CSharp-firstpass");
            Pin(game, "Assembly-CSharp", "C1C8EBAC7DDEF4173C873A00774975A5CA366F95B19164C4CA775165EB994911");
            Pin(first, "Assembly-CSharp-firstpass", "29D8FA2CB55E800CCDF1CDC6C5B274B924EA7D03C989296DFA569731E02A8D87");
            titleType = game.GetType("TitleUI", true);
            if (!typeof(MonoBehaviour).IsAssignableFrom(titleType)) throw new ProbeFailure("title_component_schema");
            started = Field("startflag_2", typeof(bool), true, 0x04001F18);
            timer = Field("timer", typeof(float), false, 0x04001F12);
            skip = Field("skipflag", typeof(bool), false, 0x04001F11);
            foreach (MethodInfo method in typeof(UObject).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] p = method.GetParameters();
                if (method.Name != "FindObjectsOfType" || method.IsGenericMethod || method.ReturnType != typeof(UObject[]) || p.Length != 1 || p[0].ParameterType.FullName != "System.Type") continue;
                if (find != null) throw new ProbeFailure("title_find_ambiguous"); find = method;
            }
            Type global = first.GetType("Global", true);
            foreach (MethodInfo method in global.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] p = method.GetParameters();
                if (method.Name != "ExternalCall" || method.ReturnType != typeof(void) || method.IsGenericMethod || p.Length != 2 || p[0].ParameterType != typeof(string) || p[1].ParameterType != typeof(object[])) continue;
                if (external != null) throw new ProbeFailure("title_external_ambiguous"); external = method;
            }
            if (find == null || external == null) throw new ProbeFailure("title_method_schema");
            mutex = OpenMutexW(0x00100001, false, "Local\\JSAssistantBattleLaunch-" + session);
            cancelled = OpenEventW(0x00100000, false, "Local\\JSAssistantBattleCancel-" + session + "-" + epoch);
            if (mutex == IntPtr.Zero || cancelled == IntPtr.Zero) { Stop(); throw new ProbeFailure("title_admission_handles"); }
            if (!Identity()) { Stop(); throw new ProbeFailure("title_identity_or_owner"); }
        }
        private FieldInfo Field(string name, Type type, bool stat, int token)
        {
            FieldInfo value = titleType.GetField(name, BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | (stat ? BindingFlags.Static : BindingFlags.Instance));
            if (value == null || value.FieldType != type || value.IsStatic != stat || value.MetadataToken != token) throw new ProbeFailure("title_field_schema_" + name);
            return value;
        }
        private bool Identity()
        {
            if (stopped || Entry.GetCurrentThreadId() != nativeThread || Thread.CurrentThread.ManagedThreadId != managedThread || Entry.GetCurrentProcessId() != pid || !IsWindow(hwnd)) return false;
            uint process; long born, end, kernel, user;
            return GetWindowThreadProcessId(hwnd, out process) == (uint)nativeThread && process == pid &&
                GetProcessTimes(new IntPtr(-1), out born, out end, out kernel, out user) && born == creation &&
                File.ReadAllText(ownerPath) == descriptor && File.ReadAllText(Path.Combine(directory, "title-session.txt")) == descriptor;
        }
        private Behaviour CaptureTarget()
        {
            instance = 0;
            if (!Identity()) { gate = "identity_or_owner_changed"; return null; }
            if ((bool)started.GetValue(null)) { gate = "already_started"; return null; }
            UObject[] all = (UObject[])find.Invoke(null, new object[] { titleType });
            Behaviour target = null; int count = 0;
            foreach (UObject item in all)
            {
                Behaviour value = item as Behaviour;
                if (value == null || !value.enabled || !value.gameObject.activeInHierarchy) continue;
                if (value.GetType() != titleType) { gate = "unexpected_title_type"; return null; }
                target = value; count++;
            }
            if (count != 1) { gate = count == 0 ? "title_not_present" : "title_not_unique"; return null; }
            instance = target.GetInstanceID();
            float time = (float)timer.GetValue(target);
            if (!(time < 0.0f || (bool)skip.GetValue(target))) { gate = "stock_timer_gate"; return null; }
            gate = "PASS"; return target;
        }
        internal void Capture() { if (!consumed && !stopped) CaptureTarget(); }
        internal void NotInvoked(string why)
        { if (!consumed) reason = why; }
        internal string Invoke(TitleStartLease leaseValid)
        {
            if (consumed) { reason = "operation_already_consumed"; return outcome; }
            if (leaseValid == null || !leaseValid() || stopped) { reason = "stopping_before_invocation"; return outcome; }
            uint wait = WaitForSingleObject(mutex, 0);
            if (wait != 0)
            {
                if (wait == 0x80) ReleaseMutex(mutex);
                reason = "title_admission_mutex_unavailable"; return outcome;
            }
            try
            {
                if (!leaseValid() || Event.current == null || !GUI.enabled || WaitForSingleObject(cancelled, 0) != 258 || CaptureTarget() == null)
                { reason = gate == "PASS" ? "title_admission_cancelled" : gate; return outcome; }
                // Durable create-new consumption precedes *both* stock mutations.
                // A crash or an existing ticket is UNKNOWN and is never replayed.
                string tickets = Path.Combine(Entry.SessionsRoot, "title-operations"); Directory.CreateDirectory(tickets);
                string ticket = Path.Combine(tickets, pid.ToString(CultureInfo.InvariantCulture) + "-" + creation.ToString(CultureInfo.InvariantCulture) + "-" + operation + ".consumed");
                consumed = true; outcome = "UNKNOWN"; reason = "invocation_not_confirmed";
                WriteTicket(ticket);
                if (!Identity()) { reason = "identity_changed_after_consumption"; return outcome; }
                // A stop can arrive while the durable reservation is flushed.
                // Release only a reservation we know has caused no stock call;
                // failure to remove it remains UNKNOWN and cannot be replayed.
                if (!leaseValid() || Event.current == null || !GUI.enabled ||
                    WaitForSingleObject(cancelled, 0) != 258 || CaptureTarget() == null)
                {
                    File.Delete(ticket); consumed = false; outcome = "NOT_INVOKED";
                    reason = gate == "PASS" ? "title_admission_cancelled" : gate; return outcome;
                }
                started.SetValue(null, true);
                external.Invoke(null, new object[] { "ClickStart", new object[0] });
                outcome = "INVOKED"; reason = "stock_click_start_returned";
            }
            catch { outcome = "UNKNOWN"; reason = "invocation_or_ticket_unconfirmed"; }
            finally { if (!ReleaseMutex(mutex)) { outcome = "UNKNOWN"; reason = "title_mutex_release_unconfirmed"; } }
            return outcome;
        }
        private void WriteTicket(string path)
        {
            // Match the project's legacy-compatible durable store: a native
            // CREATE_NEW handle with WRITE_THROUGH and an explicit OS flush.
            IntPtr output = CreateFileW(path, 0x40000000, 1, IntPtr.Zero, 1, 0x80000080, IntPtr.Zero);
            if (output == IntPtr.Zero || output == new IntPtr(-1)) throw new IOException("title_ticket_create_failed");
            bool closed;
            try
            {
                byte[] bytes = System.Text.Encoding.ASCII.GetBytes(descriptor); uint written;
                if (!WriteFile(output, bytes, (uint)bytes.Length, out written, IntPtr.Zero) || written != bytes.Length)
                    throw new IOException("title_ticket_write_failed");
                if (!FlushFileBuffers(output)) throw new IOException("title_ticket_flush_failed");
            }
            finally { closed = CloseHandle(output); }
            if (!closed) throw new IOException("title_ticket_close_failed");
        }
        internal void Stop()
        {
            stopped = true;
            if (mutex != IntPtr.Zero) { CloseHandle(mutex); mutex = IntPtr.Zero; }
            if (cancelled != IntPtr.Zero) { CloseHandle(cancelled); cancelled = IntPtr.Zero; }
        }
        internal string Json()
        { return "{\"interface\":" + Command.Quote(Interface) + ",\"operation_id\":" + Command.Quote(operation) + ",\"hwnd\":" + unchecked((uint)hwnd.ToInt32()).ToString(CultureInfo.InvariantCulture) + ",\"outcome\":" + Command.Quote(outcome) + ",\"reason\":" + Command.Quote(reason) + ",\"gate\":" + Command.Quote(gate) + ",\"instance_id\":" + instance.ToString(CultureInfo.InvariantCulture) + ",\"consumed\":" + (consumed ? "true" : "false") + "}"; }
        private static Assembly Loaded(string name)
        {
            Assembly found = null;
            foreach (Assembly value in AppDomain.CurrentDomain.GetAssemblies()) if (value.GetName().Name == name)
            { if (found != null) throw new ProbeFailure("title_duplicate_assembly"); found = value; }
            if (found == null) throw new ProbeFailure("title_assembly_missing"); return found;
        }
        private static void Pin(Assembly assembly, string name, string hash)
        {
            if (assembly.GetName().Name != name || String.IsNullOrEmpty(assembly.Location)) throw new ProbeFailure("title_assembly_identity");
            using (FileStream stream = File.OpenRead(assembly.Location)) using (SHA256Managed sha = new SHA256Managed())
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != hash) throw new ProbeFailure("title_assembly_hash_" + name);
        }
    }
}
