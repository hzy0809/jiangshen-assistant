"""Use natural attempts and the explicitly free opponent refresh in PointRace."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid


POINT_RACE_ROUTE = 109  # Assistant route, not an activity ID from the server.


@dataclass(frozen=True)
class DailyPointRaceResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int = 10
    stock_callback_returned: bool = False
    account_progress_observed: bool = False
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    challenges_completed: int = 0
    free_refreshes_completed: int = 0
    cooldown_remaining_seconds: int | None = None
    role_id: str = ''
    refresh_cycle: int | None = None
    release_verified: bool = False
    entry_timeout_released: bool = False
    cleanup_pending: bool = False
    session_restart_required: bool = False
    native_reason: str = ''
    native_snapshot: dict | None = None


class DailyPointRaceExecutor:
    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=240.0, observation_timeout=5.0, pending_timeout=20.0,
                 observation_interval=1.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.2))
        self.timeout, self.observation_timeout, self.clock = timeout, observation_timeout, clock
        self.pending_timeout, self.observation_interval = pending_timeout, observation_interval

    def execute(self, task_id=POINT_RACE_ROUTE, *, character_id: str,
                expected_current: int | None = None, expected_required: int | None = None,
                expected_cycle: int | None = None):
        if task_id != POINT_RACE_ROUTE:
            raise ValueError('pointrace_route_unsupported')
        operation_id = uuid.uuid4().hex
        initial = current = None
        returned = progressed = submitted = touched = closed = False
        completed = 0
        round_attempted = False
        baseline = premium = None
        entry_action = ''
        challenge_opponents = []
        completed_reports = set()
        completed_sequence = 0
        refresh_submitted = set()
        refresh_verified = set()
        deadline = self.clock() + self.timeout
        observation_deadline = None
        revision_deadline = None
        challenge_recovery_deadline = None
        challenge_recovery_reason = ''
        failure_reason = ''
        cleanup_submitted = False
        cleanup_deadline = None
        normal_close_submitted = False
        pending_deadline = self.clock() + self.pending_timeout
        next_observation = self.clock()
        view, operation = {}, {}

        def released():
            return (view.get('role_id') == character_id
                    and type(expected_cycle) is int and expected_cycle > 0
                    and view.get('refresh_cycle') == expected_cycle
                    and view.get('home_ready') is True
                    and operation.get('release_verified') is True
                    and operation.get('busy') is False
                    and operation.get('cleanup_pending') is False)

        def remaining_cooldown():
            cooldown, server_now = view.get('cooldown_until'), view.get('server_now')
            return (cooldown - server_now
                    if type(cooldown) is int and type(server_now) is int
                    and 0 < server_now < cooldown <= 0xffffffff else None)

        def entry_timeout_released():
            return (submitted and operation.get('id') == operation_id and entry_action == 'enter'
                    and operation.get('status') == 'UNKNOWN'
                    and operation.get('reason') == 'pointrace_entry_callback_timeout'
                    and operation.get('entry_timeout_cleanup') is True
                    and operation.get('cleanup_attempted') is True
                    and operation.get('entry_listener_released') is True
                    and type(operation.get('entry_request_sequence')) is int
                    and operation['entry_request_sequence'] > 0
                    and operation.get('entry_callback_returned') is False
                    and operation.get('baseline_ready') is False and baseline is None
                    and operation.get('mutation_attempted') is False
                    and operation.get('free_refresh_attempted') is False
                    and type(operation.get('challenges_submitted')) is int
                    and operation['challenges_submitted'] == 0
                    and type(operation.get('challenges_completed')) is int
                    and operation['challenges_completed'] == 0
                    and not challenge_opponents and not refresh_submitted and not round_attempted
                    and operation.get('closed') is True and closed and released())

        def refused_challenge_idle():
            return (challenge_recovery_deadline is not None and baseline is not None and
                    operation.get('id') == operation_id and operation.get('status') not in ('UNKNOWN', 'STOPPED') and
                    operation.get('challenge_recovery_pending') is True and
                    operation.get('round_pending') is False and not round_attempted and
                    type(operation.get('challenges_submitted')) is int and
                    operation['challenges_submitted'] == completed and
                    type(operation.get('challenges_completed')) is int and
                    operation['challenges_completed'] == completed and
                    len(challenge_opponents) == completed and current == initial + completed and
                    view.get('observation_available') is True and view.get('role_id') == character_id and
                    view.get('refresh_cycle') == expected_cycle and
                    tuple(view.get(key) for key in ('gold', 'bound_gold', 'gold_consumed', 'bought')) == premium)

        def result(status, reason, *, cooldown_remaining_seconds=None):
            if failure_reason and status != 'STOPPED':
                status, reason = 'UNKNOWN', failure_reason
            cleanup_pending = ((touched or submitted) and not released()
                               or operation.get('cleanup_pending') is True)
            return DailyPointRaceResult(
                status=status, reason=reason, progress_before=initial, progress_after=current,
                stock_callback_returned=returned, account_progress_observed=progressed,
                cleanup='VERIFIED_CLOSED' if closed else 'UNVERIFIED' if touched or submitted else 'NOT_NEEDED',
                operation_id=operation_id if submitted else '', challenges_completed=completed,
                free_refreshes_completed=len(refresh_verified),
                cooldown_remaining_seconds=cooldown_remaining_seconds,
                role_id=view.get('role_id', ''), refresh_cycle=view.get('refresh_cycle'),
                release_verified=released(), entry_timeout_released=entry_timeout_released(),
                cleanup_pending=cleanup_pending,
                session_restart_required=cleanup_pending and status == 'UNKNOWN',
                native_reason=operation.get('reason', ''),
                native_snapshot={'view': dict(view), 'operation': dict(operation)})

        while self.clock() < deadline:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                observation_deadline = None
                if pending_deadline is not None:
                    pending_deadline = self.clock() + self.pending_timeout
                if cleanup_deadline is not None:
                    cleanup_deadline = self.clock() + self.observation_timeout
                if challenge_recovery_deadline is not None:
                    challenge_recovery_deadline = self.clock() + self.observation_timeout
                continue
            if self.clock() < next_observation:
                self.wait()
                continue
            snapshot = self.objects.observe_daily_script(self.token, POINT_RACE_ROUTE)
            next_observation = self.clock() + self.observation_interval
            view, operation = snapshot['view'], snapshot['operation']
            status, reason = operation.get('status'), operation.get('reason', '')
            own_operation = submitted and operation.get('id') == operation_id
            if submitted and not own_operation:
                return result('UNKNOWN', 'pointrace_operation_changed')
            if own_operation:
                if status == 'UNKNOWN' and not failure_reason:
                    failure_reason = reason or 'pointrace_unknown'
                returned |= operation.get('stock_callback_returned') is True
                progressed |= operation.get('account_progress_correlated') is True
                touched |= operation.get('ui_touched') is True
                # mutation_attempted is cumulative across confirmed rounds.
                round_attempted |= operation.get('round_pending', operation.get('mutation_attempted')) is True
                closed = operation.get('closed') is True and released()
                if operation.get('free_refresh_verified') is True:
                    if (operation_id not in refresh_submitted or
                            operation.get('free_refresh_callback_returned') is not True or
                            operation.get('free_refresh_receipt_gold') != 0 or view.get('refresh_count') != 1):
                        return result('UNKNOWN', 'pointrace_free_refresh_evidence_changed')
                    refresh_verified.add(operation_id)
            observed = (view.get('observation_available') is True and
                        isinstance(view.get('role_id'), str) and view['role_id'] not in ('', '0'))
            if not observed:
                if own_operation and status in ('UNKNOWN', 'STOPPED'):
                    return result(status, reason)
                if observation_deadline is None:
                    observation_deadline = self.clock() + self.observation_timeout
                if self.clock() >= observation_deadline:
                    return result('UNKNOWN' if touched else 'UNAVAILABLE',
                                  view.get('gate') or reason or 'pointrace_observation_unavailable')
                self.wait()
                continue
            observation_deadline = None
            if view.get('mode') != 'pointrace' or view.get('task_id') != POINT_RACE_ROUTE:
                return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_route_changed')
            if view.get('continuous_scene') is not True:
                return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_continuous_contract_unavailable')
            if type(operation.get('baseline_ready')) is not bool:
                return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_baseline_contract_unavailable')
            if view['role_id'] != character_id:
                return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_character_changed')
            if expected_cycle is None:
                expected_cycle = view['refresh_cycle']
            if (type(expected_cycle) is not int or expected_cycle <= 0
                    or type(view.get('refresh_cycle')) is not int or view['refresh_cycle'] != expected_cycle):
                return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_refresh_cycle_changed')
            current = view['current']
            if type(current) is not int or not 0 <= current <= 20:
                return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_progress_schema_changed')
            if initial is None:
                if ((expected_current is not None and current != expected_current) or
                        (expected_required is not None and expected_required != 10) or view.get('required') != 10):
                    return result('BLOCKED', 'pointrace_initial_progress_changed')
                initial = current
                premium = tuple(view.get(key) for key in ('gold', 'bound_gold', 'gold_consumed', 'bought'))
                if any(type(value) is not int or value < 0 for value in premium):
                    return result('BLOCKED', 'pointrace_premium_schema_changed')
            if (not submitted and operation.get('id') == '' and released()
                    and view.get('open') is True and current < 10
                    and remaining_cooldown() is not None):
                # No native action was dispatched; this is a release sample,
                # not a fabricated operation or stock close receipt.
                return result('WAITING', 'pointrace_cooldown',
                              cooldown_remaining_seconds=remaining_cooldown())
            if own_operation:
                if tuple(view.get(key) for key in ('gold', 'bound_gold', 'gold_consumed', 'bought')) != premium:
                    return result('UNKNOWN', 'pointrace_premium_changed')
                native_submitted = operation.get('challenges_submitted')
                native_completed = operation.get('challenges_completed')
                native_baseline = (operation.get('baseline_count'), operation.get('baseline_source'),
                                   operation.get('baseline_sequence'))
                if baseline is None:
                    # A navigation callback may refresh a cached account count.
                    # It is never evidence that this executor completed a round.
                    if (native_submitted != 0 or type(native_submitted) is not int or
                            native_completed != 0 or type(native_completed) is not int or
                            operation.get('mutation_attempted') is not False or
                            operation.get('free_refresh_attempted') is not False or
                            challenge_opponents or refresh_submitted or round_attempted):
                        return result('UNKNOWN', 'pointrace_mutation_before_baseline')
                    if operation['baseline_ready']:
                        count, source, request_sequence = native_baseline
                        if (type(count) is not int or not 0 <= count <= 20 or current != count or
                                view.get('in_scene') is not True or
                                type(request_sequence) is not int or request_sequence <= 0 or
                                type(operation.get('sequence')) is not int or
                                operation.get('sequence') != request_sequence or
                                not (source == 'entry_callback' and entry_action == 'enter' and
                                     operation.get('entry_callback_returned') is True or
                                     source == 'scene_resume' and entry_action == 'resume' and count == initial and
                                     operation.get('entry_callback_returned') is False)):
                            return result('UNKNOWN', 'pointrace_entry_baseline_changed')
                        baseline = native_baseline
                        initial = count
                    elif (native_baseline != (None, '', 0) or
                          type(operation.get('entry_callback_returned')) is not bool or
                          (operation['entry_callback_returned'] and entry_action != 'enter') or
                          (not operation['entry_callback_returned'] and current != initial)):
                        return result('UNKNOWN', 'pointrace_pending_baseline_changed')
                elif (operation['baseline_ready'] is not True or native_baseline != baseline or
                      operation.get('entry_callback_returned') is not (baseline[1] == 'entry_callback')):
                    return result('UNKNOWN', 'pointrace_baseline_changed')
            if own_operation and baseline is not None:
                if (type(native_submitted) is not int or type(native_completed) is not int or
                        native_submitted != len(challenge_opponents) or
                        not completed <= native_completed <= completed + 1 or
                        not native_completed <= native_submitted <= max(0, 10 - initial) or
                        not initial + native_completed <= current <= initial + native_submitted):
                    return result('UNKNOWN', 'pointrace_round_progress_changed')
                if native_completed != completed:
                    report = operation.get('last_completed_report_id')
                    request_sequence = operation.get('last_completed_sequence')
                    if (native_completed != native_submitted or
                            operation.get('last_completed_count') != initial + native_completed or
                            operation.get('last_completed_opponent_id') != challenge_opponents[native_completed - 1] or
                            operation.get('last_completed_receipt_gold') != 0 or
                            operation.get('stock_callback_returned') is not True or
                            operation.get('account_progress_correlated') is not True or
                            type(request_sequence) is not int or request_sequence <= completed_sequence or
                            not isinstance(report, str) or not report.isdecimal() or int(report) == 0 or
                            report in completed_reports):
                        return result('UNKNOWN', 'pointrace_round_receipt_changed')
                    completed = native_completed
                    completed_sequence = request_sequence
                    completed_reports.add(report)
                    round_attempted = False
                    deadline = self.clock() + self.timeout
                    pending_deadline = self.clock() + self.pending_timeout
            if not submitted and current >= 10 and view.get('home_ready') is True:
                return result('ALREADY_COMPLETE', 'pointrace_natural_attempts_exhausted')
            if (not submitted and operation.get('id') and operation.get('ui_touched') is True
                    and operation.get('closed') is not True):
                return result('BLOCKED', 'pointrace_operation_owned')
            action = view.get('next_action', '')
            if own_operation and status in ('UNKNOWN', 'STOPPED'):
                # A timed-out, unmutated entry may offer only stock navigation
                # cleanup. Returning home never supplies its missing callback.
                cleanup_offer = (
                    status == 'UNKNOWN' and reason == 'pointrace_entry_callback_timeout' and
                    operation.get('entry_timeout_cleanup') is True and entry_action == 'enter' and
                    type(operation.get('entry_request_sequence')) is int and
                    operation['entry_request_sequence'] > 0 and
                    operation.get('entry_callback_returned') is False and
                    operation.get('baseline_ready') is False and baseline is None and
                    operation.get('mutation_attempted') is False and
                    operation.get('free_refresh_attempted') is False and
                    type(native_submitted) is int and native_submitted == 0 and
                    type(native_completed) is int and native_completed == 0 and
                    not challenge_opponents and not refresh_submitted and not round_attempted)
                if (cleanup_offer and cleanup_submitted and not closed and not action and
                        operation.get('cleanup_attempted') is True and self.clock() < cleanup_deadline):
                    self.wait()
                    continue
                if not (cleanup_offer and not cleanup_submitted and not closed and action == 'return' and
                        operation.get('cleanup_attempted') is False and view.get('in_scene') is True):
                    return result(status, reason)
            if own_operation and closed and status in ('COMPLETED', 'PARTIAL', 'UNAVAILABLE'):
                if len(challenge_opponents) != completed or current != initial + completed:
                    return result('UNKNOWN', 'pointrace_close_progress_changed')
                if current >= 10:
                    return result('COMPLETED', 'pointrace_natural_attempts_exhausted')
                if reason == 'pointrace_cooldown':
                    remaining = remaining_cooldown()
                    if (not normal_close_submitted or operation.get('entry_timeout_cleanup') is not False
                            or view.get('open') is not True or remaining is None):
                        return result('UNKNOWN', 'pointrace_cooldown_release_unverified')
                    return result('WAITING', reason, cooldown_remaining_seconds=remaining)
                return result('PARTIAL' if completed else 'UNAVAILABLE', reason or 'pointrace_unavailable')
            if self.clock() >= pending_deadline and status not in ('UNKNOWN', 'STOPPED'):
                if refused_challenge_idle():
                    # Cleanup can remain unavailable without turning a proven
                    # refusal into an uncertain battle. Release stays unverified.
                    return result('PARTIAL' if completed else 'UNAVAILABLE',
                                  challenge_recovery_reason or 'pointrace_challenge_cleanup_unavailable')
                # A command ACK is not the entry/round callback. Never replay
                # an uncertain request when the native watchdog is unavailable.
                entry_pending = (entry_action == 'enter' and baseline is None
                                 and operation.get('entry_callback_returned') is False
                                 and type(operation.get('entry_request_sequence')) is int
                                 and operation['entry_request_sequence'] > 0
                                 and operation.get('mutation_attempted') is False)
                return result('UNKNOWN' if submitted or touched else 'UNAVAILABLE',
                              'pointrace_entry_callback_timeout' if entry_pending else
                              'pointrace_action_timeout' if submitted or touched else 'pointrace_readiness_timeout')
            if status in ('UNAVAILABLE', 'COMPLETED', 'PARTIAL', 'UNKNOWN', 'STOPPED') and not action:
                if touched and not closed:
                    self.wait()
                    continue
                return result('PARTIAL' if completed else 'UNAVAILABLE', reason or 'pointrace_unavailable')
            if action:
                if (action == 'challenge' and challenge_recovery_deadline is not None and
                        self.clock() >= challenge_recovery_deadline):
                    # Native owns the bounded stock close; never resend the
                    # refused wire command or extend the retry window.
                    self.wait()
                    continue
                if status == 'UNKNOWN' and action != 'return':
                    return result('UNKNOWN', reason)
                if action in ('enter', 'resume') and entry_action:
                    return result('UNKNOWN', 'pointrace_duplicate_entry')
                if action in ('challenge', 'refresh_free') and (not own_operation or baseline is None):
                    return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_baseline_not_ready')
                if action == 'challenge':
                    opponent = view.get('opponent_id')
                    if (len(challenge_opponents) != completed or current != initial + completed or
                            not current < 10 or view.get('in_scene') is not True or
                            not isinstance(opponent, str) or not opponent.isdecimal() or
                            int(opponent) == 0 or opponent == character_id):
                        return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_challenge_offer_changed')
                if action == 'refresh_free':
                    if operation_id in refresh_submitted or operation.get('free_refresh_attempted') is True:
                        return result('UNKNOWN', 'pointrace_duplicate_free_refresh_offer')
                    opponent = view.get('opponent_id')
                    score, player_score = view.get('opponent_score'), view.get('player_score')
                    needs_refresh = (opponent == '0' or
                                     type(score) is int and type(player_score) is int and
                                     player_score > 0 and score > player_score)
                    if (view.get('free_refresh_available') is not True or
                            type(view.get('refresh_count')) is not int or view['refresh_count'] != 0 or
                            type(current) is not int or not 0 <= current < 10 or not needs_refresh):
                        return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_free_refresh_unavailable')
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=POINT_RACE_ROUTE, character_id=character_id,
                    operation_id=operation_id, expected_revision=snapshot['revision'])
                ack = wire.get('ack') or {}
                if (ack.get('status') == 'REJECTED' and
                        str(ack.get('reason', '')).startswith('pointrace_queue_revision_changed:')):
                    if revision_deadline is None:
                        revision_deadline = self.clock() + 10.0
                    if self.clock() >= revision_deadline:
                        return result('UNKNOWN' if touched else 'BLOCKED', 'pointrace_revision_churn')
                    self.wait()
                    continue
                if (action == 'enter' and ack.get('status') == 'REJECTED' and
                        ack.get('reason') == 'pointrace_enter_transport_busy'):
                    evidence = ((ack.get('result') or {}).get('daily_script') or {}).get('operation') or {}
                    # Retry only a refusal before the native entry invocation.
                    # A started or uncertain entry must retain duplicate guards.
                    if (evidence.get('id') != operation_id or
                            type(evidence.get('entry_request_sequence')) is not int or
                            evidence['entry_request_sequence'] != 0 or
                            evidence.get('entry_callback_returned') is not False or
                            evidence.get('baseline_ready') is not False or
                            evidence.get('mutation_attempted') is not False or
                            evidence.get('free_refresh_attempted') is not False or
                            type(evidence.get('challenges_submitted')) is not int or
                            evidence['challenges_submitted'] != 0 or
                            type(evidence.get('challenges_completed')) is not int or
                            evidence['challenges_completed'] != 0):
                        return result('UNKNOWN', 'pointrace_entry_noninvocation_unproven')
                    self.wait()
                    continue
                if (action == 'challenge' and ack.get('status') == 'REJECTED' and
                        ack.get('reason') == 'pointrace_free_challenge_unavailable'):
                    refused = (ack.get('result') or {}).get('daily_script') or {}
                    evidence, refused_view = refused.get('operation') or {}, refused.get('view') or {}
                    command_sequence = wire.get('requested_seq', wire.get('seq'))
                    refusal_reason = evidence.get('challenge_rejection_reason')
                    if (not own_operation or baseline is None or
                            type(command_sequence) is not int or command_sequence <= operation.get('sequence', 0) or
                            ack.get('command_seq') != command_sequence or
                            evidence.get('id') != operation_id or evidence.get('sequence') != command_sequence or
                            evidence.get('challenge_rejection_sequence') != command_sequence or
                            evidence.get('challenge_rejection_noninvoked') is not True or
                            evidence.get('challenge_rejection_idle') is not True or
                            evidence.get('challenge_recovery_pending') is not True or
                            evidence.get('round_pending') is not False or
                            evidence.get('status') in ('UNKNOWN', 'STOPPED') or
                            evidence.get('baseline_ready') is not True or
                            (evidence.get('baseline_count'), evidence.get('baseline_source'),
                             evidence.get('baseline_sequence')) != baseline or
                            type(evidence.get('challenges_submitted')) is not int or
                            evidence['challenges_submitted'] != completed or
                            type(evidence.get('challenges_completed')) is not int or
                            evidence['challenges_completed'] != completed or
                            evidence.get('challenge_request_sequence') != operation.get('challenge_request_sequence') or
                            evidence.get('last_completed_sequence') != completed_sequence or
                            evidence.get('last_completed_report_id') != operation.get('last_completed_report_id') or
                            len(challenge_opponents) != completed or
                            refused_view.get('observation_available') is not True or
                            refused_view.get('role_id') != character_id or
                            refused_view.get('refresh_cycle') != expected_cycle or
                            refused_view.get('current') != initial + completed or
                            tuple(refused_view.get(key) for key in ('gold', 'bound_gold', 'gold_consumed', 'bought')) != premium or
                            not isinstance(refusal_reason, str) or not refusal_reason):
                        return result('UNKNOWN', 'pointrace_challenge_noninvocation_unproven')
                    if challenge_recovery_deadline is None:
                        challenge_recovery_deadline = self.clock() + self.observation_timeout
                        challenge_recovery_reason = refusal_reason
                        pending_deadline = self.clock() + self.pending_timeout
                        deadline = max(deadline, pending_deadline)
                    round_attempted = False
                    self.wait()
                    continue
                submitted = True
                closed = False
                revision_deadline = None
                pending_deadline = self.clock() + self.pending_timeout
                if action == 'refresh_free':
                    refresh_submitted.add(operation_id)
                if action == 'return' and status == 'UNKNOWN':
                    cleanup_submitted = True
                    cleanup_deadline = self.clock() + self.observation_timeout
                if ack.get('status') != 'COMPLETED':
                    return result('UNKNOWN' if touched or round_attempted else 'BLOCKED',
                                  ack.get('reason') or 'pointrace_command_rejected')
                if action in ('enter', 'resume'):
                    entry_action = action
                if action in ('return', 'close_selector') and status != 'UNKNOWN':
                    normal_close_submitted = True
                if action == 'challenge':
                    challenge_recovery_deadline = None
                    challenge_recovery_reason = ''
                    challenge_opponents.append(view['opponent_id'])
                    round_attempted = True
                touched = True
            self.wait()
        if refused_challenge_idle():
            return result('PARTIAL' if completed else 'UNAVAILABLE',
                          challenge_recovery_reason or 'pointrace_challenge_cleanup_unavailable')
        return result('UNKNOWN' if touched and not closed else 'PARTIAL' if completed else 'UNAVAILABLE',
                      'pointrace_timeout')
