"""Detached workflow facts and owner-scoped subscriptions.

Notifications are wake-up hints; the queued events are the source of truth.
There is no bounded queue that can silently evict a settlement receipt.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass
from enum import Enum
import copy
import json
import re
import threading
from typing import Callable

from .account_profiles import DAILY_CATALOG
from .campaign import valid_settlement_evidence


class WorkflowEventKind(str, Enum):
    STATE_CHANGED = "state_changed"
    BATTLE_SETTLED = "battle_settled"
    REWARD_POPUP_APPEARED = "reward_popup_appeared"
    STORY_CHANGED = "story_changed"
    GROWTH_TASK_CHANGED = "growth_task_changed"
    EQUIPMENT_CHANGED = "equipment_changed"
    TRIBUTE_CHANGED = "tribute_changed"
    STAR_CHANGED = "star_changed"
    CONNECTION_CHANGED = "connection_changed"


@dataclass(frozen=True)
class WorkflowEvent:
    kind: WorkflowEventKind
    context_key: str
    run_id: str
    generation: int
    snapshot: dict
    sequence: int = 0


def _fields(value: dict, names: tuple[str, ...]) -> dict:
    """Only named scalar facts belong in the user log, never raw state."""
    return {name: value[name] for name in names if name in value
            and (value[name] is None or type(value[name]) in (str, int, float, bool))}


def workflow_log_message(event: WorkflowEvent) -> str:
    """Compact publisher record retained in the file log."""
    value = event.snapshot
    campaign = value.get("campaign") or {}
    development = value.get("development") or {}
    state = value.get("state") or {}
    connection = value.get("connection") or {}
    try:
        identity = json.loads(event.context_key)
    except (ValueError, TypeError):
        identity = {}
    record = {"event": event.kind.value, "seq": event.sequence, "run_id": event.run_id,
              "generation": event.generation,
              "context": _fields(identity, ("pid", "creation", "nonce", "character_id")),
              "revision": campaign.get("revision"), "scope": value.get('workflow_scope', 'campaign'),
              "development_revision": development.get("revision"),
              "campaign": _fields(campaign, ("campaign_id", "troop_id", "phase", "gate", "reason", "result_kind", "result_navigation", "mode", "campaign_type")),
              "flow": _fields(campaign.get("flow") or {}, ("status", "action", "reason")),
              "runtime": _fields(state, ("lifecycle", "gate", "reason")),
              "connection": _fields(connection, ("state", "message", "error", "stop_reason")),
              "control": _fields(value, ("stopping", "admission_open", "control_reason", "error"))}
    if value.get('workflow_scope') == 'challenge':
        record['challenge_resources'] = _fields(campaign.get('challenge_resources') or {},
            ('gold', 'bind_gold', 'stamina', 'hero_tries'))
        record['normal_candidates'] = len(campaign.get('challenge_candidates') or [])
        record['entry'] = _fields(campaign.get('entry') or {}, ('status', 'command_seq', 'campaign_id', 'reason'))
    if event.kind == WorkflowEventKind.BATTLE_SETTLED:
        record["battle"] = _fields(campaign.get("attempt") or {},
            ("attempt_id", "report_id", "status", "outcome", "evidence", "campaign_id", "troop_id"))
    if event.kind == WorkflowEventKind.REWARD_POPUP_APPEARED:
        popup = state.get("daily_popup") or {}
        record["popup"] = _fields(popup.get("last_event") or {},
            ("window_type", "instance_id", "appearance_generation", "event_sequence"))
    if event.kind == WorkflowEventKind.STORY_CHANGED:
        record["story"] = {key: _fields(campaign.get(key) or {},
            ("active", "step", "step_key", "action", "action_key", "gate", "reason"))
            for key in ("tutorial", "general_tutorial") if isinstance(campaign.get(key), dict)}
    if event.kind == WorkflowEventKind.GROWTH_TASK_CHANGED:
        growth = development.get("growth") or {}
        record["growth"] = _fields(growth, ("gate", "reason", "step", "selected_quest_id",
            "ui_selected_quest_id", "window_visible", "window_enabled", "available_action"))
        # Completion flags are enough to explain selection and reward changes;
        # templates, descriptions and full object graphs stay out of the log.
        record["growth"]["tasks"] = [_fields(item,
            ("quest_id", "isfinish", "isgot", "condition_met", "dispatch", "action_reason"))
            for item in growth.get("tasks", []) if isinstance(item, dict)]
        record["growth"]["operation"] = _fields(growth.get("operation") or {},
            ("operation_id", "sequence", "action", "status", "reason", "target_id", "requests"))
    if event.kind == WorkflowEventKind.EQUIPMENT_CHANGED:
        equipment = development.get("equipment") or {}
        record["equipment"] = _fields(equipment,
            ("gate", "reason", "target_id", "desired_level", "mode", "selected_id",
             "selected_equipment_id", "available_action", "window_visible"))
        record["equipment"]["candidates"] = [_fields(item,
            ("equipment_id", "owner_id", "slot", "level", "main_id", "in_current_formation"))
            for item in equipment.get("candidates", []) if isinstance(item, dict)]
        record["equipment"]["operation"] = _fields(equipment.get("operation") or {},
            ("operation_id", "sequence", "action", "status", "reason", "target_id", "desired_level", "requests"))
    if event.kind == WorkflowEventKind.TRIBUTE_CHANGED:
        tribute = development.get("tribute") or {}
        record["tribute"] = _fields(tribute,
            ("gate", "reason", "done", "level", "desired_level", "experience", "food", "spent_food", "tribute_requests", "available_action"))
        record["tribute"]["operation"] = _fields(tribute.get("operation") or {},
            ("operation_id", "sequence", "action", "status", "reason", "requests",
             "receipt_opcode", "receipt_result", "receipt_food", "receipt_gold", "server"))
    if event.kind == WorkflowEventKind.STAR_CHANGED:
        star = development.get("star") or {}
        record["star"] = _fields(star,
            ("gate", "reason", "done", "level", "practice", "gold", "next_cost", "spent_practice", "available_action"))
        record["star"]["operation"] = _fields(star.get("operation") or {},
            ("operation_id", "sequence", "action", "status", "reason", "requests",
             "receipt_opcode", "receipt_result", "receipt_level", "receipt_practice", "server"))
    if connection.get("first_failure"):
        record["first_failure"] = _fields(connection["first_failure"],
            ("stage", "exception_type", "message", "observed_at", "winerror", "errno", "command_seq", "command_name"))
    return "工作流事件 " + json.dumps(record, ensure_ascii=False, separators=(",", ":"))


_REASONS = {
    'soul_color_locked': '该颜色的免费聚魂资格尚未开放。',
    'soul_daily_color_done': '该颜色今日聚魂已完成。',
    'soul_free_exhausted': '免费聚魂次数已用完。',
    'soul_color_quote_unavailable': '聚魂资格或报价暂不可读，本次未开始。',
    'soul_coin_start_quote_or_balance_unavailable': '聚魂钱币余额不足或开局报价不可读，本次未开始。',
    'soul_evidence_write_failed': '聚魂记录无法保存，已停止继续花费。',
    'soul_prior_operation_unresolved': '上次聚魂缺少结果确认，请在游戏内核对聚魂结果。',
    'soul_expected_identity_or_progress_changed': '聚魂角色、日期或进度已变化，本次已停止。',
    'soul_plan_partial': '本轮聚魂仅完成部分请求。',
    'soul_close_outcome_unknown': '聚魂页面缺少关闭确认，请检查游戏当前界面。',
    'soul_close_timeout': '等待聚魂页面关闭超时，请检查游戏当前界面。',
    'soul_command_outcome_unknown': '聚魂操作缺少结果确认，请在游戏内核对后再继续。',
    'soul_readback_timeout': '等待聚魂结果超时，请在游戏内核对后再继续。',
    'quiz_closed': '答题活动当前未开放。',
    'spirit_max_level': '灵兽已满级。',
    'grave_progression_limit': '本轮地宫探宝已结束。',
    'category_hidden': '该日常分类当前未显示。',
    'daily_tribute_progress_unconfirmed': '上贡后未确认日常计数增加，请在游戏内核对上贡进度。',
    'grave_battle_lost': '地宫挑战失败。',
    'grave_soldiers_exhausted': '地宫兵力不足。',
    'grave_no_remaining_attempts': '地宫挑战次数已用完。',
    'wood_reward_pending': '木人试炼奖励等待领取。',
    'wood_reward_already_pending': '木人试炼奖励等待领取。',
    'wood_result_prompt_requires_user': '木人试炼结果提示需要在游戏内处理。',
    'wood_daily_counter_unmet': '木人试炼免费次数已用完，日常进度尚未达标。',
    'arena_battle_cooldown': '竞技场战斗仍在冷却中。',
    'arena_no_current_free_bonus': '当前没有可领取的竞技宝箱。',
    'flower_no_eligible_friends': '当前没有可送花的好友。',
    'monster_no_friend_target': '当前没有可协助降妖的好友目标。',
    'stone_no_existing_silver_pick': '当前银镐不足，本次未采石。',
    'mingshi_first_harvest_used': '今日铭石免费收获次数已用完。',
    'refine_insufficient_coins': '钱币不足，本次未炼魂。',
    'refine_coin_limit': '今日钱币炼魂次数已用完。',
    'refine_experience_limit_verified': '今日经验炼魂的 10 次机会已用完。',
    'refine_experience_limit': '今日经验炼魂的 10 次机会已用完。',
    'refine_insufficient_experience': '经验不足，本次未炼魂。',
    'refine_daily_counter_unmet': '炼魂次数已用完，日常进度尚未达标。',
    'daily_refresh_before_ready': '冷却将在游戏每日刷新之后结束，本日已不再等待。',
    'stone_disabled': '采石暂不支持自动执行。',
    'mingshi_disabled': '铭石收获暂不支持自动执行。',
    'refine_disabled': '炼魂暂不支持自动执行。',
    'arena_disabled': '竞技宝箱暂不支持自动领取。',
    'soul_disabled': '聚魂暂不支持自动执行。',
    'daily_soul_free_blue_exhausted': '免费蓝魂次数已用完。',
    'daily_brawl_view_remains': '比武大会界面尚未确认关闭，请检查游戏当前界面。',
    'daily_timed_cleanup_unverified': '定时任务界面缺少关闭确认，请检查游戏当前界面。',
    'daily_items_processed_without_reply': '本次操作请求已发出，未收到回包。',
    'mingshi_harvest_submitted_no_reply': '本次铭石收获请求已发出，未收到回包。',
    'copy_no_reply_processed': '本次抄录请求已发出，未收到回包。',
    'copy_current_state_confirmed': '游戏页面已确认抄录。',
    'mingshi_page_harvest_verified': '游戏页面已确认收获铭石。',
    'guild_request_submitted_no_reply': '本次同盟请求已发出，未收到回包。',
    'guild_current_page_confirmed': '游戏页面已确认同盟操作。',
    'guild_server_rejected': '游戏服务器拒绝本次同盟操作。',
    'online_gift_claim_submitted_no_reply': '本次礼包请求已发出，未收到回包。',
    'online_gift_page_claim_verified': '游戏页面已确认领取礼包。',
    'city_tree_submitted_no_reply': '本次灵气树收取请求已发出，未收到回包。',
    'daily_task_not_observed': '游戏日常列表未提供该任务的进度，本次未执行。',
    'daily_tribute_task_missing': '游戏日常列表未提供灵兽上贡的进度。',
    'daily_feature_locked': '游戏显示该功能入口尚未解锁。',
    'daily_activity_disabled': '游戏当前配置未启用该项日常。',
    'daily_tribute_options_not_configured': '请先配置灵兽上贡目标及次数上限。',
    'event_rewards_resource_spend_not_authorized': '活动需要消耗资源或费用未确定，本轮未获准执行。',
    'event_rewards_zhaoyun_cost_unknown': '七进七出的费用未确定，本轮未执行。',
    'selected_sign_bundles_salary_arena_point_race_rewards': '该签到入口会连带领取其他奖励，本次按任务选择跳过。',
    'selected_salary_bundles_arena_rewards': '俸禄入口会同时领取竞技奖励，无法按当前选择分别领取。',
    'selected_events_bundle_spends_money_stones_general_souls': '活动入口会同时消耗多种资源，无法按当前选择分别执行。',
    'selected_online_reward_cannot_separate_event_bundle': '在线奖励与其他活动领取及消耗无法分开，本次未执行。',
    'elite_daily_baseline_unobserved': '今天已用的精英次数不可读，需观察到游戏每日刷新后才能开始。',
    'elite_stage_and_star_required': '请先选择可用的精英关卡和星级。',
    'elite_selected_stage_not_current_unlocked_unfinished': '所选精英关卡或星级未开放、已完成或无法读取，请重新选择。',
    'elite_attempts_exhausted': '今日精英挑战次数已用完。',
    'elite_run_target_reached': '已达到本轮精英挑战目标次数。',
    'training_battle_no_progress_cooldown': '训练战斗后妖魔未被消灭，当前目标已进入冷却。',
    'sign_page_rewards_receipt_and_state_confirmed': '已收到领奖回包，游戏状态已确认奖励领取。',
    'sign_page_rewards_free_rewards_claimed': '签到页本次可领取的免费奖励已领取。',
    'sign_page_rewards_no_claimable_button': '签到页当前没有可领取的奖励。',
    'divinity_battle_lost': '天神挑战失败，本次已停止挑战。',
    'divinity_clear_required': '需要先击败本组全部天神，才能收获铭石。',
    'divinity_roster_cleared': '本组天神已全部击败。',
    'soul_no_free_starts': '当前没有可用的免费聚魂次数。',
    'soul_no_eligible_starts': '当前没有符合本轮配置的聚魂机会。',
    'soul_plan_budget_limit': '本轮聚魂剩余额度不足以继续操作。',
    'monster_demon_battle_not_won': '复仇魔王战斗未获胜，本次未收取奖励。',
    'brawl_closed': '比武大会当前未开放。',
    'arena_battle_daily_rewards_complete': '竞技场今日日常的钱币和声望奖励均已达到上限。',
    'trial_cleared': '名将试炼已通关。',
    'wood_no_free_quota': '今日木人试炼免费次数已用完。',
    'daily_reward_claim_confirmed': '日常分类奖励已领取，奖励窗口已关闭。',
    'daily_workflows_pending': '日常子任务仍在执行，等待结果。',
    'daily_workflows_running': '正在执行日常项目。',
    'daily_workflows_queued': '日常项目等待执行。',
    'daily_workflows_unknown': '部分日常操作的结果尚未收到确认，相关操作不会重复提交。',
    'daily_workflows_partial': '部分日常项目未完成。',
    'daily_workflows_unavailable': '当前没有可继续执行的日常项目。',
    'task_not_enabled': '本轮未启用该任务。',
    'no_popup': '当前没有待处理的游戏提示。',
    'result_page_pending': '当前仍在战斗结算界面，等待退出结算。',
    'business_not_idle': '战斗、培养或角色刷新尚未确认结束，暂不能切换玩法。',
    'business_pending': '当前玩法仍有未结束的操作，暂不能切换玩法。',
    'navigation_pending': '正在切换游戏界面，等待到达目标场景。',
    'scene_not_idle': '当前场景尚不能切换玩法，等待游戏返回可操作界面。',
    'owned_loss_scene_restored': '本轮失败后的战斗界面已退出。',
    'PASS': '当前操作条件已满足。',
    'city_activity_pending': '等待处理主城项目。',
    'daily_city_activity_pending': '正在处理主城项目。',
    'city_gather_no_current_targets': '当前没有可收取的目标。',
    'city_tree_overflow_skipped': '灵气容量不足，部分灵气树已跳过。',
    'city_gather_initial_target_bound': '目标数量超过本轮上限，剩余目标未处理。',
    'city_gather_initial_targets_finished': '当前目标已处理。',
    'city_monster_result_semantics_unverified': '已收到小妖收取回包，但结果含义尚未确认。',
    'city_monster_no_reply_assumed_success': '小妖收取未收到回包，按设置视为完成。',
    'monster_no_revenge_demon': '当前没有复仇魔王。',
    'monster_demon_cooldown': '复仇魔王冷却中。',
    'monster_demon_source_unavailable': '复仇魔王入口暂不可用。',
    'monster_revenge_activity_missing': '游戏未提供复仇魔王的日常状态。',
    'shop_insufficient_mana': '灵气不足，部分商铺未生产。',
    'shop_paid_immediate_enabled': '游戏已启用即时生产，商铺未使用该付费方式。',
    'shop_selected_duration_recipe_missing': '当前商铺没有所选时长的商品。',
    'shop_recipe_duration_ambiguous': '当前商铺的商品信息不明确。',
    'shop_runtime_recipe_template_mismatch': '商铺商品信息发生变化，未生产。',
    'shop_no_current_work': '当前没有可收取或生产的商铺。',
    'shop_initial_list_complete': '本轮可处理的商铺已处理。',
    'online_gift_no_next_tier': '当前角色的礼包已领完。',
    'online_gift_current_tier_not_due': '下一档礼包尚未到领取时间。',
    'online_gift_server_current_tier_unavailable': '游戏暂不允许领取当前礼包。',
    'online_gift_one_current_tier_claimed': '已领取当前档礼包。',
    'online_gift_clean_main_required': '当前游戏界面尚未准备好领取礼包。',
    'online_gift_source_icon_unavailable': '当前礼包入口不可用。',
    'event_rewards_online_entry_unconfirmed_claim_not_issued': '入口未确认，领奖未发出。',
    'daily_cooldown_deferred': '冷却中，稍后继续。',
    'daily_cooldown_wait': '正在等待冷却结束。',
    'daily_cooldown_recheck_pending': '冷却时间已到，等待重新检查。',
    'daily_free_reward_pending': '等待领取免费奖励。',
    'daily_arena_free_attempts_pending': '尚有免费竞技次数待处理。',
    'daily_latest_progress_met': '当前进度已达要求。',
    'daily_items_observed_complete': '本轮可处理项目已处理。',
    'daily_items_uncompleted': '部分日常项目尚未完成。',
    'daily_retry_current_offer_unavailable': '当前入口不满足继续条件，暂未重试。',
    'daily_retry_current_observation_unverified': '当前状态尚未确认，暂未重试。',
    'daily_reward_route_unsupported': '该分类奖励暂不支持自动领取。',
    'daily_task_route_unsupported': '该任务暂不支持自动执行。',
    'daily_reward_activity_locked': '当前日常奖励活动尚未开放。',
    'daily_reward_claimed_observed': '游戏显示该分类奖励已领取。',
    'daily_category_hidden': '该日常分类当前未显示。',
    'daily_task_unavailable': '当前任务暂不可用。',
    'daily_task_progress_unknown': '当前任务进度尚未确认。',
    'daily_progress_inconsistent': '游戏提供的任务进度不一致，暂未继续。',
    'daily_popup_unresolved': '游戏提示尚未处理完毕。',
    'elite_no_current_available_option': '当前没有可用的精英挑战选项。',
    'copy_condition_not_met': '抄录功能尚未解锁。',
    'copy_free_limit_reached': '本日免费抄录次数已用完。',
    'copy_waiting_cooldown': '抄录仍在冷却中。',
    'pointrace_closed': '分组积分赛尚未开放。',
    'event_rewards_supported_claims_processed': '本次可处理的免费奖励已处理。',
    'event_rewards_supported_routes_processed': '可支持的奖励已处理，部分项目暂不可处理。',
    'event_rewards_online_current_state_confirmed': '页面已确认领取。',
    'event_rewards_online_no_reply_processed': '本次领奖请求已发出，未收到回包。',
    'event_rewards_online_cooldown_remaining': '在线奖励仍在冷却中。',
    'event_rewards_zhaoyun_recruit_locked': '七进七出招募功能尚未解锁。',
    'event_rewards_online_reply_rejected': '游戏明确拒绝本次在线奖励操作。',
    'event_rewards_observation_failed': '无法读取奖励状态，结果尚未确认。',
    'event_rewards_command_failed': '奖励操作调用异常，结果尚未确认。',
    'event_rewards_command_unconfirmed': '奖励操作尚未确认。',
    'event_rewards_command_binding_invalid': '奖励回执与本次操作不一致。',
    'event_rewards_operation_changed': '奖励操作归属发生变化，结果尚未确认。',
    'event_rewards_claim_progress_invalid': '领取进度异常，结果尚未确认。',
    'event_rewards_cleanup_unconfirmed': '奖励界面尚未确认关闭。',
    'event_rewards_processed_release_unconfirmed': '领奖处理结果或界面关闭尚未确认。',
    'event_rewards_claims_remaining_after_close': '关闭后仍有可领奖励，本次尚未处理完。',
    'event_rewards_online_request_not_issued': '在线奖励请求尚未发出。',
    'event_rewards_online_claim_not_issued': '领奖请求尚未发出。',
    'event_rewards_online_receipt_target': '在线领奖回执与本次目标不一致。',
    'event_rewards_online_animation_or_settlement_timeout': '等待领奖动画或结算超时，结果尚未确认。',
    'copy_reward_credit_unverified': '抄录奖励入账尚未确认。',
    'copy_free_count_changed_outside_operation': '免费抄录次数出现本次操作之外的变化。',
    'sign_salary_current_day_already_signed': '今日已签到。',
    'sign_salary_checkin_state_confirmed': '页面已确认签到。',
    'sign_page_rewards_salary_already_claimed': '今日俸禄已领取。',
    'sign_page_rewards_arena_reward_no_free_reward': '竞技宝箱暂无可领取奖励。',
    'sign_page_rewards_point_reward_no_free_reward': '积分宝箱暂无可领取奖励。',
    "star": "正在进行星灵激活。",
    "star_running": "星灵激活进行中，只使用战魂。",
    "star_practice_exhausted": "战魂已用完，星灵激活结束。",
    "star_insufficient_practice": "战魂不足以下一次激活，星灵激活结束。",
    "star_maxed": "星灵已全部激活。",
    "star_no_next_activation": "没有下一项星灵激活，已结束。",
    "star_practice_budget_reached": "已达到本轮战魂预算，星灵激活结束。",
    "star_request_budget_reached": "已达到本轮星灵激活上限。",
    "star_data_unknown": "星灵等级、战魂或资源状态尚未确认，已停止。",
    "star_receipt_unverified": "星灵服务器回调未确认，已停止自动重试。",
    "star_no_progress": "尚未确认星灵等级增加，已停止。",
    "star_consumption_mismatch": "战魂消耗与激活报价或服务器回调不一致，已停止。",
    "star_gold_changed": "星灵激活期间元宝发生变化，已停止。",
    "star_cost_unknown": "下一次星灵激活的战魂消耗尚未确认，已停止。",
    "star_reward_confirm_unverified": "星灵奖励提示的状态尚未确认，已停止。",
    "star_close_unverified": "星灵界面关闭结果尚未确认，已停止。",
    "star_terminal_reason_unknown": "助手未识别星灵返回的结束条件，已停止；请在游戏内核对星灵等级和战魂。",
    "star_action_unavailable": "暂无法执行普通星灵激活，请确认游戏界面。",
    "star_typed_practice_target_required": "星灵战魂预算尚未绑定，已停止。",
    "star_resource_or_level_invalid": "星灵等级或战魂数据无效，已停止。",
    "star_network_not_idle": "正在等待本轮游戏请求完成。",
    "star_normal_ui_loading": "正在等待星灵界面加载。",
    "star_reward_not_owned": "当前奖励提示不属于本轮星灵激活，已停止。",
    "star_activation_animation": "正在等待星灵激活动画结束。",
    "star_ui_binding_or_selection": "星灵页面或选中项已变化，已停止。",
    "star_existing_subui": "当前游戏已打开其他功能界面，星灵激活已停止。",
    "star_context_or_command_unavailable": "星灵调用上下文暂不可用，已停止。",
    "star_response_progress_or_resource_mismatch": "星灵回调与等级或战魂变化不一致，已停止。",
    "star_gui_context_or_resources_changed": "发令前星灵界面或资源已变化，已停止。",
    "star_resources_changed_before_send": "发送前星灵资源已变化，已停止。",
    "star_normal_branch_threw": "星灵普通游戏操作未确认，已停止自动重试。",
    "tribute": "正在进行灵兽上贡。",
    "tribute_running": "灵兽上贡进行中，只使用桃子。",
    "tribute_peaches_exhausted": "桃子已用完，上贡结束。",
    "tribute_request_budget_reached": "已达到本轮上贡次数上限。",
    "tribute_level_cap_reached": "灵兽已达 100 级，上贡结束。",
    "tribute_rankup_required": "灵兽需要进阶，上贡已停止，请在游戏内处理。",
    "tribute_target_reached": "灵兽已达到本轮目标，上贡结束。",
    "tribute_target_binding_mismatch": "灵兽上贡目标与本轮设定不一致，已停止。",
    "tribute_rankup_gate_unknown": "尚未确认普通进阶条件，已停止。",
    "tribute_rankup_postcondition_unverified": "尚未确认灵兽进阶结果或资源未变，已保留记录。",
    "tribute_rankup_confirm_unverified": "尚未确认灵兽进阶提示关闭，已保留记录。",
    "tribute_premium_mode_forbidden": "当前上贡界面使用元宝，已停止；请在游戏内切换为桃子。",
    "tribute_receipt_unverified": "上贡服务器回执未确认，已保留记录，禁止自动重试。",
    "tribute_no_progress": "上贡后未确认等级或经验增加，已停止。",
    "tribute_premium_or_unknown_consumption": "上贡消耗无法确认或涉及元宝，已停止。",
    "tribute_food_consumption_mismatch": "桃子消耗或库存变化与预期不一致，已停止。",
    "tribute_food_budget_exhausted": "本轮可用桃子已用尽，上贡已停止。",
    "tribute_food_budget_unavailable": "桃子库存变化超出本轮资源范围，已停止。",
    "tribute_progress_gate_unknown": "灵兽上贡状态尚未确认，已停止。",
    "tribute_premium_or_unknown_quote": "未确认上贡仅消耗桃子，已停止。",
    "tribute_data_unknown": "灵兽等级、经验或桃子数量尚未确认。",
    "tribute_cleanup_unverified": "上贡已停止，尚未确认培养界面关闭。",
    "tribute_close_postcondition_missing": "尚未确认上贡界面关闭，已保留记录。",
    "tribute_close_identity_unknown": "无法确认需要关闭的上贡界面，已停止。",
    "tribute_terminal_reason_unknown": "助手未识别上贡返回的结束条件，已停止；请在游戏内核对灵兽等级和桃子。",
    "tribute_terminal_action_unavailable": "上贡已停止，暂无法关闭相关界面。",
    "tribute_unexpected_close": "上贡界面状态异常，已停止。",
    "tribute_action_unavailable": "暂无法执行普通上贡，请确认游戏界面。",
    "bridge_attempt_unknown": "本场结算未确认，保留记录并暂停后续战斗。",
    "settlement_receipt_missing_or_not_applied": "本场结算未确认，保留记录并暂停后续战斗。",
    "invalid_settlement_evidence": "结算证据不足，保留记录并暂停后续战斗。",
    "loss_observed_settlement_unconfirmed": "已观察到失败画面，但结算尚未确认，已停止后续战斗。",
    "battle_timeout_pending_retained": "等待结算超时，保留记录并暂停后续战斗。",
    "stop_timeout_pending_retained": "停止时结算尚未确认，已保留记录。",
    "three_consecutive_losses": "连续失败已达三次，已停止发起战斗。",
    "three_cumulative_losses": "本轮挑战累计失败已达三次，已停止发起战斗。",
    "challenge_no_eligible_normal": "当前没有已解锁且未完成的挑战关卡，本轮挑战已结束。",
    "challenge_normal_only": "当前为英雄关卡或非挑战界面，本轮挑战已停止。",
    "challenge_observation_invalid": "挑战状态无法确认，本轮挑战已停止。",
    "challenge_ack_correlation_failed": "挑战调用回执与本轮操作不一致，已停止后续操作。",
    "normal_challenge_type2_required": "当前关卡不是挑战关卡，本轮挑战已停止。",
    "challenge_cooldown_active": "挑战仍在冷却，本轮挑战已停止。",
    "challenge_list_not_interactive": "挑战列表尚不可操作，已停止后续操作。",
    "challenge_list_conflicting_ui": "挑战列表有其他界面遮挡，已停止后续操作。",
    "challenge_list_network_pending": "挑战列表仍有未决服务器请求，已停止后续操作。",
    "challenge_list_loading": "挑战列表仍在加载，已停止后续操作。",
    "normal_challenge_candidate_changed": "挑战关卡已发生变化，本轮挑战已停止。",
    "challenge_candidate_ambiguous": "无法唯一确认挑战关卡，本轮挑战已停止。",
    "challenge_return_unavailable": "无法确认挑战结算返回界面，本轮挑战已停止。",
    "WAITING_CHALLENGE_ENTER": "挑战关卡正在进入，等待服务器切换场景。",
    "WAITING_CHALLENGE_MAP": "挑战地图正在初始化，等待场景和敌军就绪。",
    "challenge_entry_receipt_mismatch": "挑战进入状态与本轮已提交的关卡不一致，本轮挑战已停止。",
    "challenge_result_navigation_timeout": "挑战结算已返回，但界面切换等待超时，本轮挑战已停止。",
    "unknown_guide": "检测到尚未接入的教学，请在游戏内处理后继续。",
    "no_eligible_stage": "暂未发现可继续挑战的敌军，请检查当前章节或教学。",
    "user_pause": "已暂停后续发起，当前战斗仍会继续结算。",
    "development_user_stopped": "本次培养已停止。",
    "normal_troop_selection_gate:character_level": "角色未达到下一关所需等级，本轮征战已结束。",
    "user_stop": "已请求停止后续发起；若有在途战斗，正在核对结算。",
    "assistant_shutdown": "助手已关闭，本轮进度已保存。",
    "task_cancelled": "任务已取消，停止后续发起。",
    "restart_requires_review": "已恢复本机记录；继续前会先核对未决状态。",
    "explicit_start_required": "准备就绪，可开始征战。",
    "result_reconciled_review_required": "未决结果已核对，请确认当前状态后继续。",
    "observed_loss_recorded": "已按本场小败结果补记一次失败，可继续征战。",
    "target_already_cleared": "原目标已通关，旧场次回执未确认；可开始新一轮。",
    "game_transition_timeout": "游戏过场等待超时，已暂停后续发起；请确认当前界面。",
    "guide_transition_timeout": "教学界面等待超时，请确认游戏当前界面。",
    "unmatched_result": "当前结果无法匹配，请确认游戏状态。",
    "unowned_battle": "检测到未由本轮发起的战斗，请确认游戏状态。",
    "campaign_invocation_unconfirmed": "战斗操作结果未确认，已保留记录并暂停。",
    "development_ui_wait_timeout": "等待培养界面超时，请确认游戏当前界面。",
    "development_observation_timeout": "等待培养状态超时，请在游戏内核对培养结果。",
    "prior_command_unresolved": "上一次操作缺少结果确认，请先在游戏内核对该操作结果。",
}

_PHASES = {"MAIN": "主界面", "CHALLENGE_LIST": "挑战列表", "CAMPAIGN": "章节界面", "TROOP": "敌军界面",
           "FORMATION": "布阵界面", "BATTLE": "战斗中", "RESULT": "结算界面",
           "SCENARIO": "剧情中", "GENERAL_TUTORIAL": "武将教学中", "BLOCKED": "等待确认"}

_ACTIONS = {"enter": "进入征战", "select": "选择敌军", "prepare": "准备战斗",
            "enter_challenge": "进入挑战关卡",
            "start": "发起战斗", "continue": "继续剧情", "ack_result": "确认结算界面",
            "next_chapter": "进入下一章节", "general_deploy": "上阵武将",
            "formation_confirm": "确认布阵", "player_state_refresh": "刷新角色状态",
            "growth_open": "打开成长任务", "growth_check": "查看成长任务",
            "growth_claim": "领取成长奖励", "growth_reward_confirm": "确认成长奖励",
            "growth_close": "关闭成长界面", "equipment_open": "打开装备界面",
            "reinforce_open": "打开装备强化", "enchant_tab": "切换强化界面",
            "equipment_enchant": "强化装备", "equipment_close": "关闭装备界面",
            "spirit_open": "打开培养界面", "spirit_train": "进行培养",
            "spirit_rankup": "进行升阶", "spirit_rankup_confirm": "确认升阶",
            "spirit_close": "关闭培养界面"}
_ACTIONS.update({"tribute_observe": "读取灵兽上贡状态", "tribute_open": "打开灵兽界面",
                 "tribute_panel_open": "打开上贡界面", "tribute_train": "使用桃子上贡",
                 "tribute_rankup": "灵兽普通进阶", "tribute_rankup_confirm": "确认灵兽进阶",
                 "tribute_close": "关闭上贡界面", "tribute_parent_close": "关闭灵兽界面"})
_ACTIONS.update({"star_observe": "读取星灵状态", "star_open": "打开星灵界面",
                 "star_activate": "使用战魂激活星灵", "star_reward_confirm": "确认星灵奖励",
                 "star_close": "关闭星灵界面"})


def _duration(seconds_text: str) -> str:
    if not re.fullmatch(r'[0-9]{1,9}', seconds_text):
        return ''
    hours, seconds = divmod(int(seconds_text), 3600)
    minutes, seconds = divmod(seconds, 60)
    result = (f'{hours}小时' if hours else '') + (f'{minutes}分' if minutes else '')
    return result + (f'{seconds}秒' if seconds or not result else '')


# PointRace chest is a reward-only normal scene flow, separate from battles.
_REASONS.update({
    'point_chest_free_reward_confirmed': '积分宝箱领取已确认，已返回主城。',
    'point_chest_already_claimed': '当前可免费领取的积分宝箱已领取，已返回主城。',
    'point_chest_no_free_reward': '当前没有可免费领取的积分宝箱，已返回主城。',
    'point_chest_historical_paid_only': '当前只有需要元宝找回的历史积分奖励，本次未领取。',
    'point_chest_finished': '积分宝箱流程已结束，已返回主城。',
    'point_chest_returning_home': '正在从积分赛返回主城。',
    'point_chest_waiting_selector': '正在打开竞技入口。',
    'point_chest_waiting_entry_receipt': '已请求进入积分赛，等待游戏回复。',
    'point_chest_waiting_scene': '等待积分赛场景加载。',
    'point_chest_waiting_point_page': '等待积分赛页面恢复可操作。',
    'point_chest_waiting_chest': '等待积分宝箱页面加载。',
    'point_chest_waiting_claim_receipt': '积分宝箱领取请求已发出，等待游戏确认。',
    'point_chest_waiting_reward_close': '奖励已确认，正在关闭领奖窗口。',
    'point_chest_waiting_stock_close': '等待游戏完成领奖窗口收尾。',
    'point_chest_waiting_transport': '等待积分赛请求处理结束。',
    'point_chest_home_required': '积分宝箱需要从可操作的主城入口进入。',
    'point_chest_owner_cycle_or_premium_changed': '角色、刷新时间或元宝余额发生变化，已停止后续领取。',
    'point_chest_scene_identity_changed': '积分赛场景已发生变化，已停止后续领取。',
    'point_chest_observation_failed': '积分宝箱状态未能读取，未继续发出领取请求。',
    'point_chest_observation_unavailable': '积分宝箱的当前状态暂不可读取。',
    'point_chest_operation_timeout': '等待积分宝箱处理超时，后续领取已停止。',
    'point_chest_evidence_write_failed': '领取记录无法保存，已停止后续领取。',
    'point_chest_schema_unavailable': '当前组件未提供积分宝箱状态，请重新连接新版组件。',
    'point_chest_route_changed': '积分宝箱执行入口已变化，已停止后续领取。',
    'point_chest_character_changed': '当前角色已变化，已停止积分宝箱领取。',
    'point_chest_refresh_cycle_changed': '游戏日常已刷新，已停止使用刷新前的领取状态。',
    'point_chest_operation_changed': '积分宝箱操作归属已变化，已停止后续领取。',
    'point_chest_command_outcome_unknown': '积分宝箱操作尚未取得确认，后续领取已停止；请核对游戏中的领取结果。',
    'point_chest_command_invocation_unverified': '积分宝箱操作尚未取得调用确认，已停止后续领取。',
    'point_chest_claimed_cleanup_pending': '积分宝箱已领取，但尚未确认返回主城。',
    'point_chest_return_unverified': '积分宝箱场景尚未确认返回主城，请先核对当前游戏页面。',
    'point_chest_reward_close_unverified': '领奖窗口尚未确认关闭，请先核对游戏中的奖励窗口。',
    'point_chest_reward_close_unresolved': '领奖窗口尚未完成收尾，后续领取已停止。',
    'point_chest_attempted_row_reappeared': '已提交过的积分奖励再次出现，助手不会重复领取该条奖励。',
    'point_chest_receipt_target_or_premium': '积分奖励回包与本次免费领取条件不一致，后续领取已停止。',
    'point_chest_claim_callback_unobserved': '积分奖励回复尚未完成正常处理，后续领取已停止。',
    'point_chest_receipt_state_mismatch': '领取回包与游戏奖励列表变化不一致，后续领取已停止。',
    'point_chest_entry_callback_unobserved': '积分赛进入回复尚未完成正常处理，已停止继续操作。',
    'point_chest_entry_listener_release_unverified': '积分赛进入流程尚未完成收尾，暂不能交接场景。',
})


def readable_reason(reason: object, fallback: str = "助手未识别该状态的原因，请在游戏内核对当前操作结果。") -> str:
    """Explain retained facts without changing raw reason codes or a supplied fallback."""
    text = str(reason or '').strip()
    if text in ('game_process_restart_required', 'object_connection_failed (game_process_restart_required)'):
        return '当前游戏进程有未确认操作，请先退出并重新启动游戏；重新连接不会重放该操作。'
    cooldowns = {
        'arena_cooldown': '竞技场', 'event_rewards_online_cooldown': '在线奖励',
        'copy_cooldown': '抄录', 'training_target_cooldown': '训练妖魔',
        'trial_boss_cooldown': '名将试炼', 'pointrace_cooldown': '分组积分赛',
        'arena_battle_cooldown': '竞技场战斗', 'monster_demon_cooldown': '复仇魔王',
    }
    code, separator, parameter = text.partition(':')
    parameter = parameter.strip()
    if code == 'task_dependency_not_enabled':
        names = {**DAILY_CATALOG, 'salary': '俸禄', 'arena': '竞技奖励', 'point_race': '积分赛奖励'}
        missing = [names.get(key.strip(), '关联任务') for key in parameter.split(',') if key.strip()]
        return ('需同时启用：' + '、'.join(missing) + '，本次已跳过。' if missing
                else '关联任务未启用，本次已跳过；请检查任务选择。')
    if code == 'sign_page_rewards_receiver_unavailable':
        if parameter == 'point_reward':
            return '签到页无法自动领取积分宝箱，本次未发出领取请求；可从游戏“积分赛 → 宝箱”领取。'
        reward = {'salary': '俸禄', 'arena_reward': '竞技宝箱', 'point_reward': '积分宝箱'}.get(parameter, '签到页奖励')
        return reward + '在签到页的自动领取入口不可用，本次未发出领取请求；请在游戏内检查并领取。'
    if code == 'event_rewards_zhaoyun_resources_insufficient':
        parts = parameter.split(':')
        resource = {'4': '钱币', '10': '强化石', '39': '紫魂', '40': '橙魂'}.get(parts[0], '资源')
        if len(parts) == 3 and all(re.fullmatch(r'[0-9]{1,12}', part) for part in parts[1:]):
            return f'七进七出的{resource}不足：当前 {int(parts[1])}，需要 {int(parts[2])}。'
        return '七进七出所需资源不足，本次未执行。'
    if code == 'online_gift_current_tier_not_due':
        duration = _duration(parameter)
        return '下一档礼包尚未到领取时间' + ('，剩余' + duration if duration else '') + '。'
    if code.startswith('resource_policy_'):
        names = {'money': '钱币', 'gold': '元宝', 'peaches': '桃子', 'practice': '战魂',
                 'stamina': '体力', 'stone': '强化石', 'mana': '灵气', 'experience': '经验',
                 'contribution': '同盟贡献', 'silver_pick': '银镐', 'gold_pick': '金镐'}
        resource = names.get(parameter, '该项资源')
        if code == 'resource_policy_resource_disabled':
            return '本次策略未允许消耗' + resource + '。'
        if code == 'resource_policy_budget_exhausted':
            return '本轮' + resource + '剩余额度不足以支付本次操作。'
        if code == 'resource_policy_purchase_disabled':
            return '本次策略未允许购买额外机会。'
        if code == 'resource_policy_cost_unknown':
            return '当前' + resource + '费用不可读，本次未执行；请在游戏内核对费用。'
        policy_reasons = {
            'resource_policy_provider_unavailable': '无法读取本轮资源策略，本次未执行；请检查资源设置。',
            'resource_policy_invalid': '本轮资源策略配置无效，本次未执行；请检查资源设置。',
            'resource_policy_budget_owner_invalid': '本轮资源额度未绑定到有效任务，本次未执行。',
            'resource_policy_current_revision_changed': '发令前游戏状态已变化，本次未执行。',
            'resource_policy_current_identity_changed': '发令前角色身份已变化，本次未执行。',
            'resource_policy_current_observation_unavailable': '当前玩法状态不可读，本次未执行。',
            'resource_policy_equipment_quote_unavailable': '无法确定本次装备培养的费用，本次未执行。',
            'resource_policy_tribute_peach_quote_changed': '上贡方式已变化或桃子不足，本次未执行。',
            'resource_policy_spirit_peach_quote_changed': '灵兽培养未使用桃子模式，本次未执行。',
            'resource_policy_growth_soul_free_quote_unavailable': '成长任务的免费聚魂资格不可读或次数已用完，本次未执行。',
            'resource_policy_soul_free_only': '本轮只允许免费聚魂，当前聚魂需要钱币，本次未开始。',
            'resource_policy_guild_donation_unknown': '当前同盟捐献类型不可识别，本次未捐献。',
            'resource_policy_guild_reserved_choice_changed': '同盟捐献选项与本轮预留额度不一致，本次未捐献。',
            'resource_policy_script_task_changed': '发令前日常任务已切换，本次未执行。',
            'resource_policy_shop_quote_unavailable': '当前商铺未提供可核对的商品报价，本次未生产。',
            'resource_policy_shop_charge_phase_unknown': '无法确定商铺是否即将扣费，本次未生产。',
            'resource_policy_trial_cost_unknown': '无法确定本次名将试炼的体力费用，本次未挑战。',
            'resource_policy_cost_capability_unavailable': '助手尚不能核对该操作的资源费用，本次未执行；请在游戏内处理。',
        }
        return policy_reasons.get(code, fallback)
    if code in ('daily_cooldown_cleanup_unverified', 'daily_cooldown_scene_unclosed'):
        route = {'copy': '抄录', 'online_reward': '在线奖励', 'arena': '竞技场',
                 'point_race': '分组积分赛', 'training': '训练妖魔', 'trial': '名将试炼',
                 'brawl': '比武大会'}.get(parameter, '相关功能')
        return route + ('界面尚未确认关闭，暂不能继续。' if code.endswith('cleanup_unverified')
                        else '场景尚未退出，暂不能继续。')
    if code in cooldowns:
        duration = _duration(parameter)
        return cooldowns[code] + '冷却中' + ('，剩余' + duration if duration else '') + '。'
    if text.startswith('shop_recipe_cost_not_zero:'):
        return '商品需要其他资源，本轮未生产。'
    if text.startswith('battle_start_unresolved:'):
        return '该关卡上次战斗的结算尚未确认，已停止再次发起。'
    if text == 'battle_durable_prior_pending_or_unknown':
        return '该玩法上次战斗的结算尚未确认，已停止再次发起。'
    if text.startswith('battle_durable_'):
        return '战斗记录无法确认，已停止再次发起。'
    if text.startswith('battle_start_'):
        return '战斗记录无法确认，已停止再次发起。'
    if text.startswith('campaign_gate: challenge_entry_unknown:') or text.startswith('challenge_entry_unknown:'):
        return '挑战进入结果未确认，本轮挑战已停止。'
    for prefix in ('campaign_gate: ', 'resume_gate_closed: ', 'normal_troop_selection_gate:'):
        if text.startswith(prefix):
            return readable_reason(text[len(prefix):], fallback)
    code = re.split(r'[:（(]', text, maxsplit=1)[0].strip()
    if code in _REASONS:
        return _REASONS[code]
    if any('\u4e00' <= char <= '\u9fff' for char in text) and not re.search(r'[A-Za-z_{}\\]', text):
        return text
    return fallback


def readable_message(message: str, *, level: str = 'INFO') -> str:
    """Keep ordinary messages; do not expose diagnostic payloads or code tokens."""
    reason = readable_reason(message, '')
    if reason:
        return reason
    title, separator, detail = message.rpartition('：')
    if separator and any('\u4e00' <= char <= '\u9fff' for char in title) and not re.search(r'[A-Za-z_{}\\]', title):
        reason = readable_reason(detail, '')
        if reason:
            return title + separator + reason
    if re.search(r'\{|\}|[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+|[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}|[0-9a-fA-F]{16,}|[A-Za-z]{4,}|\b(?:pid|nonce)\b|[A-Za-z]:\\', message, re.IGNORECASE):
        if str(level).upper() in ('INFO', 'DEBUG'):
            return ''
        prefix = re.split(r'[A-Za-z_{}\\]', message, maxsplit=1)[0].rstrip(' （(：:；;，,。')
        if any('\u4e00' <= char <= '\u9fff' for char in prefix):
            return prefix + '。'
        return "助手遇到异常，当前步骤未能正常完成。"
    return message


def _location(campaign: dict) -> str:
    parts = []
    for key, label in (("campaign_id", "章节"), ("troop_id", "敌军")):
        value = campaign.get(key)
        if type(value) is int and value > 0:
            parts.append(f"{label} {value}")
    return " · ".join(parts)


def workflow_ui_message(event: WorkflowEvent) -> str:
    value = event.snapshot
    campaign = value.get("campaign") or {}
    development = value.get("development") or {}
    title = '挑战' if value.get('workflow_scope') == 'challenge' else '征战'
    location = _location(campaign)
    prefix = (location + " · ") if location else ""
    reason = campaign.get("reason") or (value.get("state") or {}).get("reason") or value.get("control_reason")
    if campaign.get('gate') not in (None, '', 'PASS'):
        reason = campaign.get('reason') or campaign['gate']
    if event.kind == WorkflowEventKind.BATTLE_SETTLED:
        outcome = (campaign.get("attempt") or {}).get("outcome")
        return prefix + {"WIN": "本场战斗结算：胜利。", "LOSS": "本场战斗结算：失败。"}.get(outcome, "本场战斗结算已收到。")
    if event.kind == WorkflowEventKind.REWARD_POPUP_APPEARED:
        return "发现奖励确认窗口。"
    if event.kind == WorkflowEventKind.STORY_CHANGED:
        if campaign.get("phase") == "BLOCKED" or campaign.get('gate') not in (None, '', 'PASS'):
            return prefix + readable_reason(reason, title + "尚不能继续，缺少可识别的阻塞原因；请检查游戏当前界面。")
        return prefix + title + "进度：" + _PHASES.get(campaign.get("phase"), "正在更新") + "。"
    if event.kind in (WorkflowEventKind.GROWTH_TASK_CHANGED, WorkflowEventKind.EQUIPMENT_CHANGED,
                      WorkflowEventKind.TRIBUTE_CHANGED, WorkflowEventKind.STAR_CHANGED):
        growth = event.kind == WorkflowEventKind.GROWTH_TASK_CHANGED
        tribute = event.kind == WorkflowEventKind.TRIBUTE_CHANGED
        star = event.kind == WorkflowEventKind.STAR_CHANGED
        item = development.get("growth" if growth else "tribute" if tribute else "star" if star else "equipment") or {}
        title = "成长任务" if growth else "灵兽上贡" if tribute else "星灵激活" if star else "装备培养"
        if item.get('reason') == 'own_request_callback_capture_installed':
            return ''
        if item.get("reason") and not ((tribute and item['reason'] == 'tribute_running') or
                                       (star and item['reason'] == 'star_running')):
            return title + "：" + readable_reason(item["reason"], "助手未识别该操作的状态原因，请在游戏内核对操作结果。")
        if tribute:
            details = []
            level = item.get('level')
            if type(level) is int and level > 0:
                details.append(f'第{(level - 1) // 10 + 1}阶第{(level - 1) % 10 + 1}星')
            for key, label in (('experience', '经验'), ('food', '剩余桃子'), ('tribute_requests', '上贡请求')):
                number = item.get(key)
                if type(number) is int and number >= 0:
                    details.append(f'{label} {number}')
            if details:
                return title + '：' + ' · '.join(details) + '。'
        if star:
            details = [f'{label} {item[key]}' for key, label in
                       (('level', '等级'), ('practice', '剩余战魂'), ('spent_practice', '已用战魂'))
                       if type(item.get(key)) is int and item[key] >= 0]
            if details:
                return title + '：' + ' · '.join(details) + '。'
        action = (item.get("operation") or {}).get("action") or item.get("available_action")
        return title + "：" + _ACTIONS.get(action, "状态已更新") + "。"
    if event.kind == WorkflowEventKind.CONNECTION_CHANGED:
        connection = value.get("connection") or {}
        state = connection.get("state")
        if (state == "retired" and not connection.get("first_failure") and
                connection.get("error") in (None, "", "工作流所有权已释放。")):
            return "本轮" + title + "流程已结束，调用资源已释放。"
        if connection.get("error") or connection.get("first_failure"):
            detail = readable_reason(connection.get('error') or (connection.get('first_failure') or {}).get('message'), '')
            return "调用连接异常，已停止后续操作；" + (detail if detail else "请检查游戏是否仍在运行。")
        return "调用连接：" + {"ready": "已连接", "connecting": "正在连接", "disconnected": "已断开", "retired": "本轮连接已结束", "stopping": "正在停止", "unknown": "状态待确认", "error": "连接失败"}.get(state, "状态已更新") + "。"
    if value.get("stopping"):
        return prefix + readable_reason(reason, "正在停止后续操作；结束前仍需核对当前操作结果。")
    if campaign.get('gate') not in (None, '', 'PASS'):
        return prefix + readable_reason(reason, title + '尚不能继续，缺少可识别的阻塞原因；请检查游戏当前界面。')
    if reason in _REASONS:
        return prefix + readable_reason(reason)
    if value.get("admission_open") is False:
        return prefix + readable_reason(reason, "已暂停后续操作，缺少可识别的暂停原因；请检查游戏当前界面。")
    if reason and reason not in ("workflow_start", "workflow_resume"):
        return prefix + readable_reason(reason)
    return prefix + title + "状态：" + _PHASES.get(campaign.get("phase"), "正在更新") + "。"


def workflow_command_ui_message(record: dict) -> str:
    location = _location(record)
    action = _ACTIONS.get(record.get("action"), "游戏操作")
    if record.get('workflow_scope') == 'challenge':
        action = {'enter': '打开挑战列表', 'next_chapter': '返回挑战列表'}.get(record.get('action'), action)
    stage = record.get("stage")
    suffix = {"admitted": "已接收，准备调用。", "reply": "已收到调用回复，正在核对游戏状态。",
              "failed": "调用结果缺少确认，已停止后续操作；请在游戏内核对该操作是否完成。"}.get(stage, "状态已更新。")
    if stage == 'failed':
        detail = readable_reason(record.get('reason') or record.get('error'), '')
        if detail:
            suffix = '调用结果缺少确认，已停止后续操作。' + detail
    return ((location + " · ") if location else "") + action + "：" + suffix


def workflow_notice_ui_message(kind: str) -> str:
    return {"state_read_wait": "读取状态暂时受阻，正在等待恢复。",
            "state_read_resumed": "状态读取已恢复。",
            "session_monitor_retired": "本轮连接已结束，正在正常退出游戏。",
            "fault": "调用连接异常，已停止后续操作；请检查游戏是否仍在运行。"}.get(
                kind, "收到未识别的状态通知，请检查当前任务状态。")


def historical_log_ui_message(message: str) -> str:
    """Render an existing file record without replaying it into the logger."""
    if message.startswith("征战状态：") and "；原因：" in message:
        state, _, reason = message.partition("；原因：")
        mode = {"idle": "待命", "active": "自动推进中", "paused": "已暂停", "blocked": "需要确认", "stopped": "已停止", "loss_limit": "连续三败停止"}.get(state.partition("：")[2], "状态已更新")
        return "征战状态：" + mode + ("。" if reason == "正常推进" else "；" + readable_reason(reason))
    if message.startswith("停止征战："):
        return "停止征战：" + readable_reason(message.partition("：")[2], "已请求停止，正在核对当前操作结果。")
    for prefix in ("工作流事件 ", "工作流命令 ", "状态通知 ", "工作流故障 "):
        if not message.startswith(prefix):
            continue
        try:
            record = json.loads(message[len(prefix):])
            if not isinstance(record, dict):
                raise ValueError("record is not an object")
            if prefix == "工作流命令 ":
                return workflow_command_ui_message(record)
            if prefix == "状态通知 ":
                return workflow_notice_ui_message(record.get("kind"))
            if prefix == "工作流故障 ":
                return workflow_notice_ui_message("fault")
            kind = WorkflowEventKind(record.get("event"))
            campaign = dict(record.get("campaign") or {})
            campaign["attempt"] = record.get("battle") or {}
            control = record.get("control") or {}
            snapshot = {"campaign": campaign, "development": {"growth": record.get("growth") or {},
                        "equipment": record.get("equipment") or {}, "tribute": record.get("tribute") or {},
                        "star": record.get("star") or {}}, "state": record.get("runtime") or {},
                        "connection": record.get("connection") or {}, **control}
            if record.get("first_failure"):
                snapshot["connection"]["first_failure"] = record["first_failure"]
            return workflow_ui_message(WorkflowEvent(kind, "", "", 0, snapshot))
        except (ValueError, TypeError, KeyError, AttributeError):
            return "这条历史操作记录无法读取，请以当前任务状态和游戏界面为准。"
    return readable_message(message)


def semantic_value(value):
    """Ignore sampling clocks and diagnostic-only changes, retain gates/UI."""
    if isinstance(value, dict):
        return {key: semantic_value(item) for key, item in value.items()
                if key not in {"updated_utc", "timestamp", "frame", "frame_count", "sampled_utc",
                               "callbacks", "distinct_frames", "last_command_seq"}
                and not key.endswith(("_diagnostics", "_timestamp"))}
    if isinstance(value, list):
        return [semantic_value(item) for item in value]
    return value


def workflow_changes(previous: dict | None, current: dict,
                     settlements: set[tuple]) -> list[WorkflowEventKind]:
    """Classify validated native facts; never manufacture a settlement."""
    kinds = []
    popup = (current.get("state") or {}).get("daily_popup") or {}
    old_popup = ((previous or {}).get("state") or {}).get("daily_popup") or {}
    popup_seq, old_popup_seq = popup.get("event_sequence"), old_popup.get("event_sequence", 0)
    if (type(popup_seq) is int and popup_seq > 0 and type(old_popup_seq) is int
            and popup_seq > old_popup_seq and (previous is not None or bool(popup.get("windows")))):
        kinds.append(WorkflowEventKind.REWARD_POPUP_APPEARED)
    campaign = current.get("campaign") or {}
    attempt = campaign.get("attempt") or {}
    if (attempt.get("status") == "SETTLED" and attempt.get("attempt_id")
            and attempt.get("character_id") == campaign.get("character_id")
            and (attempt.get("report_id") not in (None, "", "0") or
                 current.get('workflow_scope') == 'challenge' and attempt.get('report_id') == '0' and attempt.get('outcome') == 'LOSS')
            and attempt.get("outcome") in ("WIN", "LOSS")
            and valid_settlement_evidence(attempt["outcome"], attempt.get("evidence"), challenge=current.get('workflow_scope') == 'challenge')):
        key = (current["context_key"], attempt["attempt_id"], attempt["report_id"])
        if key not in settlements:
            settlements.add(key)
            kinds.append(WorkflowEventKind.BATTLE_SETTLED)
    if previous is None or semantic_value(previous) != semantic_value(current):
        kinds.append(WorkflowEventKind.STATE_CHANGED)
    if previous is None or previous.get("connection") != current.get("connection"):
        kinds.append(WorkflowEventKind.CONNECTION_CHANGED)
    old_campaign = (previous or {}).get("campaign") or {}
    story_keys = ("phase", "tutorial", "flow", "general_tutorial", "result_kind", "result_navigation")
    if any(semantic_value(old_campaign.get(key)) != semantic_value(campaign.get(key))
           for key in story_keys):
        kinds.append(WorkflowEventKind.STORY_CHANGED)
    development = current.get("development") or {}
    old_development = (previous or {}).get("development") or {}
    for key, kind in (("growth", WorkflowEventKind.GROWTH_TASK_CHANGED),
                      ("equipment", WorkflowEventKind.EQUIPMENT_CHANGED),
                      ("tribute", WorkflowEventKind.TRIBUTE_CHANGED),
                      ("star", WorkflowEventKind.STAR_CHANGED)):
        if semantic_value(old_development.get(key)) != semantic_value(development.get(key)):
            kinds.append(kind)
    return kinds


class WorkflowSubscription:
    """One owner's event stream; reads never perform bridge I/O.

    A private notifier keeps caller callbacks outside controller, transport and
    subscription locks. Closing retains already queued facts for a final drain.
    """
    def __init__(self, notify: Callable[[], None] | None = None) -> None:
        self._lock = threading.Lock()
        self._events = deque()
        self._settlements: set[tuple] = set()
        self._latest: dict = {}
        self._closed = False
        self._notify = notify
        self._wake = threading.Event()
        if notify is not None:
            threading.Thread(target=self._notify_loop, name="workflow-notify", daemon=True).start()

    @property
    def closed(self) -> bool:
        with self._lock:
            return self._closed

    def _publish(self, event: WorkflowEvent) -> None:
        with self._lock:
            if self._closed:
                return
            if event.kind == WorkflowEventKind.BATTLE_SETTLED:
                attempt = (event.snapshot.get("campaign") or {}).get("attempt") or {}
                key = (event.context_key, attempt.get("attempt_id"), attempt.get("report_id"))
                if key in self._settlements:
                    return
                self._settlements.add(key)
            detached = copy.deepcopy(event)
            self._latest = detached.snapshot
            self._events.append(detached)
        self._wake.set()

    def _update_latest(self, snapshot: dict) -> None:
        with self._lock:
            if not self._closed:
                self._latest = copy.deepcopy(snapshot)

    def _notify_loop(self) -> None:
        while True:
            self._wake.wait()
            self._wake.clear()
            with self._lock:
                closed = self._closed
            try:
                self._notify()
            except Exception:
                # Notification is a hint; a failing consumer cannot destroy facts.
                pass
            if closed:
                return

    def drain(self) -> list[WorkflowEvent]:
        with self._lock:
            result = list(self._events)
            self._events.clear()
        return copy.deepcopy(result)

    def latest(self) -> dict:
        with self._lock:
            return copy.deepcopy(self._latest)

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
        self._wake.set()
