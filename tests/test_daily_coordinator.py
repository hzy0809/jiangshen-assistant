from pathlib import Path
import threading
import unittest
from unittest.mock import patch

from jiangshen_assistant.core import RunnerState, TaskRunner
from jiangshen_assistant.daily_coordinator import DailyCoordinator
from jiangshen_assistant.daily_workflow import DailyItemResult, DailyResult
from jiangshen_assistant.objects import CampaignConnectionToken, CampaignToken


class FakeObjects:
    def __init__(self):
        self.status = {'state': 'ready', 'busy': False, 'generation': 1}
        self.connecting = threading.Event()
        self.owner = self.connection = None
        self.admission = False
        self.released = []
        self.cancelled = []
        self.complete_connect = False
        self.fail_connect = False

    @property
    def connection_status(self):
        return self.status.copy()

    @property
    def has_campaign_owner(self):
        return self.owner is not None

    def begin_campaign_connection(self):
        if self.status['busy']:
            return None
        generation = self.status['generation'] + 1
        self.connection = CampaignConnectionToken(generation)
        if self.fail_connect:
            self.status = {'state': 'error', 'busy': False, 'generation': generation,
                           'error': 'test_identity_changed'}
        else:
            self.status = {'state': 'ready' if self.complete_connect else 'connecting',
                           'busy': not self.complete_connect, 'generation': generation}
        self.connecting.set()
        return self.connection

    def cancel_campaign_connection(self, token):
        self.cancelled.append(token)
        return token is self.connection and self.owner is None

    def acquire_campaign(self, run_id):
        if self.owner is not None:
            raise AssertionError('overlapping ownership')
        self.connection = None
        self.owner = CampaignToken(run_id, 'fake-character', self.status['generation'])
        self.admission = True
        return self.owner

    def pause_campaign(self, token):
        assert token is self.owner
        self.admission = False
        return True

    def resume_campaign(self, token):
        assert token is self.owner
        self.admission = True
        return True

    def stop_campaign(self, token):
        assert token is self.owner
        self.admission = False
        return True

    def release_campaign(self, token, *, cleanup):
        assert token is self.owner
        self.released.append((token, cleanup))
        self.owner = None
        self.admission = False


class DailyCoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.objects = FakeObjects()
        self.runner = TaskRunner()
        self.coordinator = DailyCoordinator(self.objects, self.runner, Path('.'))
        self.addCleanup(self.finish)
        self.addCleanup(self.coordinator.close)
        workflow_patch = patch('jiangshen_assistant.daily_coordinator.DailyWorkflow')
        self.workflow = workflow_patch.start()
        self.addCleanup(workflow_patch.stop)

    def finish(self):
        self.runner.join(2)
        self.assertFalse(self.runner.is_active)
        self.assertFalse(self.coordinator.is_active)
        self.assertTrue(self.coordinator.is_shutdown)

    def test_stop_during_connection_cancels_only_own_attempt(self):
        self.objects.status['state'] = 'disconnected'
        self.assertTrue(self.coordinator.start())
        self.assertTrue(self.objects.connecting.wait(1))
        self.assertTrue(self.coordinator.stop())
        self.assertFalse(self.coordinator.stop())
        self.finish()
        self.workflow.assert_not_called()
        self.assertEqual(self.objects.cancelled, [self.objects.connection])
        self.assertEqual(self.objects.released, [])
        self.assertEqual(self.coordinator.snapshot['status'], 'STOPPED')

    def test_refused_connect_does_not_cancel_foreign_attempt(self):
        self.objects.status.update(state='connecting', busy=True)
        self.objects.connection = CampaignConnectionToken(1)
        self.assertTrue(self.coordinator.start())
        self.finish()
        self.workflow.assert_not_called()
        self.assertEqual(self.objects.cancelled, [])
        self.assertEqual(self.objects.connection, CampaignConnectionToken(1))
        self.assertEqual(self.coordinator.snapshot['reason'], 'object_connection_unavailable')

    def test_failed_connect_retires_attempt(self):
        self.objects.status['state'] = 'disconnected'
        self.objects.fail_connect = True
        self.assertTrue(self.coordinator.start())
        self.finish()
        self.workflow.assert_not_called()
        self.assertEqual(self.objects.cancelled, [self.objects.connection])
        self.assertIn('test_identity_changed', self.coordinator.snapshot['reason'])

    def test_pause_and_resume_change_shared_admission(self):
        executing = threading.Event()

        def run_workflow(_objects, _token, _data_dir, *, stopped, paused, wait, **_options):
            def run():
                executing.set()
                while not stopped():
                    wait()
                return DailyResult('STOPPED', 'user_stop')
            self.assertFalse(paused())
            self.workflow.return_value.run.side_effect = run
            return self.workflow.return_value

        self.workflow.side_effect = run_workflow
        self.assertTrue(self.coordinator.start())
        self.assertTrue(executing.wait(1))
        self.assertFalse(self.coordinator.start())
        self.assertFalse(self.runner.start('overlap', lambda context: None))
        self.assertTrue(self.coordinator.pause())
        self.assertFalse(self.objects.admission)
        self.assertTrue(self.coordinator.is_paused)
        self.assertEqual(self.runner.state, RunnerState.PAUSED)
        self.assertTrue(self.coordinator.resume())
        self.assertTrue(self.objects.admission)
        self.assertEqual(self.runner.state, RunnerState.RUNNING)
        self.assertTrue(self.coordinator.stop())
        self.assertFalse(self.objects.admission)
        self.finish()
        self.assertIsNone(self.objects.owner)
        self.assertFalse(self.objects.released[0][1])

    def test_pause_during_connect_closes_admission_before_workflow(self):
        paused = threading.Event()
        self.objects.status['state'] = 'disconnected'

        def run_workflow(_objects, _token, _data_dir, **controls):
            def run():
                self.assertTrue(controls['paused']())
                self.assertFalse(self.objects.admission)
                paused.set()
                while not controls['stopped']():
                    controls['wait']()
                return DailyResult('STOPPED', 'user_stop')
            self.workflow.return_value.run.side_effect = run
            return self.workflow.return_value

        self.workflow.side_effect = run_workflow
        self.assertTrue(self.coordinator.start())
        self.assertTrue(self.objects.connecting.wait(1))
        self.assertTrue(self.coordinator.pause())
        self.objects.status.update(state='ready', busy=False)
        self.assertTrue(paused.wait(1))
        self.coordinator.close()
        self.finish()
        self.assertTrue(self.objects.released[0][1])
        self.assertFalse(self.coordinator.start())

    def test_workflow_failure_releases_owned_connection_and_retains_progress(self):
        self.objects.status['state'] = 'disconnected'
        self.objects.complete_connect = True
        item = DailyItemResult('task', 2, 6, '培养灵兽', 'PENDING', 'pending', 1, 2)

        def run_workflow(_objects, _token, _data_dir, *, publish, **_controls):
            def run():
                publish(DailyResult('RUNNING', 'pending', '123', (item,), ('ledger.json',)))
                raise RuntimeError('outcome unavailable')
            self.workflow.return_value.run.side_effect = run
            return self.workflow.return_value

        self.workflow.side_effect = run_workflow
        self.assertTrue(self.coordinator.start())
        self.finish()
        self.assertIsNone(self.objects.owner)
        self.assertTrue(self.objects.released[0][1])
        self.assertEqual(self.objects.cancelled, [])
        result = self.coordinator.snapshot
        self.assertEqual((result['status'], result['reason']), ('UNKNOWN', 'outcome unavailable'))
        self.assertEqual(result['character_id'], '123')
        self.assertEqual(result['items'][0]['name'], '培养灵兽')
        self.assertEqual(result['ledger_paths'], ('ledger.json',))
        result['items'][0]['name'] = 'modified'
        self.assertEqual(self.coordinator.snapshot['items'][0]['name'], '培养灵兽')

    def test_replaced_connection_is_released_without_cleanup_and_never_used(self):
        self.objects.status['state'] = 'disconnected'
        self.objects.complete_connect = True
        acquire = self.objects.acquire_campaign
        replaced = []

        def acquire_replacement(run_id):
            replaced.append(self.objects.connection)
            self.objects.status['generation'] += 1
            return acquire(run_id)

        self.objects.acquire_campaign = acquire_replacement
        self.assertTrue(self.coordinator.start())
        self.finish()
        self.workflow.assert_not_called()
        self.assertFalse(self.objects.released[0][1])
        self.assertEqual(self.objects.cancelled, replaced)
        self.assertEqual(self.coordinator.snapshot['reason'], 'object_connection_replaced')

    def test_cleanup_error_keeps_terminal_item_evidence(self):
        self.objects.status['state'] = 'disconnected'
        self.objects.complete_connect = True
        release = self.objects.release_campaign

        def release_with_error(token, *, cleanup):
            release(token, cleanup=cleanup)
            raise RuntimeError('disconnect could not start')

        self.objects.release_campaign = release_with_error
        item = DailyItemResult('task', 2, 6, '培养灵兽', 'COMPLETED', 'confirmed', 1, 1)
        self.workflow.return_value.run.return_value = DailyResult('DONE', 'daily_items_observed_complete',
                                                                  '123', (item,), ('ledger.json',))
        self.assertTrue(self.coordinator.start())
        self.finish()
        self.assertIsNone(self.objects.owner)
        self.assertTrue(self.objects.released[0][1])
        self.assertEqual(self.coordinator.snapshot['status'], 'UNKNOWN')
        self.assertIn('disconnect could not start', self.coordinator.snapshot['reason'])
        self.assertEqual(self.coordinator.snapshot['items'][0]['status'], 'COMPLETED')
        self.assertEqual(self.coordinator.snapshot['ledger_paths'], ('ledger.json',))

    def test_runner_start_exception_clears_active_state(self):
        with patch.object(self.runner, 'start', side_effect=RuntimeError('cannot start')):
            self.assertFalse(self.coordinator.start())
        self.finish()
        self.assertEqual(self.coordinator.snapshot['status'], 'BLOCKED')

    def test_donation_option_is_validated_and_passed_per_run(self):
        result = DailyResult('PARTIAL', 'daily_items_uncompleted')
        self.workflow.return_value.run.return_value = result
        for unsupported in (0, 4, True, '2'):
            with self.subTest(unsupported=unsupported):
                self.assertFalse(self.coordinator.start(unsupported))
        self.workflow.assert_not_called()
        for options, donation_type in (((), 2), ((1,), 1), ((3,), 3)):
            with self.subTest(donation_type=donation_type):
                self.assertTrue(self.coordinator.start(*options))
                self.finish()
                self.assertEqual(self.workflow.call_args.kwargs['donation_type'], donation_type)
                self.assertEqual(self.coordinator.events.get_nowait().status, 'CONNECTING')
                self.assertEqual(self.coordinator.events.get_nowait().status, 'RUNNING')
                self.assertEqual(self.coordinator.events.get_nowait(), result)


if __name__ == '__main__':
    unittest.main()
