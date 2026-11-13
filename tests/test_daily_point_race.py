from copy import deepcopy
import unittest

from jiangshen_assistant.daily_point_race import DailyPointRaceExecutor


class PointRaceBridge:
    """Detached observations and command receipts, with a controllable clock."""

    def __init__(self, *, closes=True, timeout_changes=None):
        self.now = 0.0
        self.phase = 'home'
        self.operation_id = ''
        self.sequence = self.entry_sequence = 0
        self.entry_at = None
        self.closes = closes
        self.timeout_changes = timeout_changes or {}
        self.actions = []
        self.observations = []

    def wait(self):
        self.now += 1.0

    def observe_daily_script(self, token, task_id):
        self.observations.append((self.now, self.phase))
        timed_out = self.entry_at is not None and self.now - self.entry_at >= 180
        closing = self.phase == 'cleanup'
        closed = closing and self.closes
        action = {'home': 'open', 'selector': 'enter'}.get(self.phase, '')
        if self.phase == 'entry' and timed_out:
            action = 'return'
        operation = dict(
            id=self.operation_id, sequence=self.sequence, status='READY' if action else 'WAITING',
            reason='pointrace_waiting_entry_callback' if self.phase == 'entry' else '',
            ui_touched=bool(self.operation_id), closed=closed, baseline_ready=False,
            baseline_count=None, baseline_source='', baseline_sequence=0,
            entry_request_sequence=self.entry_sequence, entry_callback_returned=False,
            mutation_attempted=False, free_refresh_attempted=False,
            free_refresh_callback_returned=False, free_refresh_verified=False,
            challenges_submitted=0, challenges_completed=0,
            stock_callback_returned=False, account_progress_correlated=False,
            entry_timeout_cleanup=timed_out, cleanup_attempted=closing,
        )
        if timed_out:
            operation.update(status='UNKNOWN', reason='pointrace_entry_callback_timeout')
            operation.update(self.timeout_changes.get('operation', {}))
        view = dict(
            observation_available=True, role_id='78', mode='pointrace', task_id=109,
            continuous_scene=True, home_ready=self.phase == 'home' or closed,
            in_scene=self.phase == 'entry' or closing and not closed,
            refresh_cycle=1000, current=0, required=10,
            gold=100, bound_gold=200, gold_consumed=0, bought=0,
            refresh_count=0, free_refresh_available=True, next_action=action,
            opponent_id='0', opponent_score=0, player_score=300,
        )
        if timed_out:
            view.update(self.timeout_changes.get('view', {}))
        return deepcopy(dict(revision=self.sequence + 1, view=view, operation=operation))

    def submit_daily_script(self, token, action, **kwargs):
        self.actions.append((self.now, action))
        self.operation_id = kwargs['operation_id']
        self.sequence += 1
        if action == 'open':
            self.phase = 'selector'
        elif action == 'enter':
            self.phase = 'entry'
            self.entry_sequence = self.sequence
            self.entry_at = self.now
        elif action == 'return':
            self.phase = 'cleanup'
        else:
            raise AssertionError('unexpected business action: ' + action)
        return dict(ack=dict(status='COMPLETED'))


class PointRaceEntryTimeoutTests(unittest.TestCase):
    def execute(self, bridge, **kwargs):
        return DailyPointRaceExecutor(
            bridge, object(), wait=bridge.wait, clock=lambda: bridge.now,
            observation_timeout=3, **kwargs).execute(character_id='78', expected_cycle=1000)

    def test_owned_missing_callback_returns_home_but_stays_unknown(self):
        bridge = PointRaceBridge()
        result = self.execute(bridge)
        self.assertEqual((result.status, result.reason, result.cleanup),
                         ('UNKNOWN', 'pointrace_entry_callback_timeout', 'VERIFIED_CLOSED'))
        self.assertEqual([a for _, a in bridge.actions], ['open', 'enter', 'return'])
        self.assertGreaterEqual(bridge.actions[-1][0] - bridge.entry_at, 180)
        self.assertFalse(result.stock_callback_returned)
        self.assertFalse(result.account_progress_observed)
        self.assertEqual((result.challenges_completed, result.free_refreshes_completed), (0, 0))

    def test_incomplete_cleanup_waits_boundedly_without_repeating(self):
        bridge = PointRaceBridge(closes=False)
        result = self.execute(bridge)
        self.assertEqual((result.status, result.reason, result.cleanup),
                         ('UNKNOWN', 'pointrace_entry_callback_timeout', 'UNVERIFIED'))
        self.assertEqual([a for _, a in bridge.actions], ['open', 'enter', 'return'])
        self.assertLess(bridge.now, 200)

    def test_timeout_cleanup_requires_exact_owned_nonmutation_evidence(self):
        changes = [
            dict(operation=dict(entry_timeout_cleanup=False)),
            dict(operation=dict(entry_callback_returned=True)),
            dict(operation=dict(entry_request_sequence=0)),
            dict(operation=dict(entry_request_sequence=True)),
            dict(operation=dict(baseline_ready=True)),
            dict(operation=dict(mutation_attempted=True)),
            dict(operation=dict(free_refresh_attempted=True)),
            dict(operation=dict(challenges_submitted=1)),
            dict(operation=dict(challenges_completed=1)),
            dict(operation=dict(cleanup_attempted=True)),
            dict(view=dict(in_scene=False)),
            dict(view=dict(next_action='enter')),
            dict(view=dict(next_action='challenge')),
            dict(view=dict(next_action='refresh_free')),
            dict(view=dict(next_action='')),
        ]
        for change in changes:
            with self.subTest(change=change):
                bridge = PointRaceBridge(timeout_changes=change)
                result = self.execute(bridge)
                self.assertEqual((result.status, result.reason, result.cleanup),
                                 ('UNKNOWN', 'pointrace_entry_callback_timeout', 'UNVERIFIED'))
                self.assertEqual([a for _, a in bridge.actions], ['open', 'enter'])

    def test_stop_before_timeout_observation_never_returns(self):
        bridge = PointRaceBridge()
        result = self.execute(bridge, stopped=lambda: bridge.entry_at is not None and
                              bridge.now - bridge.entry_at >= 180)
        self.assertEqual(result.status, 'STOPPED')
        self.assertEqual([a for _, a in bridge.actions], ['open', 'enter'])

    def test_stop_after_timeout_retains_first_failure(self):
        bridge = PointRaceBridge(closes=False)
        result = self.execute(bridge, stopped=lambda: bridge.phase == 'cleanup')
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'pointrace_entry_callback_timeout'))
        self.assertEqual([a for _, a in bridge.actions], ['open', 'enter', 'return'])

    def test_pause_has_no_new_work_until_resumed(self):
        bridge = PointRaceBridge()
        def paused():
            return bridge.entry_at is not None and 180 <= bridge.now - bridge.entry_at < 185
        result = self.execute(bridge, paused=paused)
        self.assertEqual(result.cleanup, 'VERIFIED_CLOSED')
        self.assertGreaterEqual(bridge.actions[-1][0] - bridge.entry_at, 185)
        self.assertEqual([a for _, a in bridge.actions], ['open', 'enter', 'return'])

    def test_unknown_without_timeout_contract_never_cleans_up(self):
        bridge = PointRaceBridge(timeout_changes=dict(operation=dict(
            reason='pointrace_operation_timeout', entry_timeout_cleanup=False)))
        result = self.execute(bridge)
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'pointrace_operation_timeout'))
        self.assertEqual([a for _, a in bridge.actions], ['open', 'enter'])

    def test_changed_operation_never_receives_cleanup(self):
        bridge = PointRaceBridge(timeout_changes=dict(operation=dict(id='foreign')))
        result = self.execute(bridge)
        self.assertEqual(result.reason, 'pointrace_operation_changed')
        self.assertEqual([a for _, a in bridge.actions], ['open', 'enter'])

    def test_already_complete_home_does_no_ui_work(self):
        bridge = PointRaceBridge()
        observe = bridge.observe_daily_script
        def complete(*args):
            value = observe(*args)
            value['view'].update(current=10, next_action='')
            return value
        bridge.observe_daily_script = complete
        result = self.execute(bridge)
        self.assertEqual(result.status, 'ALREADY_COMPLETE')
        self.assertEqual(bridge.actions, [])


if __name__ == '__main__':
    unittest.main()
