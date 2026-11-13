using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // Fixed read of the pinned ActivityUI model. The daily command supplies
    // no target, reflection names, UI selections or network parameters.
    internal static class DailyActivityModel
    {
        private const int CategoryCount = 6, TaskLimit = 255, StoneTask = 16;
        private const string Header = "{\"schema\":1,\"source\":\"client_projection\"";
        private static readonly int[] KnownTasks = new int[]
            { 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 21, 22, 23, 24 };

        private sealed class Task
        {
            internal int Id, Required, ButtonId, CategoryId, PanelRefresh;
            internal string Name, Reason, Availability;
            internal bool Active, Visible, ProgressInconsistent;
            internal int? Current, LatestCurrent;

            internal void MarkProgressInconsistent()
            {
                ProgressInconsistent = true;
                Availability = "unknown";
                Reason = "daily_progress_inconsistent";
            }

            internal string Json()
            {
                return "{\"task_id\":" + Id + ",\"name\":" + D.Q(Name) +
                    ",\"current\":" + Number(Current) + ",\"latest_current\":" + Number(LatestCurrent) +
                    ",\"required\":" + Required + ",\"complete\":" + (Current.HasValue && !ProgressInconsistent ? D.B(Current.Value >= Required) : "null") +
                    ",\"visible\":" + D.B(Visible) + ",\"reward_ready\":null,\"availability\":" + D.Q(Availability) +
                    ",\"reason\":" + D.Q(Reason) + ",\"ui_button_id\":" + ButtonId + "}";
            }
        }

        private sealed class Category
        {
            internal int Id, DisplayOrder;
            internal int Current, Required;
            internal string Name;
            internal uint? RewardRefresh;
            internal bool ServerFinished;
            internal readonly List<Task> Tasks = new List<Task>();

            internal string Json(uint now)
            {
                bool visible = DisplayOrder > 0, hasReward = RewardRefresh.HasValue;
                bool claimed = hasReward && RewardRefresh.Value > now;
                StringBuilder b = new StringBuilder("{\"category_id\":" + Id + ",\"name\":" + D.Q(Name) +
                    ",\"display_order\":" + Number(visible ? (int?)DisplayOrder : null) + ",\"visible\":" + D.B(visible) +
                    ",\"current\":" + Number(hasReward ? (int?)Current : null) + ",\"required\":" + Number(hasReward ? (int?)Required : null) +
                    ",\"reward_ready\":" + (hasReward ? D.B(visible && !claimed && Current >= Required) : "null") +
                    ",\"reward_claimed\":" + (hasReward ? D.B(claimed) : "null") +
                    ",\"server_finished\":" + (hasReward ? D.B(ServerFinished) : "null") +
                    ",\"reward_refresh\":" + Number(RewardRefresh) + ",\"tasks\":[");
                for (int i = 0; i < Tasks.Count; i++)
                {
                    if (i != 0) b.Append(',');
                    b.Append(Tasks[i].Json());
                }
                return b.Append("]}").ToString();
            }
        }

        internal static string Capture(Model model) { return Capture(model, DailyRefreshGate.Read); }

        internal static string Capture(Model model, DailyRefreshAdmission refresh)
        {
            try
            {
                Component root = model.CurrentRoot();
                if (root == null || !root.gameObject.activeInHierarchy) return Unavailable("daily_root_unavailable");
                Type global = Model.FirstType("Global");
                object character = D.Static(global, "character_");
                if (character == null || character.GetType() != Model.FirstType("Database.Character")) return Unavailable("daily_character_unavailable");
                ulong id = Value<ulong>(character, "id");
                object client = D.Static(Model.GameType("RootUI"), "m_client");
                if (id == 0 || client == null || !object.ReferenceEquals(client, D.Static(global, "m_client")))
                    return Unavailable("daily_character_unavailable");
                uint now, day, cycle;
                string clockGate = refresh(character, out now, out day, out cycle);
                if (clockGate != "") return Unavailable(clockGate);
                return ReadActivities(character, id, now, day);
            }
            catch (ProbeFailure error) { return Unavailable(error.Message); }
            catch (Exception) { return Unavailable("daily_capture_failed"); }
        }

        private static string ReadActivities(object character, ulong characterId, uint now, uint day)
        {
            IDictionary acts = Dictionary(character, "acts", TaskLimit), rewards = Dictionary(character, "act_rewards", CategoryCount);
            if (acts.Count == 0 && rewards.Count == 0) return Unavailable("daily_data_unavailable");
            Type actType = Model.FirstType("Database.Activity"), rewardType = Model.FirstType("Database.Activity_Reward");
            Category[] categories = new Category[CategoryCount + 1];
            Task[] panelTasks = new Task[TaskLimit + 1];
            for (int id = 1; id <= CategoryCount; id++)
            {
                object template = D.Template("activity_reward", (uint)id);
                categories[id] = new Category { Id = id, Name = template == null ? "" : Label(template) };
            }
            int displayOrder = 0;
            foreach (DictionaryEntry entry in rewards)
            {
                int id = Key(entry, CategoryCount, rewardType);
                object reward = entry.Value;
                if (Value<byte>(reward, "idact_reward") != id) throw new ProbeFailure("daily_category_identity");
                Category category = categories[id];
                object template = D.Read(reward, "temp");
                if (template != null) category.Name = Label(template);
                category.RewardRefresh = Value<uint>(reward, "refresh");
                category.ServerFinished = Value<bool>(reward, "finished_flag");
                IDictionary rows = Dictionary(reward, "acts", TaskLimit);
                if (acts.Count != 0 && rows.Count != 0) category.DisplayOrder = ++displayOrder;
                foreach (DictionaryEntry row in rows)
                {
                    Task task = ReadTask(row, actType);
                    if (task.CategoryId != id || panelTasks[task.Id] != null) throw new ProbeFailure("daily_task_category_identity");
                    panelTasks[task.Id] = task;
                    category.Tasks.Add(task);
                    task.Current = Value<byte>(row.Value, "condition_param");
                    bool requireCoherence = RequiresCoherentProgress(task);
                    if (requireCoherence) task.PanelRefresh = Value<int>(row.Value, "refresh_time");
                    string closed = ClosedReason(character, task.Id);
                    task.Visible = category.DisplayOrder != 0 && closed == "";
                    // SubLoading() recalculates these sums before showing ActivityUI.
                    // Mirror recalc_count() without calling it or editing the cache.
                    if (closed == "")
                    {
                        category.Current = checked(category.Current + task.Current.Value);
                        category.Required = checked(category.Required + task.Required);
                    }
                    AssessAvailability(character, task, closed, now, day);
                    if (requireCoherence && !acts.Contains((uint)task.Id)) task.MarkProgressInconsistent();
                }
            }
            // Character.updateActivity can replace the newest entry without updating
            // Activity_Reward.acts. Keep the two readbacks distinct.
            foreach (DictionaryEntry row in acts)
            {
                Task latest = ReadTask(row, actType);
                Task panel = panelTasks[latest.Id];
                int value = Value<byte>(row.Value, "condition_param");
                if (panel != null && panel.CategoryId == latest.CategoryId)
                {
                    panel.LatestCurrent = value;
                    // The latest cache can advance alone. Require the listed
                    // non-wealth tasks to agree on progress and refresh.
                    if (RequiresCoherentProgress(panel) && (panel.Current != value || panel.PanelRefresh != Value<int>(row.Value, "refresh_time")))
                        panel.MarkProgressInconsistent();
                }
                else if (panel == null)
                {
                    latest.LatestCurrent = value;
                    latest.Availability = "unavailable";
                    latest.Reason = latest.Active ? "not_listed_in_category" : "activity_disabled";
                    if (RequiresCoherentProgress(latest)) latest.MarkProgressInconsistent();
                    categories[latest.CategoryId].Tasks.Add(latest);
                }
                else throw new ProbeFailure("daily_task_category_identity");
            }
            if (panelTasks[StoneTask] == null && !acts.Contains((uint)StoneTask))
            {
                object template = D.Template("activity", StoneTask);
                if (template != null)
                {
                    Task stone = ReadTemplate(template, StoneTask);
                    stone.MarkProgressInconsistent();
                    categories[stone.CategoryId].Tasks.Add(stone);
                }
            }
            StringBuilder json = new StringBuilder(Header + ",\"available\":true,\"reason\":\"\",\"character_id\":" +
                D.Q(characterId.ToString(CultureInfo.InvariantCulture)) + ",\"server_now\":" + Number(now) + ",\"server_day\":" + Number(day) + ",\"refresh_clear\":true,\"categories\":[");
            for (int id = 1; id <= CategoryCount; id++)
            {
                if (id != 1) json.Append(',');
                json.Append(categories[id].Json(now));
            }
            json.Append("],\"missing_tasks\":[");
            bool firstMissing = true;
            foreach (int missingId in KnownTasks)
            {
                if (panelTasks[missingId] != null || acts.Contains((uint)missingId)) continue;
                object template = D.Template("activity", (uint)missingId);
                if (template == null) continue;
                Task missing = ReadTemplate(template, missingId);
                bool unlocked = (bool)D.Method(character.GetType(), "checkUI", new Type[] { typeof(uint) }, false, typeof(bool))
                    .Invoke(character, new object[] { (uint)missing.ButtonId });
                string entryName = missingId == 5 ? "resource_soul" : missingId == 21 ? "star_refine" : null;
                bool? entryUnlocked = entryName == null ? (bool?)null :
                    (bool)D.Method(character.GetType(), "checkUI", new Type[] { typeof(string) }, false, typeof(bool))
                        .Invoke(character, new object[] { entryName });
                if (!firstMissing) json.Append(',');
                firstMissing = false;
                json.Append("{\"task_id\":").Append(missingId).Append(",\"category_id\":").Append(missing.CategoryId)
                    .Append(",\"template_active\":").Append(D.B(missing.Active))
                    .Append(",\"ui_button_id\":").Append(missing.ButtonId)
                    .Append(",\"ui_unlocked\":").Append(D.B(unlocked))
                    .Append(",\"entry_unlocked\":").Append(entryUnlocked.HasValue ? D.B(entryUnlocked.Value) : "null").Append('}');
            }
            return json.Append("]}").ToString();
        }

        private static bool RequiresCoherentProgress(Task task)
        {
            int id = task.Id;
            return task.CategoryId != 1 && (id == 3 || id >= 5 && id <= 18 || id >= 20 && id <= 24);
        }

        private static Task ReadTask(DictionaryEntry row, Type activityType)
        {
            int id = Key(row, TaskLimit, activityType);
            object activity = row.Value, template = D.Read(activity, "temp");
            if (Value<byte>(activity, "idactivity") != id)
                throw new ProbeFailure("daily_task_identity");
            return ReadTemplate(template, id);
        }

        private static Task ReadTemplate(object template, int id)
        {
            if (Value<byte>(template, "id") != id) throw new ProbeFailure("daily_task_identity");
            int category = Value<byte>(template, "idreward"), required = Value<int>(template, "cond_param");
            if (category < 1 || category > CategoryCount || required < 0)
                throw new ProbeFailure("daily_task_template");
            return new Task { Id = id, CategoryId = category, Required = required, Name = Label(template),
                ButtonId = Value<byte>(template, "id_uibutton"), Active = Value<bool>(template, "active") };
        }

        private static string ClosedReason(object character, int id)
        {
            if (id == 6)
            {
                object spirit = D.Read(character, "spirit");
                if (spirit != null && D.Int(D.Read(spirit, "level")) == 100) return "spirit_max_level";
            }
            // Character.isGraveEnd is a pure check over graves and the pinned
            // grave template cache. This is Activity._check_act_closecondition's
            // exact branch for activity 17.
            if (id == 17 && (bool)D.Method(character.GetType(), "isGraveEnd", Type.EmptyTypes, false, typeof(bool)).Invoke(character, null))
                return "grave_progression_limit";
            return "";
        }

        private static void AssessAvailability(object character, Task task, string closed, uint now, uint day)
        {
            task.Availability = "unavailable";
            if (closed != "") { task.Reason = closed; return; }
            if (!task.Visible) { task.Reason = "category_hidden"; return; }
            if (!task.Active) { task.Reason = "activity_disabled"; return; }
            if (task.Current.Value >= task.Required) { task.Reason = "complete"; return; }
            if (task.Id == 13)
            {
                object brawl = D.Read(character, "brawlinfo");
                if (brawl == null) { task.Availability = "unknown"; task.Reason = "brawl_phase_unknown"; return; }
                uint phase = Value<uint>(brawl, "phase");
                if (phase != 2 && phase != 3) { task.Reason = "brawl_closed"; return; }
            }
            if (task.Id == 12)
            {
                object quiz = D.Read(character, "quiz");
                if (quiz != null && (Value<bool>(quiz, "bonus") || Value<uint>(quiz, "quiz_refresh") > now))
                { task.Reason = "quiz_finished"; return; }
                if (quiz == null || D.Read(quiz, "info") == null)
                {
                    object start = D.Template("event_timer", 1002u), end = D.Template("event_timer", 1003u);
                    if (day == 0 || start == null || end == null)
                    { task.Availability = "unknown"; task.Reason = "quiz_window_unknown"; return; }
                    int startHour = Value<byte>(start, "hour"), endHour = Value<byte>(end, "hour");
                    if (startHour >= endHour || endHour > 24)
                    { task.Availability = "unknown"; task.Reason = "quiz_window_unknown"; return; }
                    if (now <= day + startHour * 3600L || now >= day + endHour * 3600L)
                    { task.Reason = "quiz_closed"; return; }
                }
            }
            task.Availability = task.Id <= 24 ? "available" : "unknown";
            task.Reason = task.Id <= 24 ? "" : "activity_route_unknown";
        }

        private static int Key(DictionaryEntry entry, int maximum, Type expected)
        {
            if (!(entry.Key is uint) || (uint)entry.Key == 0 || (uint)entry.Key > maximum ||
                entry.Value == null || entry.Value.GetType() != expected) throw new ProbeFailure("daily_dictionary_entry");
            return (int)(uint)entry.Key;
        }

        private static IDictionary Dictionary(object value, string field, int maximum)
        {
            IDictionary result = D.Read(value, field) as IDictionary;
            if (result == null || result.Count > maximum) throw new ProbeFailure("daily_dictionary_bound");
            return result;
        }

        private static T Value<T>(object target, string field)
        {
            object value = D.Read(target, field);
            if (!(value is T)) throw new ProbeFailure("daily_field_schema:" + field);
            return (T)value;
        }

        private static string Label(object template)
        {
            string label = D.Read(template, "name") as string;
            if (label != null && label.Length > 256) throw new ProbeFailure("daily_label_bound");
            return label ?? "";
        }

        private static string Number(long? value)
        { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null"; }

        private static string Unavailable(string reason)
        {
            return Header + ",\"available\":false,\"reason\":" + D.Q(reason) +
                ",\"character_id\":\"\",\"server_now\":null,\"server_day\":null,\"refresh_clear\":null,\"categories\":[]}";
        }
    }
}
