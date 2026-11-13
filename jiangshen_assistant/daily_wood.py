"""Drive the stock free fast challenge for daily activity #22."""
from __future__ import annotations

from dataclasses import dataclass
import math
import re
import threading
import time
import uuid

from .objects import CampaignAdmissionClosed


ACTIONS = ('open', 'fast', 'claim_reward', 'close_reward', 'dismiss_prompt', 'close_result', 'close_panel')
_STATUSES = ('IDLE', 'OPENING', 'READY', 'PENDING', 'RESULT', 'CLOSING', 'COMPLETED', 'BLOCKED', 'UNKNOWN',
             'CLAIMING', 'REWARD', 'REFRESHING', 'PROMPT')
_ACK_PHASES = {
    'open': {'OPENING', 'READY', 'BLOCKED'},
    'fast': {'PENDING', 'RESULT', 'READY', 'BLOCKED'},
    'close_result': {'CLOSING', 'READY', 'PROMPT'},
    'claim_reward': {'CLAIMING', 'REWARD'},
    'close_reward': {'REFRESHING', 'READY', 'CLOSING'},
    'dismiss_prompt': {'CLOSING', 'READY'},
    'close_panel': {'CLOSING', 'COMPLETED', 'BLOCKED'},
}
_TRANSITIONS = {
    'OPENING': {'OPENING', 'READY', 'BLOCKED', 'UNKNOWN'},
    'READY': {'READY', 'UNKNOWN'},
    'PENDING': {'PENDING', 'RESULT', 'READY', 'BLOCKED', 'UNKNOWN'},
    'RESULT': {'RESULT', 'UNKNOWN'},
    'CLOSING': {'CLOSING', 'READY', 'PROMPT', 'COMPLETED', 'BLOCKED', 'UNKNOWN'},
    'CLAIMING': {'CLAIMING', 'REWARD', 'UNKNOWN'},
    'REWARD': {'REWARD', 'UNKNOWN'},
    'REFRESHING': {'REFRESHING', 'READY', 'CLOSING', 'UNKNOWN'},
    'PROMPT': {'PROMPT', 'UNKNOWN'},
}
_FLAGS = ('mutation_attempted', 'task_confirmed', 'reward_confirmed', 'reward_pending', 'cleanup_pending', 'busy')
_COUNTERS = ('free_attempts', 'rewards_claimed')


def quota_epoch(now, day):
    if not _uint(day, lower=1) or not _uint(now) or not day <= now < day + 86400:
        return None
    cutoff = day + 72000
    current = cutoff if now >= cutoff else cutoff - 86400
    return current if _uint(current, lower=1) else None


def _uint(value, *, lower=0, upper=0xFFFFFFFF):
    return type(value) is int and lower <= value <= upper


def _role(value):
    return (isinstance(value, str) and re.fullmatch('[1-9][0-9]{0,19}', value) is not None
            and int(value) <= 0xFFFFFFFFFFFFFFFF)


def valid_snapshot(s):
    if (not isinstance(s, dict) or not _uint(s.get('schema'), lower=1, upper=1)
            or not _uint(s.get('revision'), lower=1, upper=999999999)
            or not (s.get('role_id') == '0' or _role(s.get('role_id')))
            or not isinstance(s.get('operation_id'), str)
            or (s['operation_id'] and re.fullmatch('[0-9a-f]{32}', s['operation_id']) is None)
            or s.get('status') not in _STATUSES or not isinstance(s.get('reason'), str)
            or s.get('next_action') not in ('', *ACTIONS)
            or any(type(s.get(key)) is not bool for key in _FLAGS)):
        return False
    for key, minimum, maximum in (('activity_count', 0, 1), ('fast_quota', 0, 0xFFFFFFFF),
                                   ('refresh_cycle', 1, 0xFFFFFFFF), ('quota_epoch', 1, 0xFFFFFFFF)):
        if key not in s or s[key] is not None and not _uint(s[key], lower=minimum, upper=maximum):
            return False
    # Older bridge observations remain readable, but cannot authorize the new
    # repeated flow without per-operation receipt counters.
    if any(key in s for key in _COUNTERS) and (
            not all(_uint(s.get(key)) for key in _COUNTERS)
            or s['task_confirmed'] != (s['free_attempts'] > 0)
            or s['reward_confirmed'] != (s['rewards_claimed'] > 0)):
        return False
    return (not s['task_confirmed'] or s['mutation_attempted'] and bool(s['operation_id'])) and (
        not s['cleanup_pending'] or s['busy'] and bool(s['operation_id'])) and (
        not s['reward_confirmed'] or s['mutation_attempted'] and bool(s['operation_id'])
        and (all(key in s for key in _COUNTERS) or not s['reward_pending']))


def invocation_matches(gui, sequence):
    return (isinstance(gui, dict) and _uint(gui.get('command_seq'), lower=sequence, upper=sequence)
            and gui.get('phase') == 'completed' and gui.get('invocation_attempted') is True)


def echo_matches(echo, fields, sequence, *, invoked=True):
    return (isinstance(echo, dict) and all(echo.get(key) == value and type(echo[key]) is type(value)
                for key, value in fields.items())
            and _uint(echo.get('command_seq'), lower=sequence, upper=sequence)
            and echo.get('invocation_attempted') is invoked)


def follows(previous, s):
    if s['revision'] <= previous['revision']:
        return s == previous
    return (all(s[key] == previous[key] for key in ('role_id', 'refresh_cycle', 'quota_epoch', 'operation_id'))
            and s['status'] in _TRANSITIONS.get(previous['status'], {previous['status']})
            and all(not previous[key] or s[key] for key in ('mutation_attempted', 'task_confirmed', 'reward_confirmed'))
            and all((key in s) == (key in previous) and s.get(key, 0) >= previous.get(key, 0) for key in _COUNTERS)
            and (previous['activity_count'] != 1 or s['activity_count'] == 1))


def _clean_open_rejection(payload, state, before, fields, sequence):
    if (not isinstance(payload, dict) or not isinstance(state, dict)
            or state.get('lifecycle') in ('recovery_unknown', 'stopping', 'stopped')
            or payload.get('state') in ('recovery_unknown', 'stopping', 'stopped')
            or not _uint(state.get('last_command_seq'), lower=sequence, upper=sequence)
            or not echo_matches(payload.get('daily_wood_command'), fields, sequence, invoked=False)):
        return False
    ack_gui, current_gui = payload.get('campaign_gui'), state.get('campaign_gui')
    if (not isinstance(ack_gui, dict) or not isinstance(current_gui, dict)
            or not _uint(ack_gui.get('command_seq'), lower=sequence, upper=sequence)
            or ack_gui.get('invocation_attempted') is not False
            or ack_gui.get('phase') not in ('none', 'not_invoked')):
        return False
    if current_gui == ack_gui:
        gui_matches = True
    else:
        # Pre-queue rejection has a synthesized ACK GUI for this sequence.
        # The state can still describe the preceding, already finished GUI.
        gui_matches = (ack_gui['phase'] == 'none'
                       and _uint(current_gui.get('command_seq'), upper=sequence - 1)
                       and current_gui.get('phase') in ('none', 'completed', 'rejected', 'not_invoked')
                       and type(current_gui.get('invocation_attempted')) is bool)
    if not gui_matches:
        return False
    previous = before
    for projection in (payload, state):
        current = projection.get('daily_wood')
        if (projection.get('daily_wood_enabled') is not True or not valid_snapshot(current)
                or current['status'] not in ('IDLE', 'BLOCKED') or current['next_action'] not in ('', 'open')
                or current['operation_id'] or any(current[key] for key in _FLAGS)
                or any(current[key] != before[key] for key in ('role_id', 'refresh_cycle', 'quota_epoch', 'activity_count'))
                or current['revision'] < previous['revision']
                or current['revision'] == previous['revision'] and current != previous):
            return False
        previous = current
    return True


@dataclass(frozen=True)
class DailyWoodResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    task_confirmed: bool = False
    mutation_attempted: bool = False
    cleanup: str = 'NOT_NEEDED'
    checklist_complete: bool = False
    remaining_quota: int | None = None
    free_attempts: int = 0
    rewards_claimed: int = 0
    reward_pending: bool = False


class DailyWoodExecutor:
    """Drain fresh free quotes while retaining one stock page and operation."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=22.0, clock=time.monotonic):
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError('daily_wood_timeout_invalid')
        self.objects, self.token = objects, token
        self.stopped, self.paused, self.clock = stopped, paused, clock
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout = timeout
        self._lock = threading.Lock()
        self._used = False
        self._last_result = None

    def execute(self, task_id, *, character_id, expected_cycle, expected_quota_epoch,
                expected_current, expected_required):
        if type(task_id) is not int or task_id != 22:
            raise ValueError('daily_wood_task_unsupported')
        if (not _role(character_id) or not _uint(expected_cycle, lower=1)
                or not _uint(expected_quota_epoch, lower=1)
                or not _uint(expected_required, lower=1, upper=1)
                or not _uint(expected_current, upper=1)):
            return DailyWoodResult('BLOCKED', 'wood_expected_identity_or_progress_invalid')
        if not self._lock.acquire(blocking=False):
            return DailyWoodResult('UNKNOWN', 'wood_previous_attempt', cleanup='UNVERIFIED')
        try:
            if self._used:
                return (self._last_result if self._last_result and self._last_result.status == 'UNKNOWN'
                        else DailyWoodResult('BLOCKED', 'wood_executor_used'))
            self._used = True
            self._last_result = self._run(character_id, expected_cycle, expected_quota_epoch, expected_current)
            return self._last_result
        finally:
            self._lock.release()

    def _run(self, role, cycle, epoch, baseline):
        operation = uuid.uuid4().hex
        stage, sequence, quota = '', 0, None
        submitted = confirmed = mutation = False
        attempts = rewards = quote_attempts = quote_rewards = 0
        kind = ''
        previous = None
        snapshot = {}
        deadline = self.clock() + self.timeout
        initial_deadline = min(deadline, self.clock() + 5.0)

        def result(status, reason, *, clean=False, hold=False):
            remaining = snapshot.get('fast_quota')
            complete = clean and remaining == 0 and snapshot.get('reward_pending') is False and reason in (
                'wood_no_free_quota', 'wood_free_fast_complete', 'wood_free_quota_exhausted', 'wood_reward_claimed')
            return DailyWoodResult(status, reason, baseline, 1 if confirmed else baseline, 1,
                                   confirmed, mutation,
                                   'VERIFIED_CLOSED' if clean else 'UNVERIFIED' if submitted or hold else 'NOT_NEEDED',
                                   complete, remaining, attempts, rewards, snapshot.get('reward_pending') is True)

        def fail(reason):
            return result('UNKNOWN' if submitted else 'BLOCKED', reason)

        def owned(s):
            return (s['role_id'] == role and s['refresh_cycle'] == cycle and s['quota_epoch'] == epoch
                    and s['operation_id'] == operation)

        def quote_matches(s):
            value = s['fast_quota']
            done, taken = s['free_attempts'], s['rewards_claimed']
            if kind == 'claim_reward':
                if done != quote_attempts or taken not in (quote_rewards, quote_rewards + 1):
                    return False
                if s['status'] in ('CLAIMING', 'REWARD', 'REFRESHING') and taken != quote_rewards:
                    return False
                if s['status'] in ('READY', 'CLOSING', 'COMPLETED') and taken != quote_rewards + 1:
                    return False
                if taken == quote_rewards + 1:
                    return not s['reward_pending'] and _uint(value)
                # closeRewardResult sends 612. Its authoritative player can
                # replace the stale quote before the refresh is fully settled.
                return (quota is not None and (s['status'] == 'REFRESHING' and not s['next_action']
                                              or value == quota))
            if kind == 'fast':
                if taken != quote_rewards or done not in (quote_attempts, quote_attempts + 1):
                    return False
                if s['status'] in ('RESULT', 'PROMPT', 'COMPLETED') and done != quote_attempts + 1:
                    return False
                if s['status'] == 'PENDING' and done != quote_attempts:
                    return False
                return (quota is not None and (value == quota - 1 if done == quote_attempts + 1
                        else value == quota if s['status'] in ('READY', 'BLOCKED')
                        else value in (quota, quota - 1)))
            return done == taken == 0

        def evidence(s):
            return (s['activity_count'] in (0, 1)
                    and (s['task_confirmed'] or s['activity_count'] == baseline or s['status'] == 'PENDING')
                    and all(key in s for key in _COUNTERS)
                    and (not s['task_confirmed'] or s['activity_count'] == 1)
                    and s['free_attempts'] >= attempts and s['rewards_claimed'] >= rewards
                    and quote_matches(s))

        while True:
            if self.stopped():
                return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
            if not submitted and self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                continue
            timeout_reason = 'wood_readback_timeout' if submitted else 'wood_admission_timeout'
            if self.clock() >= deadline:
                return fail(timeout_reason)
            try:
                read = self.objects.observe_daily_wood(self.token)
            except Exception:
                if self.stopped():
                    return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
                return fail(timeout_reason if self.clock() >= deadline else 'wood_observation_failed')
            enabled = read.get('enabled') if isinstance(read, dict) else None
            s = read.get('wood') if enabled is True else None
            valid = valid_snapshot(s)
            if valid:
                snapshot = s
            monotonic = valid and (previous is None or follows(previous, s))
            bound = bool(stage and monotonic and owned(s) and evidence(s))
            if bound:
                mutation |= s['mutation_attempted']
                confirmed |= s['task_confirmed']
                attempts, rewards = s['free_attempts'], s['rewards_claimed']
            if self.stopped():
                return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
            if self.clock() >= deadline:
                return fail(timeout_reason)
            if enabled is False:
                return fail('wood_disabled_during_operation') if submitted else result('UNAVAILABLE', 'wood_disabled')
            if not valid or not monotonic:
                return fail('wood_observation_invalid_or_regressed')
            status, offer, reason = s['status'], s['next_action'], s['reason']
            if not stage:
                if status == 'UNKNOWN' or s['busy'] or s['cleanup_pending']:
                    return result('UNKNOWN', reason or 'wood_prior_operation_unresolved', hold=True)
                if not all(key in s for key in _COUNTERS):
                    return result('BLOCKED', 'wood_receipt_counters_unavailable')
                if (s['role_id'], s['refresh_cycle'], s['quota_epoch'], s['activity_count']) != (role, cycle, epoch, baseline):
                    return fail('wood_expected_identity_or_progress_changed')
                if status == 'IDLE' and reason == 'wood_unobserved' and not offer and self.clock() < initial_deadline:
                    self.wait()
                    continue
                if (offer != 'open' or status not in ('IDLE', 'BLOCKED', 'COMPLETED')
                        or status == 'COMPLETED' and not (s['task_confirmed'] or s['reward_confirmed'])):
                    return result('UNAVAILABLE', reason or 'wood_open_unavailable')
            else:
                if not bound:
                    changed_quote = (owned(s) and all(key in s for key in _COUNTERS) and not quote_matches(s))
                    return fail('wood_free_quota_or_receipt_changed' if changed_quote else 'wood_operation_or_evidence_changed')
                if status == 'UNKNOWN':
                    return fail(reason or 'wood_operation_unresolved')
                clean = not s['busy'] and not s['cleanup_pending']
                if status == 'COMPLETED':
                    if (stage == 'close_panel' and clean and (attempts or rewards) and mutation and offer in ('', 'open')
                            and s['fast_quota'] == 0 and not s['reward_pending']):
                        return result('COMPLETED', reason or 'wood_progress_and_cleanup_verified', clean=True)
                    return fail('wood_completion_unverified')
                if status == 'BLOCKED' and clean:
                    if ((stage == 'close_panel' or stage == 'open' and not mutation)
                            and s['activity_count'] == (1 if confirmed else baseline)):
                        if reason == 'wood_no_free_quota' and s['fast_quota'] == 0:
                            return result('COMPLETED', reason, clean=True)
                        return result('UNAVAILABLE', reason or 'wood_free_challenge_unavailable', clean=True)
                    return fail('wood_cleanup_or_progress_unverified')
                allowed = (
                    stage == 'open' and status == 'READY' and offer == 'fast'
                    and _uint(s['fast_quota'], lower=1) and not s['reward_pending'] and not mutation
                    or stage == 'open' and status == 'READY' and offer == 'claim_reward'
                    and _uint(s['fast_quota']) and s['reward_pending'] and not mutation
                    or stage in ('close_result', 'dismiss_prompt', 'close_reward') and status == 'READY'
                    and (stage == 'close_reward' and rewards == quote_rewards + 1
                         or stage != 'close_reward' and attempts == quote_attempts + 1)
                    and (offer == 'fast' and _uint(s['fast_quota'], lower=1) and not s['reward_pending']
                         or offer == 'claim_reward' and _uint(s['fast_quota']) and s['reward_pending'])
                    or stage == 'fast' and status == 'READY' and offer == 'claim_reward'
                    and s['reward_pending'] and attempts == quote_attempts
                    or stage == 'fast' and status == 'RESULT' and offer == 'close_result'
                    and attempts == quote_attempts + 1
                    or stage == 'close_result' and status == 'CLOSING' and offer == 'close_panel'
                    and attempts == quote_attempts + 1
                    or stage == 'close_result' and status == 'PROMPT' and offer == 'dismiss_prompt'
                    and attempts == quote_attempts + 1
                    or stage == 'dismiss_prompt' and status == 'CLOSING' and offer == 'close_panel'
                    and attempts == quote_attempts + 1
                    or stage == 'claim_reward' and status == 'REWARD' and offer == 'close_reward' and mutation
                    or stage == 'close_reward' and status == 'CLOSING' and offer == 'close_panel'
                    and rewards == quote_rewards + 1
                    or stage == 'open' and status == 'BLOCKED' and offer == 'close_panel' and not mutation
                    or stage == 'fast' and status == 'BLOCKED' and offer == 'close_panel' and attempts == quote_attempts
                )
                if offer and (not allowed or not s['cleanup_pending'] or not s['busy']):
                    return fail('wood_unexpected_action_or_quota')

            previous = s
            if not offer or self.paused():
                self.wait()
                continue
            if self.stopped():
                return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
            if self.clock() >= deadline:
                return fail(timeout_reason)
            fields = dict(action=offer, expected_revision=s['revision'], character_id=role, task_id=22, operation_id=operation)
            try:
                wire = self.objects.submit_daily_wood(self.token, **fields, admission=lambda: self.clock() < deadline)
            except CampaignAdmissionClosed:
                if self.stopped():
                    return fail('user_stop') if submitted else result('STOPPED', 'user_stop')
                if self.clock() >= deadline:
                    return fail(timeout_reason)
                if self.paused():
                    continue
                return fail('wood_command_admission_closed')
            except Exception:
                submitted = True
                return fail('wood_command_outcome_unknown')
            if offer in ('fast', 'claim_reward'):
                quota = s['fast_quota']
                kind = offer
                quote_attempts, quote_rewards = attempts, rewards
            submitted = True
            ack = wire.get('ack') if isinstance(wire, dict) else None
            sent = wire.get('requested_seq') if isinstance(wire, dict) else None
            if (not isinstance(ack, dict) or not _uint(sent, lower=sequence + 1, upper=999999999)
                    or not _uint(ack.get('command_seq'), lower=sent, upper=sent)
                    or ack.get('command_name') != 'daily-wood-command'):
                return fail('wood_command_binding_invalid')
            if ack.get('status') != 'COMPLETED':
                why = ack.get('reason')
                if (ack.get('status') == 'REJECTED' and not stage and offer == 'open'
                        and _clean_open_rejection(ack.get('result'), wire.get('state'), s, fields, sent)):
                    submitted = False
                    if self.stopped():
                        return result('STOPPED', 'user_stop')
                    if self.clock() >= deadline:
                        return fail(timeout_reason)
                    return result('BLOCKED', why if isinstance(why, str) and why else 'wood_open_not_invoked')
                return fail(why if isinstance(why, str) and why else 'wood_command_unconfirmed')
            payload = ack.get('result')
            receipt = payload.get('daily_wood') if isinstance(payload, dict) and payload.get('daily_wood_enabled') is True else None
            if (not valid_snapshot(receipt) or not owned(receipt)
                    or receipt['revision'] <= s['revision'] or receipt['status'] not in _ACK_PHASES[offer]
                    or receipt['mutation_attempted'] != (mutation or offer in ('fast', 'claim_reward'))
                    or not invocation_matches(payload.get('campaign_gui'), sent)
                    or not echo_matches(payload.get('daily_wood_command'), fields, sent)):
                return fail('wood_command_binding_invalid')
            # The invocation is already proven even if the next read fails.
            # Progress and closure still require a fresh authoritative sample.
            mutation |= receipt['mutation_attempted']
            if self.stopped():
                return fail('user_stop')
            if self.clock() >= deadline:
                return fail(timeout_reason)
            if not evidence(receipt) or confirmed and not receipt['task_confirmed']:
                return fail('wood_command_binding_invalid')
            if 'state' in wire:
                state = wire['state']
                latest = state.get('daily_wood') if isinstance(state, dict) and state.get('daily_wood_enabled') is True else None
                if (not valid_snapshot(latest) or not owned(latest) or not evidence(latest) or not follows(receipt, latest)
                        or not invocation_matches(state.get('campaign_gui'), sent)):
                    return fail('wood_command_state_inconsistent')
                receipt = latest
            stage, sequence, previous = offer, sent, receipt
            deadline = self.clock() + self.timeout
