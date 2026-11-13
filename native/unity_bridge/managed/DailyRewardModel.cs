using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // The controller rebuilds context and the overlay inventory on each call.
    // This provider runs on the game GUI thread and performs reads only.
    internal delegate D DailyRewardContextProvider();
    internal delegate bool DailyRewardInvocationGate();

    // The fixed adapter observes synchronously before forwarding the original
    // 0x164 callback, inside tcp_base.Run's monitor and after it dequeues the
    // current message. It always forwards the original callback once.
    internal delegate DevelopmentCallbackCapture DailyRewardCallbackInstaller(object client, object handler,
        string id, int sequence, Delegate original, Action<byte[]> read, out string why);

    internal sealed class DailyRewardReceipt
    {
        internal const ushort Opcode = 0x164;
        internal bool Accepted, Finished;
        internal uint Category, Refresh;
        internal int Treasures;

        // Pinned SCActivityReward_0x164 and db_character_activity_reward.
        // Treasure contents are interpreted only by the original callback.
        internal static DailyRewardReceipt Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2 || bytes.Length > 65536) throw new FormatException();
            DailyRewardReceipt receipt = new DailyRewardReceipt();
            int offset = 0;
            bool stateSeen = false, rewardSeen = false;
            while (offset < bytes.Length)
            {
                switch (ReadUInt(bytes, ref offset, bytes.Length))
                {
                    case 8:
                        if (stateSeen) throw new FormatException();
                        stateSeen = true;
                        receipt.Accepted = ReadBool(bytes, ref offset, bytes.Length);
                        break;
                    case 18:
                        if (++receipt.Treasures > 64) throw new FormatException();
                        offset = EndOfBlock(bytes, ref offset);
                        break;
                    case 26:
                        if (rewardSeen) throw new FormatException();
                        rewardSeen = true;
                        int end = EndOfBlock(bytes, ref offset);
                        int seen = 0;
                        while (offset < end)
                        {
                            uint tag = ReadUInt(bytes, ref offset, end);
                            if (tag != 8 && tag != 16 && tag != 24) throw new FormatException();
                            int bit = 1 << (int)(tag >> 3);
                            if ((seen & bit) != 0) throw new FormatException();
                            seen |= bit;
                            if (tag == 8) receipt.Category = ReadUInt(bytes, ref offset, end);
                            else if (tag == 16) receipt.Refresh = ReadUInt(bytes, ref offset, end);
                            else receipt.Finished = ReadBool(bytes, ref offset, end);
                        }
                        break;
                    default: throw new FormatException();
                }
            }
            if (!stateSeen || receipt.Accepted && (!rewardSeen || receipt.Category < 2 ||
                receipt.Category > 6 || receipt.Refresh == 0 || receipt.Treasures == 0) ||
                !receipt.Accepted && (rewardSeen || receipt.Treasures != 0)) throw new FormatException();
            return receipt;
        }

        private static int EndOfBlock(byte[] bytes, ref int offset)
        {
            uint length = ReadUInt(bytes, ref offset, bytes.Length);
            if (length == 0 || length > bytes.Length - offset) throw new FormatException();
            return offset + (int)length;
        }

        private static bool ReadBool(byte[] bytes, ref int offset, int end)
        {
            uint value = ReadUInt(bytes, ref offset, end);
            if (value > 1) throw new FormatException();
            return value == 1;
        }

        private static uint ReadUInt(byte[] bytes, ref int offset, int end)
        {
            uint result = 0;
            for (int shift = 0; shift <= 28 && offset < end; shift += 7)
            {
                byte next = bytes[offset++];
                if (shift == 28 && next > 15) break;
                result |= (uint)(next & 127) << shift;
                if ((next & 128) == 0) return result;
            }
            throw new FormatException();
        }
    }

    internal sealed class DailyRewardModel
    {
        private const uint WaitMs = 18000;
        private const int MaxTasks = 255, MaxOperations = 4096;

        private sealed class Row
        {
            internal object Task, Template;
            internal int Current, Required, Refresh;
            internal bool Closed;

            internal bool Same(Row before)
            {
                return ReferenceEquals(Task, before.Task) && ReferenceEquals(Template, before.Template) &&
                    Current == before.Current && Required == before.Required && Refresh == before.Refresh && Closed == before.Closed;
            }
        }

        private sealed class Category
        {
            internal object Reward, Template, PanelRows;
            internal readonly List<Row> Rows = new List<Row>();
            internal uint Refresh;
            internal bool Finished;
            internal int Required, Current;
            internal string Reason = "daily_reward_data_unavailable";

            internal bool SameRows(Category before)
            {
                if (before == null || !ReferenceEquals(Reward, before.Reward) || !ReferenceEquals(Template, before.Template) ||
                    !ReferenceEquals(PanelRows, before.PanelRows) || Current != before.Current || Required != before.Required ||
                    Rows.Count != before.Rows.Count) return false;
                for (int i = 0; i < Rows.Count; i++) if (!Rows[i].Same(before.Rows[i])) return false;
                return true;
            }

            internal bool Same(Category before)
            { return SameRows(before) && Refresh == before.Refresh && Finished == before.Finished && Reason == before.Reason; }
        }

        private sealed class Frame
        {
            internal D Context;
            internal Component Panel, Dialog;
            internal DailyTransport Transport;
            internal object Sub, RoleRows, Rewards, Templates, Bonuses;
            internal Delegate DialogClose;
            internal ulong Role;
            internal uint Now, Cycle, NextResource, Selection;
            internal int QueuedRewardPackets;
            internal bool NetworkObserved, NetworkIdle, PanelVisible, PanelEnabled, PanelLoading, DialogVisible, DialogEnabled, DialogLoading, DialogCached;
            internal string Error = "", Panels = "", RewardPanels = "";
            internal readonly Category[] Categories = new Category[7];

            internal bool SameOwner(Frame before)
            {
                return before != null && Context != null && Context.SameOwnership(before.Context) && Role != 0 && Role == before.Role &&
                    Panel != null && ReferenceEquals(Panel, before.Panel) && Dialog != null && ReferenceEquals(Dialog, before.Dialog) &&
                    Transport != null && Transport.Same(before.Transport) &&
                    Cycle != 0 && Cycle == before.Cycle && NextResource == before.NextResource && Now >= before.Now;
            }

            internal bool SameCaches(Frame before)
            { return ReferenceEquals(RoleRows, before.RoleRows) && ReferenceEquals(Rewards, before.Rewards) && ReferenceEquals(Templates, before.Templates); }

            internal bool SameUi(Frame before)
            {
                return ReferenceEquals(Sub, before.Sub) && Selection == before.Selection && PanelVisible == before.PanelVisible &&
                    PanelEnabled == before.PanelEnabled && PanelLoading == before.PanelLoading && DialogVisible == before.DialogVisible &&
                    DialogEnabled == before.DialogEnabled && DialogLoading == before.DialogLoading && DialogCached == before.DialogCached &&
                    ReferenceEquals(Bonuses, before.Bonuses) && ReferenceEquals(DialogClose, before.DialogClose) &&
                    Panels == before.Panels && RewardPanels == before.RewardPanels;
            }
        }

        private sealed class Intent
        {
            internal int Sequence, Revision, Category;
            internal string Action, Id;
            internal ulong Role;
            internal Frame Frame;
        }

        private readonly DailyRewardCallbackInstaller install;
        private readonly DailyRewardContextProvider freshContext;
        private readonly Hashtable usedIds = new Hashtable(), attemptedCategories = new Hashtable();
        private Frame frame = new Frame(), opened, claimed;
        private Intent queued, operation;
        private DevelopmentCallbackCapture callback;
        private DailyRewardReceipt receipt;
        private object handler, rewardBonuses;
        private Delegate rewardClose;
        private int revision, lastSequence, claimSequence, since;
        private string phase = "IDLE", reason = "daily_reward_unobserved", nextAction = "", callbackError;
        private bool stopped, attempted, claimInvoked, callbackReturned, settled, confirmationInvoked, confirmed, recoverPanel;

        internal DailyRewardModel(DailyRewardCallbackInstaller installer, DailyRewardContextProvider contextProvider)
        { install = installer; freshContext = contextProvider; }
        internal int Revision { get { return revision; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool OutcomeUnknown { get { return phase == "UNKNOWN"; } }
        internal bool Unresolved { get { return claimInvoked && phase != "COMPLETED" && phase != "REJECTED"; } }
        internal bool Busy { get { return queued != null || opened != null || Unresolved; } }

        private static T Field<T>(object obj, string field)
        {
            object value = D.Read(obj, field);
            if (!(value is T)) throw new ProbeFailure("daily_reward_field:" + field);
            return (T)value;
        }

        private static Type TemplateType(Type row, string name)
        {
            // The firstpass wrappers reference template types from pinned dbdata.
            FieldInfo field = row.GetField("temp", BindingFlags.Instance | BindingFlags.Public);
            if (field == null || field.FieldType.FullName != name || field.FieldType.Assembly.GetName().Name != "dbdata")
                throw new ProbeFailure("daily_reward_template_type:" + name);
            return field.FieldType;
        }

        private static IDictionary Map(object value, int max)
        {
            IDictionary map = value as IDictionary;
            if (map == null || map.Count > max) throw new ProbeFailure("daily_reward_cache_bound");
            return map;
        }

        private static uint Key(DictionaryEntry entry, int bound, Type type)
        {
            if (!(entry.Key is uint) || (uint)entry.Key == 0 || (uint)entry.Key > bound ||
                entry.Value == null || entry.Value.GetType() != type) throw new ProbeFailure("daily_reward_cache_entry");
            return (uint)entry.Key;
        }

        private static Component Dialog(D ctx, out bool cached)
        {
            object value = D.Static(Model.FirstType("__BaseUI"), "battle_result_ui");
            GameObject host = GameObject.Find("Battle/BattleUI");
            Component dialog = host == null ? null : host.GetComponent("BattleResultUI") as Component;
            cached = value != null;
            if (dialog == null || dialog.GetType() != Model.FirstType("BattleResultUI") || cached && !ReferenceEquals(dialog, value)) throw new ProbeFailure("daily_reward_dialog_cache_identity");
            int count = 0;
            foreach (UnityEngine.Object item in ctx.All) if (ReferenceEquals(dialog, item)) count++;
            if (count != 1) throw new ProbeFailure("daily_reward_dialog_unobserved");
            return dialog;
        }

        private Frame Read(D ctx)
        {
            Frame f = new Frame { Context = ctx };
            if (ctx == null || !ctx.SameOwnership(ctx) || ctx.Character.GetType() != Model.FirstType("Database.Character"))
            { f.Error = "daily_reward_role_unavailable"; return f; }
            f.Role = Field<ulong>(ctx.Character, "id");
            if (!Monitor.TryEnter(ctx.Client, 0)) { f.Error = "daily_reward_client_busy"; return f; }
            try
            {
                f.Transport = DailyTransport.Read(ctx.Client);
                if (f.Transport == null)
                { f.Error = "daily_reward_transport_unavailable"; return f; }
                uint day;
                f.Error = DailyRefreshGate.Read(ctx.Character, out f.Now, out day, out f.Cycle);
                f.NextResource = Field<uint>(ctx.Character, "next_resource_time");
                if (f.Error != "") return f;
                if (!f.Transport.Same(DailyTransport.Read(ctx.Client)))
                { f.Error = "daily_reward_transport_changed_during_refresh_check"; return f; }
                IDictionary handlers = Map(D.Read(ctx.Client, "m_handlers"), 512);
                IList messages = D.Read(ctx.Client, "messages") as IList, sends = D.Read(ctx.Client, "m_sendDatas") as IList;
                if (messages == null || messages.Count > 64 || sends == null || sends.Count > 64)
                    throw new ProbeFailure("daily_reward_network_bound");
                f.NetworkObserved = true;
                f.NetworkIdle = messages.Count == 0 && sends.Count == 0 && !handlers.Contains(DailyRewardReceipt.Opcode) &&
                    !DevelopmentCallbackCapture.AnyCallbackExecuting;
                Type handlerType = Model.FirstType("game.net.tcp_base+Handler");
                foreach (DictionaryEntry pair in handlers)
                {
                    if (!(pair.Key is ushort) || pair.Value == null || pair.Value.GetType() != handlerType)
                        throw new ProbeFailure("daily_reward_network_handler_schema");
                    float life = Field<float>(pair.Value, "life");
                    if (Single.IsNaN(life) || Single.IsInfinity(life)) throw new ProbeFailure("daily_reward_network_handler_life");
                    if (life > 0f) f.NetworkIdle = false;
                }
                f.QueuedRewardPackets = CountQueuedRewards(messages);
            }
            finally { Monitor.Exit(ctx.Client); }
            f.Panel = ctx.Find("ActivityUI");
            f.Dialog = Dialog(ctx, out f.DialogCached);
            f.Sub = ctx.SubUi;
            f.Selection = Field<uint>(f.Panel, "reward_select");
            f.PanelVisible = D.Visible(f.Panel); f.PanelEnabled = D.Enabled(f.Panel); f.PanelLoading = D.Loading(f.Panel);
            f.DialogVisible = D.Visible(f.Dialog); f.DialogEnabled = D.Enabled(f.Dialog); f.DialogLoading = D.Loading(f.Dialog);
            f.DialogClose = D.Read(f.Dialog, "OnClose") as Delegate;
            f.Bonuses = D.Read(f.Dialog, "m_vctBonuses");
            f.Panels = f.Sub == null ? ctx.Panels() : ctx.Panels(f.Panel);
            f.RewardPanels = ctx.RewardPanels(f.Panel, f.Dialog);
            if (!(bool)D.Method(ctx.Character.GetType(), "checkUI", new Type[] { typeof(string) }, false, typeof(bool))
                .Invoke(ctx.Character, new object[] { "quest_act" }))
            { f.Error = "daily_reward_activity_locked"; return f; }

            IDictionary latest = Map(D.Read(ctx.Character, "acts"), MaxTasks);
            IDictionary rewards = Map(D.Read(ctx.Character, "act_rewards"), 6);
            IDictionary templates = Map(D.Method(Model.FirstType("Global"), "getTemplates", new Type[] { typeof(string) }, true, typeof(Hashtable))
                .Invoke(null, new object[] { "activity" }), MaxTasks);
            f.RoleRows = latest; f.Rewards = rewards; f.Templates = templates;
            Type actType = Model.FirstType("Database.Activity");
            Type templateType = TemplateType(actType, "Database._activity");
            foreach (DictionaryEntry entry in templates)
                if (Field<byte>(entry.Value, "id") != Key(entry, MaxTasks, templateType))
                    throw new ProbeFailure("daily_reward_task_template_identity");
            foreach (DictionaryEntry entry in latest)
            {
                uint id = Key(entry, MaxTasks, actType);
                if (Field<byte>(entry.Value, "idactivity") != id || !ReferenceEquals(D.Read(entry.Value, "temp"), templates[id]))
                    throw new ProbeFailure("daily_reward_role_task_identity");
            }
            for (int id = 2; id <= 6; id++) f.Categories[id] = ReadCategory(f, id, latest, rewards, templates);
            return f;
        }

        private static int CountQueuedRewards(IList messages)
        {
            if (messages == null || messages.Count > 64) throw new ProbeFailure("daily_reward_network_bound");
            int count = 0;
            for (int i = 0; i < messages.Count; i++)
                if (Field<ushort>(messages[i], "op") == DailyRewardReceipt.Opcode) count++;
            return count;
        }

        private static Category ReadCategory(Frame f, int id, IDictionary latest, IDictionary rewards, IDictionary templates)
        {
            Category category = new Category { Reward = rewards[(uint)id] };
            object reward = category.Reward;
            if (reward == null) return category;
            if (reward.GetType() != Model.FirstType("Database.Activity_Reward") || Field<byte>(reward, "idact_reward") != id)
            { category.Reason = "daily_reward_category_identity"; return category; }
            object template = category.Template = D.Read(reward, "temp");
            if (template == null || template.GetType() != TemplateType(reward.GetType(), "Database._activity_reward") ||
                !ReferenceEquals(template, D.Template("activity_reward", (uint)id)) || Field<byte>(template, "id") != id ||
                !Field<bool>(template, "active"))
            { category.Reason = "daily_reward_category_template"; return category; }
            category.Refresh = Field<uint>(reward, "refresh");
            category.Finished = Field<bool>(reward, "finished_flag");
            IList treasures = D.Read(template, "m_treasures") as IList;
            if (treasures == null || treasures.Count == 0 || treasures.Count > 64)
            { category.Reason = "daily_reward_category_treasures"; return category; }
            IDictionary panel = Map(D.Read(reward, "acts"), MaxTasks);
            category.PanelRows = panel;
            int expected = 0;
            bool incomplete = false;
            for (uint taskId = 1; taskId <= MaxTasks; taskId++)
            {
                object taskTemplate = templates[taskId];
                if (taskTemplate == null || Field<byte>(taskTemplate, "idreward") != id || !Field<bool>(taskTemplate, "active")) continue;
                expected++;
                object roleRow = latest[taskId];
                if (roleRow == null || !ReferenceEquals(roleRow, panel[taskId]))
                { category.Reason = "daily_reward_missing_or_stale_task"; return category; }
                Row row = new Row { Task = roleRow, Template = taskTemplate, Required = Field<int>(taskTemplate, "cond_param"),
                    Current = Field<byte>(roleRow, "condition_param"), Refresh = Field<int>(roleRow, "refresh_time") };
                row.Closed = (bool)D.Method(roleRow.GetType(), "_check_act_closecondition", Type.EmptyTypes, false, typeof(bool)).Invoke(roleRow, null);
                if (row.Required < 1 || row.Required > Byte.MaxValue || row.Current > row.Required)
                { category.Reason = "daily_reward_task_counter_schema"; return category; }
                category.Rows.Add(row);
                if (row.Closed) continue;
                category.Required += row.Required;
                category.Current += row.Current;
                if (row.Current != row.Required) incomplete = true;
            }
            if (expected != panel.Count || category.Required == 0)
            { category.Reason = "daily_reward_task_membership"; return category; }
            if (category.Refresh > f.Now) category.Reason = "daily_reward_already_claimed";
            else if (category.Refresh == f.Now) category.Reason = "daily_reward_refresh_boundary";
            else if (!category.Finished || incomplete) category.Reason = "daily_reward_tasks_incomplete";
            else if (f.PanelEnabled && ReferenceEquals(f.Sub, f.Panel) &&
                (Field<int>(reward, "cur_count") != category.Current || Field<int>(reward, "total_count") != category.Required))
                category.Reason = "daily_reward_panel_totals_stale";
            else category.Reason = "";
            return category;
        }

        internal void Capture(D context)
        {
            if (stopped || phase == "UNKNOWN") return;
            Frame before = frame;
            string stateBefore = StateStamp();
            try { frame = Read(context); Advance(); }
            catch (Exception error)
            {
                frame = new Frame { Error = error is ProbeFailure ? error.Message : "daily_reward_capture_failed" };
                if (claimInvoked && opened != null) Unknown(frame.Error);
                else if (opened != null) End("REJECTED", frame.Error);
                else if (phase != "COMPLETED" && phase != "REJECTED") Set("BLOCKED", frame.Error);
            }
            if (stateBefore != StateStamp() || !SameRevision(before, frame)) Bump();
        }

        private static bool SameRevision(Frame before, Frame after)
        {
            if (before.Error != after.Error || before.NetworkIdle != after.NetworkIdle || before.NetworkObserved != after.NetworkObserved ||
                before.QueuedRewardPackets != after.QueuedRewardPackets || !after.SameUi(before)) return false;
            if (after.Error != "") return before.Role == after.Role && before.Cycle == after.Cycle;
            if (!after.SameOwner(before) || !after.SameCaches(before)) return false;
            for (int id = 2; id <= 6; id++) if (!after.Categories[id].Same(before.Categories[id])) return false;
            return true;
        }

        private void Advance()
        {
            nextAction = "";
            if (opened == null)
            {
                if (OpenReady()) nextAction = "open";
                if (phase != "COMPLETED" && phase != "REJECTED")
                    Set(nextAction == "" ? "BLOCKED" : "READY", nextAction == "" ? OpenReason() : "");
                return;
            }
            if (frame.Error == "daily_reward_client_busy")
            {
                if (Expired) { if (claimInvoked) Unknown("daily_reward_client_timeout"); else End("REJECTED", "daily_reward_client_timeout"); }
                return;
            }
            if (frame.Error != "" || frame.Context.Paused || !frame.SameOwner(opened))
            {
                string why = frame.Error != "" ? frame.Error : "daily_reward_owner_session_or_epoch_changed";
                if (claimInvoked) Unknown(why); else End("REJECTED", why);
                return;
            }
            if (phase == "CLOSING")
            {
                if (frame.Sub == null && !frame.PanelVisible && !frame.PanelLoading &&
                    !frame.DialogVisible && !frame.DialogLoading && frame.Panels == "PASS")
                {
                    if (claimInvoked && !ObserveReceipt()) return;
                    if (confirmed && (!OwnReward() || frame.Selection != operation.Category))
                    { Unknown("daily_reward_close_identity_changed"); return; }
                    End(confirmed ? "COMPLETED" : "REJECTED", confirmed ? "daily_reward_claim_confirmed" : reason);
                }
                else if (Expired)
                {
                    if (claimInvoked) Unknown("daily_reward_panel_close_timeout");
                    else End("REJECTED", "daily_reward_panel_close_timeout");
                }
                return;
            }
            if (!ReferenceEquals(frame.Sub, opened.Panel))
            {
                if (claimInvoked) Unknown("daily_reward_panel_ownership_lost");
                else End("REJECTED", "daily_reward_panel_ownership_lost");
                return;
            }
            if (claimInvoked) { AdvanceClaim(); return; }
            if (recoverPanel)
            {
                if (PanelReady()) { nextAction = "close"; Set("READY", "daily_reward_recover_existing_panel"); }
                else if (Expired) End("REJECTED", "daily_reward_panel_close_timeout");
                return;
            }
            if (PanelReady())
            {
                Category category = frame.Categories[operation.Category];
                bool sameRows = frame.SameCaches(opened) && category.SameRows(opened.Categories[operation.Category]);
                bool ready = sameRows && ClaimReady(operation.Category);
                nextAction = ready ? "claim" : "close";
                Set("READY", ready ? "" : sameRows ? category.Reason : "daily_reward_open_plan_changed");
            }
            else if (Expired) End("REJECTED", "daily_reward_open_timeout");
        }

        private string OpenReason()
        {
            if (frame.Error != "") return frame.Error;
            if (frame.Sub != null || frame.PanelVisible || frame.PanelLoading) return "daily_reward_foreign_panel";
            if (frame.DialogVisible || frame.DialogLoading || frame.Panels != "PASS") return "daily_reward_ui_busy";
            if (!frame.NetworkIdle) return "daily_reward_network_pending";
            if (freshContext == null) return "daily_reward_fresh_context_unavailable";
            if (install == null) return "daily_reward_callback_unavailable";
            if (usedIds.Count >= MaxOperations || attemptedCategories.Count >= MaxOperations) return "daily_reward_history_bound";
            return "daily_reward_no_completed_category";
        }

        private bool OpenReady()
        {
            bool clean = frame.Sub == null && !frame.PanelVisible && !frame.PanelLoading;
            if (frame.Error != "" || frame.Context == null || frame.Context.Paused || !clean && !RecoverablePanel() ||
                frame.DialogVisible || frame.DialogLoading || frame.Panels != "PASS" || !frame.NetworkIdle ||
                install == null || freshContext == null || usedIds.Count >= MaxOperations || attemptedCategories.Count >= MaxOperations) return false;
            for (int id = 2; id <= 6; id++) if (ClaimReady(id)) return true;
            return false;
        }

        private bool RecoverablePanel()
        {
            // A residual stock page is acquired only for a normal close. Its
            // visibility never proves that an earlier reward request was absent.
            return phase != "UNKNOWN" && !Unresolved && !claimInvoked && callback == null &&
                frame.Error == "" && frame.Context != null && !frame.Context.Paused && frame.Context.SameIdentity(frame.Context) &&
                ReferenceEquals(frame.Sub, frame.Panel) && frame.PanelVisible && frame.PanelEnabled && !frame.PanelLoading &&
                !frame.DialogVisible && !frame.DialogLoading && frame.Panels == "PASS" && frame.NetworkIdle && frame.QueuedRewardPackets == 0;
        }

        private string AttemptKey(int id)
        { return frame.Role.ToString(CultureInfo.InvariantCulture) + ":" + frame.Cycle + ":" + id; }

        private bool ClaimReady(int id)
        { return frame.Categories[id] != null && frame.Categories[id].Reason == "" && !attemptedCategories.Contains(AttemptKey(id)); }

        private bool PanelReady()
        {
            return frame.Context.SameIdentity(opened.Context) && frame.PanelEnabled && !frame.PanelLoading &&
                !frame.DialogVisible && !frame.DialogLoading && frame.Panels == "PASS" && frame.NetworkIdle;
        }

        private bool Expired { get { return unchecked((uint)(Environment.TickCount - since)) >= WaitMs; } }

        private bool ObserveReceipt()
        {
            if (!frame.SameOwner(claimed) || !frame.SameCaches(claimed))
            { Unknown("daily_reward_claim_identity_changed"); return false; }
            DevelopmentCallbackWitness witness = callback == null ? null : callback.Read();
            if (callbackError != null) { Unknown(callbackError); return false; }
            if (witness == null || callback.OperationId != operation.Id || callback.Sequence != claimSequence ||
                callback.Opcode != DailyRewardReceipt.Opcode || witness.Error != null || witness.OriginalThrew || witness.NullResponse ||
                !witness.Armed || witness.Calls > 1 || frame.QueuedRewardPackets > (witness.Calls == 0 ? 1 : 0))
            { Unknown(witness == null ? "daily_reward_callback_unobserved" : witness.Error ?? "daily_reward_callback_invalid"); return false; }
            if (!witness.Returned)
            {
                if (!CallbackPending()) Unknown("daily_reward_callback_replaced");
                else if (Expired) Unknown("daily_reward_callback_timeout");
                return false;
            }
            if (witness.Calls != 1 || receipt == null)
            { Unknown("daily_reward_receipt_missing"); return false; }
            if (!callbackReturned) since = Environment.TickCount;
            callbackReturned = true;
            // A state=false receipt carries neither category nor nonce. An
            // older request's late response can consume this handler.
            if (!receipt.Accepted) { Unknown("daily_reward_negative_receipt_unattributed"); return false; }
            Category category = frame.Categories[operation.Category], baseline = claimed.Categories[operation.Category];
            if (!category.SameRows(baseline) || receipt.Category != operation.Category || receipt.Refresh <= frame.Now ||
                category.Refresh != receipt.Refresh || category.Finished != receipt.Finished || category.Reason != "daily_reward_already_claimed")
            { Unknown("daily_reward_receipt_cache_mismatch"); return false; }
            settled = true;
            return true;
        }

        private bool CallbackPending()
        {
            object client = frame.Context.Client;
            if (!Monitor.TryEnter(client, 0)) return true;
            try
            {
                IDictionary handlers = Map(D.Read(client, "m_handlers"), 512);
                return ReferenceEquals(handlers[DailyRewardReceipt.Opcode], handler) && callback.StillInstalled();
            }
            finally { Monitor.Exit(client); }
        }

        private void AdvanceClaim()
        {
            if (frame.Selection != operation.Category) { Unknown("daily_reward_selection_changed"); return; }
            if (!ObserveReceipt()) return;
            if (!OwnReward()) { Unknown("daily_reward_dialog_ownership_lost"); return; }
            if (confirmationInvoked)
            {
                if (PanelReady()) { confirmed = true; Set("READY", ""); nextAction = "close"; }
            }
            else if (frame.DialogEnabled && !frame.DialogLoading && frame.RewardPanels == "PASS" &&
                frame.PanelVisible && !frame.PanelEnabled && !frame.PanelLoading && frame.NetworkIdle)
            { Set("READY", ""); nextAction = "reward_confirm"; }
            if (nextAction == "")
            {
                if (Expired) Unknown("daily_reward_dialog_or_close_timeout");
                else Set("WAITING", "daily_reward_waiting_for_owned_ui");
            }
        }

        private bool OwnReward()
        {
            if (!frame.DialogCached || frame.DialogClose == null || ReferenceEquals(frame.DialogClose, claimed.DialogClose) ||
                frame.DialogClose.GetInvocationList().Length != 1 || !ReferenceEquals(frame.DialogClose.Target, frame.Panel) ||
                frame.DialogClose.Method != ActivityMethod(frame.Panel, "OnRecieveReward", Type.EmptyTypes) ||
                !ReferenceEquals(D.Read(frame.Dialog, "m_prevUI"), frame.Context.UiRoot) ||
                Field<bool>(frame.Dialog, "autoClose") || Field<string>(frame.Dialog, "mine_ui") != "" ||
                ReferenceEquals(frame.Bonuses, claimed.Bonuses)) return false;
            IList list = frame.Bonuses as IList;
            if (list == null || list.Count != receipt.Treasures) return false;
            if (rewardClose == null)
            {
                if (!frame.DialogVisible && !frame.DialogLoading) return false;
                rewardClose = frame.DialogClose; rewardBonuses = frame.Bonuses;
            }
            return ReferenceEquals(frame.DialogClose, rewardClose) && ReferenceEquals(frame.Bonuses, rewardBonuses);
        }

        internal bool Queue(int sequence, int expectedRevision, string action, string operationId, ulong characterId, int categoryId, out string why)
        {
            why = "daily_reward_command_identity_or_revision";
            if (stopped || phase == "UNKNOWN" || queued != null || sequence <= lastSequence ||
                expectedRevision < 1 || categoryId < 2 || categoryId > 6 || action == null || action == "" ||
                !new DevelopmentCommand { OperationId = operationId }.ValidId() || characterId == 0 || characterId != frame.Role ||
                (action == "open" ? opened != null || usedIds.Contains(operationId) || !ClaimReady(categoryId) :
                    opened == null || operation.Id != operationId || operation.Category != categoryId || operation.Role != characterId))
                return false;
            if (expectedRevision != revision) { why = "daily_reward_revision_changed"; return false; }
            if (action != nextAction) { why = "daily_reward_action_changed"; return false; }
            queued = new Intent { Sequence = sequence, Revision = expectedRevision, Action = action, Id = operationId,
                Role = characterId, Category = categoryId, Frame = frame };
            lastSequence = sequence;
            why = "";
            return true;
        }

        internal bool ExecuteGui(int sequence, int expectedRevision, string action, string operationId, ulong characterId, int categoryId,
            DevelopmentPublication publish, DailyRewardInvocationGate begin, out string why)
        {
            Intent intent = queued;
            queued = null;
            why = "daily_reward_gui_command_not_owned";
            if (intent == null || stopped || publish == null || begin == null || !D.Gui || intent.Sequence != sequence ||
                intent.Revision != expectedRevision || intent.Action != action || intent.Id != operationId ||
                intent.Role != characterId || intent.Category != categoryId || revision != expectedRevision) return false;
            object client = intent.Frame.Context.Client;
            if (!Monitor.TryEnter(client, 0)) { why = "daily_reward_client_busy"; return false; }
            bool publishing = false;
            try
            {
                if (!Recheck(intent)) { why = "daily_reward_preinvoke_changed"; return false; }
                publishing = true;
                publish();
                publishing = false;
                if (!Recheck(intent)) { why = "daily_reward_publication_state_changed"; return false; }
                if (!Invoke(intent, begin)) { why = "daily_reward_gui_final_admission_changed"; return false; }
                why = "";
                Bump();
                return true;
            }
            catch (Exception error)
            {
                if (publishing) throw; // Publication owns cancellation and guarantees that no stock action has run.
                why = error is ProbeFailure ? error.Message : "daily_reward_normal_action_threw";
                Failed(why);
                return false;
            }
            finally { Monitor.Exit(client); }
        }

        private bool Recheck(Intent intent)
        {
            if (stopped || !D.Gui || freshContext == null) return false;
            Capture(freshContext());
            return !stopped && revision == intent.Revision && nextAction == intent.Action && frame.SameOwner(intent.Frame) &&
                frame.SameUi(intent.Frame) && frame.NetworkIdle && (intent.Action != "open" && intent.Action != "claim" || ClaimReady(intent.Category));
        }

        private bool BeginStockAction(DailyRewardInvocationGate begin)
        {
            if (!begin()) return false;
            since = Environment.TickCount;
            return true;
        }

        private bool Invoke(Intent intent, DailyRewardInvocationGate begin)
        {
            if (intent.Action == "open")
            {
                MethodInfo open = D.Method(Model.GameType("MainUI"), "_quest_act", Type.EmptyTypes, true, typeof(void));
                bool existing = RecoverablePanel();
                if (!BeginStockAction(begin)) return false;
                operation = intent; opened = frame;
                usedIds.Add(intent.Id, true);
                claimed = null; receipt = null; callbackError = null; rewardClose = null; rewardBonuses = null; claimSequence = 0;
                claimInvoked = callbackReturned = settled = confirmationInvoked = confirmed = false;
                recoverPanel = existing;
                Set("WAITING", existing ? "daily_reward_recover_existing_panel" : "daily_reward_opening");
                // Calling the stock open on an already selected page toggles it
                // closed. Bind this page for the separately checked close instead.
                if (!existing) open.Invoke(null, null);
            }
            else if (intent.Action == "claim")
            { if (!SendClaim(intent, begin)) return false; }
            else if (intent.Action == "reward_confirm")
            {
                MethodInfo hide = D.Method(frame.Dialog.GetType(), "Hide", Type.EmptyTypes, false, typeof(void));
                DevelopmentPublication close = (DevelopmentPublication)Delegate.CreateDelegate(typeof(DevelopmentPublication), rewardClose.Target, rewardClose.Method);
                if (!BeginStockAction(begin)) return false;
                confirmationInvoked = true;
                Set("WAITING", "daily_reward_confirming");
                hide.Invoke(frame.Dialog, null);
                close();
            }
            else
            {
                MethodInfo close = D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void));
                if (!BeginStockAction(begin)) return false;
                Set("CLOSING", reason == "" && !confirmed ? "daily_reward_closed_without_claim" : reason);
                close.Invoke(null, new object[] { null });
            }
            nextAction = "";
            return true;
        }

        private static MethodInfo ActivityMethod(Component panel, string name, Type[] args)
        {
            MethodInfo method = D.Method(panel.GetType(), name, args, false, typeof(void));
            if (method.DeclaringType != panel.GetType() || !method.IsPrivate) throw new ProbeFailure("daily_reward_activity_method");
            return method;
        }

        private bool SendClaim(Intent intent, DailyRewardInvocationGate begin)
        {
            MethodInfo send = ActivityMethod(frame.Panel, "cs_get_reward", Type.EmptyTypes);
            MethodInfo receive = ActivityMethod(frame.Panel, "sc_get_reward", new Type[] { typeof(MemoryStream), typeof(object) });
            MethodInfo disable = D.Method(frame.Panel.GetType(), "Disable", Type.EmptyTypes, false, typeof(void));
            object client = frame.Context.Client;
            if (!frame.Context.SameIdentity(intent.Frame.Context) || !frame.Transport.Same(DailyTransport.Read(client)))
                throw new ProbeFailure("daily_reward_transport_changed_before_send");
            if (!BeginStockAction(begin)) return false;
            claimed = frame;
            D.Selection(frame.Panel, "reward_select", (uint)intent.Category);
            disable.Invoke(frame.Panel, null);
            attemptedCategories.Add(AttemptKey(intent.Category), true);
            attempted = claimInvoked = true;
            claimSequence = intent.Sequence;
            Set("WAITING", "daily_reward_waiting_for_callback");
            send.Invoke(frame.Panel, null);
            handler = Map(D.Read(client, "m_handlers"), 512)[DailyRewardReceipt.Opcode];
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") || D.Read(handler, "arg") != null)
                throw new ProbeFailure("daily_reward_handler_unowned");
            Delegate original = D.Read(handler, "handler") as Delegate;
            if (original == null || !ReferenceEquals(original.Target, frame.Panel) || original.Method != receive)
                throw new ProbeFailure("daily_reward_callback_unowned");
            string why;
            callback = install(client, handler, intent.Id, intent.Sequence, original,
                ReadClaimReceipt, out why);
            if (callback == null) throw new ProbeFailure(why ?? "daily_reward_callback_install_failed");
            return true;
        }

        private void ReadClaimReceipt(byte[] bytes)
        {
            Frame baseline = claimed;
            object client = baseline.Context.Client;
            if (!Monitor.TryEnter(client, 0)) { callbackError = "daily_reward_callback_client_busy"; return; }
            try
            {
                if (!baseline.Context.SameOwnership(baseline.Context) || !baseline.Transport.Same(DailyTransport.Read(client)))
                { callbackError = "daily_reward_callback_transport_or_role_changed"; return; }
                // Run removed the current message before calling us. Another
                // queued 0x164 could disappear in the next Run before Capture.
                if (CountQueuedRewards(D.Read(client, "messages") as IList) != 0)
                { callbackError = "daily_reward_duplicate_reply_queued"; return; }
                receipt = DailyRewardReceipt.Decode(bytes);
            }
            finally { Monitor.Exit(client); }
        }

        internal void NotInvoked() { queued = null; }
        internal void Failed(string why)
        {
            if (phase == "UNKNOWN") return;
            if (claimInvoked && opened != null) Unknown(why); else if (opened != null) End("REJECTED", why);
            Bump();
        }
        internal void Stop(string why)
        {
            stopped = true; queued = null; nextAction = "";
            if (phase == "UNKNOWN") return;
            if (Unresolved) Unknown(why);
            else if (phase != "COMPLETED" && phase != "REJECTED") End("STOPPED", why);
            Bump();
        }

        private void Set(string status, string why) { phase = status; reason = why; }
        private void End(string status, string why)
        { opened = null; queued = null; nextAction = ""; Set(status, why); Detach(); }
        private void Unknown(string why)
        { queued = null; nextAction = ""; Set("UNKNOWN", why); Detach(); }
        private void Detach()
        { if (callback != null) callback.DisarmAndRestore(); callback = null; }
        private void Bump()
        { if (revision < 999999999) revision++; else StopAtLimit(); }
        private void StopAtLimit()
        {
            stopped = true;
            if (Unresolved) Unknown("daily_reward_revision_exhausted");
            else End("STOPPED", "daily_reward_revision_exhausted");
        }
        private string StateStamp()
        { return phase + ":" + reason + ":" + nextAction + ":" + callbackReturned + ":" + settled + ":" + confirmed; }

        internal string Json()
        {
            StringBuilder result = new StringBuilder("{\"schema\":1,\"revision\":" + revision +
                ",\"status\":" + D.Q(phase) + ",\"reason\":" + D.Q(reason) + ",\"next_action\":" + D.Q(nextAction) +
                ",\"character_id\":" + D.Q(frame.Role == 0 ? "" : frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"operation_id\":" + D.Q(operation == null ? "" : operation.Id) + ",\"sequence\":" + lastSequence +
                ",\"category_id\":" + (operation == null ? 0 : operation.Category) + ",\"claim_sequence\":" + claimSequence +
                ",\"refresh_epoch\":" + frame.Cycle +
                ",\"invoked\":" + D.B(claimInvoked) + ",\"unresolved\":" + D.B(Unresolved) +
                ",\"callback_returned\":" + D.B(callbackReturned) + ",\"settled\":" + D.B(settled) +
                ",\"reward_closed\":" + D.B(confirmed) + ",\"receipt_category\":" + (receipt == null ? 0 : receipt.Category) +
                ",\"receipt_refresh\":" + (receipt == null ? 0 : receipt.Refresh) + ",\"categories\":[");
            for (int id = 2; id <= 6; id++)
            {
                if (id != 2) result.Append(',');
                Category category = frame.Categories[id];
                string categoryReason = category == null ? frame.Error : category.Reason;
                if (category != null && categoryReason == "" && !ClaimReady(id)) categoryReason = "daily_reward_already_attempted";
                result.Append("{\"id\":").Append(id).Append(",\"ready\":").Append(D.B(ClaimReady(id)))
                    .Append(",\"claimed\":").Append(category == null ? "null" : D.B(category.Refresh > frame.Now))
                    .Append(",\"reason\":").Append(D.Q(categoryReason)).Append('}');
            }
            return result.Append("]}").ToString();
        }
    }
}
