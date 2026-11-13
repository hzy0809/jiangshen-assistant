"""Claim only free chests on the normal PointRace chest page."""
from __future__ import annotations

import copy
from dataclasses import asdict, dataclass
import json
import os
from pathlib import Path
import time
import uuid


POINT_CHEST_ROUTE = 310


@dataclass(frozen=True)
class DailyPointRaceChestResult:
    status: str
    reason: str
    operation_id: str = ''
    request_count: int = 0
    confirmed_count: int = 0
    stock_callback_returned: bool = False
    claim_verified: bool = False
    chest_closed: bool = False
    returned_home: bool = False
    cleanup_pending: bool = False
    release_verified: bool = False
    native_snapshot: dict | None = None
    evidence_path: str | None = None
    role_id: str = ''
    refresh_cycle: int | None = None
    mutation_attempted: bool = False
    cleanup: str = 'NOT_NEEDED'


def _evidence_value(value):
    """Keep command/receipt fields, excluding transport secrets and nonces."""
    if isinstance(value, dict):
        return {str(key): _evidence_value(item) for key, item in value.items()
                if not any(part in str(key).lower() for part in
                           ('password', 'credential', 'nonce', 'authorization', 'access_token', 'refresh_token'))}
    if isinstance(value, (list, tuple)):
        return [_evidence_value(item) for item in value]
    if value is None or type(value) in (str, int, bool, float):
        return value
    raise ValueError('point_chest_evidence_value_invalid')


class _Evidence:
    def __init__(self, path):
        self.path = Path(path)
        self.sequence = 0

    def append(self, kind, value):
        record = {'event': kind, 'event_sequence': self.sequence + 1,
                  'recorded_unix_ns': time.time_ns(), 'value': _evidence_value(value)}
        encoded = (json.dumps(record, ensure_ascii=False, sort_keys=True, separators=(',', ':')) + '\n').encode('utf-8')
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.path.open('ab') as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        self.sequence += 1


class DailyPointRaceChestExecutor:
    """Finite, request-bound free claims followed by independently observed release.

    A missing command or business receipt never permits replay. The caller owns
    connection admission; native owns exact reward-popup callbacks. This route cannot fight,
    refresh opponents, purchase, or recover a paid historical chest.
    """

    actions = frozenset(('open', 'enter', 'open_chest', 'claim', 'finish', 'close_reward',
                         'close_chest', 'return', 'close_selector'))
    cleanup_actions = frozenset(('finish', 'close_reward', 'close_chest', 'return', 'close_selector'))

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=120.0, pending_timeout=25.0, observation_interval=.25,
                 command_timeout=5.0, clock=time.monotonic, evidence_dir=None,
                 popup_service=None):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.pending_timeout = timeout, pending_timeout
        self.observation_interval, self.command_timeout = observation_interval, command_timeout
        self.clock, self.popup_service = clock, popup_service
        self.evidence_dir = Path(evidence_dir) if evidence_dir is not None else (
            Path(getattr(objects, 'data_dir', None) or Path(__file__).resolve().parent.parent / 'data')
            / 'point-chest-attempts')

    def execute(self, task_id=POINT_CHEST_ROUTE, *, character_id, expected_cycle=None):
        if task_id != POINT_CHEST_ROUTE:
            raise ValueError('point_chest_route_unsupported')
        if not isinstance(character_id, str) or not character_id.isdecimal() or not character_id.strip('0'):
            raise ValueError('point_chest_character_invalid')
        operation_id = uuid.uuid4().hex
        journal = _Evidence(self.evidence_dir / (operation_id + '.jsonl'))
        snapshot, view, operation = {}, {}, {}
        submitted = touched = entry_returned = stop_requested = False
        evidence_failed = False
        last_sequence = 0
        entry_sequence = None
        request_count = confirmed_count = 0
        returned = False
        attempted_claims = []
        receipts = []
        navigation = set()
        pending_claim = None
        accepted_command = None
        rejected_replans = 0
        replan_deadline = None
        terminal = None
        deadline = self.clock() + self.timeout
        pending_deadline = deadline
        next_observation = 0.0

        def own():
            return submitted and operation.get('id') == operation_id

        def same_owner():
            return (view.get('role_id') == character_id and type(expected_cycle) is int
                    and expected_cycle > 0 and view.get('refresh_cycle') == expected_cycle)

        def released():
            return (own() and same_owner()
                    and operation.get('status') in ('COMPLETED', 'ALREADY_DONE', 'UNAVAILABLE', 'PARTIAL')
                    and operation.get('closed') is True and view.get('home_ready') is True
                    and operation.get('release_verified') is True and operation.get('busy') is False
                    and operation.get('cleanup_pending') is False and operation.get('chest_closed') is True
                    and operation.get('entry_listener_released') is True)

        def record(kind, value):
            nonlocal evidence_failed
            try:
                journal.append(kind, value)
                return True
            except (OSError, TypeError, ValueError):
                evidence_failed = True
                return False

        def result(status, reason):
            clean = released()
            if evidence_failed:
                status, reason = 'UNKNOWN' if submitted else 'BLOCKED', 'point_chest_evidence_write_failed'
            value = DailyPointRaceChestResult(
                status=status, reason=reason, operation_id=operation_id if submitted else '',
                request_count=request_count, confirmed_count=confirmed_count,
                stock_callback_returned=returned, claim_verified=confirmed_count > 0,
                chest_closed=own() and same_owner() and operation.get('chest_closed') is True,
                returned_home=clean, cleanup_pending=(submitted or touched) and not clean,
                release_verified=clean, native_snapshot=copy.deepcopy(snapshot),
                evidence_path=str(journal.path), role_id=view.get('role_id', ''),
                refresh_cycle=view.get('refresh_cycle'), mutation_attempted=bool(attempted_claims),
                cleanup='VERIFIED_CLOSED' if clean else 'UNVERIFIED' if submitted or touched else 'NOT_NEEDED')
            if not record('result', asdict(value)) and value.reason != 'point_chest_evidence_write_failed':
                value = DailyPointRaceChestResult(**{**asdict(value),
                    'status': 'UNKNOWN' if submitted else 'BLOCKED', 'reason': 'point_chest_evidence_write_failed'})
            return value

        if not record('execution', {'task_id': task_id, 'mode': 'pointrace_chest',
                                   'operation_id': operation_id, 'character_id': character_id,
                                   'expected_cycle': expected_cycle}):
            return result('BLOCKED', 'point_chest_evidence_write_failed')

        while self.clock() < deadline:
            stop_requested |= bool(self.stopped())
            if not submitted and stop_requested:
                return result('STOPPED', 'user_stop')
            if self.clock() < next_observation:
                self.wait()
                continue
            try:
                snapshot = self.objects.observe_daily_script(self.token, task_id)
            except Exception:
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'point_chest_observation_failed')
            next_observation = self.clock() + self.observation_interval
            if not record('observation', snapshot):
                terminal = ('UNKNOWN', 'point_chest_evidence_write_failed')
            if (not isinstance(snapshot, dict) or snapshot.get('schema') != 1
                    or not isinstance(snapshot.get('view'), dict) or not isinstance(snapshot.get('operation'), dict)):
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'point_chest_schema_unavailable')
            view, operation = snapshot['view'], snapshot['operation']
            if (view.get('task_id') != task_id or view.get('mode') != 'pointrace_chest'
                    or type(snapshot.get('revision')) is not int or snapshot['revision'] <= 0):
                return result('UNKNOWN' if submitted else 'BLOCKED', 'point_chest_route_changed')
            if view.get('observation_available') is not True:
                return result('UNKNOWN' if submitted else 'UNAVAILABLE', view.get('gate') or 'point_chest_observation_unavailable')
            if view.get('role_id') != character_id:
                return result('UNKNOWN' if submitted else 'BLOCKED', 'point_chest_character_changed')
            if expected_cycle is None:
                expected_cycle = view.get('refresh_cycle')
            if not same_owner():
                return result('UNKNOWN' if submitted else 'BLOCKED', 'point_chest_refresh_cycle_changed')
            if accepted_command is not None:
                # Current BridgeClient waits for terminal ACKs internally. If
                # an asynchronous caller returns ACCEPTED, retain its intent
                # and observe without publishing another command over it.
                if self.clock() >= pending_deadline:
                    return result('UNKNOWN', 'point_chest_accepted_command_timeout')
                if (operation.get('id') == operation_id
                        and operation.get('request_sequence') == accepted_command['sequence']
                        and pending_claim is not None):
                    if self._counts_valid(operation, len(attempted_claims), confirmed_count):
                        request_count = operation['request_count']
                        verified = self._claim_receipt(operation, pending_claim, confirmed_count)
                        if isinstance(verified, dict) and view.get('bonus_count') == verified['after_bonus_count']:
                            receipts.append(verified)
                            confirmed_count += 1
                            returned = True
                            pending_claim = None
                            record('verified_claim', verified)
                self.wait()
                continue
            if submitted and not own():
                return result('UNKNOWN', 'point_chest_operation_changed')
            if own():
                touched |= operation.get('ui_touched') is True
                if type(operation.get('sequence')) is not int or operation['sequence'] != last_sequence:
                    return result('UNKNOWN', 'point_chest_operation_sequence_changed')
                if operation.get('entry_callback_returned') is True:
                    if entry_sequence is None or operation.get('entry_request_sequence') != entry_sequence:
                        return result('UNKNOWN', 'point_chest_entry_receipt_sequence_changed')
                    entry_returned = True
                if not self._counts_valid(operation, len(attempted_claims), confirmed_count):
                    return result('UNKNOWN', 'point_chest_request_count_changed')
                request_count = operation['request_count']
                if not self._receipts_retained(operation, receipts):
                    return result('UNKNOWN', 'point_chest_prior_receipt_changed')
                if pending_claim is not None:
                    if (operation.get('request_sequence') == pending_claim['sequence']
                            and operation.get('requested_time') == pending_claim['time']
                            and operation.get('requested_index') == pending_claim['index']):
                        returned |= operation.get('callback_returned') is True
                    accepted = self._claim_receipt(operation, pending_claim, confirmed_count)
                    if accepted is False:
                        terminal = terminal or ('UNKNOWN', 'point_chest_claim_receipt_unverified')
                        if operation.get('round_pending') is False:
                            pending_claim = None
                    elif accepted is not None:
                        if view.get('bonus_count') != accepted['after_bonus_count']:
                            return result('UNKNOWN', 'point_chest_post_claim_state_changed')
                        receipts.append(accepted)
                        confirmed_count += 1
                        returned = True
                        pending_claim = None
                        record('verified_claim', accepted)
                if operation.get('confirmed_count') != confirmed_count:
                    return result('UNKNOWN', 'point_chest_confirmation_count_changed')
                if released():
                    if stop_requested:
                        return result('STOPPED', 'user_stop')
                    if terminal is not None:
                        return result(*terminal)
                    if not entry_returned or 'enter' not in navigation:
                        return result('UNKNOWN', 'point_chest_fresh_entry_unconfirmed')
                    if confirmed_count:
                        return result('COMPLETED', 'point_chest_free_claim_confirmed')
                    return result('ALREADY_DONE' if view.get('point_got') is True else 'UNAVAILABLE',
                                  operation.get('reason') or 'point_chest_no_free_eligible_after_entry')
                if operation.get('status') in ('UNKNOWN', 'STOPPED'):
                    terminal = terminal or (operation['status'], operation.get('reason') or 'point_chest_native_unknown')
            elif (operation.get('busy') is True or operation.get('cleanup_pending') is True
                  or view.get('in_scene') is True or view.get('chest_open') is True
                  or view.get('home_ready') is not True):
                return result('BLOCKED', 'point_chest_existing_operation_unresolved')

            action = view.get('next_action', '')
            # Native busy includes ownership of the entire page lifetime. Only
            # a pending request prevents advancing its advertised next action.
            finish_needed = (own() and (stop_requested or terminal is not None or evidence_failed)
                             and 'finish' not in navigation)
            if own() and pending_claim is not None and not finish_needed:
                if self.clock() >= pending_deadline:
                    return result('UNKNOWN', 'point_chest_action_result_timeout')
                self.wait()
                continue
            if stop_requested or terminal is not None or evidence_failed:
                if not submitted:
                    return result(*(terminal or ('STOPPED', 'user_stop')))
                if finish_needed:
                    action = 'finish'
                elif action not in self.cleanup_actions:
                    return result(*(terminal or ('STOPPED', 'user_stop')))
            elif self.paused():
                self.wait()
                continue
            if not action:
                if self.clock() >= pending_deadline:
                    return result('UNKNOWN' if submitted else 'UNAVAILABLE', operation.get('reason') or view.get('gate') or 'point_chest_readiness_timeout')
                self.wait()
                continue
            if action not in self.actions or not submitted and action != 'open':
                return result('UNKNOWN' if submitted else 'BLOCKED', 'point_chest_action_out_of_scope')
            if self.stopped() and action not in self.cleanup_actions:
                stop_requested = True
                continue
            navigation_key = (('close_reward', receipts[-1]['request_sequence'])
                              if action == 'close_reward' and receipts else action)
            if navigation_key in navigation:
                return result('UNKNOWN', 'point_chest_navigation_reoffered')
            if action == 'close_reward':
                if (not own() or pending_claim is not None or not confirmed_count
                        or operation.get('popup_handoff_ready') is not True
                        or operation.get('reward_popup_owned') is not True):
                    return result('UNKNOWN', 'point_chest_popup_ownership_unverified')
            if action == 'claim':
                if stop_requested or self.stopped() or self.paused() or evidence_failed or terminal:
                    continue
                index, chest_time, eligible = (view.get('eligible_index'), view.get('eligible_time'), view.get('eligible_count'))
                bonus_count = view.get('bonus_count')
                if (not own() or not entry_returned or view.get('in_scene') is not True
                        or view.get('chest_open') is not True or type(index) is not int or not 0 <= index <= 3
                        or type(chest_time) is not int or chest_time <= 0 or type(eligible) is not int
                        or not 0 < eligible <= 64 or len(attempted_claims) >= 64
                        or type(bonus_count) is not int or bonus_count < eligible
                        or any(chest_time == attempted_time for _, attempted_time in attempted_claims)
                        or request_count != confirmed_count):
                    return result('UNKNOWN', 'point_chest_free_claim_offer_changed')
            elif action == 'enter' and ('open' not in navigation or attempted_claims):
                return result('UNKNOWN', 'point_chest_entry_order_changed')
            elif action == 'open_chest' and (not entry_returned or 'enter' not in navigation):
                return result('UNKNOWN', 'point_chest_entry_callback_unconfirmed')
            fields = {'task_id': task_id, 'character_id': character_id,
                      'operation_id': operation_id, 'expected_revision': snapshot['revision']}
            intent = {'action': action, 'fields': fields, 'before': copy.deepcopy(snapshot)}
            if not record('before_submit', intent):
                if action not in self.cleanup_actions:
                    terminal = ('UNKNOWN' if submitted else 'BLOCKED', 'point_chest_evidence_write_failed')
                    if not submitted:
                        return result(*terminal)
                    continue
            if action == 'claim':
                attempted_claims.append((index, chest_time))
                pending_claim = {'index': index, 'time': chest_time, 'before_count': bonus_count,
                                 'request_count': request_count + 1, 'sequence': None}
            else:
                navigation.add(navigation_key)
            was_submitted = submitted
            submitted = True
            try:
                submit = (self.objects.submit_daily_script_cleanup if stop_requested
                          and action in self.cleanup_actions else self.objects.submit_daily_script)
                wire = submit(self.token, action, **fields, timeout=self.command_timeout)
            except Exception:
                record('command_unknown', {'action': action, 'operation_id': operation_id})
                return result('UNKNOWN', 'point_chest_command_outcome_unknown')
            record('command_wire', {'action': action, 'wire': wire})
            refused = self._not_invoked_rejection(wire, snapshot)
            if refused is not None:
                # The exact rejection proves no stock invocation. Only this
                # proof releases the provisional local duplicate guard.
                submitted = was_submitted
                if action == 'claim':
                    attempted_claims.pop()
                    pending_claim = None
                else:
                    navigation.discard(navigation_key)
                record('command_not_invoked', {'action': action, 'reason': refused,
                                               'requested_seq': wire['requested_seq'],
                                               'before': snapshot,
                                               'after': wire['ack']['result']['daily_script']})
                snapshot = wire['ack']['result']['daily_script']
                view, operation = snapshot['view'], snapshot['operation']
                if refused in ('point_chest_revision_changed', 'point_chest_action_changed'):
                    rejected_replans += 1
                    if replan_deadline is None:
                        replan_deadline = self.clock() + 5.0
                    if rejected_replans < 3 and self.clock() < replan_deadline and not evidence_failed:
                        self.wait()
                        continue
                if not submitted:
                    return result('BLOCKED', refused)
                if action in self.cleanup_actions:
                    return result('STOPPED' if stop_requested else 'UNKNOWN', refused)
                terminal = ('UNAVAILABLE', refused)
                self.wait()
                continue
            sequence = self._invoked_sequence(wire, operation_id, last_sequence)
            if sequence is None:
                accepted_sequence = self._accepted_sequence(wire, last_sequence)
                if accepted_sequence is not None:
                    accepted_command = {'action': action, 'sequence': accepted_sequence}
                    if action == 'claim' and pending_claim is not None:
                        pending_claim['sequence'] = accepted_sequence
                    pending_deadline = min(deadline, self.clock() + self.pending_timeout)
                    record('command_pending', accepted_command)
                    self.wait()
                    continue
                return result('UNKNOWN', 'point_chest_command_invocation_unverified')
            last_sequence = sequence
            if action == 'enter':
                entry_sequence = sequence
            if action == 'claim' and pending_claim is not None:
                pending_claim['sequence'] = sequence
            pending_deadline = min(deadline, self.clock() + self.pending_timeout)
            self.wait()
        return result('UNKNOWN' if submitted else 'UNAVAILABLE', 'point_chest_timeout')

    @staticmethod
    def _counts_valid(operation, attempted_count, confirmed_count):
        requested, confirmed = operation.get('request_count'), operation.get('confirmed_count')
        return (type(requested) is int and type(confirmed) is int
                and requested == attempted_count and confirmed_count <= confirmed <= requested
                and confirmed <= confirmed_count + 1)

    @staticmethod
    def _receipts_retained(operation, verified):
        rows = operation.get('receipts')
        if not isinstance(rows, list) or len(rows) != operation.get('request_count'):
            return False
        return all(isinstance(rows[index], dict)
                   and all(rows[index].get(key) == value for key, value in receipt.items()
                           if key not in ('operation_id', 'sequence', 'request_count', 'confirmed_count', 'response_provenance'))
                   for index, receipt in enumerate(verified))

    @staticmethod
    def _not_invoked_rejection(wire, before):
        """Recognize only Queue refusals before native operation publication.

        Queue checks these reasons before changing id/sequence or invoking any
        stock code. Unchanged invocation and receipt fields plus GUI evidence
        bind that fact to this exact command, including an existing own route.
        """
        if not isinstance(wire, dict) or wire.get('command') != 'daily-script':
            return None
        ack, state = wire.get('ack'), wire.get('state')
        if not isinstance(ack, dict) or not isinstance(state, dict):
            return None
        sequence = wire.get('requested_seq')
        reason = ack.get('reason')
        if (type(sequence) is not int or sequence <= 0 or ack.get('command_seq') != sequence
                or ack.get('command_name') != 'daily-script' or ack.get('status') != 'REJECTED'
                or state.get('last_command_seq') != sequence
                or reason not in ('point_chest_revision_changed', 'point_chest_action_changed',
                                  'point_chest_operation_changed', 'point_chest_queue_unavailable')):
            return None
        result = ack.get('result')
        after = result.get('daily_script') if isinstance(result, dict) else None
        if (not isinstance(after, dict) or after.get('schema') != 1
                or not isinstance(after.get('view'), dict) or not isinstance(after.get('operation'), dict)):
            return None
        old_view, new_view = before.get('view') or {}, after['view']
        old, current = before.get('operation') or {}, after['operation']
        if (new_view.get('observation_available') is not True
                or any(new_view.get(key) != old_view.get(key) for key in
                       ('task_id', 'mode', 'role_id', 'refresh_cycle'))
                or any(current.get(key) != old.get(key) for key in (
                    'id', 'sequence', 'ui_touched', 'mutation_attempted', 'request_count', 'confirmed_count',
                    'entry_request_sequence', 'entry_callback_returned', 'entry_listener_released',
                    'request_sequence', 'requested_index', 'requested_time', 'callback_returned',
                    'state_confirmed', 'receipt_result', 'receipt_time', 'receipt_gold', 'receipts',
                    'before_bonus_count', 'after_bonus_count', 'round_pending', 'reward_closures',
                    'reward_close_attempted', 'reward_close_request_sequence', 'reward_close_command_sequence',
                    'finish_requested', 'closed', 'busy', 'cleanup_pending', 'release_verified'))):
            return None
        gui = state.get('campaign_gui')
        if not isinstance(gui, dict) or type(gui.get('command_seq')) is not int:
            return None
        gui_sequence = gui['command_seq']
        if (not 0 <= gui_sequence <= sequence or gui_sequence == sequence
                and (gui.get('phase') != 'not_invoked' or gui.get('invocation_attempted') is not False)):
            return None
        if type(current.get('sequence')) is not int or not current['sequence'] < sequence:
            return None
        return reason

    @staticmethod
    def _accepted_sequence(wire, previous):
        if not isinstance(wire, dict) or wire.get('command') != 'daily-script':
            return None
        ack = wire.get('ack')
        if not isinstance(ack, dict):
            return None
        sequence = wire.get('requested_seq')
        if (type(sequence) is not int or sequence <= previous or ack.get('command_seq') != sequence
                or ack.get('command_name') != 'daily-script' or ack.get('status') != 'ACCEPTED'):
            return None
        return sequence

    @staticmethod
    def _invoked_sequence(wire, operation_id, previous):
        if not isinstance(wire, dict):
            return None
        ack, state = wire.get('ack'), wire.get('state')
        if not isinstance(ack, dict) or not isinstance(state, dict):
            return None
        sequence = wire.get('requested_seq', wire.get('seq'))
        result = ack.get('result')
        observed = result.get('daily_script') if isinstance(result, dict) else None
        operation = observed.get('operation') if isinstance(observed, dict) else None
        gui = state.get('campaign_gui')
        if not isinstance(operation, dict) or not isinstance(gui, dict):
            return None
        if (type(sequence) is not int or sequence <= previous or ack.get('command_seq') != sequence
                or ack.get('status') != 'COMPLETED' or operation.get('id') != operation_id
                or operation.get('sequence') != sequence or gui.get('command_seq') != sequence
                or gui.get('phase') != 'completed' or gui.get('invocation_attempted') is not True):
            return None
        return sequence

    @staticmethod
    def _claim_receipt(operation, pending, confirmed_count):
        """None is pending; False is terminal evidence failure; dict is success."""
        if operation.get('confirmed_count') == confirmed_count:
            return None if operation.get('round_pending') is True else False
        receipts = operation.get('receipts')
        if (not isinstance(receipts, list) or len(receipts) != pending['request_count']
                or not isinstance(receipts[-1], dict)):
            return False
        receipt = receipts[-1]
        if (operation.get('confirmed_count') != confirmed_count + 1
                or operation.get('response_provenance') != 'request_handler_operation_sequence'
                or any(type(operation.get(key)) is not int for key in (
                    'request_sequence', 'requested_time', 'requested_index', 'receipt_result',
                    'receipt_time', 'receipt_gold', 'before_bonus_count', 'after_bonus_count'))
                or operation.get('request_sequence') != pending['sequence']
                or operation.get('requested_index') != pending['index']
                or operation.get('requested_time') != pending['time']
                or operation.get('before_bonus_count') != pending['before_count']
                or type(operation.get('after_bonus_count')) is not int
                or operation['after_bonus_count'] != pending['before_count'] - 1
                or operation.get('callback_returned') is not True
                or operation.get('state_confirmed') is not True
                or operation.get('receipt_result') != 0
                or operation.get('receipt_time') != pending['time']
                or operation.get('receipt_gold') != 0
                or any(receipt.get(key) != operation.get(key) for key in (
                    'request_sequence', 'requested_time', 'requested_index', 'receipt_result',
                    'receipt_time', 'receipt_gold', 'callback_returned', 'state_confirmed',
                    'before_bonus_count', 'after_bonus_count'))):
            return False
        return {'operation_id': operation['id'], 'sequence': pending['sequence'],
                'request_count': pending['request_count'], 'confirmed_count': confirmed_count + 1,
                **{key: operation[key] for key in ('requested_index', 'requested_time', 'before_bonus_count',
                    'after_bonus_count', 'callback_returned', 'receipt_result', 'receipt_time',
                    'receipt_gold', 'state_confirmed', 'request_sequence', 'response_provenance')}}
