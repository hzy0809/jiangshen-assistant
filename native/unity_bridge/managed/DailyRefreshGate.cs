using System;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate string DailyRefreshAdmission(object character, out uint now, out uint day, out uint cycle);

    internal static class DailyRefreshGate
    {
        internal static string Read(object character, out uint now, out uint day, out uint cycle)
        {
            // Read the game's loaded client projection, not a new server reply.
            // Missing bridge callback history must not force LoginUI.Refresh.
            return ReadClock(character, out now, out day, out cycle);
        }

        internal static string ReadClock(object character, out uint now, out uint day, out uint cycle)
        {
            Type time = Model.FirstType("TimeUtil");
            now = (uint)D.Static(time, "_now");
            day = (uint)D.Static(time, "_thisDay");
            cycle = 0;
            if (day == 0 || now < day || now >= day + 86400L) return "daily_server_clock_unavailable";
            long five = day + 18000L;
            long current = now >= five ? five : five - 86400L;
            if (current <= 0 || current > UInt32.MaxValue) return "daily_server_clock_unavailable";
            cycle = (uint)current;
            // RootUI sends 0x24 at next_resource_time <= Now, keeps this
            // flag during the request, and resets daily caches on success.
            bool refreshing = (bool)D.Static(Model.GameType("RootUI"), "m_bRefreshFive");
            uint next = (uint)D.Read(character, "next_resource_time");
            return refreshing || next <= now ? "daily_refresh_pending" : "";
        }
    }
}
