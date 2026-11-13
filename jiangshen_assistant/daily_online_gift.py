"""Claim one current online-award tier without waiting for the next one."""
from __future__ import annotations

from dataclasses import dataclass
import math
import time
import uuid


@dataclass(frozen=True)
class DailyOnlineGiftResult:
    status: str
    reason: str
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    mutation_attempted: bool = False
    tier: int = 0
    got_before: int = 0
    got_after: int = 0
    next_get_time: int = 0
    tier_minutes: int = 0
    template_exists: bool = False
    tier_due: bool = False
    receipts: tuple[dict, ...] = ()
    processed_without_reply: bool = False
    page_confirmed: bool = False
    role_id: str = ''
    refresh_cycle: int = 0
    server_now: int = 0


class DailyOnlineGiftExecutor:
    """Bind every action to one role, cycle, operation, sequence and quote.

    The 791 reply has no request-id echo. Its native one-shot handler, exact
    stock callback, pre-state and owned reward provide only in-process
    correlation. An uncertain claim is never retried by this executor.
    """

    TASK_ID = 309
    TERMINAL = {'COMPLETED', 'PROCESSED', 'NO_FREE_REWARD', 'BLOCKED', 'UNKNOWN', 'STOPPED'}
    ACTIONS = {'open', 'begin', 'claim', 'close_reward', 'close_unavailable', 'cleanup_no_reply'}

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=45.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.clock = timeout, clock

    def execute(self, task_id=309, *, character_id: str, expected_cycle=None,
                expected_tier=None, absolute_deadline=None):
        if task_id != self.TASK_ID:
            raise ValueError('daily_online_gift_task_unsupported')
        if (not isinstance(character_id, str) or not character_id.isdigit()
                or int(character_id) <= 0):
            raise ValueError('daily_online_gift_character_required')
        if expected_cycle is not None and (type(expected_cycle) is not int or expected_cycle <= 0):
            raise ValueError('daily_online_gift_cycle_invalid')
        if expected_tier is not None and (type(expected_tier) is not int or expected_tier <= 0):
            raise ValueError('daily_online_gift_target_tier_invalid')
        if absolute_deadline is not None and (type(absolute_deadline) not in (int, float)
                or not math.isfinite(absolute_deadline)):
            raise ValueError('daily_online_gift_absolute_deadline_invalid')
        operation_id = uuid.uuid4().hex
        command_attempted = submitted = claim_issued = False
        snapshot = {}
        observation = -1
        command_sequence = None
        command_action = ''
        claim_identity = None
        frozen_tier = None
        actions: set[str] = set()
        quote_rejections = 0
        initial_cancellations = 0
        initial_retry_deadline = None
        cancelled_request = None
        last_request_sequence = 0
        deadline = self.clock() + self.timeout

        def result(status, reason):
            same = snapshot.get('operation_id') == operation_id
            clean = (same and snapshot.get('closed') is True and snapshot.get('busy') is False
                     and snapshot.get('cleanup_pending') is False and snapshot.get('home_ready') is True)
            return DailyOnlineGiftResult(
                status=status, reason=reason,
                cleanup='VERIFIED_CLOSED' if clean else 'UNVERIFIED' if command_attempted else 'NOT_NEEDED',
                operation_id=operation_id if command_attempted else '',
                mutation_attempted=claim_issued or same and snapshot.get('mutation_attempted') is True,
                **{key: snapshot.get(key, 0) if same else 0 for key in (
                    'tier', 'got_before', 'got_after', 'next_get_time', 'tier_minutes')},
                template_exists=same and snapshot.get('template_exists') is True,
                tier_due=same and snapshot.get('tier_due') is True,
                receipts=tuple(dict(row) for row in snapshot.get('receipts', [])) if same else (),
                processed_without_reply=status == 'PROCESSED' and same and clean,
                page_confirmed=same and any(row.get('page_confirmed') is True for row in snapshot.get('receipts', [])),
                role_id=snapshot.get('role_id', '') if same else '',
                refresh_cycle=snapshot.get('refresh_cycle', 0) if same else 0,
                server_now=snapshot.get('server_now', 0) if same else 0,
            )

        while self.clock() < deadline and (absolute_deadline is None or self.clock() < absolute_deadline):
            if (initial_cancellations and not submitted and initial_retry_deadline is not None
                    and self.clock() >= initial_retry_deadline):
                # The last exact rejection proved there was no native owner or
                # request to clean up. This budget is never extended by pause,
                # refreshed quotes, or another GUI cancellation.
                command_attempted = False
                return result('BLOCKED', 'online_gift_initial_gui_unavailable')
            if self.stopped():
                if cancelled_request is not None:
                    command_attempted = False
                return result('STOPPED', 'user_stop')
            if self.paused():
                before = self.clock()
                self.wait()
                deadline += self.clock() - before
                continue
            if cancelled_request is not None:
                command_attempted = True
            try:
                observed = self.objects.observe_daily_script(self.token, task_id)
            except Exception:
                return result('UNKNOWN' if command_attempted else 'UNAVAILABLE', 'online_gift_observation_failed')
            if (not isinstance(observed, dict) or observed.get('task_id') != task_id
                    or observed.get('route') != 'online_gift'):
                return result('UNKNOWN' if command_attempted else 'UNAVAILABLE', 'online_gift_route_unavailable')
            if observed.get('role_id') != character_id:
                return result('UNKNOWN' if command_attempted else 'BLOCKED', 'online_gift_character_changed')
            cycle, fresh = observed.get('refresh_cycle'), observed.get('observation_seq')
            if type(cycle) is not int or cycle <= 0:
                return result('UNKNOWN' if command_attempted else 'UNAVAILABLE', 'online_gift_game_day_unavailable')
            if expected_cycle is None:
                expected_cycle = cycle
            if cycle != expected_cycle:
                return result('UNKNOWN' if command_attempted else 'BLOCKED', 'online_gift_game_day_changed')
            if (type(fresh) is not int or fresh <= observation
                    or type(observed.get('revision')) is not int or observed['revision'] <= 0):
                return result('UNKNOWN' if command_attempted else 'UNAVAILABLE', 'online_gift_fresh_observation_missing')
            observation = fresh
            if submitted and (observed.get('operation_id') != operation_id
                              or observed.get('sequence') != command_sequence
                              or observed.get('last_action') != command_action):
                return result('UNKNOWN', 'online_gift_operation_sequence_or_action_changed')
            if not self._valid_evidence(observed):
                return result('UNKNOWN' if command_attempted else 'UNAVAILABLE', 'online_gift_evidence_schema')
            snapshot = observed
            if cancelled_request is not None:
                seq, revision, action, rejection = cancelled_request
                if not self._clean_initial_noninvocation(
                        snapshot, task_id, character_id, expected_cycle, operation_id,
                        seq, revision, action, rejection):
                    return result('UNKNOWN', 'online_gift_initial_noninvocation_recheck_failed')
                # Only this fresh observation, bound to the exact rejected ACK,
                # permits another initial open/begin. No accepted operation or
                # claim reaches this path.
                command_attempted = False
                if self.clock() >= initial_retry_deadline:
                    return result('BLOCKED', 'online_gift_initial_gui_unavailable')
            same = snapshot['operation_id'] == operation_id
            if same and snapshot['initial_frozen']:
                # A new native operation must still refer to the first tier
                # observed by the bounded wait. Check after its own begin,
                # before any claim; a previous operation's quote is not proof.
                if expected_tier is not None and (snapshot['tier'] != expected_tier
                        or snapshot['got_before'] != expected_tier - 1):
                    return result('UNKNOWN' if claim_issued else 'BLOCKED', 'online_gift_target_tier_changed')
                tier = tuple(snapshot[key] for key in (
                    'tier', 'got_before', 'next_get_time', 'tier_minutes', 'template_exists', 'tier_due'))
                if frozen_tier is None:
                    frozen_tier = tier
                elif tier != frozen_tier:
                    return result('UNKNOWN', 'online_gift_initial_tier_changed')
            receipts = snapshot['receipts'] if same else []
            if receipts:
                row = receipts[0]
                if (not claim_issued or claim_identity is None
                        or (row['sequence'], row['revision']) != claim_identity):
                    return result('UNKNOWN', 'online_gift_unissued_or_changed_claim_receipt')
            if same and snapshot['status'] in self.TERMINAL:
                status, reason = snapshot['status'], snapshot['reason'] or 'online_gift_finished'
                if status in ('UNKNOWN', 'STOPPED'):
                    return result(status, reason)
                if (not snapshot['closed'] or snapshot['busy'] or snapshot['cleanup_pending']
                        or not snapshot['home_ready']):
                    return result('UNKNOWN', 'online_gift_terminal_cleanup_unverified')
                if status == 'COMPLETED':
                    if len(receipts) == 1 and receipts[0].get('page_confirmed') is True:
                        if (not claim_issued or receipts[0].get('request_submitted') is not True
                                or receipts[0]['got_after'] <= receipts[0]['got_before']):
                            return result('UNKNOWN', 'online_gift_page_confirmation_invalid')
                    elif (len(receipts) != 1 or receipts[0]['operate'] != 0
                            or not all(receipts[0][key] for key in (
                                'receipt_matched', 'stock_callback_returned', 'progress_confirmed',
                                'next_time_confirmed', 'reward_owned', 'reward_closed',
                                'normal_close_invoked', 'normal_close_returned'))):
                        return result('UNKNOWN', 'online_gift_completion_evidence_missing')
                elif status == 'PROCESSED':
                    if (not claim_issued or len(receipts) != 1 or receipts[0].get('request_submitted') is not True
                            or receipts[0].get('no_reply_cleanup_returned') is not True
                            or receipts[0]['operate'] is not None or receipts[0]['receipt_matched']):
                        return result('UNKNOWN', 'online_gift_no_reply_processing_unverified')
                elif status == 'NO_FREE_REWARD':
                    if receipts:
                        row = receipts[0]
                        if (row['operate'] != 1 or not row['receipt_matched']
                                or not row['stock_callback_returned'] or not row['next_time_confirmed']
                                or not row['unavailable_panel_owned'] or not row['unavailable_close_invoked']
                                or row['got_after'] != row['got_before']):
                            return result('UNKNOWN', 'online_gift_rejection_evidence_missing')
                    elif (snapshot['template_exists'] and snapshot['tier_due']
                          and not (snapshot['reason'] == 'online_gift_source_icon_unavailable'
                                   and not snapshot['source_icon_ready'])):
                        return result('UNKNOWN', 'online_gift_unavailable_reason_unverified')
                return result(status, reason)
            if not submitted and snapshot['busy']:
                return result('BLOCKED', 'online_gift_existing_operation_unresolved')
            action = snapshot['next_action']
            if not action:
                if snapshot['status'] == 'BLOCKED':
                    return result('UNKNOWN' if command_attempted else 'BLOCKED', snapshot['reason'] or 'online_gift_blocked')
                if snapshot['status'] in ('UNKNOWN', 'STOPPED'):
                    return result('UNKNOWN' if command_attempted else 'BLOCKED', 'online_gift_existing_operation_unresolved')
                self.wait()
                continue
            if action not in self.ACTIONS or (not submitted and action not in ('open', 'begin')):
                return result('UNKNOWN' if command_attempted else 'BLOCKED', 'online_gift_action_out_of_scope')
            if action in actions:
                return result('UNKNOWN', 'online_gift_action_replay_refused')
            if action == 'begin' and snapshot['home_ready'] is not True:
                return result('UNKNOWN' if command_attempted else 'BLOCKED', 'online_gift_begin_not_home')
            if action == 'claim':
                if (not same or 'begin' not in actions or frozen_tier is None or not snapshot['home_ready']
                        or not snapshot['template_exists'] or not snapshot['tier_due']
                        or not snapshot['source_icon_ready'] or receipts or claim_issued):
                    return result('UNKNOWN', 'online_gift_current_tier_not_claimable')
            elif action in ('close_reward', 'close_unavailable'):
                if len(receipts) != 1 or not receipts[0]['receipt_matched'] or not receipts[0]['stock_callback_returned']:
                    return result('UNKNOWN', 'online_gift_close_receipt_not_owned')
                row = receipts[0]
                if action == 'close_reward':
                    if (row['operate'] != 0 or not row['progress_confirmed'] or not row['next_time_confirmed']
                            or not row['reward_owned'] or row['normal_close_invoked']):
                        return result('UNKNOWN', 'online_gift_reward_close_not_owned')
                elif row['operate'] != 1 or not row['unavailable_panel_owned'] or row['unavailable_close_invoked']:
                    return result('UNKNOWN', 'online_gift_unavailable_close_not_owned')
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                continue
            if absolute_deadline is not None and self.clock() >= absolute_deadline:
                return result('UNKNOWN' if command_attempted else 'PARTIAL', 'online_gift_wait_limit')
            previously_attempted = command_attempted
            command_attempted = True
            if not submitted and action in ('open', 'begin') and initial_retry_deadline is None:
                initial_retry_deadline = self.clock() + 5.0
            actions.add(action)
            if action == 'claim':
                claim_issued = True
            revision = snapshot['revision']
            try:
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=task_id, character_id=character_id,
                    operation_id=operation_id, expected_revision=revision)
            except Exception:
                return result('UNKNOWN', 'online_gift_command_outcome_unknown_no_replay')
            ack = wire.get('ack') if isinstance(wire, dict) else None
            seq = wire.get('requested_seq') if isinstance(wire, dict) else None
            ack_snapshot = ((ack.get('result') or {}).get('daily_script')
                            if isinstance(ack, dict) and isinstance(ack.get('result'), dict) else None)
            if (not isinstance(ack, dict) or type(seq) is not int or seq <= 0
                    or ack.get('command_name') != 'daily-script' or ack.get('command_seq') != seq
                    or wire.get('requested_command') != 'daily-script' or seq <= last_request_sequence):
                return result('UNKNOWN', 'online_gift_command_receipt_identity_missing')
            last_request_sequence = seq
            rejection = ack.get('reason')
            if (ack.get('status') == 'REJECTED' and not submitted and not previously_attempted
                    and not claim_issued and action in ('open', 'begin')
                    and isinstance(rejection, str) and rejection
                    and self._clean_initial_noninvocation(
                        ack_snapshot, task_id, character_id, expected_cycle, operation_id,
                        seq, revision, action, rejection)
                    and type(ack_snapshot.get('observation_seq')) is int
                    and ack_snapshot['observation_seq'] >= observation):
                snapshot = ack_snapshot
                observation = ack_snapshot['observation_seq']
                actions.discard(action)
                initial_cancellations += 1
                cancelled_request = seq, revision, action, rejection
                if initial_cancellations >= 3 or self.clock() >= initial_retry_deadline:
                    command_attempted = False
                    return result('BLOCKED', 'online_gift_initial_gui_unavailable')
                self.wait()
                continue
            if (ack.get('status') == 'REJECTED'
                    and ack.get('reason') in ('online_gift_queue_quote_changed', 'online_gift_queue_offer_changed')):
                # The native queue rejected this command before invoking the UI.
                # A fresh observation can supply the current offer and revision.
                command_attempted = previously_attempted
                actions.discard(action)
                if action == 'claim':
                    claim_issued = False
                quote_rejections += 1
                if quote_rejections > 10:
                    return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'online_gift_quote_retry_exhausted')
                self.wait()
                continue
            if ack.get('status') != 'COMPLETED':
                return result('UNKNOWN', ack.get('reason') or 'online_gift_command_not_completed')
            if (not isinstance(ack_snapshot, dict) or ack_snapshot.get('task_id') != task_id
                    or ack_snapshot.get('route') != 'online_gift' or ack_snapshot.get('role_id') != character_id
                    or ack_snapshot.get('refresh_cycle') != expected_cycle or ack_snapshot.get('operation_id') != operation_id
                    or ack_snapshot.get('sequence') != seq or ack_snapshot.get('last_action') != action
                    or not self._valid_evidence(ack_snapshot)):
                return result('UNKNOWN', 'online_gift_ack_operation_or_action_mismatch')
            if action == 'claim':
                claim_identity = seq, revision
            submitted = True
            cancelled_request = None
            quote_rejections = 0
            command_sequence, command_action = seq, action
            snapshot = ack_snapshot
            deadline = self.clock() + self.timeout
            self.wait()
        if cancelled_request is not None:
            command_attempted = False
            return result('BLOCKED', 'online_gift_initial_gui_unavailable')
        if absolute_deadline is not None and self.clock() >= absolute_deadline:
            return result('UNKNOWN' if command_attempted else 'PARTIAL', 'online_gift_wait_limit')
        return result('UNKNOWN' if command_attempted else 'UNAVAILABLE', 'online_gift_timeout_no_replay')

    @classmethod
    def _clean_initial_noninvocation(cls, value, task_id, character_id, cycle,
                                    operation_id, sequence, revision, action, reason):
        if not isinstance(value, dict) or not cls._valid_evidence(value):
            return False
        witness = value.get('not_invoked')
        return (isinstance(witness, dict) and value.get('task_id') == task_id
                and value.get('route') == 'online_gift' and value.get('role_id') == character_id
                and value.get('refresh_cycle') == cycle and value['operation_id'] == ''
                and value['sequence'] == sequence and value['last_action'] == action
                and value['invocation_attempted'] is False and value['initial_frozen'] is False
                and value['mutation_attempted'] is False and value['busy'] is False
                and value['cleanup_pending'] is False and value['receipts'] == []
                and witness.get('initial_clean') is True
                and witness.get('operation_id') == operation_id and witness.get('sequence') == sequence
                and witness.get('revision') == revision and witness.get('action') == action
                and witness.get('role_id') == character_id and witness.get('refresh_cycle') == cycle
                and witness.get('reason') == reason)

    @staticmethod
    def _valid_evidence(value):
        flags = ('busy', 'closed', 'cleanup_pending', 'home_ready', 'initial_frozen', 'mutation_attempted',
                 'template_exists', 'tier_due', 'source_icon_ready', 'invocation_attempted')
        counts = ('sequence', 'server_now', 'tier', 'got_before', 'got_after', 'next_get_time', 'tier_minutes')
        if (value.get('schema') != 1 or any(type(value.get(key)) is not bool for key in flags)
                or any(type(value.get(key)) is not int or value[key] < 0 for key in counts)
                or any(not isinstance(value.get(key), str) for key in (
                    'operation_id', 'last_action', 'status', 'reason', 'next_action'))
                or value.get('source_icon') != 'icon_libao' or value.get('source_handler') != 'MainUI._online_reward'
                or value.get('source_opcode') != 791 or not isinstance(value.get('receipts'), list)
                or len(value['receipts']) > 1 or value['tier'] != value['got_before'] + 1
                or value['tier_due'] and not value['template_exists']
                or value['mutation_attempted'] != bool(value['receipts'])
                or value['cleanup_pending'] != value['busy']):
            return False
        witness = value.get('not_invoked')
        if witness is not None:
            if (not isinstance(witness, dict) or type(witness.get('initial_clean')) is not bool
                    or any(not isinstance(witness.get(key), str) for key in
                           ('operation_id', 'action', 'role_id', 'reason'))
                    or any(type(witness.get(key)) is not int or witness[key] <= 0 for key in
                           ('sequence', 'revision', 'refresh_cycle'))):
                return False
        receipt_flags = ('receipt_matched', 'stock_callback_returned', 'progress_confirmed', 'next_time_confirmed',
                         'reward_owned', 'reward_closed', 'normal_close_invoked', 'normal_close_returned',
                         'unavailable_panel_owned', 'unavailable_close_invoked')
        for row in value['receipts']:
            if (not isinstance(row, dict) or row.get('operation_id') != value['operation_id']
                    or row.get('opcode') != 791 or row.get('tier') != value['tier']
                    or row.get('got_before') != value['got_before'] or row.get('got_after') != value['got_after']
                    or row.get('next_get_time_before') != value['next_get_time']
                    or any(type(row.get(key)) is not bool for key in receipt_flags)
                    or any(type(row.get(key)) is not int or row[key] < 0 for key in (
                        'sequence', 'revision', 'got_before', 'got_after', 'next_get_time_before', 'next_get_time_after'))
                    or row['sequence'] <= 0 or row['revision'] <= 0
                    or row.get('operate') is not None and (type(row['operate']) is not int or row['operate'] not in (0, 1))
                    or not isinstance(row.get('type_id'), list) or not isinstance(row.get('amount'), list)
                    or len(row['type_id']) != len(row['amount'])
                    or any(type(item) is not int or not -1 <= item <= 255 for item in row['type_id'])
                    or any(type(item) is not int or not 0 <= item <= 0xFFFFFFFF for item in row['amount'])):
                return False
            if any(key in row and type(row[key]) is not bool for key in
                   ('request_submitted', 'page_confirmed', 'no_reply_cleanup_returned')):
                return False
            page_confirmed = row.get('page_confirmed') is True
            if page_confirmed and (row.get('request_submitted') is not True or row['got_after'] <= row['got_before']):
                return False
            if row['receipt_matched'] and row['operate'] not in (0, 1):
                return False
            if row['progress_confirmed'] and not page_confirmed and (row['operate'] != 0 or not row['receipt_matched']
                                              or not row['stock_callback_returned'] or not row['next_time_confirmed']
                                              or row['got_after'] != row['got_before'] + 1):
                return False
            if row['reward_owned'] and not row['progress_confirmed']:
                return False
            if row['normal_close_invoked'] and not row['reward_owned']:
                return False
            if row['normal_close_returned'] and not row['normal_close_invoked']:
                return False
            if row['reward_closed'] and not row['normal_close_returned']:
                return False
            if row['unavailable_panel_owned'] and (row['operate'] != 1 or not row['receipt_matched']
                                                   or not row['stock_callback_returned']
                                                   or row['got_after'] != row['got_before']):
                return False
            if row['unavailable_close_invoked'] and not row['unavailable_panel_owned']:
                return False
        return True
