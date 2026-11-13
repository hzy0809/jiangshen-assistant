from copy import deepcopy
import unittest

from jiangshen_assistant.daily_timed import DailyTimedExecutor
from jiangshen_assistant.objects import CampaignAdmissionClosed


ROLE = '12345'
TOKEN = object()


def frame(task_id, current=0, required=1, *, action='open', reason=''):
    tasks = {str(key): dict(action='', reason='', current=0, required=1) for key in (12, 13)}
    tasks[str(task_id)] = dict(action=action, reason=reason, current=current, required=required)
    return dict(schema=1, revision=1, character_id=ROLE, tasks=tasks,
                operation=dict(operation_id='', task_id=0, sequence=0, stage='', status='IDLE',
                               invoked=False, opcode=0, reason='', request_attribution=None,
                               counter_before=None, counter_after=None))


class Objects:
    def __init__(self, task_id=12, opcode=1829):
        self.task_id, self.opcode = task_id, opcode
        self.timed = frame(task_id)
        self.enabled = True
        self.actions = []
        self.probed = []
        self.after_ack = None
        self.before_observe = None
        self.reply_override = None

    @property
    def task(self):
        return self.timed['tasks'][str(self.task_id)]

    @property
    def operation(self):
        return self.timed['operation']

    def advance(self, status, *, action='', current=None, counter_after=None, reason=''):
        self.timed['revision'] += 1
        self.operation.update(status=status, reason=reason, counter_after=counter_after)
        self.task.update(action=action, reason=reason)
        if current is not None:
            self.task['current'] = current

    def succeed(self):
        self.advance('SUCCEEDED', current=1, counter_after=1,
                     action='close' if self.task_id == 12 else '')

    def observe_daily_timed(self, token):
        assert token is TOKEN
        if self.before_observe is not None:
            self.before_observe()
        self.probed.append(deepcopy(self.timed))
        return {'enabled': self.enabled, 'timed': deepcopy(self.timed) if self.enabled else None}

    def submit_daily_timed(self, token, action, **fields):
        assert token is TOKEN
        assert fields['expected_revision'] == self.timed['revision']
        assert fields['character_id'] == self.timed['character_id']
        assert fields['task_id'] == self.task_id
        assert self.task['action'] == action
        self.actions.append((action, fields))
        if self.reply_override is not None:
            return self.reply_override(action)
        if action != 'open':
            assert fields['operation_id'] == self.operation['operation_id']
        seq, status = {'open': (10, 'OPENING'), 'participate': (13, 'PENDING'), 'close': (16, 'CLOSING')}[action]
        self.advance(status)
        opcode = self.opcode if action == 'participate' else 0
        self.operation.update(operation_id=fields['operation_id'], task_id=self.task_id,
                              sequence=seq, stage=action, invoked=True, opcode=opcode,
                              request_attribution='UNKNOWN' if opcode in (321, 1829) else None,
                              counter_before=self.task['current'] if action == 'participate' else None)
        wire = {'requested_seq': seq, 'ack': {'status': 'COMPLETED',
                'result': {'daily_timed': deepcopy(self.timed)}}}
        if action == 'open':
            self.advance('READY', action='participate')
        elif action == 'close':
            self.advance('SUCCEEDED')
        if self.after_ack is not None:
            self.after_ack(action, wire)
        return wire


class DailyTimedTests(unittest.TestCase):
    def setUp(self):
        self.objects = Objects()
        self.now = 0.0
        self.paused = self.stopped = False

    def wait(self):
        self.now += 1

    def executor(self, wait=None):
        return DailyTimedExecutor(self.objects, TOKEN, clock=lambda: self.now,
            wait=wait or self.wait, timeout=3, paused=lambda: self.paused,
            stopped=lambda: self.stopped)

    def execute(self):
        return self.executor().execute(self.objects.task_id, character_id=ROLE)

    def actions(self):
        return [action for action, fields in self.objects.actions]

    def test_disabled_and_preexisting_completion_send_nothing(self):
        self.objects.enabled = False
        result = self.execute()
        self.assertEqual((result.status, result.reason), ('UNAVAILABLE', 'daily_timed_disabled'))
        self.objects.enabled = True
        self.objects.task.update(current=1, action='', reason='daily_timed_already_complete')
        result = self.execute()
        self.assertEqual((result.status, result.progress_after), ('ALREADY_COMPLETE', 1))
        self.assertEqual(self.actions(), [])

    def test_initial_gate_and_counter_fail_closed(self):
        for row, expected in [
            (dict(action='', reason='quiz_closed'), 'UNAVAILABLE'),
            (dict(action='', reason='daily_timed_overlay:Menu'), 'BLOCKED'),
            (dict(action='participate'), 'BLOCKED'),
            (dict(current=None), 'UNAVAILABLE'),
            (dict(current=True), 'BLOCKED'),
            (dict(current=2), 'UNAVAILABLE'),
            (dict(required=0), 'UNAVAILABLE'),
        ]:
            with self.subTest(row=row):
                self.objects.timed = frame(12)
                self.objects.task.update(row)
                self.assertEqual(self.execute().status, expected)
                self.assertEqual(self.actions(), [])

    def test_native_success_counts_for_account_and_preserves_attribution(self):
        for task_id, opcode, attribution, cleanup in [
            (12, 312, None, 'VERIFIED_CLOSED'),
            (12, 1829, 'UNKNOWN', 'VERIFIED_CLOSED'),
            (13, 321, 'UNKNOWN', 'BRAWL_MAY_REMAIN_OPEN'),
        ]:
            with self.subTest(task_id=task_id, opcode=opcode):
                self.objects = Objects(task_id, opcode)
                def settle(action, wire):
                    if action == 'participate':
                        self.objects.succeed()
                self.objects.after_ack = settle
                executor = self.executor()
                result = executor.execute(task_id, character_id=ROLE)
                self.assertEqual((result.status, result.progress_before, result.progress_after, result.required),
                                 ('COMPLETED', 0, 1, 1))
                self.assertEqual((result.opcode, result.request_attribution, result.cleanup),
                                 (opcode, attribution, cleanup))
                expected = ['open', 'participate', 'close'] if task_id == 12 else ['open', 'participate']
                self.assertEqual(self.actions(), expected)
                fields = [fields for action, fields in self.objects.actions]
                self.assertEqual(len({value['operation_id'] for value in fields}), 1)
                revisions = [value['expected_revision'] for value in fields]
                self.assertEqual(revisions, sorted(set(revisions)))
                self.assertEqual(executor.execute(task_id, character_id=ROLE).status, 'BLOCKED')
                self.assertEqual(self.actions(), expected)

    def test_role_task_sequence_operation_and_revision_mismatch_after_open(self):
        for mutation in ['role', 'task_id', 'sequence', 'operation_id', 'revision', 'stage', 'session']:
            with self.subTest(mutation=mutation):
                self.objects = Objects()
                def drift(action, wire):
                    if mutation == 'role':
                        self.objects.timed['character_id'] = '2222'
                    elif mutation == 'revision':
                        self.objects.timed['revision'] = 1
                    elif mutation == 'session':
                        def lose_session():
                            raise RuntimeError('owner token/session invalidated')
                        self.objects.before_observe = lose_session
                    else:
                        self.objects.operation[mutation] = {
                            'task_id': 13, 'sequence': 11, 'stage': 'participate', 'operation_id': '0' * 32,
                        }[mutation]
                self.objects.after_ack = drift
                result = self.execute()
                self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open'])

    def test_reply_sequence_mismatch_cannot_advance(self):
        self.objects.after_ack = lambda action, wire: wire.update(requested_seq=11)
        self.assertEqual(self.execute().reason, 'daily_timed_command_binding_invalid')
        self.assertEqual(self.actions(), ['open'])

    def test_valid_partial_progress_keeps_account_incomplete(self):
        self.objects.task['required'] = 2
        def settle(action, wire):
            if action == 'participate':
                self.objects.succeed()
        self.objects.after_ack = settle
        result = self.execute()
        self.assertEqual((result.status, result.progress_after, result.required, result.cleanup),
                         ('PARTIAL', 1, 2, 'VERIFIED_CLOSED'))

    def test_counter_change_without_both_native_witnesses_times_out_without_retry(self):
        self.objects = Objects(13, 321)
        def one_witness(action, wire):
            if action == 'participate':
                self.objects.advance('PENDING', current=1)
        self.objects.after_ack = one_witness
        executor = self.executor()
        result = executor.execute(13, character_id=ROLE)
        self.assertEqual((result.status, result.progress_after, result.request_attribution), ('UNKNOWN', None, 'UNKNOWN'))
        self.assertEqual(result.reason, 'daily_timed_readback_timeout')
        self.assertEqual(executor.execute(13, character_id=ROLE).status, 'UNKNOWN')
        self.assertEqual(self.actions(), ['open', 'participate'])

    def test_bad_counter_or_missing_attribution_cannot_claim_completion(self):
        for fault in ('counter_before', 'counter_after', 'required', 'request_attribution', 'opcode'):
            with self.subTest(fault=fault):
                self.objects = Objects()
                def corrupt(action, wire):
                    if action == 'participate':
                        self.objects.succeed()
                        if fault == 'required':
                            self.objects.task['required'] = 2
                        else:
                            self.objects.operation[fault] = {
                                'counter_before': 1, 'counter_after': 0, 'request_attribution': None, 'opcode': 312,
                            }[fault]
                self.objects.after_ack = corrupt
                result = self.execute()
                self.assertEqual((result.status, result.progress_after), ('UNKNOWN', None))
                self.assertEqual(self.actions(), ['open', 'participate'])

    def test_opening_waits_for_ready_and_paused_view_is_reobserved(self):
        def pause_at_open(action, wire):
            if action == 'open':
                self.objects.advance('OPENING')
            elif action == 'participate':
                self.objects.succeed()
        self.objects.after_ack = pause_at_open
        def resume():
            self.wait()
            self.assertEqual(self.actions(), ['open'])
            if self.now == 1:
                self.paused = True
                self.objects.advance('READY', action='participate')
            else:
                self.paused = False
                self.objects.advance('READY', action='participate')
        result = self.executor(resume).execute(12, character_id=ROLE)
        self.assertEqual(result.status, 'COMPLETED')
        self.assertEqual(self.objects.actions[1][1]['expected_revision'], 6)

    def test_stop_blocks_participation_and_preserves_unverified_cleanup(self):
        self.stopped = True
        self.assertEqual(self.execute().status, 'STOPPED')
        self.assertEqual(self.actions(), [])
        self.stopped = False
        def stop(action, wire):
            self.stopped = True
        self.objects.after_ack = stop
        result = self.execute()
        self.assertEqual((result.status, result.cleanup), ('STOPPED', 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open'])

    def test_lost_native_ui_offer_after_open_blocks_participation(self):
        self.objects.after_ack = lambda action, wire: self.objects.advance('READY', reason='daily_timed_modal_active')
        result = self.execute()
        self.assertEqual((result.status, result.reason, result.cleanup),
                         ('BLOCKED', 'daily_timed_modal_active', 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open'])

    def test_command_failure_never_retries(self):
        for reply, status in [
            ({'ack': {'status': 'REJECTED', 'reason': 'daily_timed_gui_or_baseline_changed'}}, 'BLOCKED'),
            ({'ack': {'status': 'COMPLETED'}}, 'UNKNOWN'),
            ({'command': 'stop', 'reason': 'component_stopped'}, 'UNKNOWN'),
            (TimeoutError('no reply'), 'UNKNOWN'),
        ]:
            with self.subTest(reply=reply):
                self.objects = Objects()
                def respond(action):
                    if isinstance(reply, Exception):
                        raise reply
                    return reply
                self.objects.reply_override = respond
                result = self.execute()
                self.assertEqual(result.status, status)
                self.assertEqual(self.actions(), ['open'])

    def test_stop_race_with_admission_never_submits_participation(self):
        def stop(action):
            self.stopped = True
            raise CampaignAdmissionClosed('closed before bridge submission')
        self.objects.reply_override = stop
        self.assertEqual(self.execute().status, 'STOPPED')
        self.assertEqual(self.actions(), ['open'])

    def test_stop_between_quiz_success_and_close_retains_progress_evidence(self):
        def stop(action, wire):
            if action == 'participate':
                self.objects.succeed()
                self.stopped = True
        self.objects.after_ack = stop
        result = self.execute()
        self.assertEqual((result.status, result.progress_after, result.request_attribution, result.cleanup),
                         ('STOPPED', 1, 'UNKNOWN', 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'participate'])

    def test_unavailable_quiz_close_preserves_account_evidence_but_blocks_workflow(self):
        def lose_view(action, wire):
            if action == 'participate':
                self.objects.succeed()
                self.objects.task['action'] = ''
        self.objects.after_ack = lose_view
        result = self.execute()
        self.assertEqual((result.status, result.reason, result.progress_after, result.cleanup),
                         ('BLOCKED', 'daily_timed_quiz_close_unavailable', 1, 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'participate'])

    def test_close_ack_or_wrong_readback_cannot_confirm_cleanup(self):
        for fault, reason in [
            ('pending', 'daily_timed_readback_timeout'),
            ('sequence', 'daily_timed_operation_binding_or_state_changed'),
        ]:
            with self.subTest(fault=fault):
                self.objects = Objects()
                def delay(action, wire):
                    if action == 'participate':
                        self.objects.succeed()
                    elif action == 'close':
                        if fault == 'pending':
                            self.objects.timed = deepcopy(wire['ack']['result']['daily_timed'])
                        else:
                            self.objects.operation['sequence'] = 17
                self.objects.after_ack = delay
                result = self.execute()
                self.assertEqual((result.status, result.reason, result.progress_after, result.cleanup),
                                 ('UNKNOWN', reason, 1, 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open', 'participate', 'close'])


if __name__ == '__main__':
    unittest.main()
