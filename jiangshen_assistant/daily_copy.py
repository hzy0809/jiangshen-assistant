"""Process currently ready ordinary 抄录 actions and return during cooldown."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid


@dataclass(frozen=True)
class DailyTechBookCopyResult:
    status: str
    reason: str
    mutation_attempted: bool = False
    reward_confirmed: bool = False
    stock_callback_returned: bool = False
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    free_used: int | None = None
    free_limit: int | None = None
    free_remaining: int | None = None
    finish_time: int | None = None
    server_now: int | None = None
    next_ready_seconds: int | None = None
    copying: bool | None = None
    copies_completed: int = 0
    refresh_cycle: int | None = None
    availability: str = ''
    processed_without_reply: bool = False
    page_state_confirmed: bool = False
    request_issued: bool = False
    reply_received: bool = False


class DailyTechBookCopyExecutor:
    """Never wait out the game's cooldown while owning the daily UI."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=45.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.clock = timeout, clock

    def execute(self, task_id=303, *, character_id, expected_cycle=None, **_unused):
        if task_id != 303:
            raise ValueError('daily_copy_task_unsupported')
        operation = uuid.uuid4().hex
        submitted = attempted = returned = confirmed = closed = False
        snapshot = {}
        deadline = self.clock() + self.timeout

        def result(status, reason):
            return DailyTechBookCopyResult(
                status, reason, attempted, confirmed, returned,
                'VERIFIED_CLOSED' if submitted and closed else 'UNVERIFIED' if submitted else 'NOT_NEEDED',
                operation if submitted else '',
                *(snapshot.get(key) for key in ('free_used', 'free_limit', 'free_remaining',
                    'finish_time', 'server_now', 'next_ready_seconds', 'copying')),
                snapshot.get('copies_completed', 0) if snapshot.get('operation_id') == operation else 0,
                snapshot.get('refresh_cycle'), snapshot.get('availability', ''),
                snapshot.get('processed_without_reply') is True,
                snapshot.get('page_state_confirmed') is True,
                snapshot.get('request_issued') is True,
                snapshot.get('reply_received') is True,
            )

        while self.clock() < deadline:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                before = self.clock()
                self.wait()
                deadline += self.clock() - before
                continue
            snapshot = self.objects.observe_daily_script(self.token, task_id)
            if (not isinstance(snapshot, dict) or snapshot.get('task_id') != task_id
                    or snapshot.get('route') != 'tech_book_copy'):
                snapshot = {}
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'copy_route_unavailable')
            if snapshot.get('role_id') != character_id:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'copy_character_changed')
            cycle = snapshot.get('refresh_cycle')
            if type(cycle) is not int or cycle <= 0:
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'copy_game_day_unavailable')
            if expected_cycle is None:
                expected_cycle = cycle
            if cycle != expected_cycle:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'copy_game_day_changed')
            if not self._valid_quota(snapshot):
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'copy_quota_unavailable')
            same = snapshot.get('operation_id') == operation
            if submitted and not same:
                self.wait()
                continue
            if same:
                attempted = snapshot.get('mutation_attempted') is True
                returned = snapshot.get('stock_callback_returned') is True
                confirmed = snapshot.get('reward_confirmed') is True
                closed = (snapshot.get('closed') is True and snapshot.get('busy') is False
                          and snapshot.get('cleanup_pending') is False)
                if snapshot.get('status') == 'PROCESSED':
                    if (closed and snapshot.get('processed_without_reply') is True
                            and snapshot.get('request_issued') is True
                            and snapshot.get('reply_received') is False):
                        return result('PROCESSED', 'copy_no_reply_processed')
                    return result('UNKNOWN', 'copy_processed_release_unconfirmed')
                if snapshot.get('status') == 'COMPLETED':
                    if (closed and attempted and (snapshot.get('page_state_confirmed') is True or returned and confirmed)
                            and type(snapshot.get('copies_completed')) is int
                            and snapshot['copies_completed'] > 0):
                        return result('COMPLETED', snapshot.get('reason') or 'copy_free_actions_completed')
                    return result('UNKNOWN', 'copy_completion_evidence_missing')
                if snapshot.get('status') in ('UNKNOWN', 'STOPPED'):
                    return result(snapshot['status'], snapshot.get('reason') or 'copy_unknown')
                if closed and snapshot.get('status') in ('WAITING_COOLDOWN', 'FREE_LIMIT_REACHED', 'CONDITION_NOT_MET'):
                    return result(snapshot['status'], snapshot.get('reason') or 'copy_unavailable')
            if not submitted:
                if snapshot.get('busy'):
                    return result('BLOCKED', 'copy_existing_operation_unresolved')
                # Only present eligibility governs a new run; terminal evidence
                # from a previous operation does not suppress a fresh offer.
                availability = snapshot.get('availability')
                if availability in ('WAITING_COOLDOWN', 'FREE_LIMIT_REACHED', 'CONDITION_NOT_MET'):
                    return result(availability, 'copy_' + availability.lower())
                if availability != 'READY':
                    return result('UNAVAILABLE', snapshot.get('reason') or 'copy_unavailable')
            action = snapshot.get('next_action', '')
            if action:
                if (action not in ('open', 'resume', 'copy_free', 'close')
                        or not submitted and action not in ('open', 'resume')):
                    return result('UNKNOWN' if submitted else 'BLOCKED', 'copy_action_out_of_scope')
                if type(snapshot.get('revision')) is not int:
                    return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'copy_revision_unavailable')
                if self.stopped():
                    return result('STOPPED', 'user_stop')
                if self.paused():
                    continue
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=task_id, character_id=character_id,
                    operation_id=operation, expected_revision=snapshot['revision'])
                ack = wire.get('ack') if isinstance(wire, dict) else None
                if not isinstance(ack, dict):
                    submitted = True
                    return result('UNKNOWN', 'copy_command_receipt_missing')
                if (ack.get('status') == 'REJECTED' and ack.get('reason') in (
                        'copy_queue_quote_changed', 'copy_queue_offer_changed')):
                    # The native Queue returns these exact reasons before it
                    # publishes an intent or invokes any stock method. Re-read
                    # role/day/quota/operation and propose the live offer; do
                    # not extend the admission deadline for repeated races.
                    self.wait()
                    continue
                if ack.get('status') not in ('ACCEPTED', 'COMPLETED'):
                    # An uncertain request is not replayed. The next operation
                    # re-reads live quota/cooldown after ownership is resolved.
                    return result('UNKNOWN' if submitted or action == 'copy_free' else 'BLOCKED',
                                  ack.get('reason') or 'copy_command_rejected')
                submitted = True
                # Native quota bounds the number of copies. This deadline
                # bounds a single stalled transition, not all eligible copies.
                deadline = self.clock() + self.timeout
            self.wait()
        # Use only the last valid observation's current blocked state; a
        # previous operation's terminal reason must not replace a timeout.
        if (not submitted and snapshot.get('status') == 'BLOCKED'
                and snapshot.get('busy') is False and not snapshot.get('next_action')
                and isinstance(snapshot.get('reason'), str)
                and snapshot['reason'].startswith('development_overlay:')):
            return result('BLOCKED', snapshot['reason'])
        return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'copy_timeout')

    @staticmethod
    def _valid_quota(value):
        fields = ('free_used', 'free_limit', 'free_remaining', 'finish_time', 'server_now', 'next_ready_seconds')
        if any(type(value.get(key)) is not int or value[key] < 0 for key in fields):
            return False
        return (0 <= value['free_used'] <= value['free_limit'] <= 255
                and value['free_remaining'] == value['free_limit'] - value['free_used']
                and value['next_ready_seconds'] == max(0, value['finish_time'] - value['server_now'])
                and type(value.get('copying')) is bool)
