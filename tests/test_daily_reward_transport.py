from copy import deepcopy
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from tests import test_bridge_client as fixtures


OP = '1234567890abcdef1234567890abcdef'


def reward(*, sequence=0, operation_id='', category_id=0):
    return dict(schema=1, revision=1, status='READY', reason='', next_action='open',
        character_id='12345', operation_id=operation_id, sequence=sequence, category_id=category_id,
        claim_sequence=0, refresh_epoch=104400, invoked=False, unresolved=False,
        callback_returned=False, settled=False, reward_closed=False, receipt_category=0,
        receipt_refresh=0, categories=[dict(id=i, ready=False, claimed=False, reason='') for i in range(2, 7)])


class RewardTransportTests(unittest.TestCase):
    setUp = fixtures.BridgeTests.setUp
    client = fixtures.BridgeTests.client
    common = fixtures.BridgeTests.common
    state = fixtures.BridgeTests.state
    ack = fixtures.BridgeTests.ack

    def respond(self, client, value, *, enabled=True, mutate=None):
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            state = self.state(client, seq)
            state['daily_reward'] = deepcopy(value)
            state['daily_reward_enabled'] = enabled
            state['campaign_gui'] = dict(command_seq=seq, phase='completed', invocation_attempted=True)
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result'].update(daily_reward=deepcopy(value), daily_reward_enabled=enabled)
            if mutate:
                mutate(state, ack)
            bridge._atomic_json(client.session_dir / 'state.json', state)
            bridge._atomic_json(client.session_dir / 'ack.json', ack)
            return seq
        client._send = Mock(side_effect=send)

    def test_disabled_package_cannot_publish_a_reward_action(self):
        client = self.client(); client.connect(timeout=.2)
        for marker in (False, None):
            if marker is None:
                client._last_state.pop('daily_reward_enabled', None)
            else:
                client._last_state['daily_reward_enabled'] = marker
            with self.assertRaises(bridge.BridgeError):
                client.daily_reward_observe(timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.request('daily-reward-observe', timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.daily_reward_command('open', expected_revision=1, character_id='12345',
                                            category_id=2, operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)

    def test_valid_action_has_fixed_wire_and_exact_correlation(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_reward_enabled'] = True
        self.respond(client, reward(sequence=1, operation_id=OP, category_id=2))
        wire = client.daily_reward_command('open', expected_revision=1, character_id='12345',
                                           category_id=2, operation_id=OP, timeout=.2)
        self.assertEqual(wire['ack']['result']['daily_reward']['operation_id'], OP)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-reward-command',
             'action=open', 'expected_revision=1', 'character_id=12345', 'category_id=2', 'operation_id=' + OP])

    def test_money_category_and_changed_receipt_are_rejected(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_reward_enabled'] = True
        with self.assertRaises(bridge.BridgeError):
            client.daily_reward_command('claim', expected_revision=1, character_id='12345',
                                        category_id=1, operation_id=OP, timeout=.2)
        self.respond(client, reward(sequence=1, operation_id=OP, category_id=2),
                     mutate=lambda state, ack: ack['result']['daily_reward'].update(category_id=3))
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_reward_command('open', expected_revision=1, character_id='12345',
                                        category_id=2, operation_id=OP, timeout=.2)
        self.assertEqual(client._send.call_count, 1)
