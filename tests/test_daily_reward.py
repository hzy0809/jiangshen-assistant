from copy import deepcopy
import unittest

from jiangshen_assistant.daily_reward import DailyRewardExecutor
from jiangshen_assistant.objects import CampaignAdmissionClosed


ROLE = '12345'
EPOCH = 104400
NEXT_EPOCH = EPOCH + 86400
ACTIONS = ('open', 'claim', 'reward_confirm', 'close')
COMMAND = 'daily-reward-command'


def snapshot(category_id, operation_id, revision, sequence, status, action, *,
             claim_sequence=0, settled=False, reward_closed=False, reason=''):
    return dict(schema=1, revision=revision, status=status, reason=reason, next_action=action,
        character_id=ROLE, operation_id=operation_id, sequence=sequence, category_id=category_id,
        claim_sequence=claim_sequence, refresh_epoch=EPOCH, invoked=bool(claim_sequence),
        unresolved=bool(claim_sequence) and status != 'COMPLETED', callback_returned=settled,
        settled=settled, reward_closed=reward_closed, receipt_category=category_id if settled else 0,
        receipt_refresh=NEXT_EPOCH if settled else 0,
        categories=[dict(id=i, ready=not settled or i != category_id, claimed=settled and i == category_id,
                         reason='daily_reward_already_claimed' if settled and i == category_id else '')
                    for i in range(2, 7)])


class RewardObjects:
    def __init__(self):
        self.enabled = True
        self.state = snapshot(0, '', 1, 0, 'READY', 'open')
        self.calls = []
        self.observations = 0
        self.seconds = 0
        self.advance = True
        self.stopped = self.paused = False
        self.on_observe = self.on_submit = lambda *args: None

    def wait(self):
        self.seconds += 1

    def observe_daily_reward(self, token):
        self.observations += 1
        if self.advance and self.state['status'] in ('WAITING', 'CLOSING'):
            last = self.calls[-1][0]
            next_action = {'open': 'claim', 'claim': 'reward_confirm', 'reward_confirm': 'close', 'close': ''}
            state = self.state
            self.state = snapshot(state['category_id'], state['operation_id'], state['revision'] + 1,
                state['sequence'], 'COMPLETED' if last == 'close' else 'READY', next_action[last],
                claim_sequence=state['claim_sequence'], settled=bool(state['claim_sequence']),
                reward_closed=last in ('reward_confirm', 'close'))
        self.on_observe(self)
        return dict(enabled=self.enabled, reward=deepcopy(self.state))

    def submit_daily_reward(self, token, action, *, expected_revision, character_id, category_id, operation_id):
        self.calls.append((action, expected_revision, character_id, category_id, operation_id))
        sequence = self.state['sequence'] + 2
        old = self.state
        self.state = snapshot(category_id, operation_id, old['revision'] + 1, sequence,
            'CLOSING' if action == 'close' else 'WAITING', '',
            claim_sequence=sequence if action == 'claim' else 0 if action == 'open' else old['claim_sequence'],
            settled=action != 'open' and old['settled'], reward_closed=action != 'open' and old['reward_closed'])
        wire = dict(command=COMMAND, requested_seq=sequence,
            ack=dict(command_seq=sequence, command_name=COMMAND, status='COMPLETED',
                     result=dict(daily_reward_enabled=True, daily_reward=deepcopy(self.state))),
            state=dict(last_command_seq=sequence,
                       campaign_gui=dict(command_seq=sequence, phase='completed', invocation_attempted=True)))
        self.on_submit(self, action, old, wire)
        return wire

    @property
    def actions(self):
        return tuple(call[0] for call in self.calls)


class DailyRewardTests(unittest.TestCase):
    def setUp(self):
        self.objects = RewardObjects()
        self.reports = []
        self.executor = DailyRewardExecutor(self.objects, object(), stopped=lambda: self.objects.stopped,
            paused=lambda: self.objects.paused, wait=self.objects.wait, publish=self.reports.append,
            timeout=5, clock=lambda: self.objects.seconds)

    def execute(self, category_id=2):
        return self.executor.execute(category_id, character_id=ROLE)

    def test_disabled_does_not_submit_a_reward_action(self):
        self.objects.enabled = False
        self.assertEqual(self.execute().status, 'DISABLED')
        self.assertEqual(self.objects.actions, ())

    def test_money_and_out_of_range_categories_are_rejected_before_observing(self):
        for category_id in (1, 7, 0, '2', True):
            with self.subTest(category_id=category_id), self.assertRaises(ValueError):
                self.execute(category_id)
        self.assertEqual(self.objects.observations, 0)
        self.assertEqual(self.objects.actions, ())

    def test_requires_an_open_offer_and_ready_category(self):
        for change in ({'next_action': 'claim'}, {'status': 'WAITING'}, {'refresh_epoch': 0},
                       {'character_id': '54321'}, {'revision': 0}):
            with self.subTest(change=change):
                self.objects.state = snapshot(0, '', 1, 0, 'READY', 'open')
                self.objects.state.update(change)
                self.assertEqual(self.execute().status, 'BLOCKED')
        self.objects.state = snapshot(0, '', 1, 0, 'READY', 'open')
        self.objects.state['categories'][0].update(ready=False, reason='daily_reward_tasks_incomplete')
        self.assertEqual(self.execute().reason, 'daily_reward_tasks_incomplete')
        self.assertEqual(self.objects.actions, ())

    def test_terminal_previous_operation_can_offer_open_for_another_category(self):
        self.objects.state = snapshot(2, '0' * 32, 11, 9, 'COMPLETED', 'open',
                                      claim_sequence=7, settled=True, reward_closed=True)
        self.assertEqual(self.execute().status, 'ALREADY_CLAIMED')
        result = self.execute(6)
        self.assertEqual(result.status, 'COMPLETED')
        self.assertEqual(result.receipt_category, 6)
        self.assertNotEqual(result.operation_id, '0' * 32)
        self.assertEqual(self.objects.actions, ACTIONS)

    def test_unresolved_previous_claim_stops_even_if_open_is_offered(self):
        self.objects.state.update(invoked=True, claim_sequence=3, unresolved=True)
        self.assertEqual(self.execute().status, 'UNKNOWN')
        self.assertEqual(self.objects.actions, ())

    def test_claim_requires_observed_callback_confirm_and_close(self):
        result = self.execute(5)
        self.assertEqual(result.status, 'COMPLETED')
        self.assertEqual(self.objects.actions, ACTIONS)
        self.assertEqual([call[1] for call in self.objects.calls], [1, 3, 5, 7])
        self.assertEqual({call[2:] for call in self.objects.calls}, {(ROLE, 5, result.operation_id)})
        self.assertEqual((result.claim_sequence, result.sequence), (4, 8))
        self.assertEqual((result.refresh_epoch, result.receipt_category, result.receipt_refresh), (EPOCH, 5, NEXT_EPOCH))
        self.assertTrue(result.invoked and result.callback_returned and result.settled)
        self.assertTrue(result.reward_closed and result.panel_closed)
        self.assertFalse(result.unresolved)
        self.assertEqual([report.reason for report in self.reports[:-1]],
                         ['daily_reward_' + action for action in ACTIONS])

    def test_stale_ack_stops_even_if_ui_is_ready(self):
        self.objects.on_submit = lambda obj, action, old, wire: wire['ack'].update(command_seq=1)
        self.assertEqual(self.execute().status, 'UNKNOWN')
        self.assertEqual(self.objects.actions, ('open',))
        observations = self.objects.observations
        self.assertEqual(self.execute(3).reason, 'daily_reward_previous_operation_unresolved')
        self.assertEqual(self.objects.observations, observations)

    def test_operation_binding_and_action_changes_stop_before_claim(self):
        changes = ({'character_id': '54321'}, {'category_id': 3}, {'operation_id': '0' * 32},
                   {'sequence': 0}, {'sequence': 3}, {'claim_sequence': 3}, {'revision': 1},
                   {'refresh_epoch': NEXT_EPOCH}, {'next_action': 'reward_confirm'},
                   {'next_action': 'open'}, {'status': 'WAITING'}, {'status': 'COMPLETED'})
        for change in changes:
            with self.subTest(change=change):
                self.setUp()
                self.objects.on_observe = lambda obj: obj.state.update(change) if obj.calls else None
                self.assertEqual(self.execute().status, 'UNKNOWN')
                self.assertEqual(self.objects.actions, ('open',))

    def test_noninvoked_claim_rejection_can_only_close_from_a_fresh_offer(self):
        def reject(obj, action, old, wire):
            if action == 'claim':
                obj.state = {**old, 'revision': old['revision'] + 1, 'next_action': 'close',
                             'sequence': wire['requested_seq'], 'reason': 'daily_reward_open_plan_changed'}
                wire['ack'].update(status='REJECTED', reason='daily_reward_preinvoke_changed')
                wire['state']['campaign_gui'].update(phase='owned', invocation_attempted=False)
            if action == 'close':
                obj.state.update(status='REJECTED', reason=old['reason'])
                wire['ack']['result']['daily_reward'] = deepcopy(obj.state)
        self.objects.on_submit = reject
        result = self.execute()
        self.assertEqual((result.status, result.reason), ('BLOCKED', 'daily_reward_preinvoke_changed'))
        self.assertTrue(result.panel_closed)
        self.assertFalse(result.invoked)
        self.assertEqual(self.objects.actions, ('open', 'claim', 'close'))

    def test_rejected_claim_is_never_replayed_when_close_is_unavailable(self):
        def reject(obj, action, old, wire):
            if action == 'claim':
                obj.state = old
                wire['ack'].update(status='REJECTED', reason='daily_reward_preinvoke_changed')
                wire['state']['campaign_gui'].update(phase='not_invoked', invocation_attempted=False)
        self.objects.on_submit = reject
        result = self.execute()
        self.assertEqual(result.status, 'BLOCKED')
        self.assertFalse(result.invoked or result.panel_closed)
        self.assertEqual(self.objects.actions, ('open', 'claim'))

    def test_negative_duplicate_and_cross_category_receipts_stop_without_confirmation(self):
        failures = ({'status': 'UNKNOWN', 'reason': 'daily_reward_negative_receipt_unattributed', 'next_action': ''},
                    {'status': 'UNKNOWN', 'reason': 'daily_reward_duplicate_reply_queued', 'next_action': ''},
                    {'receipt_category': 6}, {'receipt_refresh': EPOCH}, {'callback_returned': False})
        for change in failures:
            with self.subTest(change=change):
                self.setUp()
                self.objects.on_observe = lambda obj: obj.state.update(change) if obj.actions == ACTIONS[:2] else None
                result = self.execute()
                self.assertEqual(result.status, 'UNKNOWN')
                if 'reason' in change:
                    self.assertEqual(result.reason, change['reason'])
                self.assertEqual(self.objects.actions, ('open', 'claim'))

    def test_late_callback_and_transient_monitor_contention_do_not_replay_claim(self):
        def hold(obj, action, old, wire):
            if action == 'claim':
                obj.advance = False
                obj.state['revision'] += 1
                obj.state['refresh_epoch'] = 0
                for category in obj.state['categories']:
                    category.update(ready=False, claimed=None, reason='daily_reward_client_busy')

        def release(obj):
            if obj.seconds >= 2:
                obj.advance = True
        self.objects.on_submit = hold
        self.objects.on_observe = release
        self.assertEqual(self.execute().status, 'COMPLETED')
        self.assertEqual(self.objects.seconds, 3)
        self.assertEqual(self.objects.actions, ACTIONS)

    def test_invocation_ack_and_late_callback_past_timeout_are_insufficient(self):
        def hold(obj, action, old, wire):
            if action == 'claim':
                obj.advance = False
        self.objects.on_submit = hold
        result = self.execute()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_reward_observation_timeout'))
        self.assertTrue(result.invoked and result.unresolved)
        self.assertFalse(result.callback_returned)
        self.objects.advance = True
        self.assertEqual(self.execute().status, 'UNKNOWN')
        self.assertEqual(self.objects.actions, ('open', 'claim'))

    def test_transport_exception_is_unknown_and_admission_closed_is_noninvoked(self):
        for error, expected in ((OSError('lost ACK'), 'UNKNOWN'), (CampaignAdmissionClosed(), 'BLOCKED')):
            with self.subTest(error=error):
                self.setUp()
                def fail(*args):
                    raise error
                self.objects.on_submit = fail
                self.assertEqual(self.execute().status, expected)
                self.assertEqual(self.objects.actions, ('open',))

    def test_stop_before_open_and_before_claim_prevents_new_actions(self):
        self.objects.stopped = True
        self.assertEqual(self.execute().status, 'STOPPED')
        self.assertEqual(self.objects.actions, ())
        self.assertEqual(self.objects.observations, 0)
        self.objects.stopped = False
        self.objects.on_observe = lambda obj: setattr(obj, 'stopped', bool(obj.calls))
        self.assertEqual(self.execute().status, 'STOPPED')
        self.assertEqual(self.objects.actions, ('open',))

    def test_stop_after_claim_stops_without_confirming(self):
        def stop(obj, action, old, wire):
            if action == 'claim':
                obj.stopped = True
        self.objects.on_submit = stop
        result = self.execute()
        self.assertEqual(result.status, 'UNKNOWN')
        self.assertTrue(result.unresolved)
        self.assertEqual(self.objects.actions, ('open', 'claim'))

    def test_stop_when_publish_offers_open_is_checked_before_submission(self):
        def stop_when_offered(result):
            if result.status == 'RUNNING':
                self.objects.stopped = True
        self.executor.publish = stop_when_offered
        self.assertEqual(self.execute().status, 'STOPPED')
        self.assertEqual(self.objects.actions, ())

    def test_pause_while_callback_arrives_waits_for_fresh_observation(self):
        def pause(obj, action, old, wire):
            if action == 'claim':
                obj.paused = True
        self.objects.on_submit = pause

        def resume(obj):
            if obj.seconds >= 2:
                obj.paused = False
        self.objects.on_observe = resume
        self.assertEqual(self.execute().status, 'COMPLETED')
        self.assertEqual(self.objects.seconds, 2)
        self.assertEqual(self.objects.actions, ACTIONS)

    def test_close_ack_without_fresh_completed_evidence_is_unknown(self):
        def hold(obj, action, old, wire):
            if action == 'close':
                obj.advance = False
        self.objects.on_submit = hold
        result = self.execute()
        self.assertEqual(result.status, 'UNKNOWN')
        self.assertTrue(result.reward_closed)
        self.assertFalse(result.panel_closed)
        self.assertEqual(self.objects.actions, ACTIONS)
