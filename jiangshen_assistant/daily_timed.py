"""Run one timed daily activity under the caller's bridge owner."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid

from .bridge_client import _timed_valid
from .objects import CampaignAdmissionClosed


_OPCODES = {12: (312, 1829), 13: (321,)}
_UNAVAILABLE = frozenset({
    'daily_timed_counter_unavailable', 'quiz_closed', 'quiz_window_unknown',
    'quiz_state_unknown', 'quiz_level_unknown', 'quiz_finished', 'quiz_already_in_progress',
    'brawl_closed', 'brawl_phase_unknown', 'brawl_already_joined', 'brawl_progress_already_present',
})
_TRANSITIONS = {
    'OPENING': {'OPENING', 'READY', 'REJECTED', 'UNKNOWN'},
    'READY': {'READY', 'REJECTED', 'UNKNOWN'},
    'PENDING': {'PENDING', 'SUCCEEDED', 'UNKNOWN'},
    'CLOSING': {'CLOSING', 'SUCCEEDED', 'UNKNOWN'},
}


@dataclass(frozen=True)
class DailyTimedResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    opcode: int = 0
    request_attribution: str | None = None
    cleanup: str = 'NOT_NEEDED'


def _operation_matches(op, task_id, operation_id, stage, sequence):
    return (op['operation_id'] == operation_id and op['task_id'] == task_id
            and op['stage'] == stage and op['sequence'] == sequence)


def _count_valid(row):
    return (row['current'] is not None and row['required'] is not None
            and 0 <= row['current'] <= row['required'] and row['required'] > 0)


def _operation_valid(op, stage, task_id, baseline):
    if not op['invoked']:
        return False
    if stage == 'open':
        return (op['status'] in ('OPENING', 'READY', 'REJECTED', 'UNKNOWN')
                and op['opcode'] == 0 and op['request_attribution'] is None
                and op['counter_after'] is None)
    if stage == 'close':
        return (task_id == 12 and op['status'] in ('CLOSING', 'SUCCEEDED', 'UNKNOWN')
                and op['opcode'] == 0 and op['request_attribution'] is None
                and op['counter_before'] is None and op['counter_after'] is None)
    attribution = 'UNKNOWN' if op['opcode'] in (321, 1829) else None
    return (op['status'] in ('PENDING', 'SUCCEEDED', 'UNKNOWN')
            and op['opcode'] in _OPCODES[task_id] and op['request_attribution'] == attribution
            and op['counter_before'] == baseline
            and (op['counter_after'] is not None) == (op['status'] == 'SUCCEEDED'))


class DailyTimedExecutor:
    """Check native account progress and verify the stock Quiz view cleanup."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=22.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait if wait is not None else lambda: time.sleep(.1)
        self.timeout, self.clock = timeout, clock
        self._unresolved = False
        self._opened = set()

    def execute(self, task_id: int, *, character_id: str) -> DailyTimedResult:
        if type(task_id) is not int or task_id not in _OPCODES:
            raise ValueError('daily_timed_task_unsupported')
        if self._unresolved:
            return DailyTimedResult('UNKNOWN', 'daily_timed_previous_operation_unresolved', cleanup='UNVERIFIED')
        if task_id in self._opened:
            return DailyTimedResult('BLOCKED', 'daily_timed_task_already_attempted', cleanup='UNVERIFIED')

        operation_id = uuid.uuid4().hex
        stage = ''
        previous = participation = None
        sequence = 0
        baseline = required = None
        deadline = self.clock() + self.timeout

        def result(status, reason, *, current=None, cleanup=None):
            if status == 'UNKNOWN':
                self._unresolved = True
            op = participation or (previous['operation'] if stage == 'participate' else {})
            if participation is not None:
                current = participation['counter_after']
            return DailyTimedResult(status, reason, baseline, current, required,
                op.get('opcode', 0), op.get('request_attribution'),
                cleanup or ('UNVERIFIED' if stage or status == 'UNKNOWN' else 'NOT_NEEDED'))

        def fail(reason):
            return result('UNKNOWN' if stage else 'BLOCKED', reason)

        while True:
            if not stage:
                if self.stopped():
                    return result('STOPPED', 'user_stop')
                if self.paused():
                    self.wait()
                    continue
            try:
                observed = self.objects.observe_daily_timed(self.token)
            except Exception as exc:
                return fail(f'daily_timed_observation_failed ({exc})')
            if not isinstance(observed, dict):
                return fail('daily_timed_observation_invalid')
            if observed.get('enabled') is False:
                if stage:
                    return fail('daily_timed_disabled_during_operation')
                return result('UNAVAILABLE', 'daily_timed_disabled')
            timed = observed.get('timed')
            if observed.get('enabled') is not True or not _timed_valid(timed):
                return fail('daily_timed_observation_invalid')
            if (timed['character_id'] != character_id or timed['revision'] < 1 or previous is not None
                    and (timed['revision'] < previous['revision']
                         or timed['revision'] == previous['revision'] and timed != previous)):
                return fail('daily_timed_identity_or_revision_changed')

            op = timed['operation']
            row = timed['tasks'][str(task_id)]
            status = op['status']
            reason = op['reason'] or row['reason']
            current = row['current']
            if stage:
                prior_op = previous['operation']
                prior_status = prior_op['status']
                if (not _operation_matches(op, task_id, operation_id, stage, sequence)
                        or not _operation_valid(op, stage, task_id, baseline)
                        or op['opcode'] != prior_op['opcode']
                        or prior_status in ('SUCCEEDED', 'REJECTED') and op != prior_op
                        or status not in _TRANSITIONS.get(prior_status, {prior_status})):
                    return fail('daily_timed_operation_binding_or_state_changed')
            previous = timed

            if status == 'UNKNOWN':
                return result('UNKNOWN', reason or 'daily_timed_native_operation_unknown')
            if not stage:
                if status not in ('IDLE', 'SUCCEEDED', 'REJECTED'):
                    return result('BLOCKED', 'daily_timed_prior_operation_active', cleanup='UNVERIFIED')
                if self.stopped():
                    return result('STOPPED', 'user_stop')
                if not _count_valid(row):
                    return result('UNAVAILABLE', 'daily_timed_counter_unavailable')
                required = row['required']
                if current == required:
                    baseline = current
                    return result('ALREADY_COMPLETE', 'daily_timed_already_complete', current=current)
                if row['action'] != 'open':
                    return result('UNAVAILABLE' if row['reason'] in _UNAVAILABLE else 'BLOCKED',
                                  row['reason'] or 'daily_timed_open_unavailable')
                action = 'open'
            elif stage == 'close' and status == 'SUCCEEDED':
                completion = 'COMPLETED' if participation['counter_after'] == required else 'PARTIAL'
                return result(completion, participation['reason'] or 'daily_timed_account_progress_observed',
                              cleanup='VERIFIED_CLOSED')
            elif status == 'SUCCEEDED':
                if (not _count_valid(row) or row['required'] != required
                        or current != op['counter_after'] or not baseline < current):
                    return fail('daily_timed_counter_confirmation_invalid')
                participation = op
                if task_id == 13:
                    completion = 'COMPLETED' if current == required else 'PARTIAL'
                    return result(completion, reason or 'daily_timed_account_progress_observed',
                                  cleanup='BRAWL_MAY_REMAIN_OPEN')
                if self.stopped():
                    return result('STOPPED', 'user_stop')
                if row['action'] != 'close':
                    if self.clock() >= deadline:
                        return result('BLOCKED', 'daily_timed_quiz_close_unavailable')
                    self.wait()
                    continue
                action = 'close'
            elif status == 'REJECTED':
                if self.stopped():
                    return result('STOPPED', 'user_stop')
                if reason == 'daily_timed_already_complete' and _count_valid(row) and current == row['required']:
                    baseline = required = current
                    return result('ALREADY_COMPLETE', reason, current=current)
                return result('UNAVAILABLE' if reason in _UNAVAILABLE else 'BLOCKED',
                              reason or 'daily_timed_open_rejected')
            elif status == 'READY':
                if self.stopped():
                    return result('STOPPED', 'user_stop')
                if (row['action'] != 'participate' or not _count_valid(row)
                        or row['required'] != required or current == required):
                    return result('UNAVAILABLE' if row['reason'] in _UNAVAILABLE else 'BLOCKED',
                                  row['reason'] or 'daily_timed_participation_unavailable')
                action = 'participate'
            else:
                if self.clock() >= deadline:
                    return fail('daily_timed_readback_timeout')
                self.wait()
                continue

            if stage and self.clock() >= deadline:
                return result('BLOCKED', 'daily_timed_action_wait_timeout')
            if self.paused() or self.stopped():
                self.wait()
                continue

            try:
                wire = self.objects.submit_daily_timed(self.token, action, expected_revision=timed['revision'],
                    character_id=character_id, task_id=task_id, operation_id=operation_id)
            except CampaignAdmissionClosed:
                if self.stopped() or self.paused():
                    continue
                return result('BLOCKED', 'daily_timed_command_admission_closed')
            except Exception as exc:
                return result('UNKNOWN', f'daily_timed_command_outcome_unknown ({exc})')

            if not isinstance(wire, dict) or not isinstance(wire.get('ack'), (dict, type(None))):
                return result('UNKNOWN', 'daily_timed_command_reply_invalid')
            ack = wire.get('ack') or {}
            if ack.get('status') == 'REJECTED':
                if self.stopped():
                    return result('STOPPED', 'user_stop')
                why = ack.get('reason') or 'daily_timed_command_rejected'
                return result('UNAVAILABLE' if why in _UNAVAILABLE or why == 'daily_timed_disabled' else 'BLOCKED', why)
            if ack.get('status') != 'COMPLETED':
                return result('UNKNOWN', ack.get('reason') or wire.get('reason') or 'daily_timed_command_outcome_unknown')

            details = ack.get('result')
            receipt = details.get('daily_timed') if isinstance(details, dict) else None
            sent_seq = wire.get('requested_seq')
            if action == 'participate':
                baseline = current
            if (not _timed_valid(receipt) or receipt['character_id'] != character_id
                    or receipt['revision'] <= timed['revision']
                    or type(sent_seq) is not int or sent_seq <= op['sequence']
                    or not _operation_matches(receipt['operation'], task_id, operation_id, action, sent_seq)
                    or not _operation_valid(receipt['operation'], action, task_id, baseline)):
                return result('UNKNOWN', 'daily_timed_command_binding_invalid')

            # The acknowledgement binds this invocation. Only a subsequent
            # native observation can authorize the next action or completion.
            self._opened.add(task_id)
            stage, sequence, previous = action, sent_seq, receipt
            deadline = self.clock() + self.timeout
