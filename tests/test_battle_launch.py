"""Real Win32 IPC with a detached process running production native fencing.

The fixture runs no game/network. Fake native callbacks reproduce the pinned
298, Campaign ScenarioUI and Trial stFight branches; OS events/mutexes and
BattleLaunch.cs are real. Timing is controlled by explicit pipe barriers.
"""
from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
import time
import unittest
import uuid

from jiangshen_assistant.battle_launch import BattleLaunchAdmission, BattleLaunchError
from jiangshen_assistant.bridge_client import BridgeClient, BridgeAdmissionClosed
from jiangshen_assistant.mono_runtime import ProcessIdentity
from jiangshen_assistant.objects import ObjectController


@unittest.skipUnless(os.name == 'nt' and os.environ.get('BATTLE_LAUNCH_FIXTURE_EXE'),
                     'Run native/unity_bridge/tests/run-battle-launch-race.ps1 on Windows')
class BattleLaunchRaceTests(unittest.TestCase):
    def setUp(self):
        self.nonce = uuid.uuid4().hex
        self.guard = BattleLaunchAdmission(self.nonce)
        self.epoch = self.guard.open_epoch()
        self.operation = uuid.uuid4().hex
        self.client = BridgeClient(Path.cwd(), ProcessIdentity(123, 12345, 1, 1), lambda: None)
        self.client._battle_guard = self.guard
        self.controller = ObjectController.__new__(ObjectController)
        self.controller._lock = threading.RLock()
        self.controller._client = self.client
        self.controller._campaign_owner = self.token = object()
        self.controller._campaign_admission = True
        self.controller._campaign_stopping = False
        self.controller._connection_stop_reason = ''
        self.controller._workflow_error = ''
        self.controller._publish_workflow_locked = lambda: self.trace.append({'line': 'python_control_published'})
        self.controller._emit = lambda *a, **k: None
        self.child = None
        self.lines = queue.Queue()
        self.trace = []

    def tearDown(self):
        if self.child is not None:
            if self.child.poll() is None:
                self.child.stdin.write('exit\n')
                self.child.stdin.flush()
                try:
                    self.child.wait(2)
                except subprocess.TimeoutExpired:
                    self.child.kill()
                    self.child.wait(2)
            self.child.stdin.close()
            self.child.stdout.close()
        try:
            self.guard.close()
        except BattleLaunchError:
            pass  # A tested cancellation failure is never represented as success.
        output = Path(os.environ['BATTLE_LAUNCH_TEST_OUTPUT'])
        with (output / 'ipc-traces.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps({'test': self.id(), 'nonce': self.nonce,
                'pid': self.child.pid if self.child else None, 'events': self.trace}) + '\n')

    def launch(self, kind='formation'):
        self.child = subprocess.Popen([os.environ['BATTLE_LAUNCH_FIXTURE_EXE'],
            self.nonce, self.epoch, kind, self.operation], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1,
            creationflags=subprocess.CREATE_NO_WINDOW)
        def read():
            for line in self.child.stdout:
                self.lines.put(line.rstrip())
        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()
        self.until('READY')

    def until(self, prefix):
        while True:
            line = self.lines.get(timeout=3)
            self.trace.append({'at': time.perf_counter(), 'line': line})
            if line.startswith('FAILED'):
                self.fail(line)
            if line.startswith(prefix):
                return line

    def send(self, text):
        self.child.stdin.write(text + '\n')
        self.child.stdin.flush()

    def state(self, command):
        self.send(command)
        value = json.loads(self.until('STATE ')[6:])
        counts = tuple(map(int, self.until('COUNTS ')[7:].split()))
        return value, counts

    def test_stop_before_pending_298_prevents_attack(self):
        self.launch()
        self.guard.cancel_and_drain()
        value, counts = self.state('formation')
        self.assertEqual(counts[:3], (0, 0, 1))
        self.assertEqual(value['state'], 'CANCELLED_BEFORE_BATTLE')
        self.assertTrue(value['formation_update_attempted'])
        self.assertFalse(value['battle_launch_invoked'])

    def test_stop_inside_298_before_attack_prevents_send(self):
        self.launch('hold298')
        self.send('formation')
        self.until('HOLD298')
        self.guard.cancel_and_drain()
        self.send('release')
        value = json.loads(self.until('STATE ')[6:])
        self.assertEqual(tuple(map(int, self.until('COUNTS ')[7:].split()))[:3], (0, 0, 1))
        self.assertFalse(value['battle_request_observed'])

    def test_stop_drains_start_already_holding_mutex(self):
        self.launch('hold-send')
        self.send('formation')
        self.until('HOLD_SEND')
        done = threading.Event()
        returned = []
        def cancel():
            self.assertTrue(self.controller.pause_campaign(self.token))
            returned.append(time.perf_counter())
            done.set()
        thread = threading.Thread(target=cancel)
        thread.start()
        self.assertFalse(done.wait(.1), 'pause returned while native Send still held launch mutex')
        self.send('release')
        sent = float(self.until('SEND ').split()[2])
        self.until('COUNTS ')
        self.assertTrue(done.wait(2))
        thread.join()
        self.assertGreaterEqual(returned[0], sent)
        self.assertFalse(self.controller._campaign_admission)

    def test_scenario_send_holding_mutex_delays_stop_confirmation(self):
        self.launch('scenario-hold-send')
        self.state('formation')
        self.send('scenario')
        self.until('HOLD_SEND')
        done = threading.Event()
        thread = threading.Thread(target=lambda: (self.controller.stop_campaign(self.token), done.set()))
        thread.start()
        self.assertFalse(done.wait(.1))
        self.send('release')
        self.until('SEND ')
        self.until('COUNTS ')
        self.assertTrue(done.wait(2))
        thread.join()
        self.assertTrue(self.controller._campaign_stopping)

    def test_stamina_send_holding_mutex_delays_stop_confirmation(self):
        self.launch('stamina-drift-hold-send')
        self.state('formation')
        self.send('stamina')
        self.until('HOLD_SEND')
        done = threading.Event()
        thread = threading.Thread(target=lambda: (self.controller.stop_campaign(self.token), done.set()))
        thread.start()
        self.assertFalse(done.wait(.1))
        self.send('release')
        self.until('SEND ')
        self.until('COUNTS ')
        self.assertTrue(done.wait(2))
        thread.join()

    def test_after_enqueue_pause_allows_exact_existing_reply(self):
        self.launch()
        value, counts = self.state('formation')
        self.assertTrue(value['battle_request_observed'])
        self.assertFalse(value['initial_response_applied'])
        self.guard.cancel_and_drain()
        value, counts = self.state('reply')
        self.assertEqual(counts[0], 1)
        self.assertTrue(value['initial_response_applied'])
        value, _ = self.state('transfer')
        self.assertEqual(value['state'], 'REPORT_TRANSFERRED')

    def test_registered_handler_does_not_release_attempt(self):
        self.launch()
        self.state('formation')
        value, counts = self.state('new-attempt')
        self.assertEqual(counts[0], 1)
        self.assertFalse(value['report_transferred'])
        self.assertTrue(any('battle_launch_prior_pending_or_unknown' in x['line'] for x in self.trace))

    def test_missing_initial_handler_stays_unknown_no_retry(self):
        self.launch()
        self.state('formation')
        value, _ = self.state('lose-handler')
        self.assertEqual(value['state'], 'UNKNOWN')
        _, counts = self.state('new-attempt')
        self.assertEqual(counts[0], 1)

    def test_fresh_report_requires_exact_operation_handoff(self):
        self.launch()
        self.state('formation')
        self.state('reply')
        value, counts = self.state('transfer-wrong-operation')
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertFalse(value['report_transferred'])
        self.assertEqual(counts[0], 1)

    def test_scenario_cancel_and_new_epoch_never_reopen_old_callback(self):
        self.launch('scenario')
        _, counts = self.state('formation')
        self.assertEqual(counts[0], 0)
        self.guard.cancel_and_drain()
        next_epoch = self.guard.open_epoch()
        self.assertNotEqual(next_epoch, self.epoch)
        value, counts = self.state('scenario')
        self.assertEqual(counts[0], 0)
        self.assertFalse(value['battle_request_observed'])
        value, counts = self.state('scenario')
        self.assertEqual(counts[0], 0)
        self.assertEqual(value['state'], 'UNKNOWN')

    def test_scenario_native_continuation_and_report_transfer(self):
        self.launch('scenario')
        self.state('formation')
        _, counts = self.state('scenario')
        self.assertEqual(counts[0], 1)
        self.state('reply')
        value, counts = self.state('transfer')
        self.assertEqual(value['state'], 'REPORT_TRANSFERRED')
        self.assertEqual(counts[3], 1)

    def test_trial_stamina_drop_is_unknown_and_never_registers_retry(self):
        self.launch('stamina')
        self.state('drop-stamina')
        value, counts = self.state('formation')
        self.assertEqual(counts[:2], (0, 0))
        self.assertEqual(value['state'], 'UNKNOWN')

    def test_stamina_delayed_callback_cancelled_after_native_deferral(self):
        self.launch('stamina-drift')
        _, counts = self.state('formation')
        self.assertEqual(counts[0], 0)
        self.guard.cancel_and_drain()
        self.guard.open_epoch()
        value, counts = self.state('stamina')
        self.assertEqual(counts[:2], (0, 1))
        self.assertFalse(value['battle_request_observed'])

    def test_duplicate_298_forwards_native_once(self):
        self.launch()
        value, counts = self.state('duplicate298')
        self.assertEqual(counts[:3], (1, 1, 1))
        self.assertEqual(value['state'], 'UNKNOWN')

    def test_duplicate_initial_response_forwards_native_once(self):
        self.launch()
        self.state('formation')
        value, counts = self.state('duplicate-reply')
        self.assertEqual(counts[3], 1)
        self.assertEqual(value['state'], 'UNKNOWN')

    def test_changed_role_drops_298_without_native_mutation(self):
        self.launch()
        self.state('change-role')
        value, counts = self.state('formation')
        self.assertEqual(counts[:3], (0, 0, 0))
        self.assertEqual(value['state'], 'UNKNOWN')

    def test_changed_target_drops_initial_response_without_native_mutation(self):
        self.launch()
        self.state('formation')
        self.state('change-target')
        value, counts = self.state('reply')
        self.assertEqual(counts[3], 0)
        self.assertEqual(value['state'], 'UNKNOWN')

    def test_direct_settled_route_keeps_native_report_handoff(self):
        self.launch('direct')
        self.guard.cancel_and_drain()
        value, counts = self.state('reply')
        self.assertTrue(value['initial_response_applied'])
        value, counts = self.state('transfer')
        self.assertEqual(value['state'], 'REPORT_TRANSFERRED')
        self.assertEqual(counts[0], 1)

    def test_abandoned_native_mutex_cannot_acknowledge_pause(self):
        child = subprocess.run([os.environ['BATTLE_LAUNCH_FIXTURE_EXE'], self.nonce,
            self.epoch, 'abandon', self.operation], text=True, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, timeout=3, creationflags=subprocess.CREATE_NO_WINDOW)
        self.trace.append({'line': child.stdout, 'pid': child.pid if hasattr(child, 'pid') else None})
        self.assertEqual(child.returncode, 0)
        self.assertIn('ABANDONED', child.stdout)
        with self.assertRaisesRegex(BattleLaunchError, 'abandoned'):
            self.guard.cancel_and_drain()
        with self.assertRaises(BattleLaunchError):
            self.guard.open_epoch()

    def test_cancel_event_error_cannot_acknowledge_controller_stop(self):
        handle = self.guard._events[self.epoch]
        self.guard._kernel.CloseHandle(handle)
        self.assertFalse(self.controller.stop_campaign(self.token))
        self.assertEqual(self.controller._campaign_control_reason, 'battle_launch_cancel_unverified')
        self.assertFalse(self.controller._campaign_admission)
        with self.assertRaises(BattleLaunchError):
            self.guard.open_epoch()

    def test_missing_guard_api_cannot_acknowledge_controller_pause(self):
        self.controller._client = object()
        self.assertFalse(self.controller.pause_campaign(self.token))
        self.assertFalse(self.controller._campaign_admission)

    def test_expired_native_lease_does_not_send(self):
        self.launch()
        self.state('expire')
        value, counts = self.state('formation')
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertEqual(counts[0], 0)

    def test_batch_370_success_requires_exact_native_progress_and_no_report(self):
        self.launch('batch')
        value, counts = self.state('reply')
        self.assertEqual(value['state'], 'NATIVE_NONREPORT_CONFIRMED')
        self.assertTrue(value['nonreport_callback_returned'])
        self.assertFalse(value['initial_response_applied'])
        self.assertFalse(value['report_transferred'])
        self.assertFalse(value['unresolved'])
        self.assertEqual(counts[0], 1)

    def test_batch_370_rejection_is_unknown_and_never_retried(self):
        self.launch('batch-reject')
        value, counts = self.state('reply')
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertTrue(value['unresolved'])
        _, counts = self.state('new-attempt')
        self.assertEqual(counts[0], 1)

    def test_batch_370_nonzero_result_preserves_stock_success_semantics(self):
        self.launch('batch-nonzero-result')
        value, counts = self.state('reply')
        self.assertEqual(value['state'], 'NATIVE_NONREPORT_CONFIRMED')
        self.assertEqual(value['nonreport_result'], 7)
        self.assertFalse(value['report_transferred'])
        self.assertEqual(counts[0], 1)

    def test_batch_370_timeout_is_unknown_and_never_retried(self):
        self.launch('batch')
        value, counts = self.state('timeout')
        self.assertEqual(value['state'], 'UNKNOWN')
        _, counts = self.state('new-attempt')
        self.assertEqual(counts[0], 1)

    def test_story_attestation_requires_exact_owned_callback_and_data(self):
        self.launch('scenario')
        self.state('formation')
        self.state('attest')
        self.assertEqual([x['line'] for x in self.trace if x['line'].startswith('ATTEST')][-1], 'ATTEST True')
        self.state('change-scenario-data')
        self.state('attest')
        self.assertEqual([x['line'] for x in self.trace if x['line'].startswith('ATTEST')][-1], 'ATTEST False')

    def test_story_attestation_rejects_replaced_callback(self):
        self.launch('scenario')
        self.state('formation')
        self.state('replace-scenario-callback')
        self.state('attest')
        self.assertIn('ATTEST False', [x['line'] for x in self.trace])

    def test_story_attestation_never_reopens_cancelled_epoch(self):
        self.launch('scenario')
        self.state('formation')
        self.guard.cancel_and_drain()
        self.guard.open_epoch()
        self.state('attest')
        self.assertIn('ATTEST False', [x['line'] for x in self.trace])

    def _assert_blocked_response(self, kind, variant):
        self.launch(kind)
        self.state('formation')
        value, counts = self.state('reply-' + variant)
        self.assertEqual(counts[3], 0, 'wrong/invalid response reached the native callback')
        self.assertEqual(value['state'], 'UNKNOWN')
        mutations = [item['line'] for item in self.trace if item['line'].startswith('MUTATIONS ')][-1]
        self.assertEqual(mutations, 'MUTATIONS 0 0 0 0')
        _, counts = self.state('new-attempt')
        self.assertEqual(counts[0], 1, 'ambiguous response admitted a duplicate battle')

    def test_campaign_request_a_valid_response_b_is_blocked_before_stock(self):
        self._assert_blocked_response('campaign', 'wrong-target')

    def test_own_building_request_a_valid_response_b_is_blocked_before_stock(self):
        self._assert_blocked_response('own-building', 'wrong-target')

    def test_friend_building_request_a_valid_response_b_is_blocked_before_stock(self):
        self._assert_blocked_response('friend-building', 'wrong-target')

    def test_campaign_wrong_campaign_is_blocked_before_stock(self):
        self._assert_blocked_response('campaign', 'wrong-campaign')

    def test_friend_building_wrong_friend_is_blocked_before_stock(self):
        self._assert_blocked_response('friend-building', 'wrong-friend')

    def _assert_matching_response_once(self, kind):
        self.launch(kind)
        self.state('formation')
        value, counts = self.state('reply')
        self.assertEqual(counts[3], 1)
        self.assertTrue(value['initial_response_applied'])
        self.assertIn('STREAM same 0', '\n'.join(item['line'] for item in self.trace))
        value, _ = self.state('transfer')
        self.assertEqual(value['state'], 'REPORT_TRANSFERRED')

    def test_matching_campaign_response_preserves_original_stream_once(self):
        self._assert_matching_response_once('campaign')

    def test_matching_own_building_response_preserves_original_stream_once(self):
        self._assert_matching_response_once('own-building')

    def test_matching_friend_building_response_preserves_original_stream_once(self):
        self._assert_matching_response_once('friend-building')

    def test_truncated_response_never_reaches_stock(self):
        self._assert_blocked_response('own-building', 'truncated')

    def test_empty_response_never_reaches_stock(self):
        self._assert_blocked_response('own-building', 'empty')

    def test_duplicate_target_field_never_reaches_stock(self):
        self._assert_blocked_response('own-building', 'duplicate-target')

    def test_wrong_wire_type_never_reaches_stock(self):
        self._assert_blocked_response('own-building', 'wrong-wire')

    def test_unknown_schema_field_never_reaches_stock(self):
        self._assert_blocked_response('own-building', 'unknown-field')

    def test_overflow_response_never_reaches_stock(self):
        self._assert_blocked_response('own-building', 'overflow')

    def test_nonzero_stream_position_never_reaches_stock(self):
        self._assert_blocked_response('own-building', 'position')

    def test_null_timeout_never_reaches_stock(self):
        self.launch('own-building')
        self.state('formation')
        value, counts = self.state('timeout')
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertEqual(counts[3], 0)

    def test_stopped_late_wrong_target_response_never_reaches_stock(self):
        self.launch('own-building')
        self.state('formation')
        self.state('stop')
        value, counts = self.state('reply-wrong-target')
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertEqual(counts[3], 0)

    def test_stopped_late_matching_response_only_applies_the_sent_battle(self):
        self.launch('own-building')
        self.state('formation')
        self.state('stop')
        value, counts = self.state('reply')
        self.assertEqual(counts[0], 1)
        self.assertEqual(counts[3], 1)
        self.assertTrue(value['initial_response_applied'])
        value, counts = self.state('new-attempt')
        self.assertEqual(counts[0], 1)

    def test_optional_trial_target_absence_is_opcode_only(self):
        self.launch('stamina')
        self.state('formation')
        value, counts = self.state('reply-without-target')
        self.assertEqual(counts[3], 1)
        self.assertEqual(value['response_binding'], 'opcode-only')

    def test_optional_grave_target_absence_is_opcode_only(self):
        self.launch()
        self.state('formation')
        value, counts = self.state('reply-without-target')
        self.assertEqual(counts[3], 1)
        self.assertEqual(value['response_binding'], 'opcode-only')

    def test_echoed_trial_target_mismatch_is_blocked(self):
        self._assert_blocked_response('stamina', 'wrong-target')

    def test_echoed_grave_target_mismatch_is_blocked(self):
        self._assert_blocked_response('formation', 'wrong-target')

    def test_invalid_compressed_report_is_blocked_before_any_native_mutation(self):
        self._assert_blocked_response('own-building', 'bad-report')

    def test_report_status_mismatch_is_blocked_before_stock(self):
        self._assert_blocked_response('own-building', 'report-mismatch')

    def test_report_missing_required_fields_is_blocked_before_stock(self):
        self._assert_blocked_response('own-building', 'report-missing-required')

    def test_report_duplicate_status_is_blocked_before_stock(self):
        self._assert_blocked_response('own-building', 'report-duplicate-status')

    def test_inflated_report_size_is_bounded_before_stock(self):
        self._assert_blocked_response('own-building', 'report-bomb')

    def test_friend_target_echo_without_owner_echo_is_explicit(self):
        self.launch('friend-building')
        self.state('formation')
        value, counts = self.state('reply-without-friend')
        self.assertEqual(counts[3], 1)
        self.assertEqual(value['response_binding'], 'target-echo;friend-opcode-only')

    def test_boss_without_target_echo_is_opcode_only(self):
        self.launch('boss')
        self.state('formation')
        value, counts = self.state('reply-without-target')
        self.assertEqual(counts[3], 1)
        self.assertEqual(value['response_binding'], 'opcode-only')

    def test_boss_unmapped_optional_echo_does_not_guess_boss_id(self):
        self._assert_blocked_response('boss', '')

    def _assert_native_rejection_once(self, kind, variant='reject'):
        self.launch(kind)
        self.state('formation')
        value, counts = self.state('reply-' + variant)
        self.assertEqual(counts[3], 1)
        self.assertTrue(value['response_validated'])
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertFalse(value['initial_response_applied'])
        self.assertFalse(value['report_transferred'])
        _, counts = self.state('new-attempt')
        self.assertEqual(counts[0], 1)

    def test_campaign_tester_rejection_uses_exact_native_no_battle_branch_once(self):
        self._assert_native_rejection_once('campaign')

    def test_own_monster_killed_rejection_uses_exact_native_callback_once(self):
        self._assert_native_rejection_once('own-building')

    def test_friend_missing_monster_rejection_uses_exact_native_callback_once(self):
        self._assert_native_rejection_once('friend-building')

    def test_friend_killed_monster_rejection_uses_exact_native_callback_once(self):
        self._assert_native_rejection_once('friend-building', 'reject-two')

    def test_missing_required_result_is_not_defaulted_before_stock(self):
        self._assert_blocked_response('own-building', 'missing-required')

    def test_friend_missing_target_echo_is_not_defaulted_to_another_key(self):
        self._assert_blocked_response('friend-building', 'without-target')

    def test_packet_size_is_bounded_before_stock(self):
        self._assert_blocked_response('own-building', 'packet-oversize')

    def test_changed_friend_context_blocks_late_response_before_stock(self):
        self.launch('friend-building')
        self.state('formation')
        self.state('change-friend')
        value, counts = self.state('reply')
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertEqual(counts[3], 0)

    def test_training_required_target_echo_matches_real_request(self):
        self._assert_matching_response_once('training')

    def test_training_wrong_required_target_echo_is_blocked(self):
        self._assert_blocked_response('training', 'wrong-target')

    def test_changed_native_source_root_blocks_late_response_before_stock(self):
        self.launch('own-building')
        self.state('formation')
        self.state('change-source-root')
        value, counts = self.state('reply')
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertEqual(counts[3], 0)

    def _assert_embedded_blocked(self, variant):
        self.launch('campaign')
        self.state('formation')
        value, counts = self.state('reply-' + variant)
        # A native end can run before the shared helper's first presentation.
        # The negative control must observe its side effect before asserting.
        self.state('native-end')
        self.assertEqual(counts[3], 0, 'invalid embedded settlement reached stock/cache')
        self.assertEqual(value['state'], 'UNKNOWN')
        self.assertEqual([x['line'] for x in self.trace if x['line'].startswith('MUTATIONS ')][-1], 'MUTATIONS 0 0 0 0')
        self.assertEqual([x['line'] for x in self.trace if x['line'].startswith('EMBEDDED ')][-1], 'EMBEDDED none')

    def test_embedded_inner_target_b_cannot_be_cached_or_applied_for_a(self):
        self._assert_embedded_blocked('embedded-wrong-target')

    def test_embedded_wrong_campaign_is_blocked_before_stock(self):
        self._assert_embedded_blocked('embedded-wrong-campaign')

    def test_embedded_wrong_type_is_blocked_before_stock(self):
        self._assert_embedded_blocked('embedded-wrong-type')

    def test_embedded_missing_target_is_not_fabricated_from_outer(self):
        self._assert_embedded_blocked('embedded-missing-target')

    def test_embedded_missing_authoritative_report_id_is_unknown(self):
        self._assert_embedded_blocked('embedded-missing-report-id')

    def test_embedded_outer_report_id_conflict_is_blocked(self):
        self._assert_embedded_blocked('embedded-outer-report-mismatch')

    def test_embedded_wrong_win_is_blocked_before_stock(self):
        self._assert_embedded_blocked('embedded-wrong-win')

    def test_embedded_rejection_is_not_cached_as_authoritative_settlement(self):
        self._assert_embedded_blocked('embedded-rejection')

    def test_matching_embedded_with_default_type_and_no_outer_report_id_is_allowed(self):
        self.launch('campaign')
        self.state('formation')
        value, counts = self.state('reply-embedded-matching')
        self.assertEqual(counts[3], 1)
        self.assertTrue(value['response_embedded_verified'])
        self.state('native-end')
        self.assertEqual([x['line'] for x in self.trace if x['line'].startswith('MUTATIONS ')][-1], 'MUTATIONS 2 0 7 1')

    def test_matching_embedded_and_outer_report_id_are_allowed(self):
        self.launch('campaign')
        self.state('formation')
        value, counts = self.state('reply-embedded-matching-outer')
        self.assertEqual(counts[3], 1)
        self.assertTrue(value['response_embedded_verified'])
        value, _ = self.state('transfer')
        self.assertEqual(value['state'], 'REPORT_TRANSFERRED')


@unittest.skipUnless(os.name == 'nt', 'Windows named admission boundary')
class BridgeLaunchTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.client = BridgeClient(Path(self.temp.name), ProcessIdentity(123, 456, 1, 1), lambda: None,
                                   status_reader=lambda *_: 'same')

    def tearDown(self):
        self.client._close_battle_guard()
        self.temp.cleanup()

    def test_typed_campaign_captures_epoch_before_serialized_request(self):
        old = self.client.open_battle_admission()
        self.client.session_dir.mkdir(parents=True)
        self.client._write_ledger = lambda *a, **k: None
        published = []
        def request(name, **kwargs):
            self.client.cancel_battle_admission()
            published.append(self.client.open_battle_admission())
            self.client._send(name, campaign_fields=kwargs['_campaign_fields'], battle_epoch=kwargs['_battle_epoch'])
            return {}
        self.client._request = request
        self.client.campaign_command('start', expected_revision=1, campaign_id=1, troop_id=1, attempt_id=uuid.uuid4().hex)
        wire = (self.client.session_dir / 'command.txt').read_text()
        self.assertIn('battle_epoch=' + old + '\n', wire)
        self.assertNotEqual(published[0], old)

    def test_cancel_does_not_wait_for_bridge_request_lock(self):
        self.client.open_battle_admission()
        self.client._request_lock.acquire()
        done = threading.Event()
        thread = threading.Thread(target=lambda: (self.client.cancel_battle_admission(), done.set()), daemon=True)
        try:
            thread.start()
            self.assertTrue(done.wait(1))
        finally:
            self.client._request_lock.release()
            thread.join(1)

    def test_explicit_resume_creates_epoch_and_closed_admission_never_self_reopens(self):
        old = self.client.capture_battle_epoch()
        self.client.cancel_battle_admission()
        with self.assertRaises(BridgeAdmissionClosed):
            self.client.capture_battle_epoch()
        self.assertNotEqual(self.client.open_battle_admission(), old)

    def test_process_exit_releases_own_handles_and_terminal_stop_is_idempotent(self):
        self.client.open_battle_admission()
        self.client._status_reader = lambda *_: 'gone'
        self.assertTrue(self.client.target_exited())
        self.assertIsNone(self.client._battle_guard._mutex)
        self.assertEqual(self.client.stop()['command'], 'stop')


if __name__ == '__main__':
    unittest.main()
