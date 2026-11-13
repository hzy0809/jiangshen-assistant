using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal sealed class PlayerStateEquipment
    {
        internal int Id, Main, Acc1, Acc2, Level, Owner;
        internal bool LevelPresent, OwnerPresent, Acc1Present, Acc2Present;
        internal string Json()
        {
            return "{\"equipment_id\":" + Id + ",\"level\":" + Level + ",\"owner_id\":" + Owner + ",\"main_id\":" + Main +
                ",\"accessory1_id\":" + Acc1 + ",\"accessory2_id\":" + Acc2 + ",\"level_present\":" + D.B(LevelPresent) +
                ",\"owner_present\":" + D.B(OwnerPresent) + ",\"accessory1_present\":" + D.B(Acc1Present) + ",\"accessory2_present\":" + D.B(Acc2Present) + "}";
        }
    }

    // A fixed projection of the pinned SCPlayerInfo_0x10 schema. In particular,
    // field 1 is NOT a result. No game protobuf implementation is executed.
    internal sealed class PlayerStateServerSnapshot
    {
        internal const int MaxBytes = 1048576;
        internal string CharacterId = "", Error;
        internal int CurrentFormation;
        internal long Money, Money64, Stone;
        internal bool MoneyPresent, Money64Present, StonePresent;
        internal readonly ArrayList Equipments = new ArrayList();
        internal static PlayerStateServerSnapshot Decode(byte[] bytes)
        {
            PlayerStateServerSnapshot s = new PlayerStateServerSnapshot();
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) { s.Error = "refresh_packet_bound"; return s; }
            try
            {
                Reader r = new Reader(bytes, 0, bytes.Length); bool player = false, form = false; Hashtable ids = new Hashtable();
                while (!r.End)
                {
                    int wire, field = r.Tag(out wire);
                    if (field == 3)
                    { if (player || wire != 2) throw new FormatException(); player = true; ReadPlayer(r.Message(), s); }
                    else if (field == 8)
                    { if (form || wire != 0) throw new FormatException(); form = true; s.CurrentFormation = Bounded(r.Varint(), 0, Int32.MaxValue); }
                    else if (field == 13)
                    {
                        if (wire != 2 || s.Equipments.Count >= 255) throw new FormatException();
                        PlayerStateEquipment e = ReadEquipment(r.Message());
                        if (ids.Contains(e.Id)) throw new FormatException(); ids.Add(e.Id, true); s.Equipments.Add(e);
                    }
                    else r.Skip(wire);
                }
                if (!player || !form || s.CharacterId == "" || s.Equipments.Count == 0) throw new FormatException();
                // Check the normal money expression even if one optional term
                // was omitted (protobuf default zero), preserving presence.
                long total = checked(s.Money + s.Money64); if (total < 0 || s.Stone < 0) throw new FormatException();
            }
            catch (Exception) { s.Error = "refresh_packet_schema"; }
            return s;
        }
        private static void ReadPlayer(Reader r, PlayerStateServerSnapshot s)
        {
            Hashtable seen = new Hashtable();
            while (!r.End)
            {
                int wire, f = r.Tag(out wire);
                if (f == 1 || f == 23 || f == 33 || f == 270)
                {
                    if (wire != 0 || seen.Contains(f)) throw new FormatException(); seen.Add(f, true); ulong v = r.Varint();
                    if (f == 1) { if (v == 0) throw new FormatException(); s.CharacterId = v.ToString(CultureInfo.InvariantCulture); }
                    else if (f == 270) { s.Money64 = Zig(v); s.Money64Present = true; }
                    else
                    {
                        if (v > UInt32.MaxValue) throw new FormatException(); long n = Zig(v);
                        if (f == 23) { s.Money = n; s.MoneyPresent = true; }
                        else { s.Stone = n; s.StonePresent = true; }
                    }
                }
                else r.Skip(wire);
            }
            if (!seen.Contains(1)) throw new FormatException();
        }
        private static PlayerStateEquipment ReadEquipment(Reader r)
        {
            PlayerStateEquipment e = new PlayerStateEquipment(); int seen = 0;
            while (!r.End)
            {
                int wire, f = r.Tag(out wire);
                if (f >= 1 && f <= 7 && f != 5)
                {
                    if (wire != 0 || (seen & (1 << f)) != 0) throw new FormatException(); seen |= 1 << f;
                    ulong v = r.Varint();
                    if (f == 1) e.Id = Bounded(v, 1, 255);
                    else if (f == 2) e.Main = Bounded(v, 1, 255);
                    else if (f == 3) { e.Acc1 = Bounded(v, 0, 255); e.Acc1Present = true; }
                    else if (f == 4) { e.Acc2 = Bounded(v, 0, 255); e.Acc2Present = true; }
                    else if (f == 6) { e.Level = Bounded(v, 0, 255); e.LevelPresent = true; }
                    else { e.Owner = Bounded(v, 0, UInt16.MaxValue); e.OwnerPresent = true; }
                }
                else r.Skip(wire);
            }
            if ((seen & 6) != 6) throw new FormatException();
            // Level/owner are optional with zero defaults in the actual schema.
            // Presence is exported, never silently promoted to a positive
            // level/owner witness by a downstream recovery disposition.
            return e;
        }
        private static int Bounded(ulong n, int min, int max)
        { if (n < (ulong)min || n > (ulong)max) throw new FormatException(); return (int)n; }
        private static long Zig(ulong v) { return (long)(v >> 1) ^ -((long)v & 1); }
        internal sealed class Reader
        {
            private readonly byte[] bytes; private readonly int end; private int position, fields;
            internal Reader(byte[] b, int start, int stop) { bytes = b; position = start; end = stop; }
            internal bool End { get { return position == end; } }
            internal ulong Varint()
            {
                ulong n = 0;
                for (int shift = 0; shift < 70; shift += 7)
                {
                    if (position >= end) throw new FormatException(); byte b = bytes[position++];
                    if (shift == 63 && b > 1) throw new FormatException(); n |= (ulong)(b & 127) << shift;
                    if ((b & 128) == 0) return n;
                }
                throw new FormatException();
            }
            internal int Tag(out int wire)
            {
                if (++fields > MaxBytes) throw new FormatException(); ulong n = Varint();
                if (n >> 3 == 0 || n >> 3 > 536870911) throw new FormatException(); wire = (int)(n & 7); return (int)(n >> 3);
            }
            internal Reader Message()
            { int n = Length(); Reader r = new Reader(bytes, position, position + n); position += n; return r; }
            private int Length() { ulong n = Varint(); if (n > (ulong)(end - position)) throw new FormatException(); return (int)n; }
            internal void Skip(int wire)
            {
                int n;
                if (wire == 0) { Varint(); return; }
                if (wire == 1) n = 8; else if (wire == 5) n = 4; else if (wire == 2) n = Length(); else throw new FormatException();
                if (n > end - position) throw new FormatException(); position += n;
            }
        }
        internal string Json()
        {
            StringBuilder b = new StringBuilder("{\"character_id\":" + D.Q(CharacterId) + ",\"current_formation\":" + CurrentFormation + ",\"equipments\":[");
            bool comma = false; foreach (PlayerStateEquipment e in Equipments) { if (comma) b.Append(','); comma = true; b.Append(e.Json()); }
            b.Append("],\"money\":").Append(checked(Money + Money64).ToString(CultureInfo.InvariantCulture)).Append(",\"stone\":").Append(Stone.ToString(CultureInfo.InvariantCulture));
            return b.Append(",\"money_present\":").Append(D.B(MoneyPresent)).Append(",\"money64_present\":").Append(D.B(Money64Present)).Append(",\"stone_present\":").Append(D.B(StonePresent)).Append('}').ToString();
        }
    }

    // One immutable ordinary Refresh invocation. This model never changes the
    // old business operation, sends a packet, calls a receipt handler, navigates
    // a root, or retries. The controller independently owns recovery admission.
    internal sealed class PlayerStateRefreshModel
    {
        private SharedDevelopmentContext context, before, queuedContext, stable;
        private DevelopmentCommand command;
        private Component login, queuedLogin, stableMain;
        private DevelopmentCallbackCapture capture;
        private PlayerStateServerSnapshot server;
        private byte[] receipt;
        private string receiptHash = "", rootLevel = "";
        private bool clientQuiescent, invoked, invocationStarted, callbackReturned, rebuilt, mainReady, stopped;
        private int stableFrame = -1;
        private readonly bool daily;
        private DailyTransport dailyTransport;
        private DailyPlayerSnapshot dailyServer;
        private uint dailyNow, dailyCycle;
        private int dailyTick;
        internal PlayerStateRefreshModel(bool dailySnapshot = false) { daily = dailySnapshot; }
        internal string Status = "IDLE", Reason = "not_invoked";
        internal Exception BindingError;
        internal string BindingStage = "";
        internal bool Pending { get { return Status == "QUEUED" || Status == "WAITING" || Status == "UNKNOWN" || Status == "BLOCKED" && invoked; } }
        internal bool MutationAttempted { get { return invoked; } }

        internal void Capture(SharedDevelopmentContext ctx)
        {
            // OBSERVED is a sealed refresh receipt. Later business requests
            // have their own current pending gates and must not rewrite this
            // completed refresh's idle/stability witness.
            if (Status == "OBSERVED") return;
            context = ctx; clientQuiescent = ctx != null && NormalResponsesIdle(ctx.Client);
            if (Status != "WAITING" || stopped) return;
            if (daily && unchecked((uint)(Environment.TickCount - dailyTick)) >= 12000)
            { Unknown("daily_refresh_receipt_or_rebuild_timeout"); return; }
            try { Observe(); }
            catch (Exception) { Unknown("refresh_observation_schema"); }
        }
        internal bool Queue(DevelopmentCommand next, out string why)
        {
            why = "";
            if (next == null || next.Action != (daily ? "daily_state_refresh" : "player_state_refresh") || !next.ValidId() || next.Sequence <= 0 || next.ExpectedRevision <= 0 ||
                next.TargetId != 0 || next.DesiredLevel != 0 || next.Mode != "" || next.MaxRequests != 0 || next.MaxMoney != 0 || next.MaxStone != 0 || next.MaxFood != 0 || next.MaxPractice != 0 || command != null || invoked || stopped)
            { why = "refresh_command_or_already_used"; return false; }
            object lockedClient = context == null ? null : context.Client; bool locked = false;
            string bindingStage = "client_lock";
            try
            {
                if (lockedClient == null || !System.Threading.Monitor.TryEnter(lockedClient, 0))
                { why = "refresh_client_busy_before_invocation"; Status = "REJECTED"; Reason = why; return false; }
                locked = true;
                bindingStage = "admission";
                if (!Admission(context, out why)) return false;
                if (daily)
                {
                    uint day;
                    bindingStage = "daily_clock";
                    why = DailyRefreshGate.ReadClock(context.Character, out dailyNow, out day, out dailyCycle);
                    bindingStage = "daily_transport";
                    dailyTransport = DailyTransport.Read(context.Client);
                    if (why != "" || dailyTransport == null) { if (why == "") why = "daily_refresh_transport_unavailable"; return false; }
                }
                bindingStage = "login_binding";
                queuedLogin = FindLogin(context); queuedContext = context; command = next.Copy(); Status = "QUEUED"; Reason = "queued"; return true;
            }
            catch (Exception error)
            {
                // Queue runs before any refresh invocation. Preserve the exact
                // schema refusal and a bounded diagnostic so a rejected daily
                // start can be explained without repeating a game operation.
                BindingError = error; BindingStage = bindingStage;
                why = error is ProbeFailure ? error.Message : "refresh_binding_schema:" + bindingStage + ":" + error.GetType().Name;
                if (why.Length > 256) why = why.Substring(0, 256);
                why = why.Replace('\r', '_').Replace('\n', '_');
                Status = "REJECTED"; Reason = why; return false;
            }
            finally { if (locked) System.Threading.Monitor.Exit(lockedClient); }
        }
        internal bool ExecuteGui(DevelopmentCommand next, DevelopmentPublication publish, out string why)
        {
            why = "";
            if (Status != "QUEUED" || next == null || command == null || !SameCommand(command, next) || stopped || !D.Gui || publish == null)
            { why = "refresh_gui_command_not_owned"; return false; }
            object lockedClient = context == null ? null : context.Client; bool locked = false;
            try
            {
                if (lockedClient == null || !System.Threading.Monitor.TryEnter(lockedClient, 0))
                { why = "refresh_client_busy_before_invocation"; Status = "REJECTED"; Reason = why; return false; }
                locked = true;
                if (!Admission(context, out why) || !context.SameIdentity(queuedContext) || !object.ReferenceEquals(FindLogin(context), queuedLogin))
                { if (why == "") why = "refresh_preinvoke_identity_changed"; Status = "REJECTED"; Reason = why; return false; }
                before = context; login = queuedLogin;
                MethodInfo method = D.Method(Model.GameType("LoginUI"), "Refresh", Type.EmptyTypes, false, typeof(bool));
                if (!method.IsPublic || method.DeclaringType != Model.GameType("LoginUI")) throw new ProbeFailure("refresh_method_metadata");
                // Fixed method and receiver are bound before publication; no
                // caller-supplied reflection names or request fields exist.
                Status = "WAITING"; Reason = "published_before_normal_refresh"; invoked = true; publish();
                if (daily)
                {
                    uint now, day, cycle;
                    string gate = DailyRefreshGate.ReadClock(before.Character, out now, out day, out cycle);
                    if (gate != "" || cycle != dailyCycle || !dailyTransport.Same(DailyTransport.Read(DailyTransport.CurrentClient(before.Character))) ||
                        !Admission(before, out why))
                    { invoked = false; Status = "REJECTED"; why = Reason = "daily_refresh_preinvoke_context_changed"; return false; }
                    dailyNow = now; dailyTick = Environment.TickCount;
                }
                invocationStarted = true;
                if (!(bool)method.Invoke(login, new object[0])) { Unknown("normal_refresh_refused_after_intent"); why = Reason; return false; }
                // Send installs synchronously. The normal main-thread dispatcher
                // cannot run between Send and this installation; no poll needed.
                if (!InstallCapture(out why)) { Unknown(why); return false; }
                Reason = "own_refresh_callback_armed"; return true;
            }
            catch (Exception)
            {
                if (!invocationStarted)
                {
                    invoked = false; Status = "REJECTED"; Reason = "refresh_not_invoked_before_normal_method";
                    throw;
                }
                if (invoked) Unknown("normal_refresh_or_publication_threw"); else { Status = "REJECTED"; Reason = "refresh_preinvoke_schema"; }
                why = Reason; return false;
            }
            finally { if (locked) System.Threading.Monitor.Exit(lockedClient); }
        }
        private static bool SameCommand(DevelopmentCommand a, DevelopmentCommand b)
        { return a.Sequence == b.Sequence && a.ExpectedRevision == b.ExpectedRevision && a.Action == b.Action && a.GoalKey() == b.GoalKey(); }
        private bool Admission(SharedDevelopmentContext ctx, out string why)
        {
            clientQuiescent = ctx != null && NormalResponsesIdle(ctx.Client);
            why = ctx == null ? "refresh_context_missing" : ctx.Gate != "PASS" ? ctx.Gate : !clientQuiescent ? "refresh_normal_responses_not_idle" : !ctx.Network.Observed || ctx.Network.Error != null ? "refresh_development_network_unobserved" : "";
            if (why == "" && daily)
            {
                Component main;
                why = MainReady(ctx, out main) ? "PASS" : "daily_refresh_main_or_overlay_unverified";
                if (why == "PASS")
                {
                    uint now, day, cycle;
                    string gate = DailyRefreshGate.ReadClock(ctx.Character, out now, out day, out cycle);
                    if (gate != "") why = gate;
                    else if (DailyRefreshProof.Verified(ctx.Character, cycle)) why = "daily_refresh_already_verified";
                }
            }
            else if (why == "") why = ctx.Panels(ctx.Find("EquipmentUI"), ctx.Find("EquipmentReinforceUI"));
            if (why == "PASS") why = "";
            return why == "";
        }
        private static bool NormalResponsesIdle(object client)
        {
            if (client == null || !System.Threading.Monitor.TryEnter(client, 0)) return false;
            try
            {
                IDictionary handlers = D.Read(client, "m_handlers") as IDictionary; IList messages = D.Read(client, "messages") as IList;
                if (handlers == null || messages == null || handlers.Count > 65536 || messages.Count != 0 || DevelopmentCallbackCapture.AnyCallbackExecuting) return false;
                Type handlerType = Model.FirstType("game.net.tcp_base+Handler");
                foreach (DictionaryEntry entry in handlers)
                {
                    if (!(entry.Key is ushort) || entry.Value == null || entry.Value.GetType() != handlerType) return false;
                    object value = D.Read(entry.Value, "life"); if (!(value is float)) return false; float life = (float)value;
                    // tcp_base.Run removes and times out only positive-life
                    // request handlers. Zero/negative-life handlers are normal
                    // passive push subscriptions and remain registered.
                    if (Single.IsNaN(life) || Single.IsInfinity(life) || life > 0f) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
            finally { System.Threading.Monitor.Exit(client); }
        }
        private static Component FindLogin(SharedDevelopmentContext ctx)
        {
            // DBLoaderUI.LoadDB_End binds Login/LoginUI, not Root/RootUI.
            // BaseAsset.Awake/Start sets its rmg to the Login root; leaving
            // login only hides this component. Include inactive objects too,
            // without creating or activating a login UI or accepting a prefab
            // whose runtime rmg has never been initialized.
            if (ctx == null || ctx.GlobalUi == null || ctx.UiRoot == null) throw new ProbeFailure("refresh_login_context_missing");
            Type loginType = Model.GameType("LoginUI"); MethodInfo find = null;
            foreach (MethodInfo method in typeof(Resources).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.Name != "FindObjectsOfTypeAll" || method.IsGenericMethod || method.ReturnType != typeof(UnityEngine.Object[])) continue;
                ParameterInfo[] parameters = method.GetParameters();
                // Unity's fixed API refers to desktop System.Type; bind it by
                // metadata just as Model.Find does for the legacy profile.
                if (parameters.Length != 1 || parameters[0].ParameterType.FullName != "System.Type") continue;
                if (find != null) throw new ProbeFailure("refresh_login_find_ambiguous");
                find = method;
            }
            if (find == null) throw new ProbeFailure("refresh_login_find_signature");
            UnityEngine.Object[] candidates = find.Invoke(null, new object[] { loginType }) as UnityEngine.Object[];
            if (candidates == null || candidates.Length > 512) throw new ProbeFailure("refresh_login_find_bound");
            Component result = null; int count = 0;
            foreach (UnityEngine.Object item in candidates)
            {
                Component candidate = item as Component;
                if (candidate == null || candidate.GetType() != loginType || candidate.gameObject.name != "LoginUI") continue;
                Component root = D.Read(candidate, "rmg") as Component;
                if (root == null || root.GetType() != Model.FirstType("RootMessenger") || root.gameObject.name != "Login" ||
                    root.transform.parent != null || candidate.transform.parent != root.transform || candidate.transform.root != root.transform) continue;
                result = candidate; count++;
            }
            if (count != 1) throw new ProbeFailure("refresh_login_uniqueness:" + count);
            MethodInfo callback = D.Method(result.GetType(), "sc_playerinfo", new Type[] { typeof(MemoryStream), typeof(object) }, false, typeof(void));
            if (!callback.IsPrivate || callback.DeclaringType != result.GetType()) throw new ProbeFailure("refresh_callback_metadata");
            return result;
        }
        private bool InstallCapture(out string why)
        {
            why = "refresh_handler_missing"; object client = before.Client;
            if (!System.Threading.Monitor.TryEnter(client, 0)) { why = "refresh_handler_lock_unavailable"; return false; }
            try
            {
                IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
                if (handlers == null || !handlers.Contains((ushort)16)) return false; object handler = handlers[(ushort)16];
                Delegate original = D.Read(handler, "handler") as Delegate;
                if (original == null || original.GetInvocationList().Length != 1 || !object.ReferenceEquals(original.Target, login) || original.Method.DeclaringType != Model.GameType("LoginUI") || original.Method.Name != "sc_playerinfo" || D.Read(handler, "arg") != null)
                { why = "refresh_handler_identity"; return false; }
                capture = daily ? DevelopmentCallbackCapture.InstallDailyRaw16(client, handler, command.OperationId, command.Sequence, original, InspectDailyBody, out why) :
                    DevelopmentCallbackCapture.InstallRaw16(client, handler, command.OperationId, command.Sequence, original, out why);
                return capture != null;
            }
            finally { System.Threading.Monitor.Exit(client); }
        }
        private void InspectDailyBody(byte[] body)
        {
            uint now, day, cycle;
            if (!dailyTransport.Same(DailyTransport.Read(DailyTransport.CurrentClient(before.Character))) ||
                DailyRefreshGate.ReadClock(before.Character, out now, out day, out cycle) != "" || cycle != dailyCycle)
                throw new ProbeFailure("daily_refresh_callback_context_changed");
            IList queued = D.Read(before.Client, "messages") as IList;
            if (queued == null || queued.Count > 64) throw new ProbeFailure("daily_refresh_callback_queue_unverified");
            foreach (object response in queued)
                if ((ushort)D.Read(response, "op") == 16) throw new ProbeFailure("daily_refresh_duplicate_response_queued");
            DailyPlayerSnapshot candidate = DailyPlayerSnapshot.Decode(body);
            if (candidate.CharacterId.ToString(CultureInfo.InvariantCulture) != before.CharacterId ||
                candidate.Zone != (int)D.Static(Model.FirstType("TimeUtil"), "server_time_zone") ||
                candidate.ServerTime < cycle || candidate.ServerTime >= cycle + 86400L ||
                candidate.ServerTime + 2L < dailyNow || candidate.ServerTime > now + 2L ||
                candidate.Next != cycle + 86400L || candidate.Next <= now)
                throw new ProbeFailure("daily_refresh_server_clock_or_role_mismatch");
            dailyServer = candidate;
        }
        private void Observe()
        {
            if (capture == null) { Unknown("refresh_capture_not_armed"); return; }
            DevelopmentCallbackWitness witness = capture.Read();
            if (witness.Calls == 0) { Reason = "waiting_normal_refresh_callback"; return; }
            callbackReturned = witness.Returned;
            if (receipt == null && witness.RawBytes != null)
            {
                receipt = witness.RawBytes;
                using (SHA256 hash = SHA256.Create()) receiptHash = BitConverter.ToString(hash.ComputeHash(receipt)).Replace("-", "").ToLowerInvariant();
            }
            if (witness.Calls != 1 || witness.NullResponse || witness.OriginalThrew || witness.Error != null)
            {
                // The pinned tcp_base.Run calls Handler.handle(null) when its
                // ten-second request lifetime expires. Network messages use a
                // non-null MemoryStream even for an empty payload. This is an
                // unknown request outcome, never an empty server receipt.
                Unknown(witness.Error ?? (witness.NullResponse ? "refresh_request_timeout" : witness.OriginalThrew ? "refresh_normal_callback_threw" : "refresh_multiple_callbacks")); return;
            }
            if (!callbackReturned) { Reason = "waiting_normal_refresh_callback_return"; return; }
            if (daily && dailyServer == null) { Unknown("daily_refresh_snapshot_missing"); return; }
            if (!daily && server == null)
            {
                server = PlayerStateServerSnapshot.Decode(receipt);
                if (server.Error != null) { Unknown(server.Error); return; }
                if (server.CharacterId != before.CharacterId) { Unknown("refresh_server_character_mismatch"); return; }
            }
            // CleanLevel/NextLevel legitimately leave roots unknown for frames.
            // A missing transient context never stops game requests or forces a
            // root. The controller continues ordinary Update sampling.
            if (context == null || context.Root == null || context.Character == null)
            { stable = null; Reason = "waiting_refresh_context_rebuild"; return; }
            if (daily && !dailyTransport.Same(DailyTransport.Read(DailyTransport.CurrentClient(context.Character))))
            { Unknown("daily_refresh_transport_changed"); return; }
            rootLevel = D.Read(context.Root, "LevelID") as string ?? "";
            if (rootLevel != "Campaign" && rootLevel != "Capital")
            { Status = "BLOCKED"; Reason = "refresh_new_root_unsupported:" + rootLevel; capture.DisarmAndRestore(); return; }
            if (context.Gate != "PASS") { stable = null; Reason = "waiting_refresh_context_ready:" + context.Gate; return; }
            if (context.CharacterId != before.CharacterId || !object.ReferenceEquals(context.Client, before.Client) || !object.ReferenceEquals(context.GlobalUi, before.GlobalUi) || !object.ReferenceEquals(FindLogin(context), login))
            { Unknown("refresh_rebuilt_ownership_mismatch"); return; }
            if (object.ReferenceEquals(context.Character, before.Character)) { stable = null; Reason = "waiting_refresh_character_replacement"; return; }
            string mismatch = daily ? dailyServer.Reconcile(context.Character) : Reconcile(context, server);
            if (mismatch != "") { Unknown(mismatch); return; }
            rebuilt = true; Component currentMain; mainReady = MainReady(context, out currentMain);
            if (!mainReady || !clientQuiescent || !context.Network.Observed || context.Network.Error != null)
            { stable = null; Reason = !mainReady ? "waiting_refresh_main_ready" : "waiting_refresh_normal_followups"; return; }
            int frame = Time.frameCount;
            if (stable == null || !context.SameIdentity(stable) || !object.ReferenceEquals(currentMain, stableMain)) { stable = context; stableMain = currentMain; stableFrame = frame; Reason = "waiting_refresh_stable_frame"; return; }
            if (frame <= stableFrame) { Reason = "waiting_refresh_advancing_frame"; return; }
            if (daily && !DailyRefreshProof.RecordSnapshot(context.Character, dailyTransport, dailyCycle, dailyServer.Next))
            { Unknown("daily_refresh_proof_context_changed"); return; }
            Status = "OBSERVED"; Reason = "server_snapshot_and_rebuilt_context_observed";
            capture.DisarmAndRestore();
        }
        private static bool MainReady(SharedDevelopmentContext ctx, out Component main)
        {
            main = null; int count = 0;
            foreach (UnityEngine.Object item in ctx.All)
            {
                Component c = item as Component;
                if (c != null && c.GetType() == Model.GameType("MainUI") && c.gameObject.activeInHierarchy &&
                    object.ReferenceEquals(D.Read(c, "rmg"), ctx.Root) && c.transform.root == ctx.Root.transform) { main = c; count++; }
            }
            if (count != 1) return false;
            if (!D.Enabled(main) || D.Loading(main) || D.Bool(D.Read(main, "first")) || !D.Bool(D.Read(main, "m_bInit")) || ctx.SubUi != null || ctx.Panels() != "PASS" ||
                !object.ReferenceEquals(D.Read(main, "rmg"), ctx.Root) || D.Bool(D.Read(ctx.Root, "bLoading")) || D.Int(D.Read(ctx.Root, "ExpectingCount")) != 0) return false;
            return true;
        }
        private static string Reconcile(SharedDevelopmentContext ctx, PlayerStateServerSnapshot snapshot)
        {
            IDictionary equipment = D.Read(ctx.Character, "equipments") as IDictionary, generals = D.Read(ctx.Character, "form_generals_") as IDictionary;
            IDictionary forms = D.Read(ctx.Character, "formations_") as IDictionary;
            if (equipment == null || generals == null || forms == null || equipment.Count != snapshot.Equipments.Count || D.Int(D.Read(ctx.Character, "cur_form")) != snapshot.CurrentFormation || !forms.Contains(snapshot.CurrentFormation)) return "refresh_rebuilt_collection_mismatch";
            IList spots = D.Read(forms[snapshot.CurrentFormation], "spotgen") as IList;
            if (spots == null || spots.Count > 8) return "refresh_rebuilt_formation_schema";
            foreach (PlayerStateEquipment e in snapshot.Equipments)
            {
                if (!equipment.Contains((byte)e.Id)) return "refresh_rebuilt_equipment_missing"; object local = equipment[(byte)e.Id];
                if (local == null || D.Int(D.Read(local, "id")) != e.Id || D.Int(D.Read(local, "enchant_level")) != e.Level || D.Int(D.Read(local, "idgeneral_template")) != e.Owner ||
                    D.Int(D.Read(local, "idequipment_main")) != e.Main || D.Int(D.Read(local, "idequipment_acc1")) != e.Acc1 || D.Int(D.Read(local, "idequipment_acc2")) != e.Acc2) return "refresh_rebuilt_equipment_mismatch";
                object main = D.Read(local, "main_");
                if (main == null || D.Int(D.Read(main, "id")) != e.Main) return "refresh_rebuilt_main_identity";
                if (e.Owner == UInt16.MaxValue) continue;
                int kind = D.Int(D.Read(main, "idequipment_type")), slot = kind == 1 ? 0 : kind == 2 ? 1 : -1;
                if (slot < 0 || !generals.Contains((ushort)e.Owner)) return "refresh_rebuilt_owner_missing";
                object general = generals[(ushort)e.Owner]; IList slots = D.Read(general, "equipments") as IList;
                if (D.Int(D.Read(general, "idgeneral_template")) != e.Owner || slots == null || slots.Count != 2 || !object.ReferenceEquals(slots[slot], local)) return "refresh_rebuilt_owner_slot_identity";
            }
            // Follow-up normal bonus54 may legitimately change resources; the
            // server values stay sealed independently rather than falsely
            // attributing all later resources to SC16.
            return "";
        }
        private void Unknown(string why)
        { Status = "UNKNOWN"; Reason = why; if (capture != null) capture.DisarmAndRestore(); }
        internal void Stop(string why)
        {
            stopped = true;
            // Preserve the first unknown result (for example the game's own
            // request timeout) in the durable final state during cleanup.
            if (invoked && Status != "OBSERVED" && Status != "UNKNOWN") Unknown("refresh_stopped:" + why);
            else if (!invoked) { Status = "STOPPED"; Reason = why; }
            if (capture != null) capture.DisarmAndRestore();
        }
        internal void NotInvoked(string why)
        {
            if (capture != null || invocationStarted) { Stop(why); return; }
            stopped = true; invoked = false; Status = "REJECTED"; Reason = why;
        }
        internal string Json()
        {
            if (daily) return "{\"schema\":1,\"status\":" + D.Q(Status) + ",\"reason\":" + D.Q(Reason) +
                ",\"operation_id\":" + D.Q(command == null ? "" : command.OperationId) + ",\"sequence\":" + (command == null ? 0 : command.Sequence) +
                ",\"expected_revision\":" + (command == null ? 0 : command.ExpectedRevision) + ",\"character_id\":" + D.Q(before == null ? "" : before.CharacterId) +
                ",\"opcode\":16,\"invoked\":" + D.B(invoked) + ",\"callback_returned\":" + D.B(callbackReturned) + ",\"context_rebuilt\":" + D.B(rebuilt) +
                ",\"server_time\":" + (dailyServer == null ? "null" : dailyServer.ServerTime.ToString(CultureInfo.InvariantCulture)) +
                ",\"server_cycle\":" + (dailyServer == null ? "null" : dailyCycle.ToString(CultureInfo.InvariantCulture)) + ",\"receipt_hash\":" + D.Q(receiptHash) + "}";
            return "{\"schema\":1,\"status\":" + D.Q(Status) + ",\"reason\":" + D.Q(Reason) + ",\"operation_id\":" + D.Q(command == null ? "" : command.OperationId) +
                ",\"sequence\":" + (command == null ? 0 : command.Sequence) + ",\"expected_revision\":" + (command == null ? 0 : command.ExpectedRevision) +
                ",\"character_id\":" + D.Q(before == null ? "" : before.CharacterId) + ",\"opcode\":16,\"mutation_attempted\":" + D.B(invoked) +
                ",\"receipt_capture\":" + (receipt == null ? "null" : "{\"encoding\":\"base64\",\"bytes\":" + receipt.Length + ",\"data\":" + D.Q(Convert.ToBase64String(receipt)) + "}") +
                ",\"receipt_hash\":" + D.Q(receiptHash) + ",\"callback_returned\":" + D.B(callbackReturned) + ",\"server_snapshot\":" + (server == null || server.Error != null ? "null" : server.Json()) +
                ",\"context_rebuilt\":" + D.B(rebuilt) + ",\"root_level\":" + D.Q(rootLevel) + ",\"main_ready\":" + D.B(mainReady) +
                ",\"normal_responses_idle\":" + D.B(clientQuiescent) + ",\"outbound_transport_completion\":\"UNVERIFIED\"}";
        }
    }
}
