"""Run daily activity 7 through the native first-harvest and return flow."""
from __future__ import annotations

from dataclasses import dataclass, replace
import time
import uuid

from .objects import CampaignAdmissionClosed


_TASK_ID = 7
_PAGE_VERIFIED = 'mingshi_page_harvest_verified'
_NO_REPLY_PROCESSED = 'mingshi_harvest_submitted_no_reply'
_WITNESSES = ('mutation_attempted', 'stock_callback_returned', 'account_progress_observed', 'panel_closed')
_STATES = {'IDLE', 'QUEUED', 'OPENING', 'OPEN', 'PENDING', 'SETTLED', 'CLOSING', 'SUCCEEDED', 'REJECTED', 'UNKNOWN'}
_ACK_STATES = {
    'open': {'OPENING', 'OPEN'},
    'harvest': {'PENDING', 'SETTLED', 'SUCCEEDED'},
    'close': {'CLOSING', 'SUCCEEDED'},
    'cleanup': {'CLOSING', 'REJECTED'},
}
_NEXT_STATES = {
    'OPENING': {'OPEN', 'REJECTED'},
    'OPEN': {'REJECTED'},
    'PENDING': {'SETTLED', 'SUCCEEDED', 'UNKNOWN'},
    'SETTLED': {'SUCCEEDED'},
    'CLOSING': {'SUCCEEDED', 'REJECTED'},
}
_RETRYABLE_PREINVOKE = {
    'mingshi_admission', 'mingshi_client_busy', 'mingshi_gui_baseline_changed',
    'mingshi_lease_changed_before_call', 'mingshi_context_or_network_not_idle',
    'daily_mingshi_observation_or_business_pending',
}
_MAX_PREINVOKE_RETRIES = 1


@dataclass(frozen=True)
class DailyMingshiResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    stock_callback_returned: bool = False
    account_progress_observed: bool = False
    cleanup: str = 'NOT_NEEDED'
    mutation_attempted: bool = False
    processed_without_reply: bool = False
    page_confirmed: bool = False


def _page_confirmed(operation):
    return operation.get('mutation_attempted') is True and operation.get('reason') == _PAGE_VERIFIED


def _processed_without_reply(operation):
    return operation.get('mutation_attempted') is True and operation.get('reason') == _NO_REPLY_PROCESSED


def _snapshot_valid(snapshot):
    if (not isinstance(snapshot, dict) or type(snapshot.get('schema')) is not int or snapshot['schema'] != 1
            or type(snapshot.get('revision')) is not int or snapshot['revision'] <= 0):
        return False
    view, op = snapshot.get('view'), snapshot.get('operation')
    if not isinstance(view, dict) or not isinstance(op, dict):
        return False
    if (any(type(view.get(key)) is not int for key in ('task_id', 'cycle', 'current', 'required'))
            or view['task_id'] != _TASK_ID or not 0 <= view['cycle'] <= 0xffffffff
            or not 0 <= view['current'] <= view['required'] <= 1
            or not isinstance(view.get('role_id'), str) or not isinstance(view.get('reason'), str)
            or view.get('next_action') not in ('', 'open', 'harvest', 'close', 'cleanup') or 'gold_cost' not in view):
        return False
    if (not isinstance(op.get('status'), str) or op['status'] not in _STATES
            or not isinstance(op.get('reason'), str) or not isinstance(op.get('operation_id'), str)
            or type(op.get('sequence')) is not int or op['sequence'] < 0
            or any(type(op.get(key)) is not bool for key in _WITNESSES)):
        return False
    if op['status'] == 'IDLE':
        return not (op['operation_id'] or op['sequence'] or any(op[key] for key in _WITNESSES))
    return (len(op['operation_id']) == 32 and all(ch in '0123456789abcdef' for ch in op['operation_id'])
            and op['sequence'] > 0
            and (not op['panel_closed'] or op['status'] == 'SUCCEEDED' and
                 (op['account_progress_observed'] or _page_confirmed(op) or _processed_without_reply(op))
                 or op['status'] == 'REJECTED' and not op['mutation_attempted'])
            and all(not op[key] or op['mutation_attempted'] for key in ('stock_callback_returned', 'account_progress_observed')))


def _identity(snapshot, role, cycle):
    view = snapshot['view']
    return view['role_id'] == role and view['cycle'] > 0 and view['cycle'] == cycle and view['required'] == 1


def _consistent(snapshot, action, operation_id, sequence):
    op, view = snapshot['operation'], snapshot['view']
    if op['operation_id'] != operation_id or op['sequence'] != sequence:
        return False
    if action == 'cleanup':
        return not any(op[key] for key in _WITNESSES[:3])
    if action == 'open':
        return not op['mutation_attempted'] and view['current'] == 0
    if op['panel_closed'] and action != 'close':
        return False
    if op['status'] in ('PENDING', 'SETTLED', 'CLOSING', 'SUCCEEDED') and not op['mutation_attempted']:
        return False
    if (op['status'] in ('SETTLED', 'CLOSING', 'SUCCEEDED') and not op['account_progress_observed']
            and not _page_confirmed(op) and not _processed_without_reply(op)):
        return False
    return not op['account_progress_observed'] or view['current'] == view['required'] == 1


def _advances(previous, snapshot):
    if previous is None:
        return True
    old_revision, revision = previous['revision'], snapshot['revision']
    return revision > old_revision or revision == old_revision and snapshot == previous


def _preserves(previous, current):
    return all(not previous[key] or current[key] for key in _WITNESSES)


def _uninvoked_rejection(wire, before, receipt, *, bound, action, sequence):
    """Require correlated native refusal, never infer it from a missing reply."""
    ack, state = wire.get('ack'), wire.get('state')
    if (not isinstance(ack, dict) or ack.get('status') != 'REJECTED'
            or ack.get('command_name') != 'daily-mingshi-command'
            or not isinstance(state, dict) or state.get('lifecycle') != 'ready'
            or state.get('process_restart_required') is not False
            or state.get('last_command_seq') != sequence or not _snapshot_valid(receipt)
            or not _snapshot_valid(state.get('daily_mingshi'))):
        return False
    current = state['daily_mingshi']
    if (any(receipt['operation'][key] or current['operation'][key] for key in _WITNESSES[:3])
            or not _advances(receipt, current)):
        return False
    for snapshot in (receipt, current):
        if any(snapshot['view'][key] != before['view'][key] for key in
               ('task_id', 'role_id', 'cycle', 'current', 'required')):
            return False
    if bound:
        if (receipt['operation']['status'] != 'REJECTED'
                or current['operation'] != receipt['operation']):
            return False
        # Harvest latches mutation_attempted before calling the stock sender.
        # Open/return are UI effects, so they also need the GUI not-invoked proof.
        if action == 'harvest':
            return True
    elif any(snapshot['operation'] != before['operation'] for snapshot in (receipt, current)):
        return False
    result = ack.get('result')
    gui = result.get('campaign_gui') if isinstance(result, dict) else None
    if (not isinstance(gui, dict) or gui.get('command_seq') != sequence
            or gui.get('invocation_attempted') is not False):
        return False
    state_gui = state.get('campaign_gui')
    if gui.get('phase') == 'not_invoked':
        return state_gui == gui
    # A Queue refusal keeps the preceding operation and never reaches GUI.
    return (not bound and gui.get('phase') == 'none' and isinstance(state_gui, dict)
            and type(state_gui.get('command_seq')) is int and state_gui['command_seq'] < sequence
            and state_gui.get('phase') in ('none', 'completed', 'rejected', 'not_invoked'))


class DailyMingshiExecutor:
    """Consume the native offers while the caller owns the bridge admission."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=22.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait if wait is not None else lambda: time.sleep(.1)
        self.timeout, self.clock = timeout, clock
        self._prior = None

    def restore_open_panel(self, *, character_id: str, expected_cycle: int | None = None) -> DailyMingshiResult:
        return self.execute(_TASK_ID, character_id=character_id, expected_cycle=expected_cycle, cleanup_only=True)

    def execute(self, task_id: int, *, character_id: str, expected_cycle: int | None = None,
                cleanup_only=False) -> DailyMingshiResult:
        if type(task_id) is not int or task_id != _TASK_ID:
            raise ValueError('daily_mingshi_task_unsupported')
        if self._prior is not None:
            return replace(self._prior, status='UNKNOWN' if self._prior.cleanup == 'UNVERIFIED' else 'BLOCKED',
                           reason='mingshi_previous_attempt')
        if (not isinstance(character_id, str) or not character_id or character_id == '0'
                or expected_cycle is not None and (type(expected_cycle) is not int or expected_cycle <= 0)):
            return DailyMingshiResult('BLOCKED', 'mingshi_identity_invalid')

        operation_id = uuid.uuid4().hex
        stage = ''
        sequence = 0
        cycle = expected_cycle
        before = required = None
        previous = None
        evidence = {}
        submitted = False
        retry = None
        retries = 0
        abandon_reason = ''
        deadline = self.clock() + self.timeout

        def finish(status, reason, *, after=None, cleanup=None):
            progress = evidence.get('account_progress_observed', False)
            result = DailyMingshiResult(status, reason, before, required if progress else after, required,
                evidence.get('stock_callback_returned', False), progress,
                cleanup or ('UNVERIFIED' if submitted or status == 'UNKNOWN' else 'NOT_NEEDED'),
                evidence.get('mutation_attempted', False),
                status == 'PROCESSED' and _processed_without_reply(evidence),
                _page_confirmed(evidence) or status == 'ALREADY_COMPLETE' and after == required)
            if submitted or result.cleanup == 'UNVERIFIED':
                self._prior = result
            return result

        def fail(reason):
            return finish('UNKNOWN' if submitted else 'BLOCKED', reason)

        while True:
            if self.stopped():
                return finish('STOPPED', 'user_stop')
            if not stage and self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                continue

            try:
                observed = self.objects.observe_daily_mingshi(self.token)
            except Exception as exc:
                return fail(f'mingshi_observation_failed ({exc})')
            if not isinstance(observed, dict):
                return fail('mingshi_observation_invalid')
            if observed.get('enabled') is False:
                return fail('mingshi_disabled_during_operation') if stage else finish('UNAVAILABLE', 'mingshi_disabled')
            snapshot = observed.get('mingshi')
            if observed.get('enabled') is not True or not _snapshot_valid(snapshot):
                return fail('mingshi_observation_invalid')

            revision, view, op = snapshot['revision'], snapshot['view'], snapshot['operation']
            status, offer = op['status'], view['next_action']
            reason = op['reason'] or view['reason']
            if cycle is None:
                cycle = view['cycle']
            if not _identity(snapshot, character_id, cycle):
                return fail(view['reason'] or 'mingshi_observation_identity_changed')
            if not _advances(previous, snapshot):
                return fail('mingshi_observation_revision_changed')
            if stage:
                prior = previous['operation']
                if (not _consistent(snapshot, stage, operation_id, sequence)
                        or status != prior['status'] and status not in _NEXT_STATES.get(prior['status'], ())
                        or not _preserves(prior, op)):
                    return fail('mingshi_operation_binding_or_state_changed')
                evidence = op
            previous = snapshot

            if retry is not None:
                rejected_revision, rejection_reason = retry
                # A rejection publication itself is not a fresh admission.
                # Wait for a later native frame; never resend on the same one.
                if revision <= rejected_revision:
                    if self.clock() >= deadline:
                        if not submitted:
                            return finish('BLOCKED', rejection_reason, cleanup='NOT_NEEDED')
                        abandon_reason = rejection_reason
                        retry = None
                        deadline = self.clock() + self.timeout
                    else:
                        self.wait()
                        continue
                else:
                    retry = None
                if not submitted and not abandon_reason:
                    operation_id = uuid.uuid4().hex

            if self.stopped():
                return finish('STOPPED', 'user_stop')
            if status == 'UNKNOWN':
                return finish('UNKNOWN', reason or 'mingshi_native_operation_unknown')

            if not stage:
                if status not in ('IDLE', 'REJECTED', 'SUCCEEDED') or (
                        op['mutation_attempted'] and not op['panel_closed']):
                    return finish('BLOCKED', 'mingshi_prior_operation_unclosed', cleanup='UNVERIFIED')
                before, required = view['current'], view['required']
                if offer == 'cleanup' and op['operation_id']:
                    operation_id = op['operation_id']
                if cleanup_only:
                    if offer == 'cleanup':
                        action = 'cleanup'
                    elif view['reason'].startswith('mingshi_existing_page_return_unavailable:'):
                        if self.clock() >= deadline:
                            return finish('BLOCKED', view['reason'], cleanup='UNVERIFIED')
                        self.wait()
                        continue
                    else:
                        return finish('NOT_NEEDED', 'mingshi_page_not_open')
                elif offer == 'cleanup':
                    action = 'cleanup'
                elif before == required and not offer and (
                        op['panel_closed'] or view['reason'] in ('mingshi_first_harvest_used', 'mingshi_unobserved')):
                    return finish('ALREADY_COMPLETE', 'mingshi_activity_already_complete', after=before)
                elif before != 0 or offer != 'open' or view['reason'] or view['gold_cost'] is not None:
                    return finish('UNAVAILABLE', view['reason'] or 'mingshi_open_unavailable', after=before)
                else:
                    action = 'open'
            elif status == 'REJECTED':
                if op['panel_closed'] and not op['mutation_attempted']:
                    return finish('CLEANED' if cleanup_only else 'BLOCKED', abandon_reason or reason or 'mingshi_operation_rejected',
                                  after=view['current'], cleanup='VERIFIED_CLOSED')
                if offer == 'cleanup' and not op['mutation_attempted']:
                    action = 'cleanup'
                else:
                    return finish('BLOCKED', reason or 'mingshi_operation_rejected')
            elif abandon_reason and status == 'OPEN' and offer == 'harvest' and not op['mutation_attempted']:
                # Native also admits an explicit return of this exact OPEN
                # owner after a proven uninvoked Queue refusal. This is UI
                # cleanup, never another harvest attempt.
                action = 'cleanup'
            elif status == 'SUCCEEDED':
                if stage == 'close' and op['panel_closed']:
                    if _processed_without_reply(op):
                        return finish('PROCESSED', _NO_REPLY_PROCESSED, after=view['current'], cleanup='VERIFIED_CLOSED')
                    return finish('COMPLETED', reason or 'mingshi_progress_and_close_verified',
                                  after=view['current'], cleanup='VERIFIED_CLOSED')
                return finish('BLOCKED', reason or 'mingshi_close_unconfirmed')
            elif stage == 'open' and status == 'OPEN' and offer == 'harvest':
                if view['reason'] or type(view['gold_cost']) is not int or view['gold_cost'] != 0:
                    return fail('mingshi_zero_gold_quote_required')
                action = 'harvest'
            elif stage == 'harvest' and status == 'SETTLED' and offer == 'close':
                if view['reason'] or view['gold_cost'] is not None:
                    return fail('mingshi_close_offer_invalid')
                action = 'close'
            else:
                if offer:
                    return fail('mingshi_unexpected_action_offer')
                if self.clock() >= deadline:
                    return fail(view['reason'] or 'mingshi_readback_timeout')
                self.wait()
                continue

            if self.clock() >= deadline:
                return fail('mingshi_action_wait_timeout')
            if self.stopped():
                return finish('STOPPED', 'user_stop')
            if self.paused():
                self.wait()
                continue
            try:
                was_submitted = submitted
                wire = self.objects.submit_daily_mingshi(self.token, action, expected_revision=revision,
                    character_id=character_id, task_id=task_id, operation_id=operation_id)
            except CampaignAdmissionClosed:
                # ObjectController raises this before invoking the bridge.
                if self.stopped():
                    return finish('STOPPED', 'user_stop')
                if self.paused():
                    self.wait()
                    continue
                return fail('mingshi_command_admission_closed')
            except Exception as exc:
                submitted = True
                return fail(f'mingshi_command_outcome_unknown ({exc})')
            submitted = True

            ack = wire.get('ack') if isinstance(wire, dict) else None
            sent_seq = wire.get('requested_seq') if isinstance(wire, dict) else None
            if (not isinstance(ack, dict) or type(sent_seq) is not int or sent_seq <= op['sequence']
                    or type(ack.get('command_seq')) is not int or ack['command_seq'] != sent_seq):
                return fail('mingshi_command_binding_invalid')
            details = ack.get('result')
            receipt = details.get('daily_mingshi') if isinstance(details, dict) else None
            bound = (_snapshot_valid(receipt) and receipt['revision'] > revision
                     and _identity(receipt, character_id, cycle)
                     and _consistent(receipt, action, operation_id, sent_seq)
                     and (action == 'open' or _preserves(op, receipt['operation'])))
            if bound and receipt['operation']['status'] in (_ACK_STATES[action] | {'UNKNOWN', 'REJECTED'}):
                evidence = receipt['operation']
            uninvoked = _uninvoked_rejection(wire, snapshot, receipt, bound=bound,
                                            action=action, sequence=sent_seq)
            if uninvoked:
                submitted = was_submitted
                why = ack.get('reason') or receipt['operation']['reason'] or 'mingshi_command_rejected'
                if action == 'cleanup':
                    return finish('BLOCKED', why, after=receipt['view']['current'], cleanup='UNVERIFIED')
                # An owned, unmutated page is returned through the normal
                # cleanup offer even when the failed read is permanent.
                if bound and action != 'open':
                    stage, sequence, previous = action, sent_seq, receipt
                    deadline = self.clock() + self.timeout
                    continue
                if why in _RETRYABLE_PREINVOKE and retries < _MAX_PREINVOKE_RETRIES:
                    retries += 1
                    retry = (receipt['revision'], why)
                    previous = receipt
                    deadline = self.clock() + self.timeout
                    continue
                if action == 'harvest' and was_submitted:
                    abandon_reason = why
                    previous = receipt
                    deadline = self.clock() + self.timeout
                    continue
                return finish('BLOCKED', why, after=receipt['view']['current'],
                              cleanup='UNVERIFIED' if submitted else 'NOT_NEEDED')
            if (ack.get('status') == 'REJECTED' and bound
                    and receipt['operation']['status'] == 'REJECTED'
                    and not any(receipt['operation'][key] for key in _WITNESSES[:3])):
                # A failed UI invocation may have opened the stock page. Keep
                # its owner and request the offered return; do not relabel it
                # as a harvest with an unknown send outcome.
                if action == 'cleanup':
                    return finish('BLOCKED', ack.get('reason') or 'mingshi_cleanup_rejected',
                                  after=receipt['view']['current'], cleanup='UNVERIFIED')
                stage, sequence, previous = action, sent_seq, receipt
                deadline = self.clock() + self.timeout
                continue
            if ack.get('status') != 'COMPLETED':
                why = ack.get('reason')
                return fail(why if isinstance(why, str) and why else 'mingshi_command_outcome_unknown')
            if not bound or receipt['operation']['status'] not in _ACK_STATES[action]:
                return fail('mingshi_command_binding_invalid')

            # An ACK only binds the action and may retain its witnesses.
            # A separate readback authorizes the next action or completion.
            stage, sequence, previous = action, sent_seq, receipt
            deadline = self.clock() + self.timeout

