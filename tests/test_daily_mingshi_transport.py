from copy import deepcopy
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.objects import ObjectController, CampaignAdmissionClosed
from tests import test_bridge_client as fixtures

OP = '1234567890abcdef1234567890abcdef'


def mingshi(sequence=0, operation_id='', status='IDLE'):
    return dict(schema=1, revision=2,
        view=dict(task_id=7, role_id='12345', cycle=100000, current=0, required=1,
                  gold_cost=None, next_action='', reason=''),
        operation=dict(status=status, reason='', operation_id=operation_id, sequence=sequence,
                       mutation_attempted=False, stock_callback_returned=False,
                       account_progress_observed=False, panel_closed=False))


class MingshiTransportTests(unittest.TestCase):
    setUp = fixtures.BridgeTests.setUp
    client = fixtures.BridgeTests.client
    common = fixtures.BridgeTests.common
    state = fixtures.BridgeTests.state
    ack = fixtures.BridgeTests.ack

    def respond(self, client, value, mutate=None):
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            state = self.state(client, seq)
            state.update(daily_mingshi=deepcopy(value), daily_mingshi_enabled=True,
                         campaign_gui=dict(command_seq=seq, phase='completed', invocation_attempted=True))
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result'].update(daily_mingshi=deepcopy(value), daily_mingshi_enabled=True)
            if mutate:
                mutate(state, ack)
            bridge._atomic_json(client.session_dir / 'state.json', state)
            bridge._atomic_json(client.session_dir / 'ack.json', ack)
            return seq
        client._send = Mock(side_effect=send)

    def test_disabled_or_missing_marker_never_publishes(self):
        client = self.client(); client.connect(timeout=.2)
        for marker in (False, None):
            if marker is None:
                client._last_state.pop('daily_mingshi_enabled', None)
            else:
                client._last_state['daily_mingshi_enabled'] = marker
            with self.assertRaises(bridge.BridgeError):
                client.daily_mingshi_observe(timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.request('daily-mingshi-observe', timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.daily_mingshi_command('open', expected_revision=1, character_id='12345',
                                              task_id=7, operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)

    def test_command_is_fixed_and_bound_to_current_receipt(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_mingshi_enabled'] = True
        self.respond(client, mingshi(1, OP, 'OPENING'))
        wire = client.daily_mingshi_command('open', expected_revision=1, character_id='12345',
                                             task_id=7, operation_id=OP, timeout=.2)
        self.assertEqual(wire['ack']['result']['daily_mingshi']['operation']['operation_id'], OP)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-mingshi-command',
             'action=open', 'operation_id=' + OP, 'character_id=12345', 'task_id=7', 'expected_revision=1'])

    def test_foreign_later_observation_is_unknown(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_mingshi_enabled'] = True
        def change(state, ack):
            state['daily_mingshi']['revision'] += 1
            state['daily_mingshi']['view']['cycle'] += 86400
            state['daily_mingshi']['operation'].update(operation_id='f' * 32, sequence=1, status='OPEN')
        self.respond(client, mingshi(), mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_mingshi_observe(timeout=.2)

    def test_newer_state_cannot_undo_closed_witness(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_mingshi_enabled'] = True
        closed = mingshi(1, OP, 'SUCCEEDED')
        closed['operation'].update(mutation_attempted=True, stock_callback_returned=True,
                                   account_progress_observed=True, panel_closed=True)
        def change(state, ack):
            state['daily_mingshi']['revision'] += 1
            state['daily_mingshi']['operation'].update(status='UNKNOWN', panel_closed=False)
        self.respond(client, closed, mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_mingshi_command('close', expected_revision=1, character_id='12345',
                                         task_id=7, operation_id=OP, timeout=.2)


class MingshiAdmissionTests(unittest.TestCase):
    def test_missing_marker_skips_wire_and_pause_blocks_action(self):
        client = SimpleNamespace(last_state={}, daily_mingshi_observe=Mock(), daily_mingshi_command=Mock())
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _campaign_admission=False, _campaign_stopping=False, _busy=False)
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        self.assertEqual(ObjectController.observe_daily_mingshi(owner, token), {'enabled': False, 'mingshi': None})
        with self.assertRaises(CampaignAdmissionClosed):
            ObjectController.submit_daily_mingshi(owner, token, 'harvest', expected_revision=1,
                                                  character_id='12345', task_id=7, operation_id=OP)
        client.daily_mingshi_observe.assert_not_called()
        client.daily_mingshi_command.assert_not_called()
