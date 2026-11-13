"""Run the normal own/friend monster and revenge demon daily battle flows."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid

from .objects import CampaignAdmissionClosed


TASKS = (10, 15, 24)
ACTIONS = {'open', 'visit', 'prepare', 'start', 'close_battle', 'close_result', 'close_failed', 'return_home'}


@dataclass(frozen=True)
class DailyMonsterResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    task_confirmed: bool = False
    mutation_attempted: bool = False
    cleanup: str = 'NOT_NEEDED'
    clearing_complete: bool = False
    daily_complete: bool = False


class DailyMonsterExecutor:
    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=260.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.clock = timeout, clock

    def execute(self, task_id, *, character_id, expected_cycle=None, expected_current=None, expected_required=None):
        if task_id not in TASKS:
            raise ValueError('daily_monster_task_unsupported')
        operation_id = uuid.uuid4().hex
        before = required = None
        latest = {}
        submitted = False
        completed_targets = 0
        deadline = self.clock() + self.timeout
        gui_retry_deadline = None

        def result(status, reason):
            held = (latest.get('cleanup_pending', submitted) or latest.get('reward_pending', False)
                    or status == 'UNKNOWN' and submitted)
            observed_current = latest.get('current')
            advanced = type(before) is int and type(observed_current) is int and observed_current > before
            closed = (latest.get('home_ready') is True and not latest.get('reward_pending')
                      and not latest.get('cleanup_pending') and not latest.get('busy'))
            return DailyMonsterResult(status, reason, before, latest.get('current'), required,
                advanced, latest.get('mutation_attempted', False),
                'UNVERIFIED' if held else 'VERIFIED_CLOSED' if submitted else 'NOT_NEEDED',
                task_id == 24 and status in ('COMPLETED', 'ALREADY_COMPLETE') and closed
                and latest.get('clearing_complete') is True,
                type(required) is int and type(observed_current) is int and observed_current >= required)

        while True:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                self.wait()
                continue
            if self.clock() >= deadline:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'monster_timeout')
            if gui_retry_deadline is not None and self.clock() >= gui_retry_deadline:
                return result('UNAVAILABLE', 'monster_gui_callback_unavailable_retryable')
            try:
                observed = self.objects.observe_daily_script(self.token, task_id)
            except Exception as exc:
                return result('UNKNOWN' if submitted else 'BLOCKED', f'monster_observation_failed ({exc})')
            if not isinstance(observed, dict) or observed.get('task_id') != task_id:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'monster_observation_invalid')
            latest = observed
            if latest.get('role_id') != character_id:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'monster_role_changed')
            cycle = latest.get('cycle')
            if expected_cycle is None:
                expected_cycle = cycle
            if not cycle or cycle != expected_cycle:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'monster_cycle_changed')
            if before is None:
                before, required = latest['current'], latest['required']
                if (expected_current is not None and before != expected_current
                        or expected_required is not None and required != expected_required):
                    return result('BLOCKED', 'monster_daily_progress_changed')
                # Revenge owns the current demon, including a pending claim
                # after today's activity counter has already reached its cap.
                if task_id != 24 and before >= required:
                    if latest.get('home_ready') is True:
                        return result('ALREADY_COMPLETE', 'monster_activity_complete')
                    return result('UNKNOWN', 'monster_completed_ui_not_closed')
            if submitted and latest.get('operation_id') != operation_id:
                return result('UNKNOWN', 'monster_operation_changed')
            if submitted:
                settled = latest.get('completed_targets', completed_targets)
                if type(settled) is not int or settled < completed_targets:
                    return result('UNKNOWN', 'monster_completed_targets_changed')
                if settled > completed_targets:
                    if task_id == 24 and settled > 1:
                        return result('UNKNOWN', 'monster_revenge_target_bound_exceeded')
                    # One operation can settle several targets on its owned
                    # city page. A confirmed target earns a fresh bounded wait
                    # for the next target, without releasing/reopening the UI.
                    completed_targets = settled
                    if task_id != 24:
                        deadline = self.clock() + self.timeout
            status, action = latest.get('status'), latest.get('next_action')
            if status == 'COMPLETED' and (submitted or task_id == 24 and latest.get('clearing_complete') is True):
                if (latest.get('home_ready') is True and not latest.get('reward_pending')
                        and not latest.get('cleanup_pending') and not latest.get('busy')):
                    if task_id == 24 and latest.get('clearing_complete') is not True:
                        return result('UNKNOWN', 'monster_revenge_clearing_unverified')
                    return result('COMPLETED' if submitted else 'ALREADY_COMPLETE',
                                  latest.get('reason') or 'monster_activity_advanced')
                return result('UNKNOWN', 'monster_completed_ui_not_closed')
            if status in ('UNKNOWN', 'STOPPED'):
                return result(status, latest.get('reason') or 'monster_operation_stopped')
            if status in ('BLOCKED', 'UNAVAILABLE') and not action:
                if latest.get('cleanup_pending'):
                    self.wait()
                    continue
                return result(status, latest.get('reason') or 'monster_unavailable')
            if not action:
                self.wait()
                continue
            if action not in ACTIONS or latest.get('gold_cost') != 0:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'monster_action_invalid')
            if self.stopped() or self.paused():
                continue
            try:
                wire = self.objects.submit_daily_script(self.token, action, task_id=task_id,
                    character_id=character_id, operation_id=operation_id, expected_revision=latest['revision'])
            except CampaignAdmissionClosed:
                if self.stopped() or self.paused():
                    continue
                return result('UNKNOWN' if submitted else 'BLOCKED', 'monster_admission_closed')
            except Exception as exc:
                submitted = True
                return result('UNKNOWN', f'monster_command_failed ({exc})')
            ack = wire.get('ack', {})
            receipt = (ack.get('result') or {}).get('daily_script')
            requested_seq = wire.get('requested_seq')
            if (action == 'open' and ack.get('status') == 'REJECTED'
                    and ack.get('reason') == 'scenario_gui_callback_unavailable'
                    and type(requested_seq) is int and ack.get('command_seq') == requested_seq
                    and isinstance(receipt, dict) and receipt.get('sequence') == requested_seq
                    and receipt.get('gui_not_invoked_sequence') == requested_seq
                    and receipt.get('gui_invocation_attempted') is False
                    and receipt.get('task_id') == task_id and receipt.get('role_id') == character_id
                    and receipt.get('operation_id') == operation_id
                    and receipt.get('mutation_attempted') is False
                    and receipt.get('cleanup_pending') is False and receipt.get('busy') is False):
                # The native cancellation witness belongs to this exact command
                # and proves even the friend page was not opened. Re-observe;
                # an absent witness or an owned page keeps the UNKNOWN path.
                latest = receipt
                if gui_retry_deadline is None:
                    gui_retry_deadline = self.clock() + 10.0
                self.wait()
                continue
            if (ack.get('status') == 'REJECTED'
                    and ack.get('command_seq') == wire.get('requested_seq')
                    and ack.get('reason') in ('monster_admission', 'monster_revision_changed', 'monster_action_changed')):
                # Rejected commands did not invoke the GUI. Observe the current
                # offer, including closing the friend panel when no target exists.
                self.wait()
                continue
            submitted = True
            if ack.get('status') != 'COMPLETED':
                return result('UNKNOWN', ack.get('reason') or 'monster_command_unconfirmed')
            if (not isinstance(receipt, dict) or receipt.get('operation_id') != operation_id
                    or receipt.get('task_id') != task_id or receipt.get('role_id') != character_id
                    or ack.get('command_seq') != wire.get('requested_seq')):
                return result('UNKNOWN', 'monster_command_binding_invalid')
            latest = receipt
            gui_retry_deadline = None
            # The next observation selects the following normal GUI action.
            self.wait()

    def execute_all(self, *, character_id, tasks=TASKS, expected_cycle=None):
        results = []
        for task_id in tasks:
            for _ in range(32):
                step = self.execute(task_id, character_id=character_id, expected_cycle=expected_cycle)
                results.append(step)
                if step.status in ('UNKNOWN', 'STOPPED') or step.cleanup == 'UNVERIFIED':
                    return results
                if task_id == 24:
                    # A selected revenge route processes only this demon. It
                    # does not open another operation or wait for a refresh.
                    break
                if (step.status != 'COMPLETED' or step.progress_after is None or step.required is None
                        or step.progress_after >= step.required or step.progress_after <= step.progress_before):
                    break
        return results
