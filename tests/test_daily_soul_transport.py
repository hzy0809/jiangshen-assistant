from copy import deepcopy
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.objects import ObjectController, CampaignAdmissionClosed
from tests import test_bridge_client as fixtures
from tests.test_daily_soul import SoulBridge

OP = '1234567890abcdef1234567890abcdef'


def soul(sequence=0, operation_id='', stage='', status='IDLE'):
    value = deepcopy(SoulBridge().snapshot)
    value['revision'] = 2
    if stage:
        value['action'] = ''
        value['operation'] = SoulBridge._op(status, stage, operation_id, sequence, 0)
    return value


class SoulTransportTests(unittest.TestCase):
    setUp = fixtures.BridgeTests.setUp
    client = fixtures.BridgeTests.client
    common = fixtures.BridgeTests.common
    state = fixtures.BridgeTests.state
    ack = fixtures.BridgeTests.ack

    def test_deadline_expiring_while_waiting_for_bridge_lock_never_publishes(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_soul_enabled'] = True
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _campaign_admission=True, _campaign_stopping=False,
            _busy=False, _command_event_locked=Mock(), _wake=threading.Event())
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        checked = threading.Event()
        expired = threading.Event()
        errors = []
        def admission():
            checked.set()
            return not expired.is_set()
        def submit():
            try:
                ObjectController.submit_daily_soul(owner, token, 'open', expected_revision=1,
                    character_id='12345', task_id=5, operation_id=OP, admission=admission)
            except Exception as exc:
                errors.append(exc)
        with client._request_lock:
            thread = threading.Thread(target=submit)
            thread.start()
            self.assertTrue(checked.wait(2))
            expired.set()
        thread.join(2)
        self.assertFalse(thread.is_alive())
        self.assertEqual(len(errors), 1)
        self.assertIsInstance(errors[0], CampaignAdmissionClosed)
        self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir / 'command.txt').exists())
        self.assertFalse(client._unknown)
        self.assertTrue(owner._campaign_admission)

    def test_deadline_expiring_during_revalidation_never_publishes(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_soul_enabled'] = True
        expired = threading.Event()
        revalidate = client.revalidate
        def slow_revalidate():
            revalidate()
            expired.set()
        client.revalidate = slow_revalidate
        with self.assertRaises(bridge.BridgeAdmissionClosed):
            client.daily_soul_command('open', expected_revision=1, character_id='12345',
                task_id=5, operation_id=OP, admission=lambda: not expired.is_set())
        self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir / 'command.txt').exists())
        self.assertFalse(client._unknown)

    def respond(self, client, value, mutate=None):
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            state = self.state(client, seq)
            gui = dict(command_seq=seq, phase='completed', invocation_attempted=True)
            state.update(daily_soul=deepcopy(value), daily_soul_enabled=True, campaign_gui=deepcopy(gui))
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result'].update(daily_soul=deepcopy(value), daily_soul_enabled=True, campaign_gui=deepcopy(gui))
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
                client._last_state.pop('daily_soul_enabled', None)
            else:
                client._last_state['daily_soul_enabled'] = marker
            with self.assertRaises(bridge.BridgeError):
                client.daily_soul_observe(timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.request('daily-soul-observe', timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.daily_soul_command('open', expected_revision=1, character_id='12345',
                                          task_id=5, operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)

    def test_fixed_wire_and_exact_gui_invocation(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_soul_enabled'] = True
        self.respond(client, soul(1, OP, 'open', 'OPENING'))
        wire = client.daily_soul_command('open', expected_revision=1, character_id='12345',
                                         task_id=5, operation_id=OP, timeout=.2)
        self.assertEqual(wire['ack']['result']['daily_soul']['operation']['operation_id'], OP)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-soul-command',
             'action=open', 'operation_id=' + OP, 'character_id=12345', 'task_id=5', 'expected_revision=1'])

    def test_foreign_operation_and_cycle_are_unknown(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_soul_enabled'] = True
        value = soul(1, OP, 'open', 'OPENING')
        def change(state, ack):
            state['daily_soul']['revision'] += 1
            state['daily_soul']['cycle'] += 86400
            state['daily_soul']['operation']['operation_id'] = 'f' * 32
        self.respond(client, value, mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_soul_command('open', expected_revision=1, character_id='12345',
                                      task_id=5, operation_id=OP, timeout=.2)


class SoulAdmissionTests(unittest.TestCase):
    def test_missing_marker_and_expired_admission_do_not_send(self):
        client = SimpleNamespace(last_state={}, daily_soul_observe=Mock(), daily_soul_command=Mock())
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _campaign_admission=True, _campaign_stopping=False, _busy=False)
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        self.assertEqual(ObjectController.observe_daily_soul(owner, token), {'enabled': False, 'soul': None})
        with self.assertRaises(CampaignAdmissionClosed):
            ObjectController.submit_daily_soul(owner, token, 'open', expected_revision=1, character_id='12345',
                                               task_id=5, operation_id=OP, admission=lambda: False)
        client.daily_soul_observe.assert_not_called()
        client.daily_soul_command.assert_not_called()
