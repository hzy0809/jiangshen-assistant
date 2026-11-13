"""Run supported daily activities under an already-owned game connection."""
from __future__ import annotations

from dataclasses import asdict, dataclass, replace
import json
import math
import os
from pathlib import Path
import threading
import time
import uuid

from .account_profiles import RETIRED_DAILY_KEYS, SHOP_DURATIONS
from .daily import valid_observation
from .development import DailySpiritTribute, TributeTarget
from .development_task import DevelopmentWorkflow, WorkflowResult
from .daily_guild import DailyGuildExecutor
from .daily_reward import DailyRewardExecutor
from .daily_timed import DailyTimedExecutor
from .daily_stone import DailyStoneExecutor
from .daily_mingshi import DailyMingshiExecutor, _snapshot_valid as valid_mingshi_snapshot
from .account_recovery_store import validate_daily_retry_evidence
from .daily_refine import DailyRefineExecutor
from .daily_arena_chest import DailyArenaChestExecutor
from .soul_executor import SoulExecutor
from .resource_policy import soul_paid_allowed
from .daily_wood import DailyWoodExecutor, quota_epoch as wood_quota_epoch
from .daily_arena_battle import DailyArenaBattleExecutor
from .daily_point_race import DailyPointRaceExecutor
from .daily_point_chest import DailyPointRaceChestExecutor
from .daily_flower import DailyFlowerExecutor
from .daily_monster import DailyMonsterExecutor
from .daily_city_gather import DailyCityGatherExecutor
from .daily_city_shop import DailyCityShopExecutor, _snapshot as valid_city_shop_snapshot
from .daily_online_gift import DailyOnlineGiftExecutor, DailyOnlineGiftResult
from .daily_trial import DailyTrialExecutor
from .daily_training_monster import DailyTrainingMonsterExecutor
from .daily_grave import DailyGraveExecutor
from .daily_divinity import DailyDivinityExecutor
from .daily_sign_salary import DailySignPageRewardsExecutor, DailySignSalaryExecutor
from .daily_event_rewards import DailyEventRewardsExecutor
from .daily_copy import DailyTechBookCopyExecutor
from .daily_popup import DailyPopupHandler
from .home_navigation import ensure_home_scene


_TRIBUTE_TASK = (2, 6)
_OBSERVED_COMPLETE = {'ALREADY_COMPLETE', 'ALREADY_CLAIMED', 'COMPLETED'}
_HANDLED_OUTCOMES = _OBSERVED_COMPLETE | {'NO_FREE_REWARD', 'CONDITION_NOT_MET', 'PROCESSED'}
_COHERENCE_TASKS = frozenset((3, *range(5, 19), *range(20, 25)))
# Active non-wealth activities in the pinned game template. Missing rows are
# reported as unobserved; the server may legitimately hide a locked task.
_EXPECTED_TASKS = {
    2: {5: '聚魂', 6: '培养灵兽', 7: '收获铭石', 8: '击败天神', 16: '采石', 17: '地宫探宝', 21: '炼魂'},
    3: {9: '竞技对战', 11: '送花献礼', 12: '参加答题', 22: '木人试炼'},
    4: {10: '协助好友降妖', 14: '名将试炼', 15: '主城降妖', 23: '消灭训练妖魔'},
    5: {18: '同盟抽奖', 20: '同盟捐献'},
    6: {3: '领取竞技宝箱', 13: '参加比武大会', 24: '击败复仇魔王'},
}

# These are business routes, not server activity IDs. None retains the original
# standalone daily plan; an explicit set admits only these selected routes.
DAILY_ITEM_KEYS = frozenset((
    'sign', 'vip', 'events', 'online_reward', 'limited', 'copy', 'tribute', 'stone',
    'divinity', 'mingshi', 'soul', 'refine', 'wood',
    'guild_lottery', 'guild_donation', 'grave', 'arena', 'point_race', 'flower',
    'friend_monster', 'trial', 'home_monster', 'revenge', 'quiz', 'brawl',
    'training', 'category_rewards', 'spirit_tree', 'city_shop', 'little_demon', 'online_gift',
))
DAILY_ITEM_DEPENDENCIES = {
    'mingshi': frozenset(('divinity',)),
}
_CONTEXT_RETRY_ROWS = {'guild_donation': (5, 20), 'divinity': (2, 8), 'mingshi': (2, 7)}
SELECTED_MODE_UNAVAILABLE_ITEMS = {}
_ITEM_KEYS = {
    (2, 5): 'soul', (2, 6): 'tribute', (2, 7): 'mingshi', (2, 8): 'divinity',
    (2, 16): 'stone', (2, 17): 'grave', (2, 21): 'refine',
    (3, 9): 'arena', (3, 11): 'flower', (3, 12): 'quiz', (3, 22): 'wood',
    (3, 109): 'point_race', (4, 10): 'friend_monster', (4, 14): 'trial',
    (4, 15): 'home_monster', (4, 23): 'training', (5, 18): 'guild_lottery',
    (5, 20): 'guild_donation', (6, 3): 'arena_chest', (6, 13): 'brawl',
    (6, 24): 'revenge', (7, 301): 'sign', (7, 302): 'salary', (7, 202): 'vip',
    (7, 1302): 'sign', (7, 1303): 'sign', (7, 1304): 'sign',
    (7, 201): 'events', (7, 203): 'limited', (7, 303): 'copy',
    (7, 1201): 'online_reward', (7, 1246): 'events',
    (7, 306): 'spirit_tree', (7, 307): 'little_demon', (7, 308): 'city_shop',
    (7, 309): 'online_gift',
}


def normalize_selected_items(selected_items):
    """Freeze the caller's selection before ownership or async work starts."""
    if selected_items is None:
        return None
    if (not isinstance(selected_items, (set, frozenset))
            or any(not isinstance(key, str) or key not in DAILY_ITEM_KEYS | RETIRED_DAILY_KEYS for key in selected_items)):
        raise ValueError('daily_selected_items_invalid')
    return frozenset(selected_items) - RETIRED_DAILY_KEYS


def normalize_tribute_options(tribute_options):
    """Validate and freeze the explicit account cultivation limits."""
    if tribute_options is None or isinstance(tribute_options, TributeTarget):
        return tribute_options
    if (not isinstance(tribute_options, dict)
            or set(tribute_options) - {'max_requests', 'use_all', 'target_level'}):
        raise ValueError('daily_tribute_options_invalid')
    try:
        return TributeTarget(**tribute_options)
    except (TypeError, ValueError) as exc:
        raise ValueError('daily_tribute_options_invalid') from exc


def normalize_enabled_items(enabled_items, selected_items):
    """Full account configuration supplies dependencies, never execution scope."""
    enabled_items = normalize_selected_items(enabled_items)
    if enabled_items is not None and (selected_items is None or not selected_items <= enabled_items):
        raise ValueError('daily_selected_items_not_enabled')
    return enabled_items


def _selection_reason(name, selected_items, enabled_items=None):
    if name in RETIRED_DAILY_KEYS:
        return 'task_not_enabled'
    if selected_items is None:
        return ''
    if name not in selected_items:
        return 'task_not_enabled'
    if name in SELECTED_MODE_UNAVAILABLE_ITEMS:
        return SELECTED_MODE_UNAVAILABLE_ITEMS[name]
    missing = DAILY_ITEM_DEPENDENCIES.get(name, frozenset()) - (
        selected_items if enabled_items is None else enabled_items)
    return 'task_dependency_not_enabled:' + ','.join(sorted(missing)) if missing else ''


def _select_item(key, item, selected_items, *, tribute_configured=True, enabled_items=None):
    name = 'category_rewards' if key[1] is None else _ITEM_KEYS.get(key)
    reason = _selection_reason(name, selected_items, enabled_items)
    if not reason and selected_items is not None and name == 'tribute' and not tribute_configured:
        reason = 'daily_tribute_options_not_configured'
    if reason:
        return replace(item, status='SKIPPED' if reason == 'task_not_enabled' else 'UNAVAILABLE', reason=reason)
    return item


def _refresh_cycle(observed):
    """The server refreshes daily activity at 05:00 on its own clock."""
    day, now = observed['server_day'], observed['server_now']
    return day + 18000 if now >= day + 18000 else day - 86400 + 18000


@dataclass(frozen=True)
class DailyItemResult:
    kind: str
    category_id: int
    task_id: int | None
    name: str
    status: str
    reason: str
    current: int | None
    required: int | None
    money_current: int | None = None
    money_limit: int | None = None
    reputation_current: int | None = None
    reputation_limit: int | None = None
    soul_blue_completed: int | None = None
    soul_purple_completed: int | None = None
    soul_skipped: tuple[tuple[str, str], ...] = ()
    checkin_state_observed: bool = False
    checkin_today: int | None = None
    checkin_game_day: int | None = None
    checkin_request_attempted: bool | None = None
    checkin_cleanup: str = ''
    template_active: bool | None = None
    ui_button_id: int | None = None
    ui_button_unlocked: bool | None = None
    entry_unlocked: bool | None = None
    checkin_initial_today: int | None = None
    checkin_initial_times: int | None = None
    checkin_final_times: int | None = None
    checkin_native_request_count: int | None = None
    checkin_native_opcode: int | None = None
    checkin_operation_id: str = ''
    sign_reward_initial_availability: str = ''
    sign_reward_availability: str = ''
    sign_reward_request_count: int | None = None
    sign_reward_callback_returned: bool | None = None
    sign_reward_confirmed: bool | None = None
    sign_reward_receipts: tuple[dict, ...] = ()
    evidence_path: str = ''
    cleanup: str = ''
    cleanup_pending: bool = False
    operation_id: str = ''
    processed_without_reply: bool = False
    page_confirmed: bool = False
    request_issued: bool = False
    reply_received: bool = False
    continuation_safe: bool = False
    got_before: int | None = None
    got_after: int | None = None
    mana_before: int | None = None
    mana_after: int | None = None
    targets_initial: int | None = None
    targets_confirmed: int | None = None


@dataclass(frozen=True)
class DailyResult:
    status: str
    reason: str
    character_id: str = ''
    items: tuple[DailyItemResult, ...] = ()
    ledger_paths: tuple[str, ...] = ()
    popup_status: str = 'IDLE'
    popup_reason: str = ''
    cooldown_remaining_seconds: int | None = None
    requires_online: bool = False
    cooldown_items: tuple[tuple[str, int], ...] = ()
    cooldown_cleanup: str = ''
    cleanup_pending: bool = False
    route_retry_evidence: tuple[dict, ...] = ()
    cooldown_releases: tuple[tuple[str, str], ...] = ()
    cooldown_release_evidence: tuple[dict, ...] = ()
    cleanup_evidence: tuple[dict, ...] = ()
    processed_without_reply: bool = False


def _point_chest_item(step, previous=None):
    native = step.native_snapshot or {}
    view, operation = native.get('view') or {}, native.get('operation') or {}
    released = (step.cleanup == 'VERIFIED_CLOSED' and step.returned_home and step.chest_closed
                and step.release_verified and not step.cleanup_pending)
    if step.claim_verified:
        status = 'COMPLETED'
        reason = step.reason if released else 'point_chest_claimed_cleanup_pending'
    elif step.status == 'ALREADY_DONE' and released:
        status, reason = 'ALREADY_CLAIMED', step.reason
    elif step.status in ('UNKNOWN', 'STOPPED', 'BLOCKED', 'PARTIAL'):
        status, reason = step.status, step.reason
    else:
        status, reason = 'NO_FREE_REWARD' if released else 'UNAVAILABLE', step.reason
    count = view.get('eligible_count')
    availability = ('AVAILABLE' if type(count) is int and count > 0 else
                    'ALREADY_CLAIMED' if view.get('point_got') is True else 'CONDITION_NOT_MET')
    row = previous or DailyItemResult('sign_page_reward', 7, 1304, '积分宝箱',
                                     status, reason, None, None)
    return replace(row, name='积分宝箱', status=status, reason=reason,
        sign_reward_initial_availability=(row.sign_reward_initial_availability or
            ('AVAILABLE' if step.request_count else 'UNKNOWN')),
        sign_reward_availability=availability, sign_reward_request_count=step.request_count,
        sign_reward_callback_returned=step.stock_callback_returned,
        sign_reward_confirmed=step.claim_verified,
        sign_reward_receipts=tuple(dict(receipt) for receipt in operation.get('receipts', ())),
        evidence_path=step.evidence_path, cleanup=step.cleanup,
        cleanup_pending=not released and bool(step.operation_id), operation_id=step.operation_id,
        request_issued=step.request_count > 0, reply_received=step.stock_callback_returned,
        continuation_safe=released)


def _task_result(category, task, *, guild_enabled=False, timed_enabled=False, stone_enabled=False, mingshi_enabled=False,
                 refine_enabled=False, arena_enabled=False, soul_enabled=False, wood_enabled=False):
    key = category['category_id'], task['task_id']
    latest, required = task['latest_current'], task['required']
    if key[1] in _COHERENCE_TASKS and (task['reason'] == 'daily_progress_inconsistent' or latest is None
                          or task['current'] is None or latest != task['current']
                          or type(task['complete']) is not bool or task['complete'] != (latest >= required)):
        status, reason = 'UNAVAILABLE', 'daily_progress_inconsistent'
    elif task['reason'] in ('spirit_max_level', 'grave_progression_limit'):
        # Stock can locally fill these counters when the task closes; that
        # does not independently prove the activity was completed.
        status, reason = 'UNAVAILABLE', task['reason']
    elif latest is not None and latest >= required:
        status, reason = 'ALREADY_COMPLETE', 'daily_latest_progress_met'
    elif not category['visible'] or not task['visible'] or task['availability'] != 'available':
        status, reason = 'UNAVAILABLE', task['reason'] or 'daily_task_unavailable'
    elif latest is None:
        status, reason = 'UNAVAILABLE', 'daily_task_progress_unknown'
    elif guild_enabled and key in ((5, 20), (5, 18)):
        status, reason = 'PENDING', 'daily_guild_pending'
    elif timed_enabled and key in ((3, 12), (6, 13)):
        status, reason = 'PENDING', 'daily_timed_pending'
    elif stone_enabled and key == (2, 16):
        status, reason = 'PENDING', 'daily_stone_pending'
    elif mingshi_enabled and key == (2, 7):
        status, reason = 'PENDING', 'daily_mingshi_pending'
    elif refine_enabled and key == (2, 21):
        status, reason = 'PENDING', 'daily_refine_pending'
    elif arena_enabled and key == (6, 3):
        status, reason = 'PENDING', 'daily_arena_pending'
    elif soul_enabled and key == (2, 5):
        status, reason = 'PENDING', 'daily_soul_pending'
    elif wood_enabled and key == (3, 22):
        status, reason = 'PENDING', 'daily_wood_pending'
    elif key[1] in (8, 9, 10, 11, 14, 15, 17, 23, 24):
        status, reason = 'PENDING', 'daily_script_pending'
    elif key != _TRIBUTE_TASK:
        status, reason = 'UNSUPPORTED', 'daily_task_route_unsupported'
    elif latest is None or task['complete'] is not False:
        status, reason = 'UNAVAILABLE', 'daily_task_progress_unknown'
    else:
        status, reason = 'PENDING', 'daily_tribute_pending'
    return DailyItemResult('task', *key, task['name'], status, reason, latest, required)


def _items(observed, *, guild_enabled=False, timed_enabled=False, stone_enabled=False, mingshi_enabled=False,
           refine_enabled=False, arena_enabled=False, soul_enabled=False, wood_enabled=False, selected_items=None,
           tribute_configured=True, enabled_items=None):
    items = {}
    for category in observed['categories']:
        key = category['category_id']
        if key not in range(2, 7):
            continue
        if category['reward_claimed'] is True:
            status, reason = 'ALREADY_CLAIMED', 'daily_reward_claimed_observed'
        elif not category['visible']:
            status, reason = 'UNAVAILABLE', 'daily_category_hidden'
        else:
            status, reason = 'UNSUPPORTED', 'daily_reward_route_unsupported'
        items[key, None] = DailyItemResult('category_reward', key, None, category['name'],
            status, reason, category['current'], category['required'])
        for task in category['tasks']:
            if task['task_id'] == 19:  # Inactive in the pinned template.
                continue
            items[key, task['task_id']] = _task_result(category, task, guild_enabled=guild_enabled,
                                                        timed_enabled=timed_enabled, stone_enabled=stone_enabled,
                                                        mingshi_enabled=mingshi_enabled, refine_enabled=refine_enabled,
                                                        arena_enabled=arena_enabled, soul_enabled=soul_enabled,
                                                        wood_enabled=wood_enabled)
    missing_details = {(row['category_id'], row['task_id']): row
                       for row in observed.get('missing_tasks', [])}
    for category_id, tasks in _EXPECTED_TASKS.items():
        for task_id, name in tasks.items():
            key = category_id, task_id
            if key not in items:
                reason = 'daily_tribute_task_missing' if key == _TRIBUTE_TASK else 'daily_task_not_observed'
                detail = missing_details.get(key)
                if detail and not detail['template_active']:
                    reason = 'daily_activity_disabled'
                elif detail and detail.get('entry_unlocked') is False:
                    reason = 'daily_feature_locked'
                items[key] = DailyItemResult('task', category_id, task_id, name, 'UNAVAILABLE', reason, None, None,
                    template_active=detail.get('template_active') if detail else None,
                    ui_button_id=detail.get('ui_button_id') if detail else None,
                    ui_button_unlocked=detail.get('ui_unlocked') if detail else None,
                    entry_unlocked=detail.get('entry_unlocked') if detail else None)
    if selected_items is not None:
        for category_id in _EXPECTED_TASKS:
            key = category_id, None
            if key not in items:
                items[key] = DailyItemResult('category_reward', category_id, None, '日常奖励',
                    'UNAVAILABLE', 'daily_category_not_observed', None, None)
    return {key: _select_item(key, item, selected_items, tribute_configured=tribute_configured,
                              enabled_items=enabled_items)
            for key, item in items.items()}


def _refine_quota_complete(step, item, cycle):
    """Accept the ten-exchange quota only with evidence for each exchange."""
    if (step.status not in ('COMPLETED', 'ALREADY_COMPLETE') or step.cycle != cycle
            or step.mode != 'experience' or step.shared_exchange_limit != 10
            or step.checklist_complete is not True or step.remaining_exchanges != 0
            or step.progress_before != item.current or step.required != item.required
            or type(step.exchanges_completed) is not int or not 0 <= step.exchanges_completed <= 10
            or type(step.experience_exchanges_before) is not int
            or type(step.experience_exchanges_after) is not int
            or type(step.gold_exchanges_before) is not int or type(step.gold_exchanges_after) is not int
            or not 0 <= step.experience_exchanges_before <= 10
            or not 0 <= step.gold_exchanges_before <= 10
            or step.experience_exchanges_after + step.gold_exchanges_after != 10
            or step.experience_exchanges_after - step.experience_exchanges_before != step.exchanges_completed
            or step.gold_exchanges_before != step.gold_exchanges_after
            or step.progress_after != min(item.current + step.exchanges_completed, item.required)
            or step.coins_spent != 0 or step.ui_ownership_held is not False):
        return False
    if step.exchanges_completed == 0:
        return (step.status == 'ALREADY_COMPLETE' and step.cleanup == 'NOT_NEEDED'
                and not step.attempts
                and step.experience_spent == 0 and step.practice_credit == 0)
    if (step.status != 'COMPLETED' or step.cleanup != 'VERIFIED_CLOSED'
            or step.mutation_attempted is not True or not step.stock_callback_returned
            or not step.account_progress_observed):
        return False
    attempts = step.attempts
    if (len(attempts) != step.exchanges_completed
            or not step.operation_id or type(step.sequence) is not int
            or type(step.final_revision) is not int):
        return False
    count, current, spent, credited = step.experience_exchanges_before, item.current, 0, 0
    previous = None
    for attempt in attempts:
        if (attempt.status != 'COMPLETED' or attempt.cycle != cycle
                or attempt.mode != 'experience' or attempt.progress_before != current
                or attempt.required != item.required
                or attempt.progress_after != min(current + 1, item.required)
                or attempt.cleanup != 'PANEL_HELD' or attempt.ui_ownership_held is not True
                or attempt.mutation_attempted is not True or not attempt.stock_callback_returned
                or not attempt.account_progress_observed or attempt.coins_spent != 0
                or type(attempt.experience_spent) is not int or attempt.experience_spent < 0
                or type(attempt.practice_credit) is not int or attempt.practice_credit <= 0
                or type(attempt.experience_before) is not int or type(attempt.experience_after) is not int
                or attempt.experience_before - attempt.experience_after != attempt.experience_spent
                or type(attempt.practice_before) is not int or type(attempt.practice_after) is not int
                or attempt.practice_after - attempt.practice_before != attempt.practice_credit
                or attempt.experience_exchanges_before != count or attempt.experience_exchanges_after != count + 1
                or attempt.gold_exchanges_before != step.gold_exchanges_before
                or attempt.gold_exchanges_after != step.gold_exchanges_before
                or attempt.money_before != attempt.money_after
                or attempt.coin_exchanges_before != attempt.coin_exchanges_after
                or attempt.operation_id != step.operation_id
                or type(attempt.sequence) is not int or attempt.sequence <= 0
                or type(attempt.quote_revision) is not int or type(attempt.final_revision) is not int
                or attempt.final_revision <= attempt.quote_revision
                or previous is not None and (attempt.sequence <= previous.sequence
                    or attempt.quote_revision < previous.final_revision
                    or attempt.experience_before != previous.experience_after
                    or attempt.practice_before != previous.practice_after
                    or attempt.money_before != previous.money_after
                    or attempt.coin_exchanges_before != previous.coin_exchanges_after)):
            return False
        previous = attempt
        count, current = count + 1, attempt.progress_after
        spent += attempt.experience_spent
        credited += attempt.practice_credit
    return (previous is not None and step.sequence > previous.sequence
            and step.final_revision > previous.final_revision
            and step.quote_revision == previous.quote_revision
            and count == step.experience_exchanges_after and current == step.progress_after
            and spent == step.experience_spent and credited == step.practice_credit)


def _daily_soul_plan_complete(step, item):
    """Bind each free color to its own completed, zero-start-cost receipt."""
    if (step.progress_before != item.current or step.required != item.required
            or type(step.blue_completed) is not int or not 0 <= step.blue_completed <= 1
            or type(step.purple_completed) is not int or not 0 <= step.purple_completed <= 1
            or type(step.coins_spent) is not int or step.coins_spent < 0):
        return False
    count = step.blue_completed + step.purple_completed
    if (len(step.rounds) != count or step.progress_after != min(item.current + count, item.required)
            or count == 0 and (step.status != 'ALREADY_COMPLETE' or step.cleanup != 'NOT_NEEDED')
            or count > 0 and (step.status != 'COMPLETED' or step.cleanup != 'VERIFIED_CLOSED'
                or not step.operation_id or not step.start_verified or not step.harvest_verified
                or not step.account_progress_observed)):
        return False
    seen, progress, spent, sequence = set(), item.current, 0, 0
    for index, round_receipt in enumerate(step.rounds, 1):
        color = round_receipt.color
        if (color not in ('blue', 'purple') or color in seen or round_receipt.round != index
                or round_receipt.mode != (1 if color == 'blue' else 2)
                or round_receipt.start_cost != 0 or type(round_receipt.free_before) is not int
                or round_receipt.free_before < 1 or type(round_receipt.free_after) is not int
                or round_receipt.free_after != round_receipt.free_before - 1
                or round_receipt.progress_before != progress
                or round_receipt.progress_after != min(progress + 1, item.required)
                or round_receipt.start_verified is not True or round_receipt.harvest_verified is not True
                or type(round_receipt.coins_spent) is not int or round_receipt.coins_spent < 0
                or type(round_receipt.start_sequence) is not int or round_receipt.start_sequence <= sequence
                or type(round_receipt.settle_sequence) is not int
                or round_receipt.settle_sequence <= round_receipt.start_sequence):
            return False
        seen.add(color)
        progress = round_receipt.progress_after
        spent += round_receipt.coins_spent
        sequence = round_receipt.settle_sequence
    skipped = set()
    for entry in step.skipped:
        if (entry.color not in ('blue', 'purple') or entry.color in seen or entry.color in skipped
                or entry.reason not in ('soul_color_locked', 'soul_free_exhausted', 'soul_daily_color_done')):
            return False
        skipped.add(entry.color)
    return (seen | skipped == {'blue', 'purple'} and len(seen) == count
            and ('blue' in seen) == bool(step.blue_completed)
            and ('purple' in seen) == bool(step.purple_completed)
            and progress == step.progress_after and spent == step.coins_spent)


class _RouteCommandWitness:
    """Record actual route command attempts before entering the controller.

    A rejected or exceptional send still counts. Observations and constructing
    an executor do not imply that its game operation was admitted.
    """

    def __init__(self, objects):
        self.objects = objects
        self.command_attempts = 0
        self.executor_entered = False
        self.recovery_entered = False
        self.observation_failed = False

    def __getattr__(self, name):
        return getattr(self.objects, name)

    def submit_daily_script(self, *args, **kwargs):
        self.command_attempts += 1
        return self.objects.submit_daily_script(*args, **kwargs)

    def submit_daily_guild(self, *args, **kwargs):
        self.command_attempts += 1
        return self.objects.submit_daily_guild(*args, **kwargs)

    def submit_daily_mingshi(self, *args, **kwargs):
        self.command_attempts += 1
        return self.objects.submit_daily_mingshi(*args, **kwargs)

    def _observe(self, method, *args, **kwargs):
        try:
            return getattr(self.objects, method)(*args, **kwargs)
        except Exception:
            self.observation_failed = True
            raise

    def observe_daily_script(self, *args, **kwargs):
        return self._observe('observe_daily_script', *args, **kwargs)

    def observe_daily_guild(self, *args, **kwargs):
        return self._observe('observe_daily_guild', *args, **kwargs)

    def observe_daily_mingshi(self, *args, **kwargs):
        return self._observe('observe_daily_mingshi', *args, **kwargs)

    def observe_daily(self, *args, **kwargs):
        return self._observe('observe_daily', *args, **kwargs)


class DailyWorkflow:
    """Synchronous daily routes; the caller owns admission and cleanup.

    selected_items=None preserves the legacy plan. A set/frozenset is an
    explicit allowlist (including an empty plan); required selections are
    exposed in DAILY_ITEM_DEPENDENCIES. Selected reward routes carry their scope
    into the native model. A selection never supplies missing server progress.
    """

    def __init__(self, objects, token, data_dir, *, stopped=lambda: False,
                 paused=lambda: False, wait=None, publish=None, donation_type=2,
                 confirmation_timeout=5.0, clock=time.monotonic, guild_enabled=True,
                 rewards_enabled=True, timed_enabled=True,
                 stone_enabled=True, mingshi_enabled=True, refine_enabled=True, arena_enabled=True, soul_enabled=True,
                 wood_enabled=True, popup_subscription=None, selected_items=None,
                 defer_cooldowns=False, tribute_options=None, shop_duration=3600, cooldown_wait=None,
                 enabled_items=None, retry_source=None, popup_scope=None,
                 wait_for_gift=False, gift_wait_until=None, point_reward_only=False):
        if type(donation_type) is not int or donation_type not in (1, 2, 3):
            raise ValueError('daily_donation_type_unsupported')
        self.selected_items = normalize_selected_items(selected_items)
        self.enabled_items = normalize_enabled_items(enabled_items, self.selected_items)
        if (type(point_reward_only) is not bool or point_reward_only and
                (self.selected_items != frozenset(('sign',)) or self.enabled_items != self.selected_items
                 or retry_source is not None or wait_for_gift)):
            raise ValueError('point_chest_selection_invalid')
        self.point_reward_only = point_reward_only
        if type(wait_for_gift) is not bool:
            raise ValueError('daily_wait_for_gift_invalid')
        if wait_for_gift:
            if (self.selected_items != frozenset(('online_gift',)) or self.enabled_items != self.selected_items
                    or retry_source is not None or type(gift_wait_until) not in (int, float)
                    or not math.isfinite(gift_wait_until) or gift_wait_until > clock() + 1200.0):
                raise ValueError('daily_wait_for_gift_scope_invalid')
        elif gift_wait_until is not None:
            raise ValueError('daily_wait_for_gift_deadline_without_mode')
        self.wait_for_gift, self.gift_wait_until = wait_for_gift, gift_wait_until
        self.popup_scope = uuid.uuid4().hex if popup_scope is None else popup_scope
        if (not isinstance(self.popup_scope, str) or len(self.popup_scope) != 32
                or any(char not in '0123456789abcdef' for char in self.popup_scope)):
            raise ValueError('daily_popup_scope_invalid')
        self.retry_source = None
        if retry_source is not None:
            if self.selected_items is None or len(self.selected_items) != 1:
                raise ValueError('daily_retry_selection_invalid')
            route = next(iter(self.selected_items))
            if route not in ('guild_donation', 'divinity', 'mingshi'):
                raise ValueError('daily_retry_selection_invalid')
            self.retry_source = validate_daily_retry_evidence(retry_source, route=route, expected_account_run=self.popup_scope)
            if self.retry_source['proof_kind'] != 'untouched_native_route':
                raise ValueError('daily_retry_source_invalid')
        if type(defer_cooldowns) is not bool:
            raise ValueError('daily_defer_cooldowns_invalid')
        self.defer_cooldowns = defer_cooldowns
        self.tribute_options = normalize_tribute_options(tribute_options)
        if type(shop_duration) is not int or shop_duration not in SHOP_DURATIONS:
            raise ValueError('daily_shop_duration_invalid')
        self.shop_duration = shop_duration
        self.objects, self.token, self.data_dir = objects, token, data_dir
        self.stopped, self.paused, self._base_wait = stopped, paused, wait
        self._cooldown_wait = cooldown_wait or threading.Event().wait
        self._cooldown_resumed = False
        self._workflow_event_error = ''
        self._popup_subscription = popup_subscription
        self._popup_pending = True
        self._popup_status, self._popup_reason = 'IDLE', ''
        self._popup = DailyPopupHandler(objects, token, on_appearance=self._popup_appeared, attempt_scope=self.popup_scope)
        self.wait = self._wait_with_popup_events
        self.publish = publish or (lambda result: None)
        self._donation_type = donation_type
        self.confirmation_timeout = confirmation_timeout
        self.clock = clock
        self.guild_enabled = guild_enabled
        self.rewards_enabled = rewards_enabled
        self.timed_enabled = timed_enabled
        self.stone_enabled = stone_enabled
        self.mingshi_enabled = mingshi_enabled
        self.refine_enabled = refine_enabled
        self.arena_enabled = arena_enabled
        self.soul_enabled = soul_enabled
        self.wood_enabled = wood_enabled

    @property
    def donation_type(self) -> int:
        return self._donation_type

    def _route_enabled(self, name):
        if self.selected_items is not None and name == 'tribute' and self.tribute_options is None:
            return False
        return not _selection_reason(name, self.selected_items, self.enabled_items)

    def _popup_appeared(self, _event):
        self._popup_pending = True

    def _drain_popup_events(self):
        if self._popup_subscription is not None:
            for event in self._popup_subscription.drain():
                self._popup.observe_snapshot(event.snapshot)
                connection = event.snapshot.get('connection') or {}
                error = event.snapshot.get('error') or connection.get('error')
                if error:
                    self._workflow_event_error = str(error)

    def _wait_with_popup_events(self):
        if self._base_wait is None:
            time.sleep(.1)
        else:
            self._base_wait()
        self._drain_popup_events()

    def _wait_for_cooldown(self, timeout):
        """Wait for control/events without borrowing the active-action poll."""
        self._cooldown_wait(min(60.0, max(0.0, timeout)))
        self._drain_popup_events()
        if self._workflow_event_error:
            raise RuntimeError('daily_cooldown_connection_failed (' + self._workflow_event_error + ')')

    def _service_popups(self, *, force=False):
        """Service stock presentation closes on this worker's safe points.

        DailyPopupModel admits only root restoration / EnableAll / the two
        stock closeBonus callbacks that call EnableAll. It never admits a
        claim, purchase, campaign result, or arbitrary confirmation callback.
        These non-consuming closes are also safe with an explicit selection.
        """
        self._drain_popup_events()
        if self.stopped() or self.paused() or not (force or self._popup_pending):
            return
        for _ in range(8):
            try:
                result = self._popup.service(admission=not self.stopped() and not self.paused())
            except Exception as exc:
                self._popup_status, self._popup_reason = 'UNKNOWN', f'popup_observation_failed ({exc})'
                self._popup_pending = False
                return
            status = result.get('status', 'UNKNOWN')
            self._popup_status, self._popup_reason = status, result.get('reason', '')
            if status == 'HANDLED':
                continue
            # Owned/loading windows and a pause racing admission remain pending
            # until a later safe point; unknown or paid choices are not clicked.
            self._popup_pending = status in ('WAITING', 'PAUSED')
            return
        self._popup_status, self._popup_reason = 'UNKNOWN', 'popup_handling_bound_reached'
        self._popup_pending = False

    def _sign_wait(self):
        self._wait_with_popup_events()
        self._service_popups()

    def _observe(self, character_id='', cycle=None, previous_time=None):
        try:
            observed = self.objects.observe_daily(self.token)
        except Exception as exc:
            return None, f'daily_observation_failed ({exc})'
        if not valid_observation(observed):
            return None, 'daily_observation_invalid'
        if not observed['available']:
            return None, observed['reason']
        if character_id and observed['character_id'] != character_id:
            return None, 'daily_character_changed'
        if previous_time is not None and observed['server_now'] < previous_time:
            return None, 'daily_server_clock_reversed'
        if cycle is not None and _refresh_cycle(observed) != cycle:
            return None, 'daily_refresh_cycle_changed'
        return observed, ''

    def _wait_for_admission(self, *, cooldown=False):
        while self.paused() and not self.stopped():
            if cooldown:
                self._cooldown_resumed = True
                self._wait_for_cooldown(60.0)
            elif self.wait is None:
                time.sleep(.1)
            else:
                self.wait()
        if self.stopped():
            return False
        self._service_popups()
        return not self.stopped()

    def _persist_city_result(self, task_id, character_id, step):
        """Keep the bounded receipt when a city action was submitted."""
        operation_id = step.operation_id
        if not operation_id:
            if step.mutation_attempted:
                raise ValueError('city_operation_identity_missing')
            return ''
        if uuid.UUID(operation_id).hex != operation_id:
            raise ValueError('city_operation_identity_invalid')
        directory = Path(self.data_dir) / 'city_activity_runs'
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / (operation_id + '.json')
        payload = json.dumps({'schema_version': 1, 'task_id': task_id,
                              'character_id': character_id, 'result': asdict(step)},
                             ensure_ascii=False, allow_nan=False, indent=2) + '\n'
        if len(payload.encode('utf-8')) > 2 * 1024 * 1024:
            raise ValueError('city_receipt_too_large')
        with path.open('x', encoding='utf-8', newline='\n') as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        return str(path)

    def _untouched_route_evidence(self, character_id, cycle, previous_time, items, witnesses):
        """Record nonexecution independently of whether the old page is clean."""
        if (not self.defer_cooldowns or self.selected_items is None or self.stopped() or self.paused()
                or self._workflow_event_error):
            return ()
        try:
            identity = json.loads(self.token.context_key)
            daily, _ = self._observe(character_id, cycle, previous_time)
            if (daily is None or not isinstance(identity, dict) or identity.get('character_id') != character_id
                    or identity.get('generation') != self.token.generation):
                return ()
        except Exception:
            return ()
        evidence = []
        for route, key in _CONTEXT_RETRY_ROWS.items():
            witness, item = witnesses[route], items.get(key)
            if (route not in self.selected_items or item is None or item.status not in ('UNAVAILABLE', 'PARTIAL')
                    or witness.command_attempts != 0 or witness.observation_failed):
                continue
            try:
                if route == 'guild_donation':
                    raw = witness.observe_daily_guild(self.token)
                    if (raw.get('character_id') != character_id or raw.get('operation') is not None
                            or raw.get('gate') == 'UNKNOWN' or raw.get('guild_id') in (None, '', '0')
                            or raw.get('retry_unstarted') is not True):
                        continue
                    proof = dict(revision=raw['revision'], role_id=raw['character_id'], refresh_cycle=cycle,
                                 task_id=20, guild_id=raw['guild_id'], operation_absent=True, retry_unstarted=True)
                elif route == 'divinity':
                    raw = witness.observe_daily_script(self.token, 8)
                    view, op = raw['view'], raw['operation']
                    if (view.get('task_id') != 8 or view.get('role_id') != character_id
                            or view.get('refresh_cycle') != cycle or view.get('retry_unstarted') is not True
                            or op.get('id') != '' or op.get('status') != 'IDLE'
                            or type(op.get('sequence')) is not int or op['sequence'] != 0
                            or op.get('mutation_attempted') is not False or op.get('stock_callback_returned') is not False):
                        continue
                    proof = dict(revision=raw['revision'], role_id=character_id, refresh_cycle=cycle, task_id=8,
                                 operation_id=op['id'], sequence=op['sequence'], status=op['status'],
                                 mutation_attempted=op['mutation_attempted'], stock_callback_returned=op['stock_callback_returned'],
                                 retry_unstarted=True)
                else:
                    observed = witness.observe_daily_mingshi(self.token)
                    raw = observed.get('mingshi')
                    if observed.get('enabled') is not True or not valid_mingshi_snapshot(raw):
                        continue
                    view, op = raw['view'], raw['operation']
                    if (view['role_id'] != character_id or view['cycle'] != cycle or op['status'] != 'IDLE'
                            or op['operation_id'] != '' or op['sequence'] != 0
                            or any(op[name] is not False for name in ('mutation_attempted', 'stock_callback_returned',
                                                                    'account_progress_observed', 'panel_closed'))):
                        continue
                    proof = dict(revision=raw['revision'], role_id=character_id, refresh_cycle=cycle, task_id=7,
                                 **{name: op[name] for name in ('operation_id', 'sequence', 'status', 'mutation_attempted',
                                     'stock_callback_returned', 'account_progress_observed', 'panel_closed')})
                if witness.observation_failed or witness.command_attempts:
                    continue
                evidence.append(dict(schema_version=1, route=route, proof_kind='untouched_native_route',
                    operation_admitted=False, queue_started=False, gui_invoked=False, mutation_started=False,
                    executor_entered=witness.executor_entered, recovery_entered=False, workflow_command_attempts=0,
                    cleanup='NOT_NEEDED', boundary='ROUTE_UNTOUCHED', coordinator_released=False,
                    character_id=character_id, user_id=identity['user_id'], zone_id=identity['zone_id'],
                    context_key=self.token.context_key, run_id=self.token.run_id, generation=self.token.generation,
                    refresh_cycle=cycle, server_now=daily['server_now'], proof=proof, boundary_proof={}))
            except Exception:
                continue
        return tuple(evidence)

    def _retry_current_gate(self, character_id, cycle, witnesses):
        """After normal navigation, admit one source-bound fresh route attempt."""
        source = self.retry_source
        if source is None:
            return ''
        route = source['route']
        try:
            identity = json.loads(self.token.context_key)
            if (source['character_id'] != character_id or any(source[name] != identity.get(name)
                    for name in ('character_id', 'user_id', 'zone_id'))):
                return 'daily_retry_identity_changed'
            if source['refresh_cycle'] != cycle:
                return 'daily_retry_refresh_cycle_changed'
            if not self._route_enabled(route):
                return 'daily_retry_route_not_enabled'
            boundary = self.objects.observe_daily_script(self.token, 303)
            if (boundary.get('task_id') != 303 or boundary.get('role_id') != character_id
                    or boundary.get('refresh_cycle') != cycle or boundary.get('home_ready') is not True
                    or boundary.get('busy') is not False or boundary.get('cleanup_pending') is not False
                    or boundary.get('status') in ('UNKNOWN', 'STOPPED')):
                return 'daily_retry_clean_home_unverified'
            witness = witnesses[route]
            if route == 'guild_donation':
                raw = witness.observe_daily_guild(self.token)
                op = raw.get('operation')
                ready = (raw.get('character_id') == character_id and raw.get('gate') == 'PASS'
                         and raw.get('phase') == 'MAIN' and 'open' in raw.get('actions', ())
                         and raw.get('retry_unstarted') is True and op is None)
            elif route == 'divinity':
                raw = witness.observe_daily_script(self.token, 8)
                view = raw.get('view') or {}
                ready = (view.get('role_id') == character_id and view.get('refresh_cycle') == cycle
                         and view.get('retry_unstarted') is True
                         and (view.get('next_action') == 'open' or
                              view.get('clearance_complete') is True and view.get('killed_mask') == 31))
            else:
                observed = witness.observe_daily_mingshi(self.token)
                raw = observed.get('mingshi')
                ready = observed.get('enabled') is True and valid_mingshi_snapshot(raw)
                if ready:
                    view, op = raw['view'], raw['operation']
                    ready = (view['role_id'] == character_id and view['cycle'] == cycle
                             and op['status'] == 'IDLE' and view['current'] == 0 and view['required'] == 1
                             and view['next_action'] == 'open' and view['reason'] == '' and view['gold_cost'] is None)
                if ready:
                    ready = DailyDivinityExecutor(witness, self.token, stopped=self.stopped).observe_harvest_clearance(
                        character_id=character_id, expected_cycle=cycle).clearance_complete
            return '' if ready and not witness.command_attempts and not witness.observation_failed else 'daily_retry_current_offer_unavailable'
        except Exception:
            return 'daily_retry_current_observation_unverified'

    def _route_retry_evidence(self, character_id, cycle, previous_time, items, witnesses):
        """Prove a narrow, untouched route can receive one later fresh attempt.

        This does not change a PARTIAL result or claim eligibility. The
        coordinator must still validate release before exposing the evidence.
        """
        untouched = self._untouched_route_evidence(character_id, cycle, previous_time, items, witnesses)
        if (not self.defer_cooldowns or self.selected_items is None or self.stopped() or self.paused()
                or self._workflow_event_error or self._popup_status not in ('IDLE', 'HANDLED')):
            return untouched

        def fields(value, names):
            if not isinstance(value, dict) or any(name not in value for name in names):
                return None
            return {name: value[name] for name in names}

        def positive(value):
            return type(value) is int and value > 0

        def numeric_id(value):
            return (isinstance(value, str) and value.isascii() and value.isdecimal()
                    and not value.startswith('0'))

        try:
            identity = json.loads(self.token.context_key)
            if (not isinstance(identity, dict) or identity.get('character_id') != character_id
                    or set(identity) != {'pid', 'creation', 'nonce', 'generation',
                                         'character_id', 'user_id', 'zone_id'}
                    or any(not numeric_id(identity.get(key)) for key in ('character_id', 'user_id', 'zone_id'))
                    or not positive(identity['pid']) or not positive(identity['creation'])
                    or not isinstance(identity['nonce'], str) or not identity['nonce']
                    or identity['generation'] != self.token.generation
                    or not positive(self.token.generation) or not isinstance(self.token.run_id, str)
                    or uuid.UUID(self.token.run_id).hex != self.token.run_id):
                return untouched
            daily, _ = self._observe(character_id, cycle, previous_time)
            if daily is None:
                return untouched
            boundary = fields(self.objects.observe_daily_script(self.token, 303), (
                'task_id', 'revision', 'role_id', 'refresh_cycle', 'home_ready', 'busy',
                'cleanup_pending', 'status', 'operation_id', 'closed',
                'mutation_attempted', 'gui_invocation_attempted'))
            if (boundary is None or boundary['task_id'] != 303 or not positive(boundary['revision'])
                    or boundary['role_id'] != character_id or boundary['refresh_cycle'] != cycle
                    or boundary['home_ready'] is not True or boundary['busy'] is not False
                    or boundary['cleanup_pending'] is not False or boundary['status'] in ('UNKNOWN', 'STOPPED')
                    or type(boundary['closed']) is not bool
                    or type(boundary['mutation_attempted']) is not bool
                    or type(boundary['gui_invocation_attempted']) is not bool
                    or not isinstance(boundary['operation_id'], str)
                    or (boundary['operation_id'] == '' and (boundary['mutation_attempted']
                                                            or boundary['gui_invocation_attempted']))
                    or (boundary['operation_id'] != '' and boundary['closed'] is not True)):
                return untouched
        except Exception:
            return untouched

        evidence = []
        for route, key in (('arena', (3, 9)), ('training', (4, 23)),
                           ('trial', (4, 14)), ('point_race', (3, 109)),
                           ('copy', (7, 303)), ('city_shop', (7, 308))):
            witness = witnesses[route]
            item = items.get(key)
            if (route not in self.selected_items or item is None
                    or item.status not in ('UNAVAILABLE', 'PARTIAL') or witness.command_attempts != 0):
                continue
            proof = None
            try:
                raw = self.objects.observe_daily_script(self.token, key[1])
                if route in ('arena', 'training', 'trial'):
                    rows = [task for category in daily['categories']
                            for task in category['tasks'] if task['task_id'] == key[1]]
                    if rows or witness.executor_entered:
                        continue
                    if route == 'arena':
                        native_view = raw.get('view') or {}
                        operation = raw.get('operation') or {}
                        native = fields(operation, ('sequence', 'status', 'mutation_attempted', 'busy', 'closed'))
                        if (native is None or not positive(raw.get('revision'))
                                or native_view.get('role_id') != character_id
                                or native_view.get('refresh_cycle') != cycle or operation.get('id') != ''
                                or type(native['sequence']) is not int or native['sequence'] != 0
                                or native['mutation_attempted'] is not False or native['busy'] is not False
                                or type(native['closed']) is not bool
                                or type(native_view.get('home_ready')) is not bool
                                or native['status'] in ('UNKNOWN', 'STOPPED')):
                            continue
                        native.update(revision=raw['revision'], role_id=native_view['role_id'],
                                      refresh_cycle=native_view['refresh_cycle'], operation_id=operation['id'],
                                      home_ready=native_view['home_ready'])
                    elif route == 'training':
                        native = fields(raw, ('revision', 'task_id', 'role_id', 'refresh_cycle',
                            'operation_id', 'status', 'mutation_attempted', 'busy', 'cleanup_pending',
                            'training_scene', 'home_ready'))
                        if (native is None or not positive(native['revision']) or native['task_id'] != 23
                                or native['role_id'] != character_id or native['refresh_cycle'] != cycle
                                or native['operation_id'] != '' or native['mutation_attempted'] is not False
                                or native['busy'] is not False or native['cleanup_pending'] is not False
                                or native['training_scene'] is not False or type(native['home_ready']) is not bool
                                or native['status'] in ('UNKNOWN', 'STOPPED')):
                            continue
                    else:
                        native = fields(raw, ('revision', 'task_id', 'role_id', 'refresh_cycle',
                            'operation_id', 'status', 'mutation_attempted', 'busy', 'cleanup_pending',
                            'home_ready', 'release_verified', 'entry_request_attempted',
                            'battle_request_attempted', 'resumed_existing_run'))
                        if (native is None or not positive(native['revision']) or native['task_id'] != 14
                                or native['role_id'] != character_id or native['refresh_cycle'] != cycle
                                or native['operation_id'] != '' or native['status'] in ('UNKNOWN', 'STOPPED')
                                or type(native['home_ready']) is not bool
                                or type(native['release_verified']) is not bool
                                or any(native[name] is not False for name in ('mutation_attempted', 'busy',
                                    'cleanup_pending', 'entry_request_attempted', 'battle_request_attempted',
                                    'resumed_existing_run'))):
                            continue
                    proof_kind = 'daily_row_absent_no_command'
                    proof = {'category_id': key[0], 'task_id': key[1], 'daily_row_count': 0, 'native': native}
                elif route == 'point_race':
                    native_view = raw.get('view') or {}
                    operation = raw.get('operation') or {}
                    proof = fields(operation, ('sequence', 'status', 'mutation_attempted', 'busy',
                                               'cleanup_pending', 'closed'))
                    if (proof is None or not positive(raw.get('revision'))
                            or native_view.get('role_id') != character_id
                            or native_view.get('refresh_cycle') != cycle or operation.get('id') != ''
                            or type(proof['sequence']) is not int or proof['sequence'] != 0
                            or proof['status'] in ('UNKNOWN', 'STOPPED')
                            or any(proof[name] is not False for name in
                                   ('mutation_attempted', 'busy', 'cleanup_pending'))
                            or type(proof['closed']) is not bool
                            or type(native_view.get('home_ready')) is not bool
                            or native_view.get('observation_available') is not False
                            or witness.executor_entered is not True):
                        continue
                    proof.update(revision=raw['revision'], role_id=native_view['role_id'],
                                 refresh_cycle=native_view['refresh_cycle'], operation_id=operation['id'],
                                 home_ready=native_view['home_ready'],
                                 observation_available=native_view['observation_available'])
                    proof_kind = 'point_race_unavailable_no_command'
                elif route == 'copy':
                    proof = fields(raw, ('task_id', 'revision', 'role_id', 'refresh_cycle', 'entry_unlocked',
                        'operation_id', 'status', 'availability', 'gui_invocation_attempted',
                        'mutation_attempted', 'busy', 'cleanup_pending', 'closed', 'home_ready'))
                    if (proof is None or proof['task_id'] != 303 or not positive(proof['revision'])
                            or proof['role_id'] != character_id or proof['refresh_cycle'] != cycle
                            or proof['entry_unlocked'] is not False or proof['operation_id'] != ''
                            or proof['status'] != 'CONDITION_NOT_MET' or proof['availability'] != 'CONDITION_NOT_MET'
                            or any(proof[name] is not False for name in
                                   ('gui_invocation_attempted', 'mutation_attempted', 'busy', 'cleanup_pending'))
                            or type(proof['closed']) is not bool or proof['home_ready'] is not True
                            or witness.executor_entered is not True):
                        continue
                    proof_kind = 'copy_locked_no_command'
                else:
                    if not valid_city_shop_snapshot(raw):
                        continue
                    proof = fields(raw, ('schema', 'task_id', 'revision', 'role_id', 'cycle', 'status',
                        'operation_id', 'sequence', 'observation_available', 'gui_invocation_attempted',
                        'mutation_attempted', 'busy', 'cleanup_pending', 'reward_pending',
                        'stock_callback_returned', 'receipts', 'processed_candidates', 'collected_buildings',
                        'produced_buildings', 'gold_cost', 'mana_cost', 'money_received'))
                    if (proof is None or proof['role_id'] != character_id or proof['cycle'] != cycle
                            or proof['status'] != 'IDLE' or proof['operation_id'] != '' or proof['sequence'] != 0
                            or proof['observation_available'] is not True or proof['receipts']
                            or any(proof[name] is not False for name in ('gui_invocation_attempted',
                                'mutation_attempted', 'busy', 'cleanup_pending', 'reward_pending', 'stock_callback_returned'))
                            or any(proof[name] != 0 for name in ('processed_candidates', 'collected_buildings',
                                'produced_buildings', 'gold_cost', 'mana_cost', 'money_received'))
                            or witness.executor_entered is not True):
                        continue
                    proof_kind = 'city_shop_idle_no_command'
            except Exception:
                continue
            evidence.append({
                'schema_version': 1, 'route': route, 'proof_kind': proof_kind,
                'operation_admitted': False, 'queue_started': False, 'gui_invoked': False,
                'mutation_started': False, 'executor_entered': witness.executor_entered,
                'recovery_entered': witness.recovery_entered, 'workflow_command_attempts': witness.command_attempts,
                'cleanup': 'NOT_NEEDED', 'boundary': 'VERIFIED_CLEAN', 'coordinator_released': False,
                'character_id': character_id, 'user_id': identity['user_id'], 'zone_id': identity['zone_id'],
                'context_key': self.token.context_key, 'run_id': self.token.run_id,
                'generation': self.token.generation, 'refresh_cycle': cycle, 'server_now': daily['server_now'],
                'proof': proof, 'boundary_proof': dict(boundary),
            })
        return tuple(evidence) + untouched

    def _single_gift_wait(self, executor, character_id, cycle, previous_time, on_wait):
        """Keep one dispatch online; each minute admits a fresh, pinned read."""
        previous = DailyOnlineGiftResult('PARTIAL', 'online_gift_wait_limit')
        expected_tier = None
        next_read = self.clock()
        while True:
            now = self.clock()
            if self.stopped():
                return replace(previous, status='STOPPED', reason='user_stop')
            if now >= self.gift_wait_until:
                return replace(previous, status='PARTIAL', reason='online_gift_wait_limit')
            if self.paused():
                # Control wakes this wait promptly. Pausing never renews the
                # absolute budget or authorizes a background claim.
                self._wait_for_cooldown(min(1.0, self.gift_wait_until - now))
                continue
            if now < next_read:
                self._service_popups()
                if self._popup_status not in ('IDLE', 'HANDLED', 'WAITING', 'PAUSED'):
                    return replace(previous, status='UNKNOWN', reason='online_gift_wait_popup_unresolved')
                self._wait_for_cooldown(min(1.0, next_read - now, self.gift_wait_until - now))
                continue
            next_read = now + 60.0
            observed, reason = self._observe(character_id, cycle, previous_time)
            if observed is None:
                return replace(previous, status='UNKNOWN', reason=reason)
            previous_time = observed['server_now']
            if self.stopped() or self.paused():
                continue
            step = executor.execute(character_id=character_id, expected_cycle=cycle,
                                    expected_tier=expected_tier, absolute_deadline=self.gift_wait_until)
            # Only a closed, proven-unsent cooldown can admit another check.
            # A submitted claim, no reply, rejection, or unknown is terminal.
            if not (step.status == 'NO_FREE_REWARD' and step.reason == 'online_gift_current_tier_not_due'):
                return step
            if (step.cleanup != 'VERIFIED_CLOSED' or step.mutation_attempted is not False or step.receipts
                    or step.role_id != character_id or step.refresh_cycle != cycle
                    or type(step.got_before) is not int or step.got_before < 0
                    or step.got_after != step.got_before or step.tier != step.got_before + 1
                    or step.template_exists is not True or step.tier_due is not False):
                return replace(step, status='UNKNOWN', reason='online_gift_wait_evidence_incomplete')
            if expected_tier is None:
                expected_tier = step.tier
            elif step.tier != expected_tier:
                return replace(step, status='BLOCKED', reason='online_gift_target_tier_changed')
            if (type(step.server_now) is not int or step.server_now < previous_time
                    or type(step.next_get_time) is not int or step.next_get_time <= step.server_now):
                return replace(step, status='PARTIAL', reason='online_gift_deadline_unknown')
            remaining = step.next_get_time - step.server_now
            if self.clock() + remaining > self.gift_wait_until:
                return replace(step, status='PARTIAL', reason='online_gift_deadline_exceeds_wait_limit')
            previous_time, previous = step.server_now, step
            on_wait(step, remaining)

    def _point_chest(self, character_id, expected_cycle=None):
        return DailyPointRaceChestExecutor(self.objects, self.token,
            stopped=self.stopped, paused=self.paused, wait=self.wait, clock=self.clock,
            evidence_dir=Path(self.data_dir) / 'point-chest-attempts').execute(
                character_id=character_id, expected_cycle=expected_cycle)

    def _run_point_reward_only(self):
        character_id = json.loads(self.token.context_key)['character_id']
        step = self._point_chest(character_id)
        row = _point_chest_item(step)
        released = row.continuation_safe
        status = ('UNKNOWN' if not released and step.operation_id else
                  'DONE' if row.status in ('COMPLETED', 'ALREADY_CLAIMED') else
                  'STOPPED' if step.status == 'STOPPED' else
                  'UNKNOWN' if step.status == 'UNKNOWN' else 'PARTIAL')
        reason = row.reason
        witness = dict(route='point_reward', operation_id=step.operation_id,
            request_count=step.request_count, confirmed_count=step.confirmed_count,
            claim_verified=step.claim_verified, stock_callback_returned=step.stock_callback_returned,
            chest_closed=step.chest_closed, returned_home=step.returned_home,
            release_verified=step.release_verified, cleanup_pending=step.cleanup_pending,
            native_snapshot=step.native_snapshot, evidence_path=step.evidence_path)
        return DailyResult(status, reason, character_id, (row,),
            (step.evidence_path,) if step.evidence_path else (),
            cleanup_pending=not released and bool(step.operation_id), cleanup_evidence=(witness,))

    def run(self) -> DailyResult:
        if self.point_reward_only:
            return self._run_point_reward_only()
        items, ledger_paths, processed = {}, [], {}
        city_cleanup_evidence = []
        character_id = ''
        route_witnesses = {route: _RouteCommandWitness(self.objects)
                           for route in ('arena', 'training', 'trial', 'point_race', 'copy', 'city_shop',
                                         'guild_donation', 'divinity', 'mingshi')}

        def report(status, reason, *, cooldown_items=(), cooldown_cleanup='', cleanup_pending=False,
                   route_retry_evidence=(), cooldown_releases=(), cooldown_release_evidence=()):
            ordered = sorted(items.values(), key=lambda item: (item.category_id, item.task_id is not None,
                                                               item.task_id or 0))
            cleanup_pending = cleanup_pending or any(
                item.cleanup_pending or item.cleanup == 'UNVERIFIED' for item in ordered)
            result = DailyResult(status, reason, character_id, tuple(ordered), tuple(ledger_paths),
                                 self._popup_status, self._popup_reason,
                                 cooldown_remaining_seconds=min((seconds for _, seconds in cooldown_items), default=None),
                                 requires_online=bool(cooldown_items), cooldown_items=cooldown_items,
                                 cooldown_cleanup=cooldown_cleanup, cleanup_pending=cleanup_pending,
                                 route_retry_evidence=route_retry_evidence, cooldown_releases=cooldown_releases,
                                  cooldown_release_evidence=cooldown_release_evidence,
                                 cleanup_evidence=tuple(city_cleanup_evidence),
                                 processed_without_reply=any(item.status == 'PROCESSED' and item.processed_without_reply
                                                             for item in ordered))
            self.publish(result)
            return result

        if self.selected_items == frozenset():
            # No observation, popup close, or scene recovery is needed for an
            # empty account plan. No missing row is inferred as completed.
            if self.stopped():
                return report('STOPPED', 'user_stop')
            return report('DONE', 'daily_no_items_enabled')
        if not self._wait_for_admission():
            return report('STOPPED', 'user_stop')
        try:
            character_id = json.loads(self.token.context_key)['character_id']
            if not isinstance(character_id, str) or not character_id.isdigit() or int(character_id) == 0:
                raise ValueError('invalid_character')
        except (AttributeError, KeyError, TypeError, ValueError):
            return report('BLOCKED', 'daily_character_context_invalid')
        navigation = ensure_home_scene(self.objects, self.token, stopped=self.stopped,
            paused=self.paused, wait=self.wait, timeout=self.confirmation_timeout, clock=self.clock)
        if navigation.status not in ('NOT_NEEDED', 'COMPLETED'):
            return report(navigation.status, navigation.reason)
        if self._route_enabled('training'):
            route_witnesses['training'].recovery_entered = True
            recovery = DailyTrainingMonsterExecutor(
                route_witnesses['training'], self.token, stopped=self.stopped, paused=self.paused,
                wait=self.wait, clock=self.clock,
            ).recover_to_home(character_id=character_id)
            if recovery.status not in ('NOT_NEEDED', 'COMPLETED'):
                return report(recovery.status, recovery.reason)
        else:
            # resume_close calls SealMonsterUI._return, which can invoke its
            # OnClose delegate. A disabled route cannot authorize that unknown
            # delegate or adopt an existing training run.
            try:
                training = self.objects.observe_daily_script(self.token, 23)
            except Exception as exc:
                return report('BLOCKED', f'training_recovery_observation_failed ({exc})')
            if (not isinstance(training, dict) or training.get('role_id') != character_id
                    or type(training.get('training_scene')) is not bool):
                return report('BLOCKED', 'training_recovery_state_unavailable')
            if training['training_scene']:
                return report('BLOCKED', 'daily_disabled_activity_open:training')
        observed, reason = self._observe(character_id)
        if observed is None:
            return report('BLOCKED', reason)
        character_id = observed['character_id']
        cycle, last_server_time = _refresh_cycle(observed), observed['server_now']
        cycle_deadline = self.clock() + max(0, cycle + 86400 - observed['server_now'])
        items.update(_items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled, stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled, refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled, soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None))
        if self.retry_source is not None:
            retry_gate = self._retry_current_gate(character_id, cycle, route_witnesses)
            if retry_gate:
                key = _CONTEXT_RETRY_ROWS[self.retry_source['route']]
                if key in items:
                    items[key] = replace(items[key], status='UNAVAILABLE', reason=retry_gate)
                return report('PARTIAL', retry_gate)
        city_rows = ((309, 'online_gift', '礼包领取'), (306, 'spirit_tree', '灵气树收取'),
                     (308, 'city_shop', '商铺收取与生产'), (307, 'little_demon', '小妖收取'))
        if self.selected_items is not None:
            for task_id, name, label in city_rows:
                if name in self.selected_items:
                    items[(7, task_id)] = DailyItemResult('city_activity', 7, task_id, label,
                                                          'PENDING', 'city_activity_pending', None, None)

        def merge_fresh(fresh):
            """A newer conflicting projection must not be hidden by old results."""
            fresh = {key: _select_item(key, item, self.selected_items,
                                     tribute_configured=self.tribute_options is not None, enabled_items=self.enabled_items)
                     for key, item in fresh.items()}
            items.update(fresh)
            for key, prior in processed.items():
                if not self._route_enabled(_ITEM_KEYS.get(key)):
                    items[key] = _select_item(key, fresh.get(key, prior), self.selected_items,
                                              tribute_configured=self.tribute_options is not None, enabled_items=self.enabled_items)
                    continue
                latest = fresh.get(key)
                if prior.processed_without_reply or prior.page_confirmed:
                    # A handled action and a page-confirmed action are not
                    # assertions that the separate activity push has arrived.
                    if (latest is not None and latest.required == prior.required
                            and type(latest.current) is int and type(prior.current) is int
                            and latest.current >= prior.current):
                        prior = replace(prior, current=latest.current)
                    items[key] = prior
                    continue
                if key in ((7, 306), (7, 307), (7, 308), (7, 309)):
                    # City receipts are not rows in the daily activity push.
                    # A fresh daily read cannot disprove or erase them.
                    items[key] = prior
                    continue
                if key in ((2, 8), (2, 17), (2, 21), (3, 9), (3, 22), (4, 14), (6, 24)):
                    # These rows describe the whole mode; the server activity
                    # counters measure different, shorter daily objectives.
                    items[key] = prior
                    continue
                if prior.status in _OBSERVED_COMPLETE:
                    if (latest is None or latest.status != 'ALREADY_COMPLETE'
                            or latest.current != prior.current or latest.required != prior.required):
                        items[key] = replace(latest or prior, status='UNKNOWN', reason='daily_previous_progress_changed')
                        return 'daily_previous_progress_changed'
                    items[key] = prior
                elif latest is None or latest.reason != 'daily_progress_inconsistent':
                    items[key] = prior
            return ''

        def run_city_activity(task_id, name, executor_type, **kwargs):
            nonlocal gift_deadline, gift_cleanup
            key = (7, task_id)
            if self.selected_items is None or name not in self.selected_items:
                return None
            if not self._wait_for_admission():
                return report('STOPPED', 'user_stop')
            report('RUNNING', 'daily_city_activity_pending')
            step = None
            executor_objects = route_witnesses.get(name) or _RouteCommandWitness(self.objects)
            try:
                executor_objects.executor_entered = True
                executor = executor_type(executor_objects, self.token, stopped=self.stopped,
                                         paused=self.paused, wait=self.wait,
                                         **({'clock': self.clock} if task_id == 309 else {}))
                if task_id == 309 and self.wait_for_gift:
                    def gift_wait_progress(waiting, seconds):
                        items[key] = replace(items[key], status='PENDING',
                            reason='online_gift_current_tier_not_due:' + str(seconds),
                            current=waiting.got_after, cleanup=waiting.cleanup, cleanup_pending=False,
                            operation_id=waiting.operation_id, got_before=waiting.got_before,
                            got_after=waiting.got_after)
                        report('RUNNING', 'online_gift_single_tier_wait',
                               cooldown_items=(('online_gift', seconds),), cooldown_cleanup='VERIFIED_CLOSED')
                    step = self._single_gift_wait(executor, character_id, cycle, last_server_time,
                                                  gift_wait_progress)
                else:
                    step = executor.execute(task_id, character_id=character_id, expected_cycle=cycle, **kwargs)
            except Exception as exc:
                reason = '主城操作或记录结果无法确认：' + str(exc)
                items[key] = replace(items[key], status='UNKNOWN', reason=reason,
                                     cleanup='UNVERIFIED', cleanup_pending=True)
                city_cleanup_evidence.append({'route': name, 'task_id': task_id, 'reason': reason,
                                              'cleanup': 'UNVERIFIED', 'cleanup_pending': True,
                                              'command_attempts': executor_objects.command_attempts})
                return report('UNKNOWN', reason, cleanup_pending=True)
            persistence_reason = ''
            try:
                evidence_path = self._persist_city_result(task_id, character_id, step)
            except Exception as exc:
                evidence_path = ''
                persistence_reason = '主城结果记录失败：' + str(exc)
            if evidence_path:
                ledger_paths.append(evidence_path)
            current = required = None
            if task_id == 309:
                current = step.got_after
            elif task_id in (306, 307):
                current, required = step.targets_confirmed, step.targets_initial
            status = step.status
            step_reason = persistence_reason or step.reason
            cleanup_pending = (getattr(step, 'cleanup_pending', False)
                               or step.cleanup == 'UNVERIFIED')
            cleanup_safe = (not cleanup_pending and (step.cleanup == 'VERIFIED_CLOSED'
                            or step.cleanup == 'NOT_NEEDED' and not step.mutation_attempted))
            receipts = getattr(step, 'receipts', ())
            request_issued = any(isinstance(row, dict) and row.get('request_submitted') is True for row in receipts)
            reply_received = any(isinstance(row, dict) and (row.get('response_received') is True
                or row.get('receipt_matched') is True or type(row.get('operate')) is int) for row in receipts)
            without_reply = status == 'PROCESSED' and getattr(step, 'processed_without_reply', False) is True
            page_confirmed = status == 'COMPLETED' and getattr(step, 'page_confirmed', False) is True
            continuation_safe = getattr(step, 'continuation_safe', False) is True
            if persistence_reason or cleanup_pending:
                status = 'UNKNOWN'
            elif status == 'PROCESSED' and (not without_reply or not cleanup_safe or not step.mutation_attempted
                    or not request_issued or task_id in (306, 307) and not continuation_safe):
                status, step_reason = 'UNKNOWN', 'city_processed_evidence_incomplete'
            elif page_confirmed and (not cleanup_safe or not step.mutation_attempted or not request_issued):
                status, step_reason = 'UNKNOWN', 'city_page_evidence_incomplete'
            elif status == 'NO_CANDIDATES':
                status = 'NO_FREE_REWARD'
            elif status in ('REJECTED', 'UNAVAILABLE'):
                status = 'UNAVAILABLE'
            elif status not in ('COMPLETED', 'PROCESSED', 'ALREADY_COMPLETE', 'NO_FREE_REWARD',
                                'PARTIAL', 'BLOCKED', 'UNKNOWN', 'STOPPED'):
                status = 'UNKNOWN'
            if task_id == 309:
                gift_deadline = None
                gift_cleanup = step.cleanup
                cooldown_checks.pop('online_gift', None)
                if status == 'NO_FREE_REWARD' and step.reason == 'online_gift_current_tier_not_due':
                    # Only an observed, unsent current tier can be checked
                    # again. A timeout, rejected claim or no-reply send is
                    # terminal for this route and never becomes a timer.
                    now = getattr(step, 'server_now', None)
                    ready = getattr(step, 'next_get_time', None)
                    if (cleanup_safe and step.cleanup == 'VERIFIED_CLOSED'
                            and step.mutation_attempted is False and not receipts
                            and getattr(step, 'role_id', None) == character_id
                            and getattr(step, 'refresh_cycle', None) == cycle
                            and step.template_exists is True and step.tier_due is False
                            and type(now) is int and now > 0 and type(ready) is int and ready > now
                            and step.got_after == step.got_before):
                        gift_deadline = self.clock() + ready - now
                        cooldown_checks['online_gift'] = self.clock() + 60.0
                        status = 'PENDING'
                    else:
                        status, step_reason = 'UNAVAILABLE', 'online_gift_cooldown_evidence_incomplete'
                elif status == 'NO_FREE_REWARD' and step.reason == 'online_gift_source_icon_unavailable':
                    status = 'UNAVAILABLE'
            items[key] = replace(items[key], status=status, reason=step_reason, current=current,
                                 required=required, evidence_path=evidence_path, cleanup=step.cleanup,
                cleanup_pending=not cleanup_safe, operation_id=step.operation_id,
                processed_without_reply=bool(status == 'PROCESSED' and without_reply),
                page_confirmed=bool(status == 'COMPLETED' and page_confirmed),
                request_issued=request_issued, reply_received=reply_received, continuation_safe=continuation_safe,
                got_before=getattr(step, 'got_before', None), got_after=getattr(step, 'got_after', None),
                mana_before=getattr(step, 'mana_before', None), mana_after=getattr(step, 'mana_after', None),
                targets_initial=getattr(step, 'targets_initial', None), targets_confirmed=getattr(step, 'targets_confirmed', None))
            city_cleanup_evidence.append({'route': name, 'task_id': task_id,
                                          'operation_id': step.operation_id, 'reason': step_reason,
                                          'cleanup': step.cleanup, 'cleanup_pending': not cleanup_safe,
                                          'evidence_path': evidence_path,
                                          'native_snapshot': getattr(step, 'native_snapshot', None),
                                          'cleanup_evidence': getattr(step, 'cleanup_evidence', ())})
            if status == 'STOPPED':
                return report(status, step_reason, cleanup_pending=not cleanup_safe)
            if not cleanup_safe:
                return report('UNKNOWN', step_reason, cleanup_pending=True)
            # The failed route keeps its outcome. A proven normal release lets
            # later selected activities acquire their own fresh native model.
            if task_id == 309 and gift_deadline is not None:
                processed.pop(key, None)
            else:
                processed[key] = items[key]
            return None

        gift_deadline = None
        gift_cleanup = 'NOT_OBSERVED'
        cooldown_checks = {}

        def recheck_online_gift():
            nonlocal observed, last_server_time, cycle_deadline
            if (gift_deadline is None or self.clock() < max(
                    gift_deadline, cooldown_checks.get('online_gift', gift_deadline))):
                return None
            # A deadline admits a fresh read, never reuse of an old quote or
            # receipt. Role, day and server clock are checked before action.
            fresh, reason = self._observe(character_id, cycle, last_server_time)
            if fresh is None:
                items[7, 309] = replace(items[7, 309], status='UNKNOWN', reason=reason)
                return report('UNKNOWN', reason)
            observed, last_server_time = fresh, fresh['server_now']
            cycle_deadline = self.clock() + max(0, cycle + 86400 - last_server_time)
            return run_city_activity(309, 'online_gift', DailyOnlineGiftExecutor)

        terminal = run_city_activity(309, 'online_gift', DailyOnlineGiftExecutor)
        if terminal is not None:
            return terminal
        if self.wait_for_gift:
            # End this explicit invocation after its one target, including a
            # handled no-reply send. Never enter another route or next tier.
            item = items[7, 309]
            return report('DONE' if item.status in _HANDLED_OUTCOMES else
                          item.status if item.status in ('UNKNOWN', 'STOPPED', 'BLOCKED') else 'PARTIAL',
                          item.reason, cleanup_pending=item.cleanup_pending)

        online_deadline = None
        copy_deadline = None
        training_deadline = None
        arena_deadline = None
        trial_deadline = point_race_deadline = None
        online_cleanup = copy_cleanup = training_cleanup = arena_cleanup = 'NOT_OBSERVED'
        trial_cleanup = point_race_cleanup = 'NOT_OBSERVED'
        read_only_releases = {}

        sign_page_rows = ((1302, 'salary', '签到页俸禄'),
                          (1303, 'arena_reward', '签到页竞技宝箱'),
                          (1304, 'point_reward', '积分宝箱'))

        def run_sign_page_rewards():
            nonlocal observed, last_server_time
            try:
                step = DailySignPageRewardsExecutor(
                    self.objects, self.token, stopped=self.stopped,
                    paused=self.paused, wait=self._sign_wait,
                ).execute(305, character_id=character_id, expected_cycle=cycle,
                          expected_game_day=observed['server_day'])
            except Exception as exc:
                reason = f'sign_page_rewards_error ({exc})'
                for task_id, _, _ in sign_page_rows:
                    items[(7, task_id)] = replace(items[(7, task_id)], status='UNKNOWN', reason=reason)
                return report('UNKNOWN', reason)

            details = {detail.key: detail for detail in step.items}
            expected_keys = {key for _, key, _ in sign_page_rows}
            valid_details = len(step.items) == len(expected_keys) and set(details) == expected_keys
            identity_matches = (step.role_id == character_id and step.refresh_cycle == cycle
                                and step.game_day == observed['server_day'])
            receiver_unavailable = (step.status == 'UNAVAILABLE'
                                    and step.reason == 'sign_page_rewards_receiver_unavailable')
            normal = step.status in ('COMPLETED', 'NO_FREE_REWARD') or receiver_unavailable
            cleanup_ok = (step.cleanup == 'VERIFIED_CLOSED'
                          or step.cleanup == 'NOT_NEEDED' and not step.mutation_attempted)
            evidence_ok = valid_details and identity_matches and cleanup_ok
            for task_id, detail_key, _ in sign_page_rows:
                row_key = (7, task_id)
                detail = details.get(detail_key)
                if detail is None:
                    items[row_key] = replace(items[row_key], status='UNKNOWN', reason=step.reason)
                    continue
                status = detail.status
                if status == 'AVAILABLE':
                    status = 'PENDING'
                if status not in ('COMPLETED', 'ALREADY_CLAIMED', 'NO_FREE_REWARD',
                                  'CONDITION_NOT_MET', 'UNAVAILABLE', 'PENDING', 'UNKNOWN'):
                    status = 'UNKNOWN'
                if normal:
                    evidence_ok = evidence_ok and (status in _HANDLED_OUTCOMES or
                                                    receiver_unavailable and status == 'UNAVAILABLE')
                    if status == 'UNAVAILABLE':
                        evidence_ok = (evidence_ok and detail.availability == 'AVAILABLE'
                                       and detail.request_count == 0)
                    if detail.status == 'COMPLETED':
                        evidence_ok = (evidence_ok and type(detail.request_count) is int
                                       and detail.request_count > 0
                                       and detail.stock_callback_returned is True
                                       and detail.reward_confirmed is True
                                       and detail.availability != 'AVAILABLE')
                items[row_key] = replace(
                    items[row_key], status=status, reason=detail.reason,
                    sign_reward_initial_availability=detail.initial_availability,
                    sign_reward_availability=detail.availability,
                    sign_reward_request_count=detail.request_count,
                    sign_reward_callback_returned=detail.stock_callback_returned,
                    sign_reward_confirmed=detail.reward_confirmed,
                    sign_reward_receipts=tuple(asdict(receipt) for receipt in detail.receipts),
                )
            if receiver_unavailable:
                evidence_ok = evidence_ok and any(detail.status == 'UNAVAILABLE' for detail in step.items)
            if not normal:
                status = ('STOPPED' if step.status == 'STOPPED' else
                          'UNKNOWN' if step.status == 'UNKNOWN' or step.mutation_attempted else 'BLOCKED')
                return report(status, step.reason)
            if not evidence_ok:
                for task_id, _, _ in sign_page_rows:
                    key = (7, task_id)
                    if items[key].status not in ('NO_FREE_REWARD', 'CONDITION_NOT_MET', 'ALREADY_CLAIMED'):
                        items[key] = replace(items[key], status='UNKNOWN', reason='sign_page_rewards_evidence_incomplete')
                return report('UNKNOWN', 'sign_page_rewards_evidence_incomplete')

            # The stock sign-page lookup cannot reach the separate PointRace
            # scene. Only an eligible, proven-unsent item may enter the normal
            # chest workflow, after SignIn has already closed and released.
            point_detail = details.get('point_reward')
            if (step.cleanup == 'VERIFIED_CLOSED' and point_detail is not None
                    and point_detail.status == 'UNAVAILABLE' and point_detail.availability == 'AVAILABLE'
                    and point_detail.request_count == 0 and not point_detail.receipts):
                point_step = self._point_chest(character_id, cycle)
                point_row = _point_chest_item(point_step, items[(7, 1304)])
                items[(7, 1304)] = point_row
                if point_step.evidence_path and point_step.evidence_path not in ledger_paths:
                    ledger_paths.append(point_step.evidence_path)
                if point_step.status in ('UNKNOWN', 'STOPPED') or point_row.cleanup_pending:
                    return report('UNKNOWN' if point_row.cleanup_pending or point_step.status == 'UNKNOWN'
                                  else 'STOPPED', point_row.reason)

            # A page reward may also advance the daily chest counter. Refresh
            # before any separately selected chest route uses that counter.
            fresh_observed, reason = self._observe(character_id, cycle, last_server_time)
            if fresh_observed is None or fresh_observed['server_day'] != observed['server_day']:
                return report('UNKNOWN', reason or 'daily_game_day_changed')
            observed = fresh_observed
            last_server_time = observed['server_now']
            fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                           stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled,
                           refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled,
                           soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled,
                           selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
            conflict = merge_fresh(fresh)
            if conflict:
                return report('UNKNOWN', conflict)
            return None

        def run_reward_activity(task_id, name):
            nonlocal online_deadline, online_cleanup
            key = (7, task_id)
            # The sign-page route already handles salary when both are selected.
            if task_id == 302 and self._route_enabled('sign'):
                return None
            # Keep public checklist IDs stable. The selected variants bind a
            # fixed native scope, so an old unfiltered route is never a fallback.
            wire_task = {201: 204, 1201: 205}.get(task_id, task_id)
            if self.selected_items is not None and task_id == 302:
                wire_task = 304
            items[key] = DailyItemResult('reward_activity', 7, task_id, name, 'PENDING',
                                         'daily_free_reward_pending', None, None)
            items[key] = _select_item(key, items[key], self.selected_items, enabled_items=self.enabled_items)
            if not self._route_enabled(_ITEM_KEYS[key]):
                return None
            if task_id == 301:
                for detail_id, _, detail_name in sign_page_rows:
                    items[(7, detail_id)] = DailyItemResult('sign_page_reward', 7, detail_id, detail_name,
                                                           'PENDING', 'sign_page_reward_not_checked', None, None)
            if not self._wait_for_admission():
                items[key] = replace(items[key], status='STOPPED', reason='user_stop')
                return report('STOPPED', 'user_stop')
            report('RUNNING', 'daily_free_reward_pending')
            executor_type = DailySignSalaryExecutor if task_id in (301, 302) else DailyEventRewardsExecutor
            try:
                step = executor_type(self.objects, self.token, stopped=self.stopped,
                                     paused=self.paused, wait=self._sign_wait if task_id in (301, 302) else self.wait).execute(
                                         wire_task, character_id=character_id, expected_cycle=cycle,
                                         **({'expected_game_day': observed['server_day']} if task_id == 301 else {}))
            except Exception as exc:
                items[key] = replace(items[key], status='UNKNOWN', reason=f'daily_reward_error ({exc})')
                return report('UNKNOWN', items[key].reason)
            if task_id == 301:
                self._service_popups(force=True)
                if step.status == 'STOPPED' or self.stopped():
                    items[key] = replace(items[key], status='STOPPED', reason='user_stop')
                    return report('STOPPED', 'user_stop')
                signed = (step.checkin_state_observed is True and type(step.checkin_today) is int
                          and step.checkin_today == 1 and step.role_id == character_id
                          and step.refresh_cycle == cycle and step.game_day == observed['server_day'])
                if step.reason == 'sign_salary_premium_spend_observed':
                    items[key] = replace(items[key], status='UNKNOWN', reason=step.reason)
                    return report('UNKNOWN', step.reason)
                status = ('COMPLETED' if step.status == 'COMPLETED' else 'ALREADY_CLAIMED') \
                    if signed and step.status in ('COMPLETED', 'ALREADY_CLAIMED') else 'UNKNOWN'
                items[key] = replace(items[key], status=status, reason=step.reason,
                    checkin_state_observed=step.checkin_state_observed, checkin_today=step.checkin_today,
                    checkin_game_day=step.game_day, checkin_request_attempted=step.mutation_attempted,
                    checkin_cleanup=step.cleanup, checkin_initial_today=step.initial_checkin_today,
                    checkin_initial_times=step.initial_checkin_times, checkin_final_times=step.final_checkin_times,
                    checkin_native_request_count=step.native_request_count,
                    checkin_native_opcode=step.native_opcode, checkin_operation_id=step.operation_id)
                if step.cleanup == 'UNVERIFIED':
                    return report('UNKNOWN', 'sign_salary_checkin_state_confirmed_cleanup_unverified'
                                  if signed else step.reason)
                if status == 'UNKNOWN':
                    return report('UNKNOWN', step.reason)
                return None
            if task_id in (201, 1201):
                if task_id == 1201:
                    online_deadline = None
                    online_cleanup = step.cleanup
                    cooldown_checks.pop('online_reward', None)
                for detail in step.items:
                    if detail.key.startswith('premium.zhaoyun.'):
                        detail_status = {'ALREADY_CLAIMED': 'ALREADY_COMPLETE', 'AVAILABLE': 'PENDING',
                                         'UNSUPPORTED': 'UNSUPPORTED'}.get(detail.status, 'UNAVAILABLE')
                        detail_reason = detail.reason
                        if (detail.reason == 'event_rewards_zhaoyun_resources_insufficient'
                                and all(type(value) is int for value in
                                        (detail.resource_type, detail.resource_available, detail.resource_cost))):
                            detail_reason += f':{detail.resource_type}:{detail.resource_available}:{detail.resource_cost}'
                        items[(7, 1246)] = DailyItemResult('reward_detail', 7, 1246, '七进七出',
                            detail_status, detail_reason, detail.current, detail.limit)
                    if task_id != 1201 or not detail.key.startswith('premium.online.'):
                        continue
                    detail_status = {'ALREADY_CLAIMED': 'ALREADY_COMPLETE', 'COOLDOWN': 'UNAVAILABLE',
                                     'AVAILABLE': 'PENDING', 'UNSUPPORTED': 'UNSUPPORTED'}.get(detail.status, 'UNAVAILABLE')
                    detail_reason = detail.reason
                    if detail.status == 'COOLDOWN' and type(detail.remaining_seconds) is int:
                        detail_reason = f'event_rewards_online_cooldown:{max(0, detail.remaining_seconds)}'
                        if (detail.remaining_seconds > 0 and type(detail.current) is int
                                and type(detail.limit) is int and 0 <= detail.current < detail.limit):
                            online_deadline = self.clock() + detail.remaining_seconds
                            cooldown_checks['online_reward'] = self.clock() + 60.0
                    items[(7, 1201)] = DailyItemResult('reward_detail', 7, 1201, detail.name,
                        detail_status, detail_reason, detail.current, detail.limit)
            if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if step.status == 'STOPPED' else 'UNKNOWN'
                items[key] = replace(items[key], status=status, reason=step.reason)
                return report(status, step.reason)
            if task_id == 1201 and step.status == 'PROCESSED':
                if (getattr(step, 'processed_without_reply', False) is not True
                        or getattr(step, 'request_issued', False) is not True
                        or getattr(step, 'reply_received', None) is not False or step.cleanup != 'VERIFIED_CLOSED'):
                    items[key] = replace(items[key], status='UNKNOWN', reason='daily_online_processed_evidence_incomplete')
                    return report('UNKNOWN', items[key].reason)
                online_deadline = None
                cooldown_checks.pop('online_reward', None)
                items[key] = replace(items[key], status='PROCESSED', reason=step.reason,
                    processed_without_reply=True, request_issued=True, reply_received=False)
                processed[key] = items[key]
                return None
            if task_id in (301, 302):
                if step.status == 'COMPLETED':
                    if (step.cleanup != 'VERIFIED_CLOSED' or not step.mutation_attempted
                            or not step.reward_confirmed or not step.stock_callback_returned):
                        items[key] = replace(items[key], status='UNKNOWN', reason='daily_reward_evidence_incomplete')
                        return report('UNKNOWN', 'daily_reward_evidence_incomplete')
                    status = 'COMPLETED'
                elif step.status in ('ALREADY_CLAIMED', 'NO_FREE_REWARD'):
                    status = 'ALREADY_CLAIMED'
                else:
                    status = 'UNAVAILABLE'
            else:
                if step.status in ('COMPLETED', 'PARTIAL'):
                    if step.cleanup != 'VERIFIED_CLOSED' or (step.mutation_attempted and not step.task_confirmed
                            and not (task_id == 1201 and getattr(step, 'page_state_confirmed', False) is True)):
                        items[key] = replace(items[key], status='UNKNOWN', reason='daily_reward_evidence_incomplete')
                        return report('UNKNOWN', 'daily_reward_evidence_incomplete')
                    status = step.status
                else:
                    status = 'UNAVAILABLE'
            if task_id == 1201 and online_deadline is not None:
                items[key] = replace(items[key], status='PENDING')
            else:
                items[key] = replace(items[key], status=status, reason=step.reason,
                    page_confirmed=bool(status == 'COMPLETED' and getattr(step, 'page_state_confirmed', False) is True),
                    request_issued=getattr(step, 'request_issued', False) is True,
                    reply_received=getattr(step, 'reply_received', False) is True)
                if items[key].page_confirmed:
                    processed[key] = items[key]
            return None

        # The page's rewards are separate from arena and point-race challenges.
        for task_id, name in ((301, '每日签到'), (302, '领俸'), (202, 'VIP福利'),
                              (201, '精彩活动'), (203, '限时活动')):
            terminal = run_reward_activity(task_id, name)
            if terminal is not None:
                return terminal
            if task_id == 301 and self._route_enabled('sign'):
                terminal = run_sign_page_rewards()
                if terminal is not None:
                    return terminal
            terminal = recheck_online_gift()
            if terminal is not None:
                return terminal
        terminal = run_reward_activity(1201, '在线奖励')
        if terminal is not None:
            return terminal

        def run_copy_activity():
            nonlocal copy_deadline, copy_cleanup
            key = (7, 303)
            previous = items.get(key)
            items[key] = DailyItemResult('reward_activity', 7, 303, '抄录', 'PENDING',
                                         'daily_copy_pending', previous.current if previous else None,
                                         previous.required if previous else None)
            items[key] = _select_item(key, items[key], self.selected_items, enabled_items=self.enabled_items)
            if not self._route_enabled('copy'):
                return None
            if not self._wait_for_admission():
                items[key] = replace(items[key], status='STOPPED', reason='user_stop')
                return report('STOPPED', 'user_stop')
            report('RUNNING', 'daily_copy_pending')
            try:
                route_witnesses['copy'].executor_entered = True
                step = DailyTechBookCopyExecutor(route_witnesses['copy'], self.token, stopped=self.stopped,
                    paused=self.paused, wait=self.wait).execute(303, character_id=character_id, expected_cycle=cycle)
            except Exception as exc:
                items[key] = replace(items[key], status='UNKNOWN', reason=f'daily_copy_error ({exc})')
                return report('UNKNOWN', items[key].reason)
            copy_deadline = None
            copy_cleanup = step.cleanup
            cooldown_checks.pop('copy', None)
            items[key] = replace(items[key], current=step.free_used, required=step.free_limit)
            if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if step.status == 'STOPPED' else 'UNKNOWN'
                items[key] = replace(items[key], status=status, reason=step.reason)
                return report(status, step.reason)
            if step.status == 'COMPLETED' and (step.cleanup != 'VERIFIED_CLOSED'
                    or not step.mutation_attempted
                    or (not step.stock_callback_returned or not step.reward_confirmed)
                        and getattr(step, 'page_state_confirmed', False) is not True
                    or type(step.copies_completed) is not int or step.copies_completed <= 0):
                items[key] = replace(items[key], status='UNKNOWN', reason='daily_copy_evidence_incomplete')
                return report('UNKNOWN', 'daily_copy_evidence_incomplete')
            if (type(step.free_used) is not int or type(step.free_limit) is not int
                    or type(step.free_remaining) is not int or step.free_used < 0
                    or step.free_limit < step.free_used
                    or step.free_remaining != step.free_limit - step.free_used):
                items[key] = replace(items[key], status='UNAVAILABLE', reason=step.reason)
                return None
            if previous is not None and type(previous.current) is int and step.free_used < previous.current:
                items[key] = replace(items[key], status='UNKNOWN', reason='copy_progress_reversed')
                return report('UNKNOWN', 'copy_progress_reversed')
            if step.status == 'PROCESSED':
                if (getattr(step, 'processed_without_reply', False) is not True
                        or getattr(step, 'request_issued', False) is not True
                        or getattr(step, 'reply_received', None) is not False or step.cleanup != 'VERIFIED_CLOSED'):
                    items[key] = replace(items[key], status='UNKNOWN', reason='daily_copy_processed_evidence_incomplete')
                    return report('UNKNOWN', items[key].reason)
                items[key] = replace(items[key], status='PROCESSED', reason=step.reason,
                    processed_without_reply=True, request_issued=True, reply_received=False)
                processed[key] = items[key]
                return None
            if step.status == 'FREE_LIMIT_REACHED' and step.free_remaining != 0:
                items[key] = replace(items[key], status='UNKNOWN', reason='copy_quota_inconsistent')
                return report('UNKNOWN', 'copy_quota_inconsistent')
            if step.status == 'FREE_LIMIT_REACHED' or step.status == 'COMPLETED' and step.free_remaining == 0:
                status = 'COMPLETED' if step.status == 'COMPLETED' else 'ALREADY_COMPLETE'
                items[key] = replace(items[key], status=status, reason=step.reason,
                    page_confirmed=bool(status == 'COMPLETED' and getattr(step, 'page_state_confirmed', False) is True),
                    request_issued=getattr(step, 'request_issued', False) is True,
                    reply_received=getattr(step, 'reply_received', False) is True)
                if items[key].page_confirmed:
                    processed[key] = items[key]
            elif step.availability == 'WAITING_COOLDOWN' and step.free_remaining > 0:
                seconds = step.next_ready_seconds
                if type(seconds) is int and seconds > 0:
                    copy_deadline = self.clock() + seconds
                    cooldown_checks['copy'] = self.clock() + 60.0
                    items[key] = replace(items[key], status='PENDING', reason=f'copy_cooldown:{seconds}')
                    if copy_cleanup == 'NOT_NEEDED':
                        # A passive eligibility result did not close a panel.
                        # Require a fresh native proof that this route is home
                        # and idle before another activity/account can use UI.
                        released = self.objects.observe_daily_script(self.token, 303)
                        if (isinstance(released, dict) and released.get('role_id') == character_id
                                and released.get('refresh_cycle') == cycle
                                and released.get('route') == 'tech_book_copy'
                                and released.get('home_ready') is True
                                and released.get('busy') is False
                                and released.get('cleanup_pending') is False
                                and released.get('availability') == 'WAITING_COOLDOWN'
                                and released.get('free_used') == step.free_used
                                and released.get('free_limit') == step.free_limit
                                and type(released.get('next_ready_seconds')) is int
                                and released['next_ready_seconds'] > 0):
                            copy_cleanup = 'VERIFIED_CLOSED'
                            copy_deadline = self.clock() + released['next_ready_seconds']
                        else:
                            items[key] = replace(items[key], status='BLOCKED',
                                                 reason='daily_cooldown_cleanup_unverified:copy')
                            return report('BLOCKED', items[key].reason, cleanup_pending=True)
                else:
                    items[key] = replace(items[key], status='UNAVAILABLE', reason='copy_cooldown_unavailable')
            elif step.status == 'COMPLETED' and step.availability == 'READY' and step.free_remaining > 0:
                copy_deadline = self.clock()
                cooldown_checks['copy'] = self.clock() + 60.0
                items[key] = replace(items[key], status='PENDING', reason='daily_copy_pending')
            else:
                items[key] = replace(items[key], status='UNAVAILABLE', reason=step.reason)
            return None

        terminal = run_copy_activity()
        if terminal is not None:
            return terminal
        terminal = recheck_online_gift()
        if terminal is not None:
            return terminal


        def run_script(key, executor_type, *, resume_only=False, cooldown_due=False):
            nonlocal last_server_time, training_deadline, training_cleanup, arena_deadline, arena_cleanup
            nonlocal trial_deadline, trial_cleanup
            if not self._route_enabled(_ITEM_KEYS.get(key)):
                return None
            existing_deadline = {(4, 23): training_deadline, (3, 9): arena_deadline,
                                 (4, 14): trial_deadline}.get(key)
            if existing_deadline is not None and not cooldown_due:
                return None
            if key == (4, 23):
                training_deadline = None
                cooldown_checks.pop('training', None)
            elif key == (3, 9):
                arena_deadline = None
                cooldown_checks.pop('arena', None)
            elif key == (4, 14):
                trial_deadline = None
                cooldown_checks.pop('trial', None)
                read_only_releases.pop('trial', None)
            item = items.get(key)
            full_mode_reasons = {
                (2, 8): 'daily_divinity_clear_pending',
                (2, 17): 'daily_grave_clear_pending',
                (3, 9): 'daily_arena_free_attempts_pending',
                (4, 14): 'daily_trial_clear_pending',
                (4, 23): 'daily_training_pending',
                (6, 24): 'daily_revenge_target_pending',
            }
            if (key in full_mode_reasons and item is not None
                    and item.status in ('PENDING', 'ALREADY_COMPLETE', 'COMPLETED')):
                # A daily counter is a checklist entry, not the end of a mode.
                item = replace(item, status='PENDING', reason=full_mode_reasons[key])
                items[key] = item
            while item is not None and item.status == 'PENDING':
                if not self._wait_for_admission():
                    return report('STOPPED', 'user_stop')
                report('RUNNING', 'daily_script_pending')
                executor_objects = route_witnesses.get(_ITEM_KEYS.get(key), self.objects)
                if _ITEM_KEYS.get(key) in route_witnesses:
                    executor_objects.executor_entered = True
                executor = executor_type(executor_objects, self.token, stopped=self.stopped,
                                         paused=self.paused, wait=self.wait)
                arguments = dict(character_id=character_id, expected_current=item.current,
                                 expected_required=item.required, expected_cycle=cycle)
                if resume_only:
                    arguments['resume_only'] = True
                step = executor.execute(key[1], **arguments)
                if key == (2, 8) and getattr(step, 'target_receipts', ()):
                    city_cleanup_evidence.append({'route': 'divinity', 'task_id': key[1],
                        'cleanup': step.cleanup, 'reason': step.reason,
                        'stock_callback_returned': step.stock_callback_returned,
                        'account_progress_observed': step.account_progress_observed,
                        'target_receipts': list(step.target_receipts)})
                step_cleanup_pending = bool(getattr(step, 'cleanup_pending', False)
                                            or getattr(step, 'cleanup', None) == 'UNVERIFIED')
                item = replace(item, cleanup=getattr(step, 'cleanup', ''),
                               cleanup_pending=step_cleanup_pending,
                               operation_id=getattr(step, 'operation_id', None) or '')
                items[key] = item
                if key == (3, 9):
                    item = replace(item, kind='activity', name='排位竞技场',
                                   money_current=step.daily_money_after, money_limit=step.daily_money_limit,
                                   reputation_current=step.daily_reputation_after,
                                   reputation_limit=step.daily_reputation_limit)
                    items[key] = item
                if key in ((4, 23), (3, 9), (4, 14)) and step.status == 'WAITING':
                    route = {(4, 23): 'training', (3, 9): 'arena', (4, 14): 'trial'}[key]
                    seconds = getattr(step, 'cooldown_remaining_seconds', None)
                    operation = getattr(step, 'operation_id', None)
                    read_only_release = (key == (4, 14) and operation == ''
                                         and step.cleanup == 'NOT_NEEDED'
                                         and getattr(step, 'release_verified', None) is True)
                    operation_valid = (isinstance(operation, str) and len(operation) == 32
                                       and all(value in '0123456789abcdef' for value in operation))
                    if key == (3, 9):
                        operation_valid |= operation == ''
                    elif read_only_release:
                        operation_valid = True
                    arena_released = (key != (3, 9) or
                                      getattr(step, 'home_ready', None) is True
                                      and getattr(step, 'native_busy', None) is False
                                      and getattr(step, 'release_verified', None) is True)
                    if (type(seconds) is not int or seconds <= 0
                            or step_cleanup_pending
                            or step.cleanup != 'VERIFIED_CLOSED' and not read_only_release
                            or getattr(step, 'role_id', None) != character_id
                            or getattr(step, 'refresh_cycle', None) != cycle
                            or not operation_valid or not arena_released
                            or key == (4, 14) and getattr(step, 'release_verified', None) is not True):
                        items[key] = replace(item, status='UNKNOWN', reason=route + '_cooldown_evidence_incomplete')
                        return report('UNKNOWN', items[key].reason, cleanup_pending=step.cleanup != 'VERIFIED_CLOSED')
                    if key == (4, 23):
                        training_cleanup = step.cleanup
                        training_deadline = self.clock() + seconds
                        current = step.progress_after if step.progress_after is not None else item.current
                        required = step.required if step.required is not None else item.required
                    elif key == (3, 9):
                        arena_cleanup = step.cleanup
                        arena_deadline = self.clock() + seconds
                        current, required = step.challenge_count_after, step.natural_limit
                    else:
                        trial_cleanup = step.cleanup
                        trial_deadline = self.clock() + seconds
                        current = step.progress_after if step.progress_after is not None else item.current
                        required = step.required if step.required is not None else item.required
                        if read_only_release:
                            read_only_releases[route] = {
                                'route': route, 'release': 'VERIFIED_IDLE', 'role_id': step.role_id,
                                'refresh_cycle': step.refresh_cycle, 'operation_id': operation,
                                'cleanup': step.cleanup, 'release_verified': step.release_verified,
                            }
                    cooldown_checks[route] = self.clock() + 60.0
                    items[key] = replace(item, status='PENDING', reason=step.reason + ':' + str(seconds),
                                         current=current, required=required)
                    processed.pop(key, None)
                    return None
                if step.status in ('UNKNOWN', 'STOPPED') or step_cleanup_pending:
                    status = 'UNKNOWN' if step_cleanup_pending else step.status
                    current, required = item.current, item.required
                    if key == (2, 8) and type(getattr(step, 'killed_mask_after', None)) is int:
                        current, required = step.killed_mask_after.bit_count(), 5
                    elif key in ((2, 17), (4, 14)):
                        current, required = None, None
                    items[key] = replace(item, status=status, reason=step.reason,
                                         current=current if key in ((2, 8), (2, 17), (4, 14)) else
                                         step.progress_after if step.progress_after is not None else item.current,
                                         required=required)
                    return report(status, step.reason, cleanup_pending=step_cleanup_pending)
                if step.status not in ('COMPLETED', 'ALREADY_COMPLETE'):
                    current, required = item.current, item.required
                    if key == (3, 9) and type(getattr(step, 'challenge_count_after', None)) is int:
                        current, required = step.challenge_count_after, step.natural_limit
                    elif key == (2, 8) and type(getattr(step, 'killed_mask_after', None)) is int:
                        current, required = step.killed_mask_after.bit_count(), 5
                    elif key in ((2, 17), (4, 14)):
                        current, required = None, None
                    items[key] = replace(item, status='UNAVAILABLE', reason=step.reason,
                                         current=current if key in ((2, 8), (2, 17), (3, 9), (4, 14)) else
                                         step.progress_after if step.progress_after is not None else item.current,
                                         required=required)
                    processed[key] = items[key]
                    break
                required_clearance = {(2, 8): 'clearance_complete', (2, 17): 'checklist_complete',
                                      (4, 14): 'clearing_complete', (6, 24): 'clearing_complete'}.get(key)
                if required_clearance and getattr(step, required_clearance, False) is not True:
                    items[key] = replace(item, status='UNKNOWN', reason='daily_mode_clearance_unverified')
                    return report('UNKNOWN', 'daily_mode_clearance_unverified')
                if key == (3, 9) and getattr(step, 'daily_rewards_complete', False) is not True:
                    items[key] = replace(item, status='UNKNOWN', reason='arena_daily_rewards_unverified')
                    return report('UNKNOWN', 'arena_daily_rewards_unverified')
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None:
                    items[key] = replace(item, status='UNKNOWN', reason=reason)
                    return report('UNKNOWN', reason)
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                    stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled,
                    refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled,
                    soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(key)
                conflict = merge_fresh(fresh)
                if conflict:
                    return report('UNKNOWN', conflict)
                if key in full_mode_reasons and step.status in ('COMPLETED', 'ALREADY_COMPLETE'):
                    if key == (3, 9):
                        current, required = step.challenge_count_after, step.natural_limit
                    elif key == (2, 8):
                        current, required = step.killed_mask_after.bit_count(), 5
                    elif key in ((2, 17), (4, 14)):
                        current, required = None, None
                    else:
                        current, required = (latest or item).current, (latest or item).required
                    items[key] = replace(item if key == (3, 9) else latest or item,
                                         status='COMPLETED', reason=step.reason,
                                         current=current, required=required)
                    processed[key] = items[key]
                    break
                if latest is None or latest.current is None or latest.current <= item.current:
                    items[key] = replace(latest or item, status='UNAVAILABLE', reason='daily_progress_not_updated')
                    processed[key] = items[key]
                    break
                if latest.status == 'ALREADY_COMPLETE':
                    items[key] = replace(latest, status='COMPLETED', reason=step.reason)
                    processed[key] = items[key]
                    break
                item = latest


        # Continue an already-open dungeon or trial before activities that
        # require the main screen, without sending another entry request.
        for key, executor_type, task_id, label in (
                ((2, 17), DailyGraveExecutor, 17, 'grave'),
                ((4, 14), DailyTrialExecutor, 14, 'trial')):
            item = items.get(key)
            if item is None or (item.status != 'PENDING' and not
                                (key in ((2, 17), (4, 14)) and item.status == 'ALREADY_COMPLETE')):
                continue
            try:
                current_scene = self.objects.observe_daily_script(self.token, task_id)
            except Exception as exc:
                return report('BLOCKED', f'daily_{label}_observation_failed ({exc})')
            if isinstance(current_scene, dict) and current_scene.get('next_action') == 'resume':
                if (current_scene.get('task_id') != task_id or current_scene.get('status') != 'READY'
                        or current_scene.get('role_id') != character_id or current_scene.get('refresh_cycle') != cycle
                        or current_scene.get('activity_count') != item.current
                        or current_scene.get('required') != item.required):
                    return report('BLOCKED', f'daily_{label}_resume_identity_changed')
                terminal = run_script(key, executor_type, resume_only=True)
                if terminal is not None:
                    return terminal
                if key == (4, 14) and trial_deadline is not None:
                    continue
                if items[key].status not in _OBSERVED_COMPLETE:
                    return report('PARTIAL', items[key].reason)

        if self.mingshi_enabled and self._route_enabled('mingshi'):
            cleanup = DailyMingshiExecutor(self.objects, self.token, stopped=self.stopped,
                paused=self.paused, wait=self.wait).restore_open_panel(character_id=character_id, expected_cycle=cycle)
            if cleanup.status in ('UNKNOWN', 'STOPPED') or cleanup.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if cleanup.status == 'STOPPED' else 'BLOCKED'
                item = items.get((2, 7))
                if item is not None:
                    items[(2, 7)] = replace(item, status=status, reason=cleanup.reason)
                return report(status, cleanup.reason)
        task = items.get(_TRIBUTE_TASK)
        if task is None:
            items[_TRIBUTE_TASK] = DailyItemResult('task', *_TRIBUTE_TASK, '培养灵兽',
                'UNAVAILABLE', 'daily_tribute_task_missing', None, None)
            task = items[_TRIBUTE_TASK]

        if task.status == 'PENDING':
            if not self._wait_for_admission():
                items[_TRIBUTE_TASK] = replace(task, status='STOPPED', reason='user_stop')
                return report('STOPPED', 'user_stop')
            initial_current, required = task.current, task.required
            remaining_requests = required - initial_current
            if self.selected_items is None:
                target = TributeTarget(max_requests=remaining_requests)
            else:
                configured = self.tribute_options
                # Selection without an explicit target is projected unavailable
                # before this point. Never turn use_all into an unbounded daily
                # session: the server's remaining daily counter is a hard cap.
                request_limit = (remaining_requests if configured.use_all else
                                 min(remaining_requests, configured.max_requests))
                target = TributeTarget(max_requests=request_limit, target_level=configured.desired_level)
            policy = DailySpiritTribute(uuid.uuid4().hex, self.token.context_key, target)
            executor = DevelopmentWorkflow(self.objects, self.token, self.data_dir,
                stopped=self.stopped, paused=self.paused, wait=self.wait)

            def tribute_checkpoint(current_policy):
                nonlocal observed, last_server_time, task
                confirmed = current_policy.confirmed_tributes
                if type(confirmed) is not int or not 0 <= confirmed <= required - initial_current:
                    return WorkflowResult('UNKNOWN', 'daily_tribute_receipt_count_invalid')
                expected = initial_current + confirmed
                deadline = self.clock() + self.confirmation_timeout
                while True:
                    if self.stopped():
                        return WorkflowResult('STOPPED', 'user_stop')
                    observed, why = self._observe(character_id, cycle, last_server_time)
                    if observed is None:
                        return WorkflowResult('UNKNOWN' if confirmed else 'BLOCKED', why)
                    last_server_time = observed['server_now']
                    fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                                   stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled,
                                   refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled,
                                   soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                    latest = fresh.pop(_TRIBUTE_TASK, None)
                    if latest is None or latest.required != required or type(latest.current) is not int:
                        return WorkflowResult('UNKNOWN', 'daily_tribute_task_changed')
                    if latest.reason != 'daily_progress_inconsistent' and latest.current == expected:
                        conflict = merge_fresh(fresh)
                        if conflict:
                            return WorkflowResult('UNKNOWN', conflict)
                        task = latest
                        items[_TRIBUTE_TASK] = latest
                        report('RUNNING', 'daily_tribute_pending')
                        return None
                    if latest.current > expected:
                        return WorkflowResult('UNKNOWN', 'daily_tribute_progress_changed')
                    if self.clock() >= deadline:
                        return WorkflowResult('UNKNOWN' if confirmed else 'BLOCKED',
                                              'daily_tribute_progress_unconfirmed')
                    if self.wait is None:
                        time.sleep(min(.1, max(0., deadline - self.clock())))
                    else:
                        self.wait()

            report('RUNNING', 'daily_tribute_pending')
            try:
                step = executor.execute('tribute', target, policy=policy,
                                        tribute_checkpoint=tribute_checkpoint)
            except Exception as exc:
                step = WorkflowResult('UNKNOWN', f'daily_tribute_execution_failed ({exc})')
            if step.ledger_path:
                ledger_paths.append(step.ledger_path)
            if step.status in ('STOPPED', 'UNKNOWN'):
                items[_TRIBUTE_TASK] = replace(task, status=step.status, reason=step.reason)
                return report(step.status, step.reason)
            if step.cleanup != 'CONFIRMED':
                items[_TRIBUTE_TASK] = replace(task, status='UNKNOWN', reason='daily_tribute_cleanup_unconfirmed')
                return report('UNKNOWN', 'daily_tribute_cleanup_unconfirmed')
            if step.status == 'DONE' and task.current == required and policy.confirmed_tributes > 0:
                if step.evidence != 'SERVER_CONFIRMED':
                    items[_TRIBUTE_TASK] = replace(task, status='UNKNOWN', reason='daily_tribute_evidence_incomplete')
                    return report('UNKNOWN', 'daily_tribute_evidence_incomplete')
                task = replace(task, status='COMPLETED', reason='daily_tribute_progress_observed')
            elif step.status == 'DONE' and task.current < required:
                task = replace(task, status='UNAVAILABLE', reason=step.reason)
            elif self.selected_items is not None and step.reason in (
                    'tribute_target_reached', 'tribute_request_budget_reached',
                    'tribute_peaches_exhausted', 'tribute_level_cap_reached', 'tribute_rankup_required'):
                task = replace(task, status='UNAVAILABLE', reason=step.reason)
            elif step.status != 'DONE':
                task = replace(task, status='BLOCKED', reason=step.reason)
            items[_TRIBUTE_TASK] = task

        if self.stopped():
            return report('STOPPED', 'user_stop')
        processed[_TRIBUTE_TASK] = items[_TRIBUTE_TASK]


        stone_key = (2, 16)
        item = items.get(stone_key)
        if self.stone_enabled and item is not None and item.status == 'PENDING':
            if not self._wait_for_admission():
                return report('STOPPED', 'user_stop')
            report('RUNNING', 'daily_stone_pending')
            step = DailyStoneExecutor(self.objects, self.token, stopped=self.stopped,
                                      paused=self.paused, wait=self.wait).execute(
                                          16, character_id=character_id, expected_cycle=cycle)
            if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if step.status == 'STOPPED' else 'UNKNOWN'
                current = (step.progress_after if step.account_progress_observed
                           and step.progress_before == item.current and step.required == item.required
                           and type(step.progress_after) is int and item.current <= step.progress_after <= item.required
                           else item.current)
                items[stone_key] = replace(item, status=status, reason=step.reason, current=current)
                return report(status, step.reason)
            if step.status in ('COMPLETED', 'ALREADY_COMPLETE'):
                if (step.status == 'COMPLETED' and (step.cleanup != 'VERIFIED_CLOSED'
                        or step.progress_before != item.current or not step.stock_callback_returned
                        or not step.account_progress_observed)):
                    items[stone_key] = replace(item, status='UNKNOWN', reason='daily_stone_evidence_incomplete')
                    return report('UNKNOWN', 'daily_stone_evidence_incomplete')
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None:
                    items[stone_key] = replace(item, status='UNKNOWN', reason=reason)
                    return report('UNKNOWN', reason)
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                               stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled, refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled, soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(stone_key)
                if (latest is None or latest.status != 'ALREADY_COMPLETE'
                        or latest.required != item.required or step.required != item.required
                        or latest.current != step.progress_after or latest.current != item.required):
                    items[stone_key] = replace(item, status='UNKNOWN', reason='daily_stone_progress_mismatch')
                    return report('UNKNOWN', 'daily_stone_progress_mismatch')
                conflict = merge_fresh(fresh)
                if conflict:
                    return report('UNKNOWN', conflict)
                items[stone_key] = replace(latest, status=step.status, reason=step.reason)
            else:
                items[stone_key] = replace(item, status='UNAVAILABLE', reason=step.reason)
            processed[stone_key] = items[stone_key]
        # Clear today's entire divinity roster before taking the stone harvest.
        divinity_key = (2, 8)
        terminal = run_script(divinity_key, DailyDivinityExecutor)
        if terminal is not None:
            return terminal
        terminal = recheck_online_gift()
        if terminal is not None:
            return terminal
        mingshi_key = (2, 7)
        item = items.get(mingshi_key)
        divinity_cleared = False
        clearance_reason = 'divinity_clear_required'
        if self.mingshi_enabled and item is not None and item.status == 'PENDING':
            if not self._wait_for_admission():
                return report('STOPPED', 'user_stop')
            # Account dispatches select one route at a time. Its filtered items
            # cannot carry the previous divinity dispatch's completion. Require
            # a fresh game-roster proof for this role and daily cycle instead.
            clearance = DailyDivinityExecutor(route_witnesses['mingshi'], self.token, stopped=self.stopped).observe_harvest_clearance(
                character_id=character_id, expected_cycle=cycle)
            if clearance.status in ('UNKNOWN', 'STOPPED'):
                items[mingshi_key] = replace(item, status=clearance.status, reason=clearance.reason)
                return report(clearance.status, clearance.reason)
            divinity_cleared = clearance.clearance_complete
            clearance_reason = clearance.reason
        if self.mingshi_enabled and item is not None and item.status == 'PENDING' and not divinity_cleared:
            items[mingshi_key] = replace(item, status='UNAVAILABLE', reason=clearance_reason)
            processed[mingshi_key] = items[mingshi_key]
        elif self.mingshi_enabled and item is not None and item.status == 'PENDING':
            if not self._wait_for_admission():
                return report('STOPPED', 'user_stop')
            report('RUNNING', 'daily_mingshi_pending')
            route_witnesses['mingshi'].executor_entered = True
            step = DailyMingshiExecutor(route_witnesses['mingshi'], self.token, stopped=self.stopped,
                                        paused=self.paused, wait=self.wait).execute(
                                            7, character_id=character_id, expected_cycle=cycle)
            if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if step.status == 'STOPPED' else 'UNKNOWN'
                current = (step.progress_after if step.account_progress_observed
                           and step.progress_before == item.current and step.required == item.required
                           and type(step.progress_after) is int and item.current <= step.progress_after <= item.required
                           else item.current)
                items[mingshi_key] = replace(item, status=status, reason=step.reason, current=current)
                return report(status, step.reason)
            if step.status == 'PROCESSED' and getattr(step, 'processed_without_reply', False) is True:
                if step.cleanup != 'VERIFIED_CLOSED' or not step.mutation_attempted:
                    items[mingshi_key] = replace(item, status='UNKNOWN', reason='daily_mingshi_processed_evidence_incomplete')
                    return report('UNKNOWN', items[mingshi_key].reason)
                current = (step.progress_after if type(step.progress_after) is int
                           and item.current <= step.progress_after <= item.required else item.current)
                items[mingshi_key] = replace(item, status='PROCESSED', reason=step.reason, current=current,
                    processed_without_reply=True, request_issued=True, reply_received=bool(step.stock_callback_returned))
            elif step.status == 'COMPLETED' and getattr(step, 'page_confirmed', False) is True:
                if step.cleanup != 'VERIFIED_CLOSED' or not step.mutation_attempted:
                    items[mingshi_key] = replace(item, status='UNKNOWN', reason='daily_mingshi_page_evidence_incomplete')
                    return report('UNKNOWN', items[mingshi_key].reason)
                current = (step.progress_after if type(step.progress_after) is int
                           and item.current <= step.progress_after <= item.required else item.current)
                items[mingshi_key] = replace(item, status='COMPLETED', reason=step.reason, current=current,
                    page_confirmed=True, request_issued=True, reply_received=bool(step.stock_callback_returned))
            elif step.status in ('COMPLETED', 'ALREADY_COMPLETE'):
                if (step.status == 'COMPLETED' and (step.cleanup != 'VERIFIED_CLOSED'
                        or step.progress_before != item.current or not step.mutation_attempted
                        or not step.stock_callback_returned or not step.account_progress_observed)):
                    items[mingshi_key] = replace(item, status='UNKNOWN', reason='daily_mingshi_evidence_incomplete')
                    return report('UNKNOWN', 'daily_mingshi_evidence_incomplete')
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None:
                    items[mingshi_key] = replace(item, status='UNKNOWN', reason=reason)
                    return report('UNKNOWN', reason)
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                               stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled, refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled, soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(mingshi_key)
                if (latest is None or latest.status != 'ALREADY_COMPLETE'
                        or latest.required != item.required or step.required != item.required
                        or latest.current != step.progress_after or latest.current != item.required):
                    items[mingshi_key] = replace(item, status='UNKNOWN', reason='daily_mingshi_progress_mismatch')
                    return report('UNKNOWN', 'daily_mingshi_progress_mismatch')
                conflict = merge_fresh(fresh)
                if conflict:
                    return report('UNKNOWN', conflict)
                items[mingshi_key] = replace(latest, status=step.status, reason=step.reason)
            else:
                items[mingshi_key] = replace(item, status='UNAVAILABLE', reason=step.reason)
            processed[mingshi_key] = items[mingshi_key]
        soul_key = (2, 5)
        item = items.get(soul_key)
        if self.soul_enabled and item is not None and item.status in ('PENDING', 'ALREADY_COMPLETE', 'COMPLETED'):
            if not self._wait_for_admission():
                items[soul_key] = replace(item, status='STOPPED', reason='user_stop')
                return report('STOPPED', 'user_stop')
            items[soul_key] = replace(item, status='PENDING', reason='daily_soul_pending')
            report('RUNNING', 'daily_soul_pending')
            step = SoulExecutor(self.objects, self.token, stopped=self.stopped,
                                     paused=self.paused, wait=self.wait,
                                     resource_policy_provider=lambda: (
                                         self.objects.resource_policy_provider()
                                         if getattr(self.objects, 'resource_policy_provider', None) else None),
                                     evidence_dir=Path(self.data_dir) / 'soul-evidence').execute(
                                         character_id=character_id, expected_cycle=cycle,
                                         expected_current=item.current, expected_required=item.required,
                                         blue_count=1, purple_count=1,
                                         free_only=not soul_paid_allowed(
                                             getattr(self.objects, 'resource_policy_provider', None)))
            if step.evidence_path:
                ledger_paths.append(step.evidence_path)
            if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if step.status == 'STOPPED' else 'UNKNOWN'
                current = (step.progress_after if step.account_progress_observed
                           and step.progress_before == item.current and step.required == item.required
                           and type(step.progress_after) is int and item.current <= step.progress_after <= item.required
                           else item.current)
                items[soul_key] = replace(item, status=status, reason=step.reason, current=current,
                    soul_blue_completed=step.blue_completed, soul_purple_completed=step.purple_completed,
                    soul_skipped=tuple((entry.color, entry.reason) for entry in step.skipped))
                return report(status, step.reason)
            if step.status not in ('COMPLETED', 'ALREADY_COMPLETE'):
                current = (step.progress_after if step.account_progress_observed
                           and step.progress_before == item.current and step.required == item.required
                           and type(step.progress_after) is int and item.current <= step.progress_after <= item.required
                           else item.current)
                items[soul_key] = replace(item, status='UNAVAILABLE', reason=step.reason, current=current,
                    soul_blue_completed=step.blue_completed, soul_purple_completed=step.purple_completed,
                    soul_skipped=tuple((entry.color, entry.reason) for entry in step.skipped))
                processed[soul_key] = items[soul_key]
            else:
                if not _daily_soul_plan_complete(step, item):
                    items[soul_key] = replace(item, status='UNKNOWN', reason='daily_soul_evidence_incomplete')
                    return report('UNKNOWN', 'daily_soul_evidence_incomplete')
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None:
                    items[soul_key] = replace(item, status='UNKNOWN', reason=reason)
                    return report('UNKNOWN', reason)
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                               stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled,
                               refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled,
                               soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(soul_key)
                if (latest is None or latest.status not in ('PENDING', 'ALREADY_COMPLETE')
                        or latest.required != item.required or latest.current != step.progress_after):
                    items[soul_key] = replace(item, status='UNKNOWN', reason='daily_soul_progress_mismatch')
                    return report('UNKNOWN', 'daily_soul_progress_mismatch')
                conflict = merge_fresh(fresh)
                if conflict:
                    return report('UNKNOWN', conflict)
                final_status = ('COMPLETED' if step.status == 'COMPLETED' else 'ALREADY_COMPLETE') \
                    if latest.status == 'ALREADY_COMPLETE' else 'UNAVAILABLE'
                items[soul_key] = replace(latest, status=final_status, reason=step.reason,
                    soul_blue_completed=step.blue_completed, soul_purple_completed=step.purple_completed,
                    soul_skipped=tuple((entry.color, entry.reason) for entry in step.skipped))
                processed[soul_key] = items[soul_key]
        refine_key = (2, 21)
        item = items.get(refine_key)
        if self.refine_enabled and item is not None and item.status in ('PENDING', 'ALREADY_COMPLETE', 'COMPLETED'):
            if not self._wait_for_admission():
                items[refine_key] = replace(item, status='STOPPED', reason='user_stop')
                return report('STOPPED', 'user_stop')
            # The activity counter can already be 1/1 while ordinary
            # experience exchanges remain in the separate shared quota.
            items[refine_key] = replace(item, kind='activity', status='PENDING',
                                        reason='daily_refine_pending', current=None, required=10)
            report('RUNNING', 'daily_refine_pending')
            step = DailyRefineExecutor(self.objects, self.token, stopped=self.stopped,
                                       paused=self.paused, wait=self.wait).execute(
                                           21, character_id=character_id, expected_cycle=cycle,
                                           expected_current=item.current, expected_required=item.required)
            quota_current = (10 - step.remaining_exchanges if type(step.remaining_exchanges) is int
                             and 0 <= step.remaining_exchanges <= 10 else None)
            if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED' or step.ui_ownership_held is True:
                status = 'STOPPED' if step.status == 'STOPPED' else 'UNKNOWN'
                items[refine_key] = replace(items[refine_key], status=status, reason=step.reason,
                                            current=quota_current)
                return report(status, step.reason)
            if step.status not in ('COMPLETED', 'ALREADY_COMPLETE'):
                items[refine_key] = replace(items[refine_key], status='UNAVAILABLE', reason=step.reason,
                                            current=quota_current)
                processed[refine_key] = items[refine_key]
            else:
                if not _refine_quota_complete(step, item, cycle):
                    items[refine_key] = replace(items[refine_key], status='UNKNOWN',
                                                reason='daily_refine_evidence_incomplete', current=quota_current)
                    return report('UNKNOWN', 'daily_refine_evidence_incomplete')
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None:
                    items[refine_key] = replace(items[refine_key], status='UNKNOWN', reason=reason,
                                                current=quota_current)
                    return report('UNKNOWN', reason)
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                               stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled,
                               refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled,
                               soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(refine_key)
                if (latest is None or latest.status not in ('PENDING', 'ALREADY_COMPLETE')
                        or latest.required != item.required or latest.current != step.progress_after):
                    items[refine_key] = replace(items[refine_key], status='UNKNOWN',
                                                reason='daily_refine_progress_mismatch', current=quota_current)
                    return report('UNKNOWN', 'daily_refine_progress_mismatch')
                conflict = merge_fresh(fresh)
                if conflict:
                    return report('UNKNOWN', conflict)
                status = ('COMPLETED' if step.status == 'COMPLETED' else 'ALREADY_COMPLETE')
                if latest.status != 'ALREADY_COMPLETE':
                    status = 'UNAVAILABLE'
                items[refine_key] = replace(latest, kind='activity', status=status,
                    reason=step.reason if status != 'UNAVAILABLE' else 'refine_daily_counter_unmet',
                    current=10, required=10)
                processed[refine_key] = items[refine_key]
        wood_key = (3, 22)
        item = items.get(wood_key)
        if self.wood_enabled and item is not None and item.status in ('PENDING', 'ALREADY_COMPLETE', 'COMPLETED'):
            if not self._wait_for_admission():
                items[wood_key] = replace(item, status='STOPPED', reason='user_stop')
                return report('STOPPED', 'user_stop')
            epoch = wood_quota_epoch(observed['server_now'], observed['server_day'])
            if epoch is None:
                items[wood_key] = replace(item, status='BLOCKED', reason='daily_wood_quota_epoch_unverified')
                return report('BLOCKED', 'daily_wood_quota_epoch_unverified')
            items[wood_key] = replace(item, status='PENDING', reason='daily_wood_pending')
            report('RUNNING', 'daily_wood_pending')
            step = DailyWoodExecutor(self.objects, self.token, stopped=self.stopped,
                                     paused=self.paused, wait=self.wait, clock=self.clock).execute(
                                         22, character_id=character_id, expected_cycle=cycle, expected_quota_epoch=epoch,
                                         expected_current=item.current, expected_required=item.required)
            if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if step.status == 'STOPPED' and step.cleanup != 'UNVERIFIED' else 'UNKNOWN'
                current = (step.progress_after if step.task_confirmed and step.mutation_attempted
                           and step.progress_before == item.current and step.required == item.required
                           and step.progress_after == min(item.current + 1, item.required) else item.current)
                items[wood_key] = replace(item, status=status, reason=step.reason, current=current)
                return report(status, step.reason)
            if step.status == 'COMPLETED':
                if (step.cleanup != 'VERIFIED_CLOSED' or step.checklist_complete is not True
                        or step.remaining_quota != 0 or step.progress_before != item.current
                        or step.required != item.required or step.progress_after not in (item.current, item.required)
                        or type(step.free_attempts) is not int or step.free_attempts < 0
                        or type(step.rewards_claimed) is not int or step.rewards_claimed < 0
                        or (step.free_attempts > 0 and step.task_confirmed is not True)
                        or (step.mutation_attempted and step.free_attempts + step.rewards_claimed < 1)):
                    items[wood_key] = replace(item, status='UNKNOWN', reason='daily_wood_evidence_incomplete')
                    return report('UNKNOWN', 'daily_wood_evidence_incomplete')
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None or wood_quota_epoch(observed['server_now'], observed['server_day']) != epoch:
                    reason = reason or 'daily_wood_quota_epoch_changed'
                    items[wood_key] = replace(item, status='UNKNOWN', reason=reason, current=step.progress_after)
                    return report('UNKNOWN', reason)
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                               stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled,
                               refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled,
                               soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(wood_key)
                if (latest is None or latest.current != step.progress_after or latest.required != item.required):
                    items[wood_key] = replace(item, status='UNKNOWN', reason='daily_wood_progress_mismatch',
                                              current=step.progress_after)
                    return report('UNKNOWN', 'daily_wood_progress_mismatch')
                conflict = merge_fresh(fresh)
                if conflict:
                    return report('UNKNOWN', conflict)
                items[wood_key] = replace(latest,
                    status='COMPLETED' if latest.status == 'ALREADY_COMPLETE' else 'UNAVAILABLE',
                    reason=step.reason if latest.status == 'ALREADY_COMPLETE' else 'wood_daily_counter_unmet',
                    current=None, required=None)
            else:
                items[wood_key] = replace(item, status='UNAVAILABLE', reason=step.reason,
                                          current=None, required=None)
            processed[wood_key] = items[wood_key]
        arena_key = (6, 3)
        item = items.get(arena_key)
        if self.arena_enabled and item is not None and item.status in ('PENDING', 'ALREADY_COMPLETE'):
            report('RUNNING', 'daily_arena_pending')
            step = DailyArenaChestExecutor(self.objects, self.token, stopped=self.stopped,
                                           paused=self.paused, wait=self.wait).execute(
                                               3, character_id=character_id, expected_cycle=cycle)
            if step.status in ('UNKNOWN', 'STOPPED', 'CLAIMED') or step.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if step.status == 'STOPPED' else 'UNKNOWN'
                current = (step.progress_after if step.account_progress_observed
                           and step.progress_before == item.current and step.required == item.required
                           and type(step.progress_after) is int and item.current <= step.progress_after <= item.required
                           else item.current)
                items[arena_key] = replace(item, status=status, reason=step.reason, current=current)
                return report(status, step.reason)
            if step.status in ('COMPLETED', 'ALREADY_COMPLETE'):
                if (not step.all_current_free_rewards_claimed or not step.bonus_scan_complete
                        or step.free_rewards_remaining != 0 or step.progress_before != item.current
                        or step.required != item.required
                        or (step.claims_completed > 0 and (step.cleanup != 'VERIFIED_CLOSED'
                            or not step.stock_callback_returned or not step.reward_claim_observed))
                        or (item.current < item.required and step.claims_completed > 0
                            and (not step.account_progress_observed or not step.activity_push_returned))):
                    items[arena_key] = replace(item, status='UNKNOWN', reason='daily_arena_evidence_incomplete')
                    return report('UNKNOWN', 'daily_arena_evidence_incomplete')
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None:
                    items[arena_key] = replace(item, status='UNKNOWN', reason=reason)
                    return report('UNKNOWN', reason)
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                               stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled,
                               refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled, soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(arena_key)
                expected = min(item.required, item.current + step.claims_completed)
                if (latest is None or latest.required != item.required
                        or latest.current != step.progress_after or latest.current != expected):
                    items[arena_key] = replace(item, status='UNKNOWN', reason='daily_arena_progress_mismatch')
                    return report('UNKNOWN', 'daily_arena_progress_mismatch')
                conflict = merge_fresh(fresh)
                if conflict:
                    return report('UNKNOWN', conflict)
                items[arena_key] = replace(latest,
                    status='COMPLETED' if latest.status == 'ALREADY_COMPLETE' else 'UNAVAILABLE',
                    reason=step.reason)
            else:
                items[arena_key] = replace(item, status='UNAVAILABLE', reason=step.reason)
            processed[arena_key] = items[arena_key]
        if self.guild_enabled:
            guild = DailyGuildExecutor(route_witnesses['guild_donation'], self.token, donation_type=self.donation_type,
                stopped=self.stopped, paused=self.paused, wait=self.wait)
            # Donation can add contribution needed by the free guild draw.
            for key in ((5, 20), (5, 18)):
                item = items.get(key)
                if item is None or item.status != 'PENDING':
                    continue
                if not self._wait_for_admission():
                    return report('STOPPED', 'user_stop')
                report('RUNNING', 'daily_guild_pending')
                if key == (5, 20):
                    route_witnesses['guild_donation'].executor_entered = True
                step = guild.execute(key[1], character_id=character_id,
                    **({'expected_cycle': cycle} if self.retry_source is not None else {}))
                if step.status in ('UNKNOWN', 'STOPPED'):
                    items[key] = replace(item, status=step.status, reason=step.reason)
                    return report(step.status, step.reason)
                if step.status == 'PROCESSED' and getattr(step, 'processed_without_reply', False) is True:
                    if (getattr(step, 'cleanup', '') != 'VERIFIED_CLOSED' or not step.closed
                            or getattr(step, 'request_issued', False) is not True
                            or getattr(step, 'reply_received', None) is not False):
                        items[key] = replace(item, status='UNKNOWN', reason='daily_guild_processed_evidence_incomplete')
                        return report('UNKNOWN', items[key].reason, cleanup_pending=True)
                    items[key] = replace(item, status='PROCESSED', reason=step.reason, current=step.progress_after,
                        processed_without_reply=True, request_issued=True, reply_received=False,
                        cleanup=step.cleanup, cleanup_pending=False)
                    processed[key] = items[key]
                    continue
                if step.status == 'COMPLETED' and getattr(step, 'page_confirmed', False) is True:
                    if getattr(step, 'cleanup', '') != 'VERIFIED_CLOSED' or not step.closed:
                        items[key] = replace(item, status='UNKNOWN', reason='daily_guild_page_cleanup_unverified')
                        return report('UNKNOWN', items[key].reason, cleanup_pending=True)
                    items[key] = replace(item, status='COMPLETED', reason=step.reason, current=step.progress_after,
                        page_confirmed=True, request_issued=True, reply_received=getattr(step, 'reply_received', False) is True,
                        cleanup=step.cleanup, cleanup_pending=False)
                    processed[key] = items[key]
                    continue
                if step.status != 'COMPLETED':
                    items[key] = replace(item, status='UNAVAILABLE', reason=step.reason)
                    processed[key] = items[key]
                    continue
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None:
                    items[key] = replace(item, status='UNKNOWN', reason=reason)
                    return report('UNKNOWN', reason)
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled, stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled, refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled, soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(key)
                if (latest is None or latest.status != 'ALREADY_COMPLETE'
                        or latest.required != item.required or latest.current != step.progress_after
                        or step.progress_before != item.current or step.progress_after != item.current + 1):
                    items[key] = replace(item, status='UNKNOWN', reason='daily_guild_progress_mismatch')
                    return report('UNKNOWN', 'daily_guild_progress_mismatch')
                conflict = merge_fresh(fresh)
                if conflict:
                    return report('UNKNOWN', conflict)
                items[key] = replace(latest, status='COMPLETED', reason='daily_guild_progress_observed')
                processed[key] = items[key]
        def run_point_race(*, cooldown_due=False):
            nonlocal point_race_deadline, point_race_cleanup
            key = (3, 109)  # Assistant checklist entry, not a server activity ID.
            if not self._route_enabled('point_race'):
                items[key] = DailyItemResult('activity', 3, 109, '分组积分赛挑战',
                                             'SKIPPED', 'task_not_enabled', None, None)
                return None
            if point_race_deadline is not None and not cooldown_due:
                return None
            point_race_deadline = None
            cooldown_checks.pop('point_race', None)
            read_only_releases.pop('point_race', None)
            if not self._wait_for_admission():
                return report('STOPPED', 'user_stop')
            items[key] = DailyItemResult('activity', 3, 109, '分组积分赛挑战',
                                         'PENDING', 'pointrace_pending', None, 10)
            report('RUNNING', 'daily_script_pending')
            route_witnesses['point_race'].executor_entered = True
            step = DailyPointRaceExecutor(route_witnesses['point_race'], self.token, stopped=self.stopped,
                paused=self.paused, wait=self.wait).execute(
                    109, character_id=character_id, expected_required=10, expected_cycle=cycle)
            current = step.progress_after if step.progress_after is not None else step.progress_before
            if step.status == 'WAITING':
                seconds = getattr(step, 'cooldown_remaining_seconds', None)
                operation = getattr(step, 'operation_id', None)
                read_only_release = (operation == '' and step.cleanup == 'NOT_NEEDED'
                                     and getattr(step, 'release_verified', None) is True)
                owned_closed = (isinstance(operation, str) and len(operation) == 32
                                and all(value in '0123456789abcdef' for value in operation)
                                and step.cleanup == 'VERIFIED_CLOSED')
                if (type(seconds) is not int or seconds <= 0
                        or getattr(step, 'role_id', None) != character_id
                        or getattr(step, 'refresh_cycle', None) != cycle
                        or getattr(step, 'release_verified', None) is not True
                        or not (owned_closed or read_only_release)):
                    items[key] = replace(items[key], status='UNKNOWN',
                                         reason='point_race_cooldown_evidence_incomplete', current=current)
                    return report('UNKNOWN', items[key].reason, cleanup_pending=step.cleanup != 'VERIFIED_CLOSED')
                point_race_deadline = self.clock() + seconds
                point_race_cleanup = step.cleanup
                cooldown_checks['point_race'] = self.clock() + 60.0
                if read_only_release:
                    read_only_releases['point_race'] = {
                        'route': 'point_race', 'release': 'VERIFIED_IDLE', 'role_id': step.role_id,
                        'refresh_cycle': step.refresh_cycle, 'operation_id': operation,
                        'cleanup': step.cleanup, 'release_verified': step.release_verified,
                    }
                items[key] = replace(items[key], status='PENDING', reason=step.reason + ':' + str(seconds),
                                     current=current)
                return None
            if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED':
                status = 'STOPPED' if step.status == 'STOPPED' else 'UNKNOWN'
                pending = bool(getattr(step, 'cleanup_pending', False) or step.cleanup == 'UNVERIFIED')
                entry_timeout_released = (
                    status == 'UNKNOWN' and step.reason == 'pointrace_entry_callback_timeout'
                    and getattr(step, 'entry_timeout_released', False) is True
                    and getattr(step, 'release_verified', False) is True
                    and step.cleanup == 'VERIFIED_CLOSED' and not pending
                    and getattr(step, 'role_id', None) == character_id
                    and getattr(step, 'refresh_cycle', None) == cycle)
                items[key] = replace(items[key], status=status, reason=step.reason, current=current,
                                     cleanup=step.cleanup, cleanup_pending=pending,
                                     operation_id=step.operation_id)
                if (status == 'STOPPED' or pending or not entry_timeout_released
                        or self.selected_items is not None and len(self.selected_items) == 1):
                    return report(status, step.reason, cleanup_pending=pending)
                # The entry stays UNKNOWN. Its detached listener and verified
                # home release admit other routes, never a retry of this entry.
                processed[key] = items[key]
                return None
            status = 'COMPLETED' if step.status in ('COMPLETED', 'ALREADY_COMPLETE') else 'UNAVAILABLE'
            items[key] = replace(items[key], status=status, reason=step.reason, current=current)
            return None

        script_routes = (
            ((2, 17), DailyGraveExecutor),
            ((3, 9), DailyArenaBattleExecutor),
            ((3, 11), DailyFlowerExecutor),
            ((4, 10), DailyMonsterExecutor),
            ((4, 14), DailyTrialExecutor),
            ((4, 15), DailyMonsterExecutor),
            ((6, 24), DailyMonsterExecutor),
        )
        for key, executor_type in script_routes:
            terminal = run_script(key, executor_type)
            if terminal is not None:
                return terminal
            if key == (3, 9):
                terminal = run_point_race()
                if terminal is not None:
                    return terminal
            terminal = recheck_online_gift()
            if terminal is not None:
                return terminal

        terminal = run_city_activity(306, 'spirit_tree', DailyCityGatherExecutor)
        if terminal is not None:
            return terminal
        terminal = run_city_activity(308, 'city_shop', DailyCityShopExecutor,
                                     duration_seconds=self.shop_duration)
        if terminal is not None:
            return terminal
        terminal = recheck_online_gift()
        if terminal is not None:
            return terminal

        timed = (DailyTimedExecutor(self.objects, self.token, stopped=self.stopped,
                                   paused=self.paused, wait=self.wait) if self.timed_enabled else None)

        def run_timed(key):
            nonlocal observed, last_server_time
            item = items.get(key)
            if timed is None or item is None or item.status != 'PENDING':
                return None
            if not self._wait_for_admission():
                return 'STOPPED', 'user_stop'
            report('RUNNING', 'daily_timed_pending')
            step = timed.execute(key[1], character_id=character_id)
            if step.status in ('UNKNOWN', 'STOPPED'):
                items[key] = replace(item, status=step.status, reason=step.reason)
                return step.status, step.reason
            if step.status not in ('COMPLETED', 'ALREADY_COMPLETE', 'PARTIAL'):
                items[key] = replace(item, status='UNAVAILABLE', reason=step.reason)
                processed[key] = items[key]
                if step.cleanup != 'NOT_NEEDED':
                    return 'PARTIAL', step.reason
                return None
            if step.cleanup == 'UNVERIFIED':
                items[key] = replace(item, status='UNKNOWN', reason='daily_timed_cleanup_unverified')
                return 'UNKNOWN', 'daily_timed_cleanup_unverified'
            if (step.status == 'COMPLETED' and
                    (key[1] == 12 and step.cleanup != 'VERIFIED_CLOSED' or
                     key[1] == 13 and step.cleanup != 'BRAWL_MAY_REMAIN_OPEN')):
                items[key] = replace(item, status='UNKNOWN', reason='daily_timed_cleanup_unverified')
                return 'UNKNOWN', 'daily_timed_cleanup_unverified'
            if (step.required != item.required or step.progress_after is None
                    or step.progress_after < item.current or step.progress_after > item.required):
                items[key] = replace(item, status='UNKNOWN', reason='daily_timed_progress_mismatch')
                return 'UNKNOWN', 'daily_timed_progress_mismatch'
            if key[1] == 12 and step.cleanup == 'VERIFIED_CLOSED':
                observed, reason = self._observe(character_id, cycle, last_server_time)
                if observed is None:
                    items[key] = replace(item, status='UNKNOWN', reason=reason)
                    return 'UNKNOWN', reason
                last_server_time = observed['server_now']
                fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled, stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled, refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled, soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled, selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
                latest = fresh.get(key)
                if (latest is None or latest.status not in ('PENDING', 'ALREADY_COMPLETE')
                        or latest.required != item.required or latest.current != step.progress_after):
                    items[key] = replace(item, status='UNKNOWN', reason='daily_timed_progress_mismatch')
                    return 'UNKNOWN', 'daily_timed_progress_mismatch'
                conflict = merge_fresh(fresh)
                if conflict:
                    return 'UNKNOWN', conflict
            status = 'COMPLETED' if step.progress_after >= item.required else 'UNAVAILABLE'
            reason = ('daily_brawl_view_remains' if key[1] == 13 and step.cleanup == 'BRAWL_MAY_REMAIN_OPEN'
                      else step.reason)
            items[key] = replace(item, status=status, reason=reason, current=step.progress_after)
            processed[key] = items[key]
            return None

        terminal = run_timed((3, 12))
        if terminal:
            return report(*terminal)
        if self.rewards_enabled and self._route_enabled('category_rewards'):
            categories = {row['category_id']: row for row in observed['categories']}
            rewards = DailyRewardExecutor(self.objects, self.token, stopped=self.stopped,
                                           paused=self.paused, wait=self.wait)
            for category_id in range(2, 7):
                category = categories.get(category_id)
                key = (category_id, None)
                item = items.get(key)
                if (item is None or category is None or category['reward_ready'] is not True
                        or category['reward_claimed'] is True):
                    continue
                if any(row['task_id'] != 19 and _task_result(category, row).reason == 'daily_progress_inconsistent'
                       for row in category['tasks']):
                    items[key] = replace(item, status='UNAVAILABLE', reason='daily_progress_inconsistent')
                    continue
                if not self._wait_for_admission():
                    return report('STOPPED', 'user_stop')
                report('RUNNING', 'daily_reward_pending')
                step = rewards.execute(category_id, character_id=character_id)
                if step.status == 'DISABLED':
                    break
                if step.status in ('UNKNOWN', 'STOPPED'):
                    items[key] = replace(item, status=step.status, reason=step.reason)
                    return report(step.status, step.reason)
                if step.status in ('COMPLETED', 'ALREADY_CLAIMED'):
                    items[key] = replace(item, status=step.status, reason=step.reason)
                    continue
                items[key] = replace(item, status='UNAVAILABLE', reason=step.reason)
                if step.operation_id and not step.panel_closed:
                    return report('PARTIAL', step.reason)
        terminal = run_timed((6, 13))
        if terminal:
            return report(*terminal)
        # Clearing all training monsters can include long target cooldowns.
        # Finish other available daily activities first, then continue the
        # user's requested clearing and free-summon sequence.
        terminal = run_script((4, 23), DailyTrainingMonsterExecutor)
        if terminal is not None:
            return terminal
        terminal = run_city_activity(307, 'little_demon', DailyCityGatherExecutor)
        if terminal is not None:
            return terminal
        terminal = recheck_online_gift()
        if terminal is not None:
            return terminal
        # Each route has a real game due time and a separate observation
        # schedule. Events service popup/control changes; they never move the
        # observation schedule forward. All UI operations remain serial.
        cooldown_keys = {'copy': (7, 303), 'online_reward': (7, 1201), 'online_gift': (7, 309),
                         'training': (4, 23), 'arena': (3, 9), 'trial': (4, 14), 'point_race': (3, 109)}
        cooldown_reasons = {'copy': 'copy_cooldown:', 'online_reward': 'event_rewards_online_cooldown:',
                            'online_gift': 'online_gift_current_tier_not_due:',
                            'training': 'training_target_cooldown:', 'arena': 'arena_cooldown:',
                            'trial': 'trial_boss_cooldown:', 'point_race': 'pointrace_cooldown:'}

        def cooldown_routes():
            return tuple((name, deadline, cleanup) for name, deadline, cleanup in (
                ('copy', copy_deadline, copy_cleanup),
                ('online_reward', online_deadline, online_cleanup),
                ('online_gift', gift_deadline, gift_cleanup),
                ('training', training_deadline, training_cleanup),
                ('arena', arena_deadline, arena_cleanup),
                ('trial', trial_deadline, trial_cleanup),
                ('point_race', point_race_deadline, point_race_cleanup),
            ) if deadline is not None)

        def expire_cooldown(name):
            nonlocal copy_deadline, online_deadline, training_deadline, arena_deadline
            nonlocal trial_deadline, point_race_deadline
            nonlocal gift_deadline
            if name == 'copy':
                copy_deadline = None
            elif name == 'online_reward':
                online_deadline = None
            elif name == 'online_gift':
                gift_deadline = None
            elif name == 'training':
                training_deadline = None
            elif name == 'arena':
                arena_deadline = None
            elif name == 'trial':
                trial_deadline = None
            else:
                point_race_deadline = None
            cooldown_checks.pop(name, None)
            read_only_releases.pop(name, None)
            key = cooldown_keys[name]
            if key in items:
                items[key] = replace(items[key], status='UNAVAILABLE', reason='daily_refresh_before_ready')

        def refresh_cooldown_context():
            nonlocal observed, last_server_time, cycle_deadline
            fresh_observed, reason = self._observe(character_id, cycle, last_server_time)
            if fresh_observed is None:
                return report('UNKNOWN', reason)
            observed = fresh_observed
            last_server_time = observed['server_now']
            cycle_deadline = self.clock() + max(0, cycle + 86400 - last_server_time)
            fresh = _items(observed, guild_enabled=self.guild_enabled, timed_enabled=self.timed_enabled,
                           stone_enabled=self.stone_enabled, mingshi_enabled=self.mingshi_enabled,
                           refine_enabled=self.refine_enabled, arena_enabled=self.arena_enabled,
                           soul_enabled=self.soul_enabled, wood_enabled=self.wood_enabled,
                           selected_items=self.selected_items, enabled_items=self.enabled_items, tribute_configured=self.tribute_options is not None)
            conflict = merge_fresh(fresh)
            return report('UNKNOWN', conflict) if conflict else None

        next_display = 0.0
        while cooldown_routes():
            if not self._wait_for_admission(cooldown=True):
                for waiting_key in cooldown_keys.values():
                    waiting_item = items.get(waiting_key)
                    if waiting_item is not None and waiting_item.status in ('PENDING', 'UNAVAILABLE'):
                        items[waiting_key] = replace(waiting_item, status='STOPPED', reason='user_stop')
                return report('STOPPED', 'user_stop')
            if self._workflow_event_error:
                return report('UNKNOWN', 'daily_cooldown_connection_failed (' + self._workflow_event_error + ')')
            now = self.clock()
            if now >= cycle_deadline:
                for name, _, _ in cooldown_routes():
                    expire_cooldown(name)
                break
            for name, deadline, _ in cooldown_routes():
                if deadline >= cycle_deadline:
                    expire_cooldown(name)
            if not self.defer_cooldowns and all(name == 'online_gift' for name, _, _ in cooldown_routes()):
                # Opportunistic gift collection never keeps a standalone
                # daily run online after its other work/waits have ended.
                terminal = recheck_online_gift()
                if terminal is not None:
                    return terminal
                break
            if self._cooldown_resumed:
                # A user resume is an explicit new admission. If real game
                # readiness is overdue, read it now instead of trusting time.
                for name, deadline, _ in cooldown_routes():
                    if deadline <= now:
                        cooldown_checks[name] = now
                self._cooldown_resumed = False
            due = next((name for name, deadline, _ in cooldown_routes()
                        if now >= max(deadline, cooldown_checks.get(name, deadline))), None)
            if due is not None:
                terminal = refresh_cooldown_context()
                if terminal is not None:
                    return terminal
                if due == 'copy':
                    terminal = run_copy_activity()
                elif due == 'online_reward':
                    terminal = run_reward_activity(1201, '在线奖励')
                elif due == 'online_gift':
                    terminal = run_city_activity(309, 'online_gift', DailyOnlineGiftExecutor)
                elif due == 'training':
                    terminal = run_script((4, 23), DailyTrainingMonsterExecutor, cooldown_due=True)
                elif due == 'arena':
                    terminal = run_script((3, 9), DailyArenaBattleExecutor, cooldown_due=True)
                elif due == 'trial':
                    terminal = run_script((4, 14), DailyTrialExecutor, cooldown_due=True)
                else:
                    terminal = run_point_race(cooldown_due=True)
                if terminal is not None:
                    return terminal
                continue
            if not cooldown_routes():
                break
            if self.defer_cooldowns and all(deadline > now for _, deadline, _ in cooldown_routes()):
                # Each new workflow starts from fresh observations. These
                # deadlines are display/retry hints, never reusable quotes or
                # evidence that an account can progress while logged out.
                self._service_popups(force=True)
                if self.stopped():
                    return report('STOPPED', 'user_stop')
                if self.paused():
                    continue
                now = self.clock()
                deadlines = cooldown_routes()
                if any(deadline <= now for _, deadline, _ in deadlines):
                    continue
                cooldowns = tuple((name, max(1, int(deadline - now + .999)))
                                  for name, deadline, _ in deadlines)
                if self._popup_status not in ('IDLE', 'HANDLED'):
                    status = 'UNKNOWN' if self._popup_status == 'UNKNOWN' else 'BLOCKED'
                    return report(status, 'daily_cooldown_popup_unresolved',
                                  cooldown_items=cooldowns, cooldown_cleanup='UNVERIFIED')
                for name, _, cleanup in deadlines:
                    if cleanup != 'VERIFIED_CLOSED' and name not in read_only_releases:
                        # NOT_NEEDED proves this invocation did not open a
                        # panel; the cooldown projection does not prove that a
                        # pre-existing panel is closed. Do not release on that
                        # weaker claim.
                        return report('BLOCKED', 'daily_cooldown_cleanup_unverified:' + name,
                                      cooldown_items=cooldowns, cooldown_cleanup='UNVERIFIED')
                waiting_keys = {cooldown_keys[name] for name, _, _ in deadlines}
                if any(item.status == 'PENDING' and key not in waiting_keys for key, item in items.items()):
                    return report('BLOCKED', 'daily_pending_actions_before_cooldown_defer',
                                  cooldown_items=cooldowns, cooldown_cleanup='UNVERIFIED')
                if any(item.reason == 'daily_brawl_view_remains' for item in items.values()):
                    return report('BLOCKED', 'daily_cooldown_scene_unclosed:brawl',
                                  cooldown_items=cooldowns, cooldown_cleanup='UNVERIFIED')
                for name, seconds in cooldowns:
                    waiting_key = cooldown_keys[name]
                    waiting_item = items.get(waiting_key)
                    if waiting_item is not None and waiting_item.status != 'SKIPPED':
                        reason = cooldown_reasons[name]
                        items[waiting_key] = replace(waiting_item, status='PENDING', reason=reason + str(seconds))
                releases = tuple((name, 'VERIFIED_IDLE' if name in read_only_releases else 'VERIFIED_CLOSED')
                                 for name, _, _ in deadlines)
                release_evidence = tuple(dict(read_only_releases[name]) for name, _, _ in deadlines
                                         if name in read_only_releases)
                return report('WAITING', 'daily_cooldown_deferred', cooldown_items=cooldowns,
                              cooldown_cleanup='VERIFIED_RELEASED' if release_evidence else 'VERIFIED_CLOSED',
                              cooldown_releases=releases, cooldown_release_evidence=release_evidence)
            if now >= next_display:
                for name, deadline, _ in cooldown_routes():
                    key = cooldown_keys[name]
                    if key in items:
                        reason = ('daily_cooldown_recheck_pending' if deadline <= now else
                                  cooldown_reasons[name]
                                  + str(max(1, int(deadline - now + .999))))
                        items[key] = replace(items[key], status='PENDING', reason=reason)
                report('RUNNING', 'daily_cooldown_wait')
                next_display = now + 30
            wake_at = min(cycle_deadline, next_display,
                          *(deadline if deadline > now else cooldown_checks.get(name, now + 60.0)
                            for name, deadline, _ in cooldown_routes()))
            self._wait_for_cooldown(min(60.0, max(0.0, wake_at - now)))
        self._service_popups(force=True)
        enabled_items = (item for item in items.values() if item.status != 'SKIPPED')
        if all(item.status in _HANDLED_OUTCOMES for item in enabled_items) and self._popup_status in ('IDLE', 'HANDLED'):
            without_reply = any(item.status == 'PROCESSED' and item.processed_without_reply for item in items.values())
            return report('DONE', 'daily_items_processed_without_reply' if without_reply else 'daily_items_observed_complete')
        reason = ('daily_popup_unresolved' if self._popup_status in ('BLOCKED', 'WAITING', 'UNKNOWN') else
                  'daily_items_uncompleted' if task.status in _OBSERVED_COMPLETE else task.reason)
        if self.selected_items is not None and reason != 'daily_popup_unresolved':
            reason = next((item.reason for item in items.values()
                           if item.status not in _HANDLED_OUTCOMES and item.status != 'SKIPPED'),
                          'daily_items_uncompleted')
        retry_evidence = self._route_retry_evidence(character_id, cycle, last_server_time, items, route_witnesses)
        return report('PARTIAL', reason, route_retry_evidence=retry_evidence)
