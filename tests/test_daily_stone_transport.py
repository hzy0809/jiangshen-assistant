from copy import deepcopy
import unittest
import threading
from types import SimpleNamespace
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.objects import ObjectController, CampaignAdmissionClosed
from tests import test_bridge_client as fixtures

OP = '1234567890abcdef1234567890abcdef'


def stone(sequence=0, operation_id='', status='IDLE'):
    return dict(schema=1, revision=2,
        view=dict(task_id=16, role_id='12345', cycle=100000, current=0, required=1,
                  silver_picks=1, gold_cost=None, silver_pick_cost=None, next_action='', reason=''),
        operation=dict(status=status, reason='', operation_id=operation_id, sequence=sequence,
                       mutation_attempted=False, stock_callback_returned=False,
                       account_progress_observed=False, panel_closed=False))


class StoneTransportTests(unittest.TestCase):
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
            state.update(daily_stone=deepcopy(value), daily_stone_enabled=True,
                         campaign_gui=dict(command_seq=seq, phase='completed', invocation_attempted=True))
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result'].update(daily_stone=deepcopy(value), daily_stone_enabled=True)
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
                client._last_state.pop('daily_stone_enabled', None)
            else:
                client._last_state['daily_stone_enabled'] = marker
            with self.assertRaises(bridge.BridgeError):
                client.daily_stone_observe(timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.request('daily-stone-observe', timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.daily_stone_command('open', expected_revision=1, character_id='12345',
                                           task_id=16, operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)

    def test_fixed_command_and_bound_receipt(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_stone_enabled'] = True
        self.respond(client, stone(1, OP, 'OPENING'))
        wire = client.daily_stone_command('open', expected_revision=1, character_id='12345',
                                          task_id=16, operation_id=OP, timeout=.2)
        self.assertEqual(wire['ack']['result']['daily_stone']['operation']['operation_id'], OP)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-stone-command',
             'action=open', 'operation_id=' + OP, 'character_id=12345', 'task_id=16', 'expected_revision=1'])

    def test_wrong_task_or_receipt_is_rejected(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_stone_enabled'] = True
        with self.assertRaises(bridge.BridgeError):
            client.daily_stone_command('dig', expected_revision=1, character_id='12345',
                                       task_id=17, operation_id=OP, timeout=.2)
        self.respond(client, stone(1, 'f' * 32, 'OPENING'))
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_stone_command('open', expected_revision=1, character_id='12345',
                                       task_id=16, operation_id=OP, timeout=.2)
        self.assertEqual(client._send.call_count, 1)

    def test_contradictory_current_operation_or_cycle_is_unknown(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_stone_enabled'] = True
        def change(state, ack):
            state['daily_stone']['revision'] += 1
            state['daily_stone']['view']['cycle'] += 86400
            state['daily_stone']['operation']['operation_id'] = 'f' * 32
        self.respond(client, stone(1, OP, 'OPENING'), mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_stone_command('open', expected_revision=1, character_id='12345',
                                       task_id=16, operation_id=OP, timeout=.2)

    def test_observation_rejects_later_foreign_operation_and_cycle(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_stone_enabled'] = True
        def change(state, ack):
            state['daily_stone']['revision'] += 1
            state['daily_stone']['view']['cycle'] += 86400
            state['daily_stone']['operation'].update(operation_id='f' * 32, sequence=1, status='OPEN')
        self.respond(client, stone(), mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_stone_observe(timeout=.2)

    def test_newer_state_cannot_undo_closed_witness(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_stone_enabled'] = True
        closed = stone(1, OP, 'COMPLETED')
        closed['operation'].update(mutation_attempted=True, stock_callback_returned=True,
                                   account_progress_observed=True, panel_closed=True)
        def change(state, ack):
            state['daily_stone']['revision'] += 1
            state['daily_stone']['operation'].update(status='UNKNOWN', panel_closed=False)
        self.respond(client, closed, mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_stone_command('close', expected_revision=1, character_id='12345',
                                       task_id=16, operation_id=OP, timeout=.2)

    def test_pending_busy_observation_can_recover_to_later_cycle(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_stone_enabled'] = True
        busy = stone(1, OP, 'PENDING')
        busy['operation']['mutation_attempted'] = True
        busy['view'].update(cycle=0, reason='stone_client_busy')
        def recover(state, ack):
            state['daily_stone']['revision'] += 1
            state['daily_stone']['view'].update(cycle=100000, reason='')
        self.respond(client, busy, mutate=recover)
        wire = client.daily_stone_observe(timeout=.2)
        self.assertEqual(wire['state']['daily_stone']['view']['cycle'], 100000)


class StoneAdmissionTests(unittest.TestCase):
    def test_disabled_capability_does_not_read_wire(self):
        client = SimpleNamespace(last_state={}, daily_stone_observe=Mock())
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
                                _campaign_check_locked=Mock())
        self.assertEqual(ObjectController.observe_daily_stone(owner, object()), {'enabled': False, 'stone': None})
        client.daily_stone_observe.assert_not_called()

    def test_pause_blocks_command_before_publication(self):
        client = SimpleNamespace(daily_stone_command=Mock())
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _campaign_admission=False, _campaign_stopping=False, _busy=False)
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        with self.assertRaises(CampaignAdmissionClosed):
            ObjectController.submit_daily_stone(owner, token, 'open', expected_revision=1,
                                                character_id='12345', task_id=16, operation_id=OP)
        client.daily_stone_command.assert_not_called()

    def test_observation_returns_latest_validated_state(self):
        old, latest = stone(), stone()
        latest['revision'] += 1
        latest['view']['reason'] = 'stone_client_busy'
        client = SimpleNamespace(last_state={'daily_stone_enabled': True}, daily_stone_observe=Mock(
            return_value={'ack': {'status': 'COMPLETED', 'result': {'daily_stone': old, 'daily_stone_enabled': True}},
                          'state': {'daily_stone': latest}}))
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _runtime_state=Mock())
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        self.assertEqual(ObjectController.observe_daily_stone(owner, token)['stone'], latest)

    def test_retired_owner_cannot_receive_old_command_reply(self):
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        client = SimpleNamespace(last_state={}, daily_stone_command=Mock(return_value={'state': {}}))
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(side_effect=[None, bridge.BridgeError('retired')]),
            _campaign_admission=True, _campaign_stopping=False, _busy=False, _campaign_owner=token,
            _command_event_locked=Mock(), _runtime_state=Mock(), _wake=Mock())
        with self.assertRaises(bridge.BridgeError):
            ObjectController.submit_daily_stone(owner, token, 'open', expected_revision=1,
                                                character_id='12345', task_id=16, operation_id=OP)
        self.assertEqual([call.args[2] for call in owner._command_event_locked.call_args_list],
                         ['admitted', 'failed'])
