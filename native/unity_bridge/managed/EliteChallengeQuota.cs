using System;
using System.Globalization;
using System.Collections;
using System.Threading;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // Current loaded attempts admit a new run. Only a witnessed natural reset
    // establishes daily usage; an unknown daily baseline never means no balance.
    internal static class EliteChallengeQuota
    {
        private static object role;
        private static DailyTransport transport;
        private static ulong roleId;
        private static uint cycle, next;
        private static int baseline;
        private static string fault = "elite_daily_baseline_unobserved";

        internal static void Invalidate(string reason)
        { role = null; transport = null; fault = reason; }

        private static string RequestGate(object character)
        {
            object client = DailyTransport.CurrentClient(character);
            if (client == null || !Monitor.TryEnter(client, 0)) return "elite_request_observation_unavailable";
            try
            {
                IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
                if (handlers == null) return "elite_request_observation_unavailable";
                object handler = handlers[(ushort)0x402];
                if (handler == null || BattleLaunch.OwnsEliteRequest(handler)) return "PASS";
                Delegate callback = D.Read(handler, "handler") as Delegate;
                object source = callback == null ? null : callback.Target;
                if (source != null && source.GetType() == Model.GameType("CampaignUI"))
                {
                    object campaign = D.Read(source, "m_campaign");
                    if (campaign != null && D.Int(D.Read(campaign, "idcampaign_type")) == 4)
                    return "elite_unowned_request_count_unknown";
                }
                return "PASS";
            }
            finally { Monitor.Exit(client); }
        }

        internal static void RecordNaturalReset(object character, DailyTransport connection,
            uint dailyCycle, uint nextRefresh, int previousRemaining)
        {
            role = null; transport = null; fault = "elite_daily_reset_unverified";
            int remaining = Convert.ToInt32(D.Read(character, "anecdote_hero_try"), CultureInfo.InvariantCulture);
            int bought = Convert.ToInt32(D.Read(character, "anecdote_hero_buy"), CultureInfo.InvariantCulture);
            uint now, day, current;
            if (previousRemaining < 0 || remaining != Math.Max(10, previousRemaining) || bought != 0 || connection == null ||
                DailyRefreshGate.ReadClock(character, out now, out day, out current) != "" || current != dailyCycle ||
                nextRefresh != Convert.ToUInt32(D.Read(character, "next_resource_time"), CultureInfo.InvariantCulture) ||
                !connection.Same(DailyTransport.Read(DailyTransport.CurrentClient(character)))) return;
            role = character; roleId = Convert.ToUInt64(D.Read(character, "id"), CultureInfo.InvariantCulture);
            transport = connection; cycle = dailyCycle; next = nextRefresh;
            baseline = remaining; fault = "";
        }

        internal static string Read(object character, out uint currentCycle, out int remaining, out int completed, out int bought)
        {
            currentCycle = 0; remaining = completed = bought = 0;
            if (character == null) return "elite_character_unavailable";
            uint now, day;
            string gate = DailyRefreshGate.ReadClock(character, out now, out day, out currentCycle);
            remaining = Convert.ToInt32(D.Read(character, "anecdote_hero_try"), CultureInfo.InvariantCulture);
            bought = Convert.ToInt32(D.Read(character, "anecdote_hero_buy"), CultureInfo.InvariantCulture);
            if (gate != "") return gate;
            if (remaining < 0 || bought < 0) return "elite_quota_schema";
            DailyTransport currentTransport = DailyTransport.Read(DailyTransport.CurrentClient(character));
            if (currentTransport == null) return "elite_transport_unavailable";
            gate = RequestGate(character);
            if (gate != "PASS") return gate;
            // Missing/reset/disconnected history affects only the daily counter.
            // The caller binds this current balance to its own run and identity.
            if (fault != "" || !object.ReferenceEquals(role, character) || cycle != currentCycle ||
                roleId != Convert.ToUInt64(D.Read(character, "id"), CultureInfo.InvariantCulture) ||
                next != Convert.ToUInt32(D.Read(character, "next_resource_time"), CultureInfo.InvariantCulture) ||
                transport == null || !transport.Same(currentTransport))
            { completed = -1; return "PASS"; }
            if (bought != 0 || remaining > baseline)
            { fault = "elite_daily_usage_unknown"; completed = -1; return "PASS"; }
            completed = baseline - remaining;
            return "PASS";
        }

        internal static string Admission(object character, int target)
        {
            uint dailyCycle; int remaining, completed, bought;
            string gate = Read(character, out dailyCycle, out remaining, out completed, out bought);
            if (gate != "PASS") return gate;
            if (target < 1 || target > 10) return "elite_daily_target_invalid";
            // The coordinator owns the target for this newly started run.
            return remaining <= 0 ? "elite_attempts_exhausted" : "PASS";
        }

        internal static string Json(object character)
        {
            uint dailyCycle; int remaining, completed, bought;
            string gate = Read(character, out dailyCycle, out remaining, out completed, out bought);
            return "{\"cycle\":" + dailyCycle + ",\"verified\":" + (gate == "PASS" ? "true" : "false") +
                ",\"remaining\":" + remaining + ",\"bought\":" + bought +
                ",\"baseline\":" + (gate == "PASS" && completed >= 0 ? baseline.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"completed\":" + (gate == "PASS" && completed >= 0 ? completed.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"reason\":" + Command.Quote(gate) + ",\"evidence\":" + Command.Quote(gate == "PASS" ?
                    completed >= 0 ? "observed_natural_0x24_callback" : "current_loaded_elite_balance" : "") + "}";
        }
    }
}
