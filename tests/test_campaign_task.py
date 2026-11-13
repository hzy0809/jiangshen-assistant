from pathlib import Path
import copy
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from jiangshen_assistant.campaign import Campaign, GuideKind, Observation
from jiangshen_assistant.campaign_store import CampaignStore
from jiangshen_assistant.campaign_task import CampaignCoordinator
from jiangshen_assistant.core import TaskRunner
from jiangshen_assistant.objects import CampaignAdmissionClosed, ObjectController
from jiangshen_assistant.mono_runtime import ProcessIdentity
from jiangshen_assistant.windows import WindowInfo
from tests.test_objects import FakeClient, FakeReader, eventually


class Token:
    context_key = 'pid:1/creation:2/nonce:3/generation:4/character:5'


class FakeObjects:
    def __init__(self, outcomes=()):
        self.bridge_state = 'ready'
        self.is_busy = False
        self.owner = None
        self.admission = True
        self.stopping = False
        self.commands = []
        self.outcomes = list(outcomes)
        self.auto_settle = True
        self.started = threading.Event()
        self.command_release = None
        self.rejected = False
        self.snap = {'schema': 1, 'revision': 1, 'available': True, 'gate': 'PASS',
                     'phase': 'MAIN', 'campaign_id': 10, 'troop_id': 20,
                     'eligible_troops': [{'troop_id': 20, 'canattack': True, 'visible': True, 'killed': False}],
                     'tutorial': {'kind': 'NONE', 'step': '', 'gate': ''}, 'attempt': {'status': 'NONE'}}
        self.released = False
        self.observes = 0

    def acquire_campaign(self, run_id):
        assert self.owner is None
        self.admission = True
        self.stopping = False
        self.owner = Token()
        return self.owner

    def connect(self):
        self.bridge_state = 'ready'
        return True

    def begin_campaign_connection(self):
        self.connect()
        self.connection_token = object()
        return self.connection_token

    def cancel_campaign_connection(self, token):
        if token is not self.connection_token or self.owner is not None:
            return False
        self.bridge_state = 'disconnected'
        return True

    def observe_campaign(self, token):
        assert token is self.owner
        self.observes += 1
        a = self.snap['attempt']
        if a['status'] == 'WAITING' and self.auto_settle and self.outcomes:
            a.update(status='SETTLED', report_id=str(100 + len(self.commands)),
                     outcome=self.outcomes.pop(0), evidence='captured_authoritative_receipt_applied')
            self.snap.update(phase='RESULT', result_kind='NORMAL', revision=self.snap['revision'] + 1)
        return {'context_key': token.context_key, 'campaign': copy.deepcopy(self.snap), 'stopping': self.stopping}

    def submit_campaign(self, token, action, **kwargs):
        if not self.admission:
            raise CampaignAdmissionClosed('paused before submit')
        self.commands.append((action, kwargs))
        if action == 'start':
            self.started.set()
            if self.command_release:
                self.command_release.wait(1)
        if self.rejected and action == 'start':
            self.snap['revision'] += 1
            self.snap.update(gate='NO_RESOURCE')
            return {'requested_seq': len(self.commands), 'ack': {'command_seq': len(self.commands), 'status': 'REJECTED', 'result': {'campaign_command':
                {'command_seq': len(self.commands), 'action': action, 'attempt_id': kwargs['attempt_id'], 'invoked': False}}}}
        phase = {'enter': 'CAMPAIGN', 'select': 'TROOP', 'prepare': 'FORMATION',
                 'start': 'BATTLE', 'ack_result': 'CAMPAIGN', 'next_chapter': 'CAMPAIGN', 'continue': 'MAIN'}[action]
        self.snap.update(phase=phase, revision=self.snap['revision'] + 1)
        if action == 'start':
            self.snap['attempt'] = {'status': 'WAITING', 'attempt_id': kwargs['attempt_id'],
                'command_seq': len(self.commands), 'campaign_id': 10, 'troop_id': 20,
                'report_id': '', 'outcome': 'UNKNOWN', 'evidence': ''}
        if action == 'continue':
            self.snap['tutorial'] = {'kind': 'NONE', 'step': '', 'gate': ''}
        return {'requested_seq': len(self.commands), 'ack': {'command_seq': len(self.commands), 'status': 'COMPLETED', 'result': {'campaign_command':
            {'command_seq': len(self.commands), 'action': action, 'attempt_id': kwargs['attempt_id'], 'invoked': True}}}}

    def pause_campaign(self, token):
        self.admission = False
        return True

    def resume_campaign(self, token):
        self.admission = True
        return True

    def stop_campaign(self, token):
        self.admission = False
        self.stopping = True
        return True

    def release_campaign(self, token, cleanup=True):
        self.released = True
        self.owner = None
        self.bridge_state = 'disconnected'


class CoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.runner = TaskRunner()
        self.objects = FakeObjects()
        self.coordinator = None

    def make(self, **kwargs):
        self.coordinator = CampaignCoordinator(self.objects, self.runner, Path(self.temp.name),
            poll_seconds=.005, stop_timeout=.05, battle_timeout=.2, **kwargs)
        self.addCleanup(self.finish)
        return self.coordinator

    def finish(self):
        if self.coordinator:
            self.coordinator.close()
            self.runner.join(1)

    def settled(self):
        eventually(lambda: not self.coordinator.is_active)
        self.runner.join(.2)

    def test_seven_results_stop_at_three_consecutive_losses_and_old_win_cannot_reset(self):
        self.objects.outcomes = ['LOSS', 'WIN', 'LOSS', 'WIN', 'LOSS', 'LOSS', 'LOSS']
        original = self.objects.observe_campaign
        captured = {}
        def hold_after_fifth_result(token):
            observed = original(token)
            attempt = observed['campaign']['attempt']
            if attempt['status'] == 'SETTLED':
                captured.setdefault(attempt['attempt_id'], copy.deepcopy(attempt))
                if len(captured) == 5:
                    self.objects.auto_settle = False
            return observed
        self.objects.observe_campaign = hold_after_fifth_result
        c = self.make()
        self.assertTrue(c.start())
        eventually(lambda: len(c.snapshot.get('consumed', [])) == 5 and
                   sum(a == 'start' for a, _ in self.objects.commands) == 6)
        self.assertEqual(c.snapshot['losses'], 1)
        self.assertEqual(c.snapshot['mode'], 'active')
        sixth = copy.deepcopy(self.objects.snap['attempt'])
        # A duplicate of the fourth result (WIN) cannot erase the fifth LOSS
        # while the sixth challenge is already waiting for its own result.
        self.objects.snap['attempt'] = list(captured.values())[3]
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertEqual(c.snapshot['losses'], 1)
        self.assertEqual(len(c.snapshot['consumed']), 5)
        self.assertEqual(c.snapshot['pending']['attempt_id'], sixth['attempt_id'])
        self.objects.snap['attempt'] = sixth
        self.objects.auto_settle = True
        self.settled()
        self.assertEqual(c.snapshot['losses'], 3)
        self.assertEqual(c.snapshot['mode'], 'loss_limit')
        self.assertEqual(c.snapshot['reason'], 'three_consecutive_losses')
        self.assertEqual(sum(a == 'start' for a, _ in self.objects.commands), 7)
        state = CampaignStore(c.snapshot['ledger_path']).load()
        self.assertEqual(state.losses, 3)
        self.assertEqual(len(state.consumed), 7)
        self.assertTrue(self.objects.released)

    def test_campaign_progress_does_not_invoke_pending_or_unsupported_growth(self):
        # An available growth port could report a pending or unsupported task;
        # campaign must not consult it as a prerequisite for its own progress.
        self.objects.observe_development = Mock(side_effect=AssertionError('growth must stay independent'))
        c = self.make()
        self.assertTrue(c.start())
        self.assertTrue(self.objects.started.wait(1))
        self.assertEqual([action for action, _ in self.objects.commands],
                         ['enter', 'select', 'prepare', 'start'])
        self.objects.observe_development.assert_not_called()
        self.assertFalse(c.is_paused)
        self.assertEqual(c.snapshot['losses'], 0)
        c.stop()
        self.settled()

    def test_pause_does_not_block_settlement_and_resume_requires_explicit_action(self):
        self.objects.outcomes = ['LOSS']
        self.objects.auto_settle = False
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        c.pause()
        self.objects.auto_settle = True
        eventually(lambda: c.snapshot['losses'] == 1)
        self.assertTrue(c.is_paused)
        self.assertEqual(sum(a == 'start' for a, _ in self.objects.commands), 1)
        count = self.objects.observes
        eventually(lambda: self.objects.observes > count + 2)
        c.stop()
        self.settled()
        self.assertEqual(c.snapshot['losses'], 1)

    def test_stop_waits_current_result_and_never_submits_next(self):
        self.objects.outcomes = ['WIN']
        self.objects.auto_settle = False
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        c.stop()
        self.objects.auto_settle = True
        self.settled()
        self.assertIsNone(c.snapshot['pending'])
        self.assertEqual(len(c.snapshot['consumed']), 1)
        self.assertEqual(sum(a == 'start' for a, _ in self.objects.commands), 1)

    def test_close_with_missing_result_is_bounded_and_restart_does_not_attack(self):
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        started = time.monotonic()
        c.close()
        self.assertLess(time.monotonic() - started, .05)
        eventually(lambda: c.is_shutdown)
        self.assertIsNotNone(c.snapshot['pending'])
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertIn('pending_retained', c.snapshot['reason'])
        fresh = CampaignCoordinator(FakeObjects(), TaskRunner(), Path(self.temp.name))
        self.assertFalse(fresh.start())
        self.assertTrue(fresh.is_paused)
        self.assertIsNotNone(fresh.snapshot['pending'])

    def test_unknown_result_is_not_a_loss_or_retry(self):
        self.objects.auto_settle = False
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        self.objects.snap['attempt']['status'] = 'UNKNOWN'
        self.settled()
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(c.snapshot['mode'], 'blocked')
        self.assertEqual(sum(a == 'start' for a, _ in self.objects.commands), 1)

    def test_corrupt_or_old_context_settlement_is_not_counted(self):
        self.objects.auto_settle = False
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        self.objects.snap['attempt'].update(status='SETTLED', outcome='LOSS', report_id='123',
            campaign_id=999, evidence='captured_authoritative_receipt_applied')
        self.settled()
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(c.snapshot['reason'], 'invalid_settlement_evidence')

    def test_unknown_tutorial_pauses_without_any_commands(self):
        self.objects.snap['tutorial'] = {'kind': 'UNKNOWN', 'step': 'new', 'gate': 'BLOCKED'}
        c = self.make()
        c.start()
        eventually(lambda: c.is_paused)
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(self.objects.commands, [])

    def test_reviewed_scenario_progresses_and_hint_is_not_blocked(self):
        self.objects.snap.update(phase='SCENARIO')
        self.objects.snap['tutorial'] = {'kind': 'SCENARIO', 'step': '10:1', 'gate': 'PASS'}
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        c.stop()
        self.settled()
        self.assertEqual(self.objects.commands[0][0], 'continue')
        self.assertEqual(c.snapshot['completed_guides'], ['10:1'])

    def test_pause_during_admitted_start_returns_promptly_and_keeps_exact_attempt(self):
        release = threading.Event()
        self.objects.command_release = release
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        state = CampaignStore(c.snapshot['ledger_path']).load()
        self.assertEqual(state.pending.intent_id, self.objects.commands[-1][1]['attempt_id'])
        started = time.monotonic()
        c.pause()
        self.assertLess(time.monotonic() - started, .05)
        release.set()
        c.stop()
        self.settled()
        self.assertEqual(sum(a == 'start' for a, _ in self.objects.commands), 1)

    def test_rejected_start_clears_pending_without_loss(self):
        self.objects.rejected = True
        c = self.make()
        c.start()
        eventually(lambda: c.is_paused or not c.is_active)
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertIsNone(c.snapshot['pending'])

    def test_skip_rejection_pauses_same_revision_until_explicit_resume_then_once(self):
        self.objects.snap.update(phase='SCENARIO', revision=7)
        self.objects.snap['tutorial'] = {'kind': 'SCENARIO', 'step': '10:1', 'gate': 'PASS'}
        c = self.make()
        intent_ids = []
        def skip(token, action, **kwargs):
            self.assertEqual(action, 'continue')
            self.objects.commands.append((action, kwargs))
            intent_ids.append(c.snapshot['pending']['intent_id'])
            seq = len(self.objects.commands)
            rejected = seq == 1
            return {'requested_seq': seq, 'ack': {'command_seq': seq,
                'status': 'REJECTED' if rejected else 'COMPLETED', 'result': {'campaign_command':
                {'command_seq': seq, 'action': action, 'attempt_id': '', 'invoked': not rejected,
                 'reason': 'scenario_skip_ongui_unavailable' if rejected else ''}}}}
        self.objects.submit_campaign = skip
        c.start()
        eventually(lambda: c.is_paused and c.snapshot['pending'] is None)
        self.assertEqual(c.snapshot['reason'], 'scenario_skip_ongui_unavailable')
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(self.objects.snap['revision'], 7)
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertEqual(len(self.objects.commands), 1)
        self.assertTrue(c.resume())
        eventually(lambda: len(self.objects.commands) == 2)
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertEqual(len(self.objects.commands), 2)
        self.assertNotEqual(intent_ids[0], intent_ids[1])
        self.assertIsNotNone(c.snapshot['pending'])
        self.assertEqual(self.objects.snap['revision'], 7)
        self.objects.snap.update(phase='CAMPAIGN', gate='NO_RESOURCE', revision=8)
        self.objects.snap['tutorial'] = {'kind': 'NONE', 'step': '', 'gate': ''}
        eventually(lambda: c.snapshot['completed_guides'] == ['10:1'] and c.snapshot['pending'] is None)
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(len(self.objects.commands), 2)

    def rejected_skip_fixture(self, *, block_first=False):
        self.objects.snap.update(phase='SCENARIO', revision=7)
        self.objects.snap['tutorial'] = {'kind': 'SCENARIO', 'step': '10:1', 'gate': 'PASS'}
        entered, release = threading.Event(), threading.Event()
        if not block_first:
            release.set()
        c = self.make()
        self.addCleanup(release.set)
        def skip(token, action, **kwargs):
            self.objects.commands.append((action, kwargs))
            seq = len(self.objects.commands)
            if seq == 1:
                entered.set()
                release.wait(1)
            return {'requested_seq': seq, 'ack': {'command_seq': seq,
                'status': 'REJECTED' if seq == 1 else 'COMPLETED', 'result': {'campaign_command':
                {'command_seq': seq, 'action': action, 'attempt_id': '', 'invoked': seq != 1,
                 'reason': 'scenario_skip_ongui_unavailable' if seq == 1 else ''}}}}
        self.objects.submit_campaign = skip
        c.start()
        if not block_first:
            eventually(lambda: c.is_paused and c.snapshot['pending'] is None)
        return c, entered, release

    def test_new_rejection_invalidates_resume_queued_before_rejected_ack(self):
        c, entered, release = self.rejected_skip_fixture(block_first=True)
        self.assertTrue(entered.wait(1))
        self.assertTrue(c.pause())
        self.assertTrue(c.resume())
        release.set()
        eventually(lambda: c.is_paused and c.snapshot['pending'] is None)
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertEqual(len(self.objects.commands), 1)
        self.assertEqual(c.snapshot['reason'], 'scenario_skip_ongui_unavailable')
        self.assertFalse(self.objects.admission)
        self.assertTrue(c.resume())
        eventually(lambda: len(self.objects.commands) == 2)
        self.assertEqual(c.snapshot['losses'], 0)

    def test_later_pause_during_resume_observation_cannot_be_overwritten(self):
        c, _, _ = self.rejected_skip_fixture()
        entered, release = threading.Event(), threading.Event()
        armed = threading.Event()
        armed.set()
        original = self.objects.observe_campaign
        self.addCleanup(release.set)
        def blocked_read(token):
            if armed.is_set() and c._resume_epoch is not None and not c._resume_requested:
                armed.clear()
                entered.set()
                release.wait(1)
            return original(token)
        self.objects.observe_campaign = blocked_read
        self.assertTrue(c.resume())
        self.assertTrue(entered.wait(1))
        started = time.monotonic()
        self.assertTrue(c.pause())
        self.assertLess(time.monotonic() - started, .05)
        release.set()
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertEqual(len(self.objects.commands), 1)
        self.assertTrue(c.is_paused)
        self.assertFalse(self.objects.admission)
        self.assertEqual(CampaignStore(c.snapshot['ledger_path']).load().mode.value, 'paused')
        self.assertTrue(c.resume())
        eventually(lambda: len(self.objects.commands) == 2)

    def resume_checkpoint_race(self, decision):
        c, _, _ = self.rejected_skip_fixture()
        entered, release = threading.Event(), threading.Event()
        corrected = threading.Event()
        original = CampaignStore.save
        armed = [True]
        self.addCleanup(release.set)
        def blocked_save(store, state):
            original(store, state)
            if state.mode.value == 'active' and armed[0]:
                armed[0] = False
                entered.set()
                release.wait(1)
            if state.mode.value in ('paused', 'stopped') and not armed[0]:
                corrected.set()
        with patch.object(CampaignStore, 'save', blocked_save):
            self.assertTrue(c.resume())
            self.assertTrue(entered.wait(1))
            self.assertEqual(CampaignStore(c.snapshot['ledger_path']).load().mode.value, 'active')
            started = time.monotonic()
            self.assertTrue(c.pause() if decision == 'pause' else c.stop())
            self.assertLess(time.monotonic() - started, .05)
            self.assertFalse(self.objects.admission)
            release.set()
            self.assertTrue(corrected.wait(1))
            if decision == 'stop':
                self.settled()
                self.assertEqual(c.snapshot['mode'], 'stopped')
                self.assertFalse(c.resume())
            else:
                eventually(lambda: c.snapshot['reason'] == 'resume_superseded_by_newer_decision')
                self.assertTrue(c.is_paused)
                self.assertFalse(self.objects.admission)
            self.assertEqual(len(self.objects.commands), 1)
            self.assertIsNone(c.snapshot['pending'])
            self.assertEqual(c.snapshot['losses'], 0)
            self.assertEqual(CampaignStore(c.snapshot['ledger_path']).load().mode.value,
                             'paused' if decision == 'pause' else 'stopped')
            if decision == 'pause':
                self.assertTrue(c.resume())
                eventually(lambda: len(self.objects.commands) == 2)

    def test_later_pause_during_resume_checkpoint_keeps_admission_closed(self):
        self.resume_checkpoint_race('pause')

    def test_later_stop_during_resume_checkpoint_remains_terminal(self):
        self.resume_checkpoint_race('stop')

    def test_newer_resume_survives_corrective_pause_checkpoint(self):
        c, _, _ = self.rejected_skip_fixture()
        active_entered, active_release = threading.Event(), threading.Event()
        repair_entered, repair_release = threading.Event(), threading.Event()
        original = CampaignStore.save
        armed = [True, True]
        self.addCleanup(active_release.set)
        self.addCleanup(repair_release.set)
        def blocked_save(store, state):
            original(store, state)
            if state.mode.value == 'active' and armed[0]:
                armed[0] = False
                active_entered.set()
                active_release.wait(1)
            elif state.reason == 'resume_superseded_by_newer_decision' and armed[1]:
                armed[1] = False
                repair_entered.set()
                repair_release.wait(1)
        with patch.object(CampaignStore, 'save', blocked_save):
            self.assertTrue(c.resume())
            self.assertTrue(active_entered.wait(1))
            self.assertTrue(c.pause())
            active_release.set()
            self.assertTrue(repair_entered.wait(1))
            self.assertEqual(len(self.objects.commands), 1)
            self.assertFalse(self.objects.admission)
            self.assertTrue(c.resume())
            repair_release.set()
            eventually(lambda: len(self.objects.commands) == 2)
            before = self.objects.observes
            eventually(lambda: self.objects.observes >= before + 4)
            self.assertEqual(len(self.objects.commands), 2)
            self.assertFalse(c.is_paused)
            self.assertEqual(c.snapshot['losses'], 0)

    def test_navigation_rejection_pauses_and_resume_rechecks_gate_before_retry(self):
        self.objects.snap.update(phase='CAMPAIGN', revision=7)
        c = self.make()
        def select(token, action, **kwargs):
            self.assertEqual(action, 'select')
            self.objects.commands.append((action, kwargs))
            seq = len(self.objects.commands)
            rejected = seq == 1
            if not rejected:
                self.objects.snap.update(phase='TROOP', gate='NO_RESOURCE', revision=8)
            return {'requested_seq': seq, 'ack': {'command_seq': seq,
                'status': 'REJECTED' if rejected else 'COMPLETED', 'result': {'campaign_command':
                {'command_seq': seq, 'action': action, 'attempt_id': '', 'invoked': not rejected,
                 'reason': 'selection_not_invoked' if rejected else ''}}}}
        self.objects.submit_campaign = select
        c.start()
        eventually(lambda: c.is_paused and c.snapshot['reason'] == 'selection_not_invoked')
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertIsNone(c.snapshot['pending'])
        self.objects.snap['gate'] = 'NO_RESOURCE'
        self.assertTrue(c.resume())
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertTrue(c.is_paused)
        self.assertEqual(len(self.objects.commands), 1)
        self.assertEqual(c.snapshot['reason'], 'selection_not_invoked')
        self.objects.snap['gate'] = 'PASS'
        self.assertTrue(c.resume())
        eventually(lambda: len(self.objects.commands) == 2)
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertEqual(len(self.objects.commands), 2)
        self.assertEqual(c.snapshot['losses'], 0)

    def test_skip_unknown_timeout_or_missing_receipt_keeps_pending_and_never_retries(self):
        for failure in ('UNKNOWN', 'TIMEOUT', 'MISSING_RECEIPT'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as data:
                objects = FakeObjects()
                objects.snap.update(phase='SCENARIO', revision=7)
                objects.snap['tutorial'] = {'kind': 'SCENARIO', 'step': '10:1', 'gate': 'PASS'}
                runner = TaskRunner()
                c = CampaignCoordinator(objects, runner, Path(data), poll_seconds=.005,
                                        battle_timeout=.2, stop_timeout=.05)
                def uncertain(token, action, **kwargs):
                    objects.commands.append((action, kwargs))
                    if failure == 'TIMEOUT':
                        raise TimeoutError('skip ACK unknown')
                    receipt = {'command_seq': 1, 'action': action, 'attempt_id': '', 'invoked': False}
                    return {'requested_seq': 1, 'ack': {'command_seq': 1, 'status': failure,
                        'result': {'campaign_command': receipt} if failure != 'MISSING_RECEIPT' else {}}}
                objects.submit_campaign = uncertain
                try:
                    c.start()
                    eventually(lambda: not c.is_active)
                    runner.join(1)
                    self.assertEqual(c.snapshot['losses'], 0)
                    self.assertIsNotNone(c.snapshot['pending'])
                    self.assertTrue(c.snapshot['pending']['uncertain'])
                    self.assertEqual(len(objects.commands), 1)
                    self.assertFalse(c.start())
                finally:
                    c.close()
                    runner.stop()
                    runner.join(1)

    def test_persistence_failure_before_start_never_admits_attack(self):
        c = self.make()
        original = CampaignStore.save
        def fail_prepare(store, state):
            if state.pending:
                raise OSError('ledger disk full')
            return original(store, state)
        with patch.object(CampaignStore, 'save', fail_prepare):
            c.start()
            self.settled()
        self.assertFalse(any(a == 'start' for a, _ in self.objects.commands))
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(c.snapshot['mode'], 'blocked')
        self.assertIn('persistence_error', c.snapshot['reason'])

    def test_transport_unknown_keeps_planned_attempt_for_read_only_reconciliation(self):
        c = self.make()
        original = self.objects.submit_campaign
        def lost_ack(token, action, **kwargs):
            wire = original(token, action, **kwargs)
            if action == 'start':
                raise TimeoutError('ACK lost')
            return wire
        self.objects.submit_campaign = lost_ack
        c.start()
        self.settled()
        self.assertEqual(c.snapshot['pending']['intent_id'], self.objects.commands[-1][1]['attempt_id'])
        self.assertEqual(c.snapshot['pending']['attempt_id'], '')
        self.assertTrue(c.snapshot['pending']['uncertain'])
        self.assertEqual(c.snapshot['losses'], 0)

    def test_wrong_ack_cannot_clear_or_submit_pending_intent(self):
        self.objects.rejected = True
        c = self.make()
        original = self.objects.submit_campaign
        def wrong_ack(token, action, **kwargs):
            wire = original(token, action, **kwargs)
            if action == 'start':
                wire['ack']['result']['campaign_command']['attempt_id'] = 'old-attempt'
            return wire
        self.objects.submit_campaign = wrong_ack
        c.start()
        self.settled()
        self.assertIsNotNone(c.snapshot['pending'])
        self.assertTrue(c.snapshot['pending']['uncertain'])
        self.assertEqual(c.snapshot['losses'], 0)

    def test_pause_wins_gate_after_prepare_rejects_only_unsubmitted_intent(self):
        c = self.make()
        original = self.objects.submit_campaign
        def pause_before_admission(token, action, **kwargs):
            if action == 'start':
                c.pause()
            return original(token, action, **kwargs)
        self.objects.submit_campaign = pause_before_admission
        c.start()
        eventually(lambda: c.is_paused and c.snapshot['pending'] is None)
        self.assertFalse(any(a == 'start' for a, _ in self.objects.commands))
        self.assertEqual(c.snapshot['losses'], 0)

    def test_proven_no_submission_can_resume_same_revision_once_with_new_intent(self):
        for proof in ('ADMISSION_CLOSED', 'CANCELLED_BEFORE_SUBMIT'):
            with self.subTest(proof=proof), tempfile.TemporaryDirectory() as data:
                objects = FakeObjects()
                objects.snap.update(phase='SCENARIO', revision=7)
                objects.snap['tutorial'] = {'kind': 'SCENARIO', 'step': '10:1', 'gate': 'PASS'}
                runner = TaskRunner()
                c = CampaignCoordinator(objects, runner, Path(data), poll_seconds=.005,
                                        battle_timeout=.2, stop_timeout=.05)
                original = objects.submit_campaign
                intent_ids = []
                def pause_before_io(token, action, **kwargs):
                    intent_ids.append(c.snapshot['pending']['intent_id'])
                    if len(intent_ids) == 1:
                        c.pause()
                        if proof == 'ADMISSION_CLOSED':
                            raise CampaignAdmissionClosed('owning port did not admit command')
                        return {'cancelled_before_submit': True}
                    wire = original(token, action, **kwargs)
                    objects.snap['gate'] = 'NO_RESOURCE'
                    return wire
                objects.submit_campaign = pause_before_io
                try:
                    c.start()
                    eventually(lambda: c.is_paused and c.snapshot['pending'] is None)
                    self.assertEqual(objects.snap['revision'], 7)
                    self.assertEqual(objects.commands, [])
                    self.assertEqual(c.snapshot['losses'], 0)
                    self.assertTrue(c.resume())
                    eventually(lambda: len(objects.commands) == 1 and c.snapshot['pending'] is None)
                    before = objects.observes
                    eventually(lambda: objects.observes >= before + 4)
                    self.assertEqual(len(objects.commands), 1)
                    self.assertEqual(len(intent_ids), 2)
                    self.assertNotEqual(intent_ids[0], intent_ids[1])
                    self.assertEqual(c.snapshot['completed_guides'], ['10:1'])
                    self.assertEqual(c.snapshot['losses'], 0)
                finally:
                    c.close()
                    runner.stop()
                    runner.join(1)

    def test_stop_winning_pre_submission_proof_does_not_reopen_run(self):
        for proof in ('ADMISSION_CLOSED', 'CANCELLED_BEFORE_SUBMIT'):
            with self.subTest(proof=proof), tempfile.TemporaryDirectory() as data:
                objects = FakeObjects()
                objects.snap.update(phase='SCENARIO', revision=7)
                objects.snap['tutorial'] = {'kind': 'SCENARIO', 'step': '10:1', 'gate': 'PASS'}
                runner = TaskRunner()
                c = CampaignCoordinator(objects, runner, Path(data), poll_seconds=.005,
                                        battle_timeout=.2, stop_timeout=.05)
                def stop_before_io(token, action, **kwargs):
                    c.stop()
                    if proof == 'ADMISSION_CLOSED':
                        raise CampaignAdmissionClosed('owning port stopped before admission')
                    return {'cancelled_before_submit': True}
                objects.submit_campaign = stop_before_io
                try:
                    c.start()
                    eventually(lambda: not c.is_active)
                    runner.join(1)
                    self.assertEqual(c.snapshot['mode'], 'stopped')
                    self.assertEqual(c.snapshot['reason'], 'user_stop')
                    self.assertIsNone(c.snapshot['pending'])
                    self.assertEqual(c.snapshot['losses'], 0)
                    self.assertEqual(objects.commands, [])
                    self.assertFalse(c.resume())
                    state = CampaignStore(c.snapshot['ledger_path']).load()
                    self.assertEqual(state.mode.value, 'stopped')
                    self.assertIsNone(state.pending)
                finally:
                    c.close()
                    runner.stop()
                    runner.join(1)

    def test_conflicting_duplicate_result_blocks_without_increment(self):
        self.objects.outcomes = ['LOSS']
        self.objects.auto_settle = False
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        c.pause()
        self.objects.auto_settle = True
        eventually(lambda: c.snapshot['losses'] == 1)
        self.objects.snap['attempt']['outcome'] = 'WIN'
        self.settled()
        self.assertEqual(c.snapshot['losses'], 1)
        self.assertEqual(c.snapshot['reason'], 'conflicting_result')

    def test_missing_result_timeout_uses_wall_clock_even_while_paused(self):
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        c.pause()
        self.settled()
        self.assertEqual(c.snapshot['reason'], 'battle_timeout_pending_retained')
        self.assertEqual(c.snapshot['losses'], 0)

    def test_recovery_unknown_ack_reconciles_same_attempt_without_new_start(self):
        runs = Path(self.temp.name) / 'campaign_runs'
        runs.mkdir()
        state = Campaign.create(CampaignStore(runs / 'run.json'), 'run', Token.context_key)
        state.resume(Observation('run', Token.context_key, 1))
        intent = state.prepare(Observation('run', Token.context_key, 2, ('10:20',)), 'a' * 32)
        state.mark_unknown('lost_ack')
        self.objects.snap['attempt'] = {'status': 'SETTLED', 'attempt_id': intent.intent_id,
            'command_seq': 1, 'campaign_id': 10, 'troop_id': 20, 'report_id': '123',
            'outcome': 'LOSS', 'evidence': 'captured_authoritative_receipt_applied'}
        self.objects.snap.update(phase='RESULT', revision=3)
        c = self.make()
        self.assertFalse(c.start())
        self.assertTrue(c.resume())
        eventually(lambda: c.snapshot['losses'] == 1)
        self.assertTrue(c.is_paused)
        self.assertEqual(self.objects.commands, [])
        c.stop()
        self.settled()

    def test_pause_and_stop_before_runner_entry_still_finishes_cleanup(self):
        entered, release = threading.Event(), threading.Event()
        original = self.runner._run
        def delayed(*args):
            entered.set()
            release.wait(1)
            original(*args)
        self.runner._run = delayed
        c = self.make()
        c.start()
        self.assertTrue(entered.wait(1))
        c.pause()
        c.close()
        release.set()
        eventually(lambda: c.is_shutdown)
        self.runner.join(1)
        self.assertFalse(c.is_active)
        self.assertFalse(any(a == 'start' for a, _ in self.objects.commands))

    def test_cleanup_error_still_finishes_coordinator_shutdown(self):
        c = self.make()
        self.objects.release_campaign = lambda *a, **kw: (_ for _ in ()).throw(OSError('cleanup unavailable'))
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        c.close()
        eventually(lambda: c.is_shutdown)
        self.assertIn('cleanup unavailable', c.snapshot['cleanup_error'])

    def test_timeout_configuration_must_be_finite(self):
        for value in (float('nan'), float('inf'), 0, -1, True):
            with self.assertRaises(ValueError):
                CampaignCoordinator(self.objects, self.runner, Path(self.temp.name), battle_timeout=value)

    def test_non_forced_skill_hint_does_not_block_normal_campaign(self):
        self.objects.snap['tutorial'] = {'kind': 'HINT', 'step': 'new-skill', 'gate': ''}
        c = self.make()
        c.start()
        self.assertTrue(self.objects.started.wait(1))
        c.stop()
        self.settled()
        self.assertEqual(sum(a == 'start' for a, _ in self.objects.commands), 1)

    def test_consumed_chapter_result_uses_reviewed_next_chapter_action(self):
        self.objects.outcomes = ['WIN']
        original = self.objects.observe_campaign
        def chapter_result(token):
            observed = original(token)
            if observed['campaign']['attempt']['status'] == 'SETTLED':
                observed['campaign']['result_kind'] = 'CHAPTER'
            return observed
        self.objects.observe_campaign = chapter_result
        c = self.make()
        c.start()
        eventually(lambda: any(a == 'next_chapter' for a, _ in self.objects.commands))
        c.stop()
        self.settled()
        self.assertFalse(any(a == 'ack_result' for a, _ in self.objects.commands))

    def test_scenario_waits_until_ready_and_confirms_step_before_next_continue(self):
        self.objects.snap.update(phase='SCENARIO')
        self.objects.snap['tutorial'] = {'kind': 'SCENARIO', 'step': '10:1', 'gate': 'WAITING_TEXT'}
        original = self.objects.submit_campaign
        def scenario_submit(token, action, **kwargs):
            wire = original(token, action, **kwargs)
            if action == 'continue':
                step = '10:1' if sum(a == 'continue' for a, _ in self.objects.commands) == 1 else '10:2'
                self.objects.snap.update(phase='SCENARIO', tutorial={'kind': 'SCENARIO', 'step': step, 'gate': 'WAITING_NEXT'})
            return wire
        self.objects.submit_campaign = scenario_submit
        c = self.make()
        c.start()
        eventually(lambda: self.objects.observes >= 4)
        self.assertFalse(c.is_paused)
        self.assertEqual(self.objects.commands, [])
        for gate in ('WAITING_MOVEMENT', 'WAITING_UI', 'WAITING_SCENARIO_END'):
            self.objects.snap['tutorial']['gate'] = gate
            self.objects.snap['revision'] += 1
            before = self.objects.observes
            eventually(lambda: self.objects.observes >= before + 3)
            self.assertFalse(c.is_paused)
            self.assertEqual(self.objects.commands, [])
        self.objects.snap['tutorial']['gate'] = 'PASS'
        self.objects.snap['revision'] += 1
        eventually(lambda: sum(a == 'continue' for a, _ in self.objects.commands) == 1)
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertEqual(c.snapshot['completed_guides'], [])
        self.assertIsNotNone(c.snapshot['pending'])
        self.assertEqual(sum(a == 'continue' for a, _ in self.objects.commands), 1)
        # Even READY on the original step cannot replay the submitted continue.
        self.objects.snap['tutorial']['gate'] = 'PASS'
        self.objects.snap['revision'] += 1
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 3)
        self.assertEqual(sum(a == 'continue' for a, _ in self.objects.commands), 1)
        self.objects.snap['tutorial'].update(step='10:2', gate='WAITING_TEXT')
        self.objects.snap['revision'] += 1
        eventually(lambda: c.snapshot['completed_guides'] == ['10:1'])
        self.assertIsNone(c.snapshot['pending'])
        self.assertFalse(c.is_paused)
        self.objects.snap['tutorial']['gate'] = 'PASS'
        self.objects.snap['revision'] += 1
        eventually(lambda: sum(a == 'continue' for a, _ in self.objects.commands) == 2)
        self.objects.snap['tutorial'] = {'kind': 'NONE', 'step': '', 'gate': ''}
        self.objects.snap.update(phase='CAMPAIGN', gate='NO_RESOURCE', revision=self.objects.snap['revision'] + 1)
        eventually(lambda: c.snapshot['completed_guides'] == ['10:1', '10:2'])
        c.stop()
        self.settled()

    def test_old_conflicting_settlement_blocks_during_different_pending_attempt(self):
        self.objects.outcomes = ['LOSS']
        original = self.objects.observe_campaign
        captured = []
        def capture_first(token):
            observed = original(token)
            if observed['campaign']['attempt']['status'] == 'SETTLED' and not captured:
                captured.append(copy.deepcopy(observed['campaign']['attempt']))
            return observed
        self.objects.observe_campaign = capture_first
        c = self.make()
        c.start()
        eventually(lambda: sum(a == 'start' for a, _ in self.objects.commands) == 2)
        second = copy.deepcopy(self.objects.snap['attempt'])
        self.objects.snap['attempt'] = {**captured[0], 'outcome': 'WIN'}
        self.settled()
        self.assertEqual(c.snapshot['reason'], 'conflicting_result')
        self.assertEqual(c.snapshot['losses'], 1)
        self.assertEqual(c.snapshot['pending']['attempt_id'], second['attempt_id'])
        # A later valid B settlement cannot reopen the already conflicted run.
        self.objects.snap['attempt'] = {**second, 'status': 'SETTLED', 'outcome': 'LOSS',
            'report_id': '999', 'evidence': 'captured_authoritative_receipt_applied'}
        self.objects.snap.update(phase='RESULT', revision=self.objects.snap['revision'] + 1)
        self.assertFalse(c.start())
        self.assertEqual(sum(a == 'start' for a, _ in self.objects.commands), 2)
        self.assertTrue(c.resume())
        self.settled()
        self.assertEqual(c.snapshot['mode'], 'blocked')
        self.assertEqual(c.snapshot['reason'], 'conflicting_result')
        self.assertEqual(c.snapshot['losses'], 2)
        self.assertEqual(sum(a == 'start' for a, _ in self.objects.commands), 2)

    def test_old_exact_duplicate_is_idempotent_while_next_attempt_is_pending(self):
        self.objects.outcomes = ['LOSS']
        original = self.objects.observe_campaign
        captured = []
        def capture_first(token):
            observed = original(token)
            if observed['campaign']['attempt']['status'] == 'SETTLED' and not captured:
                captured.append(copy.deepcopy(observed['campaign']['attempt']))
            return observed
        self.objects.observe_campaign = capture_first
        c = self.make()
        c.start()
        eventually(lambda: sum(a == 'start' for a, _ in self.objects.commands) == 2)
        second = copy.deepcopy(self.objects.snap['attempt'])
        self.objects.snap['attempt'] = captured[0]
        before = self.objects.observes
        eventually(lambda: self.objects.observes >= before + 4)
        self.assertEqual(c.snapshot['losses'], 1)
        self.assertEqual(c.snapshot['mode'], 'active')
        self.assertEqual(c.snapshot['pending']['attempt_id'], second['attempt_id'])
        c.stop()
        self.objects.snap['attempt'] = {**second, 'status': 'SETTLED', 'outcome': 'WIN',
            'report_id': '999', 'evidence': 'captured_authoritative_receipt_applied'}
        self.settled()
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(len(c.snapshot['consumed']), 2)

    def test_settlement_proof_rejects_missing_fields_and_invalid_types(self):
        valid = {'status': 'SETTLED', 'attempt_id': 'a' * 32, 'command_seq': 3,
                 'campaign_id': 10, 'troop_id': 20, 'report_id': '123', 'outcome': 'LOSS',
                 'evidence': 'captured_authoritative_receipt_applied'}
        self.assertTrue(CampaignCoordinator._settled_result('run', 'ctx', valid, stage_key='10:20').confirmed)
        for key in valid:
            with self.subTest(missing=key):
                malformed = {k: v for k, v in valid.items() if k != key}
                self.assertFalse(CampaignCoordinator._settled_result('run', 'ctx', malformed, stage_key='10:20').confirmed)
        for key, value in (('command_seq', True), ('command_seq', '3'), ('command_seq', 0),
                           ('campaign_id', True), ('troop_id', '20'), ('report_id', 123),
                           ('report_id', '0'), ('report_id', '١٢٣'), ('outcome', 'DRAW'),
                           ('evidence', True), ('attempt_id', 123)):
            with self.subTest(field=key, value=value):
                self.assertFalse(CampaignCoordinator._settled_result('run', 'ctx', {**valid, key: value}, stage_key='10:20').confirmed)

    def test_progress_disposition_never_consumes_loss_or_replaces_a_packet(self):
        attempt = {'status': 'SETTLED', 'attempt_id': 'a' * 32, 'command_seq': 3,
                   'campaign_id': 10, 'troop_id': 20, 'report_id': '123'}
        for outcome in ('WIN', 'LOSS'):
            for evidence in ('captured_authoritative_receipt_applied',
                             'captured_queued_authoritative_receipt_applied',
                             'anchored_mainline_progress_applied', 'target_already_cleared'):
                with self.subTest(outcome=outcome, evidence=evidence):
                    result = CampaignCoordinator._settled_result('run', 'ctx',
                        {**attempt, 'outcome': outcome, 'evidence': evidence}, stage_key='10:20')
                    expected = evidence.startswith('captured_') or outcome == 'WIN' and evidence == 'anchored_mainline_progress_applied'
                    self.assertEqual(result.confirmed, expected)

    def test_normal_reward_selection_zero_uses_consumed_attempt_before_skipping_story(self):
        self.objects.outcomes = ['WIN']
        original = self.objects.submit_campaign
        acknowledgements = []

        def with_reward(token, action, **kwargs):
            if action == 'ack_result':
                self.assertEqual(kwargs['troop_id'], 20)
                acknowledgements.append(kwargs['troop_id'])
            wire = original(token, action, **kwargs)
            if action == 'ack_result' and len(acknowledgements) == 1:
                self.objects.snap.update(phase='RESULT', result_kind='REWARD', troop_id=0)
            elif action == 'ack_result':
                self.objects.snap.update(phase='SCENARIO', result_kind='',
                    tutorial={'kind': 'SCENARIO', 'step': '647:0', 'gate': 'PASS'})
            elif action == 'continue':
                self.objects.snap.update(phase='CAMPAIGN', eligible_troops=[])
            return wire

        self.objects.submit_campaign = with_reward
        c = self.make()
        self.assertTrue(c.start())
        eventually(lambda: c.is_paused and c.snapshot['reason'] == 'no_eligible_stage')
        self.assertEqual(acknowledgements, [20, 20])
        self.assertEqual(sum(action == 'start' for action, _ in self.objects.commands), 1)
        self.assertEqual(sum(action == 'continue' for action, _ in self.objects.commands), 1)
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(len(c.snapshot['consumed']), 1)
        self.assertIn('647:0', c.snapshot['completed_guides'])
        c.stop()
        self.settled()

    @staticmethod
    def loading_context():
        return {'available': False, 'gate': 'global_input_disabled', 'phase': 'CAMPAIGN',
                'ui_context': {'selection_gate': 'PASS', 'current_root_level': 'Campaign', 'current_root_active': True},
                'selection_diagnostics': {'campaign': {'type': 0, 'unlocked': True, 'm_bDebug': False,
                    'm_bPostInit': True, 'm_bBorning': True, 'm_bScenarioVisible': True}}}

    def test_post_result_transition_waits_for_reward_then_skips_actual_story(self):
        self.objects.outcomes = ['WIN']
        submit, observe = self.objects.submit_campaign, self.objects.observe_campaign
        acknowledgements, waiting = [], [0, None]

        def with_loading(token, action, **kwargs):
            if action == 'ack_result':
                self.assertEqual(kwargs['troop_id'], 20)
                acknowledgements.append(kwargs['troop_id'])
            wire = submit(token, action, **kwargs)
            if action == 'ack_result' and len(acknowledgements) == 1:
                self.objects.snap.update(self.loading_context())
                self.objects.snap['troop_id'] = 0
                waiting[0], waiting[1] = 1, len(self.objects.commands)
            elif action == 'ack_result':
                self.objects.snap.update(phase='SCENARIO', result_kind='',
                    tutorial={'kind': 'SCENARIO', 'step': '531:0', 'gate': 'PASS'})
            elif action == 'continue':
                self.objects.snap.update(phase='CAMPAIGN', eligible_troops=[])
            return wire

        def during_loading(token):
            if 0 < waiting[0] <= 3:
                self.assertEqual(len(self.objects.commands), waiting[1])
                waiting[0] += 1
            elif waiting[0] == 4:
                waiting[0] += 1
                self.objects.snap.update(phase='RESULT', result_kind='REWARD', available=True, gate='PASS')
            return observe(token)

        self.objects.submit_campaign, self.objects.observe_campaign = with_loading, during_loading
        c = self.make(transition_timeout=.2)
        self.assertTrue(c.start())
        eventually(lambda: c.is_paused and c.snapshot['reason'] == 'no_eligible_stage')
        self.assertEqual(waiting[0], 5)
        self.assertEqual(acknowledgements, [20, 20])
        self.assertIn('531:0', c.snapshot['completed_guides'])
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertEqual(len(c.snapshot['consumed']), 1)
        c.stop()
        self.settled()

    def test_loading_timeout_pauses_without_game_input_or_loss(self):
        self.objects.snap.update(self.loading_context())
        c = self.make(transition_timeout=.025)
        self.assertTrue(c.start())
        eventually(lambda: c.is_paused and c.snapshot['reason'] == 'game_transition_timeout')
        self.assertEqual(self.objects.commands, [])
        self.assertEqual(c.snapshot['losses'], 0)
        self.assertIsNone(c.snapshot['pending'])
        c.stop()
        self.settled()

    def test_unknown_or_unbound_disabled_context_is_not_a_loading_wait(self):
        valid = self.loading_context()
        self.assertTrue(CampaignCoordinator._game_transition(valid))
        self.assertTrue(CampaignCoordinator._game_transition({**valid, 'phase': 'TROOP', 'gate': 'campaign_loading'}))
        self.assertFalse(CampaignCoordinator._game_transition({**valid, 'phase': 'TROOP'}))
        for key, value in (('gate', 'campaign_unexpected_overlay'), ('phase', 'RESULT'),
                           ('ui_context', {'selection_gate': 'PASS', 'current_root_level': 'Anecdote', 'current_root_active': True}),
                           ('selection_diagnostics', {'campaign': {'type': 0, 'unlocked': True, 'm_bDebug': False}}),
                           ('selection_diagnostics', {'campaign': {'type': False, 'unlocked': True, 'm_bDebug': False, 'm_bBorning': True}})):
            self.assertFalse(CampaignCoordinator._game_transition({**valid, key: value}))

    def test_successful_recovery_terminal_clear_allows_explicit_new_run(self):
        runs = Path(self.temp.name) / 'campaign_runs'
        runs.mkdir()
        Campaign.create(CampaignStore(runs / 'paused.json'), 'old-run', Token.context_key)
        self.objects.outcomes = ['WIN']
        self.objects.auto_settle = False
        c = self.make()
        self.assertTrue(c.resume())
        self.assertTrue(self.objects.started.wait(1))
        c.stop()
        self.objects.auto_settle = True
        self.settled()
        self.assertEqual(c.snapshot['mode'], 'stopped')
        self.assertIsNone(c.snapshot['pending'])
        self.assertFalse(c.is_paused)
        old_run = c.snapshot['run_id']
        self.objects.snap.update(phase='MAIN', gate='NO_RESOURCE', revision=self.objects.snap['revision'] + 1)
        self.assertTrue(c.start())
        eventually(lambda: c.snapshot.get('run_id') != old_run)
        self.assertNotEqual(c.snapshot['run_id'], old_run)
        c.stop()
        self.settled()

    def test_malformed_settlement_through_coordinator_keeps_pending_without_loss(self):
        for changes in ({'command_seq': True}, {'report_id': 123}, {'report_id': None},
                        {'evidence': ''}, {'outcome': 'DRAW'}, {'campaign_id': '10'}):
            with self.subTest(changes=changes), tempfile.TemporaryDirectory() as data:
                objects = FakeObjects()
                runner = TaskRunner()
                c = CampaignCoordinator(objects, runner, Path(data), poll_seconds=.005,
                                        battle_timeout=.2, stop_timeout=.05)
                try:
                    c.start()
                    self.assertTrue(objects.started.wait(1))
                    objects.snap['attempt'].update(status='SETTLED', report_id='123', outcome='LOSS',
                        evidence='captured_authoritative_receipt_applied')
                    objects.snap['attempt'].update(changes)
                    eventually(lambda: not c.is_active)
                    runner.join(.2)
                    self.assertEqual(c.snapshot['losses'], 0)
                    self.assertEqual(c.snapshot['reason'], 'invalid_settlement_evidence')
                    self.assertIsNotNone(c.snapshot['pending'])
                    self.assertEqual(sum(a == 'start' for a, _ in objects.commands), 1)
                finally:
                    c.close()
                    runner.join(1)

    def test_late_cleanup_rechecks_public_boundaries_without_io_or_losing_ledger(self):
        for boundary in ('snapshot', 'is_paused', 'start'):
            with self.subTest(boundary=boundary), tempfile.TemporaryDirectory() as data:
                runs = Path(data) / 'campaign_runs'
                runs.mkdir()
                path = runs / 'old-run.json'
                Campaign.create(CampaignStore(path), 'old-run', Token.context_key)
                objects = FakeObjects()
                objects.snap['gate'] = 'NO_RESOURCE'
                runner = TaskRunner()
                def late_cleanup(token, cleanup=True):
                    objects.owner = None
                    objects.released = True
                    objects.bridge_state = 'stopping'
                    objects.is_busy = True
                objects.release_campaign = late_cleanup
                c = CampaignCoordinator(objects, runner, Path(data), poll_seconds=.005, stop_timeout=.05)
                try:
                    # Only the cleanup wait sees simulated elapsed time. The
                    # runner and assertions retain the real monotonic clock.
                    cleanup_clock = iter((0.0, 6.5))
                    with patch('jiangshen_assistant.campaign_task.time',
                               SimpleNamespace(monotonic=lambda: next(cleanup_clock))):
                        self.assertTrue(c.resume())
                        eventually(lambda: c.snapshot.get('reason') == 'campaign_gate: NO_RESOURCE')
                        c.stop()
                        eventually(lambda: not c.is_active)
                        runner.join(1)
                    self.assertEqual(c.snapshot['mode'], 'stopped')
                    self.assertEqual(c.snapshot['cleanup_state'], 'pending')
                    self.assertTrue(c.is_paused)
                    self.assertFalse(c.start())
                    state_before = path.read_bytes()
                    calls_before = len(objects.commands), objects.observes
                    objects.bridge_state = 'disconnected'
                    # A disconnect callback can precede the worker's busy
                    # release. It must not clear the recovery gate yet.
                    self.assertEqual(c.snapshot['cleanup_state'], 'pending')
                    self.assertTrue(c.is_paused)
                    objects.is_busy = False
                    started = time.monotonic()
                    if boundary == 'snapshot':
                        self.assertEqual(c.snapshot['cleanup_state'], 'confirmed')
                    elif boundary == 'is_paused':
                        self.assertFalse(c.is_paused)
                    else:
                        # Keep another task occupying the runner so this call
                        # checks admission without starting bridge work.
                        runner.start('other-task', lambda ctx: ctx.wait(60))
                        self.assertFalse(c.start())
                    self.assertLess(time.monotonic() - started, .05)
                    self.assertFalse(c.is_paused)
                    self.assertEqual(c.snapshot['cleanup_state'], 'confirmed')
                    self.assertEqual(path.read_bytes(), state_before)
                    self.assertEqual((len(objects.commands), objects.observes), calls_before)
                    self.assertTrue(any(event.data.get('cleanup_state') == 'confirmed'
                                        for event in list(c.events.queue)))
                    runner.stop()
                    runner.join(1)
                    objects.release_campaign = FakeObjects.release_campaign.__get__(objects)
                    self.assertTrue(c.start())
                    eventually(lambda: c.snapshot.get('run_id') != 'old-run')
                    c.stop()
                    eventually(lambda: not c.is_active)
                finally:
                    c.close()
                    runner.stop()
                    runner.join(1)

    def test_late_clean_disconnect_preserves_unresolved_or_error_recovery(self):
        c = self.make()
        for mode, pending, error in (('stopped', {'intent_id': 'a'}, ''),
                                     ('blocked', None, ''), ('paused', None, ''),
                                     ('stopped', None, 'persistence_error')):
            with self.subTest(mode=mode, pending=pending, error=error):
                c._recovery_path = Path(self.temp.name) / 'original.json'
                c._snapshot.update(mode=mode, pending=pending, cleanup_state='pending')
                c._runtime_error = error
                self.objects.bridge_state = 'disconnected'
                self.objects.is_busy = False
                self.assertEqual(c.snapshot['cleanup_state'], 'confirmed')
                self.assertTrue(c.is_paused)
                self.assertFalse(c.start())


class ControllerCoordinatorIntegrationTests(unittest.TestCase):
    """Real owner/runner/coordinator; only the process and bridge are fakes."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.clients = []
        self.factory_fault = None
        self.factory_entered = threading.Event()
        self.release_factory = threading.Event()
        self.release_factory.set()
        self.identity_entered = threading.Event()
        self.release_identity = threading.Event()
        self.release_identity.set()
        self.connect_ready = threading.Event()
        self.release_connect = threading.Event()
        self.release_connect.set()
        self.reader = FakeReader()
        outer = self

        class CampaignClient(FakeClient):
            def __init__(self, *args, **kwargs):
                outer.factory_entered.set()
                outer.release_factory.wait(1)
                super().__init__(*args, **kwargs)
                self.engine = FakeObjects(['LOSS', 'LOSS', 'LOSS'])
                self.engine_token = self.engine.acquire_campaign('wire')
                self._publish_campaign()

            def _publish_campaign(self):
                self.last_state['campaign'] = {**copy.deepcopy(self.engine.snap), 'character_id': '12345'}

            def connect(self, cancel=None):
                state = super().connect(cancel)
                outer.connect_ready.set()
                outer.release_connect.wait(1)
                return state

            def campaign_observe(self, **kwargs):
                self.engine.observe_campaign(self.engine_token)
                self._publish_campaign()
                self.on_state(self.last_state)
                return {'state': self.last_state}

            def campaign_command(self, action, **kwargs):
                wire = self.engine.submit_campaign(self.engine_token, action, **kwargs)
                self._publish_campaign()
                self.on_state(self.last_state)
                return {**wire, 'state': self.last_state}

        def factory(*args, **kwargs):
            if self.factory_fault:
                raise self.factory_fault
            client = CampaignClient(*args, **kwargs)
            self.clients.append(client)
            return client
        def read_identity(window):
            if threading.current_thread().name == 'object-connect':
                self.identity_entered.set()
                self.release_identity.wait(1)
            return ProcessIdentity(window.process_id, 1000, window.hwnd, 300)
        self.objects = ObjectController(Path(self.temp.name), Path(self.temp.name), reader=self.reader,
            identity_reader=read_identity,
            identity_validator=lambda *a: None, client_factory=factory, poll_seconds=.01, close_seconds=.2)
        self.runner = TaskRunner()
        self.coordinator = CampaignCoordinator(self.objects, self.runner, Path(self.temp.name),
            poll_seconds=.005, battle_timeout=.3, stop_timeout=.05, connect_timeout=.1)
        self.objects.bind(WindowInfo(200, 'fake game', 100, 'fake.exe', 1024, 768))
        self.addCleanup(self.finish)

    def finish(self):
        self.release_factory.set()
        self.release_identity.set()
        self.release_connect.set()
        self.coordinator.close()
        self.runner.join(1)
        self.objects.close()
        eventually(lambda: self.objects.is_shutdown)

    def test_first_start_connects_once_and_runs_through_real_owner_port(self):
        self.assertEqual(self.objects.bridge_state, 'disconnected')
        self.assertTrue(self.coordinator.start())
        eventually(lambda: not self.coordinator.is_active)
        self.runner.join(1)
        self.assertEqual(self.coordinator.snapshot['mode'], 'loss_limit')
        self.assertEqual(self.coordinator.snapshot['losses'], 3)
        self.assertEqual(len(self.clients), 1)
        client = self.clients[0]
        self.assertEqual(client.connect_calls, 1)
        self.assertEqual(client.stop_calls, 1)
        self.assertEqual(sum(a == 'start' for a, _ in client.engine.commands), 3)
        self.assertTrue(Path(self.coordinator.snapshot['ledger_path']).is_file())
        self.assertTrue(any(e.kind == 'bridge' for e in list(self.objects.events.queue)))

    def test_connect_timeout_cancels_unowned_connect_before_factory_returns(self):
        now = [0.0]
        self.coordinator._clock = lambda: now[0]
        self.release_factory.clear()
        self.assertTrue(self.coordinator.start())
        self.assertTrue(self.factory_entered.wait(1))
        now[0] = .2
        eventually(lambda: not self.coordinator.is_active)
        self.assertEqual(self.coordinator.snapshot['reason'], 'object_connection_timeout')
        self.assertEqual(self.coordinator.snapshot['connection']['phase'], 'factory')
        self.assertTrue(self.coordinator.snapshot['connection']['started_at'])
        self.assertEqual(self.coordinator.snapshot['losses'], 0)
        self.assertFalse((Path(self.temp.name) / 'campaign_runs').exists())
        self.release_factory.set()
        eventually(lambda: not self.objects.is_busy)
        self.assertEqual(self.clients[0].connect_calls, 0)
        self.assertEqual(self.objects.bridge_state, 'disconnected')

    def test_real_owner_connection_error_is_reported_without_waiting_generic_timeout(self):
        self.factory_fault = OSError('fake constructor failure')
        self.assertTrue(self.coordinator.start())
        eventually(lambda: not self.coordinator.is_active)
        self.runner.join(1)
        self.assertEqual(self.coordinator.snapshot['reason'], 'object_connection_failed: fake constructor failure')
        self.assertEqual(self.coordinator.snapshot['connection']['phase'], 'factory_failed')
        self.assertEqual(self.coordinator.snapshot['connection']['error'], 'fake constructor failure')
        self.assertEqual(self.clients, [])
        self.assertFalse((Path(self.temp.name) / 'campaign_runs').exists())
        self.assertTrue(any(e.kind == 'log' and 'fake constructor failure' in e.message
                            for e in list(self.objects.events.queue)))

    def test_failure_to_acquire_already_connecting_owner_does_not_stop_other_connection(self):
        self.release_factory.clear()
        self.assertTrue(self.objects.connect())
        self.assertTrue(self.factory_entered.wait(1))
        self.assertTrue(self.coordinator.start())
        eventually(lambda: not self.coordinator.is_active)
        self.assertEqual(self.coordinator.snapshot['reason'], 'object_connection_unavailable')
        status = self.coordinator.snapshot['connection']
        self.assertEqual(status['last_refusal'], 'operation_busy')
        self.assertEqual(status['phase'], 'factory')
        self.assertTrue(status['window_present'])
        self.assertFalse(status['client_present'])
        self.assertTrue(status['busy'])
        self.assertFalse(status['owner'])
        self.release_factory.set()
        eventually(lambda: not self.objects.is_busy)
        self.assertEqual(self.clients[0].connect_calls, 1)
        self.assertEqual(self.clients[0].stop_calls, 0)
        self.assertEqual(self.objects.bridge_state, 'ready')

    def test_stop_before_owner_cancels_connect_without_later_bridge_start(self):
        self.release_factory.clear()
        self.assertTrue(self.coordinator.start())
        self.assertTrue(self.factory_entered.wait(1))
        self.coordinator.stop()
        eventually(lambda: not self.coordinator.is_active)
        self.release_factory.set()
        eventually(lambda: not self.objects.is_busy)
        self.assertEqual(self.clients[0].connect_calls, 0)
        self.assertEqual(self.objects.bridge_state, 'disconnected')
        self.assertFalse((Path(self.temp.name) / 'campaign_runs').exists())

    def test_ready_callback_before_connect_returns_does_not_acquire_busy_owner(self):
        self.release_connect.clear()
        self.assertTrue(self.coordinator.start())
        self.assertTrue(self.connect_ready.wait(1))
        self.assertEqual(self.objects.bridge_state, 'ready')
        self.assertTrue(self.objects.is_busy)
        self.assertEqual(self.objects.connection_status['phase'], 'bridge_connect')
        self.assertFalse((Path(self.temp.name) / 'campaign_runs').exists())
        self.assertEqual(self.clients[0].engine.commands, [])
        self.release_connect.set()
        eventually(lambda: not self.coordinator.is_active)
        self.assertEqual(self.coordinator.snapshot['losses'], 3)
        self.assertEqual(self.clients[0].connect_calls, 1)

    def test_identity_preflight_timeout_preserves_phase_and_cannot_reach_factory(self):
        now = [0.0]
        self.coordinator._clock = lambda: now[0]
        self.release_identity.clear()
        self.assertTrue(self.coordinator.start())
        self.assertTrue(self.identity_entered.wait(1))
        started = time.monotonic()
        status = self.objects.connection_status
        self.assertLess(time.monotonic() - started, .05)
        self.assertEqual(status['phase'], 'identity')
        now[0] = .2
        eventually(lambda: not self.coordinator.is_active)
        self.assertEqual(self.coordinator.snapshot['connection']['phase'], 'identity')
        self.assertEqual(self.coordinator.snapshot['losses'], 0)
        self.release_identity.set()
        eventually(lambda: not self.objects.is_busy)
        self.assertEqual(self.clients, [])
        self.assertFalse(self.factory_entered.is_set())
        self.assertEqual(self.objects.bridge_state, 'disconnected')

    def test_unbound_first_connect_refusal_is_observable_without_starting_client(self):
        self.objects.unbind()
        self.assertTrue(self.coordinator.start())
        eventually(lambda: not self.coordinator.is_active)
        status = self.coordinator.snapshot['connection']
        self.assertEqual(status['last_refusal'], 'window_unbound')
        self.assertTrue(status['refused_at'])
        self.assertFalse(status['window_present'])
        self.assertFalse(status['client_present'])
        self.assertIsNone(status['client_terminal'])
        self.assertFalse(status['busy'])
        self.assertEqual(self.clients, [])
        self.assertTrue(any(e.kind == 'log' and 'window_unbound' in e.message
                            for e in list(self.objects.events.queue)))

    def test_other_owner_winning_ready_callback_keeps_admission_and_connection(self):
        original = self.objects.acquire_campaign
        other = []
        def other_owner_wins(run_id):
            other.append(original('other-run'))
            return original(run_id)
        self.objects.acquire_campaign = other_owner_wins
        try:
            self.assertTrue(self.coordinator.start())
            eventually(lambda: not self.coordinator.is_active)
            self.runner.join(1)
            self.assertEqual(len(other), 1)
            self.assertTrue(self.objects.has_campaign_owner)
            self.assertFalse(self.objects.campaign_snapshot(other[0])['stopping'])
            self.assertEqual(self.clients[0].stop_calls, 0)
            self.assertEqual(self.objects.bridge_state, 'ready')
            # Prove admission remains open through the public submit port,
            # rather than checking only the physical stop call counter.
            revision = self.objects.campaign_snapshot(other[0])['campaign']['revision']
            self.objects.submit_campaign(other[0], 'enter', expected_revision=revision)
            self.assertEqual(self.clients[0].engine.commands[0][0], 'enter')
            self.assertFalse((Path(self.temp.name) / 'campaign_runs').exists())
        finally:
            if other:
                self.objects.release_campaign(other[0])
                eventually(lambda: not self.objects.is_busy)


if __name__ == '__main__':
    unittest.main()


