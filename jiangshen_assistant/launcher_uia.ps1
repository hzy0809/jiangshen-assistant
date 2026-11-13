# Runtime helper for launcher_login.py. Requests arrive only through private
# stdin. Do not add tracing, transcripts, request dumps, or exception output.
# A single inherited, unnamed event cancels a request between UI mutations.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
$helperStage = 'HELPER_SETUP_FAILED'
try {
    [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName WindowsBase
    Add-Type -AssemblyName System.Web.Extensions
    $references = @(
        [System.Windows.Automation.AutomationElement].Assembly.Location,
        [System.Windows.Automation.ControlType].Assembly.Location,
        [System.Windows.Point].Assembly.Location,
        [System.Web.Script.Serialization.JavaScriptSerializer].Assembly.Location,
        'System.dll', 'System.Core.dll'
    )
    $source = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Automation;

public static class JiangShenLauncherUi
{
    private sealed class Fault : Exception
    {
        public readonly string Code;
        public Fault(string code) { Code = code; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr handle, out FileTime created,
        out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags,
        StringBuilder path, ref uint length);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);

    private static Dictionary<string, object> Result(string code)
    {
        return new Dictionary<string, object> {
            { "code", code }, { "authenticated", false }, { "captcha", false },
            { "login_form", false }, { "server_count", 0 }, { "picker", false }
        };
    }

    private sealed class Context : IDisposable
    {
        private readonly Dictionary<string, object> request;
        private readonly uint pid;
        private readonly uint tid;
        private readonly ulong creation;
        private readonly string executable;
        private readonly string server;
        private readonly IntPtr hwnd;
        private readonly IntPtr cancel;
        private IntPtr process;
        private AutomationElement root;

        public Context(Dictionary<string, object> value)
        {
            request = value;
            Dictionary<string, object> target = value["target"] as Dictionary<string, object>;
            if (target == null) throw new Fault("IDENTITY_UNAVAILABLE");
            pid = Convert.ToUInt32(target["pid"]);
            tid = Convert.ToUInt32(target["tid"]);
            creation = Convert.ToUInt64(target["process_creation_filetime"]);
            executable = target["exe_path"] as string;
            hwnd = new IntPtr(Convert.ToInt64(target["hwnd"]));
            cancel = new IntPtr(Convert.ToInt64(value["cancel_handle"]));
            server = value["server_name"] as string;
            if (pid == 0 || tid == 0 || creation == 0 || hwnd == IntPtr.Zero
                || cancel == IntPtr.Zero || String.IsNullOrEmpty(executable)
                || String.IsNullOrEmpty(server) || server.Length > 120)
                throw new Fault("IDENTITY_UNAVAILABLE");
            process = OpenProcess(0x101000, false, pid);
            if (process == IntPtr.Zero) throw new Fault("IDENTITY_UNAVAILABLE");
        }

        private void CheckCancelled()
        {
            uint state = WaitForSingleObject(cancel, 0);
            if (state == 0) throw new Fault("CANCELLED");
            if (state != 258) throw new Fault("AUTOMATION_UNAVAILABLE");
        }

        private static string Canonical(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            return Path.GetFullPath(path).TrimEnd('\\');
        }

        private void VerifyTarget()
        {
            CheckCancelled();
            FileTime born, died, kernel, user;
            StringBuilder path = new StringBuilder(32768);
            uint size = 32768;
            uint owner;
            uint thread = GetWindowThreadProcessId(hwnd, out owner);
            StringBuilder title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (WaitForSingleObject(process, 0) != 258
                || !GetProcessTimes(process, out born, out died, out kernel, out user)
                || (((ulong)born.High << 32) | born.Low) != creation
                || !QueryFullProcessImageName(process, 0, path, ref size)
                || !String.Equals(Canonical(path.ToString()), Canonical(executable), StringComparison.OrdinalIgnoreCase)
                || !String.Equals(Path.GetFileName(path.ToString()), "Launcher.exe", StringComparison.OrdinalIgnoreCase)
                || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetAncestor(hwnd, 2) != hwnd
                || owner != pid || thread != tid || title.ToString() != "\u5c06\u795e\u767b\u9646\u5668")
                throw new Fault("IDENTITY_UNAVAILABLE");
            root = AutomationElement.FromHandle(hwnd);
            if (root == null || root.Current.ProcessId != (int)pid)
                throw new Fault("IDENTITY_UNAVAILABLE");
        }

        private static bool Visible(AutomationElement element)
        {
            if (element == null) return false;
            AutomationElement.AutomationElementInformation info = element.Current;
            return !info.IsOffscreen && !info.BoundingRectangle.IsEmpty
                && info.BoundingRectangle.Width > 0 && info.BoundingRectangle.Height > 0;
        }

        private List<AutomationElement> Find(AutomationElement scope, Condition condition)
        {
            CheckCancelled();
            AutomationElementCollection elements = scope.FindAll(TreeScope.Descendants, condition);
            if (elements.Count > 4000) throw new Fault("AUTOMATION_UNAVAILABLE");
            List<AutomationElement> result = new List<AutomationElement>();
            for (int index = 0; index < elements.Count; index++)
            {
                CheckCancelled();
                if (Visible(elements[index])) result.Add(elements[index]);
            }
            return result;
        }

        private List<AutomationElement> ById(AutomationElement scope, string id)
        {
            return Find(scope, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        }

        private AutomationElement UniqueId(AutomationElement scope, string id)
        {
            List<AutomationElement> found = ById(scope, id);
            return found.Count == 1 ? found[0] : null;
        }

        private static bool OfficialAddress(string text)
        {
            Uri uri;
            return !String.IsNullOrEmpty(text) && Uri.TryCreate(text, UriKind.Absolute, out uri)
                && (uri.Scheme == "http" || uri.Scheme == "https")
                && uri.IsDefaultPort && uri.UserInfo.Length == 0
                && String.Equals(uri.Host, "js.xd.com", StringComparison.OrdinalIgnoreCase)
                && (uri.AbsolutePath == "/jswd" || uri.AbsolutePath.StartsWith("/jswd/", StringComparison.Ordinal));
        }

        private static bool OfficialDocument(AutomationElement document)
        {
            object pattern;
            // Only document address properties are read. Never read Edit,
            // password, account-name, cookie, DOM script or hidden field values.
            if (document.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)
                && OfficialAddress(((ValuePattern)pattern).Current.Value)) return true;
            // A document title/name can look like a URL without being an
            // address. It is never evidence of the website's origin.
            return false;
        }

        private AutomationElement Document()
        {
            VerifyTarget();
            List<AutomationElement> documents = Find(root,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            List<AutomationElement> verified = new List<AutomationElement>();
            foreach (AutomationElement document in documents)
                if (OfficialDocument(document)) verified.Add(document);
            if (verified.Count != 1) throw new Fault("ORIGIN_UNVERIFIED");
            return verified[0];
        }

        private bool Authenticated(AutomationElement document)
        {
            AutomationElement logout = UniqueId(document, "logout");
            if (logout == null || !logout.Current.IsEnabled || logout.Current.Name != "\u9000\u51fa\u767b\u5f55") return false;
            ControlType type = logout.Current.ControlType;
            return type == ControlType.Hyperlink || type == ControlType.Button;
        }

        private bool Captcha(AutomationElement document)
        {
            return ById(document, "login_verify").Count > 0
                || ById(document, "login_authkey_div").Count > 0;
        }

        private List<AutomationElement> Named(AutomationElement document, string name, bool linksOnly)
        {
            List<AutomationElement> found = Find(document,
                new PropertyCondition(AutomationElement.NameProperty, name));
            List<AutomationElement> accepted = new List<AutomationElement>();
            foreach (AutomationElement element in found)
            {
                ControlType type = element.Current.ControlType;
                if (element.Current.IsEnabled && (type == ControlType.Hyperlink
                    || (!linksOnly && type == ControlType.Button))) accepted.Add(element);
            }
            return accepted;
        }

        private void MutationGate(AutomationElement document, AutomationElement element)
        {
            VerifyTarget();
            CheckCancelled();
            if (!OfficialDocument(document) || !Visible(element) || !element.Current.IsEnabled
                || element.Current.ProcessId != (int)pid)
                throw new Fault("PATTERN_UNSUPPORTED");
        }

        private void Invoke(AutomationElement document, AutomationElement element)
        {
            object pattern;
            if (element == null || !element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                throw new Fault("PATTERN_UNSUPPORTED");
            MutationGate(document, element);
            ((InvokePattern)pattern).Invoke();
        }

        private ValuePattern Edit(AutomationElement form, string id, bool password)
        {
            AutomationElement element = UniqueId(form, id);
            object pattern;
            if (element == null || element.Current.ControlType != ControlType.Edit
                || element.Current.IsPassword != password || !element.Current.IsEnabled
                || element.Current.ProcessId != (int)pid
                || !element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)
                || ((ValuePattern)pattern).Current.IsReadOnly)
                throw new Fault("FORM_UNSUPPORTED");
            return (ValuePattern)pattern;
        }

        public Dictionary<string, object> Execute()
        {
            string action = request["action"] as string;
            AutomationElement document = Document();
            bool authenticated = Authenticated(document);
            bool captcha = Captcha(document);
            AutomationElement form = UniqueId(document, "UserLoginForm");
            List<AutomationElement> servers = Named(document, server, true);
            List<AutomationElement> pickers = Named(document, "\u9009\u62e9\u670d\u52a1\u5668", false);
            Dictionary<string, object> result = Result("OK");
            result["authenticated"] = authenticated;
            result["captcha"] = captcha;
            result["login_form"] = form != null;
            result["server_count"] = Math.Min(2, servers.Count);
            result["picker"] = pickers.Count == 1;
            if (action == "observe") return result;
            if (captcha) throw new Fault("CAPTCHA");
            if (action == "submit_login")
            {
                if (authenticated) throw new Fault("SHARED_LOGIN");
                if (form == null) throw new Fault("FORM_UNSUPPORTED");
                string username = request["login_name"] as string;
                string password = request["password"] as string;
                if (String.IsNullOrEmpty(username) || String.IsNullOrEmpty(password)
                    || username.Length > 512 || password.Length > 4096)
                    throw new Fault("FORM_UNSUPPORTED");
                ValuePattern usernameEdit = Edit(form, "login_username", false);
                ValuePattern passwordEdit = Edit(form, "login_password", true);
                AutomationElement submit = UniqueId(form, "log-submit");
                object submitPattern;
                if (submit == null || submit.Current.ControlType != ControlType.Button
                    || !submit.TryGetCurrentPattern(InvokePattern.Pattern, out submitPattern))
                    throw new Fault("FORM_UNSUPPORTED");
                // Resolve all controls before filling any field; each mutation
                // then checks identity, official address and cancellation again.
                MutationGate(document, UniqueId(form, "login_username"));
                usernameEdit.SetValue(username);
                MutationGate(document, UniqueId(form, "login_password"));
                passwordEdit.SetValue(password);
                MutationGate(document, submit);
                ((InvokePattern)submitPattern).Invoke();
                result["code"] = "LOGIN_SUBMITTED";
                return result;
            }
            if (!authenticated) throw new Fault("HUMAN_LOGIN");
            if (action == "launch_server")
            {
                if (servers.Count != 1) throw new Fault("SERVER_NOT_VISIBLE");
                Invoke(document, servers[0]);
                result["code"] = "SERVER_INVOKED";
                return result;
            }
            if (action == "open_picker")
            {
                if (pickers.Count != 1) throw new Fault("SERVER_NOT_VISIBLE");
                Invoke(document, pickers[0]);
                result["code"] = "PICKER_OPENED";
                return result;
            }
            if (action == "select_tab")
            {
                string tab = request["server_tab"] as string;
                if (tab != "tab_nav_web" && tab != "tab_nav_sd" && tab != "tab_nav_mobile")
                    throw new Fault("PATTERN_UNSUPPORTED");
                AutomationElement element = UniqueId(document, tab);
                object selection;
                if (element != null && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out selection))
                {
                    MutationGate(document, element);
                    ((SelectionItemPattern)selection).Select();
                }
                else Invoke(document, element);
                result["code"] = "TAB_SELECTED";
                return result;
            }
            throw new Fault("PATTERN_UNSUPPORTED");
        }

        public void Dispose()
        {
            if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
            if (cancel != IntPtr.Zero) CloseHandle(cancel);
        }
    }

    public static string Run(string input)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = 32768;
        Dictionary<string, object> request = null;
        Context context = null;
        Dictionary<string, object> result;
        try
        {
            if (input == null || input.Length > 32768) throw new Fault("AUTOMATION_UNAVAILABLE");
            request = serializer.DeserializeObject(input) as Dictionary<string, object>;
            if (request == null) throw new Fault("AUTOMATION_UNAVAILABLE");
            context = new Context(request);
            result = context.Execute();
        }
        catch (Fault failure) { result = Result(failure.Code); }
        catch { result = Result("AUTOMATION_UNAVAILABLE"); }
        finally
        {
            if (request != null) { request.Remove("login_name"); request.Remove("password"); }
            input = null;
            if (context != null) context.Dispose();
        }
        return serializer.Serialize(result);
    }
}
'@
    $helperStage = 'HELPER_COMPILE_FAILED'
    Add-Type -TypeDefinition $source -ReferencedAssemblies $references
    $helperStage = 'HELPER_EXECUTE_FAILED'
    [Console]::Out.WriteLine([JiangShenLauncherUi]::Run([Console]::In.ReadLine()))
} catch {
    # Only a fixed phase is reported; never emit exceptions or request data.
    [Console]::Out.WriteLine('{"code":"' + $helperStage + '","authenticated":false,"captcha":false,"login_form":false,"server_count":0,"picker":false}')
}
