using System;

namespace JiangShen.AssistantRuntime
{
    internal enum BattleMode
    {
        Campaign, NormalChallenge, Grave, Trial, TrialBoss, Divinity,
        TrainingMonster, OwnBuildingMonster, FriendBuildingMonster,
        Arena, PointRace, OwnDemon
    }

    internal enum BattleSettlementPath { RequestEnd, EmbeddedEnd, SettledResponse }

    // The production per-report state. A begun side effect is never repeated,
    // including when its completion cannot be observed. Unknown retains ownership.
    internal sealed class BattleLifecycle
    {
        internal readonly BattleMode Mode;
        internal readonly BattleSettlementPath Path;
        internal readonly string OperationId, TargetId;
        internal readonly object Role, Target, Source, Previous, Battle, Report;
        internal readonly ulong RoleId, InitialReportId;
        internal bool PresentationStarted { get; private set; }
        internal bool Presented { get; private set; }
        internal bool ApplyStarted { get; private set; }
        internal bool Applied { get; private set; }
        internal bool OpenStarted { get; private set; }
        internal bool Opened { get; private set; }
        internal string Error { get; private set; }
        internal object Receipt { get; private set; }
        internal ulong SettledReportId { get; private set; }

        internal BattleLifecycle(BattleMode mode, BattleSettlementPath path, string operationId,
            object role, ulong roleId, object target, string targetId, object source,
            object previous, object battle, object report, ulong initialReportId)
        {
            if (!Enum.IsDefined(typeof(BattleMode), mode) || !Enum.IsDefined(typeof(BattleSettlementPath), path) ||
                String.IsNullOrEmpty(operationId) || role == null || roleId == 0 || target == null ||
                String.IsNullOrEmpty(targetId) || source == null || previous == null || battle == null || report == null)
                throw new ArgumentException("battle_lifecycle_identity");
            Mode = mode; Path = path; OperationId = operationId; Role = role; RoleId = roleId;
            Target = target; TargetId = targetId; Source = source; Previous = previous;
            Battle = battle; Report = report; InitialReportId = initialReportId; Error = "";
        }

        internal bool TryBeginPresentation()
        { if (Error != "" || PresentationStarted) return false; PresentationStarted = true; return true; }
        internal void CompletePresentation()
        { if (Error == "" && PresentationStarted) Presented = true; }
        internal bool TryBeginApply(object receipt)
        {
            if (Error != "" || ApplyStarted || receipt == null) return false;
            Receipt = receipt; ApplyStarted = true; return true;
        }
        internal void CompleteApply(ulong reportId)
        { if (Error == "" && ApplyStarted) { SettledReportId = reportId; Applied = true; } }
        internal bool TryBeginOpen()
        { if (Error != "" || !Presented || !Applied || OpenStarted) return false; OpenStarted = true; return true; }
        internal void CompleteOpen()
        { if (Error == "" && OpenStarted) Opened = true; }
        internal void Unknown(string reason)
        { if (Error == "") Error = String.IsNullOrEmpty(reason) ? "battle_lifecycle_unknown" : reason; }
    }
}
