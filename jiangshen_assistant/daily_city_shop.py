"""Run one bounded current-account money-shop pass through stock callbacks."""
from __future__ import annotations

from dataclasses import dataclass
import math
import re
import threading
import time
import uuid

from .objects import CampaignAdmissionClosed
from .resource_policy import ResourcePolicyDenied


TASK_ID = 308
DURATION_ACTIONS = {0: 'open', 1200: 'produce_short', 3600: 'produce_medium', 43200: 'produce_long'}
ACTIONS = frozenset(('open', 'open_harvest', 'collect', 'close_reward', 'open_product',
                     'produce_short', 'produce_medium', 'produce_long', 'close_product', 'finish'))
_TERMINAL = frozenset(('COMPLETED', 'NO_FREE_REWARD', 'PARTIAL', 'UNAVAILABLE'))
_STATUSES = _TERMINAL | {'IDLE', 'QUEUED', 'OPENING', 'READY', 'PENDING', 'REWARD',
                         'BLOCKED', 'UNKNOWN', 'STOPPED'}
_COUNTERS = ('candidate_count', 'processed_candidates', 'collected_buildings',
             'produced_buildings', 'skipped_buildings', 'gold_cost', 'mana_cost', 'money_received')
_FLAGS = ('observation_available', 'gui_invocation_attempted', 'mutation_attempted',
          'stock_callback_returned', 'cleanup_pending', 'busy', 'home_ready',
          'reward_pending', 'limited', 'task_confirmed', 'checklist_complete')


@dataclass(frozen=True)
class DailyCityShopResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    task_confirmed: bool = False
    mutation_attempted: bool = False
    cleanup: str = 'NOT_NEEDED'
    checklist_complete: bool = False
    operation_id: str = ''
    duration_seconds: int = 0
    candidate_count: int = 0
    collected_buildings: int = 0
    produced_buildings: int = 0
    skipped_buildings: int = 0
    gold_cost: int = 0
    mana_cost: int = 0
    money_received: int = 0
    stock_callback_returned: bool = False
    receipts: tuple[dict, ...] = ()
    skipped: tuple[dict, ...] = ()
    cleanup_pending: bool = False
    native_snapshot: dict | None = None
    cleanup_evidence: tuple[dict, ...] = ()


def _uint(value, maximum=0xFFFFFFFFFFFFFFFF):
    return type(value) is int and 0 <= value <= maximum


def _role(value):
    return (isinstance(value, str) and re.fullmatch(r'[1-9][0-9]{0,19}', value) is not None
            and int(value) <= 0xFFFFFFFFFFFFFFFF)


def _snapshot(value):
    return (isinstance(value, dict) and type(value.get('schema')) is int and value['schema'] == 1
            and type(value.get('task_id')) is int and value['task_id'] == TASK_ID
            and _uint(value.get('revision'), 999999999) and value['revision'] > 0
            and (value.get('role_id') == '0' or _role(value.get('role_id')))
            and _uint(value.get('cycle'), 0xFFFFFFFF)
            and value.get('status') in _STATUSES
            and value.get('next_action') in ('', *ACTIONS)
            and isinstance(value.get('reason'), str) and isinstance(value.get('gate'), str)
            and isinstance(value.get('operation_id'), str)
            and (value['operation_id'] == '' or re.fullmatch(r'[0-9a-f]{32}', value['operation_id']) is not None)
            and _uint(value.get('sequence'), 999999999)
            and value.get('duration_seconds') in DURATION_ACTIONS
            and type(value.get('duration_seconds')) is int
            and all(type(value.get(key)) is bool for key in _FLAGS)
            and all(_uint(value.get(key)) for key in _COUNTERS)
            and value['candidate_count'] <= 256
            and value['processed_candidates'] <= value['candidate_count']
            and value['collected_buildings'] <= value['candidate_count']
            and value['produced_buildings'] <= value['candidate_count']
            and isinstance(value.get('receipts'), list) and len(value['receipts']) <= 512
            and all(isinstance(row, dict) for row in value['receipts'])
            and isinstance(value.get('skipped'), list) and len(value['skipped']) <= 256
            and all(isinstance(row, dict) for row in value['skipped'])
            and value['skipped_buildings'] == len(value['skipped']))


def _receipt_identity(rows, operation, candidate_count, duration):
    seen = set()
    for row in rows:
        opcode = row.get('opcode')
        identity = (opcode, row.get('building_id'))
        if (row.get('operation_id') != operation or opcode not in (1154, 1158)
                or not _uint(row.get('building_id'), 255)
                or row.get('building_template_id') not in (5, 10, 16, 20)
                or not _uint(row.get('product_id'), 255) or row['product_id'] == 0
                or not _uint(row.get('sequence'), 999999999) or row['sequence'] == 0
                or not _uint(row.get('source_revision'), 999999999) or row['source_revision'] == 0
                or identity in seen or not isinstance(row.get('error'), str)):
            return False
        if opcode == 1158 and (duration == 0 or row.get('duration_seconds') != duration
                               or row.get('action') != DURATION_ACTIONS[duration]):
            return False
        if opcode == 1154 and row.get('action') != 'collect':
            return False
        seen.add(identity)
    return len(rows) <= 2 * candidate_count


def _settled(value):
    """Completion requires every individual send and its owned UI cleanup."""
    harvested = produced = mana = gold = money = 0
    for row in value['receipts']:
        if (row.get('response_observed') is not True or row.get('result') != 0
                or row.get('response_building_id') != row['building_id']
                or row.get('response_product_id') != row['product_id']
                or row.get('receipt_success') is not True
                or row.get('stock_callback_returned') is not True
                or row.get('state_confirmed') is not True or row.get('ui_closed') is not True
                or row.get('is_immediate') is not False or row.get('error') != ''
                or row.get('gold_cost') != 0 or not _uint(row.get('mana_cost'))
                or not isinstance(row.get('response_sha256'), str)
                or re.fullmatch(r'[0-9a-f]{64}', row['response_sha256']) is None):
            return False
        gold += row['gold_cost']
        mana += row['mana_cost']
        if row['opcode'] == 1154:
            if (row.get('reward_closed') is not True or row.get('product_cleared') is not True
                    or row.get('money_credited') is not True or not _uint(row.get('money_received'))
                    or row['money_received'] == 0 or row['mana_cost'] != 0):
                return False
            money += row['money_received']
            harvested += 1
        else:
            produced += 1
    return (value['collected_buildings'] == harvested and value['produced_buildings'] == produced
            and value['gold_cost'] == gold and value['mana_cost'] == mana and value['money_received'] == money
            and value['processed_candidates'] == value['candidate_count']
            and value['home_ready'] is True and value['cleanup_pending'] is False
            and value['busy'] is False and value['reward_pending'] is False)


class DailyCityShopExecutor:
    """A single-use executor; an uncertain command is never submitted again."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=45.0, observation_timeout=10.0, clock=time.monotonic):
        if (not math.isfinite(timeout) or timeout <= 0
                or not math.isfinite(observation_timeout) or observation_timeout <= 0):
            raise ValueError('daily_city_shop_timeout_invalid')
        self.objects, self.token = objects, token
        self.stopped, self.paused, self.clock = stopped, paused, clock
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.observation_timeout = timeout, observation_timeout
        self._lock = threading.Lock()
        self._used = False
        self._last_result = None

    def execute(self, task_id=TASK_ID, *, character_id, expected_cycle=None, duration_seconds):
        if type(task_id) is not int or task_id != TASK_ID:
            raise ValueError('daily_city_shop_task_unsupported')
        if type(duration_seconds) is not int or duration_seconds not in DURATION_ACTIONS:
            raise ValueError('daily_city_shop_duration_unsupported')
        if not _role(character_id) or expected_cycle is not None and (
                not _uint(expected_cycle, 0xFFFFFFFF) or expected_cycle == 0):
            return DailyCityShopResult('BLOCKED', 'shop_expected_identity_invalid', duration_seconds=duration_seconds)
        if not self._lock.acquire(blocking=False):
            return DailyCityShopResult('UNKNOWN', 'shop_executor_in_use', cleanup='UNVERIFIED',
                                       duration_seconds=duration_seconds, cleanup_pending=True)
        try:
            if self._used:
                if self._last_result is not None and self._last_result.status == 'UNKNOWN':
                    return self._last_result
                return DailyCityShopResult('BLOCKED', 'shop_executor_used', duration_seconds=duration_seconds)
            self._used = True
            self._last_result = self._run(character_id, expected_cycle, duration_seconds)
            return self._last_result
        finally:
            self._lock.release()

    def _run(self, character_id, expected_cycle, duration_seconds):
        operation = uuid.uuid4().hex
        latest = previous = None
        latest_current = False
        submitted = False
        failed_reason = ''
        cleanup_attempted = False
        cleanup_deadline = None
        cleanup_evidence = []
        sent_sequences = set()
        command_count = 0
        deadline = self.clock() + self.timeout
        observation_deadline = self.clock() + self.observation_timeout

        resource_refusal = ''

        def result(status, reason):
            current = latest or {}
            owned = (latest_current and current.get('operation_id') == operation
                     and current.get('role_id') == character_id and current.get('cycle') == expected_cycle
                     and current.get('duration_seconds') == duration_seconds)
            closed = (owned and current.get('observation_available') is True
                      and current.get('home_ready') is True and current.get('cleanup_pending') is False
                      and current.get('busy') is False and current.get('reward_pending') is False)
            held = current.get('cleanup_pending') is True or current.get('busy') is True
            cleanup = ('VERIFIED_CLOSED' if submitted and closed else
                       'UNVERIFIED' if held or submitted else 'NOT_NEEDED')
            complete = closed and status in ('COMPLETED', 'NO_FREE_REWARD')
            return DailyCityShopResult(
                status=status, reason=reason, task_confirmed=complete and status == 'COMPLETED',
                mutation_attempted=current.get('mutation_attempted') is True, cleanup=cleanup,
                checklist_complete=complete and current.get('checklist_complete') is True,
                operation_id=operation if submitted else '', duration_seconds=duration_seconds,
                candidate_count=current.get('candidate_count', 0),
                collected_buildings=current.get('collected_buildings', 0),
                produced_buildings=current.get('produced_buildings', 0),
                skipped_buildings=current.get('skipped_buildings', 0), gold_cost=current.get('gold_cost', 0),
                mana_cost=current.get('mana_cost', 0), money_received=current.get('money_received', 0),
                stock_callback_returned=current.get('stock_callback_returned') is True,
                receipts=tuple(dict(row) for row in current.get('receipts', ())),
                skipped=tuple(dict(row) for row in current.get('skipped', ())),
                cleanup_pending=held or submitted and not closed,
                native_snapshot=dict(current) if current else None,
                cleanup_evidence=tuple(cleanup_evidence))

        def fail(reason):
            return result('UNKNOWN' if submitted else 'BLOCKED', reason)

        def cleanup_observation_pending():
            nonlocal cleanup_deadline
            if cleanup_attempted:
                return False
            if cleanup_deadline is None:
                cleanup_deadline = min(deadline, self.clock() + self.observation_timeout)
            return self.clock() < cleanup_deadline

        while True:
            if self.stopped():
                return result('UNKNOWN' if submitted else 'STOPPED', 'user_stop')
            if self.paused():
                paused_at = self.clock()
                self.wait()
                elapsed = max(0.0, self.clock() - paused_at)
                deadline += elapsed
                observation_deadline += elapsed
                if cleanup_deadline is not None:
                    cleanup_deadline += elapsed
                continue
            if self.clock() >= deadline:
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', failed_reason or 'shop_step_timeout')
            try:
                observed = self.objects.observe_daily_script(self.token, TASK_ID)
            except Exception as exc:
                latest_current = False
                return fail(f'shop_observation_failed ({exc})')
            if not _snapshot(observed):
                latest_current = False
                return fail('shop_snapshot_invalid')
            latest = observed
            latest_current = True
            if latest['observation_available'] is False:
                if submitted or latest['status'] == 'UNKNOWN':
                    failed_reason = failed_reason or latest['reason'] or latest['gate'] or 'shop_observation_unavailable'
                    owned = (submitted and latest['operation_id'] == operation
                             and latest['role_id'] == character_id and latest['cycle'] == expected_cycle
                             and latest['duration_seconds'] == duration_seconds)
                    # One bounded fresh observation can expose an owned normal
                    # close. An unavailable/stale snapshot never admits it.
                    if owned and cleanup_observation_pending():
                        self.wait()
                        continue
                    return result('UNKNOWN', failed_reason)
                if self.clock() >= observation_deadline:
                    return result('UNAVAILABLE', latest['reason'] or latest['gate'] or 'shop_observation_unavailable')
                self.wait()
                continue
            if latest['role_id'] != character_id:
                return fail('shop_character_changed')
            if expected_cycle is None:
                expected_cycle = latest['cycle']
            if not expected_cycle or latest['cycle'] != expected_cycle:
                return fail('shop_cycle_changed')
            if submitted:
                if latest['operation_id'] != operation or latest['duration_seconds'] != duration_seconds:
                    return result('UNKNOWN', 'shop_operation_or_duration_changed')
                if (not _receipt_identity(latest['receipts'], operation, latest['candidate_count'], duration_seconds)
                        or any(row['sequence'] not in sent_sequences for row in latest['receipts'])):
                    return result('UNKNOWN', 'shop_receipt_identity_invalid')
                if previous is not None:
                    if (latest['revision'] < previous['revision'] or latest['sequence'] < previous['sequence']
                            or latest['revision'] == previous['revision'] and latest != previous
                            or latest['candidate_count'] != previous['candidate_count'] and previous.get('phase') != 'opening'
                            or any(latest[key] < previous[key] for key in _COUNTERS if key != 'candidate_count')
                            or len(latest['receipts']) < len(previous['receipts'])):
                        return result('UNKNOWN', 'shop_operation_evidence_regressed')
            elif latest['operation_id'] or latest['cleanup_pending'] or latest['busy']:
                return result('UNKNOWN' if latest['cleanup_pending'] or latest['busy'] else 'BLOCKED', 'shop_previous_operation')
            status, action = latest['status'], latest['next_action']
            if status in ('UNKNOWN', 'STOPPED'):
                failed_reason = failed_reason or latest['reason'] or 'shop_operation_stopped'
                if status != 'UNKNOWN' or not submitted or cleanup_attempted:
                    return result(status, failed_reason)
                if not cleanup_observation_pending():
                    return result('UNKNOWN', failed_reason)
                if action != 'close_product':
                    previous = latest
                    self.wait()
                    continue
                cleanup_attempted = True
                cleanup_evidence.append({'phase': 'before_owned_close', 'snapshot': dict(latest)})
            elif failed_reason:
                if cleanup_observation_pending():
                    previous = latest
                    self.wait()
                    continue
                return result('UNKNOWN', failed_reason)
            if submitted and status in _TERMINAL:
                if not _settled(latest):
                    return result('UNKNOWN', 'shop_terminal_receipts_or_cleanup_unverified')
                limited = latest['limited'] or any(row.get('limits_request') is True for row in latest['skipped'])
                if (status in ('COMPLETED', 'NO_FREE_REWARD') and limited
                        or status == 'NO_FREE_REWARD' and latest['receipts']
                        or status == 'PARTIAL' and (not limited or not latest['receipts'])
                        or status == 'UNAVAILABLE' and (not limited or latest['receipts'])):
                    return result('UNKNOWN', 'shop_terminal_scope_unverified')
                return result('BLOCKED', resource_refusal) if resource_refusal else result(
                    status, latest['reason'] or 'shop_initial_list_complete')
            if status == 'BLOCKED' or not submitted and status == 'UNAVAILABLE':
                if latest['cleanup_pending'] or latest['busy']:
                    return result('UNKNOWN', latest['reason'] or 'shop_cleanup_unverified')
                return result(status, latest['reason'] or latest['gate'] or 'shop_unavailable')
            if latest['gold_cost'] != 0:
                return result('UNKNOWN', 'shop_unexpected_gold_cost')
            if not action:
                if not submitted and latest['gate'] != 'PASS' and self.clock() >= observation_deadline:
                    return result('UNAVAILABLE', latest['gate'])
                previous = latest if submitted else None
                self.wait()
                continue
            if not submitted:
                if status != 'IDLE' or action != 'open':
                    return result('BLOCKED', 'shop_start_offer_invalid')
                action = DURATION_ACTIONS[duration_seconds]
            elif (action == 'open' or action.startswith('produce_') and action != DURATION_ACTIONS[duration_seconds]
                  or duration_seconds == 0 and action in ('open_product', 'produce_short', 'produce_medium', 'produce_long')):
                return result('UNKNOWN', 'shop_duration_action_invalid')
            if resource_refusal and latest['phase'] == 'product_ready' and action.startswith('produce_'):
                # close_product is an existing normal command and advances the
                # bounded candidate list without producing or spending mana.
                action = 'close_product'
            if action not in ACTIONS or (command_count >= 2 + latest['candidate_count'] * 6 and submitted
                                         and not failed_reason):
                return fail('shop_command_bound_exceeded')
            if self.stopped() or self.paused():
                continue
            before = latest
            latest_current = False
            try:
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=TASK_ID, character_id=character_id,
                    operation_id=operation, expected_revision=before['revision'])
            except ResourcePolicyDenied as exc:
                resource_refusal = str(exc)
                latest_current = True
                if not submitted:
                    return result('BLOCKED', resource_refusal)
                if before['phase'] != 'product_ready' or not action.startswith('produce_'):
                    return result('BLOCKED', resource_refusal + '; shop_cancel_capability_unavailable')
                continue
            except CampaignAdmissionClosed:
                if self.stopped() or self.paused():
                    continue
                return fail('shop_admission_closed')
            except Exception as exc:
                submitted = True
                return result('UNKNOWN', f'shop_command_uncertain ({exc})')
            command_count += 1
            ack = wire.get('ack') if isinstance(wire, dict) else None
            sequence = wire.get('requested_seq') if isinstance(wire, dict) else None
            payload = ack.get('result') if isinstance(ack, dict) else None
            echo = payload.get('daily_script') if isinstance(payload, dict) else None
            if (not isinstance(ack, dict) or not _uint(sequence, 999999999) or sequence <= before['sequence']
                    or sequence in sent_sequences or ack.get('command_seq') != sequence
                    or ack.get('command_name') != 'daily-script'):
                submitted = True
                return result('UNKNOWN', 'shop_command_identity_unverified')
            # An exact failed ACK still identifies this one submission. It
            # never establishes that production was sent or permits a replay.
            sent_sequences.add(sequence)
            if ack.get('status') != 'COMPLETED':
                # An exact native not-invoked witness can establish that the
                # first command never opened a page. It is still not retried.
                clean_rejection = (not submitted and _snapshot(echo) and echo['operation_id'] == operation
                    and echo['role_id'] == character_id and echo['cycle'] == expected_cycle
                    and echo['sequence'] == sequence and echo['gui_invocation_attempted'] is False
                    and echo.get('gui_not_invoked_sequence') == sequence and echo['mutation_attempted'] is False
                    and echo['cleanup_pending'] is False and echo['busy'] is False)
                if clean_rejection:
                    latest = echo
                    latest_current = True
                    return result('BLOCKED', ack.get('reason') or 'shop_not_invoked')
                submitted = True
                failed_reason = failed_reason or ack.get('reason') or 'shop_command_unconfirmed'
                if (_snapshot(echo) and echo['operation_id'] == operation
                        and echo['role_id'] == character_id and echo['cycle'] == expected_cycle
                        and echo['duration_seconds'] == duration_seconds and echo['sequence'] == sequence):
                    latest = echo
                    latest_current = True
                    cleanup_evidence.append({'phase': 'owned_close_failed_ack' if cleanup_attempted else 'failed_command_ack',
                                             'sequence': sequence, 'ack_status': ack.get('status'),
                                             'ack_reason': ack.get('reason'), 'snapshot': dict(echo)})
                previous = None if before.get('phase') == 'opening' else latest
                cleanup_observation_pending()
                self.wait()
                continue
            submitted = True
            if (not _snapshot(echo) or echo['operation_id'] != operation or echo['role_id'] != character_id
                    or echo['cycle'] != expected_cycle or echo['duration_seconds'] != duration_seconds
                    or echo['sequence'] != sequence or echo['revision'] <= before['revision']
                    or echo['gui_invocation_attempted'] is not True):
                return result('UNKNOWN', 'shop_command_binding_unverified')
            latest = echo
            latest_current = True
            if failed_reason:
                cleanup_evidence.append({'phase': 'owned_close_ack', 'sequence': sequence,
                                         'snapshot': dict(echo)})
            previous = None if before.get('phase') == 'opening' else echo
            deadline = self.clock() + self.timeout
            self.wait()
