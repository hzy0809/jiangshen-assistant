using System;
using System.Globalization;

namespace JiangShen.AssistantRuntime
{
    // Detached values sampled by the helper's real Update. No network handler is
    // installed, replaced, wrapped or removed by the observer.
    internal sealed class CampaignFrame
    {
        internal object Campaign, Battle, ResultUi, Character, ReportToken, Scene, CampaignData, Target, TargetData, QueueClient;
        internal string CharacterId;
        internal double PlaybackRemaining;
        internal int CampaignId, TroopId, CampaignType, FinishCount, FinishMax, SampleTick;
        internal int ReportResultStatus, BattleResultLevel, ResultBattleLevel;
        internal ulong ReportId, ResultReportId;
        internal bool BattleVisible, Fighting, Waiting, UseEnd, Surrendered, Replay, ResultVisible, ShowDefenderResult;
        internal bool CampaignFinished, Debug, Gm, Killed, QueueObserved, RefreshPending, FinalPending;
        internal string QueueError;
        internal CampaignQueuedPacket[] QueuedPackets;
        internal CampaignPacket Packet, AppliedLossReceipt;
    }
    internal sealed class CampaignQueuedPacket
    {
        internal object Token;
        internal CampaignPacket Packet;
        internal bool RetryableRejection;
    }
    internal sealed class CampaignPacket
    {
        internal int Result, Type, CampaignId, TroopId;
        internal bool IsWin;
        internal ulong ReportId;
    }
    internal sealed class CampaignObserver
    {
        internal const string AppliedLossEvidence = "applied_owned_campaign_loss_receipt";
        private readonly int allowedCampaignType;
        private int campaignType;
        internal CampaignObserver() : this(0) { }
        internal CampaignObserver(int allowedType) { allowedCampaignType = allowedType; }
        internal string AttemptId = "", Status = "NONE", Outcome = "UNKNOWN", Evidence = "not_started";
        internal int CommandSequence, CampaignId, TroopId;
        internal bool MutationAttempted;
        private object campaign, battle, character, reportToken, scene, campaignData, target, targetData, queueClient, finalToken, observedLossResult;
        private string characterId;
        private CampaignPacket receipt;
        private bool zeroObserved, battleObserved, lateEndRequired, finalConsumed, resultWait, reportDefender;
        private bool elitePageContinued, nativeRejected, pageStateConfirmed;
        private string pageOutcome = "UNKNOWN";
        private int finishCount, finishMax, resultWaitTick, reportResultStatus, reportBattleLevel;
        private ulong previousReport, settledReport;
        private double playbackRemaining;
        private readonly System.Collections.Generic.Dictionary<string, bool> used = new System.Collections.Generic.Dictionary<string, bool>();

        internal bool Pending { get { return Status == "SUBMITTED" || Status == "WAITING" || Status == "UNKNOWN"; } }
        internal bool Finished { get { return Status == "SETTLED" || campaignType == 4 && Status == "CONTINUED"; } }
        internal bool OwnsBattlePresentation(object candidate, object report)
        {
            return Status == "WAITING" && zeroObserved && battleObserved &&
                object.ReferenceEquals(candidate, battle) && object.ReferenceEquals(report, reportToken);
        }
        internal bool Begin(string id, int sequence, CampaignFrame before, out string reason)
        {
            reason = "";
            if (!Command.ValidNonce(id)) { reason = "attempt_id"; return false; }
            if (used.ContainsKey(id)) { reason = "attempt_already_used"; return false; }
            if (Pending) { reason = "attempt_unresolved"; return false; }
            if (before == null || before.Campaign == null || before.Character == null || String.IsNullOrEmpty(before.CharacterId) || before.CharacterId == "0" || before.CampaignId < 1 || before.TroopId < 1 || before.Packet != null || before.BattleVisible)
            { reason = "battle_baseline_unavailable"; return false; }
            if (before.Scene == null || before.CampaignData == null || before.Target == null || before.TargetData == null || before.CampaignType != allowedCampaignType || before.CampaignFinished || before.Debug || before.Gm || before.Killed || before.FinishCount < 0 || before.FinishMax < 1 || before.FinishCount >= before.FinishMax)
            { reason = "mainline_progress_baseline_unavailable"; return false; }
            if (!before.QueueObserved || before.QueueClient == null || before.QueueError != null || before.RefreshPending || before.FinalPending || before.QueuedPackets == null || before.QueuedPackets.Length != 0)
            { reason = "settlement_queue_baseline_unavailable"; return false; }
            used.Add(id, true); AttemptId = id; CommandSequence = sequence;
            CampaignId = before.CampaignId; TroopId = before.TroopId;
            campaignType = before.CampaignType;
            campaign = before.Campaign; battle = before.Battle; receipt = null;
            character = before.Character; characterId = before.CharacterId;
            scene = before.Scene; campaignData = before.CampaignData; target = before.Target; targetData = before.TargetData;
            queueClient = before.QueueClient; finishCount = before.FinishCount; finishMax = before.FinishMax; previousReport = before.ReportId; settledReport = 0;
            finalToken = null; finalConsumed = false; lateEndRequired = false; resultWait = false;
            observedLossResult = null;
            reportToken = null; playbackRemaining = Double.PositiveInfinity;
            zeroObserved = false; battleObserved = false; Status = "SUBMITTED";
            elitePageContinued = nativeRejected = pageStateConfirmed = false; pageOutcome = "UNKNOWN";
            Outcome = "UNKNOWN"; Evidence = "start_recorded_before_invocation";
            return true;
        }
        internal void Invoking() { MutationAttempted = true; }
        internal void Unknown(string reason)
        {
            if (Status == "NONE") return;
            Status = "UNKNOWN"; Outcome = "UNKNOWN"; Evidence = reason;
        }
        internal void Observe(CampaignFrame frame)
        {
            if (Status == "NONE" || Status == "UNKNOWN" || Finished) return;
            if (frame == null) { Unknown("battle_observation_missing"); return; }
            if (!object.ReferenceEquals(frame.Character, character) || frame.CharacterId != characterId) { Unknown("character_identity_changed"); return; }
            if (!object.ReferenceEquals(frame.Scene, scene) || !object.ReferenceEquals(frame.CampaignData, campaignData) || !object.ReferenceEquals(frame.Target, target) || !object.ReferenceEquals(frame.TargetData, targetData))
            { Unknown("campaign_progress_identity_changed"); return; }
            if (frame.CampaignType != campaignType || frame.Debug || frame.Gm || frame.RefreshPending) { Unknown("campaign_refresh_or_debug"); return; }
            if (!object.ReferenceEquals(frame.QueueClient, queueClient) || frame.QueueError != null) { Unknown("settlement_queue_unavailable"); return; }
            if (frame.Battle == null && !battleObserved) return;
            // setPrevUI/setReport/newBattleEnd precede BattleUI.Show. A newly
            // discovered hidden instance is not a report identity witness yet.
            if (battle == null && !battleObserved && !frame.BattleVisible) return;
            if (battle == null && frame.BattleVisible && frame.ReportId == 0) battle = frame.Battle;
            if (!object.ReferenceEquals(frame.Battle, battle)) { Unknown("battle_instance_changed"); return; }
            // The scene may transition only after a settled result has been
            // acknowledged. Never reinterpret a previous report as a new fight.
            if (!frame.BattleVisible) { if (battleObserved) Unknown("battle_disappeared_before_settlement"); return; }
            battleObserved = true;
            if (!object.ReferenceEquals(frame.Campaign, campaign) || frame.CampaignId != CampaignId || frame.TroopId != TroopId)
            { Unknown("battle_target_changed"); return; }
            if (frame.Replay || frame.Surrendered) { Unknown("replay_or_surrender"); return; }
            if (Double.IsNaN(frame.PlaybackRemaining) || Double.IsInfinity(frame.PlaybackRemaining) || frame.PlaybackRemaining > playbackRemaining + 0.01)
            { Unknown("playback_restarted_or_invalid"); return; }
            playbackRemaining = frame.PlaybackRemaining;
            if (frame.ReportToken == null) { Unknown("report_identity_unavailable"); return; }
            if (reportToken == null)
            {
                reportToken = frame.ReportToken;
                reportResultStatus = frame.ReportResultStatus; reportDefender = frame.ShowDefenderResult; reportBattleLevel = frame.BattleResultLevel;
            }
            else if (!object.ReferenceEquals(reportToken, frame.ReportToken)) { Unknown("report_object_changed"); return; }
            if (frame.ReportId == 0) zeroObserved = true;
            if (frame.UseEnd) lateEndRequired = true;
            Status = "WAITING";
            // A final 1032 copy is captured before the game's own handler runs.
            // Receipt validity and queue consumption are separate witnesses.
            if (frame.QueueObserved)
            {
                bool present = false;
                if (frame.QueuedPackets == null) { Unknown("settlement_queue_unavailable"); return; }
                foreach (CampaignQueuedPacket queued in frame.QueuedPackets)
                {
                    if (queued != null && queued.Token != null && queued.Packet != null &&
                        queued.Packet.Result == 1 && queued.RetryableRejection && finalToken == null &&
                        zeroObserved && lateEndRequired && frame.ReportId == 0 && frame.UseEnd && frame.Waiting)
                    {
                        Evidence = "explicit_early_end_rejection_waiting_retry";
                        continue; // This owned request has not settled the battle.
                    }
                    if (queued == null || queued.Token == null || !Valid(queued.Packet)) { Unknown("authoritative_packet_invalid"); return; }
                    if (finalToken != null && !object.ReferenceEquals(queued.Token, finalToken)) { Unknown("authoritative_packet_conflict"); return; }
                    if (finalToken == null)
                    {
                        if (!zeroObserved || frame.ReportId != 0 || !lateEndRequired) { Unknown("authoritative_packet_unbound"); return; }
                        if (receipt != null && !Same(receipt, queued.Packet)) { Unknown("authoritative_packet_conflict"); return; }
                        finalToken = queued.Token; receipt = Copy(queued.Packet); Evidence = "captured_queued_authoritative_receipt";
                    }
                    else if (finalConsumed || !Same(receipt, queued.Packet)) { Unknown("authoritative_packet_conflict"); return; }
                    present = true;
                }
                if (finalToken != null && !present) finalConsumed = true;
            }
            if (frame.Packet != null)
            {
                CampaignPacket p = frame.Packet;
                if (campaignType == 4 && p.Result == 1 && BattleLaunch.NativeEliteRejection(AttemptId, CommandSequence))
                {
                    // The original BattleUI warning branch consumes this
                    // packet; it is neither a win nor an absent response.
                    Evidence = "elite_native_acceleration_warning_pending";
                }
                else
                {
                if (!zeroObserved || !Valid(p))
                { Unknown("authoritative_packet_invalid"); return; }
                if (receipt != null && !Same(receipt, p))
                { Unknown("authoritative_packet_conflict"); return; }
                // An embedded 1026 packet is applied by fight() only when
                // UseEnd is false. It must never stand in for a late 1032.
                if (!lateEndRequired) { receipt = Copy(p); Evidence = "captured_authoritative_receipt"; }
                }
            }
            if (frame.AppliedLossReceipt != null)
            {
                CampaignPacket p = frame.AppliedLossReceipt;
                if ((campaignType != 0 && campaignType != 2) || !zeroObserved || !lateEndRequired ||
                    !Valid(p) || p.IsWin || p.ReportId != 0)
                { Unknown("applied_loss_receipt_invalid"); return; }
                if (receipt != null && !Same(receipt, p))
                { Unknown("authoritative_packet_conflict"); return; }
                receipt = Copy(p);
            }
            if (receipt != null && frame.ReportId != 0 && frame.ReportId != receipt.ReportId)
            { Unknown("report_changed"); return; }
            bool advanced = frame.FinishMax == finishMax && finishCount + 1 == finishMax && frame.FinishCount == finishCount + 1 && frame.Killed;
            if (frame.FinishMax != finishMax || frame.FinishCount < finishCount || frame.FinishCount > finishCount + 1 || (frame.Killed && !advanced))
            { Unknown("campaign_progress_invalid"); return; }
            if (frame.Packet == null && frame.ResultVisible)
            {
                // VIP playback can open the result before the final request is
                // answered. UI visibility alone neither settles nor terminates.
                if (frame.Waiting || frame.UseEnd || frame.Fighting) { Evidence = receipt == null ? "awaiting_authoritative_receipt" : Evidence; return; }
                if (!frame.QueueObserved) return; // Contention is not an empty queue witness.
                if (campaignType == 4 && !frame.FinalPending && frame.QueuedPackets.Length == 0 &&
                    frame.ResultUi != null && frame.ResultReportId == frame.ReportId &&
                    frame.ResultBattleLevel == reportBattleLevel && object.ReferenceEquals(frame.ReportToken, reportToken))
                {
                    bool rejected = BattleLaunch.NativeEliteRejection(AttemptId, CommandSequence) &&
                        BattleSettlement.EliteNativePageFinished(AttemptId);
                    bool reportless = receipt != null && receipt.ReportId == 0 && frame.ReportId == 0;
                    bool processed = receipt == null && BattleSettlement.EliteNativePageFinished(AttemptId);
                    if (rejected || reportless || processed)
                    {
                        nativeRejected = rejected;
                        pageStateConfirmed = !rejected && reportless &&
                            (receipt.IsWin ? frame.FinishCount == finishCount + 1 && frame.FinishMax == finishMax : ReportlessLossMatches(frame));
                        pageOutcome = pageStateConfirmed ? receipt.IsWin ? "WIN" : "LOSS" : "UNKNOWN";
                        elitePageContinued = true; observedLossResult = frame.ResultUi; settledReport = frame.ReportId;
                        Status = "CONTINUED"; Outcome = "UNKNOWN";
                        Evidence = "elite_owned_result_receipt_unconfirmed";
                        return;
                    }
                }
                if (frame.AppliedLossReceipt != null && (finalToken == null || finalConsumed) &&
                    zeroObserved && ReportlessLossMatches(frame))
                {
                    observedLossResult = frame.ResultUi;
                    Status = "SETTLED"; Outcome = "LOSS"; Evidence = AppliedLossEvidence;
                    return;
                }
                // Some normal defeats have no stored report ID. The initial
                // server simulation and its matching normal result establish a
                // LOSS for retry policy, not an accepted settlement receipt.
                // Packet rejection/conflict checks above always take precedence.
                if ((receipt == null || !receipt.IsWin && receipt.ReportId == 0) && (finalToken == null || finalConsumed) && zeroObserved && ReportlessLossMatches(frame))
                {
                    observedLossResult = frame.ResultUi;
                    Status = campaignType == 4 ? "CONTINUED" : "SETTLED";
                    Outcome = campaignType == 4 ? "UNKNOWN" : "LOSS";
                    Evidence = campaignType == 4 ? "elite_owned_result_receipt_unconfirmed" : "anchored_current_battle_result_loss";
                    return;
                }
                // The user's elite continuation policy does not turn a local
                // result into an authoritative server win. Keep that absence
                // visible while admitting only this finished, owned result.
                if (campaignType == 4 && receipt == null && finalToken == null && zeroObserved &&
                    frame.ReportId != 0 && frame.ReportId != previousReport && frame.ResultReportId == frame.ReportId &&
                    frame.ResultUi != null && frame.ResultBattleLevel == frame.BattleResultLevel &&
                    !frame.FinalPending && frame.QueuedPackets.Length == 0)
                {
                    observedLossResult = frame.ResultUi; settledReport = frame.ReportId;
                    Status = "CONTINUED"; Outcome = "UNKNOWN";
                    Evidence = "elite_owned_result_receipt_unconfirmed"; return;
                }
                if (!zeroObserved || frame.ReportId == 0 || frame.ReportId == previousReport || (campaignType == 4 && receipt == null) || (!advanced && receipt == null) || (finalToken != null && !finalConsumed))
                { Unknown("settlement_receipt_missing_or_not_applied"); return; }
                if (frame.ResultReportId == 0)
                {
                    if (!resultWait) { resultWait = true; resultWaitTick = frame.SampleTick; }
                    if (unchecked((uint)(frame.SampleTick - resultWaitTick)) >= 10000U) Unknown("result_report_not_updated");
                    return;
                }
                if (frame.ResultReportId != frame.ReportId) { Unknown("settlement_result_report_mismatch"); return; }
                if (receipt != null && !receipt.IsWin && advanced) { Unknown("authoritative_packet_progress_conflict"); return; }
                settledReport = frame.ReportId; Status = "SETTLED";
                if (receipt != null)
                {
                    Outcome = receipt.IsWin ? "WIN" : "LOSS";
                    Evidence = finalToken == null ? "captured_authoritative_receipt_applied" : "captured_queued_authoritative_receipt_applied";
                }
                else { Outcome = "WIN"; Evidence = campaignType == 2 ? "anchored_normal_challenge_progress_applied" : "anchored_mainline_progress_applied"; }
            }
        }
        private bool Valid(CampaignPacket p)
        { return p != null && p.Result == 0 && p.Type == 0 && (p.CampaignId == CampaignId || !p.IsWin && p.CampaignId == 0) &&
            p.TroopId == TroopId && (p.ReportId != 0 && p.ReportId != previousReport || (!p.IsWin || campaignType == 4) && p.ReportId == 0); }
        private static bool Same(CampaignPacket a, CampaignPacket b)
        { return a.ReportId == b.ReportId && a.IsWin == b.IsWin && a.Result == b.Result && a.Type == b.Type && a.CampaignId == b.CampaignId && a.TroopId == b.TroopId; }
        private static CampaignPacket Copy(CampaignPacket p)
        { return new CampaignPacket { Result = p.Result, Type = p.Type, CampaignId = p.CampaignId, TroopId = p.TroopId, IsWin = p.IsWin, ReportId = p.ReportId }; }
        private bool ReportlessLossMatches(CampaignFrame current)
        {
            // LEFT_WIN=1, LEFT_LOSE=2; defender orientation reverses the side
            // whose result is displayed. Levels 0..4 are the game's loss range.
            bool initialLoss = reportDefender ? reportResultStatus == 1 : reportResultStatus == 2;
            return initialLoss && reportBattleLevel >= 0 && reportBattleLevel <= 4 &&
                object.ReferenceEquals(current.ReportToken, reportToken) && current.ReportResultStatus == reportResultStatus &&
                current.ShowDefenderResult == reportDefender && current.BattleResultLevel == reportBattleLevel &&
                current.ResultVisible && current.ResultUi != null && current.ResultBattleLevel == reportBattleLevel &&
                current.ReportId == 0 && current.ResultReportId == 0 && current.BattleVisible &&
                current.QueueObserved && current.QueueError == null && current.QueuedPackets != null && current.QueuedPackets.Length == 0 &&
                object.ReferenceEquals(current.QueueClient, queueClient) && !current.RefreshPending && !current.FinalPending &&
                object.ReferenceEquals(current.Target, target) && object.ReferenceEquals(current.TargetData, targetData) &&
                current.CampaignId == CampaignId && current.TroopId == TroopId && current.CampaignType == campaignType &&
                current.FinishCount == finishCount && current.FinishMax == finishMax && !current.Killed && !current.CampaignFinished &&
                !current.Debug && !current.Gm && !current.Replay && !current.Surrendered &&
                !current.Waiting && !current.UseEnd && !current.Fighting && current.Packet == null;
        }
        internal bool AppliedTo(CampaignFrame current)
        {
            return Finished && current != null &&
                object.ReferenceEquals(current.Scene, scene) && object.ReferenceEquals(current.Campaign, campaign) &&
                object.ReferenceEquals(current.CampaignData, campaignData) && object.ReferenceEquals(current.Battle, battle) &&
                object.ReferenceEquals(current.Character, character) && current.CharacterId == characterId &&
                !current.Replay && !current.Surrendered && !current.Waiting && !current.UseEnd && !current.Fighting && current.Packet == null &&
                ((settledReport != 0 && current.ReportId == settledReport) ||
                (campaignType == 4 && elitePageContinued && current.ResultUi != null &&
                    object.ReferenceEquals(current.ResultUi, observedLossResult) && current.ResultReportId == current.ReportId &&
                    current.QueueObserved && !current.FinalPending && current.QueuedPackets != null && current.QueuedPackets.Length == 0) ||
                ((Outcome == "LOSS" && (Evidence == "anchored_current_battle_result_loss" ||
                    Evidence == AppliedLossEvidence && receipt != null && current.AppliedLossReceipt != null && Same(receipt, current.AppliedLossReceipt)) ||
                    campaignType == 4 && Status == "CONTINUED" && Evidence == "elite_owned_result_receipt_unconfirmed") && observedLossResult != null &&
                object.ReferenceEquals(current.ResultUi, observedLossResult) && ReportlessLossMatches(current)));
        }
        internal bool SettledCampaignContext(CampaignFrame current)
        {
            return Finished && current != null && current.CampaignId == CampaignId && current.CampaignType == campaignType &&
                object.ReferenceEquals(current.Scene, scene) && object.ReferenceEquals(current.Campaign, campaign) &&
                object.ReferenceEquals(current.CampaignData, campaignData) && object.ReferenceEquals(current.Character, character) && current.CharacterId == characterId;
        }
        // Read-only debug of anchors actually captured by this helper. A replacement
        // helper cannot reconstruct the previous helper's Begin/Observe history.
        internal string DebugJson(CampaignFrame current)
        {
            bool anchored = Status != "NONE" && campaign != null && character != null;
            return "{\"status\":" + Command.Quote(Status) + ",\"anchors_available\":" + (anchored ? "true" : "false") +
                ",\"zero_observed\":" + (anchored ? (zeroObserved ? "true" : "false") : "null") +
                ",\"battle_observed\":" + (anchored ? (battleObserved ? "true" : "false") : "null") +
                ",\"receipt_captured\":" + (anchored ? (receipt != null ? "true" : "false") : "null") +
                ",\"campaign_match\":" + DebugMatch(anchored && current != null, campaign, current == null ? null : current.Campaign) +
                ",\"battle_match\":" + DebugMatch(anchored && current != null && battle != null, battle, current == null ? null : current.Battle) +
                ",\"character_match\":" + DebugMatch(anchored && current != null, character, current == null ? null : current.Character) +
                ",\"character_id_match\":" + (anchored && current != null ? (characterId == current.CharacterId ? "true" : "false") : "null") +
                ",\"report_token_match\":" + DebugMatch(anchored && current != null && reportToken != null, reportToken, current == null ? null : current.ReportToken) + "}";
        }
        private static string DebugMatch(bool available, object expected, object current)
        { return available ? (object.ReferenceEquals(expected, current) ? "true" : "false") : "null"; }
        internal string Json()
        {
            return "{\"attempt_id\":" + Command.Quote(AttemptId) + ",\"character_id\":" + Command.Quote(characterId == null ? "" : characterId) + ",\"command_seq\":" + CommandSequence +
                ",\"campaign_id\":" + CampaignId + ",\"troop_id\":" + TroopId + ",\"status\":" + Command.Quote(Status) +
                ",\"report_id\":" + Command.Quote((settledReport != 0 ? settledReport : receipt == null ? 0 : receipt.ReportId).ToString(CultureInfo.InvariantCulture)) +
                ",\"outcome\":" + Command.Quote(Outcome) + ",\"evidence\":" + Command.Quote(Evidence) +
                ",\"native_rejection\":" + (nativeRejected ? "true" : "false") +
                ",\"page_state_confirmed\":" + (pageStateConfirmed ? "true" : "false") +
                ",\"page_outcome\":" + Command.Quote(pageOutcome) +
                ",\"processed_without_reply\":" + (Status == "CONTINUED" && !nativeRejected && !pageStateConfirmed && receipt == null ? "true" : "false") + "}";
        }
        internal string BusinessJson()
        {
            return "{\"character_id\":" + Command.Quote(characterId == null ? "" : characterId) + ",\"mutation_attempted\":" + (MutationAttempted ? "true" : "false") + ",\"pending_attempt\":" + (Pending ? "true" : "false") +
                ",\"resource_rollback\":false,\"attempt\":" + Json() + "}";
        }
    }
}
