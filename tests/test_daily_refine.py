from copy import deepcopy
import unittest

from jiangshen_assistant.daily_refine import DailyRefineExecutor
from jiangshen_assistant.objects import CampaignAdmissionClosed


ROLE = '12345'
CYCLE = 1793826000
TOKEN = object()
START_MONEY = 5_000_000_025
COST = 40
PRACTICE = 13


class Objects:
    def __init__(self, *, current=1, required=3, cycle=CYCLE):
        self.enabled = True
        self.snapshot = dict(schema=1, revision=1,
            view=dict(task_id=21, role_id=ROLE, cycle=cycle, current=current, required=required, money=START_MONEY,
                      money_cost=COST, coin_exchanges=5, gold_cost=None, next_action='open', reason=''),
            operation=dict(status='IDLE', reason='refine_unobserved', operation_id='', sequence=0,
                           mutation_attempted=False, stock_callback_returned=False, account_progress_observed=False,
                           panel_closed=False, ui_ownership_held=False, coins_spent=0, practice_credit=0))
        self.current, self.cycle = current, cycle
        self.actions = []
        self.after_ack = lambda action, wire: None
        self.before_submit = lambda action: None
        self.on_observe = lambda: None
        self.close_verified = True

    @property
    def view(self):
        return self.snapshot['view']

    @property
    def operation(self):
        return self.snapshot['operation']

    def advance(self, status, *, offer='', reason='', view=None, **witnesses):
        self.snapshot['revision'] += 1
        self.operation.update(status=status, reason=reason, **witnesses)
        self.view.update(next_action=offer, gold_cost=0 if offer == 'refine' else None, reason='' if offer else reason)
        self.view.update(view or {})

    def ready(self):
        self.advance('OPEN', offer='refine')

    def coin_debit(self):
        self.advance('PENDING', reason='refine_waiting_practice_credit', stock_callback_returned=True,
                     view=dict(money=START_MONEY-COST, coin_exchanges=6, money_cost=None))

    def progress_push(self):
        self.snapshot['revision'] += 1
        self.view['current'] = self.current + 1

    def settle(self, offer='close'):
        self.advance('SETTLED', offer=offer, reason='refine_progress_observed',
                     stock_callback_returned=True, account_progress_observed=True,
                     coins_spent=COST, practice_credit=PRACTICE,
                     view=dict(current=self.current+1, money=START_MONEY-COST, coin_exchanges=6, money_cost=None))

    @staticmethod
    def zero_busy(snapshot):
        snapshot['view'].update(role_id='0', cycle=0, current=0, required=0, money=0, coin_exchanges=0,
                                money_cost=None, gold_cost=None, next_action='', reason='refine_client_busy')

    def observe_daily_refine(self, token):
        assert token is TOKEN
        self.on_observe()
        return dict(enabled=self.enabled, refine=deepcopy(self.snapshot) if self.enabled else None)

    def submit_daily_refine(self, token, action, **fields):
        assert token is TOKEN
        assert fields['expected_revision'] == self.snapshot['revision']
        assert fields['character_id'] == ROLE and fields['task_id'] == 21
        assert self.view['next_action'] == action
        if self.actions:
            assert fields['operation_id'] == self.actions[0][1]['operation_id']
        self.before_submit(action)
        self.actions.append((action, fields))
        seq, status = {'open': (8, 'OPENING'), 'refine': (12, 'PENDING'), 'close': (27, 'COMPLETED')}[action]
        closed = action == 'close' and self.close_verified
        reason = 'refine_close_unconfirmed' if action == 'close' and not closed else ''
        if action == 'open':
            self.operation.update(stock_callback_returned=False, account_progress_observed=False, coins_spent=0, practice_credit=0)
        self.advance(status, reason=reason, mutation_attempted=action != 'open', ui_ownership_held=not closed,
                     panel_closed=closed, operation_id=fields['operation_id'], sequence=seq)
        wire = dict(requested_seq=seq, ack=dict(command_name='daily-refine-command', command_seq=seq, status='COMPLETED',
                    result={'daily_refine': deepcopy(self.snapshot)}))
        if action == 'open':
            self.ready()
        if closed and self.view['current'] < self.view['required']:
            self.snapshot['revision'] += 1
            self.view.update(next_action='open', money_cost=50)
        self.after_ack(action, wire)
        return wire


class DailyRefineTests(unittest.TestCase):
    def setUp(self):
        self.objects = Objects()
        self.now = 0.0
        self.paused = self.stopped = False

    def wait(self):
        self.now += 1

    def executor(self, wait=None):
        return DailyRefineExecutor(self.objects, TOKEN, stopped=lambda: self.stopped,
            paused=lambda: self.paused, wait=wait or self.wait, timeout=6, clock=lambda: self.now)

    def run_task(self, executor=None, **kwargs):
        return (executor or self.executor()).execute(21, character_id=ROLE, expected_cycle=kwargs.pop('cycle', CYCLE), **kwargs)

    def actions(self):
        return [action for action, _ in self.objects.actions]

    def test_one_exchange_with_delayed_credit_and_distinct_observations(self):
        def wait():
            self.wait()
            self.assertEqual(self.actions(), ['open', 'refine'])
            if self.now == 1:
                self.objects.progress_push()
            elif self.now == 2:
                self.objects.coin_debit()
            elif self.now == 3:
                self.objects.settle()

        executor = self.executor(wait)
        result = self.run_task(executor, expected_current=1, expected_required=3)
        self.assertEqual(self.actions(), ['open', 'refine', 'close'])
        self.assertEqual((result.status, result.cleanup, result.ui_ownership_held), ('COMPLETED', 'VERIFIED_CLOSED', False))
        self.assertEqual((result.progress_before, result.progress_after, result.required), (1, 2, 3))
        self.assertEqual((result.coins_spent, result.practice_credit), (COST, PRACTICE))
        self.assertEqual((result.money_before, result.money_after), (START_MONEY, START_MONEY-COST))
        self.assertEqual((result.coin_exchanges_before, result.coin_exchanges_after), (5, 6))
        self.assertTrue(result.stock_callback_returned and result.account_progress_observed and result.mutation_attempted)
        self.assertEqual(self.objects.view['next_action'], 'open')
        self.assertEqual(self.run_task(executor).reason, 'refine_previous_attempt')
        self.assertEqual(self.actions(), ['open', 'refine', 'close'])

    def test_no_open_when_quote_missing_unaffordable_or_at_limit(self):
        for change in (dict(money_cost=None), dict(money=COST-1), dict(coin_exchanges=20),
                       dict(current=3, next_action='', reason='refine_activity_panel_stale')):
            with self.subTest(change=change):
                self.objects = Objects()
                self.objects.view.update(change)
                self.assertEqual(self.run_task().status, 'UNAVAILABLE')
                self.assertEqual(self.actions(), [])
        self.objects.enabled = False
        self.assertEqual(self.run_task().status, 'UNAVAILABLE')
        self.objects = Objects(current=3)
        self.objects.view.update(next_action='', reason='refine_activity_complete', money_cost=None)
        self.assertEqual(self.run_task().status, 'ALREADY_COMPLETE')
        self.assertEqual(self.actions(), [])

    def test_requires_pinned_role_cycle_and_outer_cache_before_open(self):
        for change in (dict(role_id='54321'), dict(cycle=CYCLE+86400), dict(current=2), dict(required=4)):
            with self.subTest(change=change):
                self.objects = Objects()
                self.objects.view.update(change)
                result = self.run_task(expected_current=1, expected_required=3)
                self.assertEqual(result.status, 'UNKNOWN')
                self.assertEqual(self.actions(), [])

    def test_rejects_changed_quote_balance_cache_or_nonzero_gold_after_open(self):
        for change in (dict(money_cost=COST+1), dict(money_cost=None), dict(money=START_MONEY-1),
                       dict(current=2), dict(required=4), dict(coin_exchanges=6),
                       dict(gold_cost=None), dict(gold_cost=1), dict(gold_cost=False)):
            with self.subTest(change=change):
                self.objects = Objects()
                self.objects.after_ack = lambda action, wire: self.objects.view.update(change)
                result = self.run_task()
                self.assertEqual((result.status, result.cleanup, result.mutation_attempted), ('UNKNOWN', 'UNVERIFIED', False))
                self.assertEqual(self.actions(), ['open'])

    def test_foreign_or_stale_ack_never_authorizes_next_action(self):
        def change(which, wire):
            receipt = wire['ack']['result']['daily_refine']
            if which == 'command':
                wire['ack']['command_name'] = 'daily-stone-command'
            if which == 'sequence':
                wire['requested_seq'] += 1
            if which == 'operation':
                receipt['operation']['operation_id'] = '1' * 32
            if which == 'cycle':
                receipt['view']['cycle'] += 86400
            if which == 'revision':
                receipt['revision'] -= 1

        for which in ('command', 'sequence', 'operation', 'cycle', 'revision'):
            with self.subTest(which=which):
                self.objects = Objects()
                self.objects.after_ack = lambda action, wire: change(which, wire) if action == 'refine' else None
                executor = self.executor()
                result = self.run_task(executor)
                self.assertEqual((result.status, result.cleanup, result.mutation_attempted), ('UNKNOWN', 'UNVERIFIED', None))
                self.assertEqual(self.run_task(executor).reason, 'refine_previous_attempt')
                self.assertEqual(self.actions(), ['open', 'refine'])

    def test_positive_cycle_changes_stop_at_each_boundary(self):
        for stage in ('open', 'refine', 'close'):
            with self.subTest(stage=stage):
                self.objects = Objects()
                def after_ack(action, wire):
                    if action == 'refine':
                        self.objects.settle()
                    if action == stage:
                        self.objects.snapshot['revision'] += 1
                        self.objects.view['cycle'] += 86400
                self.objects.after_ack = after_ack
                result = self.run_task()
                self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open', 'refine', 'close'][:('open', 'refine', 'close').index(stage)+1])
        self.objects = Objects(cycle=CYCLE+86400)
        self.objects.after_ack = lambda action, wire: self.objects.settle() if action == 'refine' else None
        self.assertEqual(self.run_task(cycle=CYCLE+86400).status, 'COMPLETED')

    def test_disagreed_coin_debit_or_progress_prevents_close(self):
        changes = (dict(view=dict(money=START_MONEY-COST-1)), dict(view=dict(coin_exchanges=7)),
                   dict(view=dict(current=3)), dict(coins_spent=COST+1), dict(practice_credit=0),
                   dict(stock_callback_returned=False), dict(ui_ownership_held=False))
        for change in changes:
            with self.subTest(change=change):
                self.objects = Objects()
                def after_ack(action, wire):
                    if action == 'refine':
                        self.objects.settle()
                        updated = change.get('view')
                        if updated is None:
                            self.objects.operation.update(change)
                        else:
                            self.objects.view.update(updated)
                self.objects.after_ack = after_ack
                result = self.run_task()
                self.assertEqual((result.status, result.cleanup, result.coins_spent), ('UNKNOWN', 'UNVERIFIED', None))
                self.assertEqual(self.actions(), ['open', 'refine'])

    def test_busy_snapshot_is_waitable_but_has_no_progress_evidence(self):
        def wait():
            self.wait()
            self.assertEqual(self.actions(), ['open', 'refine'])
            self.objects.settle()
            self.objects.view.update(role_id=ROLE, cycle=CYCLE, required=3)
        def after_ack(action, wire):
            if action == 'refine':
                self.objects.advance('PENDING', reason='refine_client_busy',
                    view=dict(role_id='0', cycle=0, current=0, required=0, money=0, coin_exchanges=0, money_cost=None))
        self.objects.after_ack = after_ack
        self.assertEqual(self.run_task(self.executor(wait)).status, 'COMPLETED')
        self.assertEqual(self.actions(), ['open', 'refine', 'close'])

    def test_busy_close_requires_fresh_readback_and_completed_gui_when_ack_is_busy(self):
        busy_ack_cases = ('ack', 'no_gui', 'wrong_gui', 'wrong_gui_sequence', 'no_invocation')
        cases = ('observation', *busy_ack_cases, 'timeout', 'cycle', 'debit', 'credit', 'operation', 'revision', 'hold')
        for case in cases:
            with self.subTest(case=case):
                self.objects = Objects()
                ready = None
                waits = []

                def after_ack(action, wire):
                    nonlocal ready
                    if action == 'refine':
                        self.objects.settle()
                    if action == 'close':
                        ready = deepcopy(self.objects.snapshot)
                        Objects.zero_busy(self.objects.snapshot)
                        if case in busy_ack_cases:
                            ack = wire['ack']
                            Objects.zero_busy(ack['result']['daily_refine'])
                            if case != 'no_gui':
                                wire['state'] = dict(campaign_gui=dict(
                                    command_seq=ack['command_seq'] + (case == 'wrong_gui_sequence'),
                                    phase='unknown' if case == 'wrong_gui' else 'completed',
                                    invocation_attempted=case != 'no_invocation'),
                                    daily_refine=deepcopy(ack['result']['daily_refine']))
                        elif case == 'credit':
                            self.objects.operation['practice_credit'] += 1
                        elif case == 'operation':
                            self.objects.operation['operation_id'] = 'e' * 32
                        elif case == 'revision':
                            self.objects.snapshot['revision'] -= 1
                        elif case == 'hold':
                            self.objects.operation.update(panel_closed=False, ui_ownership_held=True)

                def wait():
                    self.wait()
                    waits.append(True)
                    self.assertEqual(self.actions(), ['open', 'refine', 'close'])
                    if case in ('observation', 'ack', 'cycle', 'debit'):
                        self.objects.snapshot = ready
                        self.objects.snapshot['revision'] += 1
                        if case == 'cycle':
                            self.objects.view['cycle'] += 86400
                        if case == 'debit':
                            self.objects.view['money'] -= 1

                self.objects.after_ack = after_ack
                result = self.run_task(self.executor(wait))
                if case in ('observation', 'ack'):
                    self.assertTrue(waits)
                    self.assertEqual((result.status, result.cleanup, result.coins_spent, result.practice_credit),
                                     ('COMPLETED', 'VERIFIED_CLOSED', COST, PRACTICE))
                else:
                    self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open', 'refine', 'close'])

    def test_close_ack_anchors_to_newer_authoritative_state(self):
        cases = ('unknown', 'hold', 'op', 'seq', 'role', 'cycle', 'credit', 'debit', 'same_revision',
                 'older_observation', 'compatible_busy', 'compatible_closed')
        for case in cases:
            with self.subTest(case=case):
                self.objects = Objects()
                readback = None

                def after_ack(action, wire):
                    nonlocal readback
                    if action == 'refine':
                        self.objects.settle()
                    if action != 'close':
                        return
                    ack = wire['ack']
                    receipt = ack['result']['daily_refine']
                    receipt['revision'] = 6
                    Objects.zero_busy(receipt)
                    authoritative = deepcopy(self.objects.snapshot)
                    authoritative['revision'] = 9
                    readback = deepcopy(authoritative)
                    readback['revision'] = 8 if case in ('unknown', 'older_observation') else 10
                    if case == 'unknown':
                        authoritative['operation'].update(status='UNKNOWN', panel_closed=False, ui_ownership_held=True)
                    elif case == 'hold':
                        authoritative['operation'].update(panel_closed=False, ui_ownership_held=True)
                    elif case == 'op':
                        authoritative['operation']['operation_id'] = 'e' * 32
                    elif case == 'seq':
                        authoritative['operation']['sequence'] += 1
                    elif case == 'role':
                        authoritative['view']['role_id'] = '12346'
                    elif case == 'cycle':
                        authoritative['view']['cycle'] += 86400
                    elif case == 'credit':
                        authoritative['operation']['practice_credit'] += 1
                    elif case == 'debit':
                        authoritative['view']['money'] -= 1
                    elif case == 'same_revision':
                        authoritative['revision'] = receipt['revision']
                    elif case == 'compatible_busy':
                        Objects.zero_busy(authoritative)
                    self.objects.snapshot = readback
                    wire['state'] = dict(daily_refine=authoritative, campaign_gui=dict(command_seq=ack['command_seq'],
                                         phase='completed', invocation_attempted=True))

                self.objects.after_ack = after_ack
                result = self.run_task()
                if case.startswith('compatible_'):
                    self.assertEqual((result.status, result.cleanup), ('COMPLETED', 'VERIFIED_CLOSED'))
                else:
                    expected = 'refine_observation_revision_changed' if case == 'older_observation' else 'refine_command_state_conflict'
                    self.assertEqual((result.status, result.reason, result.cleanup), ('UNKNOWN', expected, 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open', 'refine', 'close'])

    def test_pending_ack_anchors_to_authoritative_busy_settlement(self):
        for stale_readback in (False, True):
            with self.subTest(stale_readback=stale_readback):
                self.objects = Objects()

                def after_ack(action, wire):
                    if action != 'refine':
                        return
                    self.objects.settle()
                    authoritative = deepcopy(self.objects.snapshot)
                    Objects.zero_busy(authoritative)
                    authoritative['revision'] += 1
                    if not stale_readback:
                        self.objects.snapshot['revision'] = authoritative['revision'] + 1
                    wire['state'] = dict(daily_refine=authoritative)

                self.objects.after_ack = after_ack
                result = self.run_task()
                if stale_readback:
                    self.assertEqual((result.status, result.reason, result.cleanup),
                                     ('UNKNOWN', 'refine_observation_revision_changed', 'UNVERIFIED'))
                    self.assertEqual(self.actions(), ['open', 'refine'])
                else:
                    self.assertEqual((result.status, result.cleanup), ('COMPLETED', 'VERIFIED_CLOSED'))
                    self.assertEqual(self.actions(), ['open', 'refine', 'close'])

    def test_historical_closed_busy_waits_without_authorizing_new_operation(self):
        for change in ('timeout', 'ready', 'other_op', 'cycle', 'credit', 'from_idle'):
            with self.subTest(change=change):
                self.objects = Objects()
                self.objects.operation.update(status='COMPLETED', operation_id='d' * 32, sequence=3, panel_closed=True,
                    mutation_attempted=True, stock_callback_returned=True, account_progress_observed=True,
                    coins_spent=COST, practice_credit=PRACTICE)
                ready = deepcopy(self.objects.snapshot)
                Objects.zero_busy(self.objects.snapshot)
                if change == 'from_idle':
                    self.objects.operation.update(status='IDLE', operation_id='', sequence=0, panel_closed=False,
                        mutation_attempted=False, stock_callback_returned=False, account_progress_observed=False,
                        coins_spent=0, practice_credit=0)
                self.objects.after_ack = lambda action, wire: self.objects.settle() if action == 'refine' else None

                def wait():
                    self.wait()
                    self.assertEqual(self.actions(), [])
                    if change != 'timeout':
                        self.objects.snapshot = ready
                        self.objects.snapshot['revision'] += 1
                        if change == 'other_op':
                            self.objects.operation['operation_id'] = 'c' * 32
                        if change == 'cycle':
                            self.objects.view['cycle'] += 86400
                        if change == 'credit':
                            self.objects.operation['practice_credit'] += 1

                result = self.run_task(self.executor(wait), expected_current=1, expected_required=3)
                if change == 'ready':
                    self.assertEqual(result.status, 'COMPLETED')
                    self.assertEqual(self.actions(), ['open', 'refine', 'close'])
                elif change == 'timeout':
                    self.assertEqual((result.status, result.cleanup, result.coins_spent),
                                     ('UNAVAILABLE', 'NOT_NEEDED', None))
                    self.assertEqual(self.actions(), [])
                else:
                    self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                    self.assertEqual(self.actions(), [])

    def test_initial_idle_busy_times_out_without_latching_an_attempt(self):
        ready_view = deepcopy(self.objects.view)
        self.objects.view.update(role_id='0', cycle=0, current=0, required=0, money=0, coin_exchanges=0,
                                 money_cost=None, next_action='', reason='refine_client_busy')
        executor = self.executor()
        result = self.run_task(executor)
        self.assertEqual((result.status, result.reason, result.cleanup, result.mutation_attempted),
                         ('UNAVAILABLE', 'refine_client_busy', 'NOT_NEEDED', False))
        self.assertEqual(self.actions(), [])

        self.objects.snapshot['revision'] += 1
        self.objects.view.update(ready_view)
        self.objects.after_ack = lambda action, wire: self.objects.settle() if action == 'refine' else None
        self.assertEqual(self.run_task(executor).status, 'COMPLETED')
        self.assertEqual(self.actions(), ['open', 'refine', 'close'])

    def test_initial_idle_busy_never_binds_an_unrestored_role_or_cycle(self):
        for change in ({}, dict(role_id='54321'), dict(cycle=CYCLE+86400)):
            with self.subTest(change=change):
                self.objects = Objects()
                ready_view = deepcopy(self.objects.view)
                self.objects.view.update(role_id='0', cycle=0, current=0, required=0, money=0, coin_exchanges=0,
                                         money_cost=None, next_action='', reason='refine_client_busy')
                self.objects.after_ack = lambda action, wire: self.objects.settle() if action == 'refine' else None

                def wait():
                    self.wait()
                    self.assertEqual(self.actions(), [])
                    self.objects.snapshot['revision'] += 1
                    self.objects.view.update(ready_view)
                    self.objects.view.update(change)

                result = self.run_task(self.executor(wait))
                self.assertEqual(result.status, 'UNKNOWN' if change else 'COMPLETED')
                self.assertEqual(self.actions(), [] if change else ['open', 'refine', 'close'])

    def test_bound_initial_open_rejection_without_ui_hold_is_safe(self):
        def reject(action, wire):
            ack = wire['ack']
            ack.update(status='REJECTED', reason='refine_cancelled_before_call')
            snapshot = ack['result']['daily_refine']
            snapshot['operation'].update(status='REJECTED', ui_ownership_held=False)
            self.objects.snapshot = deepcopy(snapshot)

        self.objects.after_ack = reject
        result = self.run_task()
        self.assertEqual((result.status, result.reason, result.cleanup, result.mutation_attempted, result.ui_ownership_held),
                         ('BLOCKED', 'refine_cancelled_before_call', 'NOT_NEEDED', False, False))
        self.assertEqual(self.actions(), ['open'])

    def test_initial_rejection_allows_native_open_offer_and_noncontradictory_state(self):
        for relationship in ('absent', 'older_baseline', 'older', 'same', 'newer'):
            with self.subTest(relationship=relationship):
                self.objects = Objects()
                baseline = deepcopy(self.objects.snapshot)

                def reject(action, wire):
                    ack = wire['ack']
                    ack.update(status='REJECTED', reason='refine_cancelled_before_call')
                    receipt = ack['result']['daily_refine']
                    receipt['revision'] += 1
                    receipt['operation'].update(status='REJECTED', ui_ownership_held=False)
                    receipt['view'].update(next_action='open', reason='')
                    if relationship == 'absent':
                        return
                    current = deepcopy(receipt)
                    phase, invoked = 'not_invoked', False
                    if relationship == 'older_baseline':
                        current = baseline
                        phase = 'queued'
                    if relationship == 'older':
                        current['revision'] -= 1
                        current['operation']['status'] = 'QUEUED'
                        current['view']['next_action'] = ''
                        phase = 'queued'
                    if relationship == 'newer':
                        current['revision'] += 1
                        current['view']['money'] += 1
                        phase, invoked = 'rejected', True
                    wire['state'] = dict(daily_refine=current, campaign_gui=dict(command_seq=ack['command_seq'],
                                        phase=phase, invocation_attempted=invoked))

                self.objects.after_ack = reject
                result = self.run_task()
                self.assertEqual((result.status, result.cleanup), ('BLOCKED', 'NOT_NEEDED'))
                self.assertEqual(self.actions(), ['open'])

    def test_initial_rejection_never_accepts_impossible_offers(self):
        for offer in ('refine', 'close'):
            with self.subTest(offer=offer):
                self.objects = Objects()

                def reject(action, wire):
                    ack = wire['ack']
                    ack['status'] = 'REJECTED'
                    receipt = ack['result']['daily_refine']
                    receipt['operation'].update(status='REJECTED', ui_ownership_held=False)
                    receipt['view'].update(next_action=offer, gold_cost=0 if offer == 'refine' else None)

                self.objects.after_ack = reject
                result = self.run_task()
                self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open'])

    def test_initial_rejection_does_not_override_authoritative_contradictions(self):
        for contradiction in ('unknown', 'hold', 'mutation', 'other_op', 'sequence', 'role', 'cycle',
                              'offer', 'same_revision', 'gui_unknown', 'older_scope_gui_unknown', 'older_gui_unknown',
                              'older_foreign_unknown', 'older_foreign_held', 'older_foreign_queued', 'newer_gui', 'missing'):
            with self.subTest(contradiction=contradiction):
                self.objects = Objects()

                def reject(action, wire):
                    ack = wire['ack']
                    ack.update(status='REJECTED', reason='refine_cancelled_before_call')
                    receipt = ack['result']['daily_refine']
                    receipt['revision'] += 1
                    receipt['operation'].update(status='REJECTED', ui_ownership_held=False)
                    current = deepcopy(receipt)
                    current['revision'] += 1
                    gui = dict(command_seq=ack['command_seq'], phase='not_invoked', invocation_attempted=False)
                    wire['state'] = dict(daily_refine=current, campaign_gui=gui)
                    if contradiction == 'unknown':
                        current['operation']['status'] = 'UNKNOWN'
                    elif contradiction == 'hold':
                        current['operation']['ui_ownership_held'] = True
                    elif contradiction == 'mutation':
                        current['operation']['mutation_attempted'] = True
                    elif contradiction == 'other_op':
                        current['operation']['operation_id'] = 'a' * 32
                    elif contradiction == 'sequence':
                        current['operation']['sequence'] += 1
                    elif contradiction == 'role':
                        current['view']['role_id'] = '12346'
                    elif contradiction == 'cycle':
                        current['view']['cycle'] += 86400
                    elif contradiction == 'offer':
                        current['view']['next_action'] = 'close'
                    elif contradiction == 'same_revision':
                        current['revision'] -= 1
                        current['view']['money'] += 1
                    elif contradiction in ('gui_unknown', 'older_scope_gui_unknown', 'older_gui_unknown'):
                        gui.update(phase='unknown', invocation_attempted=True)
                        if contradiction == 'older_scope_gui_unknown':
                            current['revision'] -= 2
                        if contradiction == 'older_gui_unknown':
                            gui['command_seq'] -= 1
                    elif contradiction.startswith('older_foreign_'):
                        current['revision'] -= 2
                        current['operation']['operation_id'] = 'a' * 32
                        if contradiction == 'older_foreign_unknown':
                            current['operation']['status'] = 'UNKNOWN'
                        elif contradiction == 'older_foreign_held':
                            current['operation'].update(status='OPENING', ui_ownership_held=True)
                        else:
                            current['operation']['status'] = 'QUEUED'
                    elif contradiction == 'newer_gui':
                        gui['command_seq'] += 1
                    elif contradiction == 'missing':
                        wire['state'] = {}

                self.objects.after_ack = reject
                result = self.run_task()
                self.assertEqual((result.status, result.reason, result.cleanup), ('UNKNOWN', 'refine_rejection_state_conflict', 'UNVERIFIED'))
                self.assertIsNone(result.ui_ownership_held)
                self.assertEqual(self.actions(), ['open'])

    def test_ambiguous_rejections_do_not_release_cleanup_obligation(self):
        for status, change in (('UNKNOWN', {}), ('REJECTED', dict(operation_id='9' * 32)),
                               ('REJECTED', dict(ui_ownership_held=True)), ('REJECTED', dict(mutation_attempted=True)),
                               ('REJECTED', dict(status='QUEUED'))):
            with self.subTest(status=status, change=change):
                self.objects = Objects()

                def reject(action, wire):
                    ack = wire['ack']
                    ack['status'] = status
                    op = ack['result']['daily_refine']['operation']
                    op.update(status='REJECTED', ui_ownership_held=False)
                    op.update(change)

                self.objects.after_ack = reject
                result = self.run_task()
                self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.actions(), ['open'])

        self.objects = Objects()
        def reject_after_open(action, wire):
            if action == 'refine':
                ack = wire['ack']
                ack['status'] = 'REJECTED'
                ack['result']['daily_refine']['operation'].update(status='REJECTED',
                    mutation_attempted=False, ui_ownership_held=False)
        self.objects.after_ack = reject_after_open
        result = self.run_task()
        self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
        self.assertEqual(self.actions(), ['open', 'refine'])

    def test_stale_observation_revision_and_foreign_status_abort(self):
        for which in ('regression', 'different_same_revision', 'foreign_operation'):
            with self.subTest(which=which):
                self.objects = Objects()
                def after_ack(action, wire):
                    if action == 'refine':
                        self.objects.coin_debit()
                        if which == 'foreign_operation':
                            self.objects.operation['operation_id'] = '2' * 32
                        else:
                            self.objects.snapshot['revision'] -= 1 if which == 'different_same_revision' else 2
                self.objects.after_ack = after_ack
                self.assertEqual(self.run_task().status, 'UNKNOWN')
                self.assertEqual(self.actions(), ['open', 'refine'])

    def test_unverified_close_preserves_observed_progress_and_blocks_repeat(self):
        self.objects.close_verified = False
        self.objects.after_ack = lambda action, wire: self.objects.settle() if action == 'refine' else None
        executor = self.executor()
        result = self.run_task(executor)
        self.assertEqual((result.status, result.cleanup, result.ui_ownership_held), ('BLOCKED', 'UNVERIFIED', True))
        self.assertEqual((result.coins_spent, result.progress_after), (COST, 2))
        self.assertEqual(self.run_task(executor).reason, 'refine_previous_attempt')
        self.assertEqual(self.run_task().reason, 'refine_prior_operation_unclosed')
        self.assertEqual(self.actions(), ['open', 'refine', 'close'])

    def test_known_rejection_after_open_keeps_ui_hold(self):
        def after_ack(action, wire):
            if action == 'refine':
                self.objects.advance('REJECTED', reason='refine_server_rejected', stock_callback_returned=True)
        self.objects.after_ack = after_ack
        result = self.run_task()
        self.assertEqual((result.status, result.cleanup, result.ui_ownership_held), ('REJECTED', 'UNVERIFIED', True))
        self.assertEqual(self.actions(), ['open', 'refine'])

    def test_timeout_and_stop_prevent_close_and_repeat(self):
        executor = self.executor()
        result = self.run_task(executor)
        self.assertEqual((result.status, result.reason, result.cleanup), ('UNKNOWN', 'refine_readback_timeout', 'UNVERIFIED'))
        self.assertEqual(self.run_task(executor).reason, 'refine_previous_attempt')
        self.assertEqual(self.actions(), ['open', 'refine'])
        for stop_after in ('open', 'refine'):
            with self.subTest(stop_after=stop_after):
                self.objects = Objects()
                self.stopped = False
                def after_ack(action, wire):
                    if action == stop_after:
                        self.stopped = True
                self.objects.after_ack = after_ack
                self.assertEqual(self.run_task().status, 'STOPPED')
                self.assertEqual(self.actions(), ['open'] if stop_after == 'open' else ['open', 'refine'])

    def test_pause_never_sends_until_a_fresh_unpaused_observation(self):
        def wait():
            self.wait()
            self.assertEqual(self.actions(), ['open'])
            if self.now == 2:
                self.paused = False

        def after_ack(action, wire):
            if action == 'open':
                self.paused = True
            if action == 'refine':
                self.objects.settle()

        self.objects.after_ack = after_ack
        self.assertEqual(self.run_task(self.executor(wait)).status, 'COMPLETED')
        self.assertEqual(self.actions(), ['open', 'refine', 'close'])

    def test_admission_closed_and_transport_error_do_not_repeat(self):
        def admission_closed(action):
            if action == 'refine':
                raise CampaignAdmissionClosed('closed before bridge')
        self.objects.before_submit = admission_closed
        result = self.run_task()
        self.assertEqual((result.status, result.cleanup, result.mutation_attempted), ('UNKNOWN', 'UNVERIFIED', False))
        self.assertEqual(self.actions(), ['open'])

        def no_ack(action, wire):
            if action == 'refine':
                raise TimeoutError('sent but no reply')
        self.objects = Objects()
        self.objects.after_ack = no_ack
        executor = self.executor()
        result = self.run_task(executor)
        self.assertEqual((result.status, result.cleanup, result.mutation_attempted), ('UNKNOWN', 'UNVERIFIED', None))
        self.assertEqual(self.run_task(executor).reason, 'refine_previous_attempt')
        self.assertEqual(self.actions(), ['open', 'refine'])


if __name__ == '__main__':
    unittest.main()
