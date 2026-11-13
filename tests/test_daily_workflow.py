from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from jiangshen_assistant.daily_workflow import DailyWorkflow
from jiangshen_assistant.development_task import WorkflowResult
from jiangshen_assistant.daily_guild import DailyGuildResult
from jiangshen_assistant.daily_timed import DailyTimedResult
from jiangshen_assistant.daily_stone import DailyStoneResult
from jiangshen_assistant.daily_mingshi import DailyMingshiResult
from jiangshen_assistant.daily_refine import DailyRefineResult
from jiangshen_assistant.daily_arena_chest import DailyArenaChestResult
from jiangshen_assistant.daily_soul import DailySoulResult
from tests.test_daily_refresh_transport import refresh


def activity(task_id=6, latest=0, *, cached=None, required=2, **values):
    if cached is None:
        cached = latest if latest is not None else 0
    return dict(dict(task_id=task_id, name=f'任务 {task_id}', current=cached,
        latest_current=latest, required=required, complete=cached >= required,
        visible=True, reward_ready=None, availability='available', reason='',
        ui_button_id=task_id), **values)


def observation(*entries, character_id='1001'):
    categories = [dict(category_id=i, name=f'分类 {i}', display_order=i, visible=True,
        current=0, required=3, reward_ready=False, reward_claimed=False,
        server_finished=False, reward_refresh=0, tasks=[]) for i in range(1, 7)]
    for category, task in entries:
        categories[category - 1]['tasks'].append(task)
    return dict(schema=1, available=True, reason='', source='client_projection',
        character_id=character_id, server_now=123456, server_day=86400, refresh_clear=True, categories=categories)


def tribute_observation(**values):
    return observation((2, activity(**values)))


def by_id(result):
    return {(item.category_id, item.task_id): item for item in result.items}


_CONFIRMED = WorkflowResult('DONE', 'tribute_request_budget_reached', 'SERVER_CONFIRMED')


class DailyWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.token = SimpleNamespace(context_key='test owner')
        self.trace = []
        self.targets = []
        self.policies = []
        self.events = []
        self.stopped = self.paused = False

    def run_workflow(self, observations, steps=(), *, on_publish=None, on_wait=None):
        observations, steps = iter(observations), iter(steps)

        def observe(token):
            self.assertIs(token, self.token)
            self.trace.append('observe')
            value = next(observations)
            if isinstance(value, Exception):
                raise value
            return value

        def execute(module, target, *, policy):
            self.assertEqual(module, 'tribute')
            self.trace.append('tribute')
            self.targets.append(target)
            self.policies.append(policy)
            step = next(steps)
            return step(policy) if callable(step) else step

        def publish(result):
            self.events.append(result)
            if on_publish:
                on_publish(result)

        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=execute)):
            return DailyWorkflow(SimpleNamespace(observe_daily=observe), self.token, Path('.'),
                stopped=lambda: self.stopped, paused=lambda: self.paused,
                wait=on_wait, publish=publish, confirmation_timeout=0, rewards_enabled=False).run()

    def test_each_peach_requires_a_new_latest_daily_counter_before_repeating(self):
        result = self.run_workflow([tribute_observation(latest=i) for i in range(3)], [
            WorkflowResult('DONE', 'tribute_request_budget_reached', 'SERVER_CONFIRMED', 'one.json'),
            WorkflowResult('DONE', 'tribute_request_budget_reached', 'SERVER_CONFIRMED', 'two.json'),
        ])
        self.assertEqual(self.trace, ['observe', 'tribute', 'observe', 'tribute', 'observe'])
        self.assertEqual([(t.max_requests, t.use_all, t.desired_level) for t in self.targets],
                         [(1, False, 0), (1, False, 0)])
        self.assertNotEqual(self.policies[0].operation_id, self.policies[1].operation_id)
        self.assertEqual(result.ledger_paths, ('one.json', 'two.json'))
        self.assertEqual(result.status, 'PARTIAL')
        item = by_id(result)[2, 6]
        self.assertEqual((item.status, item.current, item.required), ('COMPLETED', 2, 2))
        self.assertEqual(self.events[-1], result)

    def test_cached_progress_cannot_authorize_repeat(self):
        result = self.run_workflow([tribute_observation(), tribute_observation(cached=2)], [_CONFIRMED])
        self.assertEqual(self.trace, ['observe', 'tribute', 'observe'])
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_tribute_progress_unconfirmed'))
        self.assertEqual(by_id(result)[2, 6].current, 0)

    def test_already_complete_latest_counter_with_stale_panel_is_unverified(self):
        result = self.run_workflow([tribute_observation(latest=2, cached=0)])
        self.assertEqual(self.trace, ['observe'])
        self.assertEqual((by_id(result)[2, 6].status, by_id(result)[2, 6].reason),
                         ('UNAVAILABLE', 'daily_progress_inconsistent'))

    def test_stone_stale_rows_cannot_be_reported_complete(self):
        stale = activity(task_id=16, latest=1, cached=1, required=1,
                         availability='unknown', reason='daily_progress_inconsistent', complete=None)
        result = self.run_workflow([observation((2, stale))])
        self.assertEqual((by_id(result)[2, 16].status, by_id(result)[2, 16].reason),
                         ('UNAVAILABLE', 'daily_progress_inconsistent'))

    def test_wrong_character_cannot_confirm_progress_or_authorize_repeat(self):
        result = self.run_workflow([tribute_observation(),
            observation((2, activity(latest=2)), character_id='1002')], [_CONFIRMED])
        self.assertEqual(self.trace, ['observe', 'tribute', 'observe'])
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_character_changed'))
        self.assertEqual(result.character_id, '1001')

    def test_new_refresh_cycle_cannot_confirm_an_older_request(self):
        before = tribute_observation(latest=0, required=1)
        before.update(server_day=86400, server_now=104399)
        after = tribute_observation(latest=1, required=1)
        after.update(server_day=86400, server_now=104400)
        result = self.run_workflow([before, after], [_CONFIRMED])
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_refresh_cycle_changed'))
        self.assertEqual(self.trace.count('tribute'), 1)

    def test_midnight_without_five_oclock_refresh_keeps_cycle(self):
        before = tribute_observation(latest=0, required=1)
        before.update(server_day=86400, server_now=172799)
        after = tribute_observation(latest=1, required=1)
        after.update(server_day=172800, server_now=172801)
        result = self.run_workflow([before, after], [_CONFIRMED])
        self.assertEqual(by_id(result)[2, 6].status, 'COMPLETED')
        self.assertEqual(self.trace.count('tribute'), 1)

    def test_delayed_daily_push_is_observed_without_repeating_tribute(self):
        observations = iter([tribute_observation(latest=0, required=1),
                             tribute_observation(latest=0, required=1),
                             tribute_observation(latest=1, required=1)])
        objects = SimpleNamespace(observe_daily=lambda token: next(observations))
        now = [0.0]
        execute = unittest.mock.Mock(return_value=_CONFIRMED)
        def wait():
            now[0] += .1
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(objects, self.token, Path('.'), wait=wait,
                clock=lambda: now[0], confirmation_timeout=1).run()
        self.assertEqual(by_id(result)[2, 6].status, 'COMPLETED')
        self.assertEqual(execute.call_count, 1)

    def test_tribute_waits_for_both_caches_before_repeating(self):
        observations = iter([tribute_observation(latest=0, required=1),
                             tribute_observation(latest=1, cached=0, required=1),
                             tribute_observation(latest=1, cached=1, required=1)])
        now = [0.0]
        execute = unittest.mock.Mock(return_value=_CONFIRMED)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(observations)),
                self.token, Path('.'), wait=lambda: now.__setitem__(0, now[0] + .1),
                clock=lambda: now[0], confirmation_timeout=1).run()
        self.assertEqual(by_id(result)[2, 6].status, 'COMPLETED')
        self.assertEqual(execute.call_count, 1)

    def test_tribute_cache_conflict_after_receipt_stops_without_repeat(self):
        result = self.run_workflow([tribute_observation(latest=0, required=1),
                                    tribute_observation(latest=1, cached=0, required=1)], [_CONFIRMED])
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_tribute_progress_unconfirmed'))
        self.assertEqual(self.trace.count('tribute'), 1)

    def test_tribute_temporarily_missing_latest_row_can_reconcile(self):
        observations = iter([tribute_observation(latest=0, required=1),
                             tribute_observation(latest=None, cached=1, required=1),
                             tribute_observation(latest=1, cached=1, required=1)])
        now = [0.0]
        execute = unittest.mock.Mock(return_value=_CONFIRMED)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(observations)),
                self.token, Path('.'), wait=lambda: now.__setitem__(0, now[0] + .1),
                clock=lambda: now[0], confirmation_timeout=1).run()
        self.assertEqual(by_id(result)[2, 6].status, 'COMPLETED')
        self.assertEqual(execute.call_count, 1)

    def test_tribute_temporarily_missing_both_rows_can_reconcile(self):
        observations = iter([tribute_observation(latest=0, required=1), observation(),
                             tribute_observation(latest=1, required=1)])
        now = [0.0]
        execute = unittest.mock.Mock(return_value=_CONFIRMED)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(observations)),
                self.token, Path('.'), wait=lambda: now.__setitem__(0, now[0] + .1),
                clock=lambda: now[0], confirmation_timeout=1).run()
        self.assertEqual(by_id(result)[2, 6].status, 'COMPLETED')
        self.assertEqual(execute.call_count, 1)

    def test_pause_does_not_block_readback_for_already_sent_tribute(self):
        now, paused, count = [0.0], [False], [0]
        def observe(token):
            count[0] += 1
            if count[0] == 2:
                paused[0] = True
                return tribute_observation(latest=0, required=1)
            return tribute_observation(latest=1 if count[0] == 3 else 0, required=1)
        def wait():
            now[0] += .1
            if now[0] >= 2:
                paused[0] = False
        execute = unittest.mock.Mock(return_value=_CONFIRMED)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=observe), self.token, Path('.'),
                paused=lambda: paused[0], wait=wait, clock=lambda: now[0], confirmation_timeout=1,
                rewards_enabled=False).run()
        self.assertEqual(by_id(result)[2, 6].status, 'COMPLETED')
        self.assertEqual((execute.call_count, count[0]), (1, 3))

    def test_tribute_read_returning_after_deadline_does_not_authorize_retry(self):
        now, count = [0.0], [0]
        def observe(token):
            count[0] += 1
            if count[0] == 3:
                now[0] = 2.0
                return tribute_observation(latest=1, required=2)
            return tribute_observation(latest=0, required=2)
        execute = unittest.mock.Mock(return_value=_CONFIRMED)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=observe), self.token, Path('.'),
                wait=lambda: now.__setitem__(0, now[0] + .1), clock=lambda: now[0],
                confirmation_timeout=1).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_tribute_progress_unconfirmed'))
        self.assertEqual(execute.call_count, 1)

    def test_initial_post_tribute_read_counts_against_confirmation_deadline(self):
        now, count = [0.0], [0]
        def observe(token):
            count[0] += 1
            if count[0] == 2:
                now[0] = 2.0
                return tribute_observation(latest=1, required=2)
            return tribute_observation(latest=0, required=2)
        execute = unittest.mock.Mock(return_value=_CONFIRMED)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=observe), self.token, Path('.'),
                wait=lambda: None, clock=lambda: now[0], confirmation_timeout=1).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_tribute_progress_unconfirmed'))
        self.assertEqual(execute.call_count, 1)

    def test_missing_requested_tasks_cannot_produce_done(self):
        observed = tribute_observation(latest=2, required=2)
        for category in observed['categories'][1:]:
            category['reward_claimed'] = True
        result = self.run_workflow([observed])
        missing = [item for item in result.items if item.reason == 'daily_task_not_observed']
        self.assertEqual(result.status, 'PARTIAL')
        self.assertEqual(len(missing), 19)
        self.assertTrue(all(item.status == 'UNAVAILABLE' for item in missing))

    def test_inactive_task_19_does_not_block_completed_daily(self):
        catalog = {2: (5, 6, 7, 8, 16, 17, 21), 3: (9, 11, 12, 22),
                   4: (10, 14, 15, 23), 5: (18, 20), 6: (3, 13, 24)}
        entries = [(category, activity(task_id=task, latest=1, required=1))
                   for category, tasks in catalog.items() for task in tasks]
        entries.append((5, activity(task_id=19, latest=0, required=1)))
        observed = observation(*entries)
        for category in observed['categories'][1:]:
            category['reward_claimed'] = True
        result = self.run_workflow([observed])
        self.assertEqual(result.status, 'DONE')
        self.assertNotIn((5, 19), by_id(result))

    def test_stock_closed_tasks_do_not_count_synthetic_counter_as_completion(self):
        observed = observation(
            (2, activity(task_id=6, latest=5, required=5, visible=False,
                         availability='unavailable', reason='spirit_max_level')),
            (2, activity(task_id=17, latest=1, required=1, visible=False,
                         availability='unavailable', reason='grave_progression_limit')))
        result = self.run_workflow([observed])
        self.assertEqual((by_id(result)[2, 6].status, by_id(result)[2, 6].reason),
                         ('UNAVAILABLE', 'spirit_max_level'))
        self.assertEqual((by_id(result)[2, 17].status, by_id(result)[2, 17].reason),
                         ('UNAVAILABLE', 'grave_progression_limit'))
        self.assertNotEqual(result.status, 'DONE')

    def test_tribute_progress_jump_cannot_authorize_another_action(self):
        result = self.run_workflow([tribute_observation(latest=0, required=3),
                                    tribute_observation(latest=2, required=3)], [_CONFIRMED])
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_tribute_progress_unconfirmed'))
        self.assertEqual(self.trace.count('tribute'), 1)

    def test_enabled_guild_donates_before_drawing_and_binds_choice(self):
        def sample(donate, lottery):
            return observation((5, activity(task_id=20, latest=donate, required=1)),
                               (5, activity(task_id=18, latest=lottery, required=1)))
        snapshots = iter([sample(0, 0), sample(1, 0), sample(1, 1)])
        objects = SimpleNamespace(observe_daily=lambda token: next(snapshots))
        execute = unittest.mock.Mock(side_effect=[
            DailyGuildResult('COMPLETED', 'ok', 0, 1, 301, 1, True),
            DailyGuildResult('COMPLETED', 'ok', 0, 1, 302, 8, True)])
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor',
                   return_value=SimpleNamespace(execute=execute)) as factory:
            result = DailyWorkflow(objects, self.token, Path('.'), guild_enabled=True, donation_type=3).run()
        self.assertEqual([call.args[0] for call in execute.call_args_list], [20, 18])
        self.assertEqual(factory.call_args.kwargs['donation_type'], 3)
        self.assertEqual(by_id(result)[5, 20].status, 'COMPLETED')
        self.assertEqual(by_id(result)[5, 18].status, 'COMPLETED')

    def test_unavailable_donation_does_not_hide_it_when_lottery_completes(self):
        initial = observation((5, activity(task_id=20, required=1)), (5, activity(task_id=18, required=1)))
        after = observation((5, activity(task_id=20, required=1)), (5, activity(task_id=18, latest=1, required=1)))
        snapshots = iter([initial, after])
        execute = unittest.mock.Mock(side_effect=[DailyGuildResult('BLOCKED', 'guild_coins_insufficient'),
            DailyGuildResult('COMPLETED', 'ok', 0, 1, 302, 1, True)])
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                   self.token, Path('.'), guild_enabled=True).run()
        self.assertEqual((by_id(result)[5, 20].status, by_id(result)[5, 20].reason),
                         ('UNAVAILABLE', 'guild_coins_insufficient'))
        self.assertEqual(by_id(result)[5, 18].status, 'COMPLETED')

    def test_unknown_donation_stops_before_lottery(self):
        initial = observation((5, activity(task_id=20, required=1)), (5, activity(task_id=18, required=1)))
        execute = unittest.mock.Mock(return_value=DailyGuildResult('UNKNOWN', 'lost_reply'))
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: initial),
                                   self.token, Path('.'), guild_enabled=True).run()
        self.assertEqual(result.status, 'UNKNOWN')
        self.assertEqual(execute.call_count, 1)

    def test_guild_counter_conflict_stops_before_lottery(self):
        initial = observation((5, activity(task_id=20, required=1)), (5, activity(task_id=18, required=1)))
        after = observation((5, activity(task_id=20, latest=1, cached=0, required=1)),
                            (5, activity(task_id=18, required=1)))
        snapshots = iter([initial, after])
        execute = unittest.mock.Mock(return_value=DailyGuildResult('COMPLETED', 'ok', 0, 1, 301, 1, True))
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)), self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_guild_progress_mismatch'))
        self.assertEqual(execute.call_count, 1)

    def test_ready_selected_category_reward_is_claimed_without_money_category(self):
        observed = observation()
        observed['categories'][0]['reward_ready'] = True
        observed['categories'][1]['reward_ready'] = True
        execute = unittest.mock.Mock(return_value=SimpleNamespace(status='COMPLETED', reason='verified',
                                                                    operation_id='id', panel_closed=True))
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyRewardExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed),
                                   self.token, Path('.')).run()
        self.assertEqual([call.args[0] for call in execute.call_args_list], [2])
        self.assertEqual(by_id(result)[2, None].status, 'COMPLETED')
        self.assertNotIn((1, None), by_id(result))

    def test_stone_requires_fresh_daily_progress_and_verified_close(self):
        before = observation((2, activity(task_id=16, required=1)))
        after = observation((2, activity(task_id=16, latest=1, cached=1, required=1)))
        snapshots = iter([before, after])
        step = DailyStoneResult('COMPLETED', 'verified', 0, 1, 1, True, True, 'VERIFIED_CLOSED')
        execute = unittest.mock.Mock(return_value=step)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyStoneExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                   self.token, Path('.'), rewards_enabled=False).run()
        self.assertEqual(by_id(result)[2, 16].status, 'COMPLETED')
        execute.assert_called_once_with(16, character_id='1001', expected_cycle=104400)

    def test_stone_unverified_close_stops_before_later_actions(self):
        observed = observation((2, activity(task_id=16, required=1)), (5, activity(task_id=20, required=1)))
        step = DailyStoneResult('COMPLETED', 'close_unknown', 0, 1, 1, True, True, 'UNVERIFIED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyStoneExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor') as guild:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed), self.token, Path('.')).run()
        self.assertEqual(result.status, 'UNKNOWN')
        self.assertEqual(by_id(result)[2, 16].current, 1)
        guild.assert_not_called()

    def test_stone_progress_mismatch_stops_workflow(self):
        observed = observation((2, activity(task_id=16, required=1)))
        step = DailyStoneResult('COMPLETED', 'verified', 0, 1, 1, True, True, 'VERIFIED_CLOSED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyStoneExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed), self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_stone_progress_mismatch'))

    def test_stone_inconsistent_daily_caches_never_count_as_complete(self):
        before = observation((2, activity(task_id=16, required=1)))
        after = observation((2, activity(task_id=16, latest=1, cached=0, required=1,
                                          reason='daily_progress_inconsistent')))
        snapshots = iter([before, after])
        step = DailyStoneResult('COMPLETED', 'verified', 0, 1, 1, True, True, 'VERIFIED_CLOSED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyStoneExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)), self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_stone_progress_mismatch'))

    def test_newer_cache_conflict_cannot_be_hidden_by_previous_completion(self):
        before = observation((2, activity(task_id=6, required=1)), (2, activity(task_id=16, required=1)))
        after_tribute = observation((2, activity(task_id=6, latest=1, required=1)),
                                    (2, activity(task_id=16, required=1)))
        after_stone = observation((2, activity(task_id=6, latest=1, cached=1, required=1,
                                                reason='daily_progress_inconsistent', complete=None)),
                                  (2, activity(task_id=16, latest=1, required=1)))
        after_stone['categories'][1]['reward_ready'] = True
        snapshots = iter([before, after_tribute, after_stone])
        stone = DailyStoneResult('COMPLETED', 'verified', 0, 1, 1, True, True, 'VERIFIED_CLOSED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=lambda *a, **k: _CONFIRMED)), \
             patch('jiangshen_assistant.daily_workflow.DailyStoneExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: stone)), \
             patch('jiangshen_assistant.daily_workflow.DailyRewardExecutor') as rewards:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                   self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_previous_progress_changed'))
        self.assertEqual(by_id(result)[2, 6].status, 'UNKNOWN')
        rewards.assert_not_called()

    def test_mingshi_requires_callback_progress_and_verified_return(self):
        before = observation((2, activity(task_id=7, required=1)))
        after = observation((2, activity(task_id=7, latest=1, cached=1, required=1)))
        snapshots = iter([before, after])
        step = DailyMingshiResult('COMPLETED', 'verified', 0, 1, 1, True, True, 'VERIFIED_CLOSED', True)
        execute = unittest.mock.Mock(return_value=step)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyMingshiExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                   self.token, Path('.'), rewards_enabled=False).run()
        self.assertEqual(by_id(result)[2, 7].status, 'COMPLETED')
        execute.assert_called_once_with(7, character_id='1001', expected_cycle=104400)

    def test_mingshi_unverified_return_stops_following_tasks(self):
        observed = observation((2, activity(task_id=7, required=1)), (5, activity(task_id=20, required=1)))
        step = DailyMingshiResult('BLOCKED', 'return_unconfirmed', 0, 1, 1, True, True, 'UNVERIFIED', True)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyMingshiExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor') as guild:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed), self.token, Path('.')).run()
        self.assertEqual(result.status, 'UNKNOWN')
        self.assertEqual(by_id(result)[2, 7].current, 1)
        guild.assert_not_called()

    def test_mingshi_inconsistent_daily_caches_do_not_count_as_completion(self):
        before = observation((2, activity(task_id=7, required=1)))
        after = observation((2, activity(task_id=7, latest=1, cached=0, required=1,
                                          reason='daily_progress_inconsistent')))
        snapshots = iter([before, after])
        step = DailyMingshiResult('COMPLETED', 'verified', 0, 1, 1, True, True, 'VERIFIED_CLOSED', True)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyMingshiExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)), self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_mingshi_progress_mismatch'))

    def test_refine_requotes_only_after_fresh_same_cycle_progress_and_close(self):
        snapshots = iter([observation((2, activity(task_id=21, latest=i, cached=i, required=2))) for i in range(3)])
        def result(before):
            return DailyRefineResult('COMPLETED', 'verified', before, before + 1, 2, 104400,
                5000, 4000, before, before + 1, 1000, 20, True, True, True, False, 'VERIFIED_CLOSED')
        execute = unittest.mock.Mock(side_effect=[result(0), result(1)])
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyRefineExecutor', return_value=SimpleNamespace(execute=execute)):
            result_value = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                         self.token, Path('.'), rewards_enabled=False).run()
        self.assertEqual(by_id(result_value)[2, 21].status, 'COMPLETED')
        self.assertEqual([call.kwargs['expected_current'] for call in execute.call_args_list], [0, 1])

    def test_soul_rechecks_progress_and_free_offer_between_two_gathers(self):
        snapshots = iter([observation((2, activity(task_id=5, latest=i, cached=i, required=2))) for i in range(3)])
        execute = unittest.mock.Mock(side_effect=[
            DailySoulResult('COMPLETED', 'verified', 0, 1, 2, True, True, True, 'VERIFIED_CLOSED'),
            DailySoulResult('COMPLETED', 'verified', 1, 2, 2, True, True, True, 'VERIFIED_CLOSED')])
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailySoulExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                   self.token, Path('.'), rewards_enabled=False).run()
        self.assertEqual(by_id(result)[2, 5].status, 'COMPLETED')
        self.assertEqual([call.kwargs['expected_current'] for call in execute.call_args_list], [0, 1])

    def test_soul_free_quota_exhausted_reports_partial_progress(self):
        snapshots = iter([observation((2, activity(task_id=5, latest=i, cached=i, required=2))) for i in range(2)])
        execute = unittest.mock.Mock(side_effect=[
            DailySoulResult('COMPLETED', 'verified', 0, 1, 2, True, True, True, 'VERIFIED_CLOSED'),
            DailySoulResult('UNAVAILABLE', 'daily_soul_free_blue_exhausted', 1, 1, 2)])
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailySoulExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                   self.token, Path('.'), rewards_enabled=False).run()
        self.assertEqual((by_id(result)[2, 5].status, by_id(result)[2, 5].current), ('UNAVAILABLE', 1))
        self.assertEqual(execute.call_count, 2)

    def test_soul_unverified_cleanup_stops_following_actions(self):
        observed = observation((2, activity(task_id=5, required=2)), (5, activity(task_id=20, required=1)))
        step = DailySoulResult('UNKNOWN', 'cleanup_unknown', 0, 1, 2, True, True, True, 'UNVERIFIED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailySoulExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor') as guild:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed), self.token, Path('.')).run()
        self.assertEqual((result.status, by_id(result)[2, 5].current), ('UNKNOWN', 1))
        guild.assert_not_called()

    def test_soul_cache_conflict_prevents_next_gather(self):
        before = observation((2, activity(task_id=5, required=2)))
        after = observation((2, activity(task_id=5, latest=1, cached=0, required=2)))
        snapshots = iter([before, after])
        execute = unittest.mock.Mock(return_value=DailySoulResult('COMPLETED', 'verified', 0, 1, 2,
                                                                  True, True, True, 'VERIFIED_CLOSED'))
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailySoulExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)), self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_soul_progress_mismatch'))
        self.assertEqual(execute.call_count, 1)

    def test_refine_inconsistent_fresh_progress_prevents_another_exchange(self):
        before = observation((2, activity(task_id=21, latest=0, cached=0, required=2)))
        after = observation((2, activity(task_id=21, latest=1, cached=0, required=2,
                                          reason='daily_progress_inconsistent')))
        snapshots = iter([before, after])
        step = DailyRefineResult('COMPLETED', 'verified', 0, 1, 2, 104400,
            5000, 4000, 0, 1, 1000, 20, True, True, True, False, 'VERIFIED_CLOSED')
        execute = unittest.mock.Mock(return_value=step)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyRefineExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)), self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_refine_progress_mismatch'))
        self.assertEqual(execute.call_count, 1)

    def test_refine_unverified_ui_stops_with_known_progress(self):
        observed = observation((2, activity(task_id=21, latest=0, cached=0, required=2)),
                               (5, activity(task_id=20, required=1)))
        step = DailyRefineResult('UNKNOWN', 'close_unknown', 0, 1, 2, 104400,
            5000, 4000, 0, 1, 1000, 20, True, True, True, True, 'UNVERIFIED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyRefineExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor') as guild:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed), self.token, Path('.')).run()
        self.assertEqual((result.status, by_id(result)[2, 21].current), ('UNKNOWN', 1))
        guild.assert_not_called()

    def test_arena_chest_requires_fresh_progress_and_closed_ui(self):
        before = observation((6, activity(task_id=3, latest=0, cached=0, required=1)))
        after = observation((6, activity(task_id=3, latest=1, cached=1, required=1)))
        snapshots = iter([before, after])
        step = DailyArenaChestResult('COMPLETED', 'verified', 0, 1, 1, True, True, True, 'VERIFIED_CLOSED')
        execute = unittest.mock.Mock(return_value=step)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyArenaChestExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                   self.token, Path('.'), rewards_enabled=False).run()
        self.assertEqual(by_id(result)[6, 3].status, 'COMPLETED')
        execute.assert_called_once_with(3, character_id='1001', expected_cycle=104400)

    def test_arena_unverified_cleanup_stops_before_guild(self):
        observed = observation((6, activity(task_id=3, required=1)), (5, activity(task_id=20, required=1)))
        step = DailyArenaChestResult('CLAIMED', 'cleanup_unknown', 0, 1, 1, True, True, True, 'UNVERIFIED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyArenaChestExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)), \
             patch('jiangshen_assistant.daily_workflow.DailyGuildExecutor') as guild:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed), self.token, Path('.')).run()
        self.assertEqual((result.status, by_id(result)[6, 3].current), ('UNKNOWN', 1))
        guild.assert_not_called()

    def test_arena_inconsistent_caches_do_not_count_as_complete(self):
        before = observation((6, activity(task_id=3, latest=0, cached=0, required=1)))
        after = observation((6, activity(task_id=3, latest=1, cached=0, required=1,
                                          reason='daily_progress_inconsistent')))
        snapshots = iter([before, after])
        step = DailyArenaChestResult('COMPLETED', 'verified', 0, 1, 1, True, True, True, 'VERIFIED_CLOSED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyArenaChestExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)), self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_arena_progress_mismatch'))

    def test_disabled_reward_remains_unimplemented(self):
        observed = observation()
        observed['categories'][1]['reward_ready'] = True
        execute = unittest.mock.Mock(return_value=SimpleNamespace(status='DISABLED', reason='disabled'))
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyRewardExecutor', return_value=SimpleNamespace(execute=execute)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed),
                                   self.token, Path('.')).run()
        self.assertEqual(by_id(result)[2, None].status, 'UNSUPPORTED')
        execute.assert_called_once()

    def test_quiz_precedes_reward_and_brawl_is_last(self):
        def sample(quiz):
            value = observation((3, activity(task_id=12, latest=quiz, required=1)),
                                (6, activity(task_id=13, latest=0, required=1)))
            value['categories'][1]['reward_ready'] = True
            return value
        snapshots, order = iter([sample(0), sample(1)]), []
        def timed(task_id, *, character_id):
            order.append(task_id)
            return DailyTimedResult('COMPLETED', 'account_progress_observed', 0, 1, 1,
                1829 if task_id == 12 else 321, 'UNKNOWN',
                'VERIFIED_CLOSED' if task_id == 12 else 'BRAWL_MAY_REMAIN_OPEN')
        def reward(category_id, *, character_id):
            order.append('reward')
            return SimpleNamespace(status='COMPLETED', reason='verified', operation_id='id', panel_closed=True)
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyTimedExecutor', return_value=SimpleNamespace(execute=timed)), \
             patch('jiangshen_assistant.daily_workflow.DailyRewardExecutor', return_value=SimpleNamespace(execute=reward)):
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)),
                                   self.token, Path('.')).run()
        self.assertEqual(order, [12, 'reward', 13])
        self.assertEqual(by_id(result)[3, 12].status, 'COMPLETED')
        self.assertEqual((by_id(result)[6, 13].status, by_id(result)[6, 13].reason),
                         ('COMPLETED', 'daily_brawl_view_remains'))

    def test_quiz_cache_conflict_stops_before_reward(self):
        before = observation((3, activity(task_id=12, latest=0, cached=0, required=1)))
        after = observation((3, activity(task_id=12, latest=1, cached=0, required=1)))
        before['categories'][2]['reward_ready'] = True
        snapshots = iter([before, after])
        step = DailyTimedResult('COMPLETED', 'verified', 0, 1, 1, 1829, 'UNKNOWN', 'VERIFIED_CLOSED')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyTimedExecutor', return_value=SimpleNamespace(execute=lambda *a, **k: step)), \
             patch('jiangshen_assistant.daily_workflow.DailyRewardExecutor') as rewards:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)), self.token, Path('.')).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_timed_progress_mismatch'))
        rewards.assert_not_called()

    def test_category_reward_is_not_claimed_with_inconsistent_task_cache(self):
        observed = observation((2, activity(task_id=8, latest=1, cached=0, required=1)))
        observed['categories'][1]['reward_ready'] = True
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyRewardExecutor') as rewards:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed), self.token, Path('.')).run()
        self.assertEqual((by_id(result)[2, None].status, by_id(result)[2, None].reason),
                         ('UNAVAILABLE', 'daily_progress_inconsistent'))
        rewards.return_value.execute.assert_not_called()

    def test_blocked_tribute_cannot_hide_raw_conflict_from_reward_gate(self):
        before = tribute_observation(latest=0, required=1)
        after = tribute_observation(latest=1, cached=0, required=1)
        after['categories'][1]['reward_ready'] = True
        snapshots = iter([before, after])
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow',
                   return_value=SimpleNamespace(execute=lambda *a, **k: WorkflowResult('BLOCKED', 'tribute_blocked'))), \
             patch('jiangshen_assistant.daily_workflow.DailyRewardExecutor') as rewards:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: next(snapshots)), self.token, Path('.')).run()
        self.assertEqual((by_id(result)[2, None].status, by_id(result)[2, None].reason),
                         ('UNAVAILABLE', 'daily_progress_inconsistent'))
        rewards.return_value.execute.assert_not_called()

    def test_unverified_quiz_cleanup_stops_before_other_actions(self):
        observed = observation((3, activity(task_id=12, required=1)), (6, activity(task_id=13, required=1)))
        timed = unittest.mock.Mock(return_value=DailyTimedResult('COMPLETED', 'ok', 0, 1, 1,
                                                                1829, 'UNKNOWN', 'UNVERIFIED'))
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'), \
             patch('jiangshen_assistant.daily_workflow.DailyTimedExecutor', return_value=SimpleNamespace(execute=timed)), \
             patch('jiangshen_assistant.daily_workflow.DailyRewardExecutor') as rewards:
            result = DailyWorkflow(SimpleNamespace(observe_daily=lambda token: observed),
                                   self.token, Path('.')).run()
        self.assertEqual(result.status, 'UNKNOWN')
        self.assertEqual(timed.call_count, 1)
        rewards.assert_not_called()

    def test_unverified_initial_state_runs_one_refresh_and_reobserves(self):
        missing = dict(schema=1, available=False, reason='daily_refresh_unverified',
            source='client_projection', character_id='', server_now=None, server_day=None,
            refresh_clear=None, categories=[])
        snapshots = iter([missing, observation(character_id='12345')])
        seen = []
        objects = SimpleNamespace(observe_daily=lambda token: next(snapshots))
        def begin(token, operation_id):
            seen.append(operation_id)
            return dict(ack={'status': 'COMPLETED'}, requested_seq=1)
        def current(token):
            return dict(refresh('OBSERVED'), operation_id=seen[0])
        objects.begin_daily_refresh = begin
        objects.daily_refresh_snapshot = current
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow'):
            result = DailyWorkflow(objects, token, Path('.'), refresh_enabled=True).run()
        self.assertEqual(result.status, 'PARTIAL')
        self.assertEqual(len(seen), 1)

    def test_unconfirmed_refresh_never_starts_a_business_action(self):
        missing = dict(schema=1, available=False, reason='daily_refresh_unverified',
            source='client_projection', character_id='', server_now=None, server_day=None,
            refresh_clear=None, categories=[])
        objects = SimpleNamespace(observe_daily=lambda token: missing,
            begin_daily_refresh=lambda token, operation_id: dict(ack={'status': 'COMPLETED'}, requested_seq=1),
            daily_refresh_snapshot=lambda token: dict(refresh('OBSERVED'), operation_id='0' * 32))
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        with patch('jiangshen_assistant.daily_workflow.DevelopmentWorkflow') as execution:
            result = DailyWorkflow(objects, token, Path('.'), refresh_enabled=True).run()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'daily_refresh_binding_changed'))
        execution.assert_not_called()

    def test_unknown_tribute_cannot_be_retried_even_if_progress_advanced(self):
        result = self.run_workflow([tribute_observation(), tribute_observation(latest=2)],
            [WorkflowResult('UNKNOWN', 'development_invocation_unconfirmed')])
        self.assertEqual(self.trace, ['observe', 'tribute', 'observe'])
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'development_invocation_unconfirmed'))
        self.assertEqual(by_id(result)[2, 6].status, 'UNKNOWN')

    def test_no_stock_terminal_is_not_daily_success(self):
        result = self.run_workflow([tribute_observation(), tribute_observation()],
            [WorkflowResult('DONE', 'tribute_peaches_exhausted', 'LOCAL_OBSERVED')])
        self.assertEqual(self.trace, ['observe', 'tribute', 'observe'])
        item = by_id(result)[2, 6]
        self.assertEqual((item.status, item.reason), ('BLOCKED', 'tribute_peaches_exhausted'))

    def test_rejected_native_resource_admission_is_reported(self):
        result = self.run_workflow([tribute_observation(), tribute_observation()],
            [WorkflowResult('BLOCKED', 'tribute_premium_mode_forbidden')])
        self.assertEqual(self.trace, ['observe', 'tribute', 'observe'])
        self.assertEqual(by_id(result)[2, 6].reason, 'tribute_premium_mode_forbidden')

    def test_categories_one_and_its_reward_are_excluded_and_other_items_are_reported(self):
        observed = observation((1, activity(task_id=1)), (3, activity(task_id=7)),
            (4, activity(task_id=12, availability='unavailable', reason='quiz_closed')))
        observed['categories'][2]['reward_ready'] = True
        result = self.run_workflow([observed])
        items = by_id(result)
        self.assertEqual(self.trace, ['observe'])
        self.assertEqual({item.category_id for item in result.items}, {2, 3, 4, 5, 6})
        self.assertEqual(items[2, 6].reason, 'daily_tribute_task_missing')
        self.assertEqual(items[3, 7].status, 'UNSUPPORTED')
        self.assertEqual(items[3, None].status, 'UNSUPPORTED')
        self.assertEqual((items[4, 12].status, items[4, 12].reason), ('UNAVAILABLE', 'quiz_closed'))

    def test_task_number_outside_category_two_never_executes(self):
        result = self.run_workflow([observation((1, activity()))])
        self.assertEqual(self.trace, ['observe'])
        self.assertEqual(result.reason, 'daily_tribute_task_missing')

    def test_missing_latest_counter_cannot_authorize_first_peach(self):
        result = self.run_workflow([tribute_observation(latest=None)])
        self.assertEqual(self.trace, ['observe'])
        self.assertEqual(by_id(result)[2, 6].status, 'UNAVAILABLE')

    def test_stop_before_start_avoids_observation_and_mutation(self):
        self.stopped = True
        result = self.run_workflow([])
        self.assertEqual(self.trace, [])
        self.assertEqual(result.status, 'STOPPED')

    def test_stop_after_first_peach_prevents_repeat(self):
        def step(_):
            self.stopped = True
            return _CONFIRMED

        result = self.run_workflow([tribute_observation(), tribute_observation(latest=1)], [step])
        self.assertEqual(self.trace, ['observe', 'tribute', 'observe'])
        self.assertEqual(result.status, 'STOPPED')
        self.assertEqual(by_id(result)[2, 6].status, 'STOPPED')

    def test_after_pause_refresh_before_spending(self):
        def on_publish(result):
            if result.status == 'RUNNING':
                self.paused = True

        def resume():
            self.trace.append('paused')
            self.paused = False

        result = self.run_workflow([tribute_observation(), tribute_observation(latest=2)],
            on_publish=on_publish, on_wait=resume)
        self.assertEqual(self.trace, ['observe', 'paused', 'observe'])
        self.assertEqual(by_id(result)[2, 6].status, 'ALREADY_COMPLETE')

    def test_daily_policy_blocks_rankup_and_premium_actions(self):
        for action, level, exp, food_type, quote, reason in (
            ('tribute_open', 10, 100, 0, 0, 'tribute_rankup_required'),
            ('tribute_rankup', 10, 100, 0, 0, 'tribute_rankup_required'),
            ('tribute_train', 11, 10, 1, 0, 'tribute_premium_mode_forbidden'),
            ('tribute_train', 11, 10, 0, 1, 'tribute_premium_or_unknown_quote'),
        ):
            with self.subTest(action=action, food_type=food_type, quote=quote):
                def native_observation(policy):
                    planned = policy.plan(dict(schema=1, gate='PASS', operation={'status': 'IDLE'},
                        available_action=action, desired_level=0, level=level, experience=exp,
                        next_exp=100, food=3, food_type=food_type, gold=100, refreshing=False,
                        cost={'gold': quote}), revision=1, context_key=self.token.context_key)
                    self.assertIsNone(planned.command)
                    return WorkflowResult(planned.status, planned.reason)

                result = self.run_workflow([tribute_observation(), tribute_observation()], [native_observation])
                self.assertEqual(by_id(result)[2, 6].reason, reason)

    def test_donation_choice_is_read_only_and_does_not_run_an_unsupported_route(self):
        observed = observation((3, activity(task_id=7)))
        objects = SimpleNamespace(observe_daily=lambda token: observed)
        self.assertEqual(DailyWorkflow(objects, self.token, Path('.')).donation_type, 2)
        for choice in (1, 2, 3):
            workflow = DailyWorkflow(objects, self.token, Path('.'), donation_type=choice)
            self.assertEqual(workflow.donation_type, choice)
            with self.assertRaises(AttributeError):
                workflow.donation_type = 3
            self.assertEqual(by_id(workflow.run())[3, 7].status, 'UNSUPPORTED')
        for invalid in (False, True, 0, 4, '2', None):
            with self.assertRaises(ValueError):
                DailyWorkflow(objects, self.token, Path('.'), donation_type=invalid)

    def test_real_executor_receipt_and_daily_projection_both_required(self):
        commands = []
        tribute = dict(schema=1, gate='PASS', operation={'status': 'IDLE'},
            available_action='tribute_train', desired_level=0, level=1, experience=0,
            next_exp=100, food=1, food_type=0, gold=100, refreshing=False, cost={'gold': 0},
            ui={'spirit': {'visible': False}, 'rankup': {'visible': False}, 'nested_owned': False})
        development = {'revision': 1, 'gate': 'PASS', 'tribute': tribute}
        closed = []

        def latest():
            return {'context_key': self.token.context_key, 'development': development}

        def subscribe(token, *, notify):
            self.assertIs(token, self.token)
            return SimpleNamespace(latest=latest, drain=lambda: (), close=lambda: closed.append(True))

        def observe_development(token, *, fields):
            self.assertIs(token, self.token)
            self.assertEqual((fields['action'], fields['mode']), ('tribute_observe', 'fruit'))
            return latest()

        def submit(token, **command):
            self.assertIs(token, self.token)
            self.assertEqual((command['action'], command['mode'], command['max_requests'],
                command['max_money'], command['max_food']), ('tribute_train', 'fruit', 1, 0, 1))
            commands.append(command)
            tribute.update(done=True, reason='tribute_peaches_exhausted', available_action=None,
                experience=10, food=0, operation=dict(command, status='OBSERVED', receipt_opcode=1136,
                    receipt_result=0, receipt_level=1, receipt_experience=10, receipt_food=1,
                    receipt_gold=0, server='PASS'))
            development['revision'] += 1
            receipt = dict(command, status='INVOKED', sequence=2)
            return {'requested_seq': 2, 'ack': {'command_seq': 2, 'status': 'COMPLETED',
                'result': {'development': {'last_command': receipt}}}}

        observations = iter((tribute_observation(latest=0, required=1),
                             tribute_observation(latest=1, required=1)))
        objects = SimpleNamespace(subscribe_workflow=subscribe, observe_development=observe_development,
            submit_development=submit, observe_daily=lambda token: next(observations))
        with TemporaryDirectory() as data_dir:
            result = DailyWorkflow(objects, self.token, data_dir, wait=lambda: None).run()
            self.assertEqual(by_id(result)[2, 6].status, 'COMPLETED')
            self.assertEqual(len(result.ledger_paths), 1)
            self.assertTrue(Path(result.ledger_paths[0]).is_file())
        self.assertEqual(len(commands), 1)
        self.assertEqual(closed, [True])


if __name__ == '__main__':
    unittest.main()
