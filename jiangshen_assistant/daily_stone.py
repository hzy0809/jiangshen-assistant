"""Run daily activity #16 through the stock silver-pick operation."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid

from .objects import CampaignAdmissionClosed
from .resource_policy import ResourcePolicyDenied


_TASK_ID = 16
_TERMINAL = {'COMPLETED', 'REJECTED', 'UNKNOWN'}
_STATUSES = _TERMINAL | {'IDLE', 'QUEUED', 'OPENING', 'OPEN', 'PENDING', 'SETTLED'}
_ACK_PHASES = {'open': {'OPENING', 'OPEN'}, 'dig': {'PENDING', 'SETTLED', 'COMPLETED'},
               'close': {'COMPLETED'}}
_TRANSITIONS = {
    'OPENING': {'OPENING', 'OPEN', 'REJECTED', 'UNKNOWN'},
    'OPEN': {'OPEN', 'REJECTED', 'UNKNOWN'},
    'PENDING': {'PENDING', 'SETTLED', *_TERMINAL},
    'SETTLED': {'SETTLED', 'COMPLETED', 'UNKNOWN'},
}
_WITNESSES = ('mutation_attempted', 'stock_callback_returned', 'account_progress_observed', 'panel_closed')


@dataclass(frozen=True)
class DailyStoneResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    stock_callback_returned: bool = False
    account_progress_observed: bool = False
    cleanup: str = 'NOT_NEEDED'


def _valid_snapshot(stone):
    if (not isinstance(stone, dict) or type(stone.get('schema')) is not int or stone['schema'] != 1
            or type(stone.get('revision')) is not int or stone['revision'] < 1):
        return False
    view, op = stone.get('view'), stone.get('operation')
    if not isinstance(view, dict) or not isinstance(op, dict):
        return False
    if (any(type(view.get(key)) is not int for key in ('task_id', 'cycle', 'current', 'required', 'silver_picks'))
            or view['task_id'] != _TASK_ID or view['cycle'] < 0 or view['silver_picks'] < 0
            or not 0 <= view['current'] <= view['required'] <= 1
            or not isinstance(view.get('role_id'), str) or not isinstance(view.get('reason'), str)
            or view.get('next_action') not in ('', 'open', 'dig', 'close')
            or not all(key in view for key in ('gold_cost', 'silver_pick_cost'))):
        return False
    if (not isinstance(op.get('status'), str) or op['status'] not in _STATUSES
            or not isinstance(op.get('reason'), str) or not isinstance(op.get('operation_id'), str)
            or type(op.get('sequence')) is not int or op['sequence'] < 0
            or any(type(op.get(key)) is not bool for key in _WITNESSES)):
        return False
    if op['status'] == 'IDLE' and (op['operation_id'] or op['sequence'] or any(op[key] for key in _WITNESSES)):
        return False
    if op['status'] != 'IDLE' and (not op['operation_id'] or not op['sequence']):
        return False
    return all(not op[later] or op[earlier] for earlier, later in zip(_WITNESSES, _WITNESSES[1:]))


def _identity(stone, character_id, cycle):
    view = stone['view']
    return view['role_id'] == character_id and view['cycle'] > 0 and view['cycle'] == cycle


def _matched(stone, operation_id, sequence):
    op = stone['operation']
    return op['operation_id'] == operation_id and op['sequence'] == sequence


def _pending_busy(stone, character_id):
    view, op = stone['view'], stone['operation']
    return (view['role_id'] == character_id and view['cycle'] == 0
            and view['reason'] == 'stone_client_busy' and view['next_action'] == ''
            and op['status'] == 'PENDING' and op['mutation_attempted'])


def _consistent_stage(stone, stage, original_silver):
    op, view = stone['operation'], stone['view']
    if stage == 'open' and op['mutation_attempted'] or stage != 'close' and op['panel_closed']:
        return False
    if op['status'] in ('PENDING', 'SETTLED', 'COMPLETED') and not op['mutation_attempted']:
        return False
    if op['status'] in ('SETTLED', 'COMPLETED') and not op['account_progress_observed']:
        return False
    return not op['account_progress_observed'] or (view['current'] == view['required'] == 1
        and view['silver_picks'] == original_silver - 1)


class DailyStoneExecutor:
    """Follow the native offers under the caller's exclusive bridge owner."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=22.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait if wait is not None else lambda: time.sleep(.1)
        self.timeout, self.clock = timeout, clock
        self._submitted = self._unresolved = False

    def execute(self, task_id: int, *, character_id: str, expected_cycle: int | None = None) -> DailyStoneResult:
        if type(task_id) is not int or task_id != _TASK_ID:
            raise ValueError('daily_stone_task_unsupported')
        if self._unresolved or self._submitted:
            return DailyStoneResult('UNKNOWN' if self._unresolved else 'BLOCKED',
                                    'stone_previous_attempt', cleanup='UNVERIFIED')

        operation_id = uuid.uuid4().hex
        stage = ''
        sequence = 0
        cycle = expected_cycle
        baseline = original_silver = required = None
        previous = witnessed = None
        deadline = self.clock() + self.timeout

        def result(status, reason, *, after=None, closed=False):
            if status == 'UNKNOWN':
                self._unresolved = True
            op = witnessed or {}
            progress = op.get('account_progress_observed', False)
            cleanup = 'VERIFIED_CLOSED' if closed else 'UNVERIFIED' if self._submitted else 'NOT_NEEDED'
            return DailyStoneResult(status, reason, baseline, required if progress else after, required,
                                    op.get('stock_callback_returned', False), progress, cleanup)

        def fail(reason):
            return result('UNKNOWN' if self._submitted else 'BLOCKED', reason)

        while True:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if not stage and self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                continue
            try:
                observed = self.objects.observe_daily_stone(self.token)
            except Exception as exc:
                return fail(f'stone_observation_failed ({exc})')
            if not isinstance(observed, dict):
                return fail('stone_observation_invalid')
            if observed.get('enabled') is False:
                return fail('stone_disabled_during_operation') if stage else result('UNAVAILABLE', 'stone_disabled')
            stone = observed.get('stone')
            if observed.get('enabled') is not True or not _valid_snapshot(stone):
                return fail('stone_observation_invalid')
            revision, view, op = stone['revision'], stone['view'], stone['operation']
            status, offer = op['status'], view['next_action']
            reason = op['reason'] or view['reason']
            if cycle is None:
                cycle = view['cycle']
            if (previous is not None and (revision < previous['revision']
                    or revision == previous['revision'] and stone != previous)):
                return fail('stone_observation_revision_changed')

            if stage:
                prior_op = previous['operation']
                prior_status = prior_op['status']
                if (not _matched(stone, operation_id, sequence)
                        or status not in _TRANSITIONS.get(prior_status, {prior_status})
                        or any(prior_op[key] and not op[key] for key in _WITNESSES)
                        or not _consistent_stage(stone, stage, original_silver)):
                    return fail('stone_operation_binding_or_state_changed')
                if stage == 'dig' and _pending_busy(stone, character_id):
                    if self.clock() >= deadline:
                        return fail('stone_readback_timeout')
                    previous = stone
                    self.wait()
                    continue

            if not _identity(stone, character_id, cycle) or view['required'] != 1:
                return fail(view['reason'] or 'stone_observation_identity_changed')
            previous = stone
            if stage:
                witnessed = op
            if status == 'UNKNOWN':
                return result('UNKNOWN', reason or 'stone_native_operation_unknown')

            if not stage:
                if status not in ('IDLE', 'REJECTED', 'COMPLETED') or (
                        op['mutation_attempted'] and not op['panel_closed']):
                    return DailyStoneResult('BLOCKED', 'stone_prior_operation_unclosed', cleanup='UNVERIFIED')
                baseline, required, original_silver = view['current'], view['required'], view['silver_picks']
                if baseline == required and offer == '' and view['reason'] == 'stone_activity_complete':
                    return result('ALREADY_COMPLETE', view['reason'], after=baseline)
                if baseline != 0 or offer != 'open' or view['reason'] or original_silver < 1:
                    return result('UNAVAILABLE', view['reason'] or 'stone_open_unavailable', after=baseline)
                action = 'open'
            elif status == 'REJECTED':
                return result('REJECTED', reason or 'stone_operation_rejected')
            elif status == 'COMPLETED':
                if stage == 'close' and op['panel_closed']:
                    return result('COMPLETED', reason or 'stone_progress_and_close_verified', closed=True)
                return result('BLOCKED', reason or 'stone_close_unconfirmed')
            elif stage == 'open' and status == 'OPEN' and offer == 'dig':
                if (view['reason'] or view['current'] != baseline or view['silver_picks'] != original_silver
                        or type(view['gold_cost']) is not int or view['gold_cost'] != 0
                        or type(view['silver_pick_cost']) is not int or view['silver_pick_cost'] != 1):
                    return fail('stone_silver_only_quote_invalid')
                action = 'dig'
            elif stage == 'dig' and status == 'SETTLED' and offer == 'close' and not view['reason']:
                action = 'close'
            else:
                if offer:
                    return fail('stone_unexpected_action_offer')
                if self.clock() >= deadline:
                    return fail(view['reason'] or 'stone_readback_timeout')
                self.wait()
                continue

            if self.clock() >= deadline:
                return fail('stone_action_wait_timeout')
            if self.stopped() or self.paused():
                self.wait()
                continue
            try:
                wire = self.objects.submit_daily_stone(self.token, action, expected_revision=revision,
                    character_id=character_id, task_id=task_id, operation_id=operation_id)
            except ResourcePolicyDenied as exc:
                # The fixed native close requires SETTLED; there is no cancel
                # for an opened, undug page. Keep that cleanup gap explicit.
                return result('BLOCKED', str(exc) +
                    ('; stone_cancel_capability_unavailable' if self._submitted else ''))
            except CampaignAdmissionClosed:
                if self.stopped() or self.paused():
                    continue
                return fail('stone_command_admission_closed')
            except Exception as exc:
                self._submitted = True
                return fail(f'stone_command_outcome_unknown ({exc})')
            self._submitted = True

            ack = wire.get('ack') if isinstance(wire, dict) else None
            if not isinstance(ack, dict):
                return fail('stone_command_reply_invalid')
            sent_seq = wire.get('requested_seq')
            if (type(sent_seq) is not int or sent_seq <= op['sequence']
                    or type(ack.get('command_seq')) is not int or ack['command_seq'] != sent_seq):
                return fail('stone_command_binding_invalid')
            ack_status = ack.get('status')
            if ack_status != 'COMPLETED':
                why = ack.get('reason') or 'stone_command_outcome_unknown'
                if ack_status == 'REJECTED' and action in ('open', 'close'):
                    return result('BLOCKED', why)
                return fail(why)

            details = ack.get('result')
            receipt = details.get('daily_stone') if isinstance(details, dict) else None
            if (not _valid_snapshot(receipt) or receipt['revision'] <= revision
                    or not (_identity(receipt, character_id, cycle)
                            or action == 'dig' and _pending_busy(receipt, character_id))
                    or not _matched(receipt, operation_id, sent_seq)
                    or receipt['operation']['status'] not in _ACK_PHASES[action]
                    or not _consistent_stage(receipt, action, original_silver)):
                return fail('stone_command_binding_invalid')

            # The GUI acknowledgement binds the invocation. Only a later
            # observation can authorize the next action or confirm completion.
            stage, sequence, previous = action, sent_seq, receipt
            deadline = self.clock() + self.timeout
