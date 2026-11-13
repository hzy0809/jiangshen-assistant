using System;
using UnityEngine;

// Instrumented, fail-closed boundary substitute for BattleLaunch. The mode
// tests prove which guard method the real adapter calls and which stock branch
// it supplies, but do not run the real named-event/mutex or cancellation code.
// The guard's cross-process behavior is tested by its own fixture.
namespace JiangShen.AssistantRuntime
{
    internal delegate bool BattleLaunchCheck();
    internal delegate void BattleLaunchAction();
    internal sealed class BattleLaunchAttempt
    {
        internal readonly BattleMode Mode;
        internal readonly Component Source, Formation;
        internal readonly object Target;
        internal readonly ushort Opcode;
        internal int InitialCalls;
        internal Component InitialBattle;
        internal object InitialReport;
        internal BattleLaunchAttempt(BattleMode mode, Component source, Component formation, object target, ushort opcode)
        { Mode = mode; Source = source; Formation = formation; Target = target; Opcode = opcode; }
        internal void InitialResponseApplied(Component battle, object report)
        {
            if (battle == null || report == null || InitialCalls != 0) throw new ProbeFailure("stub_initial_response_identity");
            InitialCalls++; InitialBattle = battle; InitialReport = report;
        }
    }
    internal static class BattleLaunch
    {
        internal static int DirectCalls, FormationCalls, ReportBoundCalls, CheckCalls;
        internal static bool ForceDeny;
        internal static BattleLaunchAttempt Last;
        internal static BattlePresentationOwnership LastOwnership;
        internal static void Reset()
        { DirectCalls = FormationCalls = ReportBoundCalls = CheckCalls = 0; ForceDeny = false; Last = null; LastOwnership = null; }
        private static void Admit(BattleLaunchCheck check)
        {
            CheckCalls++;
            if (ForceDeny || check == null || !check()) throw new ProbeFailure("fixture_launch_guard_denied");
        }
        internal static BattleLaunchAttempt Direct(BattleMode mode, Component source, object target,
            BattleLaunchCheck check, BattleLaunchAction stock, ushort opcode)
        {
            DirectCalls++; Admit(check);
            if (source == null || target == null || stock == null || opcode == 0) throw new ProbeFailure("fixture_direct_identity");
            Last = new BattleLaunchAttempt(mode, source, null, target, opcode); stock(); return Last;
        }
        internal static BattleLaunchAttempt Formation(BattleMode mode, Component formation, Component source,
            object target, BattleLaunchCheck check, BattleLaunchAction afterAttack)
        {
            FormationCalls++; Admit(check);
            if (formation == null || source == null || target == null) throw new ProbeFailure("fixture_formation_identity");
            Last = new BattleLaunchAttempt(mode, source, formation, target, 0);
            SharedDevelopmentContext.Call(formation, "OnClickSetCurrentForm");
            if (afterAttack != null) afterAttack();
            return Last;
        }
        internal static NativeBattleStartTicket ReportBound(BattlePresentationOwnership owner, object role, ulong roleId, Component battle, object report)
        {
            ReportBoundCalls++;
            if (Last == null || owner == null || role == null || roleId == 0 || battle == null || report == null ||
                owner.Mode != Last.Mode || !ReferenceEquals(owner.Source, Last.Source) || !ReferenceEquals(owner.Target, Last.Target))
                throw new ProbeFailure("fixture_guard_report_binding");
            LastOwnership = owner;
            return null;
        }
    }
}
