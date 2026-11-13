from copy import deepcopy
import unittest

from jiangshen_assistant.daily_mingshi import DailyMingshiExecutor
from jiangshen_assistant.objects import CampaignAdmissionClosed


ROLE = '12345'
CYCLE = 1793826000
TOKEN = object()


class Objects:
    def __init__(self):
        self.enabled = True
        self.snapshot = dict(schema=1, revision=1,
            view=dict(task_id=7, role_id=ROLE, cycle=CYCLE, current=0, required=1,
                      gold_cost=None, next_action='open', reason=''),
            operation=dict(status='IDLE', reason='mingshi_unobserved', operation_id='', sequence=0,
                           mutation_attempted=False, stock_callback_returned=False,
                           account_progress_observed=False, panel_closed=False))
        self.actions = []
        self.after_ack = lambda action, wire: None

    @property
    def view(self):
        return self.snapshot['view']

    @property
    def operation(self):
        return self.snapshot['operation']

    def advance(self, status, *, offer='', reason='', view=None, **witnesses):
        self.snapshot['revision'] += 1
        self.operation.update(status=status, reason=reason, **witnesses)
        self.view.update(next_action=offer, reason='' if offer else reason,
                         gold_cost=0 if offer == 'harvest' else None)
        self.view.update(view or {})

    def ready(self):
        self.advance('OPEN', offer='harvest', reason='mingshi_stock_panel_ready')

    def settle(self, offer='close'):
        self.advance('SETTLED', offer=offer, reason='mingshi_free_harvest_and_activity_verified',
                     stock_callback_returned=True, account_progress_observed=True, view={'current': 1})

    def close(self, verified=True):
        self.advance('SUCCEEDED', reason='mingshi_stock_return_verified' if verified else 'mingshi_stock_return_unconfirmed',
                     panel_closed=verified)

    def observe_daily_mingshi(self, token):
        assert token is TOKEN
        return dict(enabled=self.enabled, mingshi=deepcopy(self.snapshot) if self.enabled else None)

    def submit_daily_mingshi(self, token, action, **fields):
        assert token is TOKEN
        assert fields['expected_revision'] == self.snapshot['revision']
        assert fields['character_id'] == ROLE and fields['task_id'] == 7
        assert self.view['next_action'] == action
        if self.actions:
            assert fields['operation_id'] == self.actions[0][1]['operation_id']
        self.actions.append((action, fields))
        seq, status = {'open': (8, 'OPENING'), 'harvest': (12, 'PENDING'), 'close': (27, 'CLOSING')}[action]
        self.advance(status, mutation_attempted=action != 'open', operation_id=fields['operation_id'], sequence=seq)
        wire = dict(requested_seq=seq, ack=dict(command_seq=seq, status='COMPLETED',
                    result={'daily_mingshi': deepcopy(self.snapshot)}))
        if action == 'open':
            self.ready()
        self.after_ack(action, wire)
        return wire


class DailyMingshiTests(unittest.TestCase):
    def setUp(self):
        self.objects = Objects()
        self.now = 0.0
        self.paused = self.stopped = False

    def wait(self):
        self.now += 1

    def executor(self, wait=None):
        return DailyMingshiExecutor(self.objects, TOKEN, stopped=lambda: self.stopped,
            paused=lambda: self.paused, wait=wait or self.wait, timeout=6, clock=lambda: self.now)

    def run_task(self, executor=None):
        return (executor or self.executor()).execute(7, character_id=ROLE, expected_cycle=CYCLE)

    def actions(self):
        return [action for action, _ in self.objects.actions]

    def test_disabled_and_inconsistent_caches_send_nothing(self):
        self.objects.enabled = False
        result = self.run_task()
        self.assertEqual((result.status, result.cleanup), ('UNAVAILABLE', 'NOT_NEEDED'))
        self.objects.enabled = True
        self.objects.view.update(current=1, next_action='', reason='mingshi_activity_cache_mismatch')
        self.assertEqual(self.run_task().status, 'UNAVAILABLE')
        self.objects.view['reason'] = 'mingshi_first_harvest_used'
        result = self.run_task()
        self.assertEqual((result.status, result.progress_before, result.progress_after),
                         ('ALREADY_COMPLETE', 1, 1))
        self.assertEqual(self.actions(), [])

    def test_requires_same_role_cycle_and_task_before_open(self):
        for change in (dict(role_id='54321'), dict(cycle=CYCLE + 86400), dict(cycle=0), dict(task_id=8)):
            with self.subTest(change=change):
                self.objects = Objects()
                self.objects.view.update(change)
                self.assertEqual(self.run_task().status, 'BLOCKED')
                self.assertEqual(self.actions(), [])

    def test_paid_or_malformed_quote_never_sends_harvest(self):
        for cost in (False, True, None, 1, '0', -1):
            with self.subTest(cost=cost):
                self.objects = Objects()
                self.objects.after_ack = lambda action, wire: self.objects.view.update(gold_cost=cost)
                result = self.run_task()
                self.assertEqual((result.status, result.cleanup, result.mutation_attempted),
                                 ('UNKNOWN', 'UNVERIFIED', False))
                self.assertEqual(self.actions(), ['open'])

    def test_waits_for_both_receipts_and_verifies_stock_return(self):
        def wait():
            self.wait()
            if self.now == 1:
                self.objects.advance('PENDING', reason='mingshi_waiting_1202_and_355',
                                     stock_callback_returned=True)
            elif self.now == 2:
                self.objects.settle(offer='')
            elif self.now == 3:
                self.objects.settle()
            elif self.now == 4:
                self.objects.close()

        result = self.run_task(self.executor(wait))
        self.assertEqual(self.now, 4)
        self.assertEqual(self.actions(), ['open', 'harvest', 'close'])
        self.assertEqual((result.status, result.progress_before, result.progress_after, result.required,
                          result.stock_callback_returned, result.account_progress_observed, result.cleanup,
                          result.mutation_attempted), ('COMPLETED', 0, 1, 1, True, True, 'VERIFIED_CLOSED', True))

    def test_355_without_1202_stays_pending_then_unknown(self):
        def wait():
            self.wait()
            if self.now == 1:
                self.objects.advance('PENDING', reason='mingshi_waiting_1202_and_355', view={'current': 1})

        result = self.run_task(self.executor(wait))
        self.assertEqual((result.status, result.progress_after, result.account_progress_observed),
                         ('UNKNOWN', None, False))
        self.assertEqual(self.actions(), ['open', 'harvest'])

    def test_ack_is_bound_to_id_role_cycle_revision_and_command_sequence(self):
        for bad in ('wire_sequence', 'ack_sequence', 'receipt_sequence', 'role', 'cycle', 'revision', 'id'):
            with self.subTest(bad=bad):
                self.objects = Objects()

                def alter(action, wire):
                    ack = wire['ack']
                    receipt = ack['result']['daily_mingshi']
                    if bad == 'wire_sequence':
                        wire['requested_seq'] += 1
                    elif bad == 'ack_sequence':
                        ack['command_seq'] += 1
                    elif bad == 'receipt_sequence':
                        receipt['operation']['sequence'] += 1
                    elif bad == 'role':
                        receipt['view']['role_id'] = '54321'
                    elif bad == 'cycle':
                        receipt['view']['cycle'] += 86400
                    elif bad == 'revision':
                        receipt['revision'] = 1
                    else:
                        receipt['operation']['operation_id'] = 'b' * 32

                self.objects.after_ack = alter
                self.assertEqual(self.run_task().status, 'UNKNOWN')
                self.assertEqual(self.actions(), ['open'])

    def test_readback_cannot_replace_operation_or_revise_an_existing_revision(self):
        for scope, change in [('view', dict(role_id='54321')), ('view', dict(cycle=CYCLE + 86400)),
                              ('operation', dict(operation_id='b' * 32)), ('operation', dict(sequence=9)),
                              ('root', dict(revision=2))]:
            with self.subTest(change=change):
                self.objects = Objects()

                def alter(action, wire):
                    target = self.objects.snapshot if scope == 'root' else self.objects.snapshot[scope]
                    target.update(change)

                self.objects.after_ack = alter
                self.assertEqual(self.run_task().status, 'UNKNOWN')
                self.assertEqual(self.actions(), ['open'])

    def test_stop_during_pending_and_after_settlement_keeps_evidence(self):
        for settled in (False, True):
            with self.subTest(settled=settled):
                self.objects = Objects()
                self.now = 0
                self.stopped = False
                if settled:
                    self.objects.after_ack = lambda action, wire: self.objects.settle(offer='') if action == 'harvest' else None

                def wait():
                    self.stopped = True

                result = self.run_task(self.executor(wait))
                self.assertEqual((result.status, result.account_progress_observed, result.progress_after, result.cleanup),
                                 ('STOPPED', settled, 1 if settled else None, 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open', 'harvest'])

    def test_pause_waits_and_uses_a_fresh_observation_after_resume(self):
        def after(action, wire):
            if action == 'open':
                self.paused = True
            elif action == 'harvest':
                self.objects.settle()
            else:
                self.objects.close()

        def wait():
            self.wait()
            self.assertEqual(self.actions(), ['open'])
            self.objects.ready()
            if self.now == 2:
                self.paused = False

        self.objects.after_ack = after
        self.assertEqual(self.run_task(self.executor(wait)).status, 'COMPLETED')
        self.assertEqual(self.actions(), ['open', 'harvest', 'close'])

    def test_stop_wins_at_object_admission(self):
        submit = self.objects.submit_daily_mingshi

        def raced(token, action, **fields):
            if action == 'harvest':
                self.stopped = True
                raise CampaignAdmissionClosed('daily owner stopped')
            return submit(token, action, **fields)

        self.objects.submit_daily_mingshi = raced
        result = self.run_task()
        self.assertEqual((result.status, result.cleanup, result.mutation_attempted), ('STOPPED', 'UNVERIFIED', False))
        self.assertEqual(self.actions(), ['open'])

    def test_unknown_ack_or_readback_never_retries(self):
        for unknown_ack in (True, False):
            with self.subTest(unknown_ack=unknown_ack):
                self.objects = Objects()
                self.now = 0
                if unknown_ack:
                    self.objects.after_ack = lambda action, wire: (
                        wire['ack'].update(status='UNKNOWN', reason='mingshi_stock_1202_unbound')
                        if action == 'harvest' else None)
                executor = self.executor()
                first, again = self.run_task(executor), self.run_task(executor)
                self.assertEqual((first.status, first.cleanup, first.mutation_attempted), ('UNKNOWN', 'UNVERIFIED', True))
                self.assertEqual((again.status, again.cleanup, again.mutation_attempted), ('UNKNOWN', 'UNVERIFIED', True))
                self.assertEqual(self.actions(), ['open', 'harvest'])

    def test_close_ack_alone_cannot_confirm_return(self):
        def after(action, wire):
            if action == 'harvest':
                self.objects.settle()
            if action == 'close':
                self.objects.close()
                wire['ack']['result']['daily_mingshi'] = deepcopy(self.objects.snapshot)
                self.objects.enabled = False

        self.objects.after_ack = after
        result = self.run_task()
        self.assertEqual((result.status, result.progress_after, result.cleanup), ('UNKNOWN', 1, 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'harvest', 'close'])

    def test_unconfirmed_close_keeps_verified_progress_and_stops(self):
        def after(action, wire):
            if action == 'harvest':
                self.objects.settle()
            if action == 'close':
                self.objects.close(verified=False)

        self.objects.after_ack = after
        result = self.run_task()
        self.assertEqual((result.status, result.reason, result.progress_after, result.cleanup),
            ('BLOCKED', 'mingshi_stock_return_unconfirmed', 1, 'UNVERIFIED'))
        self.assertTrue(result.stock_callback_returned and result.account_progress_observed)
        self.assertEqual(self.actions(), ['open', 'harvest', 'close'])

    def test_lost_owned_panel_after_progress_does_not_send_close(self):
        def after(action, wire):
            if action == 'harvest':
                self.objects.settle()
                self.objects.advance('SUCCEEDED', reason='mingshi_stock_return_relinquished')

        self.objects.after_ack = after
        result = self.run_task()
        self.assertEqual((result.status, result.reason, result.progress_after, result.cleanup),
            ('BLOCKED', 'mingshi_stock_return_relinquished', 1, 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'harvest'])

    def test_progress_witnesses_cannot_recede_after_close(self):
        def after(action, wire):
            if action == 'harvest':
                self.objects.settle()
            if action == 'close':
                self.objects.close()
                self.objects.operation['stock_callback_returned'] = False

        self.objects.after_ack = after
        result = self.run_task()
        self.assertEqual((result.status, result.progress_after, result.cleanup), ('UNKNOWN', 1, 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'harvest', 'close'])

    def test_prior_verified_operation_does_not_bind_new_cycle(self):
        self.objects.operation.update(status='SUCCEEDED', operation_id='a' * 32, sequence=3,
                                      mutation_attempted=True, stock_callback_returned=True,
                                      account_progress_observed=True, panel_closed=True)

        def after(action, wire):
            if action == 'open':
                receipt = wire['ack']['result']['daily_mingshi']['operation']
                for key in ('stock_callback_returned', 'account_progress_observed', 'panel_closed'):
                    self.objects.operation[key] = receipt[key] = False
            elif action == 'harvest':
                self.objects.settle()
            else:
                self.objects.close()

        self.objects.after_ack = after
        self.assertEqual(self.run_task().status, 'COMPLETED')
        self.assertEqual(self.actions(), ['open', 'harvest', 'close'])
