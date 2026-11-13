from copy import deepcopy
from types import SimpleNamespace
import threading
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.daily import valid_observation
from tests import test_bridge_client as fixtures
from tests import test_objects as object_fixtures
from jiangshen_assistant.objects import ObjectController


def observation():
    categories = [dict(category_id=i, name='', display_order=i, visible=True,
        current=0, required=1, reward_ready=False, reward_claimed=False,
        server_finished=False, reward_refresh=0, tasks=[]) for i in range(1, 7)]
    categories[1]['tasks'].append(dict(task_id=6, name='培养灵兽', current=0,
        latest_current=1, required=5, complete=False, visible=True, reward_ready=None,
        availability='unknown', reason='', ui_button_id=6))
    return dict(schema=1, available=True, reason='', source='client_projection',
                character_id='12345', server_now=1234, server_day=1, refresh_clear=True, categories=categories)


class DailyObservationTests(unittest.TestCase):
    def test_distinct_cached_and_latest_progress_is_valid(self):
        self.assertTrue(valid_observation(observation()))

    def test_unavailable_has_no_fabricated_identity_or_progress(self):
        unavailable = dict(schema=1, available=False, reason='not_ready', source='client_projection',
                           character_id='', server_now=None, server_day=None, refresh_clear=None, categories=[])
        self.assertTrue(valid_observation(unavailable))
        unavailable['character_id'] = '12345'
        self.assertFalse(valid_observation(unavailable))

    def test_invalid_missing_types_and_duplicate_tasks_are_rejected(self):
        changes = [lambda x: x.update(schema=True), lambda x: x.pop('server_now'),
                   lambda x: x.update(refresh_clear=False),
                   lambda x: x.update(server_day=2000),
                   lambda x: x['categories'][1]['tasks'][0].update(required=True),
                   lambda x: x['categories'][1]['tasks'][0].pop('latest_current'),
                   lambda x: x['categories'][2]['tasks'].append(deepcopy(x['categories'][1]['tasks'][0]))]
        for change in changes:
            with self.subTest(change=change):
                value = observation()
                change(value)
                self.assertFalse(valid_observation(value))


class DailyBridgeTests(unittest.TestCase):
    setUp = fixtures.BridgeTests.setUp
    client = fixtures.BridgeTests.client
    common = fixtures.BridgeTests.common
    state = fixtures.BridgeTests.state
    ack = fixtures.BridgeTests.ack

    def respond(self, client, daily):
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            state = self.state(client, seq)
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result']['daily'] = daily
            bridge._atomic_json(client.session_dir / 'state.json', state)
            bridge._atomic_json(client.session_dir / 'ack.json', ack)
            return seq
        client._send = Mock(side_effect=send)

    def test_one_fixed_read_command_returns_its_own_projection(self):
        client = self.client(); client.connect(timeout=.2)
        self.respond(client, observation())
        wire = client.daily_observe(timeout=.2)
        self.assertEqual(wire['ack']['result']['daily'], observation())
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
                         ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-observe'])
        self.assertEqual(client._send.call_count, 1)

    def test_malformed_observation_cannot_be_reported_as_success(self):
        client = self.client(); client.connect(timeout=.2)
        self.respond(client, {'schema': 1})
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_observe(timeout=.2)
        self.assertEqual(client._send.call_count, 1)


class DailyOwnerTests(unittest.TestCase):
    def test_observation_is_detached_and_must_match_owner(self):
        daily = observation()
        client = SimpleNamespace(last_state={})
        client.daily_observe = Mock(return_value={'state': {}, 'ack': {'status': 'COMPLETED', 'result': {'daily': daily}}})
        controller = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(),
            _client=client, _campaign_check_locked=Mock(), _runtime_state=Mock(),
            _campaign_admission=True, _campaign_stopping=False)
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        result = ObjectController.observe_daily(controller, token)
        result['categories'].clear()
        self.assertEqual(len(daily['categories']), 6)
        daily['character_id'] = '54321'
        with self.assertRaisesRegex(bridge.BridgeError, '身份不一致'):
            ObjectController.observe_daily(controller, token)
        self.assertFalse(controller._campaign_admission)


class ConnectionStartFailureTests(unittest.TestCase):
    setUp = object_fixtures.ControllerTests.setUp
    cleanup_controller = object_fixtures.ControllerTests.cleanup_controller

    def test_failed_worker_start_does_not_leave_a_connecting_attempt(self):
        self.controller.bind(self.window)
        self.controller._launch_locked = Mock(side_effect=RuntimeError('thread unavailable'))
        with self.assertRaisesRegex(RuntimeError, 'thread unavailable'):
            self.controller.begin_campaign_connection()
        self.assertEqual(self.controller.bridge_state, 'error')
        self.assertIsNone(self.controller._connection_token)
        self.assertEqual(self.controller.connection_status['phase'], 'queued_failed')


if __name__ == '__main__':
    unittest.main()
