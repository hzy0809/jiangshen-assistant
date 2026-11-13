import copy
import hashlib
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from types import SimpleNamespace

from jiangshen_assistant.campaign_recovery import canonical_bytes, load_json_bytes
from jiangshen_assistant.core import TaskRunner
from jiangshen_assistant.development import EquipmentTarget
from jiangshen_assistant.development_task import DevelopmentWorkflow
from jiangshen_assistant.development_recovery import (
    ORIGINAL_OPERATION, build_equipment_progress_resolution, verify_equipment_progress_resolution,
)
from jiangshen_assistant.development_recovery_task import DevelopmentRecoveryCoordinator
from jiangshen_assistant.development_completion import verify_equipment_completion
from tests.test_development_recovery import inputs
from tests.test_development_task import DevObjects
from tests.test_objects import eventually


class RecoveryObjects(DevObjects):
    def __init__(self, directory):
        super().__init__(directory)
        journal, original, refresh, facts, handler = inputs()
        self.root = Path(directory)
        self.state = load_json_bytes(refresh)
        self.dev = self.state['development']
        self.dev['equipment']['available_action'] = 'equipment_enchant'
        self.dev['growth'].update(tasks=[], window_visible=False)
        self.full_dto = copy.deepcopy(self.state['player_refresh'])
        self.state['player_refresh'] = {'schema': 1, 'status': 'IDLE'}
        self.refresh_calls = 0
        self.refresh_unknown = self.capital = False
        self.epoch_bad = False
        self.current_proof = None
        self.sync_seen = threading.Event()
        self.info = {'facts': facts,
            'journal_path': self.root / 'development_runs' / (ORIGINAL_OPERATION + '.json'),
            'original_state_path': self.root / 'original-state.json',
            'handler_proof_path': self.root / 'handler-proof.json',
            'resolution_path': self.root / 'development_resolutions' / (ORIGINAL_OPERATION + '.json'),
            'session_dir': self.root / 'object_sessions' / self.state['session_id']}
        for key, raw in [('journal_path', journal), ('original_state_path', original),
                         ('handler_proof_path', canonical_bytes(handler))]:
            self.info[key].parent.mkdir(parents=True, exist_ok=True)
            self.info[key].write_bytes(raw)
        self.original_bytes = journal

    def development_recovery_info(self, token):
        return copy.deepcopy(self.info)

    def observe_player_refresh(self, token, *, snapshot=False, timeout=3):
        self.state['frame'] += 1
        self.state['business']['development'] = copy.deepcopy(self.dev)
        state = copy.deepcopy(self.state)
        if self.epoch_bad:
            state['nonce'] = 'b' * 32
        return {'context_key': token.context_key, 'state': state,
                'player_refresh': copy.deepcopy(state['player_refresh']), 'stopping': self.stopping}

    def submit_player_refresh(self, token, *, expected_revision, operation_id, timeout=5):
        self.refresh_calls += 1
        journal = load_json_bytes((self.root / 'player_refresh_runs' / (operation_id + '.json')).read_bytes())
        assert journal['unresolved'] == dict(operation_id=operation_id, expected_revision=expected_revision)
        assert journal['events'][-1]['kind'] == 'intent'
        assert not self.paused and not self.stopping
        self.sync_seen.set()
        if self.refresh_unknown:
            raise TimeoutError('lost_refresh_ack')
        self.seq = 7
        self.full_dto.update(operation_id=operation_id, expected_revision=expected_revision,
                             root_level='Capital' if self.capital else 'Campaign')
        self.state['player_refresh'] = copy.deepcopy(self.full_dto)
        self.state['business']['player_refresh'] = dict(status='OBSERVED', operation_id=operation_id, sequence=7)
        if self.capital:
            self.state['campaign']['ui_context']['current_root_level'] = 'Capital'
            self.state['campaign']['phase'] = 'CAPITAL'
            self.state['campaign']['gate'] = 'BLOCKED'
        return dict(requested_seq=7, ack=dict(command_seq=7, status='COMPLETED',
            result=dict(player_refresh=copy.deepcopy(self.full_dto))))

    def freeze_player_refresh(self, token):
        path = self.info['session_dir'] / 'player-refresh-observed.json'
        path.parent.mkdir(parents=True, exist_ok=True)
        assert not path.exists()
        path.write_bytes(canonical_bytes(self.state) + b'\n')
        return path

    def check_development_resolution(self, token):
        proof = load_json_bytes(self.info['resolution_path'].read_bytes())
        verify_equipment_progress_resolution(proof,
            journal_bytes=self.info['journal_path'].read_bytes(),
            original_state_bytes=self.info['original_state_path'].read_bytes(),
            refresh_state_bytes=(self.info['session_dir'] / 'player-refresh-observed.json').read_bytes())
        if self.epoch_bad or self.state['player_refresh'] != load_json_bytes(
                (self.info['session_dir'] / 'player-refresh-observed.json').read_bytes())['player_refresh']:
            raise RuntimeError('current_refresh_epoch_mismatch')
        return proof

    def submit_development(self, token, **fields):
        wire = super().submit_development(token, **fields)
        self.state['last_command_seq'] = self.seq
        self.state['business']['development'] = copy.deepcopy(self.dev)
        return wire


class RecoveryTaskTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.objects = RecoveryObjects(self.temp.name)
        self.runner = TaskRunner()
        self.coordinator = DevelopmentRecoveryCoordinator(self.objects, self.runner, self.temp.name)
        self.addCleanup(self.cleanup)

    def cleanup(self):
        self.coordinator.stop()
        self.runner.join(1)

    def start(self):
        self.assertTrue(self.coordinator.start('recover_equipment'))

    def review(self):
        eventually(lambda: self.coordinator.snapshot['status'] == 'READY_FOR_REVIEW')
        path = Path(self.coordinator.snapshot['ledger_path'])
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(),
            self.coordinator.snapshot['reason'].split('=')[1])
        self.objects.info['resolution_path'].parent.mkdir(parents=True, exist_ok=True)
        self.objects.info['resolution_path'].write_bytes(path.read_bytes())
        return path

    def detached_continuation(self):
        self.objects.state['player_refresh'] = copy.deepcopy(self.objects.full_dto)
        capsule = self.objects.freeze_player_refresh(self.objects.token)
        proof = build_equipment_progress_resolution(
            self.objects.info['journal_path'].read_bytes(),
            self.objects.info['original_state_path'].read_bytes(), capsule.read_bytes(),
            self.objects.info['facts'], load_json_bytes(self.objects.info['handler_proof_path'].read_bytes()))
        self.objects.info['resolution_path'].parent.mkdir(parents=True, exist_ok=True)
        self.objects.info['resolution_path'].write_bytes(canonical_bytes(proof))
        workflow = DevelopmentWorkflow(self.objects, self.objects.token, self.temp.name,
            recovery_proof=proof, wait=lambda: None)
        target = EquipmentTarget(1, 5, 'enchant', **proof['remaining_budget'])
        return workflow, target, proof

    def test_known_settled_partial_cannot_restart_same_proof_budget(self):
        workflow, target, proof = self.detached_continuation()
        self.objects.stop_callback = lambda: self.objects.dev['equipment']['candidates'][0]['cost'].update(money=99400)
        first = workflow.equipment(target)
        self.assertEqual((first.status, first.reason), ('BLOCKED', 'resource_budget_exhausted'))
        first_bytes = Path(first.ledger_path).read_bytes()
        first_journal = load_json_bytes(first_bytes)
        self.assertIsNone(first_journal['unresolved'])
        self.assertTrue(any(e['kind'] == 'settled' for e in first_journal['events']))
        self.assertEqual(self.objects.dev['equipment']['candidates'][0]['level'], 2)
        self.objects.dev['equipment']['candidates'][0]['cost']['money'] = 1200
        self.objects.stop_callback = None
        before_commands = copy.deepcopy(self.objects.commands)
        before_journals = set((Path(self.temp.name) / 'development_runs').glob('*.json'))
        second = DevelopmentWorkflow(self.objects, self.objects.token, self.temp.name,
            recovery_proof=proof, wait=lambda: None).equipment(target)
        self.assertEqual(second.status, 'BLOCKED')
        self.assertIn('development_recovery_continuation_already_reserved', second.reason)
        self.assertEqual(self.objects.commands, before_commands)
        self.assertEqual(set((Path(self.temp.name) / 'development_runs').glob('*.json')), before_journals)
        self.assertEqual(Path(first.ledger_path).read_bytes(), first_bytes)
        self.assertEqual(self.objects.info['journal_path'].read_bytes(), self.objects.original_bytes)

    def test_created_only_continuation_reserves_proof_even_without_submission(self):
        workflow, target, proof = self.detached_continuation()
        from dataclasses import asdict
        workflow._begin('d' * 32, 'equipment', asdict(target))
        reserved = workflow._path.read_bytes()
        journal = load_json_bytes(reserved)
        self.assertEqual([e['kind'] for e in journal['events']], ['created'])
        self.assertIsNone(journal['unresolved'])
        fresh = DevelopmentWorkflow(self.objects, self.objects.token, self.temp.name,
            recovery_proof=proof, wait=lambda: None)
        result = fresh.equipment(target)
        self.assertIn('development_recovery_continuation_already_reserved', result.reason)
        self.assertEqual(self.objects.commands, [])
        self.assertEqual(workflow._path.read_bytes(), reserved)
        # Growth is a separate capability and retains the same proof/epoch.
        self.objects.dev['growth'].update(gate='PASS', tasks=[], window_visible=False)
        growth = fresh.close_growth(3)
        self.assertEqual(growth.status, 'DONE', growth.reason)
        self.assertEqual(load_json_bytes(Path(growth.ledger_path).read_bytes())['module'], 'growth')

    def test_same_helper_wait_review_four_carried_steps_completion_then_growth(self):
        self.start()
        eventually(lambda: self.coordinator.snapshot['status'] == 'READY_FOR_REVIEW')
        self.assertTrue(self.objects.has_campaign_owner)
        self.assertFalse(self.objects.released)
        self.assertEqual(self.objects.commands, [])
        self.assertFalse(self.objects.info['resolution_path'].exists())
        self.review()
        eventually(lambda: not self.coordinator.is_active)
        self.runner.join(1)
        self.assertEqual(self.coordinator.snapshot['status'], 'DONE')
        enchants = [c for c in self.objects.commands if c['action'] == 'equipment_enchant']
        self.assertEqual(len(enchants), 4)
        self.assertEqual({(c['max_requests'], c['max_money'], c['max_stone']) for c in enchants}, {(4, 99400, 1000)})
        self.assertEqual(self.objects.refresh_calls, 1)
        self.assertEqual(self.objects.info['journal_path'].read_bytes(), self.objects.original_bytes)
        output = Path(self.temp.name) / 'development_completions' / (ORIGINAL_OPERATION + '.json')
        verify_equipment_completion(load_json_bytes(output.read_bytes()))
        self.assertTrue(self.objects.released)

    def test_unknown_refresh_retains_intent_and_second_run_never_calls_again(self):
        self.objects.refresh_unknown = True
        self.start()
        eventually(lambda: not self.coordinator.is_active)
        self.runner.join(1)
        self.assertEqual(self.coordinator.snapshot['status'], 'UNKNOWN')
        journal = next((Path(self.temp.name) / 'player_refresh_runs').glob('*.json'))
        self.assertIsNotNone(load_json_bytes(journal.read_bytes())['unresolved'])
        self.objects.bridge_state = 'ready'
        self.start()
        eventually(lambda: not self.coordinator.is_active)
        self.runner.join(1)
        self.assertIn('player_refresh_unresolved_requires_review', self.coordinator.snapshot['reason'])
        self.assertEqual(self.objects.refresh_calls, 1)

    def test_capital_waits_without_repeat_then_normal_navigation_can_continue(self):
        self.objects.capital = True
        self.start()
        eventually(lambda: self.coordinator.snapshot['status'] == 'NEEDS_NAVIGATION')
        self.assertTrue(self.objects.has_campaign_owner)
        self.assertEqual(self.objects.commands, [])
        self.assertFalse((self.objects.info['session_dir'] / 'player-refresh-observed.json').exists())
        self.objects.state['campaign'].update(phase='CAMPAIGN', gate='PASS')
        self.objects.state['campaign']['ui_context']['current_root_level'] = 'Campaign'
        self.review()
        eventually(lambda: not self.coordinator.is_active)
        self.assertEqual(self.objects.refresh_calls, 1)
        self.assertEqual(self.coordinator.snapshot['status'], 'DONE')

    def test_pause_and_stop_review_keep_no_business_io_and_release(self):
        self.start()
        eventually(lambda: self.coordinator.snapshot['status'] == 'READY_FOR_REVIEW')
        self.assertTrue(self.coordinator.pause())
        self.review()
        time.sleep(.12)
        self.assertEqual(self.objects.commands, [])
        self.assertTrue(self.objects.has_campaign_owner)
        self.assertTrue(self.coordinator.stop())
        self.runner.join(1)
        self.assertEqual(self.objects.commands, [])
        self.assertTrue(self.objects.released)

    def assert_tampered_publication_blocks(self, mutation):
        self.start()
        eventually(lambda: self.coordinator.snapshot['status'] == 'READY_FOR_REVIEW')
        candidate = Path(self.coordinator.snapshot['ledger_path'])
        if mutation == 'source':
            self.objects.info['journal_path'].write_bytes(self.objects.original_bytes + b' ')
        if mutation == 'epoch':
            self.objects.epoch_bad = True
        self.objects.info['resolution_path'].parent.mkdir(parents=True, exist_ok=True)
        proof = load_json_bytes(candidate.read_bytes())
        if mutation == 'proof':
            proof['remaining_budget']['max_requests'] = 5
        self.objects.info['resolution_path'].write_bytes(canonical_bytes(proof))
        eventually(lambda: not self.coordinator.is_active)
        self.assertEqual(self.coordinator.snapshot['status'], 'BLOCKED')
        self.assertEqual(self.objects.commands, [])

    def test_wrong_published_proof_blocks_before_enchant(self):
        self.assert_tampered_publication_blocks('proof')

    def test_changed_source_blocks_before_enchant(self):
        self.assert_tampered_publication_blocks('source')

    def test_stale_epoch_cannot_use_published_proof(self):
        self.assert_tampered_publication_blocks('epoch')

    def test_other_unresolved_journal_remains_a_blocker(self):
        path = Path(self.temp.name) / 'development_runs' / ('c' * 32 + '.json')
        path.write_text(json.dumps({'unresolved': {'action': 'spirit_train'}}), encoding='utf-8')
        self.start()
        self.review()
        eventually(lambda: not self.coordinator.is_active)
        self.assertIn('development_unresolved_requires_review', self.coordinator.snapshot['reason'])
        self.assertEqual(self.objects.commands, [])

    def test_stop_after_durable_intent_prevents_refresh_io(self):
        saved = self.coordinator._save_sync
        def save_then_stop(path, value):
            saved(path, value)
            if value['events'][-1]['kind'] == 'intent':
                self.coordinator._stopping = True
        self.coordinator._save_sync = save_then_stop
        observed = self.coordinator._refresh(SimpleNamespace(cancelled=False), self.objects.token)
        self.assertIsNone(observed)
        self.assertEqual(self.objects.refresh_calls, 0)
        path = next((Path(self.temp.name) / 'player_refresh_runs').glob('*.json'))
        self.assertIsNone(load_json_bytes(path.read_bytes())['unresolved'])

    def test_epoch_change_after_first_own_step_blocks_second_request(self):
        self.objects.stop_callback = lambda: setattr(self.objects, 'epoch_bad', True)
        self.start()
        self.review()
        eventually(lambda: not self.coordinator.is_active)
        self.assertEqual(self.coordinator.snapshot['status'], 'UNKNOWN')
        self.assertEqual([c['action'] for c in self.objects.commands], ['equipment_enchant'])
        self.assertEqual(self.objects.info['journal_path'].read_bytes(), self.objects.original_bytes)

    def test_normal_workflow_needs_completion_before_future_helper_can_skip_history(self):
        self.start()
        self.review()
        eventually(lambda: not self.coordinator.is_active)
        self.objects.stopping = False
        proof = load_json_bytes(self.objects.info['resolution_path'].read_bytes())
        # Simulate the root port's completion-admitted fresh helper contract.
        # This helper has no historical refresh epoch of its own.
        self.objects.token = SimpleNamespace(context_key='future-helper/new-character-epoch')
        self.objects.state['player_refresh'] = {'schema': 1, 'status': 'IDLE'}
        self.objects.check_development_resolution = lambda token: proof
        # A fresh ordinary workflow may use fresh goals after the entire old
        # allowance was positively completed and its canonical seal is valid.
        fresh = DevelopmentWorkflow(self.objects, self.objects.token, self.temp.name, wait=lambda: None)
        result = fresh.equipment(EquipmentTarget(1, 5, max_requests=1))
        self.assertEqual(result.status, 'DONE', result.reason)
        completion = Path(self.temp.name) / 'development_completions' / (ORIGINAL_OPERATION + '.json')
        completion.unlink()
        fresh = DevelopmentWorkflow(self.objects, self.objects.token, self.temp.name, wait=lambda: None)
        result = fresh.equipment(EquipmentTarget(1, 5))
        self.assertEqual(result.status, 'BLOCKED')
        self.assertIn('development_unresolved_requires_review', result.reason)


if __name__ == '__main__':
    unittest.main()
