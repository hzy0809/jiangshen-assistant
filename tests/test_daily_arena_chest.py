from copy import deepcopy
import unittest

from jiangshen_assistant.daily_arena_chest import DailyArenaChestExecutor
from jiangshen_assistant.objects import CampaignAdmissionClosed


ROLE = '12345'
CYCLE = 1793826000
DAY = CYCLE - 18000
BONUS = DAY + 60
TOKEN = object()
GOLD = 426


class ArenaBridge:
    def __init__(self, route=('open', 'enter', 'open_chest', 'claim', 'reward_confirm', 'close', 'close')):
        self.route = route
        self.snapshot = dict(schema=1, revision=1, activity_id=3, status='READY', reason='', next_action=route[0],
                             character_id=ROLE, operation_id='', sequence=0, current=0, required=1,
                             refresh_cycle=CYCLE, free_since=DAY - 86400, bonus_time=BONUS, invoked=False,
                             unresolved=False, navigation_pending=False, callback_returned=False,
                             settled=False, activity_push_returned=False, ui_disposition='', gold_before=None,
                             gold_after=None, counter_before=None, counter_after=None, receipt_opcode=0,
                             receipt_result=None, receipt_time=0, receipt_gold_consume=None)
        self.enabled = True
        self.actions = []
        self.observed = 0
        self.last_submit_observation = 0
        self.on_submit = lambda action: None
        self.after_ack = lambda action, wire: None
        self.after_observe = lambda: None
        self.auto_settle = True
        self.clear = True

    def advance(self, **fields):
        self.snapshot['revision'] += 1
        self.snapshot.update(fields)

    def receipt(self):
        self.advance(callback_returned=True, receipt_opcode=1122, receipt_result=0,
                     receipt_time=BONUS, receipt_gold_consume=0)

    def settle(self):
        self.receipt()
        self.advance(status='CLAIMED', next_action='reward_confirm', ui_disposition='confirm_ready',
                     unresolved=False, settled=True, activity_push_returned=True, current=1, counter_after=1,
                     gold_after=GOLD)

    def observe_daily_arena(self, token):
        assert token is TOKEN
        self.observed += 1
        self.after_observe()
        return dict(enabled=self.enabled, arena=deepcopy(self.snapshot) if self.enabled else None)

    def submit_daily_arena(self, token, action, *, expected_revision, character_id, operation_id):
        assert token is TOKEN and self.observed > self.last_submit_observation
        assert action == self.snapshot['next_action'] == self.route[len(self.actions)]
        assert expected_revision == self.snapshot['revision'] and character_id == ROLE
        if self.actions:
            assert operation_id == self.snapshot['operation_id']
        self.on_submit(action)
        self.last_submit_observation = self.observed
        self.actions.append(action)
        seq = 7 + self.observed
        self.advance(operation_id=operation_id, sequence=seq, next_action='')
        if action in ('open', 'enter', 'open_chest'):
            self.advance(status='WAITING', navigation_pending=True)
        elif action == 'claim':
            self.advance(status='WAITING', invoked=True, unresolved=True, counter_before=0, gold_before=GOLD)
        else:
            self.advance(ui_disposition='unconfirmed', navigation_pending=True)
        wire = dict(requested_seq=seq, ack=dict(command_name='daily-arena-command', command_seq=seq,
                    status='COMPLETED', result=dict(daily_arena_enabled=True, daily_arena=deepcopy(self.snapshot),
                    campaign_gui=dict(command_seq=seq, phase='completed', invocation_attempted=True))))
        next_action = self.route[len(self.actions)] if len(self.actions) < len(self.route) else ''
        if action in ('open', 'enter', 'open_chest'):
            self.advance(status='READY', next_action=next_action)
        elif action == 'claim' and self.auto_settle:
            self.settle()
        elif next_action == 'close':
            self.advance(next_action='close', ui_disposition='close_ready')
        elif action == 'close' and self.clear:
            self.advance(status='COMPLETED', navigation_pending=False, ui_disposition='clear')
        self.after_ack(action, wire)
        return wire


class DailyArenaChestTests(unittest.TestCase):
    def setUp(self):
        self.bridge = ArenaBridge()
        self.stopped = self.paused = False
        self.now = 0

    def wait(self):
        self.now += 1

    def executor(self, wait=None):
        return DailyArenaChestExecutor(self.bridge, TOKEN, stopped=lambda: self.stopped,
                                      paused=lambda: self.paused, wait=wait or self.wait,
                                      timeout=5, clock=lambda: self.now)

    def execute(self, executor=None):
        return (executor or self.executor()).execute(3, character_id=ROLE, expected_cycle=CYCLE)

    def test_stock_paths_require_fresh_read_after_every_command(self):
        for route in (('open', 'enter', 'open_chest', 'claim', 'reward_confirm', 'close', 'close'),
                      ('open', 'enter', 'claim', 'reward_confirm', 'close'),
                      ('claim', 'reward_confirm', 'close')):
            with self.subTest(route=route):
                self.bridge = ArenaBridge(route)
                executor = self.executor()
                result = self.execute(executor)
                self.assertEqual((result.status, result.cleanup), ('COMPLETED', 'VERIFIED_CLOSED'))
                self.assertEqual((result.progress_before, result.progress_after, result.required), (0, 1, 1))
                self.assertTrue(result.stock_callback_returned and result.activity_push_returned
                                and result.account_progress_observed)
                self.assertEqual((result.operation_id, result.bonus_time), (self.bridge.snapshot['operation_id'], BONUS))
                self.assertEqual(self.bridge.actions, list(route))
                self.assertGreater(self.bridge.observed, self.bridge.last_submit_observation)
                self.assertEqual(self.execute(executor).reason, 'arena_previous_attempt')
                self.assertEqual(self.bridge.actions.count('claim'), 1)

    def test_already_complete_and_disabled_are_read_only(self):
        self.bridge.advance(status='UNAVAILABLE', reason='arena_activity_complete', current=1, next_action='')
        self.assertEqual(self.execute().status, 'ALREADY_COMPLETE')
        self.bridge.enabled = False
        self.assertEqual(self.execute().status, 'UNAVAILABLE')
        self.assertEqual(self.bridge.actions, [])

    def test_foreign_prior_operation_and_unverified_free_window_are_read_only(self):
        for fields in (dict(character_id='67890'), dict(refresh_cycle=CYCLE + 86400),
                       dict(free_since=BONUS + 1), dict(bonus_time=DAY - 86401),
                       dict(operation_id='a'*32, sequence=9), dict(current=True)):
            with self.subTest(fields=fields):
                self.bridge = ArenaBridge()
                self.bridge.advance(**fields)
                self.assertIn(self.execute().status, ('BLOCKED', 'UNKNOWN'))
                self.assertEqual(self.bridge.actions, [])

    def test_navigation_ack_must_bind_actual_gui_and_submitted_offer(self):
        def change_ack(action, wire):
            section, field, value = modification
            target = wire if section == 'wire' else wire['ack']
            if section in ('daily_arena', 'campaign_gui'):
                target = target['result'][section]
            target[field] = value

        for modification in (('ack', 'command_name', 'daily-timed-command'), ('ack', 'status', 'REJECTED'),
                             ('wire', 'requested_seq', True), ('campaign_gui', 'command_seq', 999),
                             ('campaign_gui', 'invocation_attempted', False),
                             ('campaign_gui', 'phase', 'owned'), ('daily_arena', 'revision', 1),
                             ('daily_arena', 'navigation_pending', False),
                             ('daily_arena', 'operation_id', 'b'*32), ('daily_arena', 'bonus_time', BONUS + 1)):
            with self.subTest(modification=modification):
                self.bridge = ArenaBridge()
                self.bridge.after_ack = change_ack
                self.assertEqual(self.execute().status, 'UNKNOWN')
                self.assertEqual(self.bridge.actions, ['open'])

    def test_navigation_stops_on_regression_or_rebound_identity(self):
        for fields in (dict(revision=1), dict(sequence=99), dict(operation_id='c'*32), dict(navigation_pending=False),
                       dict(character_id='67890'), dict(refresh_cycle=CYCLE + 86400),
                       dict(bonus_time=BONUS + 1), dict(free_since=DAY), dict(current=1)):
            with self.subTest(fields=fields):
                self.bridge = ArenaBridge()
                self.bridge.after_ack = lambda action, wire: self.bridge.advance(**fields)
                self.assertEqual(self.execute().status, 'UNKNOWN')
                self.assertEqual(self.bridge.actions, ['open'])

    def test_terminal_claim_ack_cannot_regress_to_reward_offer(self):
        self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))

        def terminal_ack(action, wire):
            ack = deepcopy(self.bridge.snapshot)
            ack.update(status='COMPLETED', next_action='', ui_disposition='clear', navigation_pending=False)
            wire['ack']['result']['daily_arena'] = ack
            self.bridge.advance()

        self.bridge.after_ack = terminal_ack
        result = self.execute()
        self.assertEqual((result.status, result.reason, result.cleanup),
                         ('UNKNOWN', 'arena_observation_regressed', 'UNVERIFIED'))
        self.assertEqual(self.bridge.actions, ['claim'])

    def test_command_state_anchors_separate_readback_to_latest_scope(self):
        for mode in ('newer_compatible', 'stale_readback', 'terminal_regression', 'lost_hold', 'foreign_operation'):
            with self.subTest(mode=mode):
                self.bridge = ArenaBridge()
                if mode == 'terminal_regression':
                    self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))

                def command_state(action, wire):
                    latest = deepcopy(self.bridge.snapshot)
                    if mode == 'stale_readback':
                        latest['revision'] += 1
                    elif mode == 'terminal_regression':
                        latest.update(status='COMPLETED', next_action='', ui_disposition='clear', navigation_pending=False)
                        self.bridge.advance()
                    elif mode == 'lost_hold':
                        latest['navigation_pending'] = False
                    elif mode == 'foreign_operation':
                        latest['operation_id'] = 'a'*32
                    wire['state'] = dict(daily_arena_enabled=True, daily_arena=latest)

                self.bridge.after_ack = command_state
                result = self.execute()
                if mode == 'newer_compatible':
                    self.assertEqual(result.status, 'COMPLETED')
                else:
                    self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                    self.assertEqual(self.bridge.actions, ['claim' if mode == 'terminal_regression' else 'open'])

    def test_waits_for_355_and_cache_advance_after_1122(self):
        self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))
        self.bridge.auto_settle = False

        def wait():
            self.wait()
            self.assertEqual(self.bridge.actions, ['claim'])
            if self.now == 1:
                self.bridge.receipt()
            elif self.now == 2:
                self.bridge.advance(current=1, counter_after=1, gold_after=GOLD)
            elif self.now == 3:
                self.bridge.settle()

        self.assertEqual(self.execute(self.executor(wait)).status, 'COMPLETED')
        self.assertEqual(self.bridge.actions, ['claim', 'reward_confirm', 'close'])
        self.assertEqual(self.now, 3)

    def test_bad_settlement_or_premium_mutation_never_authorizes_confirmation(self):
        for fields in (dict(receipt_opcode=355), dict(receipt_result=1), dict(receipt_time=BONUS + 1),
                       dict(receipt_gold_consume=1), dict(gold_after=GOLD - 1),
                       dict(gold_before=GOLD+1, gold_after=GOLD+1), dict(counter_after=0),
                       dict(current=0), dict(activity_push_returned=False), dict(callback_returned=False)):
            with self.subTest(fields=fields):
                self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))
                self.bridge.after_ack = lambda action, wire: self.bridge.advance(**fields)
                result = self.execute()
                self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.bridge.actions, ['claim'])

    def test_claimed_and_unverified_cleanup_remain_distinct_from_success(self):
        for mode in ('waiting', 'false_clear', 'third_close', 'duplicate_confirmation'):
            with self.subTest(mode=mode):
                self.bridge = ArenaBridge()
                self.bridge.clear = False

                def change_close(action, wire):
                    if mode == 'duplicate_confirmation' and action == 'reward_confirm':
                        self.bridge.advance(next_action='reward_confirm', ui_disposition='confirm_ready')
                    if action == 'close' and self.bridge.actions.count('close') == 2:
                        if mode == 'false_clear':
                            self.bridge.advance(status='COMPLETED', navigation_pending=True, ui_disposition='clear')
                        elif mode == 'third_close':
                            self.bridge.advance(next_action='close', ui_disposition='close_ready')

                self.bridge.after_ack = change_close
                result = self.execute()
                self.assertEqual((result.status, result.cleanup), ('CLAIMED', 'UNVERIFIED'))
                self.assertTrue(result.account_progress_observed)
                self.assertEqual(self.bridge.actions.count('claim'), 1)
                self.assertEqual(self.bridge.actions.count('reward_confirm'), 1)
                self.assertLessEqual(self.bridge.actions.count('close'), 2)

    def test_missing_post_ack_observation_does_not_promote_ack_to_completion(self):
        self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))

        def claim_ack(action, wire):
            self.assertEqual(action, 'claim')
            self.bridge.advance(status='COMPLETED', next_action='', ui_disposition='clear')
            wire['ack']['result']['daily_arena'] = deepcopy(self.bridge.snapshot)
            self.bridge.enabled = False

        self.bridge.after_ack = claim_ack
        result = self.execute()
        self.assertEqual((result.status, result.cleanup), ('UNKNOWN', 'UNVERIFIED'))
        self.assertEqual(self.bridge.actions, ['claim'])

    def test_timeout_or_lost_reply_never_resends_claim(self):
        for lost_reply in (False, True):
            with self.subTest(lost_reply=lost_reply):
                self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))
                self.bridge.auto_settle = False
                if lost_reply:
                    def drop_reply(action, wire):
                        raise TimeoutError('reply lost')
                    self.bridge.after_ack = drop_reply
                executor = self.executor()
                self.assertEqual(self.execute(executor).status, 'UNKNOWN')
                self.assertEqual(self.execute(executor).status, 'UNKNOWN')
                self.assertEqual(self.bridge.actions, ['claim'])

    def test_stop_or_deadline_during_terminal_observation_leaves_cleanup_unverified(self):
        for stopped in (False, True):
            with self.subTest(stopped=stopped):
                self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))
                self.stopped = False
                self.now = 0

                def finish_late():
                    if self.bridge.snapshot['status'] == 'COMPLETED':
                        self.stopped = stopped
                        self.now = 5

                self.bridge.after_observe = finish_late
                result = self.execute()
                self.assertEqual((result.status, result.reason, result.cleanup),
                                 ('STOPPED', 'user_stop', 'UNVERIFIED') if stopped else
                                 ('CLAIMED', 'arena_readback_timeout', 'UNVERIFIED'))
                self.assertTrue(result.account_progress_observed)
                self.assertEqual(self.bridge.actions, ['claim', 'reward_confirm', 'close'])

    def test_late_settlement_observation_retains_progress_without_confirming_reward(self):
        self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))

        def settle_late():
            if self.bridge.snapshot['settled']:
                self.now = 6

        self.bridge.after_observe = settle_late
        result = self.execute()
        self.assertEqual((result.status, result.cleanup), ('CLAIMED', 'UNVERIFIED'))
        self.assertTrue(result.account_progress_observed and result.activity_push_returned)
        self.assertEqual(self.bridge.actions, ['claim'])

    def test_stop_at_admission_or_after_claim_never_continues(self):
        for during_admission in (True, False):
            with self.subTest(during_admission=during_admission):
                self.stopped = False
                self.bridge = ArenaBridge(('claim', 'reward_confirm', 'close'))

                def stop(action, wire=None):
                    self.stopped = True
                    if during_admission:
                        raise CampaignAdmissionClosed('stop')

                if during_admission:
                    self.bridge.on_submit = stop
                else:
                    self.bridge.after_ack = stop
                self.assertEqual(self.execute().status, 'STOPPED')
                self.assertEqual(self.bridge.actions, [] if during_admission else ['claim'])


if __name__ == '__main__':
    unittest.main()
