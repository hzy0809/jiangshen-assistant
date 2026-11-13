"""Controller fakes exercise concurrency and invalidation without Tk or a game."""
from pathlib import Path
import json
import queue
import tempfile
import threading
import time
import unittest
import uuid
from unittest.mock import Mock

from jiangshen_assistant.objects import ObjectController, CampaignAdmissionClosed
from jiangshen_assistant.bridge_client import BridgeError
from jiangshen_assistant.mono_runtime import ProcessIdentity, Refusal
from jiangshen_assistant.windows import WindowInfo


def eventually(predicate, seconds=1.5):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.005)
    raise AssertionError("condition did not settle")


class FakeReader:
    def __init__(self):
        self.calls = []
        self.error = None

    def read(self, pid, expected_creation=None):
        self.calls.append((pid, expected_creation))
        if self.error:
            raise self.error
        return {"pid": pid, "process_creation_filetime": expected_creation,
                "level": 86, "stamina": 20, "gold": 375, "bind_gold": 5,
                "money": 100, "mana": 200, "observed_at": "2026-10-31T00:00:00+00:00"}


class FakeClient:
    def __init__(self, project_root, identity, revalidate, on_state=None):
        self.identity, self.revalidate = identity, revalidate
        self.nonce = "1" * 32
        self.session_dir = Path(project_root) / "data" / "object_sessions" / self.nonce
        self.on_state = on_state or (lambda state: None)
        self.terminal = False
        self.connect_calls = self.verify_calls = self.stop_calls = self.heartbeat_calls = 0
        self.block_action = False
        self.marked_unknown = False
        self.battle_epoch = ''
        self.last_state = {"lifecycle": "ready", "ready": True, "main_thread_verified": True,
                           "component_destroyed": False, "main_instance_id": 321, "gate": "PASS",
                           "snapshot": {"safe": True, "gate": "PASS", "main_enabled": True, "main_instance_id": 321},
                           "action": {"outcome": "NONE", "mutation_attempted": False}}

    def connect(self, cancel=None):
        self.connect_calls += 1
        self.revalidate()
        self.on_state(self.last_state)
        return self.last_state

    def open_battle_admission(self):
        # Explicit no-game test double; production uses named OS objects.
        self.battle_epoch = uuid.uuid4().hex
        return self.battle_epoch

    def capture_battle_epoch(self):
        if not self.battle_epoch:
            raise BridgeError('battle_launch_admission_closed')
        return self.battle_epoch

    def cancel_battle_admission(self):
        self.battle_epoch = ''

    def request(self, name, **kwargs):
        self.on_state(self.last_state)
        return {"state": self.last_state}

    def heartbeat(self):
        self.heartbeat_calls += 1
        return self.request("heartbeat")

    def development_observe(self, *, fields=None, timeout=3):
        """Campaign fixtures have no growth work; transport tests override it."""
        if 'development' not in self.last_state:
            samples = Path(__file__).resolve().parents[1] / 'data/acceptance/20261031-campaign/23-growth-capabilities'
            development = dict(schema=1, revision=1, character_id=self.last_state.get('campaign', {}).get('character_id', '12345'),
                gate='PASS', reason='', last_command=None)
            for kind in ('growth', 'equipment', 'spirit'):
                development[kind] = json.loads((samples / (kind + '-snapshot.json')).read_text(encoding='utf-8-sig'))
            development['growth'].update(tasks=[], selected_quest_id=0, ui_selected_quest_id=0,
                window_visible=False, window_enabled=False, available_action='', reason='', step='NONE')
            self.last_state['development'] = development
        self.last_state['development']['revision'] += 1
        self.on_state(self.last_state)
        return {'state': self.last_state}

    def verify(self, cancel=None):
        self.verify_calls += 1
        self.revalidate()
        if self.block_action:
            eventually(lambda: cancel.is_set())
            self.stop()
            return {"command": "stop", "state": self.last_state, "requested_command": "disable-restore", "requested_seq": 1}
        action = {"command_seq": 1, "outcome": "PASS", "mutation_attempted": True,
                  "restore_verified": True, "restore_method_invoked": True, "resource_equality_status": "PASS"}
        self.last_state["action"] = action
        self.on_state(self.last_state)
        return {"state": self.last_state, "requested_command": "disable-restore", "requested_seq": 1,
                "ack": {"status": "COMPLETED", "result": {"action": action}}}

    def stop(self):
        self.stop_calls += 1
        self.terminal = True
        self.last_state.update(lifecycle="stopped", ready=False, component_destroyed=True)
        self.on_state(self.last_state)
        return {"command": "stop", "state": self.last_state}

    def mark_unknown(self, reason):
        self.marked_unknown = True


class ControllerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.reader = FakeReader()
        self.clients = []
        self.window = WindowInfo(200, "将神", 100, "js.exe", 1024, 768)
        def factory(*args, **kwargs):
            client = FakeClient(*args, **kwargs)
            self.clients.append(client)
            return client
        self.factory = Mock(side_effect=factory)
        self.identity_reader = Mock(side_effect=lambda window: ProcessIdentity(window.process_id, window.process_id*10, window.hwnd, 300))
        self.validator = Mock()
        self.controller = ObjectController(Path(self.temp.name), Path(self.temp.name)/"custom-data",
                                           reader=self.reader, identity_reader=self.identity_reader,
                                           identity_validator=self.validator, client_factory=self.factory,
                                           poll_seconds=0.02, close_seconds=0.3)
        self.addCleanup(self.cleanup_controller)

    def cleanup_controller(self):
        self.controller.close()
        eventually(lambda: self.controller.is_shutdown)

    def events(self):
        result = []
        while True:
            try:
                result.append(self.controller.events.get_nowait())
            except queue.Empty:
                return result

    def ready(self):
        self.controller.bind(self.window)
        eventually(lambda: bool(self.reader.calls))
        self.assertTrue(self.controller.connect())
        eventually(lambda: self.controller.can_verify)
        return self.clients[-1]

    def test_binding_only_polls_passive_reader_never_starts_payload(self):
        self.controller.bind(self.window)
        eventually(lambda: bool(self.reader.calls))
        self.factory.assert_not_called()
        players = [event for event in self.events() if event.kind == "player"]
        self.assertTrue(players)
        self.assertEqual(players[-1].data["pid"], 100)
        self.assertEqual(players[-1].data["generation"], self.controller.generation)

    def test_state_failure_invalidates_old_stats_and_verification_gate(self):
        self.ready()
        self.reader.error = Refusal("process changed")
        self.controller.refresh_state()
        eventually(lambda: not self.controller.can_verify)
        eventually(lambda: any(event.kind == "state_error" for event in list(self.controller.events.queue)))

    def test_stopping_keeps_passive_monitor_and_allows_clean_reconnect(self):
        first = self.ready()
        calls = len(self.reader.calls)
        self.assertTrue(self.controller.stop())
        eventually(lambda: not self.controller.is_busy and self.controller.bridge_state == "disconnected")
        eventually(lambda: len(self.reader.calls) > calls)
        self.assertTrue(first.terminal)
        self.assertTrue(self.controller.connect())
        eventually(lambda: self.controller.can_verify)
        self.assertEqual(len(self.clients), 2)

    def test_normalized_action_result_contains_only_current_verified_restoration(self):
        client = self.ready()
        self.events()
        self.assertTrue(self.controller.verify_call())
        eventually(lambda: not self.controller.is_busy)
        completed = [e for e in self.events() if e.kind == "action" and e.data["state"] == "complete"]
        self.assertEqual(len(completed), 1)
        result = completed[0].data["result"]
        self.assertTrue(result["performed"])
        self.assertTrue(result["restored"])
        self.assertEqual(result["resource_equality_status"], "PASS")
        stale = {"requested_command": "disable-restore", "requested_seq": 2, "state": client.last_state}
        normalized = self.controller._action_result(client, stale)
        self.assertFalse(normalized["performed"])
        self.assertFalse(normalized["restored"])

    def test_stop_during_action_requests_one_stop_without_retry(self):
        client = self.ready()
        client.block_action = True
        self.assertTrue(self.controller.verify_call())
        eventually(lambda: client.verify_calls == 1)
        self.assertTrue(self.controller.stop())
        eventually(lambda: not self.controller.is_busy)
        self.assertEqual(client.verify_calls, 1)
        self.assertEqual(client.stop_calls, 1)
        self.assertEqual(self.controller.bridge_state, "disconnected")

    def test_cancel_between_identity_and_factory_return_settles_disconnected(self):
        entered, release = threading.Event(), threading.Event()
        original = self.factory.side_effect
        def blocked(*args, **kwargs):
            entered.set()
            release.wait(1.0)
            return original(*args, **kwargs)
        self.factory.side_effect = blocked
        self.controller.bind(self.window)
        self.assertTrue(self.controller.connect())
        self.assertTrue(entered.wait(1.0))
        self.assertTrue(self.controller.stop())
        release.set()
        eventually(lambda: not self.controller.is_busy)
        self.assertEqual(self.controller.bridge_state, "disconnected")
        self.assertEqual(self.clients[-1].connect_calls, 0)

    def test_binding_switch_suppresses_late_previous_player_and_clears_queued_old_events(self):
        entered, release = threading.Event(), threading.Event()
        original = self.reader.read
        def blocked(pid, expected_creation=None):
            if pid == 100:
                entered.set()
                release.wait(1.0)
            return original(pid, expected_creation)
        self.reader.read = blocked
        self.controller.bind(self.window)
        self.assertTrue(entered.wait(1.0))
        next_window = WindowInfo(201, "另一个将神", 101, "js.exe", 1024, 768)
        self.controller.bind(next_window)
        generation = self.controller.generation
        release.set()
        eventually(lambda: any(call[0] == 101 for call in self.reader.calls))
        events = self.events()
        self.assertFalse(any(e.kind == "player" and e.data["pid"] == 100 for e in events))
        self.assertTrue(all(e.data["generation"] == generation for e in events))

    def test_unbind_detaches_but_controller_can_bind_again(self):
        self.ready()
        self.controller.unbind()
        eventually(lambda: not self.controller.is_busy)
        self.assertFalse(self.controller.can_verify)
        self.controller.bind(self.window)
        eventually(lambda: self.controller._state_valid)
        self.assertTrue(self.controller.connect())
        eventually(lambda: self.controller.can_verify)

    def test_close_is_async_stops_component_and_blocks_new_work(self):
        client = self.ready()
        started = time.monotonic()
        self.controller.close()
        self.assertLess(time.monotonic()-started, 0.1)
        self.assertFalse(self.controller.connect())
        self.assertFalse(self.controller.verify_call())
        eventually(lambda: self.controller.is_shutdown)
        self.assertTrue(client.terminal)
        self.assertFalse(self.controller.can_verify)

    def test_close_pending_operation_with_ledger_error_still_settles_unknown(self):
        client = self.ready()
        release = threading.Event()
        entered = threading.Event()
        original_verify = client.verify
        def pending(cancel=None):
            entered.set()
            release.wait(1.0)
            return original_verify(cancel)
        client.verify = pending
        client.mark_unknown = Mock(side_effect=OSError("disk unavailable"))
        self.controller._close_seconds = 0.03
        self.assertTrue(self.controller.verify_call())
        self.assertTrue(entered.wait(1.0))
        self.controller.close()
        eventually(lambda: self.controller.is_shutdown)
        self.assertEqual(self.controller.bridge_state, "unknown")
        self.assertFalse(self.controller.can_verify)
        client.mark_unknown.assert_called()
        self.assertTrue(any(e.kind == "log" and "写入失败" in e.message for e in self.events()))
        release.set()
        eventually(lambda: not self.controller._operation.is_alive())

    def test_first_request_os_error_immediately_stops_more_bridge_polling(self):
        client = self.ready()
        original = PermissionError("own publication error must stay visible")
        original.winerror = 5
        client.request = Mock(side_effect=original)
        client.heartbeat = Mock(side_effect=original)
        self.events()
        self.controller.refresh_state()
        eventually(lambda: self.controller.bridge_state == "unknown")
        calls = client.request.call_count + client.heartbeat.call_count
        self.assertEqual(calls, 1)
        self.assertFalse(self.controller.can_verify)
        errors = [event for event in self.events() if event.kind in ("state_error", "bridge")]
        self.assertTrue(any(str(original) in event.message for event in errors))
        passive_count = len(self.reader.calls)
        eventually(lambda: len(self.reader.calls) >= passive_count + 2)
        self.assertEqual(client.request.call_count + client.heartbeat.call_count, calls)
        self.assertTrue(client.marked_unknown)

    def campaign_ready(self):
        client = self.ready()
        client.last_state['campaign'] = {'schema': 1, 'revision': 1, 'character_id': '12345',
                                          'available': True, 'phase': 'MAIN', 'gate': 'PASS'}
        client.campaign_observe = Mock(side_effect=lambda **kw: client.request('campaign-observe'))
        client.campaign_command = Mock(side_effect=lambda action, **kw: client.request('campaign-command'))
        return client, self.controller.acquire_campaign('run-1')

    def test_campaign_owner_blocks_manual_actions_rebind_and_second_owner(self):
        client, token = self.campaign_ready()
        generation = self.controller.generation
        self.assertFalse(self.controller.can_verify)
        self.assertFalse(self.controller.verify_call())
        self.assertFalse(self.controller.connect())
        self.controller.bind(WindowInfo(201, 'other', 101, 'js.exe', 1024, 768))
        self.controller.unbind()
        self.assertEqual(self.controller.generation, generation)
        with self.assertRaises(Exception):
            self.controller.acquire_campaign('run-2')
        self.controller.release_campaign(token)
        eventually(lambda: client.terminal)

    def test_campaign_pause_gate_returns_before_admitted_io_and_blocks_next(self):
        client, token = self.campaign_ready()
        entered, release = threading.Event(), threading.Event()
        def blocked(action, **kwargs):
            entered.set()
            release.wait(1)
            return {'state': client.last_state}
        client.campaign_command.side_effect = blocked
        thread = threading.Thread(target=lambda: self.controller.submit_campaign(token, 'enter', expected_revision=1))
        thread.start()
        self.assertTrue(entered.wait(1))
        started = time.monotonic()
        self.assertTrue(self.controller.pause_campaign(token))
        self.assertLess(time.monotonic() - started, .05)
        release.set()
        thread.join(1)
        with self.assertRaises(CampaignAdmissionClosed):
            self.controller.submit_campaign(token, 'enter', expected_revision=1)
        self.assertEqual(client.campaign_command.call_count, 1)
        self.assertTrue(self.controller.resume_campaign(token))
        self.controller.submit_campaign(token, 'enter', expected_revision=1)
        self.assertEqual(client.campaign_command.call_count, 2)
        self.controller.release_campaign(token)

    def test_campaign_subscription_is_independent_bounded_and_identity_checked(self):
        client, token = self.campaign_ready()
        subscription = self.controller.subscribe_campaign(token)
        self.events()
        for revision in range(2, 9):
            client.last_state['campaign']['revision'] = revision
            client.on_state(client.last_state)
        self.assertLessEqual(subscription.qsize(), 4)
        self.assertTrue(any(e.kind == 'bridge' for e in self.events()))
        latest = self.controller.campaign_snapshot(token)
        latest['campaign']['revision'] = -999
        self.assertEqual(self.controller.campaign_snapshot(token)['campaign']['revision'], 8)
        client.last_state['campaign']['character_id'] = '54321'
        with self.assertRaisesRegex(Exception, '身份已改变'):
            self.controller.submit_campaign(token, 'enter', expected_revision=8)
        client.campaign_command.assert_not_called()
        self.controller.release_campaign(token)

    def test_campaign_pause_still_observes_and_heartbeats_without_gui_events_consumption(self):
        client, token = self.campaign_ready()
        self.controller.pause_campaign(token)
        self.controller._last_heartbeat = 0
        eventually(lambda: client.heartbeat_calls >= 1)
        eventually(lambda: client.campaign_observe.call_count >= 2)
        self.assertFalse(self.controller.is_busy)
        self.assertTrue(self.controller.stop())
        self.assertTrue(self.controller.campaign_snapshot(token)['stopping'])
        self.assertFalse(client.terminal)
        self.controller.release_campaign(token)
        eventually(lambda: client.terminal)

    def test_campaign_requires_current_character_identity(self):
        self.ready()
        with self.assertRaisesRegex(Exception, '角色身份'):
            self.controller.acquire_campaign('run-1')

    def test_live_client_connect_refusal_keeps_state_and_exposes_exact_reason(self):
        client = self.ready()
        self.assertFalse(self.controller.connect())
        status = self.controller.connection_status
        self.assertEqual(status['last_refusal'], 'active_client')
        self.assertEqual(status['state'], 'ready')
        self.assertEqual(status['phase'], 'ready')
        self.assertTrue(status['window_present'])
        self.assertTrue(status['client_present'])
        self.assertFalse(status['client_terminal'])
        self.assertTrue(status['started_at'])
        self.assertEqual(client.connect_calls, 1)
        self.assertTrue(any(e.kind == 'bridge' and e.data['connection']['last_refusal'] == 'active_client'
                            for e in self.events()))

    def test_stale_connection_capability_cannot_cancel_replacement_attempt(self):
        self.controller.bind(self.window)
        old = self.controller.begin_campaign_connection()
        self.assertIsNotNone(old)
        eventually(lambda: self.controller.can_verify)
        first = self.clients[-1]
        self.controller.stop()
        eventually(lambda: not self.controller.is_busy and first.terminal)
        current = self.controller.begin_campaign_connection()
        self.assertIsNotNone(current)
        eventually(lambda: self.controller.can_verify)
        second = self.clients[-1]
        generation = self.controller.generation
        self.assertFalse(self.controller.cancel_campaign_connection(old))
        self.assertEqual(self.controller.generation, generation)
        self.assertEqual(second.stop_calls, 0)
        self.assertEqual(self.controller.bridge_state, 'ready')
        self.assertTrue(self.controller.cancel_campaign_connection(current))
        eventually(lambda: second.terminal)
        self.assertEqual(second.stop_calls, 1)
        self.assertFalse(self.controller.cancel_campaign_connection(current))

    def test_business_claim_permanently_retires_startup_cancel_capability(self):
        self.controller.bind(self.window)
        connection = self.controller.begin_campaign_connection()
        eventually(lambda: self.controller.can_verify)
        client = self.clients[-1]
        client.last_state['campaign'] = {'character_id': '12345', 'revision': 1}
        owner = self.controller.acquire_campaign('other-run')
        self.assertFalse(self.controller.cancel_campaign_connection(None))
        self.assertFalse(self.controller.cancel_campaign_connection(connection))
        self.assertFalse(self.controller.campaign_snapshot(owner)['stopping'])
        self.controller.release_campaign(owner, cleanup=False)
        self.assertFalse(self.controller.cancel_campaign_connection(connection))
        self.assertEqual(client.stop_calls, 0)
        self.assertEqual(self.controller.bridge_state, 'ready')

    def clean_stop_during_connect(self, reason, *, cancelled=False, envelope=False):
        original = self.factory.side_effect
        def factory(*args, **kwargs):
            client = original(*args, **kwargs)
            def connect(cancel=None):
                client.connect_calls += 1
                if cancelled:
                    cancel.set()
                client.terminal = True
                client.last_state.update(lifecycle='stopped', ready=False, component_destroyed=True)
                if reason:
                    client.last_state['reason'] = reason
                client.on_state(client.last_state)
                return {'state': client.last_state} if envelope else client.last_state
            client.connect = connect
            return client
        self.factory.side_effect = factory
        self.controller.bind(self.window)
        self.assertTrue(self.controller.connect())
        eventually(lambda: not self.controller.is_busy)
        return self.clients[-1]

    def test_unexpected_clean_connect_stop_preserves_native_reason(self):
        reason = 'campaign_ui_not_unique_CampaignUI'
        client = self.clean_stop_during_connect(reason)
        status = self.controller.connection_status
        self.assertEqual(status['state'], 'disconnected')
        self.assertEqual(status['phase'], 'stopped')
        self.assertEqual(status['error'], reason)
        self.assertEqual(status['stop_reason'], reason)
        self.assertIn(reason, status['message'])
        self.assertTrue(status['client_terminal'])
        self.assertFalse(status['cancelled'])
        self.assertEqual(client.stop_calls, 0)
        events = self.events()
        self.assertTrue(any(e.kind == 'bridge' and e.data['connection']['error'] == reason for e in events))
        self.assertTrue(any(e.kind == 'log' and reason in e.message and e.level == 'WARNING' for e in events))

    def test_cancel_before_bootstrap_remains_normal_without_connection_error(self):
        # The real BridgeClient returns stopped/destroyed without a reason in
        # this branch; its durable ledger records cancelled_before_bootstrap.
        self.clean_stop_during_connect('', cancelled=True)
        status = self.controller.connection_status
        self.assertEqual(status['state'], 'disconnected')
        self.assertEqual(status['error'], '')
        self.assertTrue(status['cancelled'])
        self.assertIn('取消', status['message'])
        self.assertFalse(any(e.kind == 'log' and e.level in ('WARNING', 'ERROR') for e in self.events()))

    def test_explicit_stop_envelope_remains_normal_and_keeps_stop_reason(self):
        self.clean_stop_during_connect('stop', cancelled=True, envelope=True)
        status = self.controller.connection_status
        self.assertEqual(status['error'], '')
        self.assertEqual(status['stop_reason'], 'stop')
        self.assertIn('stop', status['message'])
        self.assertFalse(any(e.kind == 'log' and e.level in ('WARNING', 'ERROR') for e in self.events()))

    def test_late_cancel_does_not_replace_native_failure_with_generic_stop(self):
        reason = 'heartbeat_timeout'
        self.clean_stop_during_connect(reason, cancelled=True)
        status = self.controller.connection_status
        self.assertEqual(status['error'], reason)
        self.assertEqual(status['stop_reason'], reason)
        self.assertIn(reason, status['message'])

    def test_bootstrap_not_started_failure_is_distinct_from_user_cancellation(self):
        self.clean_stop_during_connect('bootstrap_not_started')
        self.assertEqual(self.controller.connection_status['error'], 'bootstrap_not_started')
        self.assertFalse(self.controller.connection_status['cancelled'])


if __name__ == "__main__":
    unittest.main()
