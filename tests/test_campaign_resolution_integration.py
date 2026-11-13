"""Admission and restart checks for a separately disposed, still UNKNOWN run."""
import copy
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.campaign_recovery import canonical_bytes, prepare_resolution
from jiangshen_assistant.campaign_task import CampaignCoordinator
from jiangshen_assistant.mono_runtime import ProcessIdentity
from tests.test_campaign_recovery import proof_inputs


class ResolutionIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = Path(self.temp.name)
        self.data = self.project / 'data'
        self.checkpoint, self.guard, self.before, self.after = proof_inputs()
        self.proof = prepare_resolution(self.checkpoint, self.guard, self.before, self.after)
        self.run_path = self.data / 'campaign_runs' / (self.checkpoint['run_id'] + '.json')
        self.proof_path = self.data / 'campaign_resolutions' / self.run_path.name
        self.run_path.parent.mkdir(parents=True)
        self.run_path.write_bytes(canonical_bytes(self.checkpoint))
        self.proof_path.parent.mkdir()
        self.identity = ProcessIdentity(2648, 134378768910968977, 10, 20)
        self.client = bridge.BridgeClient(self.project, self.identity, lambda: None)
        self.client.business_guard_path.parent.mkdir(parents=True)
        self.client.business_guard_path.write_bytes(canonical_bytes(self.guard))
        self.client._prior_business = bridge._merge_pending_business(self.guard)

    def publish(self, proof=None):
        self.proof_path.write_bytes(canonical_bytes(self.proof if proof is None else proof))

    def test_only_exact_disposed_fact_is_admitted_and_guard_is_preserved(self):
        original_guard = self.client.business_guard_path.read_bytes()
        original_run = self.run_path.read_bytes()
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()
        self.publish()
        self.client._check_campaign_admission()
        self.assertEqual(self.client._prior_business['business']['attempt']['status'], 'UNKNOWN')
        self.assertEqual(self.client.business_guard_path.read_bytes(), original_guard)
        self.assertEqual(self.run_path.read_bytes(), original_run)
        other = copy.deepcopy(self.guard)
        other['session_id'] = 'e' * 32
        other['business']['attempt']['attempt_id'] = 'd' * 32
        self.client._prior_business = bridge._merge_pending_business(self.guard, other)
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()
        self.assertEqual(self.client._seq, 0)

    def test_conflict_stale_checkpoint_malformed_proof_and_other_process_block(self):
        self.publish()
        self.client._prior_business['business']['attempt']['evidence'] = 'different_receipt'
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()
        self.client._prior_business = bridge._merge_pending_business(self.guard)
        original = self.run_path.read_bytes()
        self.run_path.write_bytes(original + b'\n')
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()
        self.run_path.write_bytes(original)
        self.proof_path.write_text('{"version":1}', encoding='utf-8')
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()
        self.publish()
        self.client.identity = ProcessIdentity(2648, self.identity.creation_time + 1, 10, 20)
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()

    def test_disposition_never_rewrites_run_or_starts_it_automatically(self):
        class Runner:
            is_active = False
            calls = 0

            def start(self, *args, **kwargs):
                self.calls += 1
                return True

        runner = Runner()
        old = CampaignCoordinator(SimpleNamespace(), runner, self.data)
        self.assertTrue(old.is_paused)
        self.assertFalse(old.start())
        self.publish()
        original = self.run_path.read_bytes()
        current = CampaignCoordinator(SimpleNamespace(), runner, self.data)
        self.assertFalse(current.is_paused)
        self.assertFalse(current.resume())
        self.assertEqual(runner.calls, 0)
        snapshot = current.snapshot
        self.assertEqual(snapshot['reason'], 'target_already_cleared')
        self.assertEqual(snapshot['historical_outcome'], 'UNKNOWN')
        self.assertIsNotNone(snapshot['historical_pending'])
        self.assertIsNone(snapshot['pending'])
        self.assertEqual(snapshot['losses'], 0)
        self.assertEqual(snapshot['consumed'], [])
        self.assertEqual(self.run_path.read_bytes(), original)
        self.assertTrue(current.start())
        self.assertEqual(runner.calls, 1)

    def test_invalid_resolution_keeps_the_old_run_in_recovery(self):
        proof = copy.deepcopy(self.proof)
        proof['historical_outcome'] = 'WIN'
        self.publish(proof)
        current = CampaignCoordinator(SimpleNamespace(), SimpleNamespace(is_active=False), self.data)
        self.assertTrue(current.is_paused)
        self.assertFalse(current.start())
        self.assertEqual(current.snapshot['reason'], 'restart_requires_review')


if __name__ == '__main__':
    unittest.main()
