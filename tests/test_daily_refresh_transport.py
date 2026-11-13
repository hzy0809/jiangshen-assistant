from copy import deepcopy
from types import SimpleNamespace
import threading
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.objects import ObjectController, CampaignAdmissionClosed
from tests import test_bridge_client as fixtures


OP = '1234567890abcdef1234567890abcdef'


def refresh(status='IDLE'):
    idle = status == 'IDLE'
    observed = status == 'OBSERVED'
    return dict(schema=1, status=status, reason='', operation_id='' if idle else OP,
        sequence=0 if idle else 1, expected_revision=0 if idle else 2,
        character_id='' if idle else '12345', opcode=16, invoked=not idle,
        callback_returned=observed, context_rebuilt=observed,
        server_time=120000 if observed else None, server_cycle=104400 if observed else None,
        receipt_hash='a' * 64 if observed else '')


class DailyRefreshTests(unittest.TestCase):
    setUp = fixtures.BridgeTests.setUp
    client = fixtures.BridgeTests.client
    common = fixtures.BridgeTests.common
    state = fixtures.BridgeTests.state
    ack = fixtures.BridgeTests.ack

    def ready(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_refresh'] = refresh()
        client._last_state['business'] = dict(pending_attempt=False, pending_development=False,
            pending_player_refresh=False, pending_daily_guild=False)
        return client

    def test_only_fixed_empty_budget_refresh_is_allowed(self):
        client = self.ready()
        with self.assertRaises(bridge.BridgeError):
            client.request('daily-state-refresh')
        values = dict(action='daily_state_refresh', expected_revision=2, operation_id=OP,
            target_id=0, desired_level=0, max_requests=0, max_money=0, max_stone=0, max_food=0, mode='')
        self.assertEqual(bridge._validate_development_fields(values, daily_refresh=True), values)
        for changed in (dict(max_money=1), dict(action='player_state_refresh'), dict(max_requests=1)):
            with self.subTest(changed=changed), self.assertRaises(bridge.BridgeError):
                bridge._validate_development_fields(dict(values, **changed), daily_refresh=True)

    def test_invocation_receipt_is_bound_to_this_command(self):
        client = self.ready()
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            state = self.state(client, seq)
            state['daily_refresh'] = refresh('WAITING')
            state['campaign_gui'] = dict(command_seq=seq, phase='completed', invocation_attempted=True)
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result']['daily_refresh'] = deepcopy(state['daily_refresh'])
            bridge._atomic_json(client.session_dir / 'state.json', state)
            bridge._atomic_json(client.session_dir / 'ack.json', ack)
            return seq
        client._send = Mock(side_effect=send)
        wire = client.daily_state_refresh(expected_revision=2, operation_id=OP, timeout=.2)
        self.assertEqual(wire['ack']['result']['daily_refresh']['status'], 'WAITING')
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines()[3:5],
                         ['command=daily-state-refresh', 'action=daily_state_refresh'])

    def test_observed_requires_callback_rebuild_and_current_cycle(self):
        observed = refresh('OBSERVED')
        self.assertTrue(bridge._daily_refresh_valid(observed))
        for changed in (dict(callback_returned=False), dict(context_rebuilt=False),
                        dict(server_cycle=120001), dict(receipt_hash=''), dict(character_id='0')):
            with self.subTest(changed=changed):
                self.assertFalse(bridge._daily_refresh_valid(dict(observed, **changed)))

    def test_pending_business_cannot_start_refresh(self):
        client = self.ready()
        client._last_state['business']['pending_daily_guild'] = True
        with self.assertRaises(bridge.BridgeError):
            client.daily_state_refresh(expected_revision=2, operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)


class DailyRefreshAdmissionTests(unittest.TestCase):
    def test_pause_blocks_refresh_before_calling_bridge(self):
        client = SimpleNamespace(last_state={'development': {'revision': 2}}, daily_state_refresh=Mock())
        controller = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _campaign_admission=False, _campaign_stopping=False, _busy=False)
        with self.assertRaises(CampaignAdmissionClosed):
            ObjectController.begin_daily_refresh(controller, object(), OP)
        client.daily_state_refresh.assert_not_called()
