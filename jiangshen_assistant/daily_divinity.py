"""Fight the stock divinity sequence through the daily script bridge."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid


@dataclass(frozen=True)
class DailyDivinityResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    stock_callback_returned: bool = False
    account_progress_observed: bool = False
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    clearance_complete: bool = False
    killed_mask_before: int | None = None
    killed_mask_after: int | None = None
    target_receipts: tuple[dict, ...] = ()


class DailyDivinityExecutor:
    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=3600.0, initial_timeout=10.0, cleanup_timeout=30.0,
                 clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.2))
        self.timeout, self.initial_timeout, self.clock = timeout, initial_timeout, clock
        self.cleanup_timeout = cleanup_timeout

    def observe_harvest_clearance(self, *, character_id: str, expected_cycle: int) -> DailyDivinityResult:
        """Read the live five-god prerequisite without opening or fighting."""
        if self.stopped():
            return DailyDivinityResult('STOPPED', 'user_stop')
        if (not isinstance(character_id, str) or not character_id or character_id == '0'
                or type(expected_cycle) is not int or expected_cycle <= 0):
            return DailyDivinityResult('UNAVAILABLE', 'divinity_clearance_identity_invalid')
        try:
            snapshot = self.objects.observe_daily_script(self.token, 8)
        except Exception:
            return DailyDivinityResult('UNAVAILABLE', 'divinity_clearance_observation_failed')
        if (not isinstance(snapshot, dict) or type(snapshot.get('schema')) is not int or snapshot['schema'] != 1
                or type(snapshot.get('revision')) is not int or snapshot['revision'] <= 0
                or not isinstance(snapshot.get('view'), dict) or not isinstance(snapshot.get('operation'), dict)):
            return DailyDivinityResult('UNAVAILABLE', 'divinity_clearance_state_unavailable')
        view, operation = snapshot['view'], snapshot['operation']
        if type(view.get('task_id')) is not int or view['task_id'] != 8:
            return DailyDivinityResult('UNAVAILABLE', 'divinity_clearance_state_unavailable')
        if view.get('role_id') != character_id:
            return DailyDivinityResult('UNKNOWN', 'divinity_character_changed')
        if type(view.get('refresh_cycle')) is not int or view['refresh_cycle'] != expected_cycle:
            return DailyDivinityResult('UNKNOWN', 'divinity_refresh_cycle_changed')
        mask = view.get('killed_mask')
        if (type(mask) is not int or not 0 <= mask <= 31
                or type(view.get('target_total')) is not int or view['target_total'] != 5
                or type(view.get('clearance_complete')) is not bool
                or view.get('harvest_clearance_interface') != 'stock-five-god-harvest-clearance-v1'
                or type(view.get('harvest_clearance_verified')) is not bool):
            return DailyDivinityResult('UNAVAILABLE', 'divinity_clearance_state_unavailable')
        if operation.get('status') == 'UNKNOWN':
            return DailyDivinityResult('UNKNOWN', 'divinity_clearance_operation_unresolved',
                                       killed_mask_before=mask, killed_mask_after=mask)
        if self.stopped():
            return DailyDivinityResult('STOPPED', 'user_stop')
        if mask != 31:
            return DailyDivinityResult('UNAVAILABLE', 'divinity_clear_required',
                                       killed_mask_before=mask, killed_mask_after=mask)
        if not view['clearance_complete'] or not view['harvest_clearance_verified']:
            return DailyDivinityResult('UNAVAILABLE', 'divinity_harvest_clearance_unverified',
                                       killed_mask_before=mask, killed_mask_after=mask)
        return DailyDivinityResult('ALREADY_COMPLETE', 'divinity_roster_cleared', clearance_complete=True,
                                   killed_mask_before=mask, killed_mask_after=mask)

    def execute(self, task_id=8, *, character_id: str, expected_current: int | None = None,
                expected_required: int | None = None, expected_cycle: int | None = None):
        if task_id != 8:
            raise ValueError('daily_divinity_task_unsupported')
        operation_id = uuid.uuid4().hex
        baseline = current = required = None
        killed_before = killed_after = None
        clearance_complete = False
        attempted_targets = set()
        target_receipts = {}
        submitted = returned = progressed = closed = engaged = False
        previous_round = ''
        started = self.clock()
        deadline = started + self.timeout
        initial_deadline = started + self.initial_timeout
        initial_reason = 'divinity_initial_observation_timeout'
        failure_reason = ''
        cleanup_deadline = None
        rejected_observations = 0

        def result(status, reason):
            return DailyDivinityResult(
                status, reason, baseline, current, required, returned, progressed,
                'VERIFIED_CLOSED' if closed else 'UNVERIFIED' if engaged else 'NOT_NEEDED',
                operation_id if submitted else '', clearance_complete, killed_before, killed_after,
                tuple(target_receipts.values()))

        while self.clock() < deadline:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                pause_started = self.clock()
                self.wait()
                paused_for = max(0.0, self.clock() - pause_started)
                deadline += paused_for
                initial_deadline += paused_for
                if cleanup_deadline is not None:
                    cleanup_deadline += paused_for
                continue
            if not engaged and self.clock() >= initial_deadline:
                return result('UNAVAILABLE', initial_reason)
            snapshot = self.objects.observe_daily_script(self.token, 8)
            view, operation = snapshot['view'], snapshot['operation']
            status, reason = operation['status'], operation.get('reason', '')
            engaged = engaged or (not operation.get('session_closed', False) and (
                operation.get('session_mutation_attempted', False) or operation.get('mutation_attempted', False)
                or status in ('OPENING', 'SELECTING', 'PENDING')))
            if view['role_id'] != character_id:
                return result('UNKNOWN' if engaged else 'BLOCKED', 'divinity_character_changed')
            if expected_cycle is None:
                expected_cycle = view['refresh_cycle']
            if view['refresh_cycle'] != expected_cycle:
                return result('UNKNOWN' if engaged else 'BLOCKED', 'divinity_refresh_cycle_changed')
            if (view.get('continuous_interface') != 'stock-owned-divinity-session-v1'
                    or type(operation.get('session_closed')) is not bool
                    or type(operation.get('target_closed')) is not bool):
                return result('UNKNOWN' if engaged else 'UNAVAILABLE', 'divinity_continuous_interface_unavailable')
            current, required = view['current'], view['required']
            if baseline is None:
                if ((expected_current is not None and current != expected_current)
                        or (expected_required is not None and required != expected_required)):
                    return result('UNKNOWN' if engaged else 'BLOCKED', 'divinity_initial_progress_changed')
                baseline = current
            owns_operation = submitted and operation.get('id') == operation_id
            if owns_operation:
                returned = returned or operation.get('stock_callback_returned', False)
                progressed = progressed or operation.get('account_progress_correlated', False)
            # A target's callbacks/rewards close on the owned Divinity page.
            # Only session_closed proves the final normal return to clean home.
            closed = operation['session_closed'] and owns_operation
            if operation.get('closed') != operation['session_closed']:
                return result('UNKNOWN', 'divinity_session_closure_mismatch')
            # The daily counter describes only task 8. Clearance is the stock
            # five-target killed roster, which can remain unfinished at 1/1.
            mask = view.get('killed_mask')
            if (type(mask) is not int or not 0 <= mask <= 31
                    or view.get('target_total') != 5
                    or type(view.get('clearance_complete')) is not bool):
                return result('UNKNOWN' if engaged else 'UNAVAILABLE', 'divinity_clearance_state_unavailable')
            if killed_before is None:
                killed_before = mask
            if killed_after is not None and mask & killed_after != killed_after:
                return result('UNKNOWN', 'divinity_roster_reset_or_regressed')
            killed_after = mask
            if operation['target_closed'] and submitted and operation.get('id') == operation_id:
                settled_target = operation.get('target')
                if (type(settled_target) is not int or settled_target not in range(5)
                        or type(operation.get('won')) is not bool
                        or not operation.get('stock_callback_returned')):
                    return result('UNKNOWN', 'divinity_target_receipt_unavailable')
                if settled_target not in target_receipts:
                    target_receipts[settled_target] = {
                        'target': settled_target, 'operation_id': operation.get('id', ''),
                        'won': operation['won'], 'report_id': operation.get('report_id', ''),
                        'killed_mask_before': operation.get('target_killed_mask_before'),
                        'killed_mask_after': mask,
                        'progress_before': operation.get('target_progress_before'), 'progress_after': current,
                        'stock_callback_returned': True,
                        'account_progress_correlated': operation.get('account_progress_correlated', False),
                        'target_closed': True,
                    }
            clearance_complete = view['clearance_complete'] and mask == 31
            if (clearance_complete and (closed or not engaged) and
                    view.get('gate') in ('', 'PASS', 'divinity_stock_home_required')):
                return result('COMPLETED' if submitted else 'ALREADY_COMPLETE', 'divinity_roster_cleared')
            action = view.get('next_action', '')
            if reason:
                initial_reason = reason
            if status == 'UNKNOWN':
                return result(status, reason)
            if status == 'BLOCKED' and engaged and not operation.get('mutation_attempted'):
                failure_reason = failure_reason or reason or 'divinity_open_failed'
                if cleanup_deadline is None:
                    cleanup_deadline = self.clock() + self.cleanup_timeout
            if operation.get('won') is False and operation.get('stock_callback_returned'):
                failure_reason = 'divinity_battle_lost'
                if cleanup_deadline is None:
                    cleanup_deadline = self.clock() + self.cleanup_timeout
            if failure_reason:
                if closed:
                    return result('UNAVAILABLE', failure_reason)
                if self.clock() >= cleanup_deadline:
                    return result('UNKNOWN', 'divinity_return_timeout')
                allowed_cleanup = ('', 'return', 'battle_close', 'reward_confirm', 'clear_confirm', 'failed_close')
                if action not in allowed_cleanup:
                    return result('UNKNOWN', 'divinity_return_unavailable')
            elif closed and submitted and operation.get('id') == operation_id:
                return result('UNAVAILABLE', view.get('gate') or 'divinity_no_eligible_target')
            if not engaged and self.clock() >= initial_deadline:
                return result('UNAVAILABLE', initial_reason)
            if action == 'open' and operation.get('id') and operation.get('id') != previous_round:
                previous_round = operation['id']
                operation_id = uuid.uuid4().hex
            if action:
                target = view.get('target', -1)
                if action == 'start':
                    if type(target) is not int or target not in range(5) or mask & (1 << target):
                        return result('UNKNOWN', 'divinity_target_not_available')
                    if target in attempted_targets:
                        return result('UNKNOWN', 'divinity_target_already_attempted')
                if action == 'select' and operation.get('stock_callback_returned'):
                    if not operation['target_closed'] or operation.get('won') is not True:
                        return result('UNKNOWN', 'divinity_previous_target_not_closed')
                    if operation.get('target') not in target_receipts:
                        return result('UNKNOWN', 'divinity_previous_target_receipt_missing')
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=8, character_id=character_id,
                    operation_id=operation_id, expected_revision=snapshot['revision'])
                submitted = True
                engaged = True
                ack = wire.get('ack') or {}
                if ack.get('status') != 'COMPLETED':
                    rejection = ack.get('reason') or 'divinity_command_rejected'
                    # A queue rejection happens before any stock call. Refresh
                    # transiently stale observations, but never retry an
                    # unknown or possibly invoked operation.
                    if (ack.get('status') == 'REJECTED' and
                            (rejection == 'divinity_cleanup_unavailable' or
                             rejection.startswith('divinity_command_identity ')) and
                            rejected_observations < 20):
                        rejected_observations += 1
                        self.wait()
                        continue
                    return result('UNKNOWN' if operation.get('mutation_attempted') or failure_reason else 'BLOCKED',
                                  rejection)
                rejected_observations = 0
                if action == 'start':
                    attempted_targets.add(target)
            elif failure_reason:
                pass  # Wait for a safe stock return and the original clean home.
            elif view.get('gate') not in ('', 'PASS'):
                return result('UNKNOWN' if engaged else 'UNAVAILABLE', view['gate'])
            elif status == 'BLOCKED':
                return result('BLOCKED', reason)
            elif closed and view.get('target', -1) < 0 and mask != 31:
                return result('UNKNOWN', 'divinity_roster_incomplete_without_target')
            self.wait()
        return result('UNKNOWN' if engaged and not closed else 'UNAVAILABLE', 'divinity_timeout')
