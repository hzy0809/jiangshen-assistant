"""Execute one guild daily activity under the caller's exclusive bridge owner."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid

from .objects import CampaignAdmissionClosed
from .resource_policy import ResourcePolicyDenied
from .daily import valid_observation


@dataclass(frozen=True)
class DailyGuildResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    receipt_opcode: int = 0
    receipt_result: int | None = None
    closed: bool = False
    processed_without_reply: bool = False
    page_confirmed: bool = False
    request_issued: bool = False
    reply_received: bool = False
    cleanup: str = 'NOT_NEEDED'


@dataclass(frozen=True)
class _Operation:
    character_id: str
    guild_id: str
    task_id: int
    operation_id: str
    donation_type: int
    max_premium: int

    def matches(self, observed):
        return (observed is not None and observed['operation_id'] == self.operation_id
                and observed['task_id'] == self.task_id
                and observed['donation_type'] == self.donation_type
                and observed['max_premium'] == self.max_premium)

    def submit(self, objects, token, action, revision):
        return objects.submit_daily_guild(token, action, expected_revision=revision,
            character_id=self.character_id, guild_id=self.guild_id, task_id=self.task_id,
            operation_id=self.operation_id, donation_type=self.donation_type,
            max_premium=self.max_premium)


def _accepted_receipt(operation):
    opcode, result = operation['receipt_opcode'], operation['receipt_result']
    if operation['invoked'] is not True or type(result) is not int:
        return False
    if operation['task_id'] == 20:
        return opcode == 301 and result == 1
    return opcode == 302 and 0 <= result <= 2147483647 and result != 255


def _confirmed(operation):
    if (operation['status'] == 'COMPLETED' and operation.get('page_confirmed') is True
            and operation.get('request_issued') is True):
        return True
    before, after = operation['progress_before'], operation['progress_after']
    return (operation['status'] == 'COMPLETED' and _accepted_receipt(operation)
            and type(before) is int and type(after) is int and 0 <= before < after <= 255
            and after == before + 1)


def _next_action(observed, target, sent, failure):
    """An action needs both the native offer and the matching Python route."""
    phase, actions = observed['phase'], observed['actions']
    if observed['gate'] != 'PASS':
        return None
    if 'open' not in sent:
        return 'open' if phase == 'MAIN' and 'open' in actions else None
    if 'close' in sent:
        return None
    operation = observed['operation']
    status = operation['status']
    mutation = 'donate' if target.task_id == 20 else 'lottery'
    panel = mutation + '_panel'
    if (not failure and phase == 'REWARD' and mutation in sent
            and status in ('PENDING', 'COMPLETED') and (_accepted_receipt(operation) or operation.get('page_confirmed') is True)
            and 'reward_confirm' in actions and 'reward_confirm' not in sent):
        return 'reward_confirm'
    if 'close' in actions and phase in ('GUILD', mutation.upper(), 'WAITING'):
        if status in ('COMPLETED', 'PROCESSED', 'REJECTED') or failure and status == 'OPEN':
            return 'close'
    if status != 'OPEN' or operation['invoked'] or failure:
        return None
    if phase == 'GUILD' and panel in actions and panel not in sent:
        return panel
    if (phase == mutation.upper() and panel in sent and mutation not in sent):
        if mutation in actions:
            return mutation
        if 'close' in actions:
            return 'close'
    if (phase == 'PREMIUM' and target.max_premium == 80 and mutation in sent
            and 'premium_confirm' in actions and 'premium_confirm' not in sent):
        return 'premium_confirm'
    return None


class DailyGuildExecutor:
    """Drive a fixed route, then verify the matched receipt, progress and close."""

    def __init__(self, objects, token, *, donation_type=2, stopped=lambda: False,
                 paused=lambda: False, wait=None, timeout=25.0, clock=time.monotonic):
        if type(donation_type) is not int or donation_type not in (1, 2, 3):
            raise ValueError('daily_donation_type_unsupported')
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait if wait is not None else lambda: time.sleep(.1)
        self.timeout, self.clock = timeout, clock
        self._donation_type = donation_type
        self._unresolved = False

    def execute(self, task_id: int, *, character_id: str, expected_cycle: int | None = None) -> DailyGuildResult:
        if type(task_id) is not int or task_id not in (18, 20):
            raise ValueError('daily_guild_task_unsupported')
        if expected_cycle is not None and (type(expected_cycle) is not int or expected_cycle <= 0):
            raise ValueError('daily_guild_cycle_invalid')
        if self._unresolved:
            return DailyGuildResult('UNKNOWN', 'guild_previous_operation_unresolved')

        mutation = 'donate' if task_id == 20 else 'lottery'
        target = operation = None
        prior_operation_id = None
        failure = None
        open_wait_reason = None
        open_retry_revision = None
        last_revision = 0
        sent = set()
        deadline = self.clock() + self.timeout

        def result(status, reason, *, closed=False):
            if status == 'UNKNOWN':
                self._unresolved = True
            if operation is not None:
                after = operation.get('observed_progress') if operation.get('page_confirmed') or operation.get('processed_without_reply') else operation['progress_after']
                return DailyGuildResult(status, reason, operation['progress_before'], after,
                    operation['receipt_opcode'], operation['receipt_result'], closed,
                    operation.get('processed_without_reply') is True, operation.get('page_confirmed') is True,
                    operation.get('request_issued', operation['invoked']) is True,
                    operation.get('reply_received', type(operation['receipt_result']) is int) is True,
                    'VERIFIED_CLOSED' if closed else 'UNVERIFIED' if 'open' in sent else 'NOT_NEEDED')
            return DailyGuildResult(status, reason, closed=closed,
                cleanup='UNVERIFIED' if 'open' in sent and not closed else 'NOT_NEEDED')

        while True:
            if 'open' not in sent and self.stopped():
                return result('STOPPED', 'user_stop')
            try:
                observed = self.objects.observe_daily_guild(self.token)
            except Exception as exc:
                status = 'UNKNOWN' if 'open' in sent else 'BLOCKED'
                return result(status, f'guild_observation_failed ({exc})')
            latest = observed['operation']
            revision = observed['revision']
            if (observed['character_id'] != character_id or revision < last_revision
                    or target is not None and observed['guild_id'] != target.guild_id):
                status = 'UNKNOWN' if 'open' in sent else 'BLOCKED'
                return result(status, 'guild_observation_identity_or_revision_changed')
            last_revision = revision
            if observed['gate'] == 'UNKNOWN' or latest is not None and latest['status'] == 'UNKNOWN':
                return result('UNKNOWN', (latest or {}).get('reason') or observed['reason']
                              or 'guild_native_operation_unknown')

            if 'open' in sent:
                if target.matches(latest):
                    if (operation is not None and (operation['invoked'] and not latest['invoked']
                            or operation['closed'] and not latest['closed']
                            or operation['status'] != 'OPEN' and latest['status'] == 'OPEN'
                            or operation['status'] in ('COMPLETED', 'PROCESSED', 'REJECTED')
                            and latest['status'] != operation['status'])):
                        return result('UNKNOWN', 'guild_operation_state_reversed')
                    operation = latest
                elif operation is not None or (latest or {}).get('operation_id') != prior_operation_id:
                    return result('UNKNOWN', 'guild_operation_binding_changed')
            elif latest is not None and not latest['closed']:
                return result('BLOCKED', 'guild_prior_operation_open')

            if operation is not None:
                if operation['invoked'] and mutation not in sent:
                    return result('UNKNOWN', 'guild_request_not_owned_by_executor')
                if operation['status'] == 'COMPLETED' and not _confirmed(operation):
                    return result('UNKNOWN', 'guild_receipt_or_progress_invalid')
                if operation['status'] == 'PROCESSED' and not (
                        operation.get('processed_without_reply') is True and operation.get('request_issued') is True
                        and operation.get('reply_received') is False):
                    return result('UNKNOWN', 'guild_processed_evidence_invalid')
                if operation['closed'] and observed['phase'] == 'MAIN':
                    if operation.get('wrappers_released') is False:
                        if self.stopped():
                            return result('STOPPED', 'user_stop')
                        if self.clock() >= deadline:
                            return result('UNKNOWN', 'guild_callback_release_unverified')
                        self.wait()
                        continue
                    if operation['status'] == 'PROCESSED':
                        return result('PROCESSED', operation['reason'] or 'guild_request_submitted_no_reply', closed=True)
                    if operation['status'] == 'COMPLETED':
                        return result('COMPLETED', operation['reason'] or 'guild_receipt_and_daily_progress_verified', closed=True)
                    if operation['status'] == 'REJECTED':
                        if failure is not None:
                            return result(*failure, closed=True)
                        if self.stopped() and not operation['invoked']:
                            return result('STOPPED', 'user_stop', closed=True)
                        return result('REJECTED', operation['reason'] or 'guild_operation_rejected', closed=True)
                    return result('UNKNOWN', 'guild_closed_operation_inconsistent')

            pending = operation is not None and operation['status'] == 'PENDING'
            if self.stopped():
                if 'open' not in sent:
                    return result('STOPPED', 'user_stop')
                if not pending and 'close' not in sent:
                    return result('UNKNOWN', 'guild_cleanup_unverified_after_stop')
            elif self.paused() and not pending:
                paused_at = self.clock()
                while self.paused() and not self.stopped():
                    self.wait()
                deadline += self.clock() - paused_at
                continue

            if 'open' not in sent and observed['gate'] == 'BLOCKED':
                return result('BLOCKED', observed['reason'] or 'guild_open_blocked')
            if self.clock() >= deadline:
                if 'open' not in sent:
                    return result('BLOCKED', open_wait_reason or observed['reason'] or 'guild_open_timeout')
                return result('UNKNOWN', 'guild_receipt_or_cleanup_timeout')

            if ('open' in sent and (operation is None or operation['closed'])
                    or self.stopped() or self.paused()):
                self.wait()
                continue
            action = _next_action(observed, target, sent, failure)
            if action is None:
                self.wait()
                continue
            if action == 'open':
                # An unreadable activity cache can outlast the preceding panel's
                # close. Observe until it changes, within the original deadline.
                if revision == open_retry_revision:
                    self.wait()
                    continue
                if observed['guild_id'] == '0':
                    return result('BLOCKED', 'guild_membership_required')
                prior_operation_id = (latest or {}).get('operation_id')
                choice = self._donation_type if task_id == 20 else 0
                target = _Operation(character_id, observed['guild_id'], task_id,
                    uuid.uuid4().hex, choice, 80 if choice == 3 else 0)

            # The shared admission lock handles pause/stop races after this check.
            if self.stopped() or self.paused():
                continue
            if expected_cycle is not None and action not in ('close', 'reward_confirm'):
                try:
                    daily = self.objects.observe_daily(self.token)
                except Exception:
                    return result('UNKNOWN' if 'open' in sent else 'BLOCKED', 'guild_retry_cycle_observation_failed')
                if not valid_observation(daily) or not daily['available'] or daily['character_id'] != character_id:
                    return result('UNKNOWN' if 'open' in sent else 'BLOCKED', 'guild_retry_identity_unverified')
                day, now = daily['server_day'], daily['server_now']
                current_cycle = day + 18000 if now >= day + 18000 else day - 68400
                if current_cycle != expected_cycle:
                    return result('UNKNOWN' if 'open' in sent else 'BLOCKED', 'guild_retry_refresh_cycle_changed')
            try:
                wire = target.submit(self.objects, self.token, action, revision)
            except ResourcePolicyDenied as exc:
                failure = ('BLOCKED', str(exc))
                if 'open' not in sent:
                    return result(*failure)
                # No mutation was submitted by this refusal. Preserve any
                # earlier receipt and use only the existing owned close offer.
                if 'close' in observed['actions']:
                    continue
                return result('BLOCKED', str(exc) + '; guild_cancel_capability_unavailable')
            except CampaignAdmissionClosed:
                if self.stopped() or self.paused():
                    continue
                status = 'UNKNOWN' if 'open' in sent else 'BLOCKED'
                return result(status, 'guild_command_admission_closed')
            except Exception as exc:
                return result('UNKNOWN', f'guild_command_outcome_unknown ({exc})')
            ack = wire.get('ack') or {}
            if ack.get('status') == 'REJECTED':
                reason = ack.get('reason') or 'guild_command_rejected'
                if action == 'open':
                    if reason == 'guild_daily_task_unverified' or reason.startswith('guild_daily_task_unverified:'):
                        open_wait_reason = reason
                        open_retry_revision = revision
                        self.wait()
                        continue
                    return result('STOPPED', 'user_stop') if self.stopped() else result('BLOCKED', reason)
                if action == 'close':
                    # A subsequent observation may still prove the page closed.
                    sent.add('close')
                else:
                    failure = ('REJECTED', reason)
            elif ack.get('status') == 'COMPLETED':
                sent.add(action)
                if action == 'close' and operation['status'] == 'OPEN' and failure is None:
                    failure = ('BLOCKED', 'guild_task_action_unavailable')
            else:
                return result('UNKNOWN', ack.get('reason') or wire.get('reason')
                              or 'guild_command_outcome_unknown')
            deadline = self.clock() + self.timeout
