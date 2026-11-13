from copy import deepcopy
import unittest

from jiangshen_assistant.daily_stone import DailyStoneExecutor
from jiangshen_assistant.objects import CampaignAdmissionClosed


ROLE = '12345'
CYCLE = 1793826000
TOKEN = object()


class Objects:
    def __init__(self):
        self.enabled = True
        self.stone = dict(schema=1, revision=1,
            view=dict(task_id=16, role_id=ROLE, cycle=CYCLE, current=0, required=1,
                      silver_picks=4, gold_cost=None, silver_pick_cost=None, next_action='open', reason=''),
            operation=dict(status='IDLE', reason='stone_unobserved', operation_id='', sequence=0,
                           mutation_attempted=False, stock_callback_returned=False,
                           account_progress_observed=False, panel_closed=False))
        self.actions = []
        self.observations = 0
        self.before_observe = lambda: None
        self.after_ack = lambda action, wire: None

    @property
    def view(self):
        return self.stone['view']

    @property
    def op(self):
        return self.stone['operation']

    def advance(self, status, *, offer='', reason='', view=None, **witnesses):
        self.stone['revision'] += 1
        self.op.update(status=status, reason=reason, **witnesses)
        self.view.update(next_action=offer, reason='' if offer else reason,
                         gold_cost=0 if offer == 'dig' else None,
                         silver_pick_cost=1 if offer == 'dig' else None)
        self.view.update(view or {})

    def progress(self, *, offer='close'):
        self.advance('SETTLED', offer=offer, reason='stone_progress_observed',
                     stock_callback_returned=True, account_progress_observed=True,
                     view=dict(current=1, silver_picks=3))

    def observe_daily_stone(self, token):
        assert token is TOKEN
        self.observations += 1
        self.before_observe()
        return {'enabled': self.enabled, 'stone': deepcopy(self.stone) if self.enabled else None}

    def submit_daily_stone(self, token, action, **fields):
        assert token is TOKEN
        assert fields['expected_revision'] == self.stone['revision']
        assert fields['character_id'] == ROLE
        assert fields['task_id'] == 16
        assert self.view['next_action'] == action
        if self.actions:
            assert fields['operation_id'] == self.actions[0][1]['operation_id']
        self.actions.append((action, fields))
        seq, status = {'open': (8, 'OPENING'), 'dig': (12, 'PENDING'), 'close': (27, 'COMPLETED')}[action]
        self.advance(status, mutation_attempted=action != 'open', panel_closed=action == 'close',
                     operation_id=fields['operation_id'], sequence=seq)
        wire = dict(requested_seq=seq, ack=dict(command_seq=seq, status='COMPLETED',
                    result={'daily_stone': deepcopy(self.stone)}))
        if action == 'open':
            self.advance('OPEN', offer='dig')
        self.after_ack(action, wire)
        return wire


class DailyStoneTests(unittest.TestCase):
    def setUp(self):
        self.objects = Objects()
        self.now = 0.0
        self.paused = self.stopped = False

    def wait(self):
        self.now += 1

    def executor(self, wait=None):
        return DailyStoneExecutor(self.objects, TOKEN, stopped=lambda: self.stopped,
            paused=lambda: self.paused, wait=wait or self.wait, timeout=6, clock=lambda: self.now)

    def run_stone(self, executor=None):
        return (executor or self.executor()).execute(16, character_id=ROLE, expected_cycle=CYCLE)

    def actions(self):
        return [action for action, _ in self.objects.actions]

    def test_disabled_stale_cache_and_already_complete_send_nothing(self):
        self.objects.enabled = False
        result = self.run_stone()
        self.assertEqual((result.status, result.reason, result.cleanup), ('UNAVAILABLE', 'stone_disabled', 'NOT_NEEDED'))
        self.objects.enabled = True
        self.objects.view.update(current=1, next_action='', reason='stone_activity_panel_stale')
        self.assertEqual(self.run_stone().status, 'UNAVAILABLE')
        self.objects.view.update(reason='stone_activity_complete')
        result = self.run_stone()
        self.assertEqual((result.status, result.progress_before, result.progress_after), ('ALREADY_COMPLETE', 1, 1))
        self.assertEqual(self.actions(), [])

    def test_role_and_cycle_are_bound_from_first_observation(self):
        for change in (dict(role_id='54321'), dict(cycle=CYCLE + 86400), dict(cycle=0)):
            with self.subTest(change=change):
                self.objects = Objects()
                self.objects.view.update(change)
                self.assertEqual(self.run_stone().status, 'BLOCKED')
                self.assertEqual(self.actions(), [])

    def test_requires_an_existing_pick_and_an_exact_zero_gold_quote(self):
        self.objects.view['silver_picks'] = 0
        self.assertEqual(self.run_stone().status, 'UNAVAILABLE')
        self.assertEqual(self.actions(), [])
        for change in (dict(gold_cost=1), dict(gold_cost=False), dict(silver_pick_cost=None),
                       dict(silver_pick_cost=True), dict(silver_pick_cost=2), dict(silver_picks=3),
                       dict(current=1)):
            with self.subTest(change=change):
                self.objects = Objects()
                self.objects.after_ack = lambda action, wire: self.objects.view.update(change)
                result = self.run_stone()
                self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open'])

    def test_pending_callback_and_delayed_activity_cache_observation(self):
        def wait():
            self.wait()
            if self.now == 1:
                self.objects.advance('PENDING', reason='stone_waiting_stock_callback',
                    view=dict(cycle=0, current=0, required=0, silver_picks=0,
                              reason='stone_client_busy'))
            elif self.now == 2:
                self.objects.advance('PENDING', reason='stone_waiting_stock_callback',
                    stock_callback_returned=True, view=dict(cycle=CYCLE, required=1, current=1, silver_picks=3))
            elif self.now == 4:
                self.objects.progress(offer='')
            elif self.now == 5:
                self.objects.progress()

        result = self.run_stone(self.executor(wait))
        self.assertEqual(self.now, 5)
        self.assertEqual(self.actions(), ['open', 'dig', 'close'])
        self.assertEqual((result.status, result.progress_before, result.progress_after, result.required,
                          result.stock_callback_returned, result.account_progress_observed, result.cleanup),
                         ('COMPLETED', 0, 1, 1, True, True, 'VERIFIED_CLOSED'))

    def test_pending_busy_ack_waits_for_same_cycle_before_next_action(self):
        def after_ack(action, wire):
            if action == 'dig':
                view = wire['ack']['result']['daily_stone']['view']
                view.update(cycle=0, current=0, required=0, silver_picks=0,
                            next_action='', reason='stone_client_busy')
                self.objects.advance('PENDING', reason='stone_waiting_stock_callback')
        self.objects.after_ack = after_ack
        def wait():
            self.wait()
            if self.now == 1:
                self.objects.progress()
        result = self.run_stone(self.executor(wait))
        self.assertEqual(result.status, 'COMPLETED')
        self.assertEqual(self.actions(), ['open', 'dig', 'close'])

    def test_settled_cleanup_failure_keeps_progress_and_stops(self):
        def wait():
            self.wait()
            if self.now == 1:
                self.objects.progress(offer='')
            elif self.now == 2:
                self.objects.advance('UNKNOWN', reason='stone_cleanup_unverified')
        result = self.run_stone(self.executor(wait))
        self.assertEqual((result.status, result.cleanup, result.progress_after,
                          result.stock_callback_returned, result.account_progress_observed),
                         ('UNKNOWN', 'UNVERIFIED', 1, True, True))
        self.assertEqual(self.actions(), ['open', 'dig'])

    def test_changed_role_cycle_revision_operation_or_sequence_stops_after_open(self):
        for scope, change in [
            ('view', dict(role_id='54321')), ('view', dict(cycle=CYCLE + 86400)),
            ('operation', dict(operation_id='unexpected')), ('operation', dict(sequence=9)),
            ('root', dict(revision=1)), ('root', dict(revision=2)),
        ]:
            with self.subTest(scope=scope, change=change):
                self.objects = Objects()
                self.objects.after_ack = lambda action, wire: (
                    self.objects.stone if scope == 'root' else self.objects.stone[scope]).update(change)
                self.assertEqual(self.run_stone().status, 'UNKNOWN')
                self.assertEqual(self.actions(), ['open'])

    def test_ack_sequence_and_binding_mismatch_prevent_next_command(self):
        for change in ('request_seq', 'command_seq', 'snapshot_seq', 'snapshot_role', 'snapshot_op'):
            with self.subTest(change=change):
                self.objects = Objects()

                def alter_ack(action, wire):
                    ack = wire['ack']
                    receipt = ack['result']['daily_stone']
                    if change == 'request_seq':
                        wire['requested_seq'] += 1
                    elif change == 'command_seq':
                        ack['command_seq'] += 1
                    elif change == 'snapshot_seq':
                        receipt['operation']['sequence'] += 1
                    elif change == 'snapshot_role':
                        receipt['view']['role_id'] = '54321'
                    else:
                        receipt['operation']['operation_id'] = 'unexpected'

                self.objects.after_ack = alter_ack
                self.assertEqual(self.run_stone().status, 'UNKNOWN')
                self.assertEqual(self.actions(), ['open'])

    def test_cache_conflict_after_stock_callback_never_sends_close(self):
        def wait():
            self.wait()
            self.objects.advance('UNKNOWN', reason='stone_activity_progress_unobserved',
                stock_callback_returned=True, view=dict(current=1, silver_picks=3))

        result = self.run_stone(self.executor(wait))
        self.assertEqual((result.status, result.reason, result.progress_after, result.cleanup),
                         ('UNKNOWN', 'stone_activity_progress_unobserved', None, 'UNVERIFIED'))
        self.assertTrue(result.stock_callback_returned)
        self.assertFalse(result.account_progress_observed)
        self.assertEqual(self.actions(), ['open', 'dig'])

    def test_stop_prevents_close_and_preserves_observed_progress(self):
        self.objects.after_ack = lambda action, wire: self.objects.progress(offer='') if action == 'dig' else None

        def wait():
            self.wait()
            self.stopped = True

        result = self.run_stone(self.executor(wait))
        self.assertEqual((result.status, result.progress_after, result.cleanup), ('STOPPED', 1, 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'dig'])

    def test_pause_prevents_next_action_until_fresh_observation_after_resume(self):
        def after_ack(action, wire):
            if action == 'open':
                self.paused = True
            if action == 'dig':
                self.objects.progress()

        def wait():
            self.wait()
            self.assertEqual(self.actions(), ['open'])
            if self.now == 2:
                self.paused = False

        self.objects.after_ack = after_ack
        result = self.run_stone(self.executor(wait))
        self.assertEqual(result.status, 'COMPLETED')
        self.assertEqual(self.actions(), ['open', 'dig', 'close'])

    def test_pause_admission_race_cannot_send_or_retry_after_stop(self):
        submit = self.objects.submit_daily_stone

        def raced(token, action, **fields):
            if action == 'dig':
                self.paused = self.stopped = True
                raise CampaignAdmissionClosed('daily owner stopped')
            return submit(token, action, **fields)

        self.objects.submit_daily_stone = raced
        result = self.run_stone()
        self.assertEqual((result.status, result.cleanup), ('STOPPED', 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open'])

    def test_timeout_and_ambiguous_dig_ack_do_not_retry(self):
        for timeout in (True, False):
            with self.subTest(timeout=timeout):
                self.objects = Objects()
                self.now = 0
                if not timeout:
                    self.objects.after_ack = lambda action, wire: (
                        wire['ack'].update(status='ACCEPTED') if action == 'dig' else None)
                executor = self.executor()
                first = self.run_stone(executor)
                again = self.run_stone(executor)
                self.assertEqual((first.status, first.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual((again.status, again.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open', 'dig'])

    def test_native_failed_close_preserves_progress_without_completing(self):
        def after_ack(action, wire):
            if action == 'dig':
                self.objects.progress()
            if action == 'close':
                self.objects.advance('COMPLETED', reason='stone_close_unconfirmed', panel_closed=False)
                wire['ack']['result']['daily_stone'] = deepcopy(self.objects.stone)

        self.objects.after_ack = after_ack
        result = self.run_stone()
        self.assertEqual((result.status, result.progress_after, result.cleanup), ('BLOCKED', 1, 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'dig', 'close'])

    def test_close_ack_alone_cannot_confirm_completion(self):
        def after_ack(action, wire):
            if action == 'dig':
                self.objects.progress()
            if action == 'close':
                self.objects.enabled = False

        self.objects.after_ack = after_ack
        result = self.run_stone()
        self.assertEqual((result.status, result.progress_after, result.cleanup), ('UNKNOWN', 1, 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'dig', 'close'])
