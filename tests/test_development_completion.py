import copy
from pathlib import Path
import tempfile
import unittest

from jiangshen_assistant.campaign_recovery import canonical_bytes, load_json_bytes
from jiangshen_assistant.core import TaskRunner
from jiangshen_assistant.development_completion import build_equipment_completion, verify_equipment_completion
from jiangshen_assistant.development_recovery import ORIGINAL_OPERATION, ResolutionError
from jiangshen_assistant.development_recovery_task import DevelopmentRecoveryCoordinator
from tests.test_development_recovery_task import RecoveryObjects
from tests.test_objects import eventually


class CompletionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        objects = RecoveryObjects(cls.temp.name)
        runner = TaskRunner()
        coordinator = DevelopmentRecoveryCoordinator(objects, runner, cls.temp.name)
        try:
            assert coordinator.start('recover_equipment')
            eventually(lambda: coordinator.snapshot['status'] == 'READY_FOR_REVIEW')
            candidate = Path(coordinator.snapshot['ledger_path'])
            objects.info['resolution_path'].parent.mkdir(parents=True, exist_ok=True)
            objects.info['resolution_path'].write_bytes(candidate.read_bytes())
            eventually(lambda: not coordinator.is_active)
            runner.join(1)
            assert coordinator.snapshot['status'] == 'DONE', coordinator.snapshot
            path = Path(cls.temp.name) / 'development_completions' / (ORIGINAL_OPERATION + '.json')
            cls.proof = load_json_bytes(path.read_bytes())
            cls.resolution = cls.proof['resolution']
            cls.journal = load_json_bytes((Path(cls.temp.name) / 'development_runs' /
                (cls.proof['continuation_operation_id'] + '.json')).read_bytes())
            cls.final = load_json_bytes((Path(cls.temp.name) / 'development_completion_sources' /
                (cls.resolution['identity']['refresh_session_id'] + '-equipment-final.json')).read_bytes())
        finally:
            coordinator.stop()
            runner.join(1)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def test_positive_completion_rebuilds_without_current_live_epoch(self):
        proof = build_equipment_completion(self.resolution, canonical_bytes(self.journal), canonical_bytes(self.final))
        verify_equipment_completion(proof, resolution=self.resolution,
            continuation_journal_bytes=canonical_bytes(self.journal), final_state_bytes=canonical_bytes(self.final))
        self.assertEqual(proof['consumed_continuation_budget'], {'requests': 4, 'money': 4800, 'stone': 0})
        self.assertEqual(proof['original_outcome'], 'UNKNOWN')
        self.assertFalse(proof['retries_original'])

    def test_seal_tampering_and_changed_external_bytes_fail(self):
        for key, value in [('completed_level', 6), ('original_outcome', 'OBSERVED'),
            ('resets_budget', True), ('resolution_sha256', '0' * 64), ('positive_receipts', []),
            ('extra', True)]:
            with self.subTest(key=key):
                changed = copy.deepcopy(self.proof)
                changed[key] = value
                with self.assertRaises(ResolutionError):
                    verify_equipment_completion(changed)
        with self.assertRaises(ResolutionError):
            verify_equipment_completion(self.proof, continuation_journal_bytes=canonical_bytes(self.journal) + b' ')

    def test_local_target_without_four_exact_positive_receipts_is_insufficient(self):
        changes = []
        journal = copy.deepcopy(self.journal)
        next(e for e in journal['events'] if e['kind'] == 'observation' and
            e['observed']['operation']['action'] == 'equipment_enchant')['observed']['operation']['receipt_opcode'] = 0
        changes.append(journal)
        journal = copy.deepcopy(self.journal)
        next(e for e in journal['events'] if e['kind'] == 'observation')['observed']['operation']['sequence'] += 1
        changes.append(journal)
        journal = copy.deepcopy(self.journal)
        journal['events'] = [e for e in journal['events'] if e['kind'] != 'settled']
        changes.append(journal)
        journal = copy.deepcopy(self.journal)
        journal['goal']['max_requests'] = 5
        changes.append(journal)
        journal = copy.deepcopy(self.journal)
        journal['unresolved'] = {'action': 'equipment_enchant'}
        changes.append(journal)
        journal = copy.deepcopy(self.journal)
        next(e for e in journal['events'] if e['kind'] == 'intent')['policy_state']['_money'] = 0
        changes.append(journal)
        for index, changed in enumerate(changes):
            with self.subTest(index=index), self.assertRaises(ResolutionError):
                build_equipment_completion(self.resolution, canonical_bytes(changed), canonical_bytes(self.final))

    def test_wrong_helper_epoch_owner_or_pending_capability_cannot_seal(self):
        for key in ('nonce', 'player_refresh', 'owner', 'pending', 'final_receipt'):
            final = copy.deepcopy(self.final)
            if key == 'nonce':
                final['nonce'] = 'b' * 32
            elif key == 'player_refresh':
                final['player_refresh']['receipt_hash'] = '0' * 64
            elif key == 'owner':
                final['development']['equipment']['candidates'][0]['owner_id'] = 23
            elif key == 'pending':
                final['development']['spirit']['operation']['status'] = 'WAITING'
            else:
                final['development']['equipment']['operation']['operation_id'] = 'c' * 32
            with self.subTest(key=key), self.assertRaises(ResolutionError):
                build_equipment_completion(self.resolution, canonical_bytes(self.journal), canonical_bytes(final))


if __name__ == '__main__':
    unittest.main()
