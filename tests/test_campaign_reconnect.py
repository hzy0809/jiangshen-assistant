"""Reconnect recovery invariants. No GUI, host, network or live game is used."""
from dataclasses import replace
import copy
import json
from pathlib import Path
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from jiangshen_assistant.bridge_client import BridgeClient, BridgeError
from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.campaign import (Campaign, CampaignError, Checkpoint, ConsumedResult,
    Intent, Mode, Observation, Outcome, Result)
from jiangshen_assistant.campaign_store import CampaignStore
from jiangshen_assistant.campaign_task import CampaignCoordinator
from jiangshen_assistant.core import TaskRunner
from jiangshen_assistant.objects import ObjectController
from tests import test_campaign_task as campaign_fakes
from tests.test_development_adapter import development
from tests.test_objects import eventually


RUN = 'f' * 32
OLD = dict(pid=123, creation=456, nonce='a' * 32, generation=2, character_id='789')
NEW = {**OLD, 'nonce': 'b' * 32, 'generation': 0}


def context(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'))


def clean_attempt():
    return dict(status='NONE', attempt_id='', character_id='', command_seq=0,
        campaign_id=0, troop_id=0, report_id='0', outcome='UNKNOWN', evidence='not_started')


class ReconnectObjects(campaign_fakes.FakeObjects):
    def __init__(self, key=None):
        super().__init__(['LOSS'])
        self.context_key = context(NEW) if key is None else key
        self.snap['attempt'] = clean_attempt()
        self.snap['general_tutorial'] = dict(status='NONE', network_pending=False)
        self.proof_calls = 0
        self.proof_error = None
        self.proof = dict(ready=True, character_id=NEW['character_id'], pending_attempt=False,
            pending_development=False, development_operations=dict(growth='IDLE', equipment='IDLE', spirit='IDLE'))
        self.before_submit = lambda: None

    def acquire_campaign(self, run_id):
        super().acquire_campaign(run_id)
        self.owner = SimpleNamespace(context_key=self.context_key)
        return self.owner

    def confirm_campaign_reconnect(self, token):
        assert token is self.owner
        self.proof_calls += 1
        if self.proof_error:
            raise self.proof_error
        return dict(context_key=token.context_key, **copy.deepcopy(self.proof))

    def submit_campaign(self, token, action, **kwargs):
        self.before_submit()
        return super().submit_campaign(token, action, **kwargs)


class ReconnectTests(unittest.TestCase):
    def seed(self, root, **changes):
        runs = Path(root) / 'campaign_runs'
        runs.mkdir()
        self.path = runs / (RUN + '.json')
        self.state = Checkpoint(RUN, context(OLD), losses=2, mode=Mode.PAUSED,
            reason='user_pause', consumed=(ConsumedResult('1' * 32, '101', Outcome.LOSS),
                ConsumedResult('2' * 32, '102', Outcome.LOSS)), last_revision=40,
            guide_step='old-step', completed_guides=('reviewed-step',))
        self.state = replace(self.state, **changes)
        CampaignStore(self.path).initialize(self.state)
        self.before = self.path.read_bytes()

    def coordinator(self, objects, root):
        runner = TaskRunner()
        c = CampaignCoordinator(objects, runner, Path(root), poll_seconds=.002,
            stop_timeout=.025, battle_timeout=.04)
        self.addCleanup(lambda: (c.close(), runner.join(1)))
        return c, runner

    def run_rejected(self, objects, root):
        c, runner = self.coordinator(objects, root)
        self.assertFalse(c.start())
        self.assertTrue(c.resume())
        eventually(lambda: not c.is_active)
        runner.join(.2)
        self.assertEqual(objects.commands, [])
        self.assertEqual(self.path.read_bytes(), self.before)
        self.assertTrue(c.is_paused)
        self.assertEqual(c.snapshot['mode'], 'blocked')
        return c

    def test_explicit_continue_rebinds_before_first_command_and_third_loss_stops(self):
        with tempfile.TemporaryDirectory() as root:
            self.seed(root)
            objects = ReconnectObjects()
            persisted = []
            def before_command():
                state = CampaignStore(self.path).load()
                persisted.append(state)
                self.assertEqual(state.context_key, context(NEW))
                self.assertEqual(state.run_id, RUN)
                self.assertEqual(state.losses, 2)
                self.assertEqual(state.consumed, self.state.consumed)
                self.assertEqual(state.completed_guides, self.state.completed_guides)
            objects.before_submit = before_command
            c, runner = self.coordinator(objects, root)
            self.assertEqual(self.path.read_bytes(), self.before)
            self.assertFalse(c.start())
            self.assertEqual(objects.commands, [])
            self.assertTrue(c.resume())
            eventually(lambda: not c.is_active)
            runner.join(.2)
            state = CampaignStore(self.path).load()
            self.assertTrue(persisted)
            self.assertEqual(objects.proof_calls, 1)
            self.assertEqual(state.run_id, RUN)
            self.assertEqual(state.context_key, context(NEW))
            self.assertEqual(state.losses, 3)
            self.assertEqual(state.mode, Mode.LOSS_LIMIT)
            self.assertEqual(state.consumed[:2], self.state.consumed)
            self.assertEqual(len(state.consumed), 3)
            self.assertEqual(state.completed_guides, self.state.completed_guides)
            self.assertEqual(sum(action == 'start' for action, _ in objects.commands), 1)
            self.assertEqual(len(list(self.path.parent.glob('*.json'))), 1)

    def test_changed_stable_owner_never_changes_checkpoint(self):
        for key, value in (('pid', 124), ('creation', 457), ('character_id', '790')):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as root:
                self.seed(root)
                objects = ReconnectObjects(context({**NEW, key: value}))
                c = self.run_rejected(objects, root)
                self.assertIn('stable_owner_changed', c.snapshot['reason'])
                self.assertEqual(objects.proof_calls, 0)

    def test_malformed_or_unknown_old_and_new_contexts_never_change_checkpoint(self):
        bad = ['legacy/context', '[]', 'null', '{', context({**NEW, 'extra': 1}),
            context({k: v for k, v in NEW.items() if k != 'pid'}),
            context(NEW)[:-1] + ',"pid":123}']
        bad += [context({**NEW, key: value}) for key, value in (
            ('pid', True), ('pid', 0), ('creation', -1), ('creation', '456'),
            ('generation', True), ('generation', -1), ('nonce', 'A' * 32), ('nonce', ''),
            ('character_id', '0789'), ('character_id', '0'), ('character_id', 789),
            ('character_id', '\u0667\u0668\u0669'))]
        for side in ('old', 'new'):
            for value in bad:
                with self.subTest(side=side, value=value), tempfile.TemporaryDirectory() as root:
                    self.seed(root, **({'context_key': value} if side == 'old' else {}))
                    self.run_rejected(ReconnectObjects(value if side == 'new' else None), root)

    def test_pending_and_nonpaused_checkpoints_are_never_rebound(self):
        pending = Intent('3' * 32, 'challenge', 40, stage_key='10:20')
        for changes in ({'pending': pending}, {'pending': replace(pending, uncertain=True), 'mode': Mode.BLOCKED},
                        {'mode': Mode.ACTIVE}, {'mode': Mode.BLOCKED}):
            with self.subTest(changes=changes), tempfile.TemporaryDirectory() as root:
                self.seed(root, **changes)
                objects = ReconnectObjects()
                self.run_rejected(objects, root)
                self.assertEqual(objects.proof_calls, 0)

    def test_native_pending_missing_and_malformed_none_are_not_clean(self):
        attempts = [None, {}, {'status': 'NONE'}, *[dict(status=status) for status in
            ('WAITING', 'SUBMITTED', 'UNKNOWN', 'OTHER')], {**clean_attempt(), 'command_seq': False},
            {**clean_attempt(), 'attempt_id': '3' * 32}, {**clean_attempt(), 'outcome': 'LOSS'}]
        for attempt in attempts:
            with self.subTest(attempt=attempt), tempfile.TemporaryDirectory() as root:
                self.seed(root)
                objects = ReconnectObjects()
                objects.snap['attempt'] = attempt
                self.run_rejected(objects, root)

    def test_settled_must_be_valid_and_exactly_already_consumed(self):
        settled = dict(status='SETTLED', attempt_id='2' * 32, character_id=OLD['character_id'],
            command_seq=7, campaign_id=10, troop_id=20, report_id='102', outcome='LOSS',
            evidence='captured_authoritative_receipt_applied')
        for changes in ({'attempt_id': '4' * 32}, {'report_id': '103'}, {'outcome': 'WIN'},
                        {'character_id': '790'}, {'evidence': ''}, {'command_seq': True}):
            with self.subTest(changes=changes), tempfile.TemporaryDirectory() as root:
                self.seed(root)
                objects = ReconnectObjects()
                objects.snap['attempt'] = {**settled, **changes}
                self.run_rejected(objects, root)
        with tempfile.TemporaryDirectory() as root:
            self.seed(root)
            objects = ReconnectObjects()
            objects.snap['attempt'] = settled
            c, runner = self.coordinator(objects, root)
            self.assertTrue(c.resume())
            eventually(lambda: not c.is_active)
            runner.join(.2)
            self.assertEqual(CampaignStore(self.path).load().mode, Mode.LOSS_LIMIT)
            self.assertEqual(sum(a == 'start' for a, _ in objects.commands), 1)

    def test_general_network_pending_and_uncertain_observation_are_rejected(self):
        mutations = [lambda s: s.update(general_tutorial={}),
            lambda s: s['general_tutorial'].update(network_pending=True),
            lambda s: s['general_tutorial'].update(status='WAITING_FORMATION'),
            lambda s: s['general_tutorial'].update(status='UNKNOWN'),
            lambda s: s.update(gate='WAITING_NETWORK'), lambda s: s.update(available=False),
            lambda s: s.update(revision=True)]
        for mutate in mutations:
            with self.subTest(mutate=mutate), tempfile.TemporaryDirectory() as root:
                self.seed(root)
                objects = ReconnectObjects()
                mutate(objects.snap)
                self.run_rejected(objects, root)

    def test_business_guard_or_missing_api_does_not_rebind(self):
        for mutate in (lambda o: setattr(o, 'proof_error', BridgeError('prior_unresolved')),
                       lambda o: o.proof.update(pending_attempt=True),
                       lambda o: o.proof.update(pending_development=True),
                       lambda o: o.proof.update(character_id='790'),
                       lambda o: setattr(o, 'confirm_campaign_reconnect', None)):
            with self.subTest(mutate=mutate), tempfile.TemporaryDirectory() as root:
                self.seed(root)
                objects = ReconnectObjects()
                mutate(objects)
                self.run_rejected(objects, root)

    def test_unresolved_or_unreadable_development_journal_blocks_before_rebind(self):
        for raw in ('{"unresolved":{"operation_id":"x"}}', 'broken', '{}'):
            with self.subTest(raw=raw), tempfile.TemporaryDirectory() as root:
                self.seed(root)
                journals = Path(root) / 'development_runs'
                journals.mkdir()
                journal = journals / 'old.json'
                journal.write_text(raw)
                self.run_rejected(ReconnectObjects(), root)
                self.assertEqual(journal.read_text(), raw)

    def test_rebind_store_failure_never_submits_or_changes_old_context(self):
        with tempfile.TemporaryDirectory() as root:
            self.seed(root)
            objects = ReconnectObjects()
            c, runner = self.coordinator(objects, root)
            with patch.object(CampaignStore, 'save', side_effect=OSError('fsync failed')):
                self.assertTrue(c.resume())
                eventually(lambda: not c.is_active)
                runner.join(.2)
            self.assertEqual(objects.commands, [])
            self.assertEqual(self.path.read_bytes(), self.before)
            self.assertEqual(c.snapshot['mode'], 'blocked')
            self.assertIn('persistence_error', c.snapshot['reason'])

    def test_domain_rebind_stays_paused_and_preserves_business_facts(self):
        with tempfile.TemporaryDirectory() as root:
            self.seed(root)
            campaign = Campaign.open(CampaignStore(self.path))
            campaign.rebind_context(context(NEW))
            state = campaign.state
            self.assertEqual(state, replace(self.state, context_key=context(NEW), last_revision=-1,
                reason='reconnected_requires_resume'))
            campaign.resume(Observation(RUN, context(NEW), 1))
            with self.assertRaises(CampaignError):
                campaign.rebind_context(context(OLD))
            intent = campaign.prepare(Observation(RUN, context(NEW), 2, ('10:20',)), '3' * 32)
            campaign.mark_submitted(intent.intent_id, intent.intent_id)
            campaign.record_result(Result(RUN, context(NEW), intent.intent_id, '103', Outcome.LOSS, True))
            self.assertEqual(campaign.state.mode, Mode.LOSS_LIMIT)
            self.assertEqual(campaign.state.losses, 3)


class ReconnectAdmissionTests(unittest.TestCase):
    def client(self):
        client = BridgeClient.__new__(BridgeClient)
        client._request_lock = threading.Lock()
        client._terminal = client._unknown = False
        client._prior_business = None
        observed = development()
        client._last_state = dict(development=observed, business=dict(pending_attempt=False,
            pending_development=False, development=copy.deepcopy(observed)))
        return client

    def test_bridge_status_returns_detached_facts_without_writing_or_requests(self):
        client = self.client()
        before = copy.deepcopy(client._last_state)
        proof = client.business_admission_status()
        self.assertTrue(proof['ready'])
        self.assertEqual(client._last_state, before)
        proof['development_operations']['growth'] = 'UNKNOWN'
        self.assertEqual(client.business_admission_status()['development_operations']['growth'], 'IDLE')

    def test_bridge_pending_flags_and_development_unknown_are_never_ready(self):
        for module in ('growth', 'equipment', 'spirit'):
            for status in ('WAITING', 'UNKNOWN'):
                with self.subTest(module=module, status=status):
                    client = self.client()
                    for value in (client._last_state['development'], client._last_state['business']['development']):
                        value[module]['operation'].update(status=status, operation_id='c' * 32,
                            action='growth_open' if module == 'growth' else 'equipment_open' if module == 'equipment' else 'spirit_open',
                            sequence=1, expected_revision=1)
                    self.assertTrue(bridge._development_valid(client._last_state['development']))
                    with self.assertRaises(BridgeError):
                        client.business_admission_status()
        for key in ('pending_attempt', 'pending_development'):
            for value in (True, None, 0):
                with self.subTest(key=key, value=value):
                    client = self.client()
                    client._last_state['business'][key] = value
                    with self.assertRaises(BridgeError):
                        client.business_admission_status()

    def test_missing_mismatched_development_and_unsettled_command_fail_closed(self):
        for mutate in (lambda c: c._last_state.pop('development'),
                       lambda c: c._last_state['business'].pop('development'),
                       lambda c: setattr(c, '_terminal', True),
                       lambda c: setattr(c, '_unknown', True)):
            with self.subTest(mutate=mutate):
                client = self.client()
                mutate(client)
                with self.assertRaises(BridgeError):
                    client.business_admission_status()
        for status in ('QUEUED', 'UNKNOWN'):
            client = self.client()
            for value in (client._last_state['development'], client._last_state['business']['development']):
                value['last_command'] = dict(sequence=1, action='growth_open', operation_id='c' * 32,
                    status=status, reason='fixture')
            self.assertTrue(bridge._development_valid(client._last_state['development']))
            with self.assertRaises(BridgeError):
                client.business_admission_status()

    def test_prior_unresolved_guard_is_checked_without_rewriting_or_resolution(self):
        client = self.client()
        client._prior_business = {'business': {'pending_attempt': True}}
        before = copy.deepcopy(client._prior_business)
        with patch.object(client, '_business_fact_resolved', return_value=False) as resolve:
            with self.assertRaises(BridgeError):
                client.business_admission_status()
            resolve.assert_called_once()
        self.assertEqual(client._prior_business, before)
        with patch.object(client, '_business_fact_resolved', return_value=True):
            self.assertTrue(client.business_admission_status()['ready'])
        self.assertEqual(client._prior_business, before)
        client._prior_business = {'business': {'pending_attempt': False, 'pending_development': True}}
        with self.assertRaises(BridgeError):
            client.business_admission_status()

    def test_object_port_checks_owner_and_role_and_returns_detached_proof(self):
        # Exercise the real method using just its documented serialized port.
        token = SimpleNamespace(context_key=context(NEW))
        client = SimpleNamespace(business_admission_status=lambda: dict(ready=True, character_id=NEW['character_id']))
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.Lock(), _client=client,
            _campaign_check_locked=lambda candidate: self.assertIs(candidate, token))
        proof = ObjectController.confirm_campaign_reconnect(owner, token)
        self.assertEqual(proof['context_key'], token.context_key)
        client.business_admission_status = lambda: dict(ready=True, character_id='different')
        with self.assertRaises(BridgeError):
            ObjectController.confirm_campaign_reconnect(owner, token)


if __name__ == '__main__':
    unittest.main()
