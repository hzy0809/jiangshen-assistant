"""Claim the current free rank settlement independently of daily activity #3."""
from __future__ import annotations

from dataclasses import dataclass, replace
import math
import re
import threading
import time
import uuid

from .objects import CampaignAdmissionClosed


_NAVIGATION = frozenset(('open', 'enter', 'open_chest'))
_OFFERS = {
    '': _NAVIGATION | {'claim'},
    'open': {'enter'},
    'enter': {'open_chest', 'claim'},
    'open_chest': {'claim'},
    'claim': {'reward_confirm'},
    'reward_confirm': {'close'},
    'close': {'close'},
}
_STATUSES = frozenset(('IDLE', 'READY', 'WAITING', 'BLOCKED', 'UNAVAILABLE',
                       'REJECTED', 'UNKNOWN', 'CLAIMED', 'COMPLETED', 'STOPPED'))
_TERMINAL = frozenset(('REJECTED', 'UNKNOWN', 'COMPLETED', 'STOPPED'))
_UI = frozenset(('', 'manual', 'unconfirmed', 'loading', 'confirm_ready', 'close_ready',
                 'continue_ready', 'retained', 'clear'))
_FLAGS = ('invoked', 'unresolved', 'navigation_pending', 'callback_returned', 'settled', 'activity_push_returned',
          'bonus_scan_complete')
_MONOTONIC_FLAGS = ('invoked', 'callback_returned', 'settled', 'activity_push_returned')
_CLAIM_BINDINGS = ('gold_before', 'counter_before', 'receipt_result', 'receipt_gold_consume',
                   'receipt_money', 'receipt_reputation')
_NUMBERS = ('sequence', 'refresh_cycle', 'free_since', 'bonus_time', 'receipt_opcode', 'receipt_time',
            'free_rewards_remaining', 'paid_rewards_skipped', 'bonus_rank', 'rearmed_sequence', 'rearmed_revision')
_OPTIONAL_NUMBERS = ('current', 'required', 'gold_before', 'gold_after', 'counter_before', 'counter_after',
                     'receipt_result', 'receipt_gold_consume', 'receipt_money', 'receipt_reputation')
_FIELDS = frozenset(('schema', 'revision', 'activity_id', 'status', 'reason', 'next_action', 'character_id',
                     'operation_id', 'rearmed_operation_id', 'ui_disposition', *_FLAGS, *_NUMBERS, *_OPTIONAL_NUMBERS))


@dataclass(frozen=True)
class DailyArenaChestResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    stock_callback_returned: bool = False
    account_progress_observed: bool = False
    activity_push_returned: bool = False
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    bonus_time: int | None = None
    reward_claim_observed: bool = False
    received_money: int | None = None
    received_reputation: int | None = None
    free_rewards_remaining: int | None = None
    paid_rewards_skipped: int | None = None
    bonus_rank: int | None = None
    bonus_scan_complete: bool = False
    claims_completed: int = 0
    claimed_bonus_times: tuple[int, ...] = ()
    operation_ids: tuple[str, ...] = ()
    rearm_count: int = 0
    all_current_free_rewards_claimed: bool = False
    page_reuse_count: int = 0


def _role_id(value):
    return (isinstance(value, str) and 0 < len(value) <= 20 and value.isascii() and value.isdecimal()
            and value[0] != '0' and int(value) <= 2**64 - 1)


def _uint(value):
    return type(value) is int and 0 <= value <= 2**32 - 1


def _valid_snapshot(snapshot):
    if not isinstance(snapshot, dict) or snapshot.keys() != _FIELDS:
        return False
    s = snapshot
    if (type(s['schema']) is not int or s['schema'] != 1 or type(s['activity_id']) is not int
            or s['activity_id'] != 3 or type(s['revision']) is not int or not 1 <= s['revision'] <= 999_999_999
            or any(not isinstance(s[key], str) for key in ('status', 'reason', 'next_action', 'ui_disposition'))
            or s['status'] not in _STATUSES or s['next_action'] not in _OFFERS or s['ui_disposition'] not in _UI
            or not (s['character_id'] == '' or _role_id(s['character_id']))
            or not isinstance(s['operation_id'], str)
            or s['operation_id'] and re.fullmatch('[0-9a-f]{32}', s['operation_id']) is None
            or not isinstance(s['rearmed_operation_id'], str)
            or s['rearmed_operation_id'] and re.fullmatch('[0-9a-f]{32}', s['rearmed_operation_id']) is None
            or any(type(s[key]) is not bool for key in _FLAGS)
            or any(not _uint(s[key]) for key in _NUMBERS)
            or not 0 <= s['sequence'] <= 999_999_999
            or s['rearmed_sequence'] > 999_999_999
            or any(s[key] is not None and not _uint(s[key]) for key in _OPTIONAL_NUMBERS)):
        return False
    if (bool(s['sequence']) != bool(s['operation_id'])
            or bool(s['rearmed_sequence']) != bool(s['rearmed_operation_id'])
            or bool(s['rearmed_revision']) != bool(s['rearmed_operation_id'])
            or s['rearmed_revision'] >= s['revision']
            or s['sequence'] and s['sequence'] <= s['rearmed_sequence']
            or s['invoked'] and not s['operation_id']
            or s['navigation_pending'] and not (s['operation_id'] or
                s['rearmed_operation_id'] and s['ui_disposition'] == 'retained')
            or s['unresolved'] != (s['invoked'] and not s['settled'])
            or (s['callback_returned'] or s['settled']) and not s['invoked']
            or s['activity_push_returned'] and not s['settled']
            or s['settled'] and not s['callback_returned']
            or s['status'] in ('CLAIMED', 'COMPLETED') and not s['settled']
            or s['status'] == 'COMPLETED' and (s['navigation_pending'] or s['ui_disposition'] != 'clear')):
        return False
    if s['ui_disposition'] == 'continue_ready' and (s['status'] != 'CLAIMED' or not s['settled']
            or s['next_action'] or not s['bonus_scan_complete'] or s['free_rewards_remaining'] == 0):
        return False
    offer = s['next_action']
    if offer in _NAVIGATION or offer == 'claim':
        if s['status'] != 'READY' or s['invoked']:
            return False
    if offer in ('reward_confirm', 'close'):
        if s['status'] != 'CLAIMED' or s['ui_disposition'] != ('confirm_ready' if offer == 'reward_confirm' else 'close_ready'):
            return False
    if s['receipt_opcode'] == 0:
        return (s['receipt_result'] is None and s['receipt_time'] == 0 and s['receipt_gold_consume'] is None
                and s['receipt_money'] is None and s['receipt_reputation'] is None)
    return (s['invoked'] and s['receipt_opcode'] == 1122 and s['receipt_result'] in (0, 1, 2)
            and s['receipt_gold_consume'] is not None and s['receipt_money'] is not None
            and s['receipt_reputation'] is not None)


def _claim_evidence(s, baseline, required, bonus):
    if s['required'] != required or s['current'] not in (baseline, baseline + 1):
        return False
    if not s['invoked']:
        return (s['current'] == baseline and s['counter_before'] is None and s['counter_after'] is None
                and s['gold_before'] is None and s['gold_after'] is None)
    if (s['counter_before'] != baseline or s['gold_before'] is None
            or s['gold_after'] not in (None, s['gold_before'])
            or s['counter_after'] not in (None, baseline, baseline + 1)):
        return False
    receipt_ok = (s['receipt_opcode'] == 1122 and s['receipt_result'] == 0 and s['receipt_time'] == bonus
                  and s['receipt_gold_consume'] == 0)
    if s['receipt_opcode'] and not receipt_ok:
        return False
    if s['settled']:
        # The native settled/push witnesses include both activity caches and
        # unchanged bound premium/spend fields. There is no wire nonce on 1122.
        expected = min(required, baseline + 1)
        return (receipt_ok and (baseline >= required or s['activity_push_returned'])
                and s['gold_after'] == s['gold_before']
                and s['counter_after'] == s['current'] == expected)
    return s['status'] not in ('CLAIMED', 'COMPLETED')


def _follows(previous, snapshot):
    return (previous is None or
            (snapshot['revision'] > previous['revision'] or snapshot == previous)
            and all(previous[key] == snapshot[key] for key in
                    ('rearmed_operation_id', 'rearmed_sequence', 'rearmed_revision'))
            and (previous['status'] not in _TERMINAL or snapshot['status'] == previous['status'])
            and (not previous['navigation_pending'] or snapshot['navigation_pending'] or snapshot['status'] in _TERMINAL)
            and all(not previous[key] or snapshot[key] for key in _MONOTONIC_FLAGS)
            and all(previous[key] is None or previous[key] == snapshot[key] for key in _CLAIM_BINDINGS))


def _rearmed_snapshot(snapshot, *, character_id, operation_id, expected_revision, sequence, retained=None):
    # The bridge validates either native handoff shape. The executor supplies
    # the exact mode proven by its preceding reward observation.
    if retained is None:
        retained = isinstance(snapshot, dict) and snapshot.get('ui_disposition') == 'retained'
    return (_valid_snapshot(snapshot) and snapshot['character_id'] == character_id
            and snapshot['revision'] > expected_revision
            and snapshot['rearmed_operation_id'] == operation_id
            and snapshot['rearmed_revision'] == expected_revision and snapshot['rearmed_sequence'] == sequence
            and snapshot['operation_id'] == '' and snapshot['sequence'] == 0
            and snapshot['navigation_pending'] == retained
            and snapshot['ui_disposition'] == ('retained' if retained else '')
            and (not retained or snapshot['status'] == 'READY' and snapshot['next_action'] == 'claim')
            and not any(snapshot[key] for key in ('invoked', 'unresolved', 'callback_returned',
                                                 'settled', 'activity_push_returned')))


class DailyArenaChestExecutor:
    """Drive native offers within the caller's exclusive daily session."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=22.0, clock=time.monotonic):
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError('daily_arena_timeout_invalid')
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait if wait is not None else lambda: time.sleep(.1)
        self.timeout, self.clock = timeout, clock
        self._lock = threading.Lock()
        self._submitted = False
        self._completed_snapshot = None

    def execute(self, task_id: int, *, character_id: str, expected_cycle: int) -> DailyArenaChestResult:
        if type(task_id) is not int or task_id != 3:
            raise ValueError('daily_arena_task_unsupported')
        if not _role_id(character_id) or not _uint(expected_cycle) or expected_cycle <= 18000:
            return DailyArenaChestResult('BLOCKED', 'arena_expected_identity_invalid')
        if not self._lock.acquire(blocking=False):
            return DailyArenaChestResult('UNKNOWN', 'arena_previous_attempt', cleanup='UNVERIFIED')
        try:
            if self._submitted:
                return DailyArenaChestResult('UNKNOWN', 'arena_previous_attempt', cleanup='UNVERIFIED')
            return self._execute_available(character_id, expected_cycle)
        finally:
            self._lock.release()

    def _rearm(self, snapshot, character_id, cycle):
        retained = (_valid_snapshot(snapshot) and snapshot['status'] == 'CLAIMED'
                    and snapshot['ui_disposition'] == 'continue_ready')
        if (not _valid_snapshot(snapshot) or not retained and snapshot['status'] != 'COMPLETED'
                or snapshot['character_id'] != character_id or snapshot['refresh_cycle'] != cycle
                or not snapshot['bonus_scan_complete'] or snapshot['unresolved']
                or not retained and (snapshot['navigation_pending'] or snapshot['ui_disposition'] != 'clear')
                or not _uint(snapshot['counter_before'])
                or not _uint(snapshot['required'])
                or not _claim_evidence(snapshot, snapshot['counter_before'], snapshot['required'], snapshot['bonus_time'])):
            raise ValueError('arena_rearm_completion_unverified')
        wire = self.objects.rearm_daily_arena(self.token, character_id=character_id,
                operation_id=snapshot['operation_id'], expected_revision=snapshot['revision'])
        ack = wire.get('ack') if isinstance(wire, dict) else None
        sent = wire.get('requested_seq') if isinstance(wire, dict) else None
        if (not isinstance(ack, dict) or type(sent) is not int or sent <= snapshot['sequence']
                or ack.get('command_name') != 'daily-arena-command' or ack.get('command_seq') != sent
                or ack.get('status') != 'COMPLETED'):
            raise ValueError((ack or {}).get('reason') or 'arena_rearm_command_unverified')
        details = ack.get('result') or {}
        fresh = details.get('daily_arena')
        if (details.get('daily_arena_enabled') is not True or not _rearmed_snapshot(fresh,
                character_id=character_id, operation_id=snapshot['operation_id'],
                expected_revision=snapshot['revision'], sequence=sent, retained=retained) or fresh['refresh_cycle'] != cycle):
            raise ValueError('arena_rearm_result_unverified')
        current = (wire.get('state') or {}).get('daily_arena')
        if (not _rearmed_snapshot(current, character_id=character_id, operation_id=snapshot['operation_id'],
                expected_revision=snapshot['revision'], sequence=sent, retained=retained)
                or current['refresh_cycle'] != cycle or not _follows(fresh, current)):
            raise ValueError('arena_rearm_state_unverified')
        if retained and any(fresh[key] != current[key] or fresh[key] != snapshot[key]
                            for key in ('current', 'required', 'free_since', 'free_rewards_remaining',
                                        'paid_rewards_skipped')):
            raise ValueError('arena_rearm_retained_evidence_changed')
        if retained and (fresh['bonus_time'] == snapshot['bonus_time'] or fresh['bonus_time'] != current['bonus_time']):
            raise ValueError('arena_rearm_retained_bonus_unverified')
        return dict(current) if retained else None

    def _execute_available(self, character_id, cycle):
        claims, excluded = [], set()
        rearms = page_reuses = 0
        retained_offer = None

        def combined(step):
            if not claims:
                return replace(step, rearm_count=rearms, page_reuse_count=page_reuses,
                    all_current_free_rewards_claimed=step.status == 'ALREADY_COMPLETE'
                        and step.bonus_scan_complete and step.free_rewards_remaining == 0)
            status, reason = step.status, step.reason
            if status == 'ALREADY_COMPLETE':
                status, reason = 'COMPLETED', 'arena_all_current_free_rewards_claimed'
            elif status in ('BLOCKED', 'UNAVAILABLE'):
                status = 'PARTIAL'
            clean = step.cleanup
            if clean == 'NOT_NEEDED':
                clean = 'VERIFIED_CLOSED' if claims[-1].cleanup == 'VERIFIED_CLOSED' else 'UNVERIFIED'
            elif clean == 'PAGE_RETAINED':
                clean = 'UNVERIFIED'
            return replace(step, status=status, reason=reason,
                progress_before=claims[0].progress_before,
                progress_after=step.progress_after if step.progress_after is not None else claims[-1].progress_after,
                required=step.required if step.required is not None else claims[0].required,
                stock_callback_returned=any(x.stock_callback_returned for x in claims),
                account_progress_observed=any(x.account_progress_observed for x in claims),
                activity_push_returned=any(x.activity_push_returned for x in claims),
                reward_claim_observed=True, cleanup=clean,
                operation_id=claims[-1].operation_id, bonus_time=claims[-1].bonus_time,
                bonus_rank=claims[-1].bonus_rank,
                received_money=sum(x.received_money or 0 for x in claims),
                received_reputation=sum(x.received_reputation or 0 for x in claims),
                claims_completed=len(claims),
                claimed_bonus_times=tuple(x.bonus_time for x in claims),
                operation_ids=tuple(x.operation_id for x in claims), rearm_count=rearms, page_reuse_count=page_reuses,
                all_current_free_rewards_claimed=status == 'COMPLETED' and step.bonus_scan_complete
                    and step.free_rewards_remaining == 0)

        try:
            observed = self.objects.observe_daily_arena(self.token)
            prior = observed.get('arena') if isinstance(observed, dict) and observed.get('enabled') is True else None
            if _valid_snapshot(prior) and prior['status'] == 'COMPLETED':
                if self.stopped():
                    return DailyArenaChestResult('STOPPED', 'user_stop')
                while self.paused() and not self.stopped():
                    self.wait()
                if self.stopped():
                    return DailyArenaChestResult('STOPPED', 'user_stop')
                self._rearm(prior, character_id, cycle)
                excluded.add(prior['bonus_time'])
                rearms += 1
        except Exception as exc:
            return DailyArenaChestResult('STOPPED' if self.stopped() else 'BLOCKED',
                                         'user_stop' if self.stopped() else f'arena_rearm_preparation_failed ({exc})')

        for _ in range(65):
            self._completed_snapshot = None
            step = self._execute(character_id, cycle, excluded, retained_offer)
            retained_offer = None
            if step.reward_claim_observed:
                if step.bonus_time in excluded:
                    return combined(replace(step, status='UNKNOWN', reason='arena_bonus_repeated'))
                claims.append(step)
                excluded.add(step.bonus_time)
            retained = step.status == 'CLAIMED' and step.cleanup == 'PAGE_RETAINED'
            if step.status != 'COMPLETED' and not retained:
                return combined(step)
            if step.status == 'COMPLETED' and step.bonus_scan_complete and step.free_rewards_remaining == 0:
                return combined(replace(step, reason='arena_all_current_free_rewards_claimed'))
            if len(claims) >= 64:
                return combined(replace(step, status='PARTIAL', reason='arena_free_claim_limit_reached'))
            while self.paused() and not self.stopped():
                self.wait()
            if self.stopped():
                return combined(replace(step, status='STOPPED', reason='user_stop'))
            try:
                retained_offer = self._rearm(self._completed_snapshot, character_id, cycle)
                page_reuses += int(retained_offer is not None)
                rearms += 1
            except Exception as exc:
                return combined(replace(step, status='STOPPED' if self.stopped() else 'PARTIAL',
                    reason='user_stop' if self.stopped() else f'arena_rearm_failed ({exc})'))
        return combined(DailyArenaChestResult('PARTIAL', 'arena_free_claim_limit_reached'))

    def _execute(self, character_id, cycle, excluded=(), retained_offer=None):
        operation = uuid.uuid4().hex
        action, sequence, closes = '', 0, 0
        baseline = required = bonus = free_since = None
        previous, witness, latest = retained_offer, None, None
        submitted = False
        deadline = self.clock() + self.timeout

        def result(status, reason, *, clean=False, retained=False, foreign_hold=False):
            observed = witness or latest or {}
            return DailyArenaChestResult(
                status, reason, baseline, observed.get('counter_after') if observed.get('settled') else baseline,
                required, bool(observed.get('callback_returned')), bool(observed.get('activity_push_returned')),
                bool(observed.get('activity_push_returned')),
                'VERIFIED_CLOSED' if clean else 'PAGE_RETAINED' if retained else
                'UNVERIFIED' if submitted or foreign_hold else 'NOT_NEEDED',
                operation if submitted else '', bonus,
                reward_claim_observed=bool(observed.get('settled')),
                received_money=observed.get('receipt_money'), received_reputation=observed.get('receipt_reputation'),
                free_rewards_remaining=observed.get('free_rewards_remaining'),
                paid_rewards_skipped=observed.get('paid_rewards_skipped'), bonus_rank=observed.get('bonus_rank'),
                bonus_scan_complete=bool(observed.get('bonus_scan_complete')))

        def fail(reason):
            return result('CLAIMED' if witness and witness['settled'] else
                          'UNKNOWN' if submitted else 'BLOCKED', reason)

        def same_offer(s):
            return (s['character_id'] == character_id and s['refresh_cycle'] == cycle
                    and s['bonus_time'] == bonus and s['free_since'] == free_since
                    and _claim_evidence(s, baseline, required, bonus))

        def binds_action(s, command_action, command_sequence):
            return (s['operation_id'] == operation and s['sequence'] == command_sequence
                    and s['invoked'] == (command_action not in _NAVIGATION) and same_offer(s))

        while True:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if not action and self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                continue
            timeout_reason = 'arena_readback_timeout' if action else 'arena_admission_timeout'
            if self.clock() >= deadline:
                return fail(timeout_reason)
            try:
                observed = self.objects.observe_daily_arena(self.token)
            except Exception as exc:
                if self.stopped():
                    return result('STOPPED', 'user_stop')
                if self.clock() >= deadline:
                    return fail(timeout_reason)
                return fail(f'arena_observation_failed ({exc})')

            stopped, expired = self.stopped(), self.clock() >= deadline
            enabled = observed.get('enabled') if isinstance(observed, dict) else None
            s = observed.get('arena') if enabled is True else None
            valid = _valid_snapshot(s)
            if valid:
                latest = s
            follows = valid and _follows(previous, s)
            bound = action and follows and binds_action(s, action, sequence)
            if bound:
                witness = s
            if stopped:
                return result('STOPPED', 'user_stop')
            if expired:
                return fail(timeout_reason)
            if enabled is False:
                return fail('arena_disabled_during_operation') if submitted else result('UNAVAILABLE', 'arena_disabled')
            if not valid:
                return fail('arena_observation_invalid')
            if not follows:
                return fail('arena_observation_regressed')
            status, offer = s['status'], s['next_action']
            if s['character_id'] != character_id or s['refresh_cycle'] != cycle:
                return fail('arena_identity_or_cycle_changed')

            if not action:
                if s['operation_id'] or s['invoked'] or s['navigation_pending'] and retained_offer is None:
                    return result('UNKNOWN', 'arena_foreign_or_prior_operation', foreign_hold=True)
                if retained_offer is not None and (not s['navigation_pending'] or s['ui_disposition'] != 'retained'
                        or any(s[key] != retained_offer[key] for key in ('bonus_time', 'bonus_rank', 'current',
                            'required', 'free_since', 'free_rewards_remaining', 'paid_rewards_skipped'))):
                    return fail('arena_retained_page_evidence_changed')
                if status in ('BLOCKED', 'UNAVAILABLE'):
                    baseline, required = s['current'], s['required']
                    if (status == 'UNAVAILABLE' and s['reason'] == 'arena_no_current_free_bonus'
                            and s['bonus_scan_complete'] and s['free_rewards_remaining'] == 0 and not offer):
                        return result('ALREADY_COMPLETE', s['reason'])
                    return result(status, s['reason'] or 'arena_unavailable')
                if status == 'WAITING' and not offer:
                    previous = s
                    self.wait()
                    continue
                if status != 'READY' or not offer:
                    return fail(s['reason'] or 'arena_stock_entry_unavailable')
                baseline, required = s['current'], s['required']
                bonus, free_since = s['bonus_time'], s['free_since']
                if bonus in excluded:
                    return fail('arena_bonus_already_claimed_this_run')
                day = cycle - 18000
                if (baseline is None or required is None or not 0 <= baseline <= required <= 255
                        or not free_since or free_since not in (day - 86400, day)
                        or not free_since <= bonus < cycle + 86400 or not same_offer(s)):
                    return fail('arena_free_bonus_unverified')
            else:
                if not bound:
                    return fail('arena_operation_or_evidence_changed')
                if status == 'STOPPED':
                    return result('STOPPED', s['reason'] or 'arena_native_stopped')
                if status in ('REJECTED', 'UNKNOWN', 'UNAVAILABLE', 'BLOCKED'):
                    return fail(s['reason'] or 'arena_operation_unresolved')
                if status == 'COMPLETED':
                    self._completed_snapshot = dict(s)
                    return result('COMPLETED', s['reason'] or 'arena_progress_and_close_verified', clean=True)
                if status == 'CLAIMED' and s['ui_disposition'] == 'continue_ready':
                    self._completed_snapshot = dict(s)
                    return result('CLAIMED', s['reason'] or 'arena_next_free_bonus_same_page', retained=True)

            previous = s
            if not offer or self.paused():
                if self.clock() >= deadline:
                    return fail(s['reason'] or 'arena_readback_timeout')
                self.wait()
                continue
            if offer not in _OFFERS[action] or offer == 'close' and closes >= 2:
                return fail('arena_unexpected_action_offer')
            if self.clock() >= deadline:
                return fail('arena_action_wait_timeout')
            if self.stopped() or self.paused():
                continue
            try:
                wire = self.objects.submit_daily_arena(self.token, offer, expected_revision=s['revision'],
                                                       character_id=character_id, operation_id=operation)
            except CampaignAdmissionClosed:
                if self.stopped() or self.paused():
                    continue
                return fail('arena_command_admission_closed')
            except Exception as exc:
                submitted = self._submitted = True
                return fail(f'arena_command_outcome_unknown ({exc})')
            submitted = self._submitted = True
            if self.stopped():
                return result('STOPPED', 'user_stop')

            ack = wire.get('ack') if isinstance(wire, dict) else None
            sent = wire.get('requested_seq') if isinstance(wire, dict) else None
            if (not isinstance(ack, dict) or type(sent) is not int or sent <= sequence
                    or ack.get('command_name') != 'daily-arena-command'
                    or type(ack.get('command_seq')) is not int or ack['command_seq'] != sent):
                return fail('arena_command_binding_invalid')
            if ack.get('status') != 'COMPLETED':
                reason = ack.get('reason')
                return fail(reason if isinstance(reason, str) and reason else 'arena_command_outcome_unknown')
            details = ack.get('result')
            if (not isinstance(details, dict) or details.get('daily_arena_enabled') is not True):
                return fail('arena_command_binding_invalid')
            gui = details.get('campaign_gui')
            if (not isinstance(gui, dict) or type(gui.get('command_seq')) is not int or gui['command_seq'] != sent
                    or gui.get('phase') != 'completed' or gui.get('invocation_attempted') is not True):
                return fail('arena_command_binding_invalid')
            receipt = details.get('daily_arena')
            valid_status = {'READY', 'WAITING'} if offer in _NAVIGATION else (
                {'WAITING', 'CLAIMED', 'COMPLETED'} if offer == 'claim' else {'CLAIMED', 'COMPLETED'})
            if (not _valid_snapshot(receipt) or receipt['revision'] <= s['revision'] or not _follows(s, receipt)
                    or receipt['status'] not in valid_status or not binds_action(receipt, offer, sent)
                    or offer != 'claim' and receipt['status'] != 'COMPLETED' and not receipt['navigation_pending']):
                return fail('arena_command_binding_invalid')
            if 'state' in wire:
                state = wire['state']
                latest = state.get('daily_arena') if isinstance(state, dict) and state.get('daily_arena_enabled') is True else None
                if (not _valid_snapshot(latest) or not _follows(receipt, latest)
                        or not binds_action(latest, offer, sent)):
                    return fail('arena_command_state_inconsistent')
                receipt = latest
            # The GUI ACK binds invocation only. A separate observation must
            # witness progress and authorize each subsequent stock action.
            action, sequence, previous = offer, sent, receipt
            closes += offer == 'close'
            deadline = self.clock() + self.timeout
