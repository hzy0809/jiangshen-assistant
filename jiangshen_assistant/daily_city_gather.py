"""Collect a bounded snapshot of ready trees or defeated own-city monsters."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid


@dataclass(frozen=True)
class DailyCityGatherResult:
    status: str
    reason: str
    cleanup: str = 'NOT_NEEDED'
    operation_id: str = ''
    mutation_attempted: bool = False
    targets_initial: int = 0
    targets_attempted: int = 0
    targets_confirmed: int = 0
    callbacks_returned: int = 0
    targets_skipped: int = 0
    overflow_skipped: int = 0
    targets_unprocessed: int = 0
    result_semantics_verified: bool = False
    receipts: tuple[dict, ...] = ()
    continuation_safe: bool = False
    semantic_unknown_clean: bool = False
    in_flight: bool = False
    cleanup_pending: bool = False
    assumed_success: bool = False
    attempts_cleaned: int = 0
    unique_targets_attempted: int = 0
    targets_currently_collectible: int = 0
    request_limit_reached: bool = False
    processed_without_reply: bool = False
    page_confirmed: bool = False
    mana_before: int = 0
    mana_after: int = 0


class DailyCityGatherExecutor:
    """Use fresh game eligibility, bounded requests and separate receipt evidence.

    Native observation_seq proves a fresh capture, revision quotes the offered
    action, and the acknowledged command sequence binds the next observation.
    An available own-city monster may be collected repeatedly after the previous
    request/UI releases. Per user policy, no response is treated as processed
    success, while server_success remains unknown and cleanup stays explicit.
    """

    ROUTES = {306: 'city_tree', 307: 'city_monster_collect'}
    TERMINAL = {'COMPLETED', 'PROCESSED', 'PARTIAL', 'NO_CANDIDATES', 'REJECTED', 'UNKNOWN', 'STOPPED'}

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=45.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.clock = timeout, clock

    def execute(self, task_id=306, *, character_id: str, expected_cycle=None, **_unused):
        if task_id not in self.ROUTES:
            raise ValueError('daily_city_gather_task_unsupported')
        if not isinstance(character_id, str) or character_id in ('', '0'):
            raise ValueError('daily_city_gather_character_required')
        operation_id = uuid.uuid4().hex
        attempted_command = submitted = False
        snapshot = {}
        observation = -1
        command_sequence = None
        command_action = ''
        initial_ids = None
        issued_targets: list[int] = []
        completed_ui_actions: set[tuple[str, int]] = set()
        quote_rejections = 0
        deadline = self.clock() + self.timeout

        def result(status, reason):
            same = snapshot.get('operation_id') == operation_id
            clean = (same and snapshot.get('continuation_safe') is True
                     and snapshot.get('closed') is True and snapshot.get('busy') is False
                     and snapshot.get('cleanup_pending') is False and snapshot.get('home_ready') is True)
            evidence = [dict(row) for row in snapshot.get('receipts', []) if isinstance(row, dict)] if same else []
            return DailyCityGatherResult(
                status=status, reason=reason,
                cleanup='VERIFIED_CLOSED' if clean else 'UNVERIFIED' if attempted_command else 'NOT_NEEDED',
                operation_id=operation_id if attempted_command else '',
                # A transport exception after issuing collect cannot prove
                # that the native send was not reached.
                mutation_attempted=bool(issued_targets) or same and snapshot.get('mutation_attempted') is True,
                targets_attempted=max(len(evidence), snapshot.get('targets_attempted', 0) if same else 0),
                **{key: snapshot.get(key, 0) if same else 0 for key in (
                    'targets_initial', 'targets_confirmed', 'callbacks_returned',
                    'targets_skipped', 'overflow_skipped', 'targets_unprocessed')},
                result_semantics_verified=same and snapshot.get('result_semantics_verified') is True,
                receipts=tuple(evidence),
                continuation_safe=clean,
                semantic_unknown_clean=clean and snapshot.get('semantic_unknown_clean') is True,
                in_flight=snapshot.get('in_flight') is True if same else attempted_command,
                cleanup_pending=snapshot.get('cleanup_pending') is True if same else attempted_command,
                assumed_success=same and snapshot.get('assumed_success') is True,
                attempts_cleaned=snapshot.get('attempts_cleaned', 0) if same else 0,
                unique_targets_attempted=len({row['target_id'] for row in evidence}),
                targets_currently_collectible=snapshot.get('targets_currently_collectible', 0) if same else 0,
                request_limit_reached=same and snapshot.get('request_limit_reached') is True,
                processed_without_reply=status == 'PROCESSED' and clean and same and snapshot.get('processed_without_reply') is True,
                page_confirmed=same and snapshot.get('page_confirmed') is True,
                mana_before=snapshot.get('mana_before', 0) if same else 0,
                mana_after=snapshot.get('mana_after', 0) if same else 0,
            )

        while self.clock() < deadline:
            if self.stopped():
                return result('STOPPED', 'user_stop')
            if self.paused():
                before = self.clock()
                self.wait()
                deadline += self.clock() - before
                continue
            try:
                observed = self.objects.observe_daily_script(self.token, task_id)
            except Exception:
                # The last trusted evidence is retained. No exception from
                # observation or submission authorizes replaying a request.
                return result('UNKNOWN' if attempted_command else 'UNAVAILABLE', 'city_gather_observation_failed')
            if (not isinstance(observed, dict) or observed.get('task_id') != task_id
                    or observed.get('route') != self.ROUTES[task_id]):
                return result('UNKNOWN' if attempted_command else 'UNAVAILABLE', 'city_gather_route_unavailable')
            if observed.get('role_id') != character_id:
                return result('UNKNOWN' if attempted_command else 'BLOCKED', 'city_gather_character_changed')
            cycle, fresh = observed.get('refresh_cycle'), observed.get('observation_seq')
            if type(cycle) is not int or cycle <= 0:
                return result('UNKNOWN' if attempted_command else 'UNAVAILABLE', 'city_gather_game_day_unavailable')
            if expected_cycle is None:
                expected_cycle = cycle
            if cycle != expected_cycle:
                return result('UNKNOWN' if attempted_command else 'BLOCKED', 'city_gather_game_day_changed')
            if (type(fresh) is not int or fresh <= observation
                    or type(observed.get('revision')) is not int or observed['revision'] <= 0):
                return result('UNKNOWN' if attempted_command else 'UNAVAILABLE', 'city_gather_fresh_observation_missing')
            observation = fresh
            if submitted and (observed.get('operation_id') != operation_id
                              or observed.get('sequence') != command_sequence
                              or observed.get('last_action') != command_action):
                return result('UNKNOWN', 'city_gather_operation_sequence_or_action_changed')
            if not self._valid_evidence(observed, task_id):
                return result('UNKNOWN' if attempted_command else 'UNAVAILABLE', 'city_gather_evidence_schema')
            snapshot = observed
            if submitted and snapshot['initial_frozen']:
                ids = tuple(snapshot['initial_target_ids'])
                if initial_ids is None:
                    initial_ids = ids
                elif ids != initial_ids:
                    return result('UNKNOWN', 'city_gather_initial_targets_changed')
                receipt_ids = [row['target_id'] for row in snapshot['receipts']]
                if len(receipt_ids) > len(issued_targets) or receipt_ids != issued_targets[:len(receipt_ids)]:
                    return result('UNKNOWN', 'city_gather_unissued_target_receipt')
            same = snapshot.get('operation_id') == operation_id
            if same and snapshot['status'] in self.TERMINAL:
                status, reason = snapshot['status'], snapshot.get('reason') or 'city_gather_finished'
                if status in ('UNKNOWN', 'STOPPED'):
                    return result(status, reason)
                if (snapshot['closed'] is not True or snapshot['busy'] is not False
                        or snapshot['cleanup_pending'] is not False or snapshot['home_ready'] is not True):
                    return result('UNKNOWN', 'city_gather_terminal_cleanup_unverified')
                if status == 'PROCESSED':
                    if (snapshot.get('processed_without_reply') is not True or not snapshot['mutation_attempted']
                            or not any(row.get('request_submitted') is True and row['assumed_success']
                                       and not row['response_received'] and row['cleanup_confirmed']
                                       for row in snapshot['receipts'])):
                        return result('UNKNOWN', 'city_gather_no_reply_processing_unverified')
                    return result('PROCESSED', reason)
                if status == 'COMPLETED' and (snapshot['targets_unprocessed'] != 0 or snapshot['targets_skipped'] != 0
                                             or snapshot['targets_confirmed'] != snapshot['targets_attempted']):
                    return result('UNKNOWN', 'city_gather_completion_evidence_missing')
                if task_id == 307 and snapshot['targets_attempted'] and not snapshot.get('page_confirmed'):
                    return result('PARTIAL', 'city_monster_result_semantics_unverified')
                return result(status, reason)
            if not submitted and snapshot['busy']:
                return result('BLOCKED', 'city_gather_existing_operation_unresolved')
            action = snapshot.get('next_action', '')
            if not action:
                if snapshot.get('status') == 'BLOCKED':
                    return result('UNKNOWN' if attempted_command else 'BLOCKED', snapshot.get('reason') or 'city_gather_blocked')
                self.wait()
                continue
            if action not in ('open', 'begin', 'collect', 'close_reward', 'cleanup_no_reply') or not submitted and action not in ('open', 'begin'):
                return result('UNKNOWN' if attempted_command else 'BLOCKED', 'city_gather_action_out_of_scope')
            target = snapshot.get('target_id')
            attempt_id = snapshot.get('attempt_id')
            if action in ('collect', 'close_reward', 'cleanup_no_reply'):
                if initial_ids is None or target not in initial_ids:
                    return result('UNKNOWN', 'city_gather_target_not_in_initial_observation')
                if action == 'collect':
                    if task_id == 306 and target in issued_targets:
                        return result('UNKNOWN', 'city_gather_collect_replay_refused')
                    if (len(issued_targets) >= 64 or attempt_id != len(issued_targets) + 1
                            or task_id == 307 and any(row.get('cleanup_confirmed') is not True for row in snapshot['receipts'])):
                        return result('UNKNOWN', 'city_monster_previous_attempt_not_released_or_request_bound')
                    issued_targets.append(target)
                else:
                    receipt = next((row for row in snapshot['receipts'] if row['attempt_id'] == attempt_id), None)
                    owned = (receipt is not None and receipt['target_id'] == target
                             and ((action == 'close_reward' and receipt.get('receipt_matched') is True
                                   and receipt.get('stock_callback_returned') is True and receipt.get('reward_owned') is True)
                                  or (action == 'cleanup_no_reply' and receipt.get('request_submitted') is True
                                      and (receipt.get('assumed_success') is True or receipt.get('page_confirmed') is True)
                                      and receipt.get('response_received') is False)))
                    if (action == 'close_reward' and task_id != 307
                            or (action, attempt_id) in completed_ui_actions or not owned):
                        return result('UNKNOWN', 'city_monster_reward_close_not_owned')
                    completed_ui_actions.add((action, attempt_id))
            if self.stopped():
                if action == 'collect':
                    issued_targets.pop()
                elif action in ('close_reward', 'cleanup_no_reply'):
                    completed_ui_actions.discard((action, attempt_id))
                return result('STOPPED', 'user_stop')
            if self.paused():
                # Nothing was sent; retain no speculative issued marker.
                if action == 'collect':
                    issued_targets.pop()
                elif action in ('close_reward', 'cleanup_no_reply'):
                    completed_ui_actions.discard((action, attempt_id))
                continue
            previously_attempted = attempted_command
            attempted_command = True
            try:
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=task_id, character_id=character_id,
                    operation_id=operation_id, expected_revision=snapshot['revision'])
            except Exception:
                return result('UNKNOWN', 'city_gather_command_outcome_unknown_no_replay')
            ack = wire.get('ack') if isinstance(wire, dict) else None
            seq = wire.get('requested_seq') if isinstance(wire, dict) else None
            ack_snapshot = ((ack.get('result') or {}).get('daily_script')
                            if isinstance(ack, dict) and isinstance(ack.get('result'), dict) else None)
            if (not isinstance(ack, dict) or type(seq) is not int or seq <= 0
                    or ack.get('command_name') != 'daily-script' or ack.get('command_seq') != seq
                    or command_sequence is not None and seq <= command_sequence):
                return result('UNKNOWN', 'city_gather_command_receipt_identity_missing')
            if (ack.get('status') == 'REJECTED'
                    and ack.get('reason') in ('city_gather_queue_quote_changed', 'city_gather_queue_offer_changed')):
                # Queue rejected this exact command before any GUI invocation.
                # Discard its local reservation and quote a fresh game state.
                attempted_command = previously_attempted
                if action == 'collect':
                    issued_targets.pop()
                elif action in ('close_reward', 'cleanup_no_reply'):
                    completed_ui_actions.discard((action, attempt_id))
                quote_rejections += 1
                if quote_rejections > 10:
                    return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'city_gather_quote_retry_exhausted')
                self.wait()
                continue
            if ack.get('status') != 'COMPLETED':
                return result('UNKNOWN' if submitted or action in ('collect', 'close_reward') else 'BLOCKED',
                              ack.get('reason') or 'city_gather_command_not_completed')
            if (not isinstance(ack_snapshot, dict) or ack_snapshot.get('task_id') != task_id
                    or ack_snapshot.get('role_id') != character_id or ack_snapshot.get('operation_id') != operation_id
                    or ack_snapshot.get('sequence') != seq or ack_snapshot.get('last_action') != action):
                return result('UNKNOWN', 'city_gather_ack_operation_or_action_mismatch')
            submitted = True
            quote_rejections = 0
            command_sequence, command_action = seq, action
            deadline = self.clock() + self.timeout
            self.wait()
        return result('UNKNOWN' if attempted_command else 'UNAVAILABLE', 'city_gather_timeout_no_replay')

    @staticmethod
    def _valid_evidence(value, task_id):
        counts = ('targets_initial', 'targets_attempted', 'targets_confirmed', 'callbacks_returned',
                  'targets_skipped', 'overflow_skipped', 'unavailable_skipped', 'targets_unprocessed',
                  'attempts_cleaned', 'unique_targets_attempted', 'targets_currently_collectible')
        flags = ('busy', 'closed', 'cleanup_pending', 'home_ready', 'initial_frozen', 'mutation_attempted',
                 'result_semantics_verified', 'continuation_safe', 'semantic_unknown_clean', 'in_flight',
                 'assumed_success', 'request_limit_reached')
        if (any(type(value.get(key)) is not int or value[key] < 0 for key in counts)
                or any(type(value.get(key)) is not bool for key in flags)
                or value.get('target_limit') != 64 or value.get('request_limit') != 64
                or not isinstance(value.get('initial_target_ids'), list)
                or not isinstance(value.get('receipts'), list) or type(value.get('sequence')) is not int
                or not isinstance(value.get('last_action'), str) or not isinstance(value.get('status'), str)
                or not isinstance(value.get('operation_id'), str)):
            return False
        ids, receipts = value['initial_target_ids'], value['receipts']
        if (len(ids) > 64 or any(type(item) is not int or not 0 <= item <= 255 for item in ids)
                or ids != sorted(set(ids)) or value['targets_initial'] > 256
                or value['targets_attempted'] != len(receipts) or len(receipts) > 64
                or value['targets_skipped'] != value['overflow_skipped'] + value['unavailable_skipped']
                or value['result_semantics_verified'] != (task_id == 306)
                or value['request_limit_reached'] != (len(receipts) >= 64)
                or value['targets_currently_collectible'] > len(ids)
                or value['targets_unprocessed'] > value['targets_initial']):
            return False
        if task_id == 306 and value['targets_attempted'] + value['targets_skipped'] + value['targets_unprocessed'] != value['targets_initial']:
            return False
        seen = set()
        previous_request = -1
        previous_cleanup = -1
        for attempt_id, row in enumerate(receipts, 1):
            if not isinstance(row, dict):
                return False
            if any(key in row and type(row[key]) is not bool for key in ('request_submitted', 'page_confirmed')):
                return False
            target = row.get('target_id')
            if (type(target) is not int or target not in ids or task_id == 306 and target in seen
                    or row.get('attempt_id') != attempt_id
                    or type(row.get('command_sequence')) is not int or row['command_sequence'] <= 0
                    or type(row.get('request_observation_seq')) is not int
                    or row['request_observation_seq'] <= previous_request
                    or row['request_observation_seq'] <= previous_cleanup
                    or type(row.get('cleanup_observation_seq')) is not int):
                return False
            seen.add(target)
            previous_request = row['request_observation_seq']
            if (any(type(row.get(key)) is not bool for key in ('receipt_matched', 'stock_callback_returned',
                    'reward_owned', 'reward_closed', 'local_effect_confirmed', 'close_callback_returned',
                    'cleanup_confirmed', 'object_removed', 'target_still_collectible', 'response_received',
                    'assumed_success', 'no_reply_cleanup_returned'))
                    or type(row.get('reply_target_id')) is not int
                    or row.get('raw_result') is not None and (type(row['raw_result']) is not int or not 0 <= row['raw_result'] <= 0xFFFFFFFF)):
                return False
            if row['cleanup_confirmed']:
                if row['cleanup_observation_seq'] <= row['request_observation_seq']:
                    return False
                previous_cleanup = row['cleanup_observation_seq']
                if task_id == 307 and not (row['assumed_success'] and row['no_reply_cleanup_returned']
                                           or row.get('page_confirmed') is True and row['no_reply_cleanup_returned']
                                           or row['receipt_matched'] and row['stock_callback_returned']
                                           and row['reward_closed'] and row['close_callback_returned']):
                    return False
            elif attempt_id < len(receipts):
                return False  # No next request may overlap an uncleared one.
            if row.get('page_confirmed') is True:
                if row.get('request_submitted') is not True or not row['local_effect_confirmed']:
                    return False
                if task_id == 307 and not row['object_removed']:
                    return False
            elif row['local_effect_confirmed']:
                if (not row['response_received'] or not row['receipt_matched']
                        or not row['stock_callback_returned'] or row['reply_target_id'] != target):
                    return False
                if task_id == 306 and (row['raw_result'] != 0 or row.get('server_success') is not True):
                    return False
                if task_id == 307 and (not row['reward_owned'] or not row['reward_closed'] or not row['object_removed']):
                    return False
            if row['assumed_success'] and (not isinstance(row.get('status'), str) or row.get('request_submitted') is not True):
                return False
            if not row['response_received'] and (row['raw_result'] is not None or row['receipt_matched']):
                return False
            if task_id == 307 and row.get('server_success') is not None:
                return False
        return (value['targets_confirmed'] == sum(row['local_effect_confirmed'] for row in receipts)
                and value['callbacks_returned'] == sum(row['stock_callback_returned'] for row in receipts)
                and value['attempts_cleaned'] == sum(row['cleanup_confirmed'] for row in receipts)
                and value['unique_targets_attempted'] == len(seen)
                and value['assumed_success'] == any(row['assumed_success'] for row in receipts)
                and value['mutation_attempted'] == bool(receipts))
