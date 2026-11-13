"""Claim audited free event rewards using the stock client daily-script route."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid

from .objects import CampaignAdmissionClosed


TASKS = (201, 202, 203)
# Fixed native variants: free event claims without online draws, and online only.
SELECTED_TASKS = (204, 205)
ACTIONS = frozenset(('open', 'claim', 'close_reward', 'close_message', 'close'))


@dataclass(frozen=True)
class EventRewardItem:
    key: str
    name: str
    status: str
    reason: str = ''
    current: int | None = None
    limit: int | None = None
    remaining_seconds: int | None = None
    visibility: str = 'UNOBSERVED'
    resource_type: int | None = None
    resource_cost: int | None = None
    resource_available: int | None = None


@dataclass(frozen=True)
class DailyEventRewardsResult:
    status: str
    reason: str
    items: tuple[EventRewardItem, ...] = ()
    claimed_keys: tuple[str, ...] = ()
    mutation_attempted: bool = False
    task_confirmed: bool = False
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    claim_count: int = 0
    processed_without_reply: bool = False
    page_state_confirmed: bool = False
    request_issued: bool = False
    reply_received: bool = False
    entry_unconfirmed: bool = False
    claim_request_issued: bool = False


class DailyEventRewardsExecutor:
    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=260.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.clock = timeout, clock

    def execute(self, task_id, *, character_id, expected_cycle=None):
        if task_id not in TASKS + SELECTED_TASKS:
            raise ValueError('daily_event_rewards_task_unsupported')
        operation_id = uuid.uuid4().hex
        latest = {}
        submitted = False
        confirmed_count = 0
        deadline = self.clock() + self.timeout
        readiness_deadline = self.clock() + min(10.0, self.timeout)

        def result(status, reason):
            items = tuple(EventRewardItem(row['key'], row['name'], row['status'], row.get('reason', ''),
                          row.get('current') if type(row.get('current')) is int and row['current'] >= 0 else None,
                          row.get('limit') if type(row.get('limit')) is int and row['limit'] >= 0 else None,
                          row.get('remaining_seconds') if type(row.get('remaining_seconds')) is int and row['remaining_seconds'] >= 0 else None,
                          row.get('visibility', 'UNOBSERVED'),
                          row.get('resource_type') if type(row.get('resource_type')) is int and row['resource_type'] >= 0 else None,
                          row.get('resource_cost') if type(row.get('resource_cost')) is int and row['resource_cost'] >= 0 else None,
                          row.get('resource_available') if type(row.get('resource_available')) is int and row['resource_available'] >= 0 else None)
                          for row in latest.get('rows', ()) if isinstance(row, dict)
                          and all(isinstance(row.get(key), str) for key in ('key', 'name', 'status')))
            claimed = tuple(key for key in latest.get('claimed_keys', ()) if isinstance(key, str))
            held = latest.get('cleanup_pending', submitted) or status == 'UNKNOWN' and submitted
            return DailyEventRewardsResult(status, reason, items, claimed,
                latest.get('mutation_attempted') is True, latest.get('task_confirmed') is True,
                'UNVERIFIED' if held else 'VERIFIED_CLOSED' if submitted else 'NOT_NEEDED',
                operation_id if submitted else '',
                latest.get('claim_count') if type(latest.get('claim_count')) is int and latest['claim_count'] >= 0 else len(claimed),
                latest.get('processed_without_reply') is True,
                latest.get('page_state_confirmed') is True,
                latest.get('request_issued') is True,
                latest.get('reply_received') is True,
                latest.get('entry_unconfirmed') is True,
                latest.get('claim_request_issued') is True)

        while self.clock() < deadline:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                readiness_deadline = self.clock() + min(10.0, self.timeout)
                continue
            try:
                observed = self.objects.observe_daily_script(self.token, task_id)
            except Exception as exc:
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', f'event_rewards_observation_failed ({exc})')
            if (not isinstance(observed, dict) or observed.get('task_id') != task_id
                    or type(observed.get('revision')) is not int or not isinstance(observed.get('rows'), list)):
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'event_rewards_observation_invalid')
            latest = observed
            if latest.get('role_id') != character_id:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'event_rewards_role_changed')
            cycle = latest.get('cycle')
            if expected_cycle is None:
                expected_cycle = cycle
            if type(cycle) is not int or cycle <= 0 or cycle != expected_cycle:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'event_rewards_cycle_changed')
            if submitted and latest.get('operation_id') != operation_id:
                return result('UNKNOWN', 'event_rewards_operation_changed')
            if submitted:
                count = latest.get('claim_count', len(latest.get('claimed_keys', ())))
                if type(count) is not int or count < confirmed_count:
                    return result('UNKNOWN', 'event_rewards_claim_progress_invalid')
                if count > confirmed_count:
                    # A long sequence of immediately eligible rewards gets a
                    # fresh bounded settlement window after proven progress.
                    # Polling, retries and cooldowns never extend this deadline.
                    confirmed_count = count
                    deadline = self.clock() + self.timeout
            status, action = latest.get('status'), latest.get('next_action')
            if status in ('UNKNOWN', 'STOPPED'):
                return result(status, latest.get('reason') or 'event_rewards_stopped')
            if (status == 'UNAVAILABLE' and submitted and latest.get('entry_unconfirmed') is True
                    and latest.get('claim_request_issued') is False
                    and latest.get('cleanup_pending') is False and latest.get('reward_closed') is True):
                return result('UNAVAILABLE', 'event_rewards_online_entry_unconfirmed_claim_not_issued')
            if status == 'PROCESSED' and submitted:
                if (latest.get('processed_without_reply') is True and latest.get('request_issued') is True
                        and latest.get('claim_request_issued') is True and latest.get('entry_unconfirmed') is not True
                        and latest.get('reply_received') is False
                        and latest.get('cleanup_pending') is False and latest.get('reward_closed') is True):
                    return result('PROCESSED', 'event_rewards_online_no_reply_processed')
                return result('UNKNOWN', 'event_rewards_processed_release_unconfirmed')
            if status == 'COMPLETED' and submitted:
                if latest.get('cleanup_pending') is not False or latest.get('reward_closed') is not True:
                    return result('UNKNOWN', 'event_rewards_cleanup_unconfirmed')
                if any(row.get('status') == 'AVAILABLE' for row in latest['rows']):
                    return result('UNKNOWN', 'event_rewards_claims_remaining_after_close')
                # Known paid-only shops are EXCLUDED, not an unverified free
                # branch. Only unresolved free-route coverage makes PARTIAL.
                unsupported = any(row.get('status') in ('UNSUPPORTED', 'UNOBSERVED', 'UNAVAILABLE')
                                  and row.get('visibility') != 'HIDDEN' for row in latest['rows'])
                cooldown = any(row.get('status') == 'COOLDOWN' for row in latest['rows'])
                return result('PARTIAL' if unsupported or cooldown else 'COMPLETED',
                              'event_rewards_online_cooldown_remaining' if cooldown else
                              'event_rewards_supported_routes_processed' if unsupported else
                              latest.get('reason') or 'event_rewards_processed')
            gate = latest.get('gate')
            if gate and not action:
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', gate)
            if not action:
                if not submitted and latest.get('entry_gate'):
                    return result('UNAVAILABLE', latest['entry_gate'])
                if not submitted and self.clock() >= readiness_deadline:
                    return result('UNAVAILABLE', 'event_rewards_network_busy' if latest.get('network_idle') is False
                                  else 'event_rewards_not_ready')
                self.wait()
                continue
            if action not in ACTIONS or latest.get('gold_cost') != 0:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'event_rewards_action_invalid')
            if self.stopped() or self.paused():
                continue
            try:
                wire = self.objects.submit_daily_script(self.token, action, task_id=task_id,
                    character_id=character_id, operation_id=operation_id, expected_revision=latest['revision'])
            except CampaignAdmissionClosed:
                if self.stopped() or self.paused():
                    continue
                return result('UNKNOWN' if submitted else 'BLOCKED', 'event_rewards_admission_closed')
            except Exception as exc:
                submitted = True
                return result('UNKNOWN', f'event_rewards_command_failed ({exc})')
            ack = wire.get('ack') or {}
            receipt = (ack.get('result') or {}).get('daily_script')
            if (ack.get('status') == 'REJECTED' and ack.get('command_seq') == wire.get('requested_seq')
                    and ack.get('reason') in ('event_rewards_admission', 'event_rewards_action_changed')):
                self.wait()
                continue
            submitted = True
            if ack.get('status') != 'COMPLETED':
                return result('UNKNOWN', ack.get('reason') or 'event_rewards_command_unconfirmed')
            if (not isinstance(receipt, dict) or receipt.get('operation_id') != operation_id
                    or receipt.get('task_id') != task_id or receipt.get('role_id') != character_id
                    or ack.get('command_seq') != wire.get('requested_seq')):
                return result('UNKNOWN', 'event_rewards_command_binding_invalid')
            latest = receipt
            self.wait()
        return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'event_rewards_timeout')

    def execute_all(self, *, character_id, tasks=TASKS, expected_cycle=None):
        results = []
        for task_id in tasks:
            current = self.execute(task_id, character_id=character_id, expected_cycle=expected_cycle)
            results.append(current)
            if current.status in ('UNKNOWN', 'STOPPED') or current.cleanup == 'UNVERIFIED':
                break
        return results
