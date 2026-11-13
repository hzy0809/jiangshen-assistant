using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate void DailyPopupEventPublisher(string name, string fields);
    internal delegate bool DailyPopupOwnershipCheck(Component popup, Delegate close);

    // Passive on Update; the only mutation is an explicitly queued command on
    // OnGUI. Appearance identity is local to this bridge session, never a task
    // receipt or a permanent suppression of a Unity instance.
    internal sealed class DailyPopupModel
    {
        private sealed class Window
        {
            internal Component Ui, Previous, CallbackOwner;
            internal Delegate Close;
            internal object Items;
            internal DateTime Shown;
            internal string Type, Callback = "", Kind = "", Why = "", State = "", AppearanceStamp = "";
            internal string MessageBoxObservation = "null";
            internal int Instance, Generation, EventSequence;
            internal bool Present, Visible, Loading, Attempted;
        }
        private sealed class Intent
        {
            internal Command Command;
            internal Window Window;
            internal D Context;
            internal DailyTransport Transport;
            internal int Tick;
        }
        private readonly Dictionary<string, Window> tracked = new Dictionary<string, Window>();
        private readonly List<Window> open = new List<Window>();
        private readonly DailyPopupEventPublisher emit;
        private D context;
        private DailyTransport transport;
        private Intent queued;
        private bool blocked, stopped;
        private int epoch, eventSequence;
        private string stamp = "", status = "IDLE", reason = "not_observed";
        private string operationId = "", result = "", resultReason = "";
        private string lastEvent = "null";
        private int resultGeneration, resultInstance;
        internal DailyPopupModel(DailyPopupEventPublisher publishEvent) { emit = publishEvent; }
        internal int Revision { get; private set; }
        internal bool Busy { get { return queued != null; } }
        internal bool GuiInvocationAttempted { get; private set; }
        internal bool OutcomeUnknown { get { return result == "UNKNOWN"; } }

        internal void Capture(Model model, bool admissionBlocked)
        {
            blocked = admissionBlocked; open.Clear();
            try
            {
                D current = DevelopmentContextReader.DailyWood(model, false);
                if (context != null && !current.SameOwnership(context))
                {
                    // A new root/account cannot inherit a prior appearance or
                    // command, even when Unity recycles a numeric instance id.
                    tracked.Clear(); queued = null; lastEvent = "null";
                }
                context = current;
                transport = DailyTransport.Read(DailyTransport.CurrentClient(current.Character));
                if (current.OwnershipGate() != "PASS" || transport == null)
                {
                    status = "BLOCKED"; reason = "popup_identity_unavailable";
                    PublishRevision(); return;
                }
                Dictionary<string, bool> seen = new Dictionary<string, bool>();
                foreach (UnityEngine.Object item in current.All)
                {
                    Component ui = item as Component;
                    if (ui == null) continue;
                    string type = ui.GetType().Name;
                    if (type != "BattleResultUI" && type != "MessageBox" && type != "SpecialGetUI" && type != "ConsumeUI") continue;
                    bool visible = D.Visible(ui), loading = D.Loading(ui);
                    if (!visible && !loading) continue;
                    string key = type + ":" + ui.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                    seen[key] = true;
                    Window prior;
                    tracked.TryGetValue(key, out prior);
                    Window sample = ReadWindow(ui, type, visible, loading);
                    // Loading is observable, but is not yet an appearance. The
                    // stable visible_time/list/callback tuple binds the actual
                    // shown content, including hidden-and-reshown cached UIs.
                    bool appeared = visible && !loading && (prior == null || !prior.Present || !prior.Visible || prior.Loading || !SameContent(prior, sample));
                    if (prior == null || appeared)
                    {
                        sample.Generation = appeared ? checked(++epoch) : 0;
                        tracked[key] = sample; prior = sample;
                        if (appeared)
                        {
                            Describe(sample);
                            eventSequence++;
                            sample.EventSequence = eventSequence;
                            lastEvent = "{\"event_sequence\":" + eventSequence + ",\"window_type\":" + D.Q(sample.Type) +
                                ",\"instance_id\":" + sample.Instance + ",\"appearance_generation\":" + sample.Generation +
                                ",\"appearance_stamp\":" + D.Q(sample.AppearanceStamp) +
                                ",\"message_box\":" + sample.MessageBoxObservation + "}";
                            if (emit != null) emit("daily_popup_appeared", "\"event_sequence\":" + eventSequence + ",\"window\":" + WindowJson(sample));
                        }
                    }
                    else
                    {
                        sample.Generation = prior.Generation; sample.EventSequence = prior.EventSequence; sample.Attempted = prior.Attempted;
                        tracked[key] = sample; prior = sample;
                    }
                    prior.Present = true;
                    Describe(prior); open.Add(prior);
                }
                foreach (KeyValuePair<string, Window> item in tracked) if (!seen.ContainsKey(item.Key)) item.Value.Present = false;
                status = open.Count == 0 ? "IDLE" : "OPEN";
                reason = open.Count == 0 ? "no_popup" : "popup_observed";
                if (stopped) { status = "STOPPED"; reason = "user_stop"; }
                else if (current.OwnershipGate() != "PASS" || transport == null)
                { status = "BLOCKED"; reason = "popup_identity_unavailable"; }
            }
            catch (Exception error) { status = "BLOCKED"; reason = "popup_observation:" + error.Message; }
            PublishRevision();
        }

        private Window ReadWindow(Component ui, string type, bool visible, bool loading)
        {
            Window w = new Window { Ui = ui, Type = type, Instance = ui.GetInstanceID(), Visible = visible, Loading = loading };
            w.Shown = (DateTime)D.Read(ui, "visible_time");
            w.AppearanceStamp = w.Shown.Ticks.ToString(CultureInfo.InvariantCulture);
            if (type != "BattleResultUI")
            {
                w.Why = "popup_unknown_or_consumption_confirmation";
                if (type == "MessageBox" && visible && !loading)
                    w.MessageBoxObservation = ReadMessageBox(ui);
                return w;
            }
            w.Close = D.Read(ui, "OnClose") as Delegate;
            w.Previous = D.Read(ui, "m_prevUI") as Component;
            w.Items = D.Read(ui, "m_vctBonuses");
            // Runtime reference hashes stay with these live objects across
            // bridge reconnects in the same game process. visible_time supplies
            // the stock appearance clock; identity changes also start a new
            // generation when a cached UI receives new content without hiding.
            w.AppearanceStamp += ":" + ReferenceStamp(w.Items) + ":" + ReferenceStamp(w.Close) + ":" + ReferenceStamp(w.Previous);
            w.CallbackOwner = w.Close == null ? null : w.Close.Target as Component;
            w.Callback = w.Close == null ? "" : w.Close.Method.DeclaringType.FullName + "." + w.Close.Method.Name;
            if (!ReferenceEquals(ui, D.Static(Model.FirstType("__BaseUI"), "battle_result_ui")) ||
                ui.GetType() != Model.FirstType("BattleResultUI") || !(w.Items is IList) ||
                D.Bool(D.Read(ui, "m_bCampaign")) || D.Bool(D.Read(ui, "m_bBattleRank")) ||
                D.Int(D.Read(ui, "m_iTreasureCount")) != 0 || (string)D.Read(ui, "mine_ui") != "" || D.Read(ui, "m_clit") != null)
            { w.Why = "popup_reward_mode_unknown"; return w; }
            if (w.Close == null)
            {
                // Stock ordinary OK falls back to this exact RootMessenger.
                if (ReferenceEquals(w.Previous, context.UiRoot)) w.Kind = "restore_previous";
                else w.Why = "popup_restore_owner_unknown";
                return w;
            }
            if (w.Close.GetInvocationList().Length != 1 || w.CallbackOwner == null || !KnownCallback(w.CallbackOwner, w.Close))
            { w.Why = "popup_callback_not_allowlisted"; return w; }
            w.Kind = "hide_and_onclose";
            return w;
        }

        // Diagnostics are deliberately separate from Close/Kind/appearance
        // admission. Reading an OnOK delegate never authorizes or invokes it.
        // No text, argument values, object ToString(), or delegate execution is
        // used here. A missing field is explicit and cannot turn into consent.
        private string ReadMessageBox(Component ui)
        {
            try
            {
                List<string> missing = new List<string>();
                object onOk = ObserveField(ui, "OnOK", missing);
                object onClose = ObserveField(ui, "OnClose", missing);
                object args = ObserveField(ui, "OnOK_objs", missing);
                object oldRoot = ObserveField(ui, "old_rmg", missing);
                Component guild = null;
                try { guild = context.Find("GuildUI_3"); }
                catch { missing.Add("guild_owner"); }
                object subUi = null;
                try { subUi = context.SubUi; }
                catch { missing.Add("main_subui"); }
                object guildRoot = guild == null ? null : ObserveField(guild, "rmg", missing);
                StringBuilder b = new StringBuilder("{\"interface\":\"message-box-observation-v1\",\"read_only\":true");
                b.Append(",\"title\":").Append(TextFingerprint(ObserveField(ui, "Title", missing)));
                b.Append(",\"message\":").Append(TextFingerprint(ObserveField(ui, "Message", missing)));
                b.Append(",\"on_ok\":").Append(DelegateObservation(onOk, guild, subUi));
                b.Append(",\"on_close\":").Append(DelegateObservation(onClose, guild, subUi));
                b.Append(",\"on_ok_objs_is_null\":").Append(missing.Contains("OnOK_objs") ? "null" : D.B(args == null));
                b.Append(",\"old_rmg\":").Append(ObjectIdentity(oldRoot));
                b.Append(",\"old_rmg_is_context_root\":").Append(missing.Contains("old_rmg") ? "null" : D.B(oldRoot != null && ReferenceEquals(oldRoot, context.UiRoot)));
                b.Append(",\"old_rmg_is_guild_root\":").Append(guild == null || missing.Contains("old_rmg") || missing.Contains("rmg") ? "null" : D.B(oldRoot != null && ReferenceEquals(oldRoot, guildRoot)));
                string[] flags = { "bCancel", "bNoButton", "bModal", "bGoButton", "isGold", "isPicMode" };
                b.Append(",\"flags\":{");
                for (int i = 0; i < flags.Length; i++)
                {
                    if (i != 0) b.Append(',');
                    object flag = ObserveField(ui, flags[i], missing);
                    if (!(flag is bool) && !missing.Contains(flags[i])) missing.Add(flags[i]);
                    b.Append(D.Q(flags[i])).Append(':').Append(flag is bool ? D.B((bool)flag) : "null");
                }
                b.Append("},\"guild\":").Append(ObjectIdentity(guild));
                b.Append(",\"main_subui\":").Append(ObjectIdentity(subUi));
                b.Append(",\"main_subui_is_guild\":").Append(guild == null || missing.Contains("main_subui") ? "null" : D.B(ReferenceEquals(subUi, guild)));
                b.Append(",\"guild_visible\":").Append(guild == null ? "null" : D.B(D.Visible(guild)));
                b.Append(",\"guild_page\":").Append(guild == null ? "null" : FixedPage(ObserveField(guild, "page", missing)));
                b.Append(",\"guild_subwindow\":").Append(guild == null ? "null" : FixedPage(ObserveField(guild, "subwindow", missing)));
                b.Append(",\"guild_tree_phase\":").Append(guild == null ? "null" : SafePhase(ObserveField(guild, "tree_phase", missing)));
                b.Append(",\"unavailable_fields\":[");
                for (int i = 0; i < missing.Count; i++) { if (i != 0) b.Append(','); b.Append(D.Q(missing[i])); }
                return b.Append("]}").ToString();
            }
            catch
            {
                // Diagnostic failure must not suppress the normal BLOCKED
                // window or publish exception text containing game content.
                return "{\"interface\":\"message-box-observation-v1\",\"read_only\":true,\"status\":\"unavailable\"}";
            }
        }

        private static object ObserveField(object owner, string name, List<string> missing)
        {
            try { return D.Read(owner, name); }
            catch { missing.Add(name); return null; }
        }
        private static string BoundedCodeName(string value)
        { return value == null ? "" : value.Substring(0, Math.Min(value.Length, 256)); }
        private static string ObjectIdentity(object value)
        {
            if (ReferenceEquals(value, null)) return "null";
            UnityEngine.Object unity = value as UnityEngine.Object;
            return "{\"type\":" + D.Q(BoundedCodeName(value.GetType().FullName)) +
                ",\"reference_id\":" + D.Q(ReferenceStamp(value)) +
                ",\"instance_id\":" + (ReferenceEquals(unity, null) ? "null" : unity.GetInstanceID().ToString(CultureInfo.InvariantCulture)) + "}";
        }
        private static string DelegateObservation(object value, Component guild, object subUi)
        {
            Delegate callback = value as Delegate;
            if (value != null && callback == null) return "{\"status\":\"unexpected_type\"}";
            if (callback == null) return "{\"is_null\":true,\"invocation_count\":0,\"invocations\":[],\"truncated\":false}";
            Delegate[] calls = callback.GetInvocationList();
            StringBuilder b = new StringBuilder("{\"is_null\":false,\"invocation_count\":" + calls.Length + ",\"invocations\":[");
            for (int i = 0; i < calls.Length && i < 16; i++)
            {
                if (i != 0) b.Append(',');
                Type declaring = calls[i].Method.DeclaringType;
                b.Append("{\"declaring_type\":").Append(D.Q(BoundedCodeName(declaring == null ? "" : declaring.FullName)));
                b.Append(",\"method\":").Append(D.Q(BoundedCodeName(calls[i].Method.Name)));
                object target = calls[i].Target;
                b.Append(",\"target\":").Append(ObjectIdentity(target));
                b.Append(",\"target_is_guild\":").Append(guild == null ? "null" : D.B(ReferenceEquals(target, guild)));
                b.Append(",\"target_is_main_subui\":").Append(subUi == null ? "null" : D.B(ReferenceEquals(target, subUi))).Append('}');
            }
            return b.Append("],\"truncated\":").Append(D.B(calls.Length > 16)).Append('}').ToString();
        }
        private static string TextFingerprint(object value)
        {
            if (value == null) return "null";
            string text = value as string;
            if (text == null) return "{\"status\":\"unexpected_type\"}";
            int units = Math.Min(text.Length, 4096);
            byte[] digest;
            using (SHA256 hash = SHA256.Create()) digest = hash.ComputeHash(Encoding.UTF8.GetBytes(text.Substring(0, units)));
            StringBuilder hex = new StringBuilder(64);
            foreach (byte part in digest) hex.Append(part.ToString("x2", CultureInfo.InvariantCulture));
            return "{\"utf16_length\":" + text.Length + ",\"hashed_utf16_units\":" + units +
                ",\"sha256_utf8_prefix\":" + D.Q(hex.ToString()) + ",\"truncated\":" + D.B(units != text.Length) + "}";
        }
        private static string FixedPage(object value)
        {
            string page = value as string;
            if (page == "" || page == "donate" || page == "lottery" || page == "tree") return "{\"fixed_value\":" + D.Q(page) + "}";
            return TextFingerprint(value);
        }
        private static string SafePhase(object value)
        {
            if (value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value != null && value.GetType().IsEnum)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            return TextFingerprint(value);
        }

        private bool KnownCallback(Component owner, Delegate close)
        {
            // Source: SignInUI.sc_all_bonus_get_new, ReputationUI.sc_reputation_salary,
            // AllBonusUI.sc_get_all_bonus_0x348/getArenaBonus, and event reward Set
            // routes. Exact inherited EnableAll only restores input; arbitrary
            // same-named overrides, anonymous callbacks and purchase callbacks
            // are never admitted.
            string type = owner.GetType().Name;
            bool standard = type == "SignInUI" || type == "ReputationUI" || type == "AllBonusUI" || type == "PremiumUI" || type == "VipWealUI";
            if (!standard || !ReferenceEquals(D.Read(owner, "rmg"), context.UiRoot) || owner.gameObject != context.GlobalUi.gameObject) return false;
            if (owner.GetType() != (type == "ReputationUI" ? Model.FirstType(type) : Model.GameType(type))) return false;
            if (close.Method == D.Method(Model.FirstType("__BaseUI"), "EnableAll", Type.EmptyTypes, false, typeof(void))) return true;
            if (type == "AllBonusUI" && close.Method == D.Method(owner.GetType(), "closeArenaBonus", Type.EmptyTypes, false, typeof(void))) return true;
            if (type == "SignInUI" && close.Method == D.Method(owner.GetType(), "closeBonus", Type.EmptyTypes, false, typeof(void))) return true;
            return false;
        }

        private static bool SameContent(Window a, Window b)
        {
            return ReferenceEquals(a.Ui, b.Ui) && a.Instance == b.Instance && a.Type == b.Type && a.Shown == b.Shown &&
                ReferenceEquals(a.Close, b.Close) && ReferenceEquals(a.Items, b.Items) && ReferenceEquals(a.Previous, b.Previous);
        }
        private static string ReferenceStamp(object value)
        { return value == null ? "null" : RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture); }
        private void Describe(Window w)
        {
            w.State = w.Attempted ? "ATTEMPTED" : stopped ? "STOPPED" : w.Loading || !w.Visible ? "LOADING" :
                w.Why != "" ? "BLOCKED" : blocked ? "OWNED" : "AVAILABLE";
        }

        internal bool Queue(Command command, out string why)
        {
            why = "popup_command_unavailable";
            if (stopped || blocked || queued != null || context == null || context.OwnershipGate() != "PASS" || transport == null ||
                command.ExpectedRevision != Revision || command.DailyPopupCharacterId.ToString(CultureInfo.InvariantCulture) != context.CharacterId ||
                !Command.ValidNonce(command.OperationId) || command.DailyPopupAction != "confirm" || open.Count != 1) return false;
            Window w = open[0];
            if (w.Type != command.DailyPopupType || w.Instance != command.DailyPopupInstance || w.Generation != command.DailyPopupGeneration ||
                w.Generation == 0 || w.State != "AVAILABLE" || w.Attempted) return false;
            queued = new Intent { Command = command, Window = w, Context = context, Transport = transport, Tick = Environment.TickCount };
            GuiInvocationAttempted = false; Revision++; why = ""; return true;
        }

        internal bool HandoffOwned(DailyPopupOwnershipCheck owns)
        { return owns != null && open.Count == 1 && open[0].Type == "BattleResultUI" && owns(open[0].Ui, open[0].Close); }

        internal bool ExecuteGui(Model model, bool admissionBlocked, DevelopmentPublication publish, DailyMonsterInvocationGate begin, out string why)
        {
            why = "popup_not_queued";
            Intent intent = queued;
            if (intent == null) return false;
            object client = intent.Context.Client;
            if (client == null || !Monitor.TryEnter(client, 0))
            { why = "popup_client_callback_busy"; NotInvoked(why); return false; }
            try
            {
                publish(); Capture(model, admissionBlocked);
                if (stopped || blocked || !D.Gui || context == null || !context.SameOwnership(intent.Context) ||
                    transport == null || !transport.Same(intent.Transport) || open.Count != 1 ||
                    unchecked((uint)(Environment.TickCount - intent.Tick)) >= 1500)
                { why = "popup_gui_admission_changed"; NotInvoked(why); return false; }
                Window current = open[0];
                if (current.Generation != intent.Window.Generation || current.State != "AVAILABLE" ||
                    !SameContent(current, intent.Window) || !SafePanels(current) || !begin())
                { why = "popup_exact_window_changed_or_owned"; NotInvoked(why); return false; }
                // No publication or asynchronous work between the last exact
                // recheck and stock invocation. Consume this appearance first:
                // an exception after Hide/OnClose must never replay it.
                Window fresh = ReadWindow(current.Ui, current.Type, D.Visible(current.Ui), D.Loading(current.Ui));
                if (!fresh.Visible || fresh.Loading || fresh.Why != "" || !SameContent(current, fresh))
                { why = "popup_final_identity_changed"; NotInvoked(why); return false; }
                queued = null; current.Attempted = true; GuiInvocationAttempted = true;
                operationId = intent.Command.OperationId; resultInstance = current.Instance; resultGeneration = current.Generation;
                result = "UNKNOWN"; resultReason = "popup_stock_close_unverified";
                D.Call(current.Ui, "Hide");
                if (current.Close != null) current.Close.DynamicInvoke(new object[0]);
                else D.Call(current.Previous, "ToEnable");
                bool closed = !D.Visible(current.Ui) && !D.Loading(current.Ui) && D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled"));
                result = closed ? "COMPLETED" : "UNKNOWN";
                resultReason = why = closed ? "popup_stock_close_completed" : "popup_stock_close_unverified";
                PublishRevision(); return closed;
            }
            catch (Exception error)
            {
                queued = null; why = "popup_stock_close:" + error.Message;
                if (GuiInvocationAttempted) { result = "UNKNOWN"; resultReason = why; }
                else { result = "REJECTED"; resultReason = why; }
                PublishRevision(); return false;
            }
            finally { Monitor.Exit(client); }
        }

        private bool SafePanels(Window w)
        {
            // A reward can disable Global.extern_enabled. RewardPanels permits
            // only that known input gate and still checks current root/overlays.
            List<Component> allowed = new List<Component>(); allowed.Add(w.Ui);
            if (w.CallbackOwner != null) allowed.Add(w.CallbackOwner);
            object sub = context.SubUi;
            if (sub != null)
            {
                Component panel = sub as Component;
                if (panel == null) return false;
                string type = panel.GetType().Name;
                bool own = ReferenceEquals(panel, w.CallbackOwner) ||
                    w.CallbackOwner != null && w.CallbackOwner.GetType().Name == "ReputationUI" && (type == "AllBonusUI" || type == "SignInUI");
                if (!own) return false;
                allowed.Add(panel);
            }
            return context.RewardPanels(allowed.ToArray()) == "PASS";
        }
        internal void NotInvoked(string why) { queued = null; GuiInvocationAttempted = false; result = "REJECTED"; resultReason = why; PublishRevision(); }
        internal void Stop() { stopped = true; queued = null; status = "STOPPED"; reason = "user_stop"; PublishRevision(); }
        private void PublishRevision()
        {
            string next = BodyJson();
            if (next != stamp) { stamp = next; Revision++; }
        }
        private static string WindowJson(Window w)
        {
            return "{\"window_type\":" + D.Q(w.Type) + ",\"instance_id\":" + w.Instance + ",\"appearance_generation\":" + w.Generation +
                ",\"event_sequence\":" + w.EventSequence +
                ",\"appearance_stamp\":" + D.Q(w.AppearanceStamp) +
                ",\"visible\":" + D.B(w.Visible) + ",\"loading\":" + D.B(w.Loading) + ",\"classification\":" + D.Q(w.Kind) +
                ",\"callback\":" + D.Q(w.Callback) + ",\"status\":" + D.Q(w.State) + ",\"reason\":" + D.Q(w.Why) +
                ",\"attempted\":" + D.B(w.Attempted) + ",\"message_box\":" + w.MessageBoxObservation + "}";
        }
        private string BodyJson()
        {
            StringBuilder b = new StringBuilder("\"interface\":\"scoped-daily-popup-v1\",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) +
                ",\"character_id\":" + D.Q(context == null ? "" : context.CharacterId) + ",\"event_sequence\":" + eventSequence +
                ",\"last_event\":" + lastEvent + ",\"windows\":[");
            for (int i = 0; i < open.Count; i++) { if (i != 0) b.Append(','); b.Append(WindowJson(open[i])); }
            b.Append("],\"command\":{\"operation_id\":" + D.Q(operationId) + ",\"status\":" + D.Q(result) + ",\"reason\":" + D.Q(resultReason) +
                ",\"instance_id\":" + resultInstance + ",\"appearance_generation\":" + resultGeneration + ",\"invocation_attempted\":" + D.B(GuiInvocationAttempted) + "}");
            return b.ToString();
        }
        internal string Json() { return "{\"revision\":" + Revision + "," + BodyJson() + "}"; }
    }
}
