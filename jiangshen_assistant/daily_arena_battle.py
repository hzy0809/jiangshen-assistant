"""Use available natural arena challenges independently of activity 9's counter."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid


@dataclass(frozen=True)
class DailyArenaBattleResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    stock_callback_returned: bool = False
    account_progress_observed: bool = False
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    challenge_count_before: int | None = None
    challenge_count_after: int | None = None
    natural_limit: int | None = None
    natural_remaining: int | None = None
    challenge_buy: int | None = None
    cooldown_until: int | None = None
    cooldown_remaining: int | None = None
    battles_completed: int = 0
    battle_progress_observed: bool = False
    daily_money_before: int | None = None
    daily_money_after: int | None = None
    daily_money_limit: int = 20000
    daily_reputation_before: int | None = None
    daily_reputation_after: int | None = None
    daily_reputation_limit: int = 200
    daily_rewards_complete: bool = False
    cooldown_remaining_seconds: int | None = None
    role_id: str | None = None
    refresh_cycle: int | None = None
    home_ready: bool = False
    native_busy: bool | None = None
    release_verified: bool = False
    native_closed: bool = False
    native_operation_id: str = ''


class DailyArenaBattleExecutor:
    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=3600.0, observation_timeout=5.0,
                 initial_wait_timeout=15.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.2))
        self.timeout, self.clock = timeout, clock
        self.observation_timeout = observation_timeout
        self.initial_wait_timeout = initial_wait_timeout

    def execute(self, task_id=9, *, character_id: str, expected_current: int | None = None,
                expected_required: int | None = None, expected_cycle: int | None = None):
        if task_id != 9:
            raise ValueError('daily_arena_battle_task_unsupported')
        operation_id = uuid.uuid4().hex
        baseline = current = required = None
        challenge_before = challenge_after = natural_limit = natural_remaining = None
        challenge_buy = cooldown_until = cooldown_remaining = None
        money_before = money_after = reputation_before = reputation_after = None
        rewards_complete = False
        submitted = returned = progressed = closed = False
        home_ready = release_verified = False
        native_busy = None
        view, operation = {}, {}
        battle_progress = False
        owned_operations, completed_operations = set(), {}
        previous_round = ''
        deadline = self.clock() + self.timeout
        observation_deadline = None
        observation_reason = ''
        initial_wait_deadline = None
        revision_retry_deadline = cancel_deadline = None
        cancelling = cancel_submitted = False

        def result(status, reason, *, cooldown_remaining_seconds=None):
            return DailyArenaBattleResult(
                status=status, reason=reason, progress_before=baseline, progress_after=current,
                required=required, stock_callback_returned=returned, account_progress_observed=progressed,
                cleanup='VERIFIED_CLOSED' if release_verified else
                'UNVERIFIED' if submitted or native_busy is not False or not home_ready else 'NOT_NEEDED',
                operation_id=operation_id if submitted else '', challenge_count_before=challenge_before,
                challenge_count_after=challenge_after, natural_limit=natural_limit,
                natural_remaining=natural_remaining, challenge_buy=challenge_buy,
                cooldown_until=cooldown_until, cooldown_remaining=cooldown_remaining,
                battles_completed=sum(completed_operations.values()), battle_progress_observed=battle_progress,
                daily_money_before=money_before, daily_money_after=money_after,
                daily_reputation_before=reputation_before, daily_reputation_after=reputation_after,
                daily_rewards_complete=rewards_complete,
                cooldown_remaining_seconds=cooldown_remaining_seconds,
                role_id=view.get('role_id'), refresh_cycle=view.get('refresh_cycle'),
                home_ready=home_ready, native_busy=native_busy,
                release_verified=release_verified, native_closed=operation.get('closed') is True,
                native_operation_id=operation.get('id', ''))

        while self.clock() < deadline:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                observation_deadline = None
                initial_wait_deadline = None
                continue
            if cancel_deadline is not None and self.clock() >= cancel_deadline:
                return result('UNKNOWN', 'arena_battle_revision_cleanup_timeout')
            snapshot = self.objects.observe_daily_script(self.token, 9)
            closed = release_verified = home_ready = False
            native_busy = None
            if (not isinstance(snapshot, dict) or not isinstance(snapshot.get('view'), dict)
                    or not isinstance(snapshot.get('operation'), dict)):
                view, operation = {}, {}
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'arena_battle_observation_invalid')
            view, operation = snapshot['view'], snapshot['operation']
            home_ready = view.get('home_ready') is True
            if type(operation.get('busy')) is bool:
                native_busy = operation['busy']
            status, reason = operation['status'], operation.get('reason', '')
            role_id = view.get('role_id')
            observed = (isinstance(role_id, str) and role_id not in ('', '0')
                        and view.get('observation_available', True))
            if observed and role_id != character_id:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'arena_battle_character_changed')
            if not observed:
                if status in ('UNKNOWN', 'STOPPED'):
                    return result(status, reason)
                # An empty role is the native placeholder for an incomplete read.
                # Wait briefly without admitting any action or binding progress.
                observation_reason = view.get('gate') or reason or 'arena_battle_observation_unavailable'
                if observation_deadline is None:
                    observation_deadline = self.clock() + self.observation_timeout
                if self.clock() >= observation_deadline:
                    return result('UNKNOWN' if submitted else 'UNAVAILABLE', observation_reason)
                self.wait()
                continue
            observation_deadline = None
            observation_reason = ''
            observed_cycle = view.get('refresh_cycle')
            if (type(observed_cycle) is not int or observed_cycle <= 0
                    or expected_cycle is not None and (type(expected_cycle) is not int or expected_cycle <= 0)):
                return result('UNKNOWN' if submitted else 'BLOCKED', 'arena_battle_refresh_cycle_unavailable')
            if expected_cycle is None:
                expected_cycle = observed_cycle
            if observed_cycle != expected_cycle:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'arena_battle_refresh_cycle_changed')
            if submitted and (operation.get('id') != operation_id or operation_id not in owned_operations):
                return result('UNKNOWN', 'arena_battle_operation_changed')
            closed = (submitted and operation.get('id') == operation_id
                      and operation.get('closed') is True and home_ready and native_busy is False)
            release_verified = bool(home_ready and native_busy is False and
                                    (closed if submitted else operation.get('closed') is True or
                                     operation.get('id') == '' and operation.get('mutation_attempted') is False))
            if operation.get('id') in owned_operations:
                returned = returned or operation.get('stock_callback_returned', False)
                progressed = progressed or operation.get('account_progress_correlated', False)
                battle_progress = battle_progress or operation.get('battle_progress_observed', False)
                # Keep receipt-backed round counts separate from UI release.
                rounds = operation.get('battles_completed')
                if rounds is not None:
                    if (type(rounds) is not int or rounds < completed_operations.get(operation['id'], 0)
                            or rounds > 12):
                        return result('UNKNOWN', 'arena_battle_completed_rounds_changed')
                    completed_operations[operation['id']] = rounds
                elif (closed and operation.get('battle_progress_observed') is True
                      and operation.get('stock_callback_returned') is True):
                    completed_operations[operation['id']] = 1
            current, required = view['current'], view['required']
            if baseline is None:
                if ((expected_current is not None and current != expected_current)
                        or (expected_required is not None and required != expected_required)):
                    return result('BLOCKED', 'arena_battle_initial_progress_changed')
                baseline = current
            if status in ('UNKNOWN', 'STOPPED'):
                return result(status, reason)
            challenge_after = view.get('challenge_count')
            natural_limit, natural_remaining = view.get('natural_limit'), view.get('natural_remaining')
            challenge_buy = view.get('challenge_buy')
            cooldown_until, cooldown_remaining = view.get('cooldown_until'), view.get('cooldown_remaining')
            money_after, reputation_after = view.get('daily_money'), view.get('daily_reputation')
            if (type(challenge_after) is not int or challenge_after < 0
                    or natural_limit != 12 or type(natural_remaining) is not int
                    or natural_remaining != max(0, natural_limit - challenge_after)
                    or type(money_after) is not int or money_after < 0
                    or type(reputation_after) is not int or reputation_after < 0
                    or view.get('daily_money_limit') != 20000 or view.get('daily_reputation_limit') != 200):
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'arena_battle_natural_observation_unavailable')
            if challenge_before is None:
                challenge_before = challenge_after
                money_before, reputation_before = money_after, reputation_after
            rewards_complete = money_after >= 20000 and reputation_after >= 200
            if rewards_complete and release_verified:
                return result('COMPLETED' if any(completed_operations.values()) else 'ALREADY_COMPLETE',
                              'arena_battle_daily_rewards_complete')
            if natural_remaining == 0 and release_verified:
                return result('UNAVAILABLE', 'arena_battle_natural_attempts_exhausted')
            if status == 'RETRYABLE' and closed and operation.get('id') == operation_id:
                return result('PARTIAL', reason or 'arena_battle_revision_changed_retryable')
            action = view.get('next_action', '')
            if cancelling:
                if cancel_submitted:
                    self.wait()
                    continue
                action = 'cancel'
            elif revision_retry_deadline is not None and self.clock() >= revision_retry_deadline:
                if not submitted or closed:
                    return result('PARTIAL', 'arena_battle_revision_changed_retryable')
                if operation.get('mutation_attempted') is not False:
                    return result('UNKNOWN', 'arena_battle_revision_changed_after_mutation')
                cancelling = True
                cancel_deadline = self.clock() + 5.0
                action = 'cancel'
            if (not action and (reason == 'arena_battle_cooldown'
                                or status == 'COMPLETED' and operation.get('closed') is True
                                and type(cooldown_remaining) is int and cooldown_remaining > 0)):
                server_time = view.get('server_now')
                if (type(cooldown_remaining) is not int or type(cooldown_until) is not int
                        or type(server_time) is not int or not 0 < server_time < cooldown_until <= 0xffffffff
                        or cooldown_remaining != cooldown_until - server_time):
                    return result('UNKNOWN' if submitted else 'BLOCKED', 'arena_battle_cooldown_unverified')
                if not release_verified:
                    return result('UNKNOWN' if submitted else 'BLOCKED', 'arena_battle_cooldown_release_unverified')
                if natural_remaining > 0 and not rewards_complete:
                    return result('WAITING', 'arena_battle_cooldown',
                                  cooldown_remaining_seconds=cooldown_remaining)
            if status == 'COMPLETED' and closed and not action:
                # Cleanup and the next free offer are published in successive
                # captures; a completed round alone is not the reward goal.
                self.wait()
                continue
            if status == 'UNAVAILABLE' and not action:
                return result('UNAVAILABLE', reason)
            if not submitted and status == 'WAITING' and not action:
                # Entry has not acquired an operation or invoked stock UI yet.
                # Do not spend the battle timeout waiting for another panel or
                # an unfinished home transition, and do not close either here.
                if initial_wait_deadline is None:
                    initial_wait_deadline = self.clock() + self.initial_wait_timeout
                if self.clock() >= initial_wait_deadline:
                    return result('UNAVAILABLE', 'arena_battle_initial_wait_timeout:' +
                                  (reason or 'arena_battle_waiting_arena'))
            else:
                initial_wait_deadline = None
            if status == 'COMPLETED' and action == 'open' and operation.get('id') != previous_round:
                previous_round = operation.get('id', '')
                operation_id = uuid.uuid4().hex
            if action:
                if action not in ('open', 'enter', 'refresh', 'challenge', 'continue', 'skip',
                                  'battle_close', 'reward_confirm', 'compare_close', 'close', 'cancel'):
                    return result('UNKNOWN' if submitted else 'BLOCKED', 'arena_battle_action_unsupported')
                closed = release_verified = False
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=9, character_id=character_id,
                    operation_id=operation_id, expected_revision=snapshot['revision'])
                ack = wire.get('ack') or {}
                if (ack.get('status') == 'REJECTED'
                        and str(ack.get('reason', '')).startswith('arena_battle_queue_revision_changed:')):
                    # Queue rejects this before binding or invoking the action.
                    # Bound continuous churn, then close only our untouched
                    # arena page; cleanup has its own short deadline.
                    if revision_retry_deadline is None:
                        revision_retry_deadline = self.clock() + 10.0
                    self.wait()
                    continue
                submitted = True
                if ack.get('status') != 'COMPLETED':
                    return result('UNKNOWN' if operation.get('mutation_attempted') else 'BLOCKED',
                                  ack.get('reason') or 'arena_battle_command_rejected')
                owned_operations.add(operation_id)
                revision_retry_deadline = None
                if action == 'cancel':
                    cancel_submitted = True
            self.wait()
        return result('UNKNOWN' if submitted and not closed else 'UNAVAILABLE',
                      observation_reason or 'arena_battle_timeout')
