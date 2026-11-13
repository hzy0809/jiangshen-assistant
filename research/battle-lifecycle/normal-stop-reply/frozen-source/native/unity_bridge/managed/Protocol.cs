using System;
using System.Globalization;

namespace JiangShen.AssistantRuntime
{
    // Numeric observation only; equality is independent of UI success and cannot
    // establish resource-consumption or network causality.
    internal sealed class ResourceObservation
    {
        private readonly string[] names;
        private readonly long[] before;
        private readonly bool[] equalAtEverySample;
        internal int Samples;
        internal ResourceObservation(string[] fieldNames, long[] baseline)
        {
            if (fieldNames == null || baseline == null || fieldNames.Length != baseline.Length) throw new ArgumentException("resource_schema");
            names = (string[])fieldNames.Clone(); before = (long[])baseline.Clone();
            equalAtEverySample = new bool[baseline.Length];
            for (int i = 0; i < baseline.Length; i++) equalAtEverySample[i] = true;
            Samples = 1;
        }
        internal void Observe(long[] current)
        {
            Require(current);
            for (int i = 0; i < before.Length; i++) if (before[i] != current[i]) equalAtEverySample[i] = false;
            Samples++;
        }
        internal bool FinalEqual(long[] current)
        {
            Require(current);
            for (int i = 0; i < before.Length; i++) if (before[i] != current[i]) return false;
            return true;
        }
        internal bool EverySampleEqual()
        {
            for (int i = 0; i < before.Length; i++) if (!equalAtEverySample[i]) return false;
            return true;
        }
        internal string Json(long[] current)
        {
            Require(current);
            System.Text.StringBuilder b = new System.Text.StringBuilder("{");
            for (int i = 0; i < before.Length; i++)
            {
                if (i != 0) b.Append(',');
                b.Append(Command.Quote(names[i])); b.Append(":{\"before\":"); b.Append(before[i].ToString(CultureInfo.InvariantCulture));
                b.Append(",\"after\":"); b.Append(current[i].ToString(CultureInfo.InvariantCulture));
                b.Append(",\"final_equal\":"); b.Append(before[i] == current[i] ? "true" : "false");
                b.Append(",\"every_sample_equal\":"); b.Append(equalAtEverySample[i] ? "true" : "false");
                b.Append('}');
            }
            b.Append('}'); return b.ToString();
        }
        private void Require(long[] current)
        {
            if (current == null || current.Length != before.Length) throw new ArgumentException("resource_schema");
        }
    }

    // Deliberately no JSON dependency and no free-form reflection/RPC parameters.
    internal sealed class Command
    {
        internal int Sequence;
        internal string Name;
        internal string BattleEpoch = "";
        internal string CampaignAction = "", AttemptId = "";
        internal int ExpectedRevision, CampaignId, TroopId;
        internal string DevelopmentAction = "", OperationId = "", DevelopmentMode = "";
        internal int TargetId, DesiredLevel, MaxRequests;
        internal long MaxMoney, MaxStone, MaxFood, MaxPractice;
        internal string GuildAction = "";
        internal ulong GuildCharacterId, GuildId;
        internal int GuildTaskId, DonationType, MaxPremium;
        internal string DailyRewardAction = "";
        internal ulong DailyRewardCharacterId;
        internal int DailyRewardCategoryId;
        internal string DailyTimedAction = "";
        internal ulong DailyTimedCharacterId;
        internal int DailyTimedTaskId;
        internal string DailyStoneAction = "";
        internal ulong DailyStoneCharacterId;
        internal int DailyStoneTaskId;
        internal string DailyMingshiAction = "";
        internal ulong DailyMingshiCharacterId;
        internal int DailyMingshiTaskId;
        internal string DailyArenaAction = "";
        internal ulong DailyArenaCharacterId;
        internal string DailySoulAction = "";
        internal ulong DailySoulCharacterId;
        internal int DailySoulTaskId;
        internal int DailySoulMode, DailySoulFreeOnly;
        internal string DailyRefineAction = "";
        internal ulong DailyRefineCharacterId;
        internal int DailyRefineTaskId;
        internal string DailyWoodAction = "";
        internal ulong DailyWoodCharacterId;
        internal int DailyWoodTaskId;
        internal string DailyScriptAction = "";
        internal ulong DailyScriptCharacterId;
        internal int DailyScriptTaskId;
        internal string DailyPopupAction = "", DailyPopupType = "";
        internal ulong DailyPopupCharacterId;
        internal int DailyPopupInstance, DailyPopupGeneration;

        internal static bool ValidNonce(string nonce)
        {
            if (nonce == null || nonce.Length != 32) return false;
            for (int i = 0; i < nonce.Length; i++)
                if (!(nonce[i] >= '0' && nonce[i] <= '9') && !(nonce[i] >= 'a' && nonce[i] <= 'f')) return false;
            return true;
        }

        internal static Command Parse(string text, string nonce, int previous)
        {
            if (!ValidNonce(nonce) || text == null || text.Length > 768) return null;
            text = text.Replace("\r\n", "\n");
            if (text.EndsWith("\n")) text = text.Substring(0, text.Length - 1);
            string[] lines = text.Split(new char[] { '\n' });
            string battleEpoch = "";
            if (lines.Length > 4 && lines[4].StartsWith("battle_epoch="))
            {
                battleEpoch = lines[4].Substring(13);
                if (!ValidNonce(battleEpoch)) return null;
                string[] withoutEpoch = new string[lines.Length - 1];
                Array.Copy(lines, 0, withoutEpoch, 0, 4);
                Array.Copy(lines, 5, withoutEpoch, 4, lines.Length - 5);
                lines = withoutEpoch;
            }
            // Soul commands contain the five timed fields plus mode/free-only:
            // four envelope lines and seven command lines must reach their parser.
            if ((lines.Length != 4 && lines.Length != 8 && lines.Length != 9 && lines.Length != 11 && lines.Length != 12 && lines.Length != 14 && lines.Length != 15) || lines[0] != "version=3" || lines[1] != "nonce=" + nonce) return null;
            const string prefix = "sequence=";
            if (!lines[2].StartsWith(prefix)) return null;
            string digits = lines[2].Substring(prefix.Length);
            if (digits.Length == 0 || digits.Length > 9 || digits[0] == '0') return null;
            for (int i = 0; i < digits.Length; i++) if (digits[i] < '0' || digits[i] > '9') return null;
            int sequence = Int32.Parse(digits, CultureInfo.InvariantCulture);
            if (sequence <= previous || !lines[3].StartsWith("command=")) return null;
            string name = lines[3].Substring(8);
            if (name != "heartbeat" && name != "diagnose" && name != "snapshot" && name != "disable-restore" && name != "stop" && name != "campaign-observe" && name != "campaign-command" && name != "challenge-observe" && name != "challenge-command" && name != "development-observe" && name != "development-command" && name != "player-state-refresh" && name != "daily-state-refresh" && name != "daily-observe" && name != "daily-guild-observe" && name != "daily-guild-command" && name != "daily-reward-observe" && name != "daily-reward-command" && name != "daily-timed-observe" && name != "daily-timed-command" && name != "daily-stone-observe" && name != "daily-stone-command" && name != "daily-mingshi-observe" && name != "daily-mingshi-command" && name != "daily-arena-observe" && name != "daily-arena-command" && name != "daily-soul-observe" && name != "daily-soul-command" && name != "daily-refine-observe" && name != "daily-refine-command" && name != "daily-wood-observe" && name != "daily-wood-command" && name != "daily-script" && name != "daily-popup-observe" && name != "daily-popup-command") return null;
            Command result = new Command();
            result.Sequence = sequence;
            result.Name = name;
            result.BattleEpoch = battleEpoch;
            if (name == "campaign-command" || name == "challenge-command")
            {
                if (lines.Length != 9 || !lines[4].StartsWith("action=") || !lines[8].StartsWith("attempt_id=")) return null;
                result.CampaignAction = lines[4].Substring(7);
                if (!(name == "challenge-command" ? ChallengeActionAllowed(result.CampaignAction) : CampaignActionAllowed(result.CampaignAction)) || !Number(lines[5], "expected_revision=", 1, 999999999, out result.ExpectedRevision) ||
                    !Number(lines[6], "campaign_id=", 0, 65535, out result.CampaignId) || !Number(lines[7], "troop_id=", 0, 255, out result.TroopId)) return null;
                result.AttemptId = lines[8].Substring(11);
                if (result.CampaignAction == "start" ? !ValidNonce(result.AttemptId) : result.AttemptId != "") return null;
                bool teaching = result.CampaignAction == "general_deploy" || result.CampaignAction == "formation_confirm";
                if (teaching ? result.CampaignId != 0 || result.TroopId != 0 : result.CampaignAction != "enter" && (result.CampaignId == 0 || (result.CampaignAction != "enter_challenge" && result.CampaignAction != "next_chapter" && result.CampaignAction != "continue" && result.TroopId == 0))) return null;
            }
            else if (name == "daily-reward-command")
            {
                if (lines.Length != 9 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !Number(lines[5], "expected_revision=", 1, 999999999, out result.ExpectedRevision) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyRewardCharacterId) ||
                    !Number(lines[7], "category_id=", 2, 6, out result.DailyRewardCategoryId) ||
                    !lines[8].StartsWith("operation_id=", StringComparison.Ordinal)) return null;
                result.OperationId = lines[8].Substring(13);
                result.DailyRewardAction = lines[4].Substring(7);
                string action = result.DailyRewardAction;
                if (!ValidNonce(result.OperationId) || action != "open" && action != "claim" && action != "reward_confirm" && action != "close") return null;
            }
            else if (name == "daily-timed-command")
            {
                if (lines.Length != 9 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyTimedCharacterId) ||
                    !Number(lines[7], "task_id=", 12, 13, out result.DailyTimedTaskId) ||
                    !Number(lines[8], "expected_revision=", 1, 999999999, out result.ExpectedRevision)) return null;
                result.OperationId = lines[5].Substring(13);
                result.DailyTimedAction = lines[4].Substring(7);
                if (!ValidNonce(result.OperationId) || result.DailyTimedAction != "open" && result.DailyTimedAction != "participate" &&
                    !(result.DailyTimedAction == "close" && result.DailyTimedTaskId == 12)) return null;
            }
            else if (name == "daily-stone-command")
            {
                if (lines.Length != 9 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyStoneCharacterId) ||
                    !Number(lines[7], "task_id=", 16, 16, out result.DailyStoneTaskId) ||
                    !Number(lines[8], "expected_revision=", 1, 999999999, out result.ExpectedRevision)) return null;
                result.OperationId = lines[5].Substring(13);
                result.DailyStoneAction = lines[4].Substring(7);
                if (!ValidNonce(result.OperationId) || result.DailyStoneAction != "open" && result.DailyStoneAction != "dig" &&
                    result.DailyStoneAction != "close") return null;
            }
            else if (name == "daily-mingshi-command")
            {
                if (lines.Length != 9 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyMingshiCharacterId) ||
                    !Number(lines[7], "task_id=", 7, 7, out result.DailyMingshiTaskId) ||
                    !Number(lines[8], "expected_revision=", 1, 999999999, out result.ExpectedRevision)) return null;
                result.OperationId = lines[5].Substring(13);
                result.DailyMingshiAction = lines[4].Substring(7);
                string action = result.DailyMingshiAction;
                if (!ValidNonce(result.OperationId) || action != "open" && action != "harvest" && action != "close" && action != "cleanup") return null;
            }
            else if (name == "daily-soul-command")
            {
                if (lines.Length != 11 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailySoulCharacterId) ||
                    !Number(lines[7], "task_id=", 5, 5, out result.DailySoulTaskId) ||
                    !Number(lines[8], "expected_revision=", 1, 999999999, out result.ExpectedRevision) ||
                    !Number(lines[9], "soul_mode=", 1, 2, out result.DailySoulMode) ||
                    !Number(lines[10], "free_only=", 0, 1, out result.DailySoulFreeOnly)) return null;
                result.OperationId = lines[5].Substring(13);
                result.DailySoulAction = lines[4].Substring(7);
                string action = result.DailySoulAction;
                if (!ValidNonce(result.OperationId) || action != "open" && action != "start" && action != "grow" && action != "harvest" && action != "close" && action != "next") return null;
            }
            else if (name == "daily-refine-command")
            {
                if (lines.Length != 9 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyRefineCharacterId) ||
                    !Number(lines[7], "task_id=", 21, 21, out result.DailyRefineTaskId) ||
                    !Number(lines[8], "expected_revision=", 1, 999999999, out result.ExpectedRevision)) return null;
                result.OperationId = lines[5].Substring(13);
                result.DailyRefineAction = lines[4].Substring(7);
                string action = result.DailyRefineAction;
                if (!ValidNonce(result.OperationId) || action != "open" && action != "experience" && action != "refine" && action != "close") return null;
            }
            else if (name == "daily-popup-command")
            {
                if (lines.Length != 11 || lines[4] != "action=confirm" ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyPopupCharacterId) ||
                    lines[7] != "window_type=BattleResultUI" ||
                    !lines[8].StartsWith("instance_id=", StringComparison.Ordinal) ||
                    !Int32.TryParse(lines[8].Substring(12), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result.DailyPopupInstance) || result.DailyPopupInstance == 0 ||
                    !Number(lines[9], "appearance_generation=", 1, 999999999, out result.DailyPopupGeneration) ||
                    !Number(lines[10], "expected_revision=", 1, 999999999, out result.ExpectedRevision)) return null;
                result.OperationId = lines[5].Substring(13); result.DailyPopupAction = "confirm"; result.DailyPopupType = "BattleResultUI";
                if (!ValidNonce(result.OperationId)) return null;
            }
            else if (name == "daily-script")
            {
                if (lines.Length != 9 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyScriptCharacterId) ||
                    !Number(lines[7], "task_id=", 1, 303, out result.DailyScriptTaskId) ||
                    !Number(lines[8], "expected_revision=", 1, 999999999, out result.ExpectedRevision)) return null;
                result.OperationId = lines[5].Substring(13);
                result.DailyScriptAction = lines[4].Substring(7);
                if (result.DailyScriptTaskId > 24 && result.DailyScriptTaskId != 109 &&
                    result.DailyScriptTaskId != 201 && result.DailyScriptTaskId != 202 && result.DailyScriptTaskId != 203 &&
                    result.DailyScriptTaskId != 301 && result.DailyScriptTaskId != 302 && result.DailyScriptTaskId != 303) return null;
                if (!ValidNonce(result.OperationId) || result.DailyScriptAction.Length == 0 || result.DailyScriptAction.Length > 32) return null;
                foreach (char c in result.DailyScriptAction) if (!(c >= 'a' && c <= 'z') && c != '_') return null;
                string scriptAction = result.DailyScriptAction;
                if (result.DailyScriptTaskId >= 201 && result.DailyScriptTaskId <= 203 &&
                    scriptAction != "observe" && scriptAction != "open" && scriptAction != "claim" &&
                    scriptAction != "close_reward" && scriptAction != "close_message" && scriptAction != "close") return null;
                if (result.DailyScriptTaskId == 301 && scriptAction != "observe" && scriptAction != "open" &&
                    scriptAction != "resume" && scriptAction != "claim_signin" && scriptAction != "reward_close" &&
                    scriptAction != "close") return null;
                if (result.DailyScriptTaskId == 302 && scriptAction != "observe" && scriptAction != "open" &&
                    scriptAction != "resume" && scriptAction != "claim_salary" && scriptAction != "claim_arena" &&
                    scriptAction != "claim_all_salary" && scriptAction != "reward_close" && scriptAction != "close") return null;
                if (result.DailyScriptTaskId == 303 && scriptAction != "observe" && scriptAction != "open" &&
                    scriptAction != "resume" && scriptAction != "copy_free" && scriptAction != "close") return null;
            }
            else if (name == "daily-wood-command")
            {
                if (lines.Length != 9 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyWoodCharacterId) ||
                    !Number(lines[7], "task_id=", 22, 22, out result.DailyWoodTaskId) ||
                    !Number(lines[8], "expected_revision=", 1, 999999999, out result.ExpectedRevision)) return null;
                result.OperationId = lines[5].Substring(13);
                string action = result.DailyWoodAction = lines[4].Substring(7);
                if (!ValidNonce(result.OperationId) || action != "open" && action != "fast" && action != "close_result" && action != "close_panel" &&
                    action != "claim_reward" && action != "close_reward" && action != "dismiss_prompt") return null;
            }
            else if (name == "daily-arena-command")
            {
                if (lines.Length != 8 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !lines[5].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !IdentityNumber(lines[6], "character_id=", out result.DailyArenaCharacterId) ||
                    !Number(lines[7], "expected_revision=", 1, 999999999, out result.ExpectedRevision)) return null;
                result.OperationId = lines[5].Substring(13);
                result.DailyArenaAction = lines[4].Substring(7);
                string action = result.DailyArenaAction;
                if (!ValidNonce(result.OperationId) || action != "open" && action != "enter" && action != "open_chest" &&
                    action != "claim" && action != "reward_confirm" && action != "close" && action != "rearm") return null;
            }
            else if (name == "daily-guild-command")
            {
                if (lines.Length != 12 || !lines[4].StartsWith("action=", StringComparison.Ordinal) ||
                    !Number(lines[5], "expected_revision=", 1, 999999999, out result.ExpectedRevision) ||
                    !IdentityNumber(lines[6], "character_id=", out result.GuildCharacterId) || !IdentityNumber(lines[7], "guild_id=", out result.GuildId) ||
                    !Number(lines[8], "task_id=", 18, 20, out result.GuildTaskId) || !lines[9].StartsWith("operation_id=", StringComparison.Ordinal) ||
                    !Number(lines[10], "donation_type=", 0, 3, out result.DonationType) || !Number(lines[11], "max_premium=", 0, 80, out result.MaxPremium)) return null;
                result.OperationId = lines[9].Substring(13); result.GuildAction = lines[4].Substring(7);
                if (!ValidNonce(result.OperationId) || !GuildActionAllowed(result)) return null;
            }
            else if (name == "development-command" || name == "player-state-refresh" || name == "daily-state-refresh" || name == "development-observe" && lines.Length != 4)
            {
                bool star = lines.Length > 4 && lines[4].StartsWith("action=star_", StringComparison.Ordinal);
                if (lines.Length != (star ? 15 : 14) || !lines[4].StartsWith("action=") || !lines[6].StartsWith("operation_id=") || !lines[13].StartsWith("mode=")) return null;
                result.DevelopmentAction = lines[4].Substring(7); result.OperationId = lines[6].Substring(13); result.DevelopmentMode = lines[13].Substring(5);
                bool refresh = name == "player-state-refresh" || name == "daily-state-refresh";
                if (!ValidNonce(result.OperationId) || (refresh ? result.DevelopmentAction != (name == "daily-state-refresh" ? "daily_state_refresh" : "player_state_refresh") : !DevelopmentActionAllowed(result.DevelopmentAction, name == "development-observe")) ||
                    !Number(lines[5], "expected_revision=", 1, 999999999, out result.ExpectedRevision) ||
                    !Number(lines[7], "target_id=", 0, 32767, out result.TargetId) || !Number(lines[8], "desired_level=", 0, 255, out result.DesiredLevel) ||
                    !Number(lines[9], "max_requests=", refresh ? 0 : 1, refresh ? 0 : result.DevelopmentAction.StartsWith("tribute_", StringComparison.Ordinal) ? Int32.MaxValue : star ? 196 : 100, out result.MaxRequests) || !Budget(lines[10], "max_money=", out result.MaxMoney) ||
                    !Budget(lines[11], "max_stone=", out result.MaxStone) || !Budget(lines[12], "max_food=", out result.MaxFood) ||
                    (star && !Budget(lines[14], "max_practice=", out result.MaxPractice)) || (!star && result.DevelopmentMode == "practice") ||
                    (result.DevelopmentMode != "practice" && result.DevelopmentMode != "" && result.DevelopmentMode != "enchant" && result.DevelopmentMode != "evolve" && result.DevelopmentMode != "fruit")) return null;
                if (refresh && (result.TargetId != 0 || result.DesiredLevel != 0 || result.MaxMoney != 0 || result.MaxStone != 0 || result.MaxFood != 0 || result.DevelopmentMode != "")) return null;
            }
            else if (lines.Length != 4) return null;
            return result;
        }

        internal static bool CampaignActionAllowed(string action)
        { return action == "enter" || action == "select" || action == "prepare" || action == "start" || action == "continue" || action == "ack_result" || action == "next_chapter" || action == "general_deploy" || action == "formation_confirm"; }
        internal static bool ChallengeActionAllowed(string action)
        { return action == "enter_challenge" || action != "general_deploy" && action != "formation_confirm" && CampaignActionAllowed(action); }
        internal static bool GuildActionAllowed(Command command)
        {
            int task = command.GuildTaskId, type = command.DonationType;
            if (task == 18 ? type != 0 : task != 20 || type < 1 || type > 3) return false;
            if (command.MaxPremium != (type == 3 ? 80 : 0)) return false;
            string action = command.GuildAction;
            return action == "open" || action == "reward_confirm" || action == "close" ||
                (task == 18 ? action == "lottery_panel" || action == "lottery" : action == "donate_panel" || action == "donate" || type == 3 && action == "premium_confirm");
        }
        internal static bool DevelopmentActionAllowed(string action, bool observe)
        {
            if (observe) return action == "star_observe" || action == "growth_observe" || action == "equipment_observe" || action == "spirit_observe" || action == "tribute_observe";
            return action == "star_open" || action == "star_activate" || action == "star_reward_confirm" || action == "star_close" || action == "growth_open" || action == "growth_check" || action == "growth_claim" || action == "growth_reward_confirm" || action == "growth_close" ||
                action == "growth_guide_open" || action == "growth_soul_recruit" || action == "growth_soul_open" || action == "growth_soul_start" || action == "growth_soul_grow" || action == "growth_soul_harvest" || action == "growth_artifact_open" || action == "growth_artifact_wear" || action == "growth_requirement_close" ||
                action == "growth_reputation_open" || action == "growth_artifact_grant" || action == "growth_artifact_view" ||
                action == "growth_recruit_open" || action == "growth_recruit_right" || action == "growth_recruit_left" || action == "growth_recruit_choose" || action == "growth_recruit_confirm" || action == "growth_recruit_deploy" || action == "growth_recruit_save" ||
                action == "equipment_open" || action == "reinforce_open" || action == "enchant_tab" || action == "equipment_enchant" || action == "equipment_close" ||
                action == "spirit_open" || action == "spirit_train" || action == "spirit_rankup" || action == "spirit_rankup_confirm" || action == "spirit_close" ||
                action == "tribute_open" || action == "tribute_panel_open" || action == "tribute_train" || action == "tribute_rankup" || action == "tribute_rankup_confirm" || action == "tribute_close" || action == "tribute_parent_close";
        }
        private static bool Budget(string line, string prefix, out long value)
        {
            value = 0; if (!line.StartsWith(prefix)) return false;
            string digits = line.Substring(prefix.Length);
            if (digits.Length == 0 || digits.Length > 10 || digits.Length > 1 && digits[0] == '0') return false;
            foreach (char c in digits) if (c < '0' || c > '9') return false;
            return Int64.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= 2147483647L;
        }
        private static bool IdentityNumber(string line, string prefix, out ulong value)
        {
            value = 0;
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string digits = line.Substring(prefix.Length);
            if (digits.Length == 0 || digits.Length > 20 || digits[0] == '0') return false;
            foreach (char digit in digits) if (digit < '0' || digit > '9') return false;
            return UInt64.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        private static bool Number(string line, string prefix, int minimum, int maximum, out int number)
        {
            number = 0; if (!line.StartsWith(prefix)) return false;
            string digits = line.Substring(prefix.Length);
            if (digits.Length == 0 || digits.Length > (maximum == Int32.MaxValue ? 10 : 9) || (digits.Length > 1 && digits[0] == '0')) return false;
            for (int i = 0; i < digits.Length; i++) if (digits[i] < '0' || digits[i] > '9') return false;
            return Int32.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= minimum && number <= maximum;
        }

        internal static string Quote(string value)
        {
            if (value == null) return "null";
            System.Text.StringBuilder b = new System.Text.StringBuilder("\"");
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\') { b.Append('\\'); b.Append(c); }
                else if (c < 32 || c > 126) b.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            b.Append('"');
            return b.ToString();
        }
    }
}
