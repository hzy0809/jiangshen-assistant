"""Run the normal free flower gift through the native daily script bridge."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid


@dataclass(frozen=True)
class DailyFlowerResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    stock_callback_returned: bool = False
    account_progress_observed: bool = False
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''


def _uninvoked_network_rejection(wire, before):
    """Recognize only a queue rejection that left this operation untouched."""
    if not isinstance(wire, dict) or wire.get('command') != 'daily-script':
        return False
    ack, state, sequence = wire.get('ack'), wire.get('state'), wire.get('requested_seq')
    if (not isinstance(ack, dict) or not isinstance(state, dict) or type(sequence) is not int
            or ack.get('status') != 'REJECTED' or ack.get('reason') != 'flower_network_busy'
            or ack.get('command_name') != 'daily-script' or ack.get('command_seq') != sequence
            or state.get('last_command_seq') != sequence or state.get('lifecycle') != 'ready'):
        return False
    gui, reply = state.get('campaign_gui'), ack.get('result')
    if (not isinstance(gui, dict) or type(gui.get('command_seq')) is not int
            or gui['command_seq'] >= sequence or not isinstance(reply, dict)):
        return False
    op = before['operation']
    if (sequence <= op.get('sequence', sequence) or op.get('mutation_attempted') is not False
            or op.get('stock_callback_returned') is not False or op.get('account_progress_correlated') is not False):
        return False
    for current in (reply.get('daily_script'), state.get('daily_script')):
        if not isinstance(current, dict) or type(current.get('revision')) is not int or current['revision'] < before['revision']:
            return False
        view, operation = current.get('view'), current.get('operation')
        if (not isinstance(view, dict) or not isinstance(operation, dict)
                or any(view.get(key) != before['view'].get(key) for key in
                       ('task_id', 'role_id', 'current', 'required', 'refresh_cycle', 'recipient_id', 'ui_ownership_unreleased'))
                or view.get('task_id') != 11 or any(operation.get(key) != op.get(key) for key in
                       ('id', 'sequence', 'status', 'mutation_attempted', 'stock_callback_returned', 'account_progress_correlated', 'closed'))):
            return False
    return True


def _uninvoked_open_timeout(wire, before, operation_id):
    if (not isinstance(wire, dict) or wire.get('command') != 'daily-script'
            or before['operation'].get('status') != 'IDLE'
            or before['view'].get('ui_ownership_unreleased') is not False):
        return False
    ack, state, sequence = wire.get('ack'), wire.get('state'), wire.get('requested_seq')
    if (not isinstance(ack, dict) or not isinstance(state, dict) or type(sequence) is not int
            or sequence <= before['operation'].get('sequence', sequence)
            or ack.get('status') != 'REJECTED' or ack.get('reason') != 'scenario_gui_callback_unavailable'
            or ack.get('command_name') != 'daily-script' or ack.get('command_seq') != sequence
            or state.get('last_command_seq') != sequence or state.get('lifecycle') != 'ready'):
        return False
    gui, reply = state.get('campaign_gui'), ack.get('result')
    if (not isinstance(gui, dict) or gui.get('command_seq') != sequence or gui.get('phase') != 'not_invoked'
            or gui.get('invocation_attempted') is not False or not isinstance(reply, dict)):
        return False
    for current in (reply.get('daily_script'), state.get('daily_script')):
        if not isinstance(current, dict) or type(current.get('revision')) is not int or current['revision'] <= before['revision']:
            return False
        view, op = current.get('view'), current.get('operation')
        if (not isinstance(view, dict) or not isinstance(op, dict) or view.get('task_id') != 11
                or any(view.get(key) != before['view'].get(key) for key in ('role_id', 'current', 'required', 'refresh_cycle'))
                or view.get('ui_ownership_unreleased') is not False or op.get('id') != '' or op.get('sequence') != sequence
                or op.get('gui_not_invoked_sequence') != sequence or op.get('gui_not_invoked_operation_id') != operation_id
                or op.get('status') != 'IDLE' or any(op.get(key) is not False for key in
                       ('queued', 'mutation_attempted', 'stock_callback_returned', 'account_progress_correlated', 'closed'))):
            return False
    return True


class DailyFlowerExecutor:
    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=90.0, observation_timeout=15.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.clock = timeout, clock
        self.observation_timeout = observation_timeout

    def execute(self, task_id=11, *, character_id: str, expected_current: int | None = None,
                expected_required: int | None = None, expected_cycle: int | None = None):
        if task_id != 11:
            raise ValueError('daily_flower_task_unsupported')
        operation_id = uuid.uuid4().hex
        baseline = current = required = None
        submitted = returned = progressed = closed = False
        deadline = self.clock() + self.timeout
        observation_deadline = None
        overlay_deadline = None
        network_deadline = None
        gui_retry_deadline = None
        observation_reason = ''

        def result(status, reason):
            return DailyFlowerResult(status, reason, baseline, current, required, returned, progressed,
                                     'VERIFIED_CLOSED' if closed else 'UNVERIFIED' if submitted else 'NOT_NEEDED',
                                     operation_id if submitted else '')

        while self.clock() < deadline:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                observation_deadline = None
                overlay_deadline = None
                network_deadline = None
                gui_retry_deadline = None
                continue
            if gui_retry_deadline is not None and self.clock() >= gui_retry_deadline:
                return result('UNAVAILABLE', 'flower_gui_callback_unavailable_retryable')
            snapshot = self.objects.observe_daily_script(self.token, 11)
            view, operation = snapshot['view'], snapshot['operation']
            returned = returned or operation.get('stock_callback_returned', False)
            progressed = progressed or operation.get('account_progress_correlated', False)
            closed = operation.get('closed', False)
            status, reason = operation['status'], operation.get('reason', '')
            role_id = view.get('role_id')
            observed = (isinstance(role_id, str) and role_id not in ('', '0')
                        and view.get('observation_available', True))
            if observed and role_id != character_id:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'flower_character_changed')
            if not observed:
                if status == 'COMPLETED':
                    return result('COMPLETED', reason)
                if status == 'NO_CANDIDATES':
                    return result('UNAVAILABLE', reason)
                if status in ('UNKNOWN', 'REJECTED', 'STOPPED'):
                    return result(status, reason)
                # Missing native UI/role observations are not a role switch.
                observation_reason = view.get('gate') or reason or 'flower_observation_unavailable'
                if observation_deadline is None:
                    observation_deadline = self.clock() + self.observation_timeout
                if self.clock() >= observation_deadline:
                    return result('UNKNOWN' if submitted else 'UNAVAILABLE', observation_reason)
                self.wait()
                continue
            observation_deadline = None
            observation_reason = ''
            if expected_cycle is None:
                expected_cycle = view['refresh_cycle']
            if view['refresh_cycle'] != expected_cycle:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'flower_refresh_cycle_changed')
            current, required = view['current'], view['required']
            if baseline is None:
                if ((expected_current is not None and current != expected_current)
                        or (expected_required is not None and required != expected_required)):
                    return result('BLOCKED', 'flower_initial_progress_changed')
                baseline = current
            if status == 'COMPLETED':
                return result('COMPLETED', reason)
            if status == 'NO_CANDIDATES':
                return result('UNAVAILABLE', reason)
            if status in ('UNKNOWN', 'REJECTED', 'STOPPED'):
                return result(status, reason)
            if not submitted and current >= required > 0:
                return result('ALREADY_COMPLETE', 'flower_activity_complete')
            action = view.get('next_action', '')
            if action:
                overlay_deadline = None
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=11, character_id=character_id,
                    operation_id=operation_id, expected_revision=snapshot['revision'])
                ack = wire.get('ack') or {}
                if action == 'open' and not submitted and _uninvoked_open_timeout(wire, snapshot, operation_id):
                    if gui_retry_deadline is None:
                        gui_retry_deadline = self.clock() + self.observation_timeout
                    self.wait()
                    continue
                if _uninvoked_network_rejection(wire, snapshot):
                    if network_deadline is None:
                        network_deadline = self.clock() + self.observation_timeout
                    if self.clock() < network_deadline:
                        self.wait()
                        continue
                    return result('BLOCKED' if submitted else 'UNAVAILABLE', 'flower_network_busy')
                submitted = True
                if ack.get('status') != 'COMPLETED':
                    return result('UNKNOWN' if operation.get('mutation_attempted') else 'BLOCKED',
                                  ack.get('reason') or 'flower_command_rejected')
                network_deadline = None
                gui_retry_deadline = None
                deadline = self.clock() + self.timeout
            elif not submitted and view.get('gate') not in ('', 'PASS'):
                gate = view['gate']
                if gate in ('development_overlay:visible_unknown_ui', 'flower_ui_overlay', 'flower_network_busy'):
                    if overlay_deadline is None:
                        overlay_deadline = self.clock() + self.observation_timeout
                    if self.clock() < overlay_deadline:
                        self.wait()
                        continue
                return result('UNAVAILABLE', gate)
            self.wait()
        return result('UNKNOWN' if submitted else 'UNAVAILABLE', observation_reason or 'flower_timeout')
