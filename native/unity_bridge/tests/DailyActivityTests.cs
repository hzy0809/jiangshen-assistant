using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using JiangShen.AssistantRuntime;

namespace UnityEngine
{
    internal sealed class Component { internal readonly GameObject gameObject = new GameObject(); }
    internal sealed class GameObject { internal bool activeInHierarchy = true; }
}

namespace JiangShen.AssistantRuntime
{
    internal static class DailyRefreshProof
    { internal static bool Verified(object character, uint cycle) { return false; } }
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string reason) : base(reason) { } }
    internal sealed class Model
    {
        internal UnityEngine.Component CurrentRoot() { return new UnityEngine.Component(); }
        internal static Type FirstType(string name)
        {
            switch (name)
            {
                case "Global": return typeof(Globals);
                case "TimeUtil": return typeof(Clock);
                case "Database.Character": return typeof(Character);
                case "Database.Activity": return typeof(Activity);
                case "Database.Activity_Reward": return typeof(Reward);
                default: throw new Exception(name);
            }
        }
        internal static Type GameType(string name) { return typeof(RootUi); }
    }
    internal static class SharedDevelopmentContext
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static object Read(object target, string name) { return target.GetType().GetField(name, Instance).GetValue(target); }
        internal static object Static(Type type, string name) { return type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null); }
        internal static MethodInfo Method(Type type, string name, Type[] args, bool isStatic, Type result) { return type.GetMethod(name, Instance); }
        internal static object Template(string name, uint id)
        {
            if (name == "activity_reward") return new Template { name = "tab_" + id };
            if (name == "activity")
            {
                if (id == 16) return new Template { id = 16, idreward = 2, id_uibutton = 48, name = "采石" };
                foreach (byte[] row in DailyActivityTests.RequestedTasks)
                    if (row[0] == id) return new Template { id = row[0], idreward = row[1], cond_param = row[2] };
                return null;
            }
            return new Timer { hour = id == 1002 ? (byte)12 : (byte)13 };
        }
        internal static int Int(object value) { return Convert.ToInt32(value); }
        internal static string Q(string value) { return Command.Quote(value); }
        internal static string B(bool value) { return value ? "true" : "false"; }
    }
    internal static class Globals { internal static Character character_ = new Character(); internal static object m_client = new object(); }
    internal static class RootUi { internal static object m_client = Globals.m_client; internal static bool m_bRefreshFive = false; }
    internal static class Clock { internal static uint _now = 143200, _thisDay = 100000; }
    internal sealed class Timer { internal byte hour; }
    internal sealed class Character
    {
        internal ulong id = 123;
        internal uint next_resource_time = 300000;
        internal readonly Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
        internal readonly Spirit spirit = new Spirit();
        internal readonly Brawl brawlinfo = new Brawl();
        internal object quiz = null;
        internal bool GraveEnd = true;
        internal bool isGraveEnd() { return GraveEnd; }
    }
    internal sealed class Spirit { internal int level = 100; }
    internal sealed class Brawl { internal uint phase = 1; }
    internal sealed class Template
    {
        internal string name;
        internal byte id, idreward, id_uibutton = 47;
        internal int cond_param = 1;
        internal bool active = true;
    }
    internal sealed class Activity { internal byte idactivity, condition_param; internal int refresh_time; internal Template temp; }
    internal sealed class Reward
    {
        internal byte idact_reward;
        internal uint refresh;
        internal bool finished_flag;
        internal Template temp;
        internal readonly Hashtable acts = new Hashtable();
    }
}

internal static class DailyActivityTests
{
    // Activity ID, category, and threshold from the pinned db_template1.
    internal static readonly byte[][] RequestedTasks = {
        new byte[] { 3, 6, 1 }, new byte[] { 5, 2, 2 }, new byte[] { 6, 2, 5 }, new byte[] { 7, 2, 1 },
        new byte[] { 8, 2, 1 }, new byte[] { 9, 3, 5 }, new byte[] { 10, 4, 2 }, new byte[] { 11, 3, 1 },
        new byte[] { 12, 3, 1 }, new byte[] { 13, 6, 1 }, new byte[] { 14, 4, 10 }, new byte[] { 15, 4, 1 },
        new byte[] { 16, 2, 1 }, new byte[] { 17, 2, 1 }, new byte[] { 18, 5, 1 }, new byte[] { 20, 5, 1 },
        new byte[] { 21, 2, 1 }, new byte[] { 22, 3, 1 }, new byte[] { 23, 4, 1 }, new byte[] { 24, 6, 1 }
    };

    private static IDictionary Read() { return (IDictionary)new JavaScriptSerializer().DeserializeObject(DailyActivityModel.Capture(new Model(), DailyRefreshGate.ReadClock)); }
    private static IDictionary Find(IDictionary node, string array, string key, int id)
    {
        foreach (IDictionary item in (IEnumerable)node[array]) if ((int)item[key] == id) return item;
        throw new Exception("missing " + key + " " + id);
    }
    private static void Expect(IDictionary row, string key, object value)
    { if (!object.Equals(row[key], value)) throw new Exception(key + " was " + row[key] + " (expected " + value + ")"); }
    private static Activity Act(byte id, byte category, byte progress, int required = 1, bool active = true)
    { return new Activity { idactivity = id, condition_param = progress, refresh_time = 204400,
        temp = new Template { id = id, idreward = category, cond_param = required, active = active, name = "\u4efb\u52a1 " + id } }; }

    private static IDictionary Stone()
    { return ReadTask(2, 16); }

    private static IDictionary ReadTask(int category, int id)
    { return Find(Find(Read(), "categories", "category_id", category), "tasks", "task_id", id); }

    private static void InconsistentStone(object panelCurrent, object latestCurrent)
    { Inconsistent(Stone(), panelCurrent, latestCurrent); }

    private static void Inconsistent(IDictionary row, object panelCurrent, object latestCurrent)
    {
        Expect(row, "current", panelCurrent); Expect(row, "latest_current", latestCurrent);
        Expect(row, "complete", null); Expect(row, "availability", "unknown");
        Expect(row, "reason", "daily_progress_inconsistent");
    }

    private static void TaskCacheReconciliation(Character character)
    {
        character.spirit.level = 99;
        character.GraveEnd = false;
        foreach (byte[] row in RequestedTasks)
        {
            byte id = row[0], category = row[1], required = row[2];
            uint key = id;
            Reward reward = (Reward)character.act_rewards[(uint)category];
            object originalPanel = reward.acts[key], originalLatest = character.acts[key];
            character.acts.Remove(key);
            reward.acts.Remove(key);
            IDictionary tab = Find(Read(), "categories", "category_id", category);
            if (id != 16)
                foreach (IDictionary task in (IEnumerable)tab["tasks"])
                    if ((int)task["task_id"] == id) throw new Exception("unexpected synthesized task " + id);

            Activity latest = Act(id, category, required, required), panel = Act(id, category, required, required);
            reward.acts[key] = panel;
            Inconsistent(ReadTask(category, id), (int)required, null);
            reward.acts.Remove(key);
            character.acts[key] = latest;
            Inconsistent(ReadTask(category, id), null, (int)required);
            reward.acts[key] = panel;
            latest.refresh_time++;
            Inconsistent(ReadTask(category, id), (int)required, (int)required);

            latest.refresh_time = panel.refresh_time;
            latest.condition_param--;
            Inconsistent(ReadTask(category, id), (int)required, required - 1);
            latest.condition_param++;
            IDictionary complete = ReadTask(category, id);
            Expect(complete, "current", (int)required); Expect(complete, "latest_current", (int)required); Expect(complete, "complete", true);
            Expect(complete, "availability", "unavailable"); Expect(complete, "reason", "complete");

            if (originalPanel == null) reward.acts.Remove(key);
            else reward.acts[key] = originalPanel;
            if (originalLatest == null) character.acts.Remove(key);
            else character.acts[key] = originalLatest;
        }
        character.spirit.level = 100;
        character.GraveEnd = true;
    }

    private static void ExcludedRows(Character character)
    {
        Reward wealth = (Reward)character.act_rewards[1u], guild = (Reward)character.act_rewards[5u];
        foreach (byte id in new byte[] { 1, 2, 4 })
        {
            uint key = id;
            wealth.acts[key] = Act(id, 1, 1);
            Activity latest = Act(id, 1, 0);
            character.acts[key] = latest;
            latest.refresh_time++;
            Expect(ReadTask(1, id), "complete", true);
            Expect(ReadTask(1, id), "reason", "complete");
            latest.condition_param = 1;
            Expect(ReadTask(1, id), "complete", true);
            character.acts.Remove(key);
            Expect(ReadTask(1, id), "complete", true);
        }
        guild.acts[19u] = Act(19, 5, 0, active: false);
        Activity disabled = Act(19, 5, 0, active: false);
        disabled.refresh_time++;
        character.acts[19u] = disabled;
        Expect(ReadTask(5, 19), "complete", false);
        Expect(ReadTask(5, 19), "reason", "activity_disabled");
        character.acts.Remove(19u);
        Expect(ReadTask(5, 19), "reason", "activity_disabled");
    }

    private static void StoneCacheReconciliation(Character character, Reward reward)
    {
        InconsistentStone(null, null);
        Activity initial = Act(16, 2, 0);
        reward.acts[16u] = character.acts[16u] = initial;
        IDictionary row = Stone();
        Expect(row, "complete", false); Expect(row, "availability", "available");

        // 0x106 updates only the latest row through Character.addActs.
        character.acts[16u] = Act(16, 2, 1);
        InconsistentStone(0, 1);
        reward.acts[16u] = Act(16, 2, 1);
        character.acts[16u] = initial;
        InconsistentStone(1, 0);
        Activity latest = Act(16, 2, 1);
        latest.refresh_time++;
        character.acts[16u] = latest;
        InconsistentStone(1, 1);

        character.acts.Remove(16u);
        InconsistentStone(1, null);
        reward.acts.Remove(16u);
        character.acts[16u] = latest;
        InconsistentStone(null, 1);

        // The stock 355 handler sends the same rebuilt row to both caches.
        reward.acts[16u] = character.acts[16u] = latest;
        row = Stone();
        Expect(row, "current", 1); Expect(row, "latest_current", 1); Expect(row, "complete", true);
        Expect(row, "availability", "unavailable"); Expect(row, "reason", "complete");
    }

    private static int Main()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        string wire = "version=3\nnonce=" + nonce + "\nsequence=5\ncommand=daily-observe\n";
        if (Command.Parse(wire, nonce, 4) == null || Command.Parse(wire, nonce, 5) != null ||
            Command.Parse(wire + "action=claim\nexpected_revision=1\ncampaign_id=1\ntroop_id=1\nattempt_id=\n", nonce, 4) != null)
            throw new Exception("daily wire grammar");

        IDictionary result = Read();
        Expect(result, "available", false);
        Expect(result, "reason", "daily_data_unavailable");
        Expect(result, "server_day", null);
        Expect(result, "refresh_clear", null);
        Character character = Globals.character_;
        Reward reward = new Reward { idact_reward = 2, refresh = Clock._now, finished_flag = false, temp = new Template { name = "reward_key" } };
        for (byte categoryId = 1; categoryId <= 6; categoryId++)
            character.act_rewards[(uint)categoryId] = new Reward { idact_reward = categoryId };
        character.act_rewards[2u] = reward;
        foreach (byte id in new byte[] { 6, 8, 17 })
        {
            reward.acts[(uint)id] = character.acts[(uint)id] = Act(id, 2, 1, id == 6 ? 5 : 1);
        }
        Reward timed = new Reward { idact_reward = 6, refresh = Clock._now + 1, finished_flag = true, temp = null };
        character.act_rewards[6u] = timed;
        ((Reward)character.act_rewards[3u]).acts[12u] = character.acts[12u] = Act(12, 3, 0);
        timed.acts[13u] = character.acts[13u] = Act(13, 6, 0);
        character.acts[18u] = Act(18, 5, 0);

        result = Read();
        Expect(result, "available", true); Expect(result, "character_id", "123"); Expect(result, "server_day", 100000); Expect(result, "refresh_clear", true);
        if (((IList)result["categories"]).Count != 6) throw new Exception("six categories required");
        IDictionary category = Find(result, "categories", "category_id", 2), task = Find(category, "tasks", "task_id", 8);
        Expect(category, "name", "reward_key"); Expect(category, "current", 1); Expect(category, "required", 1);
        Expect(category, "reward_ready", true); Expect(category, "reward_claimed", false); Expect(category, "server_finished", false);
        Expect(task, "name", "\u4efb\u52a1 8"); Expect(task, "complete", true); Expect(task, "current", 1); Expect(task, "latest_current", 1);
        Expect(Find(category, "tasks", "task_id", 6), "reason", "spirit_max_level");
        Expect(Find(category, "tasks", "task_id", 17), "reason", "grave_progression_limit");
        category = Find(result, "categories", "category_id", 5);
        Expect(category, "visible", false); Expect(category, "reward_ready", false);
        task = Find(category, "tasks", "task_id", 18);
        Inconsistent(task, null, 0);
        category = Find(result, "categories", "category_id", 6);
        Expect(category, "reward_ready", false); Expect(category, "reward_claimed", true); Expect(category, "server_finished", true);
        Expect(ReadTask(3, 12), "reason", "quiz_closed");
        Expect(Find(category, "tasks", "task_id", 13), "reason", "brawl_closed");
        StoneCacheReconciliation(character, reward);
        TaskCacheReconciliation(character);
        ExcludedRows(character);
        character.brawlinfo.phase = 2;
        Clock._now++;
        category = Find(Read(), "categories", "category_id", 6);
        Expect(ReadTask(3, 12), "availability", "available");
        Expect(Find(category, "tasks", "task_id", 13), "availability", "available");
        Clock._now = Clock._thisDay + 13 * 3600;
        Expect(ReadTask(3, 12), "reason", "quiz_closed");
        Clock._now = Clock._thisDay + 86400;
        result = Read(); Expect(result, "reason", "daily_server_clock_unavailable"); Expect(result, "server_day", null);
        Clock._now = Clock._thisDay - 1;
        result = Read(); Expect(result, "reason", "daily_server_clock_unavailable"); Expect(result, "server_day", null);
        Clock._now = Clock._thisDay + 18000;
        character.next_resource_time = 0;
        result = Read(); Expect(result, "reason", "daily_refresh_pending"); Expect(result, "refresh_clear", null);
        character.next_resource_time = Clock._now;
        result = Read(); Expect(result, "available", false); Expect(result, "refresh_clear", null);
        character.next_resource_time = Clock._now + 86400;
        RootUi.m_bRefreshFive = true;
        result = Read(); Expect(result, "reason", "daily_refresh_pending"); Expect(result, "refresh_clear", null);
        RootUi.m_bRefreshFive = false;
        result = Read(); Expect(result, "available", true); Expect(result, "refresh_clear", true);
        uint now, day, cycle;
        if (DailyRefreshGate.ReadClock(character, out now, out day, out cycle) != "" || cycle != Clock._now)
            throw new Exception("the exact 05:00 tick must belong to the new cycle");
        result = (IDictionary)new JavaScriptSerializer().DeserializeObject(DailyActivityModel.Capture(new Model()));
        Expect(result, "available", false); Expect(result, "reason", "daily_refresh_unverified"); Expect(result, "refresh_clear", null);
        RootUi.m_client = new object();
        Expect(Read(), "reason", "daily_character_unavailable");
        List<byte> reconciled = new List<byte>();
        foreach (byte[] row in RequestedTasks) reconciled.Add(row[0]);
        Console.WriteLine("{\"status\":\"PASS\",\"coherent_tasks\":[" + String.Join(",", reconciled) +
            "],\"scope\":\"daily wire grammar, dual-cache count and refresh coherence, missing rows, excluded wealth and task 19, reward and event boundaries\"}");
        return 0;
    }
}
