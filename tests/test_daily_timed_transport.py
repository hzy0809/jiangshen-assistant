from copy import deepcopy
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from tests import test_bridge_client as fixtures

OP = '1234567890abcdef1234567890abcdef'


def timed(*, sequence=0, operation_id='', task_id=0, stage='', status='IDLE'):
    return dict(schema=1, revision=1, character_id='12345',
        tasks={str(i): dict(action='open', reason='', current=0, required=1) for i in (12, 13)},
        operation=dict(operation_id=operation_id, task_id=task_id, sequence=sequence, stage=stage,
            status=status, invoked=bool(sequence), opcode=0, reason='', request_attribution=None,
            counter_before=None, counter_after=None))


class TimedTransportTests(unittest.TestCase):
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
            state['daily_timed'], state['daily_timed_enabled'] = deepcopy(value), enabled
            state['campaign_gui'] = dict(command_seq=seq, phase='completed', invocation_attempted=True)
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result'].update(daily_timed=deepcopy(value), daily_timed_enabled=enabled)
            if mutate:
                mutate(state, ack)
            bridge._atomic_json(client.session_dir / 'state.json', state)
            bridge._atomic_json(client.session_dir / 'ack.json', ack)
            return seq
        client._send = Mock(side_effect=send)

    def test_disabled_or_older_package_never_receives_timed_commands(self):
        client = self.client(); client.connect(timeout=.2)
        for marker in (False, None):
            if marker is None:
                client._last_state.pop('daily_timed_enabled', None)
            else:
                client._last_state['daily_timed_enabled'] = marker
            with self.assertRaises(bridge.BridgeError):
                client.daily_timed_observe(timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.request('daily-timed-observe', timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.daily_timed_command('open', expected_revision=1, character_id='12345',
                                           task_id=12, operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)

    def test_fixed_wire_and_exact_operation(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_timed_enabled'] = True
        self.respond(client, timed(sequence=1, operation_id=OP, task_id=12, stage='open', status='OPENING'))
        wire = client.daily_timed_command('open', expected_revision=1, character_id='12345',
                                           task_id=12, operation_id=OP, timeout=.2)
        self.assertEqual(wire['ack']['result']['daily_timed']['operation']['operation_id'], OP)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-timed-command',
             'action=open', 'operation_id=' + OP, 'character_id=12345', 'task_id=12', 'expected_revision=1'])

    def test_wrong_task_or_stale_receipt_cannot_claim_invocation(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_timed_enabled'] = True
        with self.assertRaises(bridge.BridgeError):
            client.daily_timed_command('participate', expected_revision=1, character_id='12345',
                                       task_id=16, operation_id=OP, timeout=.2)
        with self.assertRaises(bridge.BridgeError):
            client.daily_timed_command('close', expected_revision=1, character_id='12345',
                                       task_id=13, operation_id=OP, timeout=.2)
        self.respond(client, timed(sequence=1, operation_id=OP, task_id=13, stage='open', status='OPENING'))
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_timed_command('open', expected_revision=1, character_id='12345',
                                       task_id=12, operation_id=OP, timeout=.2)
        self.assertEqual(client._send.call_count, 1)
