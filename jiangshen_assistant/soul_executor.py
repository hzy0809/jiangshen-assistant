"""Independent soul gameplay, owned stock panels and exact resource receipts."""
from __future__ import annotations

from dataclasses import asdict, dataclass, replace
from copy import deepcopy
import json
import math
from pathlib import Path
import re
import threading
import time
import uuid

from .objects import CampaignAdmissionClosed, SoulPrepublicationAdmissionClosed
from .bridge_client import BridgeError
from .soul_strategy import SoulDecision, SoulPolicy, decide_soul_action
from .soul_exploration import EXPLORATION_REASONS
from .soul_chain_rules import analyze_chain
from .soul_economics import ECONOMIC_CONTEXT_SCHEMA
from .soul_policy import resolve_soul_policy
from .resource_policy import (ResourcePolicyDenied, read_policy, check_costs,
                              soul_money_budget)


_TASK, _CATEGORY = 5, 2
_ACTIONS = ('open', 'adopt', 'start', 'grow', 'harvest', 'close', 'next')
_OPCODES = {'': 0, 'next': 0, 'open': 0, 'adopt': 0, 'start': 517, 'grow': 519, 'harvest': 518, 'close': 0}
_STATUSES = {'IDLE', 'QUEUED', 'OPENING', 'READY', 'PENDING', 'CLOSING',
             'COMPLETED', 'REJECTED', 'STOPPED', 'UNKNOWN'}
_ACK_STATUSES = {'next': {'OPENING', 'READY'}, 'open': {'OPENING', 'READY'}, 'start': {'PENDING', 'READY'},
                 'adopt': {'OPENING', 'READY'},
                 'grow': {'PENDING', 'READY'},
                 'harvest': {'PENDING', 'READY'}, 'close': {'CLOSING', 'COMPLETED'}}
_TERMINAL = {'REJECTED', 'STOPPED', 'UNKNOWN'}
_TRANSITIONS = {
    'OPENING': {'OPENING', 'READY', *_TERMINAL},
    'READY': {'READY', *_TERMINAL},
    'PENDING': {'PENDING', 'READY', *_TERMINAL},
    'CLOSING': {'CLOSING', 'COMPLETED', *_TERMINAL},
}
_OFFERS = {'next': ('start',), 'open': ('start',), 'adopt': ('grow', 'harvest'), 'start': ('grow', 'harvest'),
           'grow': ('grow', 'harvest', 'close'), 'harvest': ('close',)}
_WITNESSES = ('invoked', 'mutation_attempted', 'start_verified', 'harvest_verified')
_CLEANUP_TRANSITIONS = {
    'none': {'none', 'waiting', 'closing', 'clear', 'unknown'},
    'waiting': {'waiting', 'closing', 'clear', 'unknown'},
    'closing': {'closing', 'clear', 'unknown'},
    'clear': {'clear'},
    'unknown': {'unknown'},
}


class SoulEvidenceWriteError(OSError):
    """A persistence sink failed before or after a stock action."""


@dataclass(frozen=True)
class SoulSkip:
    color: str
    reason: str


@dataclass(frozen=True)
class SoulActionEvidence:
    action: str
    state: str
    round: int
    revision: int
    sequence: int | None
    board_before: tuple
    coins_before: int | None
    quoted_cost: int
    spent_before: int
    decision: SoulDecision | None = None
    refusal_reason: str = ''


@dataclass(frozen=True)
class SoulGrowthReceipt:
    round: int
    sequence: int
    revision_before: int
    revision_after: int
    growth_before: int
    growth_after: int
    quoted_cost: int
    coins_before: int
    coins_after: int
    board_before: tuple
    board_after: tuple | None
    board_after_source: str
    growth_steps: tuple
    auto_harvest: bool
    award: tuple | None
    decision: SoulDecision
    chain_settlement: dict | None = None


@dataclass(frozen=True)
class SoulDecisionEvidence:
    round: int
    revision: int
    sequence: int
    board_source: str
    decision: SoulDecision


@dataclass(frozen=True)
class SoulRoundReceipt:
    round: int
    color: str
    mode: int
    start_cost: int
    coins_spent: int
    growth_count: int
    free_before: int
    free_after: int
    progress_before: int | None
    progress_after: int | None
    start_verified: bool
    harvest_verified: bool
    auto_harvest: bool
    start_sequence: int
    settle_sequence: int
    board_before_growth: tuple
    award: tuple
    growths: tuple[SoulGrowthReceipt, ...] = ()
    decisions: tuple[SoulDecisionEvidence, ...] = ()
    harvest_before_board: tuple | None = None
    harvest_slot_awards: tuple | None = None
    harvest_evidence_sequence: int | None = None
    native_harvest_receipt: dict | None = None
    adopted_board: bool = False
    adoption_sequence: int | None = None
    adoption_provenance: dict | None = None
    native_close_receipt: dict | None = None
    policy_source: str = ''


@dataclass(frozen=True)
class DailySoulResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    start_verified: bool = False
    harvest_verified: bool = False
    account_progress_observed: bool = False
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    free_before: int | None = None
    free_after: int | None = None
    blue_completed: int = 0
    purple_completed: int = 0
    coins_spent: int = 0
    rounds: tuple[SoulRoundReceipt, ...] = ()
    skipped: tuple[SoulSkip, ...] = ()
    quote: dict | None = None
    current_color: str = ''
    growths: tuple[SoulGrowthReceipt, ...] = ()
    decisions: tuple[SoulDecisionEvidence, ...] = ()
    pending_action: SoulActionEvidence | None = None
    policy: SoulPolicy | None = None
    coin_budget: int | None = None
    evidence_path: str = ''
    evidence_error: str = ''
    not_submitted_actions: tuple[SoulActionEvidence, ...] = ()
    failure_evidence_path: str = ''
    policy_source: str = ''



def _integer(value, minimum=0, maximum=0xFFFFFFFF):
    return type(value) is int and minimum <= value <= maximum


def _role_id(value):
    return (isinstance(value, str) and re.fullmatch(r'[1-9][0-9]{0,19}', value) is not None
            and int(value) <= 0xFFFFFFFFFFFFFFFF)


def _growth_metadata(s):
    board = s.get('board')
    return (isinstance(board, list) and len(board) == 8
            and all(isinstance(slot, dict) and _integer(slot.get('color'), 0, 4)
                    and _integer(slot.get('level'), 0, 10)
                    and (slot['color'] == 0) == (slot['level'] == 0) for slot in board)
            and 'coins' in s and (s['coins'] is None or _integer(s['coins'], 0, 0x7FFFFFFFFFFFFFFF))
            and 'growth_cost' in s and (s['growth_cost'] is None or _integer(s['growth_cost'], 1, 0x7FFFFFFFFFFFFFFF))
            and _integer(s.get('growth_count'), 0, 999999999))


def _growth_projection(s):
    return (_growth_metadata(s) and s['coins'] is not None and s['growth_cost'] is not None)


def _board_tuple(board):
    if (not isinstance(board, list) or len(board) != 8
            or any(not isinstance(slot, dict) or not _integer(slot.get('color'), 0, 4)
                   or not _integer(slot.get('level'), 0, 10)
                   or (slot['color'] == 0) != (slot['level'] == 0) for slot in board)):
        raise ValueError('soul_evidence_board_invalid')
    return tuple((slot['color'], slot['level']) for slot in board)


def _quotes_valid(s):
    quotes = s.get('quotes')
    if not isinstance(quotes, dict) or set(quotes) != {'blue', 'purple'}:
        return False
    for q in quotes.values():
        if (not isinstance(q, dict) or type(q.get('known')) is not bool or not isinstance(q.get('reason'), str)
                or any(q.get(key) is not None and type(q[key]) is not bool
                       for key in ('unlocked', 'free_unlocked', 'start_available'))):
            return False
        for key in ('free_remaining', 'used_today', 'start_cost', 'paid_start_cost'):
            if key not in q or q[key] is not None and not _integer(q[key], 0, 0xFFFFFFFF):
                return False
        if q['known'] and (any(type(q.get(key)) is not bool for key in ('unlocked', 'free_unlocked', 'start_available')) or any(q[k] is None for k in
                           ('free_remaining', 'used_today', 'start_cost', 'paid_start_cost'))):
            return False
        if q['known'] and (not _integer(q['used_today'], 0, 255) or not _integer(q['free_remaining'], 0, 255)
                           or q['unlocked'] != q['free_unlocked']
                           or q['start_cost'] != (0 if q['free_unlocked'] and q['free_remaining'] else q['paid_start_cost'])):
            return False
    return True


def soul_quote(s):
    """A loaded stock quotation, never a predicted aggregate growth bill."""
    if _snapshot_error(s):
        return None
    return dict(available=bool(_live_available(s) and not s['reason'] and s['action']),
                observation=dict(s.get('observation') or {'available': _live_available(s), 'reason': s['reason']}),
                reason=s['reason'], character_id=s['character_id'], cycle=s['cycle'],
                coins=s.get('coins'), growth_cost=s.get('growth_cost'), growth_variable=True,
                blue=dict(s['quotes']['blue']), purple=dict(s['quotes']['purple']))


def _live_available(s):
    """Only the current observation can admit work; a receipt cannot supply it."""
    return (s.get('observation', {}).get('available', True) is True
            and _role_id(s.get('character_id')) and _integer(s.get('cycle'), 1))


def _valid(s):
    """Fresh business projection required by the owned soul executor."""
    return not _snapshot_error(s) and _live_available(s)


def _adoption_valid(value):
    if value.get('adopted_board') is not True:
        return False
    try:
        board = _board_tuple(value.get('adoption_before_board'))
    except ValueError:
        return False
    source_session = value.get('adoption_source_session_id')
    source_operation = value.get('adoption_source_operation_id')
    proof = value.get('adoption_resolution_sha256')
    return (any(color for color, _ in board)
            and _integer(value.get('sequence'), 1, 999999999)
            and _integer(value.get('adoption_sequence'), 1, value.get('sequence', 0))
            and value.get('start_verified') is False and value.get('start_cost') == 0
            and _integer(value.get('coins_spent'), 0, 0x7FFFFFFFFFFFFFFF)
            and isinstance(source_session, str) and isinstance(source_operation, str)
            and isinstance(proof, str)
            and (source_session == source_operation == proof == '' or
                 bool(re.fullmatch(r'[0-9a-f]{32}', source_session))
                 and bool(re.fullmatch(r'[0-9a-f]{32}', source_operation))
                 and bool(re.fullmatch(r'[0-9a-f]{64}', proof))))


def _activity_required(value):
    return value.get('activity_required', False)


def _activity_shape(current, required):
    return ((current is None and required is None)
            or _integer(required, 1, 255) and (current is None or _integer(current, 0, required)))


def _activity_contract(value):
    if 'activity_required' in value and type(value['activity_required']) is not bool:
        return False
    observation = value.get('activity_observation')
    return (observation is None or isinstance(observation, dict)
            and type(observation.get('available')) is bool and isinstance(observation.get('reason'), str))


def _historical_receipt_valid(r):
    """The frozen receipt describes its own account and resources, never the scene."""
    if (not isinstance(r, dict) or not _activity_contract(r) or any(key not in r for key in
            ('growth_before_board', 'growth_after_board', 'growth_steps', 'growth_after_source',
             'growth_evidence_sequence', 'harvest_before_board', 'close_sequence'))
            or not _integer(r.get('schema'), 1, 1)
            or not _role_id(r.get('character_id')) or not _integer(r.get('server_id'), 1)
            or not _integer(r.get('cycle'), 1) or not _activity_shape(r.get('current'), r.get('required'))
            or not _integer(r.get('selected_mode'), 1, 2) or type(r.get('free_only')) is not bool
            or not _integer(r.get('round'), 1, 100)
            or not isinstance(r.get('operation_id'), str) or not re.fullmatch(r'[0-9a-f]{32}', r['operation_id'])
            or type(r.get('start_verified')) is not bool
            or not (r['start_verified'] or _adoption_valid(r))
            or any(r.get(key) is not True for key in ('harvest_verified', 'mutation_attempted'))
            or type(r.get('progress_verified')) is not bool
            or any(r.get(key) is not None and not _integer(r[key], 0, 255)
                   for key in ('counter_before', 'counter_after'))
            or any(not _integer(r.get(key), 0, 0x7FFFFFFFFFFFFFFF) for key in ('start_cost', 'coins_spent'))
            or not _integer(r.get('growth_count'), 0, 999999999) or type(r.get('auto_harvest')) is not bool
            or not isinstance(r.get('award'), list) or len(r['award']) != 4
            or any(not _integer(x, 0, 2040) for x in r['award'])
            or not _integer(r.get('harvest_sequence'), 1, 999999999)
            or type(r.get('harvest_evidence_sequence')) is not int
            or r['harvest_evidence_sequence'] != r['harvest_sequence']
            or not _integer(r.get('sequence'), r['harvest_sequence'], 999999999)
            or type(r.get('close_verified')) is not bool):
        return False
    if _activity_required(r) and (not _integer(r.get('required'), 1, 255)
            or not _integer(r.get('current'), 0, r['required'])
            or not _integer(r.get('counter_before'), 0, r['required'])
            or not _integer(r.get('counter_after'), 0, r['required'])
            or r['counter_after'] != min(r['counter_before'] + 1, r['required'])
            or r['current'] != r['counter_after'] or not r['progress_verified']):
        return False
    for key in ('resources_before', 'resources_after'):
        resources = r.get(key)
        if (not isinstance(resources, dict) or any(not _integer(resources.get(name), 0, 0x7FFFFFFFFFFFFFFF)
                for name in ('coins', 'gold', 'bound_gold', 'gold_spent', 'exchanges',
                             'blue_used', 'purple_used', 'orange_used'))
                or not isinstance(resources.get('inventory'), list) or len(resources['inventory']) != 4
                or any(not _integer(x, 0, 0x7FFFFFFF) for x in resources['inventory'])):
            return False
        try:
            _board_tuple(resources.get('board'))
        except ValueError:
            return False
    before, after = r['resources_before'], r['resources_after']
    if (before['coins'] - after['coins'] != r['coins_spent']
            or any(before[key] != after[key] for key in ('gold', 'bound_gold', 'gold_spent', 'exchanges', 'orange_used'))
            or any(after['inventory'][i] != before['inventory'][i] + r['award'][i] for i in range(4))):
        return False
    for key in ('growth_before_board', 'growth_after_board', 'harvest_before_board'):
        if key not in r:
            return False
        if r[key] is not None:
            try:
                _board_tuple(r[key])
            except ValueError:
                return False
    growth_sequence = r.get('growth_evidence_sequence')
    if growth_sequence is None:
        if any(r.get(key) is not None for key in
               ('growth_before_board', 'growth_after_board', 'growth_steps', 'growth_after_source')):
            return False
    else:
        if (not _integer(growth_sequence, 1, r['harvest_sequence']) or r.get('growth_after_source') != 'decoded_response'
                or r['growth_before_board'] is None or r['growth_after_board'] is None
                or not isinstance(r.get('growth_steps'), list) or not 1 <= len(r['growth_steps']) <= 64):
            return False
        try:
            for board in r['growth_steps']:
                _board_tuple(board)
        except ValueError:
            return False
        if r['growth_steps'][-1] != r['growth_after_board']:
            return False
    slots = r.get('harvest_slot_awards')
    if (not isinstance(slots, list) or len(slots) != 8 or any(not isinstance(slot, dict)
            or not _integer(slot.get('color'), 0, 4) or not _integer(slot.get('number'), 0, 0x7FFFFFFF) for slot in slots)):
        return False
    if r.get('phase') == 'CLOSE_VERIFIED':
        return (r.get('stage') == 'close' and r.get('status') == 'COMPLETED' and r['close_verified']
                and _integer(r.get('close_sequence'), r['harvest_sequence'] + 1, 999999999)
                and r['sequence'] == r['close_sequence'] and r.get('ui_cleanup') == 'clear')
    return (r.get('phase') == 'HARVEST_VERIFIED' and r.get('stage') in ('harvest', 'grow')
            and r.get('status') == 'READY' and not r['close_verified'] and r.get('close_sequence') is None
            and r['sequence'] == r['harvest_sequence'] and r.get('ui_cleanup') in ('none', 'waiting', 'closing', 'clear'))


def _receipt_matches_operation(s):
    r, op = s.get('historical_receipt'), s['operation']
    return (isinstance(r, dict) and r['operation_id'] == op['operation_id']
            and r['selected_mode'] == op['mode'] and r['free_only'] == op['free_only']
            and r['round'] == op['round'] and r['sequence'] == op['sequence']
            and r['growth_count'] == s.get('growth_count')
            and r['stage'] == op['stage'] and r['status'] == op['status']
            and r.get('adopted_board', False) == op.get('adopted_board', False)
            and (not op.get('adopted_board') or all(r.get(key) == op.get(key) for key in (
                'adoption_sequence', 'adoption_before_board', 'adoption_source_session_id',
                'adoption_source_operation_id', 'adoption_resolution_sha256')))
            and all(r[key] == op.get(key) for key in
                    ('counter_before', 'counter_after', 'start_verified', 'harvest_verified',
                     'mutation_attempted', 'start_cost', 'coins_spent', 'award', 'auto_harvest')))


def _snapshot_error(s):
    """Validate transport shape without borrowing availability from old receipts.

    An unavailable current view may coexist with an independently valid finished
    operation. Such a publication is evidence, never admission for another action.
    Return a stable predicate name so a failed live read retains its exact cause.
    """
    if (not isinstance(s, dict) or not _integer(s.get('schema'), 1, 1)
            or not _integer(s.get('revision'), 1, 999999999)
            or not _integer(s.get('category_id'), _CATEGORY, _CATEGORY)
            or not _integer(s.get('task_id'), _TASK, _TASK)
            or not _integer(s.get('cycle')) or not _quotes_valid(s)
            or not _integer(s.get('selected_mode'), 1, 2) or type(s.get('free_only')) is not bool
            or not _integer(s.get('round'), 0, 100)
            or 'start_cost' in s and s['start_cost'] is not None and not _integer(s['start_cost'], 0, 0x7FFFFFFFFFFFFFFF)
            or not _integer(s.get('free_remaining'), 0, 255)
            or not (s.get('character_id') == '0' or _role_id(s.get('character_id')))
            or s.get('action') not in ('', *_ACTIONS) or not isinstance(s.get('reason'), str)
            or s.get('ui_cleanup') not in ('none', 'waiting', 'closing', 'clear', 'unknown')):
        return 'soul_projection_fields'
    if any(key in s for key in ('board', 'growth_count', 'growth_cost', 'coins')) and not _growth_metadata(s):
        return 'soul_growth_metadata'
    if ('current' not in s or 'required' not in s or
            s['required'] is not None and not _integer(s['required'], 1, 255) or
            s['current'] is not None and (s['required'] is None or not _integer(s['current'], 0, s['required']))):
        return 'soul_current_progress_shape'
    observation = s.get('observation')
    if 'observation' in s and (not isinstance(observation, dict)
            or type(observation.get('available')) is not bool or not isinstance(observation.get('reason'), str)):
        return 'soul_observation_shape'
    live = _live_available(s)
    if observation is not None and observation['available'] and not live:
        return 'soul_available_without_current_identity_or_progress'
    if observation is not None and observation['available'] and observation['reason']:
        return 'soul_available_observation_reason'
    if not live and (s['action'] or not s['reason']
                     or observation is not None and not observation['reason']):
        return 'soul_unavailable_action_or_reason'
    if s.get('historical_receipt') is not None and not _historical_receipt_valid(s['historical_receipt']):
        return 'soul_historical_receipt'
    op = s.get('operation')
    if (not isinstance(op, dict) or not isinstance(op.get('status'), str) or op['status'] not in _STATUSES
            or not _integer(op.get('mode'), 1, 2) or op['mode'] != s['selected_mode']
            or type(op.get('free_only')) is not bool or op['free_only'] != s['free_only']
            or not _integer(op.get('round'), s['round'], s['round'])
            or op.get('award') is not None and (not isinstance(op['award'], list) or len(op['award']) != 4
                                               or any(not _integer(x, 0, 2040) for x in op['award']))
            or not isinstance(op.get('reason'), str) or not isinstance(op.get('operation_id'), str)
            or op.get('stage') not in ('', *_ACTIONS) or not _integer(op.get('sequence'), 0, 999999999)
            or 'start_cost' in op and not _integer(op['start_cost'], 0, 0x7FFFFFFFFFFFFFFF)
            or not _integer(op.get('opcode'), _OPCODES[op['stage']], _OPCODES[op['stage']])
            or any(type(op.get(key)) is not bool for key in _WITNESSES)
            or any(key not in op or op[key] is not None and not _integer(op[key], 0, 255)
                   for key in ('counter_before', 'counter_after'))):
        return 'soul_operation_fields'
    if not _activity_contract(op):
        return 'soul_activity_contract'
    adopted = op.get('adopted_board') is True
    if adopted and not _adoption_valid(op):
        return 'soul_adoption_witness'
    if op['status'] == 'IDLE':
        idle = (op['operation_id'] == op['stage'] == '' and op['sequence'] == 0
                and not any(op[key] for key in _WITNESSES)
                and op['counter_before'] is op['counter_after'] is None and s['ui_cleanup'] == 'none')
        return '' if idle else 'soul_idle_evidence'
    if 'close_verified' in op and (type(op['close_verified']) is not bool
            or op['close_verified'] != (op['status'] == 'COMPLETED' and op['stage'] == 'close' and s['ui_cleanup'] == 'clear')):
        return 'soul_close_witness'
    if any(key in op for key in ('growth_verified', 'coins_spent', 'auto_harvest')) and (
            type(op.get('growth_verified')) is not bool or type(op.get('auto_harvest')) is not bool
            or not _integer(op.get('coins_spent'), 0, 0x7FFFFFFFFFFFFFFF)):
        return 'soul_growth_operation_fields'
    if (s['action'] == 'grow' or op['stage'] == 'grow') and (not _growth_metadata(s)
            or type(op.get('growth_verified')) is not bool or type(op.get('auto_harvest')) is not bool
            or not _integer(op.get('coins_spent'), 0, 0x7FFFFFFFFFFFFFFF)):
        return 'soul_grow_evidence_fields'
    if not (bool(re.fullmatch(r'[0-9a-f]{32}', op['operation_id'])) and bool(op['stage'])
            and op['sequence'] > 0 and (op['counter_before'] is not None or not _activity_required(op))
            and (op['counter_after'] is None or op['mutation_attempted'])
            and (not op['start_verified'] or op['mutation_attempted'])
            and (not op['harvest_verified'] or (op['start_verified'] or adopted)
                 and (op['counter_after'] is not None or not _activity_required(op)))):
        return 'soul_operation_witnesses'
    if op['status'] == 'COMPLETED' and not (op['stage'] == 'close' and s['ui_cleanup'] == 'clear'
            and op['invoked'] and op['mutation_attempted'] and (op['start_verified'] or adopted) and op['harvest_verified']
            and isinstance(op.get('award'), list)
            and (not _activity_required(op) or op['counter_after'] is not None
                 and op['counter_before'] <= op['counter_after'] <= op['counter_before'] + 1)):
        return 'soul_completed_close_evidence'
    if op['status'] == 'COMPLETED' and 'historical_receipt' in s and not (
            _receipt_matches_operation(s) and s['historical_receipt']['phase'] == 'CLOSE_VERIFIED'):
        return 'soul_completed_historical_binding'
    return ''


def _newer(previous, s):
    return previous is None or s['revision'] > previous['revision'] or s == previous


def _keeps_evidence(previous, s):
    before, after = previous['operation'], s['operation']
    return (before['mode'] == after['mode'] and before['round'] == after['round']
            and before['free_only'] == after['free_only']
            and all(not before[key] or after[key] for key in _WITNESSES)
            and (not _activity_required(before) or before['counter_after'] is None
                 or after['counter_after'] == before['counter_after'])
            and s['ui_cleanup'] in _CLEANUP_TRANSITIONS[previous['ui_cleanup']]
            and (not _growth_metadata(previous) or _growth_metadata(s)
                 and s['growth_count'] >= previous['growth_count'])
            and (not _activity_required(before) or s['current'] is not None
                 and previous['current'] is not None and s['current'] >= previous['current']))


def _follows(previous, s):
    before, after = previous['operation']['status'], s['operation']['status']
    return (_newer(previous, s) and after in _TRANSITIONS.get(before, {before}) and _keeps_evidence(previous, s))


def _clean_prior(s):
    op, cleanup = s['operation'], s['ui_cleanup']
    if op['status'] == 'IDLE':
        return True
    if op['status'] == 'COMPLETED':
        required = (s['historical_receipt']['required'] if _receipt_matches_operation(s) else s['required'])
        return (cleanup == 'clear' and op['stage'] == 'close' and op['invoked'] and op['harvest_verified']
                and (not _activity_required(op) or required is not None
                     and op['counter_after'] == min(op['counter_before'] + 1, required)))
    return (op['status'] in ('REJECTED', 'STOPPED') and not op['mutation_attempted']
            and (cleanup == 'clear' or cleanup == 'none' and not op['invoked']))


def _invoked_gui(gui, sequence):
    return (isinstance(gui, dict) and _integer(gui.get('command_seq'), sequence, sequence)
            and gui.get('phase') == 'completed' and gui.get('invocation_attempted') is True)


class SoulExecutor:
    """Choose coin growth from fresh stock board and resource observations."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=22.0, clock=time.monotonic, publish=lambda result: None,
                 evidence_dir=None, resource_policy_provider=None):
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError('daily_soul_timeout_invalid')
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait if wait is not None else lambda: time.sleep(.1)
        self.timeout, self.clock = timeout, clock
        self._publish_sink = publish
        self.publish = self._publish_evidence
        self._evidence_dir = None if evidence_dir is None else Path(evidence_dir)
        self._evidence_error = ''
        self._round_state = None
        self._lock = threading.Lock()
        self._used = False
        self._last_result = None
        self.resource_policy_provider = (resource_policy_provider if resource_policy_provider is not None
            else lambda: (getattr(self.objects, 'resource_policy_provider', None) or (lambda: None))())
        self._resource_refusal = ''
        self._not_submitted_actions = []
        self._failure_evidence_path = ''
        self._closed_native_receipt = None

    def _live_budget(self, free_only):
        try:
            return soul_money_budget(self.resource_policy_provider, self._coin_budget,
                                     free_only=free_only)
        except ResourcePolicyDenied as exc:
            self._resource_refusal = str(exc)
            return 0

    def _publish_evidence(self, result):
        """Latch disk failure without preventing safe settlement or close.

        The same operation file is replaced throughout the run. Current-round
        evidence lives in the top-level fields until a verified round receipt
        is appended; a pending round is never invented for serialization.
        """
        if self._evidence_error:
            result = replace(result, evidence_error=self._evidence_error)
        if self._failure_evidence_path:
            result = replace(result, failure_evidence_path=self._failure_evidence_path)
        if getattr(self, '_policy_source', ''):
            result = replace(result, policy_source=self._policy_source)
        if self._evidence_dir is not None and result.operation_id:
            target = self._evidence_dir / (result.operation_id + '.json')
            result = replace(result, evidence_path=str(target))
            try:
                target.parent.mkdir(parents=True, exist_ok=True)
                temporary = target.with_suffix('.tmp')
                payload = {'ledger_schema': 2, **asdict(result)}
                temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding='utf-8')
                temporary.replace(target)
            except (OSError, TypeError, ValueError) as exc:
                if not self._evidence_error:
                    self._evidence_error = f'soul_evidence_write_failed ({exc})'
                result = replace(result, evidence_error=self._evidence_error)
        try:
            self._publish_sink(result)
        except SoulEvidenceWriteError as exc:
            if not self._evidence_error:
                self._evidence_error = f'soul_evidence_write_failed ({exc})'
            result = replace(result, evidence_error=self._evidence_error)
        return result

    def execute(self, task_id: int | None = None, *, character_id: str, expected_cycle: int,
                expected_current: int | None = None, expected_required: int | None = None,
                blue_count: int = 1, purple_count: int = 1,
                free_only: bool = False, coin_budget: int | None = None,
                policy: SoulPolicy | None = None) -> DailySoulResult:
        if task_id is not None and (type(task_id) is not int or task_id != _TASK):
            raise ValueError('daily_soul_task_unsupported')
        if (coin_budget is not None and not _integer(coin_budget, 0, 0x7FFFFFFFFFFFFFFF)
                or policy is not None and not isinstance(policy, SoulPolicy)):
            return DailySoulResult('BLOCKED', 'soul_budget_or_policy_invalid')
        if (type(free_only) is not bool or type(blue_count) is not int or type(purple_count) is not int
                or not 0 <= blue_count <= 100 or not 0 <= purple_count <= 100
                or not 1 <= blue_count + purple_count <= 100):
            return DailySoulResult('BLOCKED', 'soul_counts_invalid')
        if (not _role_id(character_id) or not _integer(expected_cycle, 1)
                or not _activity_shape(expected_current, expected_required)):
            return DailySoulResult('BLOCKED', 'soul_expected_identity_or_progress_invalid')
        if not self._lock.acquire(blocking=False):
            return DailySoulResult('UNKNOWN', 'soul_previous_attempt', cleanup='UNVERIFIED')
        try:
            if self._used:
                if self._last_result is not None and self._last_result.status == 'UNKNOWN':
                    return self._last_result
                return DailySoulResult('BLOCKED', 'soul_executor_used')
            self._used = True
            self._coin_budget = coin_budget
            self._explicit_policy = policy
            self._mode_policies = {}
            self._mode_models = {}
            self._policy_source = 'explicit' if policy is not None else ''
            self._policy = SoulPolicy() if policy is None else policy
            self._joint_model = None
            self._last_result = self._execute_plan(character_id, expected_cycle, expected_current, expected_required,
                                                   blue_count, purple_count, free_only)
            self._last_result = self.publish(self._last_result)
            if self._evidence_error:
                failed = self._last_result
                status = (failed.status if failed.status in ('UNKNOWN', 'STOPPED') else
                          'PARTIAL' if failed.rounds and failed.cleanup == 'VERIFIED_CLOSED' else
                          'UNKNOWN' if failed.cleanup == 'UNVERIFIED' else 'BLOCKED')
                self._last_result = self.publish(replace(failed, status=status,
                    reason=self._evidence_error + ('; ' + failed.reason if failed.reason != self._evidence_error else ''),
                    evidence_error=self._evidence_error))
            return self._last_result
        finally:
            self._lock.release()

    def harvest_existing(self, *, character_id: str, expected_cycle: int,
                         expected_current: int | None, expected_required: int | None, mode: int) -> DailySoulResult:
        """Adopt a live board, harvest once and close; never start or grow."""
        if (not _role_id(character_id) or not _integer(expected_cycle, 1)
                or not _activity_shape(expected_current, expected_required) or not _integer(mode, 1, 2)):
            return DailySoulResult('BLOCKED', 'soul_expected_identity_or_progress_invalid')
        if not self._lock.acquire(blocking=False):
            return DailySoulResult('UNKNOWN', 'soul_previous_attempt', cleanup='UNVERIFIED')
        try:
            if self._used:
                return self._last_result or DailySoulResult('BLOCKED', 'soul_executor_used')
            self._used = True
            self._coin_budget = 0
            self._policy_source = 'explicit_harvest_current_board'
            self._policy = SoulPolicy()
            self._joint_model = None
            operation_id = uuid.uuid4().hex
            step = self._execute_round(character_id, expected_cycle, expected_current, expected_required,
                mode, False, operation_id, True, 0, self.publish, adopt_existing=True, harvest_only=True,
                future_starts_by_mode={'1': 0, '2': 0})
            if step.status == 'COMPLETED' and len(step.rounds) == 1:
                step = self.publish(replace(step, status='RUNNING', reason='soul_existing_board_harvest_verified'))
                closed, reason = self._close_plan(self._round_state, operation_id)
                if closed and self._closed_native_receipt is not None:
                    step = replace(step, rounds=(replace(step.rounds[0],
                        native_close_receipt=deepcopy(self._closed_native_receipt)),))
                step = replace(step, status='COMPLETED' if closed else 'UNKNOWN',
                    reason='soul_existing_board_harvested' if closed else reason,
                    cleanup='VERIFIED_CLOSED' if closed else 'UNVERIFIED')
            if self._evidence_error:
                step = replace(step, status='PARTIAL' if step.cleanup == 'VERIFIED_CLOSED' else 'UNKNOWN',
                               reason=self._evidence_error, evidence_error=self._evidence_error)
            self._last_result = self.publish(step)
            return self._last_result
        finally:
            self._lock.release()

    def _activate_mode_policy(self, character_id, mode):
        if mode not in self._mode_policies:
            data_dir = getattr(self.objects, 'data_dir', None)
            if data_dir is None:
                selected, source = self._explicit_policy or SoulPolicy(), 'explicit' if self._explicit_policy else 'built_in'
            else:
                selected, source = resolve_soul_policy(data_dir, character_id, mode, self._explicit_policy)
            model = None
            if selected.version == 'joint-value-v3':
                from .soul_joint_strategy import JointSoulModel
                model = JointSoulModel.load(selected.model_path, character_id=character_id, mode=mode)
                if not model.ready:
                    raise ValueError('soul_joint_model_unavailable')
            self._mode_policies[mode] = selected, source
            self._mode_models[mode] = model
        self._policy, self._policy_source = self._mode_policies[mode]
        self._joint_model = self._mode_models[mode]

    def _execute_plan(self, character_id, cycle, baseline, required, blue_count, purple_count, free_only):
        operation_id = uuid.uuid4().hex
        remaining = {'blue': blue_count, 'purple': purple_count}
        rounds, skipped = [], []
        current, quote = baseline, None
        current_color = ''

        def aggregate(status, reason, cleanup='NOT_NEEDED', partial=None):
            complete = bool(rounds)
            retained = list(rounds)
            if (retained and self._closed_native_receipt is not None
                    and self._closed_native_receipt.get('operation_id') == operation_id
                    and self._closed_native_receipt.get('round') == retained[-1].round):
                retained[-1] = replace(retained[-1], native_close_receipt=deepcopy(self._closed_native_receipt))
            return DailySoulResult(status, reason, baseline,
                current if partial is None else partial.progress_after, required,
                bool(complete or partial and partial.operation_id) and all(r.start_verified for r in rounds)
                    and (partial is None or not partial.operation_id or partial.start_verified),
                bool(complete or partial and partial.operation_id) and all(r.harvest_verified for r in rounds)
                    and (partial is None or not partial.operation_id or partial.harvest_verified),
                bool(complete or partial and partial.account_progress_observed),
                cleanup, operation_id,
                rounds[0].free_before if complete else None,
                rounds[-1].free_after if complete else None,
                sum(r.mode == 1 for r in rounds), sum(r.mode == 2 for r in rounds),
                sum(r.coins_spent for r in rounds) + (partial.coins_spent if partial else 0),
                tuple(retained), tuple(skipped), quote, current_color,
                tuple(g for r in rounds for g in r.growths) + (partial.growths if partial else ()),
                tuple(d for r in rounds for d in r.decisions) + (partial.decisions if partial else ()),
                partial.pending_action if partial else None, self._policy, self._coin_budget,
                not_submitted_actions=tuple(self._not_submitted_actions))

        while any(remaining.values()):
            if self.stopped():
                return aggregate('STOPPED', 'user_stop', 'UNVERIFIED' if rounds else 'NOT_NEEDED')
            if self._evidence_error:
                if rounds:
                    closed, reason = self._close_plan(self._round_state, operation_id)
                    return aggregate('PARTIAL' if closed else 'UNKNOWN',
                        self._evidence_error if closed else self._evidence_error + '; ' + reason,
                        'VERIFIED_CLOSED' if closed else 'UNVERIFIED')
                return aggregate('BLOCKED', self._evidence_error)
            while self.paused() and not self.stopped():
                self.wait()
            if self.stopped():
                continue
            try:
                observed = self.objects.observe_daily_soul(self.token)
                s = observed.get('soul') if observed.get('enabled') is True else None
            except Exception:
                return aggregate('UNKNOWN' if rounds else 'BLOCKED', 'soul_observation_failed',
                                 'UNVERIFIED' if rounds else 'NOT_NEEDED')
            if not _valid(s):
                return aggregate('UNKNOWN' if rounds else 'UNAVAILABLE', 'soul_color_quote_unavailable',
                                 'UNVERIFIED' if rounds else 'NOT_NEEDED')
            quote = soul_quote(s)
            if not rounds and not _clean_prior(s):
                return aggregate('UNKNOWN', 'soul_prior_operation_unresolved', 'UNVERIFIED')
            if s['character_id'] != character_id or s['cycle'] != cycle:
                return aggregate('UNKNOWN' if rounds else 'BLOCKED', 'soul_expected_identity_or_progress_changed',
                                 'UNVERIFIED' if rounds else 'NOT_NEEDED')
            color = next(name for name in ('blue', 'purple') if remaining[name])
            adopting = not rounds and s['action'] == 'adopt' and not free_only
            if adopting:
                color = 'blue' if s['selected_mode'] == 1 else 'purple'
            mode = 1 if color == 'blue' else 2
            try:
                self._activate_mode_policy(character_id, mode)
            except (ValueError, OSError, BridgeError) as exc:
                if rounds:
                    closed, _ = self._close_plan(self._round_state, operation_id)
                    return aggregate('PARTIAL' if closed else 'UNKNOWN', str(exc),
                                     'VERIFIED_CLOSED' if closed else 'UNVERIFIED')
                return aggregate('BLOCKED', str(exc))
            q = s['quotes'][color]
            if not q['known']:
                return aggregate('UNKNOWN' if rounds else 'UNAVAILABLE', 'soul_color_quote_unavailable',
                                 'UNVERIFIED' if rounds else 'NOT_NEEDED')
            skip = ('soul_color_locked' if free_only and not q['free_unlocked'] else
                    'soul_free_exhausted' if free_only and q['free_remaining'] == 0 else '')
            if skip:
                skipped.append(SoulSkip(color, skip))
                remaining[color] = 0
                continue
            spent = sum(r.coins_spent for r in rounds)
            budget = self._live_budget(free_only)
            start_quote = 0 if adopting else q['start_cost']
            if budget is not None and start_quote > budget - spent:
                skipped.append(SoulSkip(color, 'soul_plan_budget_limit'))
                remaining[color] = 0
                continue
            try:
                check_costs(read_policy(self.resource_policy_provider), {'money': start_quote},
                            reserved={'money': spent}, purchase=start_quote > 0)
            except ResourcePolicyDenied as exc:
                skipped.append(SoulSkip(color, str(exc)))
                remaining[color] = 0
                continue
            current_color = color
            self.publish(aggregate('RUNNING', 'soul_color_running', 'PANEL_RETAINED' if rounds else 'NOT_NEEDED'))
            if self._evidence_error:
                continue
            # The current planned slot is consumed by this round, including an
            # adopted board. An adopted entry absent from the plan consumes no
            # slot in the other entry. Neither case grants an extra future start.
            future_starts = {str(entry): max(0, remaining[name] - int(name == color))
                             for entry, name in ((1, 'blue'), (2, 'purple'))}
            step = self._execute_round(character_id, cycle, current, required, mode, free_only,
                                       operation_id, not rounds, spent,
                                       lambda partial: self.publish(aggregate('RUNNING', partial.reason,
                                                                              partial.cleanup, partial)),
                                       exploration_history=tuple(g for r in rounds for g in r.growths),
                                       adopt_existing=adopting, future_starts_by_mode=future_starts)
            if step.status != 'COMPLETED' or len(step.rounds) != 1:
                if rounds and not step.operation_id and step.status in ('UNAVAILABLE', 'BLOCKED'):
                    closed, reason = self._close_plan(self._round_state, operation_id)
                    return aggregate('PARTIAL' if closed else 'UNKNOWN', step.reason if closed else reason,
                                     'VERIFIED_CLOSED' if closed else 'UNVERIFIED', step)
                return aggregate(step.status, step.reason, 'UNVERIFIED' if rounds else step.cleanup, step)
            receipt = step.rounds[0]
            rounds.append(receipt)
            current = receipt.progress_after
            quote = step.quote
            if remaining[color]:
                remaining[color] -= 1
            self.publish(aggregate('RUNNING', 'soul_round_verified', 'PANEL_RETAINED'))

        if not rounds:
            return aggregate('ALREADY_COMPLETE' if free_only else 'UNAVAILABLE',
                             'soul_plan_budget_limit' if any(skip.reason == 'soul_plan_budget_limit' for skip in skipped)
                             else 'soul_no_free_starts' if free_only else 'soul_no_eligible_starts')
        closed, reason = self._close_plan(self._round_state, operation_id)
        if not closed:
            return aggregate('UNKNOWN', reason, 'UNVERIFIED')
        return aggregate('COMPLETED' if free_only or not skipped else 'PARTIAL',
                         'soul_plan_completed' if free_only or not skipped else 'soul_plan_partial', 'VERIFIED_CLOSED')

    def _close_plan(self, previous, operation_id):
        """Close exactly once after all per-round receipts have been retained."""
        deadline = self.clock() + self.timeout
        while True:
            while self.paused() and not self.stopped():
                self.wait()
                deadline = self.clock() + self.timeout
            if self.stopped():
                return False, 'user_stop'
            try:
                wire = self.objects.submit_daily_soul(self.token, 'close', expected_revision=previous['revision'],
                    character_id=previous['character_id'], task_id=_TASK, operation_id=operation_id,
                    soul_mode=previous['selected_mode'], free_only=int(previous['free_only']),
                    admission=lambda: self.clock() < deadline)
            except CampaignAdmissionClosed:
                if self.paused() and not self.stopped():
                    continue  # The local admission gate proves no command was submitted.
                return False, 'soul_close_admission_closed'
            except Exception:
                return False, 'soul_close_outcome_unknown'
            break
        ack, sequence = wire.get('ack'), wire.get('requested_seq')
        if (not isinstance(ack, dict) or ack.get('status') != 'COMPLETED'
                or ack.get('command_name') != 'daily-soul-command'
                or not _integer(sequence, previous['operation']['sequence'] + 1, 999999999)
                or ack.get('command_seq') != sequence):
            return False, 'soul_close_binding_invalid'
        details = ack.get('result') or {}
        receipt = details.get('daily_soul')

        def bound(s):
            return (_valid(s) and s['character_id'] == previous['character_id']
                    and s['cycle'] == previous['cycle']
                    and (not _activity_required(previous['operation']) or
                         s['current'] == previous['current'] and s['required'] == previous['required'])
                    and s['operation']['operation_id'] == operation_id
                    and s['operation']['sequence'] == sequence and s['operation']['stage'] == 'close'
                    and s['operation']['invoked'] and _keeps_evidence(previous, s)
                    and s['operation']['coins_spent'] == previous['operation']['coins_spent']
                    and s['coins'] == previous['coins'])

        def closed(s):
            receipt = s.get('historical_receipt') or {}
            complete = (bound(s) and s['operation']['status'] == 'COMPLETED' and s['ui_cleanup'] == 'clear'
                        and receipt.get('phase') == 'CLOSE_VERIFIED' and receipt.get('close_verified') is True
                        and receipt.get('close_sequence') == sequence and not any(slot['color'] for slot in s['board']))
            if complete:
                self._closed_native_receipt = deepcopy(receipt)
            return complete

        if (details.get('daily_soul_enabled') is not True or not _invoked_gui(details.get('campaign_gui'), sequence)
                or not bound(receipt) or receipt['revision'] <= previous['revision']
                or receipt['operation']['status'] not in _ACK_STATUSES['close']):
            return False, 'soul_close_binding_invalid'
        if closed(receipt):
            return True, 'soul_plan_completed'
        last = receipt
        deadline = self.clock() + self.timeout
        while self.clock() < deadline:
            if self.stopped():
                return False, 'user_stop'
            try:
                observed = self.objects.observe_daily_soul(self.token)
                s = observed.get('soul') if observed.get('enabled') is True else None
            except Exception:
                return False, 'soul_close_observation_failed'
            if not bound(s) or not _follows(last, s):
                self._retain_close_failure(operation_id, previous, ack, last, s)
                return False, 'soul_close_evidence_changed'
            if closed(s):
                return True, 'soul_plan_completed'
            if s['operation']['status'] in _TERMINAL or s['ui_cleanup'] == 'unknown':
                return False, s['operation']['reason'] or 'soul_close_unverified'
            last = s
            self.wait()
        return False, 'soul_close_timeout'

    def _retain_close_failure(self, operation_id, previous, ack, last, observed):
        root = getattr(self.objects, 'data_dir', None)
        if root is None:
            return
        target = Path(root) / 'soul-observation-failures' / (operation_id + '.close.json')
        valid = _valid(observed)
        document = dict(stage='close', operation_id=operation_id,
            observed_at=time.time(), snapshot_error=_snapshot_error(observed),
            projection_valid=valid, follows=_follows(last, observed) if valid else False,
            previous=previous, ack=ack, last=last, observed=observed)
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            staged = target.with_suffix('.writing')
            staged.write_text(json.dumps(document, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
            staged.replace(target)
            self._failure_evidence_path = str(target.resolve())
        except (OSError, TypeError, ValueError):
            self._evidence_error = 'soul_close_failure_evidence_write_failed'

    def _execute_round(self, character_id, cycle, baseline, required, mode, free_only, operation_id, first,
                       plan_spent, publish, *, exploration_history=(), adopt_existing=False, harvest_only=False,
                       future_starts_by_mode=None):
        self._resource_refusal = ''
        color = 'blue' if mode == 1 else 'purple'
        initial_action = 'adopt' if adopt_existing else 'open' if first else 'next'
        round_number = 1 if first else self._round_state['round'] + 1
        start_sequence = 0
        initial_board = ()
        verified_free_after = None
        stage, sequence, initial_free = '', 0, None
        free_remaining = None
        start_cost = 0
        previous = witness = None
        submitted = False
        growth_quote = None
        cleanup_waiting = False
        growths, decisions = [], []
        pending_action = None
        decision = None
        refused_candidates = set()
        deadline = self.clock() + self.timeout

        def result(status, reason, *, clean=False, hold=False):
            op = witness or {}
            progress = op.get('counter_after')
            return DailySoulResult(
                status, reason, baseline, baseline if progress is None else progress, required,
                op.get('start_verified', False), op.get('harvest_verified', False), progress is not None,
                'VERIFIED_CLOSED' if clean else 'UNVERIFIED' if submitted or hold else 'NOT_NEEDED',
                operation_id if submitted else '', initial_free, free_remaining,
                coins_spent=op.get('coins_spent', 0), current_color=color,
                growths=tuple(growths), decisions=tuple(decisions), pending_action=pending_action,
                policy=self._policy, coin_budget=self._coin_budget,
                not_submitted_actions=tuple(self._not_submitted_actions))

        def publish_evidence(reason):
            # The coordinator persists this snapshot before any paid submission.
            publish(result('RUNNING', reason))

        def remaining_budget(spent):
            if self._resource_refusal:
                return 0
            budget = self._live_budget(free_only)
            return None if budget is None else max(0, budget - plan_spent - spent)

        def economic_context(s, spent):
            """Fresh comparison inputs only; every submission keeps admission checks."""
            try:
                resource_policy = read_policy(self.resource_policy_provider)
                # One provider read supplies both the comparison's money cap
                # and paid-opening permission. Real submission reads it again.
                budget = soul_money_budget(lambda: resource_policy, self._coin_budget, free_only=free_only)
                paid_allowed = (resource_policy is None or resource_policy['allow_purchase']
                    and 'money' in resource_policy['allowed_resources']
                    and resource_policy['limits'].get('money') != 0)
                permission_source = ('legacy_no_resource_policy' if resource_policy is None
                                     else 'live_resource_policy')
            except ResourcePolicyDenied as exc:
                self._resource_refusal = str(exc)
                budget = 0
                paid_allowed, permission_source = None, str(exc)
            allowance = (0 if self._resource_refusal else None if budget is None
                         else max(0, budget - plan_spent - spent))
            return {
                'schema': ECONOMIC_CONTEXT_SCHEMA,
                'source': 'fresh_bound_executor_frame_and_live_resource_policy',
                'character_id': character_id, 'revision': s['revision'],
                'entry_mode': mode, 'current_round_excluded': True,
                'future_starts_by_mode': None if future_starts_by_mode is None else dict(future_starts_by_mode),
                'free_only': free_only, 'paid_starts_allowed': paid_allowed,
                'paid_permission_source': permission_source,
                'plan_budget_remaining': allowance, 'coin_balance': s.get('coins'),
                'quotes_by_mode': {str(entry): {
                    'known': s['quotes'][name].get('known'),
                    'free_unlocked': s['quotes'][name].get('free_unlocked'),
                    'free_remaining': s['quotes'][name].get('free_remaining'),
                    'next_start_cost': s['quotes'][name].get('start_cost'),
                    'paid_start_cost': s['quotes'][name].get('paid_start_cost'),
                } for entry, name in ((1, 'blue'), (2, 'purple'))},
                'constraints': {
                    'current_round_max_growths': self._policy.max_growths,
                    'plan_scope': 'this_executor_call_only',
                    'free_only_scope': 'start_only_growth_remains_money_spend',
                    'future_authorization': 'snapshot_not_commitment_recheck_before_each_action',
                    'external_stop_constraints': 'not_predicted_by_open_then_harvest_reference',
                },
            }

        def capture_ready(s):
            nonlocal pending_action
            if pending_action is None or pending_action.sequence != s['operation']['sequence']:
                return
            op = s['operation']
            if op['status'] != 'READY':
                return
            if pending_action.action == 'grow':
                if not op.get('growth_verified') or growth_quote is None:
                    raise ValueError('soul_growth_evidence_unverified')
                count, coins, cost, spent = growth_quote
                before = pending_action.board_before
                steps = ()
                if op.get('growth_evidence_sequence') is not None:
                    if (op['growth_evidence_sequence'] != sequence
                            or op.get('growth_after_source') != 'decoded_response'):
                        raise ValueError('soul_growth_evidence_sequence_invalid')
                    if _board_tuple(op.get('growth_before_board')) != before:
                        raise ValueError('soul_growth_before_board_mismatch')
                    after = _board_tuple(op.get('growth_after_board'))
                    raw_steps = op.get('growth_steps')
                    if not isinstance(raw_steps, list) or not 1 <= len(raw_steps) <= 64:
                        raise ValueError('soul_growth_steps_invalid')
                    steps = tuple(_board_tuple(board) for board in raw_steps)
                    if steps[-1] != after or after != _board_tuple(s['board']):
                        raise ValueError('soul_growth_after_board_mismatch')
                    source = 'decoded_response'
                elif op.get('auto_harvest'):
                    # An empty post-settlement frame cannot reconstruct a response.
                    after, source = None, 'unavailable_auto_harvest'
                else:
                    after, source = _board_tuple(s['board']), 'fresh_bound_frame'
                try:
                    chain = analyze_chain(before, steps,
                        payoff=getattr(self._joint_model, 'payoff_mapping', None)) if steps else None
                except Exception as exc:
                    # Optional rule diagnostics cannot invalidate a separately
                    # verified native charge, response or final board.
                    chain = {'status': 'UNVERIFIED',
                             'violations': [f'chain_analysis_error:{type(exc).__name__}:{exc}']}
                growths.append(SoulGrowthReceipt(round_number, sequence, pending_action.revision,
                    s['revision'], count, s['growth_count'], cost, coins, s['coins'], before, after,
                    source, steps, op['auto_harvest'],
                    tuple(op['award']) if isinstance(op.get('award'), list) else None, pending_action.decision,
                    chain))
                if op['auto_harvest'] and after is not None:
                    economics = economic_context(s, op['coins_spent'])
                    automatic = decide_soul_action(after, s['growth_count'], s['coins'], s['growth_cost'],
                        policy=self._policy, budget_remaining=economics['plan_budget_remaining'],
                        force_harvest_reason='soul_auto_harvest', joint_model=self._joint_model, start_cost=start_cost,
                        exploration_history=exploration_history, round_growths=tuple(growths),
                        economic_context=economics)
                    decisions.append(SoulDecisionEvidence(round_number, s['revision'], sequence, source, automatic))
            elif pending_action.action == 'start' and not op.get('start_verified'):
                return
            elif pending_action.action == 'harvest' and not op.get('harvest_verified'):
                return
            pending_action = None
            publish_evidence('soul_action_verified')

        def fail(reason):
            return result('UNKNOWN' if submitted else 'BLOCKED', reason)

        def identity(s):
            return s['character_id'] == character_id and s['cycle'] == cycle

        def bound(s, action, seq):
            nonlocal verified_free_after
            op = s['operation']
            if (not identity(s) or op['operation_id'] != operation_id or op['sequence'] != seq
                    or op['stage'] != action or not op['invoked'] or op['mode'] != mode
                    or op['free_only'] != free_only or op['round'] != round_number
                    or op.get('start_cost', 0) != start_cost
                    ):
                return False
            if growth_quote is not None:
                count, coins, cost, spent = growth_quote
                if not _growth_metadata(s):
                    return False
                if action == 'grow':
                    if not _growth_projection(s) or s['growth_cost'] != cost or s['growth_count'] not in (count, count + 1):
                        return False
                    if s['growth_count'] == count + 1:
                        if (s['coins'] != coins - cost or op.get('growth_verified') is not True
                                or op.get('coins_spent') != spent + cost):
                            return False
                    elif (op['status'] == 'READY' or s['coins'] not in (coins, coins - cost)
                          or op.get('growth_verified') is not False or op.get('coins_spent') != spent):
                        return False
                else:
                    if s['growth_count'] != count:
                        return False
                    if action == 'start':
                        if op['start_verified']:
                            if s['coins'] != coins - start_cost or op.get('coins_spent', spent) != spent + start_cost:
                                return False
                        elif s['coins'] not in (coins, coins - start_cost) or op.get('coins_spent', spent) != spent:
                            return False
                    elif (s['coins'] is not None and s['coins'] != coins or op.get('coins_spent', spent) != spent):
                        return False
            if action == 'adopt':
                return (adopt_existing and _adoption_valid(op) and not op['mutation_attempted']
                        and s['free_remaining'] == initial_free
                        and _board_tuple(s['board']) == initial_board
                        and _board_tuple(op['adoption_before_board']) == initial_board)
            if action in ('open', 'next'):
                return (not op['mutation_attempted']
                        and s['free_remaining'] == initial_free)
            if not op['mutation_attempted']:
                return False
            if action == 'start' and not op['start_verified']:
                return s['free_remaining'] in (initial_free, max(0, initial_free - 1))
            if not op['start_verified'] and not (adopt_existing and _adoption_valid(op)):
                return False
            allowed = ((initial_free,) if adopt_existing else
                       (max(0, initial_free - 1),) if start_cost == 0 else (initial_free, max(0, initial_free - 1)))
            if s['free_remaining'] not in allowed:
                return False
            if verified_free_after is None:
                verified_free_after = s['free_remaining']
            return (s['free_remaining'] == verified_free_after and (action != 'close' or op['harvest_verified']))

        while True:
            if self.stopped():
                return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
            if not submitted and self.paused():
                self.wait()
                if not refused_candidates:
                    deadline = self.clock() + self.timeout
                continue
            timeout_reason = 'soul_readback_timeout' if submitted else 'soul_admission_timeout'
            if self.clock() >= deadline:
                return fail(timeout_reason)
            try:
                observed = self.objects.observe_daily_soul(self.token)
            except Exception:
                if self.stopped():
                    return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
                return fail(timeout_reason if self.clock() >= deadline else 'soul_observation_failed')
            enabled = observed.get('enabled') if isinstance(observed, dict) else None
            s = observed.get('soul') if enabled is True else None
            valid = _valid(s)
            follows = bool(valid and _newer(previous, s))
            matched = bool(stage and follows and bound(s, stage, sequence) and _follows(previous, s))
            if matched:
                witness = s['operation']
                if (witness['start_verified'] or witness.get('adopted_board')) and not initial_board:
                    initial_board = tuple((slot['color'], slot['level']) for slot in s['board'])
                try:
                    capture_ready(s)
                except ValueError as exc:
                    return fail(str(exc))
            if self.stopped():
                return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
            if self.clock() >= deadline:
                return fail(timeout_reason)
            if enabled is False:
                return fail('soul_disabled_during_operation') if submitted else result('UNAVAILABLE', 'soul_disabled')
            if not valid or not follows:
                return fail('soul_observation_invalid_or_regressed')
            free_remaining = s['free_remaining']
            op, offer = s['operation'], s['action']
            status, reason = op['status'], op['reason'] or s['reason']
            if not stage:
                if first and not _clean_prior(s):
                    return result('UNKNOWN', 'soul_prior_operation_unresolved', hold=True)
                if not identity(s):
                    return fail(s['reason'] or 'soul_expected_identity_or_progress_changed')
                if not first and (s != self._round_state or offer != 'close' or op['operation_id'] != operation_id
                                  or not op['harvest_verified']):
                    return result('UNKNOWN', 'soul_rearm_baseline_changed', hold=True)
                q = s['quotes'][color]
                if not adopt_existing and (not q['known'] or q['start_available'] is not True):
                    return result('UNAVAILABLE', q['reason'] or 'soul_color_quote_unavailable')
                if (first and offer != initial_action) or s['reason']:
                    return result('UNAVAILABLE', s['reason'] or 'soul_gather_unavailable')
                initial_free = q['free_remaining']
                start_cost = 0 if adopt_existing else q['start_cost']
                if adopt_existing:
                    if s['selected_mode'] != mode or not any(slot['color'] for slot in s['board']):
                        return result('BLOCKED', 'soul_existing_board_changed')
                    initial_board = _board_tuple(s['board'])
                if free_only and (initial_free == 0 or start_cost != 0):
                    return result('UNAVAILABLE', 'soul_daily_free_start_unavailable')
                if (start_cost > 0 and (s.get('coins') is None or s['coins'] < start_cost)):
                    return result('UNAVAILABLE', 'soul_coin_start_quote_or_balance_unavailable')
                offer = initial_action
            else:
                if not matched:
                    return fail('soul_operation_binding_or_evidence_changed')
                if status in _TERMINAL:
                    if status in ('REJECTED', 'STOPPED') and not op['mutation_attempted']:
                        if s['ui_cleanup'] in ('waiting', 'closing'):
                            if not cleanup_waiting:
                                cleanup_waiting = True
                                deadline = self.clock() + self.timeout
                            previous = s
                            self.wait()
                            continue
                        if s['ui_cleanup'] == 'clear':
                            return result('STOPPED' if status == 'STOPPED' else 'UNAVAILABLE',
                                          reason or 'soul_not_started', clean=True)
                    if status == 'STOPPED' and s['ui_cleanup'] == 'clear':
                        return result('STOPPED', reason or 'soul_native_stopped', clean=True)
                    return result('UNKNOWN', reason or 'soul_operation_unresolved', clean=s['ui_cleanup'] == 'clear')
                if status == 'READY' and offer == 'close' and op['harvest_verified']:
                    if not isinstance(op.get('award'), list):
                        return fail('soul_round_settlement_unverified')
                    self._round_state = s
                    receipt = SoulRoundReceipt(round_number, color, mode, start_cost, op['coins_spent'],
                        s['growth_count'], initial_free, s['free_remaining'], op['counter_before'], op['counter_after'],
                        op['start_verified'], True, op['auto_harvest'], start_sequence, sequence, initial_board, tuple(op['award']),
                        tuple(growths), tuple(decisions))
                    receipt = replace(receipt, policy_source=getattr(self, '_policy_source', ''))
                    if adopt_existing:
                        if not _adoption_valid(op) or op['adoption_before_board'] != [
                                {'color': c, 'level': level} for c, level in initial_board]:
                            return fail('soul_adoption_board_mismatch')
                        receipt = replace(receipt, adopted_board=True,
                            adoption_sequence=op['adoption_sequence'],
                            adoption_provenance={key: deepcopy(op[key]) for key in (
                                'adoption_before_board', 'adoption_source_session_id',
                                'adoption_source_operation_id', 'adoption_resolution_sha256')})
                    if op.get('harvest_evidence_sequence') is not None:
                        slots = op.get('harvest_slot_awards')
                        if (op['harvest_evidence_sequence'] != sequence or not isinstance(slots, list)
                                or len(slots) != 8 or any(not isinstance(slot, dict)
                                    or not _integer(slot.get('color'), 0, 4)
                                    or not _integer(slot.get('number'), 0, 0x7FFFFFFF) for slot in slots)):
                            return fail('soul_harvest_evidence_invalid')
                        try:
                            harvest_board = (None if op.get('harvest_before_board') is None else
                                             _board_tuple(op['harvest_before_board']))
                        except ValueError as exc:
                            return fail(str(exc))
                        if (not op['auto_harvest'] and (not decisions
                                or decisions[-1].decision.action != 'harvest'
                                or decisions[-1].decision.board != harvest_board)):
                            return fail('soul_harvest_before_board_mismatch')
                        receipt = replace(receipt, harvest_before_board=harvest_board,
                            harvest_slot_awards=tuple((slot['color'], slot['number']) for slot in slots),
                            harvest_evidence_sequence=sequence)
                    if _receipt_matches_operation(s):
                        receipt = replace(receipt,
                            native_harvest_receipt=deepcopy(s['historical_receipt']))
                    return replace(result('COMPLETED', 'soul_round_verified'), cleanup='PANEL_RETAINED',
                                   blue_completed=int(mode == 1), purple_completed=int(mode == 2),
                                   coins_spent=op['coins_spent'], rounds=(receipt,), quote=soul_quote(s))
                if status == 'COMPLETED':
                    next_open = offer == 'open'
                    if (stage == 'close' and (not offer or next_open) and s['ui_cleanup'] == 'clear' and op['harvest_verified']
                            ):
                        return result('COMPLETED', reason or 'soul_progress_and_close_verified', clean=True)
                    return fail('soul_close_unverified')
                if offer and (status != 'READY' or offer not in _OFFERS.get(stage, ()) or s['reason']
                              or s['ui_cleanup'] != 'none'
                              or offer in ('grow', 'harvest') and not (op['start_verified'] or op.get('adopted_board'))
                              or offer == 'close' and not op['harvest_verified']):
                    return fail('soul_unexpected_action_or_quote')
                if status == 'READY' and offer == 'close' and stage == 'grow' and (
                        op.get('auto_harvest') is not True or any(slot['color'] for slot in s['board'])):
                    return fail('soul_auto_harvest_unverified')
                if status == 'READY' and offer in ('grow', 'harvest') and _growth_metadata(s):
                    board = [(slot['color'], slot['level']) for slot in s['board']]
                    if not any(color for color, _ in board):
                        return fail('soul_empty_board_not_settled')
                    try:
                        economics = economic_context(s, op.get('coins_spent', 0))
                        decision = decide_soul_action(board, s['growth_count'], s['coins'], s['growth_cost'],
                            policy=self._policy, budget_remaining=economics['plan_budget_remaining'],
                            joint_model=self._joint_model, start_cost=start_cost,
                            economic_context=economics,
                            exploration_remaining=max(0, self._policy.explore_growths - sum(
                                row.decision.reason in EXPLORATION_REASONS
                                for row in growths if row.decision is not None)),
                            exploration_history=exploration_history, round_growths=tuple(growths),
                            force_harvest_reason=('soul_evidence_write_failed' if self._evidence_error else
                                                 'soul_explicit_harvest_current_board' if harvest_only else
                                                 'soul_native_harvest_only' if offer == 'harvest' else
                                                 ''))
                        choice = decision.action
                    except ValueError as exc:
                        return fail(str(exc))
                    if (choice not in ('grow', 'harvest') or choice == 'grow' and offer != 'grow'
                            or harvest_only and choice != 'harvest'):
                        return fail('soul_growth_policy_or_native_offer_invalid')
                    offer = choice

            previous = s
            if self.paused() and status == 'READY' and not refused_candidates:
                # Settled ownership and exact quotes are rechecked while paused.
                # Pending callbacks retain their finite reconciliation deadline.
                deadline = self.clock() + self.timeout
            if not offer or self.paused():
                self.wait()
                continue
            if self.stopped():
                return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
            if self.clock() >= deadline:
                return fail(timeout_reason)
            if (s['revision'], offer) in refused_candidates:
                # A fresh read may return the same revision. It cannot revive
                # the refused candidate; wait for new evidence within the same
                # absolute deadline, without preparing or charging it again.
                self.wait()
                continue
            prior_growth_quote = growth_quote
            if _growth_projection(s):
                # Opening starts a new gather even when the native model still
                # exposes the previous completed gather's growth accounting.
                growth_quote = (0 if offer in ('open', 'next') else s['growth_count'], s['coins'], s['growth_cost'],
                                0 if offer in ('open', 'next') else op.get('coins_spent', 0))
            elif _growth_metadata(s):
                growth_quote = None
            elif offer == 'grow':
                return fail('soul_growth_quote_unavailable')
            # Quote each charge immediately before submission. Spending is shared
            # by all rounds; settlement and close never consume this allowance.
            if offer in ('start', 'grow'):
                quoted_cost = s['quotes'][color]['start_cost'] if offer == 'start' else s.get('growth_cost')
                if (not _integer(quoted_cost, 0 if offer == 'start' else 1, 0x7FFFFFFFFFFFFFFF)
                        or s.get('coins') is None or s['coins'] < quoted_cost
                        or offer == 'start' and quoted_cost != start_cost):
                    return fail('soul_charge_quote_or_balance_changed')
                available = remaining_budget(op.get('coins_spent', 0))
                if available is not None and quoted_cost > available:
                    if offer == 'grow':
                        self._resource_refusal = 'soul_plan_budget_quote_changed'
                        growth_quote = prior_growth_quote
                        continue
                    return fail('soul_plan_budget_quote_changed')
                try:
                    check_costs(read_policy(self.resource_policy_provider), {'money': quoted_cost},
                                reserved={'money': plan_spent + op.get('coins_spent', 0)},
                                purchase=offer == 'start' and quoted_cost > 0)
                except ResourcePolicyDenied as exc:
                    self._resource_refusal = str(exc)
                    growth_quote = prior_growth_quote
                    if offer == 'grow':
                        continue
                    return fail(str(exc))
            else:
                quoted_cost = 0
            if offer in ('adopt', 'start', 'grow', 'harvest'):
                if offer in ('grow', 'harvest'):
                    if decision is None or decision.action != offer:
                        return fail('soul_decision_missing')
                    decisions.append(SoulDecisionEvidence(round_number, s['revision'], op['sequence'],
                                                           'fresh_bound_frame', decision))
                pending_action = SoulActionEvidence(offer, 'prepared', round_number, s['revision'], None,
                    _board_tuple(s['board']), s.get('coins'), quoted_cost, op.get('coins_spent', 0),
                    decision if offer in ('grow', 'harvest') else None)
                publish_evidence('soul_action_prepared')
            if offer in ('start', 'grow') and self._evidence_error:
                pending_action = replace(pending_action, state='not_submitted_evidence_failure')
                growth_quote = prior_growth_quote
                if offer == 'grow':
                    decisions.pop()  # No command was admitted for this candidate.
                publish_evidence('soul_action_not_submitted')
                if offer == 'grow':
                    # Reobserve the owned board and choose harvest under the
                    # latched failure; do not retry the refused paid command.
                    continue
                return fail(self._evidence_error)
            try:
                # ObjectController evaluates this under its own lock. The
                # coordinator's pause and stop callbacks take another lock.
                wire = self.objects.submit_daily_soul(self.token, offer, expected_revision=s['revision'],
                    character_id=character_id, task_id=_TASK, operation_id=operation_id,
                    soul_mode=mode, free_only=int(free_only),
                    admission=lambda: self.clock() < deadline and
                        (offer not in ('start', 'grow') or not self._evidence_error))
            except ResourcePolicyDenied as exc:
                # This command was refused locally, so keep reconciling the
                # owned gather and choose harvest rather than retrying growth.
                self._resource_refusal = str(exc)
                growth_quote = prior_growth_quote
                if pending_action is not None:
                    pending_action = replace(pending_action, state='not_submitted_resource_policy',
                                             refusal_reason=str(exc))
                    self._not_submitted_actions.append(pending_action)
                    publish_evidence('soul_action_not_submitted')
                    pending_action = None
                if offer == 'grow':
                    decisions.pop()
                    continue
                return fail(str(exc))
            except CampaignAdmissionClosed as exc:
                growth_quote = prior_growth_quote
                # This exception proves no sequence/publication occurred. Undo
                # only this prepared candidate; the last published stage,
                # sequence, witness, growth receipts and spend remain intact.
                if (pending_action is not None and pending_action.state == 'prepared'
                        and pending_action.sequence is None):
                    pending_action = replace(pending_action, state='not_submitted_admission_closed',
                                             refusal_reason=str(exc))
                    self._not_submitted_actions.append(pending_action)
                    if offer in ('grow', 'harvest'):
                        decisions.pop()
                    publish_evidence('soul_action_not_submitted')
                    pending_action = None
                if self.stopped():
                    return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
                if self.clock() >= deadline:
                    return fail(timeout_reason)
                if (isinstance(exc, SoulPrepublicationAdmissionClosed) and str(exc) ==
                        'soul_current_observation_or_ownership_changed_before_submit'):
                    refused_candidates.add((s['revision'], offer))
                    if len(refused_candidates) > 3:
                        return fail('soul_prepublication_reobservation_limit')
                    # Re-enter normal observe and all identity/ownership,
                    # sequence, readiness, quote and budget checks. Never call
                    # submit again with this frame, or reset its deadline.
                    self.wait()
                    continue
                if self.paused():
                    continue
                return fail('soul_command_admission_closed')
            except Exception:
                submitted = True
                if pending_action is not None:
                    pending_action = replace(pending_action, state='submission_outcome_unknown')
                return fail('soul_command_outcome_unknown')
            submitted = True
            if pending_action is not None:
                sent_sequence = wire.get('requested_seq') if isinstance(wire, dict) else None
                pending_action = replace(pending_action, state='awaiting_verified_readback',
                    sequence=sent_sequence if _integer(sent_sequence, 1, 999999999) else None)
                publish_evidence('soul_action_pending')
            if self.stopped():
                return fail('user_stop')
            if self.clock() >= deadline:
                return fail(timeout_reason)

            ack = wire.get('ack') if isinstance(wire, dict) else None
            sent = wire.get('requested_seq') if isinstance(wire, dict) else None
            if (not isinstance(ack, dict) or not _integer(sent, op['sequence'] + 1, 999999999)
                    or ack.get('command_name') != 'daily-soul-command'
                    or not _integer(ack.get('command_seq'), sent, sent)):
                return fail('soul_command_binding_invalid')
            if ack.get('status') != 'COMPLETED':
                reason = ack.get('reason')
                return fail(reason if isinstance(reason, str) and reason else 'soul_command_outcome_unknown')
            details = ack.get('result')
            if not isinstance(details, dict) or details.get('daily_soul_enabled') is not True:
                return fail('soul_command_binding_invalid')
            gui, receipt = details.get('campaign_gui'), details.get('daily_soul')
            if (not _invoked_gui(gui, sent) or not _valid(receipt) or receipt['revision'] <= s['revision']
                    or not bound(receipt, offer, sent) or receipt['operation']['status'] not in _ACK_STATUSES[offer]
                    or stage and not _keeps_evidence(s, receipt)):
                return fail('soul_command_binding_invalid')
            if 'state' in wire:
                state = wire['state']
                latest = state.get('daily_soul') if isinstance(state, dict) and state.get('daily_soul_enabled') is True else None
                if (not _valid(latest) or not bound(latest, offer, sent) or not _follows(receipt, latest)
                        or 'campaign_gui' in state and not _invoked_gui(state['campaign_gui'], sent)):
                    return fail('soul_command_state_inconsistent')
                receipt = latest

            # A completed GUI ACK binds invocation. Only a subsequent fresh
            # observation can prove stock readback or authorize the next action.
            if offer == 'start':
                start_sequence = sent
            if receipt['operation']['start_verified'] and not initial_board:
                initial_board = tuple((slot['color'], slot['level']) for slot in receipt['board'])
            stage, sequence, previous = offer, sent, receipt
            refused_candidates.clear()
            deadline = self.clock() + self.timeout
