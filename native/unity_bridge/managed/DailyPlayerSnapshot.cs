using System;
using System.Collections;
using System.Collections.Generic;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;
using Reader = JiangShen.AssistantRuntime.PlayerStateServerSnapshot.Reader;

namespace JiangShen.AssistantRuntime
{
    // Bounded projection of only the daily fields of the pinned SCPlayerInfo_0x10.
    internal sealed class DailyPlayerSnapshot
    {
        internal ulong CharacterId, GuildId;
        internal uint ServerTime, Next;
        internal int Zone;
        private uint guildLevel, contribution, draws, donationRefresh, donation;
        private ulong memberGuild;
        private bool activeGuild;
        private readonly Dictionary<uint, Activity> acts = new Dictionary<uint, Activity>();
        private readonly Dictionary<uint, Reward> rewards = new Dictionary<uint, Reward>();
        private sealed class Activity { internal uint Progress; internal int Refresh; }
        private sealed class Reward { internal uint Refresh; internal bool Finished; }

        internal static DailyPlayerSnapshot Decode(byte[] body)
        {
            if (body == null || body.Length == 0 || body.Length > PlayerStateServerSnapshot.MaxBytes) throw new FormatException("daily_16_bound");
            DailyPlayerSnapshot s = new DailyPlayerSnapshot();
            Reader r = new Reader(body, 0, body.Length), player = null, guild = null;
            int seen = 0;
            while (!r.End)
            {
                int wire, field = r.Tag(out wire);
                if (field == 1 || field == 2 || field == 3 || field == 17)
                {
                    Once(ref seen, field);
                    if (field == 1) s.ServerTime = Uint(r, wire);
                    else if (field == 2) s.Zone = Signed(Uint(r, wire));
                    else if (field == 3) player = Message(r, wire);
                    else guild = Message(r, wire);
                }
                else if (field == 15) s.ReadActivity(Message(r, wire));
                else if (field == 48) s.ReadReward(Message(r, wire));
                else r.Skip(wire);
            }
            if (seen != ((1 << 1) | (1 << 2) | (1 << 3) | (1 << 17)) || s.ServerTime == 0 ||
                s.acts.Count == 0 || s.rewards.Count == 0) throw new FormatException("daily_16_required");
            s.ReadPlayer(player);
            s.ReadGuild(guild);
            return s;
        }

        private void ReadPlayer(Reader r)
        {
            int seen = 0;
            while (!r.End)
            {
                int wire, field = r.Tag(out wire);
                if (field == 1) { Once(ref seen, 1); CharacterId = Scalar(r, wire); }
                else if (field == 92) { Once(ref seen, 2); Next = Uint(r, wire); }
                else r.Skip(wire);
            }
            if (seen != 6 || CharacterId == 0 || Next == 0) throw new FormatException("daily_16_role_clock");
        }

        private void ReadActivity(Reader r)
        {
            uint id = 0;
            int seen = 0;
            Activity a = new Activity();
            while (!r.End)
            {
                int wire, field = r.Tag(out wire);
                if (field == 1) { Once(ref seen, 1); id = Uint(r, wire); }
                else if (field == 2) { Once(ref seen, 2); a.Progress = Uint(r, wire); }
                else if (field == 3) { Once(ref seen, 3); a.Refresh = Signed(Uint(r, wire)); }
                else r.Skip(wire);
            }
            if (id == 0 || id > 255 || acts.ContainsKey(id)) throw new FormatException("daily_16_activity");
            acts.Add(id, a);
        }

        private void ReadReward(Reader r)
        {
            uint id = 0;
            int seen = 0;
            Reward a = new Reward();
            while (!r.End)
            {
                int wire, field = r.Tag(out wire);
                if (field == 1) { Once(ref seen, 1); id = Uint(r, wire); }
                else if (field == 2) { Once(ref seen, 2); a.Refresh = Uint(r, wire); }
                else if (field == 3) { Once(ref seen, 3); a.Finished = Bool(r, wire); }
                else r.Skip(wire);
            }
            if (id == 0 || id > 6 || rewards.ContainsKey(id)) throw new FormatException("daily_16_reward");
            rewards.Add(id, a);
        }

        private void ReadGuild(Reader r)
        {
            int seen = 0;
            Reader guild = null;
            bool self = false;
            Dictionary<ulong, bool> members = new Dictionary<ulong, bool>();
            while (!r.End)
            {
                int wire, field = r.Tag(out wire);
                if (field == 1) { Once(ref seen, 1); GuildId = Scalar(r, wire); }
                else if (field == 2) { Once(ref seen, 2); guild = Message(r, wire); }
                else if (field == 3) { if (ReadMember(Message(r, wire), members)) self = true; }
                else r.Skip(wire);
            }
            if ((seen & 2) == 0) throw new FormatException("daily_16_guild_identity");
            if (GuildId == 0)
            {
                if (self) throw new FormatException("daily_16_guildless_member");
                return;
            }
            if (guild == null || !self) throw new FormatException("daily_16_guild_member_missing");
            Reader data = null;
            while (!guild.End)
            {
                int wire, field = guild.Tag(out wire);
                if (field == 1)
                {
                    if (data != null) throw new FormatException("daily_16_guild_duplicate");
                    data = Message(guild, wire);
                }
                else guild.Skip(wire);
            }
            if (data == null) throw new FormatException("daily_16_guild_data_missing");
            ulong id = 0; seen = 0;
            while (!data.End)
            {
                int wire, field = data.Tag(out wire);
                if (field == 1) { Once(ref seen, 1); id = Scalar(data, wire); }
                else if (field == 5) { Once(ref seen, 5); activeGuild = Bool(data, wire); }
                else if (field == 7) { Once(ref seen, 7); guildLevel = Uint(data, wire); }
                else data.Skip(wire);
            }
            if (id != GuildId || !activeGuild || guildLevel == 0 || guildLevel > UInt16.MaxValue || memberGuild != GuildId)
                throw new FormatException("daily_16_guild_data");
        }

        private bool ReadMember(Reader r, Dictionary<ulong, bool> members)
        {
            ulong id = 0;
            bool leave = false;
            Reader data = null;
            int seen = 0;
            while (!r.End)
            {
                int wire, field = r.Tag(out wire);
                if (field == 1) { Once(ref seen, 1); data = Message(r, wire); }
                else if (field == 3) { Once(ref seen, 3); id = Scalar(r, wire); }
                else if (field == 4) { Once(ref seen, 4); leave = Bool(r, wire); }
                else r.Skip(wire);
            }
            if (id == 0 || seen != 26 || members.Count >= 512 || members.ContainsKey(id)) throw new FormatException("daily_16_member");
            members.Add(id, true);
            if (id != CharacterId) return false;
            if (leave) throw new FormatException("daily_16_member_left");
            seen = 0;
            while (!data.End)
            {
                int wire, field = data.Tag(out wire);
                if (field == 1 || field == 2 || field == 4 || field == 5 || field == 8)
                {
                    Once(ref seen, field);
                    if (field == 1) memberGuild = Scalar(data, wire);
                    else if (field == 2) contribution = Uint(data, wire);
                    else if (field == 4) donation = Uint(data, wire);
                    else if (field == 5) draws = Uint(data, wire);
                    else donationRefresh = Uint(data, wire);
                }
                else data.Skip(wire);
            }
            if (memberGuild == 0 || draws > 255 || donation > 3) throw new FormatException("daily_16_member_values");
            return true;
        }

        internal string Reconcile(object role)
        {
            if ((ulong)D.Read(role, "id") != CharacterId || (uint)D.Read(role, "next_resource_time") != Next ||
                (ulong)D.Read(role, "idguild") != GuildId) return "daily_16_rebuilt_role_mismatch";
            IDictionary rows = D.Read(role, "acts") as IDictionary, tabs = D.Read(role, "act_rewards") as IDictionary;
            if (rows == null || tabs == null || rows.Count != acts.Count || tabs.Count != rewards.Count)
                return "daily_16_rebuilt_counts_mismatch";
            int[] panelCounts = new int[7];
            foreach (KeyValuePair<uint, Reward> entry in rewards)
            {
                object row = tabs[entry.Key];
                if (row == null || row.GetType() != Model.FirstType("Database.Activity_Reward") || (byte)D.Read(row, "idact_reward") != entry.Key ||
                    (uint)D.Read(row, "refresh") != entry.Value.Refresh || (bool)D.Read(row, "finished_flag") != entry.Value.Finished)
                    return "daily_16_rebuilt_reward_mismatch";
            }
            foreach (KeyValuePair<uint, Activity> entry in acts)
            {
                object row = rows[entry.Key];
                if (row == null || row.GetType() != Model.FirstType("Database.Activity") || (byte)D.Read(row, "idactivity") != entry.Key ||
                    (int)D.Read(row, "refresh_time") != entry.Value.Refresh)
                    return "daily_16_rebuilt_activity_mismatch";
                object template = D.Read(row, "temp");
                if (template == null || (byte)D.Read(template, "id") != entry.Key) return "daily_16_rebuilt_template_mismatch";
                int required = (int)D.Read(template, "cond_param");
                if (required < 0 || required > Byte.MaxValue) return "daily_16_rebuilt_template_requirement";
                // _character_activity.Set converts the wire uint to a byte
                // before Activity's constructor clamps to the template or
                // marks a closed task complete. Verify that projection only.
                byte expected = unchecked((byte)entry.Value.Progress);
                if (expected > required) expected = (byte)required;
                byte observed = (byte)D.Read(row, "condition_param");
                if (observed != expected)
                {
                    // LoginUI builds acts before it populates spirit/graves.
                    // A row can retain the clamped value or be rebuilt by the
                    // normal activity update after the task actually closes.
                    if (observed != required || (entry.Key != 6 && entry.Key != 17))
                        return "daily_16_rebuilt_activity_mismatch";
                    if (!object.ReferenceEquals(role, D.Static(Model.FirstType("Global"), "character_")))
                        return "daily_16_rebuilt_role_mismatch";
                    if (!(bool)D.Method(row.GetType(), "_check_act_closecondition", Type.EmptyTypes, false, typeof(bool)).Invoke(row, null))
                        return "daily_16_rebuilt_activity_mismatch";
                }
                int tab = (byte)D.Read(template, "idreward");
                if (tab < 1 || tab > 6 || !tabs.Contains((uint)tab)) return "daily_16_rebuilt_category_mismatch";
                if ((bool)D.Read(template, "active"))
                {
                    IDictionary listed = D.Read(tabs[(uint)tab], "acts") as IDictionary;
                    if (listed == null || !object.ReferenceEquals(listed[entry.Key], row)) return "daily_16_rebuilt_listed_activity_mismatch";
                    panelCounts[tab]++;
                }
            }
            foreach (uint tab in rewards.Keys)
            {
                IDictionary listed = D.Read(tabs[tab], "acts") as IDictionary;
                if (listed == null || listed.Count != panelCounts[tab]) return "daily_16_rebuilt_listed_count_mismatch";
            }
            IDictionary guilds = D.Read(role, "guilds") as IDictionary, members = D.Read(role, "members") as IDictionary;
            if (guilds == null || members == null) return "daily_16_rebuilt_guild_schema";
            if (GuildId == 0) return guilds.Count == 0 && members.Count == 0 ? "" : "daily_16_rebuilt_guildless_mismatch";
            object guild = guilds[GuildId], member = members[CharacterId];
            if (guild == null || guild.GetType() != Model.FirstType("Database.Guild") || (ulong)D.Read(guild, "id") != GuildId ||
                (bool)D.Read(guild, "active") != activeGuild || (ushort)D.Read(guild, "level") != guildLevel ||
                member == null || member.GetType() != Model.FirstType("Database.GuildMember") || (ulong)D.Read(member, "id") != CharacterId ||
                (ulong)D.Read(member, "idguild") != GuildId || (uint)D.Read(member, "contribution") != contribution ||
                (byte)D.Read(member, "guild_lottery_count") != draws || (uint)D.Read(member, "donate_refresh") != donationRefresh ||
                (byte)D.Read(member, "today_donate") != donation) return "daily_16_rebuilt_membership_mismatch";
            return "";
        }

        internal static void ReadClock36(byte[] body, out uint result, out uint next)
        {
            Reader r = new Reader(body, 0, body.Length);
            int seen = 0;
            result = next = 0;
            while (!r.End)
            {
                int wire, field = r.Tag(out wire);
                if (field == 1) { Once(ref seen, 1); result = Uint(r, wire); }
                else if (field == 2) { Once(ref seen, 2); next = Uint(r, wire); }
                else r.Skip(wire);
            }
            if (seen != 6) throw new FormatException("daily_36_required");
        }

        private static void Once(ref int seen, int field)
        { int mask = 1 << field; if ((seen & mask) != 0) throw new FormatException("daily_duplicate_field"); seen |= mask; }
        private static ulong Scalar(Reader r, int wire)
        { if (wire != 0) throw new FormatException("daily_scalar_wire"); return r.Varint(); }
        private static uint Uint(Reader r, int wire) { return checked((uint)Scalar(r, wire)); }
        private static int Signed(uint zig) { return (int)(zig >> 1) ^ -((int)zig & 1); }
        private static bool Bool(Reader r, int wire)
        { uint n = Uint(r, wire); if (n > 1) throw new FormatException("daily_boolean"); return n == 1; }
        private static Reader Message(Reader r, int wire)
        { if (wire != 2) throw new FormatException("daily_message_wire"); return r.Message(); }
    }
}
