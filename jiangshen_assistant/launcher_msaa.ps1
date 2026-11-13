# MSAA access for the installed launcher. Requests use private
# stdin; output is one fixed-schema result. Never add request/exception dumps,
# transcripts, UI values, arbitrary accessible names, or trace files.

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
    Add-Type -AssemblyName Accessibility
    Add-Type -AssemblyName System.Web.Extensions
    $references = @(
        [Accessibility.IAccessible].Assembly.Location,
        [System.Web.Script.Serialization.JavaScriptSerializer].Assembly.Location,
        'System.dll', 'System.Core.dll'
    )
    $source = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Accessibility;

public static class JiangShenLauncherMsaa
{
    private const int MaximumChildren = 8192;
    private const int MaximumNodes = 16384;
    private const int MaximumDepth = 12;
    private const int MaximumWindows = 16384;
    private const int Unavailable = 0x00000001;
    private const int Invisible = 0x00008000;
    private const int Offscreen = 0x00010000;
    private const int ReadOnly = 0x00000040;
    private const int Focusable = 0x00100000;
    private const int Protected = 0x20000000;

    private sealed class Fault : Exception
    {
        public readonly string Code;
        public Fault(string code) { Code = code; }
    }

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr parameter);

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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object accessible);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(
        [MarshalAs(UnmanagedType.Interface)] IAccessible parent, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct,
            SizeParamIndex = 2)] object[] children, out int obtained);

    private static Dictionary<string, object> Result(string code)
    {
        // Unknown states remain conservative; a verified scan sets fields.
        return new Dictionary<string, object> {
            { "code", code }, { "authenticated", false }, { "captcha", true },
            { "login_form", false }, { "server_count", 0 }, { "picker", false }
        };
    }

    private sealed class Context : IDisposable
    {
        private readonly uint pid;
        private readonly uint tid;
        private readonly ulong creation;
        private readonly string executable;
        private readonly string server;
        private readonly string serverTab;
        private readonly IntPtr hwnd;
        private readonly IntPtr cancel;
        private IntPtr process;
        private int nodes;
        private int logoutCount;
        private int serverCount;
        private int pickerCount;
        private IAccessible logoutScope;
        private int logoutChildId;
        private IAccessible documentPane;
        private IntPtr documentWindow;
        private IAccessible usernameControl;
        private IAccessible passwordControl;
        private IAccessible submitControl;
        private IAccessible serverControl;
        private IAccessible pickerControl;
        private IAccessible tabControl;
        private int serverChildId;
        private int pickerChildId;
        private int tabChildId;
        private int usernameCount;
        private int passwordCount;
        private int submitCount;
        private int tabCount;
        private int otherEditCount;
        private int captchaCount;
        private readonly HashSet<IAccessible> visited = new HashSet<IAccessible>();

        public Context(Dictionary<string, object> request)
        {
            Dictionary<string, object> target = request["target"] as Dictionary<string, object>;
            if (target == null) throw new Fault("IDENTITY_UNAVAILABLE");
            pid = Convert.ToUInt32(target["pid"]);
            tid = Convert.ToUInt32(target["tid"]);
            creation = Convert.ToUInt64(target["process_creation_filetime"]);
            executable = target["exe_path"] as string;
            hwnd = new IntPtr(Convert.ToInt64(target["hwnd"]));
            cancel = new IntPtr(Convert.ToInt64(request["cancel_handle"]));
            server = request["server_name"] as string;
            object requestedTab;
            serverTab = request.TryGetValue("server_tab", out requestedTab) ? requestedTab as string : null;
            if (pid == 0 || tid == 0 || creation == 0 || hwnd == IntPtr.Zero
                || cancel == IntPtr.Zero || String.IsNullOrEmpty(executable)
                || String.IsNullOrEmpty(server) || server.Length > 120)
                throw new Fault("IDENTITY_UNAVAILABLE");
            if (serverTab != null && serverTab != "tab_nav_web" && serverTab != "tab_nav_sd" && serverTab != "tab_nav_mobile")
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
                || owner != pid || thread != tid
                || title.ToString() != "\u5c06\u795e\u767b\u9646\u5668")
                throw new Fault("IDENTITY_UNAVAILABLE");
        }

        private bool OwnedTopLevel(IntPtr window)
        {
            if (!IsWindow(window) || GetAncestor(window, 2) != window) return false;
            uint owner;
            if (GetWindowThreadProcessId(window, out owner) != tid || owner != pid) return false;
            IntPtr current = window;
            for (int depth = 0; depth < 32 && current != IntPtr.Zero; depth++)
            {
                if (current == hwnd) return true;
                current = GetWindow(current, 4); // GW_OWNER, never parent PID inference.
            }
            return false;
        }

        private IntPtr BrowserWindow()
        {
            VerifyTarget();
            List<IntPtr> tops = new List<IntPtr>();
            Fault callbackFailure = null;
            int windowCount = 0;
            EnumWindow topCallback = delegate(IntPtr window, IntPtr unused)
            {
                try
                {
                    CheckCancelled();
                    if (++windowCount > MaximumWindows) throw new Fault("AUTOMATION_UNAVAILABLE");
                    if (IsWindowVisible(window) && OwnedTopLevel(window)) tops.Add(window);
                    return true;
                }
                catch (Fault failure) { callbackFailure = failure; return false; }
                catch { callbackFailure = new Fault("AUTOMATION_UNAVAILABLE"); return false; }
            };
            bool enumerated = EnumWindows(topCallback, IntPtr.Zero);
            if (callbackFailure != null) throw callbackFailure;
            if (!enumerated) throw new Fault("AUTOMATION_UNAVAILABLE");
            HashSet<IntPtr> browsers = new HashSet<IntPtr>();
            EnumWindow childCallback = delegate(IntPtr window, IntPtr unused)
            {
                try
                {
                    CheckCancelled();
                    if (++windowCount > MaximumWindows) throw new Fault("AUTOMATION_UNAVAILABLE");
                    if (!IsWindowVisible(window)) return true;
                    StringBuilder name = new StringBuilder(128);
                    GetClassName(window, name, name.Capacity);
                    if (name.ToString() != "Internet Explorer_Server") return true;
                    uint owner;
                    if (GetWindowThreadProcessId(window, out owner) != tid || owner != pid)
                        throw new Fault("IDENTITY_UNAVAILABLE");
                    if (!OwnedTopLevel(GetAncestor(window, 2)))
                        throw new Fault("IDENTITY_UNAVAILABLE");
                    browsers.Add(window);
                    if (browsers.Count > 1) throw new Fault("ORIGIN_UNVERIFIED");
                    return true;
                }
                catch (Fault failure) { callbackFailure = failure; return false; }
                catch { callbackFailure = new Fault("AUTOMATION_UNAVAILABLE"); return false; }
            };
            foreach (IntPtr top in tops)
            {
                CheckCancelled();
                // EnumChildWindows also visits nested children. Its return
                // value does not distinguish successful empty enumeration.
                EnumChildWindows(top, childCallback, IntPtr.Zero);
                if (callbackFailure != null) throw callbackFailure;
            }
            VerifyTarget();
            if (browsers.Count != 1) throw new Fault("ORIGIN_UNVERIFIED");
            foreach (IntPtr browser in browsers) return browser;
            throw new Fault("ORIGIN_UNVERIFIED");
        }

        private static int Integer(object value)
        {
            if (value is int) return (int)value;
            if (value is uint) return unchecked((int)(uint)value);
            if (value is short) return (short)value;
            throw new Fault("AUTOMATION_UNAVAILABLE");
        }

        private static bool VisibleAndEnabled(int state)
        {
            return (state & (Unavailable | Invisible | Offscreen)) == 0;
        }

        private object[] Children(IAccessible parent)
        {
            CheckCancelled();
            int count = parent.accChildCount;
            if (count < 0 || count > MaximumChildren || nodes > MaximumNodes - count)
                throw new Fault("AUTOMATION_UNAVAILABLE");
            nodes += count;
            object[] children = new object[count];
            if (count == 0) return children;
            int obtained;
            int result = AccessibleChildren(parent, 0, count, children, out obtained);
            CheckCancelled();
            // A shortened/changing list cannot establish unique controls.
            if (result != 0 || obtained != count || parent.accChildCount != count)
                throw new Fault("AUTOMATION_UNAVAILABLE");
            return children;
        }

        private static bool OfficialAddress(string text)
        {
            Uri uri;
            return !String.IsNullOrEmpty(text) && text.Length <= 32768
                && Uri.TryCreate(text, UriKind.Absolute, out uri)
                && (uri.Scheme == "http" || uri.Scheme == "https")
                && uri.IsDefaultPort && uri.UserInfo.Length == 0
                && String.Equals(uri.Host, "js.xd.com", StringComparison.OrdinalIgnoreCase)
                && (uri.AbsolutePath == "/jswd" || uri.AbsolutePath.StartsWith("/jswd/", StringComparison.Ordinal));
        }

        private static string ElementId(IAccessible accessible)
        {
            try
            {
                IObjectServiceProvider provider = accessible as IObjectServiceProvider;
                if (provider == null) return null;
                Guid iid = new Guid("3050F1FF-98B5-11CF-BB82-00AA00BDCE0B");
                object element;
                if (provider.QueryService(ref iid, ref iid, out element) != 0 || element == null) return null;
                return element.GetType().InvokeMember("id", BindingFlags.GetProperty, null, element, null) as string;
            }
            catch { return null; }
        }

        private static bool HasArea(IAccessible scope, int childId)
        {
            int x, y, width, height;
            scope.accLocation(out x, out y, out width, out height, childId);
            return width > 0 && height > 0;
        }

        private void ObserveChildren(IAccessible parent, int depth)
        {
            if (depth > MaximumDepth || !visited.Add(parent))
                throw new Fault("AUTOMATION_UNAVAILABLE");
            object[] children = Children(parent);
            foreach (object child in children)
            {
                CheckCancelled();
                IAccessible accessible = child as IAccessible;
                IAccessible scope = accessible ?? parent;
                int childId = accessible != null ? 0 : Integer(child);
                if (accessible == null && childId <= 0) throw new Fault("AUTOMATION_UNAVAILABLE");
                int role = Integer(scope.get_accRole(childId));
                int state = Integer(scope.get_accState(childId));
                if (!VisibleAndEnabled(state)) continue;
                if (role == 42 && (state & ReadOnly) == 0 && (state & Focusable) != 0)
                {
                    string id = accessible == null ? null : ElementId(accessible);
                    if (id == "login_username" && (state & Protected) == 0 && HasArea(scope, childId))
                    { usernameCount++; usernameControl = accessible; }
                    else if (id == "login_password" && (state & Protected) != 0 && HasArea(scope, childId))
                    { passwordCount++; passwordControl = accessible; }
                    else if (id == "login_verify") captchaCount++;
                    else otherEditCount++;
                }
                if (role == 40 && accessible != null && ElementId(accessible) == "login_authkey_img")
                    captchaCount++;
                // Only actionable links/buttons need names. Never read the
                // names or values of Edit, password, text, or account fields.
                if (role == 30 || role == 43) // ROLE_SYSTEM_LINK / PUSHBUTTON.
                {
                    string name = scope.get_accName(childId);
                    if (String.Equals(name, "\u9000\u51fa\u767b\u5f55", StringComparison.Ordinal))
                    {
                        logoutCount = Math.Min(2, logoutCount + 1);
                        logoutScope = scope;
                        logoutChildId = childId;
                    }
                    if (role == 30 && String.Equals(name, server, StringComparison.Ordinal))
                    {
                        serverCount = Math.Min(2, serverCount + 1);
                        serverControl = scope;
                        serverChildId = childId;
                    }
                    if (String.Equals(name, "\u9009\u62e9\u670d\u52a1\u5668", StringComparison.Ordinal))
                    {
                        pickerCount = Math.Min(2, pickerCount + 1);
                        pickerControl = scope;
                        pickerChildId = childId;
                    }
                    if (role == 43 && accessible != null && ElementId(accessible) == "log-submit" && HasArea(scope, childId))
                    { submitCount++; submitControl = accessible; }
                    string tabName = serverTab == "tab_nav_sd" ? "\u4e09\u7aef\u7248" :
                                     serverTab == "tab_nav_web" ? "\u7f51\u9875\u7248" :
                                     serverTab == "tab_nav_mobile" ? "\u79fb\u52a8\u7248" : null;
                    if (tabName != null && name == tabName && accessible != null && ElementId(accessible) == serverTab)
                    { tabCount++; tabControl = accessible; tabChildId = 0; }
                }
                // AccessibleChildren distinguishes dispatch children from
                // simple integer child IDs without one accChild RPC per item.
                // A simple child has no independent IAccessible subtree.
                if (accessible != null) ObserveChildren(accessible, depth + 1);
            }
        }

        public Dictionary<string, object> Observe()
        {
            IntPtr browser = BrowserWindow();
            Guid iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
            object value;
            CheckCancelled();
            if (AccessibleObjectFromWindow(browser, 0xFFFFFFFC, ref iid, out value) != 0)
                throw new Fault("AUTOMATION_UNAVAILABLE");
            IAccessible root = value as IAccessible;
            if (root == null || Integer(root.get_accRole(0)) != 10
                || !VisibleAndEnabled(Integer(root.get_accState(0))))
                throw new Fault("ORIGIN_UNVERIFIED");
            object[] children = Children(root);
            if (children.Length != 1) throw new Fault("ORIGIN_UNVERIFIED");
            IAccessible pane = children[0] as IAccessible;
            if (pane == null || Integer(pane.get_accRole(0)) != 16
                || !VisibleAndEnabled(Integer(pane.get_accState(0))))
                throw new Fault("ORIGIN_UNVERIFIED");
            CheckCancelled();
            // This is the sole accValue read: the verified document pane's
            // address at CHILDID_SELF. Never read child/edit/password values.
            if (!OfficialAddress(pane.get_accValue(0))) throw new Fault("ORIGIN_UNVERIFIED");
            visited.Add(root);
            ObserveChildren(pane, 0);
            CheckCancelled();
            if (!OfficialAddress(pane.get_accValue(0))) throw new Fault("ORIGIN_UNVERIFIED");
            if (BrowserWindow() != browser) throw new Fault("IDENTITY_UNAVAILABLE");
            documentPane = pane;
            documentWindow = browser;
            bool hasForm = usernameCount == 1 && passwordCount == 1 && submitCount == 1 && otherEditCount == 0;
            string code = captchaCount > 0 ? "CAPTCHA" : logoutCount > 1 ? "AUTOMATION_UNAVAILABLE" :
                          logoutCount == 1 || hasForm ? "OK" : "FORM_UNSUPPORTED";
            Dictionary<string, object> result = Result(code);
            result["authenticated"] = logoutCount == 1;
            result["captcha"] = captchaCount > 0;
            result["login_form"] = hasForm;
            result["server_count"] = serverCount;
            result["picker"] = pickerCount == 1;
            return result;
        }

        public Dictionary<string, object> Logout()
        {
            Dictionary<string, object> observed = Observe();
            if ((string)observed["code"] != "OK" || !(bool)observed["authenticated"] || logoutCount != 1
                || logoutScope == null || logoutChildId != 0)
                throw new Fault("PATTERN_UNSUPPORTED");
            CheckCancelled();
            if (BrowserWindow() != documentWindow || !OfficialAddress(documentPane.get_accValue(0))
                || !VisibleAndEnabled(Integer(logoutScope.get_accState(0)))
                || (Integer(logoutScope.get_accRole(0)) != 30 && Integer(logoutScope.get_accRole(0)) != 43)
                || !String.Equals(logoutScope.get_accName(0), "\u9000\u51fa\u767b\u5f55", StringComparison.Ordinal))
                throw new Fault("PATTERN_UNSUPPORTED");
            CheckCancelled();
            logoutScope.accDoDefaultAction(0);
            return Result("LOGOUT_SUBMITTED");
        }

        private void MutationGate(IAccessible scope, string expectedId, int expectedRole, bool password)
        {
            CheckCancelled();
            if (scope == null || BrowserWindow() != documentWindow
                || !OfficialAddress(documentPane.get_accValue(0)))
                throw new Fault("IDENTITY_UNAVAILABLE");
            int state = Integer(scope.get_accState(0));
            if (!VisibleAndEnabled(state) || Integer(scope.get_accRole(0)) != expectedRole
                || !HasArea(scope, 0) || ElementId(scope) != expectedId)
                throw new Fault("PATTERN_UNSUPPORTED");
            if (expectedRole == 42 && ((state & (ReadOnly | Focusable)) != Focusable
                || ((state & Protected) != 0) != password))
                throw new Fault("FORM_UNSUPPORTED");
            CheckCancelled();
        }

        public Dictionary<string, object> SubmitLogin(string username, string password)
        {
            Dictionary<string, object> observed = Observe();
            if ((string)observed["code"] == "CAPTCHA") throw new Fault("CAPTCHA");
            if ((string)observed["code"] != "OK" || (bool)observed["authenticated"]
                || !(bool)observed["login_form"] || usernameCount != 1 || passwordCount != 1 || submitCount != 1
                || String.IsNullOrEmpty(username) || String.IsNullOrEmpty(password)
                || username.Length > 512 || password.Length > 4096)
                throw new Fault("FORM_UNSUPPORTED");
            MutationGate(usernameControl, "login_username", 42, false);
            usernameControl.set_accValue(0, username);
            MutationGate(passwordControl, "login_password", 42, true);
            passwordControl.set_accValue(0, password);
            MutationGate(submitControl, "log-submit", 43, false);
            submitControl.accDoDefaultAction(0);
            return Result("LOGIN_SUBMITTED");
        }

        public Dictionary<string, object> InvokeAction(string action)
        {
            Dictionary<string, object> observed = Observe();
            if ((string)observed["code"] == "CAPTCHA") throw new Fault("CAPTCHA");
            if ((string)observed["code"] != "OK" || !(bool)observed["authenticated"])
                throw new Fault("HUMAN_LOGIN");
            IAccessible target;
            int childId;
            string expectedName = null;
            string expectedId = null;
            string resultCode;
            if (action == "launch_server" && serverCount == 1)
            { target = serverControl; childId = serverChildId; expectedName = server; resultCode = "SERVER_INVOKED"; }
            else if (action == "open_picker" && pickerCount == 1)
            { target = pickerControl; childId = pickerChildId; expectedName = "\u9009\u62e9\u670d\u52a1\u5668"; resultCode = "PICKER_OPENED"; }
            else if (action == "select_tab" && tabCount == 1)
            { target = tabControl; childId = tabChildId; expectedId = serverTab; resultCode = "TAB_SELECTED"; }
            else throw new Fault("SERVER_NOT_VISIBLE");
            CheckCancelled();
            if (target == null || childId != 0 || BrowserWindow() != documentWindow
                || !OfficialAddress(documentPane.get_accValue(0))
                || !VisibleAndEnabled(Integer(target.get_accState(0))) || !HasArea(target, 0))
                throw new Fault("IDENTITY_UNAVAILABLE");
            int role = Integer(target.get_accRole(0));
            if ((role != 30 && role != 43)
                || (expectedName != null && !String.Equals(target.get_accName(0), expectedName, StringComparison.Ordinal))
                || (expectedId != null && ElementId(target) != expectedId))
                throw new Fault("PATTERN_UNSUPPORTED");
            CheckCancelled();
            target.accDoDefaultAction(0);
            return Result(resultCode);
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
            object action;
            if (!request.TryGetValue("action", out action) || !(action is string))
                throw new Fault("PATTERN_UNSUPPORTED");
            context = new Context(request);
            string operation = (string)action;
            if (operation == "observe") result = context.Observe();
            else if (operation == "logout") result = context.Logout();
            else if (operation == "submit_login")
            {
                object username, password;
                if (!request.TryGetValue("login_name", out username) || !request.TryGetValue("password", out password))
                    throw new Fault("FORM_UNSUPPORTED");
                result = context.SubmitLogin(username as string, password as string);
            }
            else if (operation == "launch_server" || operation == "open_picker" || operation == "select_tab")
                result = context.InvokeAction(operation);
            else throw new Fault("PATTERN_UNSUPPORTED");
        }
        catch (Fault failure) { result = Result(failure.Code); }
        catch { result = Result("AUTOMATION_UNAVAILABLE"); }
        finally
        {
            if (request != null) request.Clear();
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
    [Console]::Out.WriteLine([JiangShenLauncherMsaa]::Run([Console]::In.ReadLine()))
} catch {
    [Console]::Out.WriteLine('{"code":"' + $helperStage + '","authenticated":false,"captcha":true,"login_form":false,"server_count":0,"picker":false}')
}
