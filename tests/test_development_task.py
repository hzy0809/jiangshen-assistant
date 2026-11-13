import copy
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from types import SimpleNamespace

from jiangshen_assistant.core import TaskRunner
from jiangshen_assistant.campaign_task import CampaignCoordinator
from jiangshen_assistant.development import EquipmentTarget, SpiritTarget
from jiangshen_assistant.development_task import DevelopmentCoordinator, DevelopmentWorkflow
from tests.test_development import equipment, spirit
from tests.test_campaign_task import FakeObjects as CampaignObjects
from tests.test_objects import eventually


def quest(qid, kind=3, met=False, finished=0, claimed=0):
    return dict(quest_id=qid, isfinish=finished, isgot=claimed, condition_met=met,
        template=dict(quest_id=qid, type=kind, param0=2, param1=1, exp=10, money=20))


class DevObjects:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.bridge_state, self.is_busy, self.has_campaign_owner = 'ready', False, False
        self.token = SimpleNamespace(context_key='session/character')
        self.commands, self.observations = [], []
        self.stopping = self.paused = False
        self.unknown = self.no_result = self.bad_receipt = self.stop_after = False
        self.stop_callback = None
        self.released = False
        self.seq = 0
        self.dev = dict(schema=1, revision=1, gate='PASS', equipment=equipment(desired_level=2), spirit=spirit(desired_level=2),
            growth=dict(schema=1, gate='PASS', tasks=[quest(42, met=True)], selected_quest_id=42,
                ui_selected_quest_id=0, window_visible=False, window_enabled=True, show_window=True,
                available_action='growth_check', operation=dict(status='IDLE')))
        self.dev['equipment']['candidates'][0]['in_current_formation'] = True
        self.dev['equipment']['ui'].update(reinforce=dict(instance_id=10, visible=True), equipment=dict(instance_id=11, visible=False))

    def acquire_campaign(self, run_id):
        self.has_campaign_owner = True
        return self.token

    def release_campaign(self, token, cleanup=True):
        self.released, self.has_campaign_owner = True, False
        self.bridge_state = 'disconnected'

    def pause_campaign(self, token):
        self.paused = True
        return True

    def resume_campaign(self, token):
        self.paused = False
        return True

    def stop_campaign(self, token):
        self.stopping = True
        return True

    def observe_development(self, token, fields=None):
        self.observations.append(copy.deepcopy(fields))
        self.dev['revision'] += 1
        if fields:
            module = fields['action'].split('_')[0]
            snap = self.dev[module]
            if module == 'equipment':
                snap.update(target_id=fields['target_id'], desired_level=fields['desired_level'], mode=fields['mode'])
            elif module == 'spirit':
                snap['desired_level'] = fields['desired_level']
        return dict(context_key=token.context_key, development=copy.deepcopy(self.dev), stopping=self.stopping)

    def submit_development(self, token, **fields):
        # This is the decisive write-before-I/O boundary, not a mock of policy.
        path = self.directory / 'development_runs' / (fields['operation_id'] + '.json')
        journal = json.loads(path.read_text(encoding='utf-8'))
        assert journal['unresolved'] == fields
        assert journal['events'][-1]['kind'] == 'intent'
        assert not self.stopping and not self.paused
        self.commands.append(copy.deepcopy(fields))
        if self.unknown:
            raise TimeoutError('lost_ack')
        self.seq += 1
        action = fields['action']
        module = action.split('_')[0]
        snap = self.dev[module]
        op = {key: fields[key] for key in ('action', 'operation_id', 'expected_revision', 'target_id', 'desired_level', 'mode')}
        op.update(status='WAITING' if self.no_result else 'OBSERVED', sequence=self.seq, server='PASS')
        if self.no_result:
            pass
        elif action == 'equipment_enchant':
            snap['candidates'][0]['level'] += 1
            level = snap['candidates'][0]['level']
            snap['available_action'] = 'equipment_close' if level >= fields['desired_level'] else 'equipment_enchant'
            op.update(receipt_opcode=1104, receipt_id=999 if self.bad_receipt else fields['target_id'],
                receipt_level=level, server_progress_observed=True)
        elif action == 'equipment_close':
            if snap['ui']['reinforce']['visible']:
                snap['ui']['reinforce']['visible'] = False
                snap['ui']['equipment']['visible'] = True
                snap['available_action'] = 'equipment_close'
            else:
                snap['ui']['equipment']['visible'] = False
                snap['available_action'] = 'equipment_open'
        elif action == 'growth_check':
            snap['tasks'][0]['isfinish'] = 1
            snap['available_action'] = 'growth_open'
            op['receipt_opcode'] = 810
        elif action == 'growth_open':
            snap.update(window_visible=True, ui_selected_quest_id=fields['target_id'], available_action='growth_claim')
        elif action == 'growth_claim':
            snap['tasks'][0]['isgot'] = 1
            snap['tasks'].append(quest(91, kind=8))
            snap.update(selected_quest_id=91, available_action='')
            op.update(receipt_opcode=811, receipt_result=0)
        elif action == 'growth_close':
            snap.update(window_visible=False, ui_selected_quest_id=0, available_action='growth_open')
        snap['operation'] = op
        if self.stop_callback:
            self.stop_callback()
        receipt = dict(sequence=self.seq, action=action, operation_id=fields['operation_id'], status='INVOKED', reason='')
        return dict(requested_seq=self.seq, ack=dict(command_seq=self.seq, status='COMPLETED',
            result=dict(development=dict(last_command=receipt))))


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.objects = DevObjects(self.temp.name)
        self.stopped = False
        self.workflow = DevelopmentWorkflow(self.objects, self.objects.token, self.temp.name,
            wait=lambda: None, timeout=.015, stopped=lambda: self.stopped)

    def target(self, **changes):
        values = dict(equipment_id=7, desired_level=2, max_requests=1, max_money=10, max_stone=2)
        values.update(changes)
        return EquipmentTarget(**values)

    def journal(self, result):
        return json.loads(Path(result.ledger_path).read_text(encoding='utf-8'))

    def test_typed_observe_then_intent_then_exact_result_and_normal_close(self):
        result = self.workflow.equipment(self.target())
        self.assertEqual((result.status, result.evidence), ('DONE', 'SERVER_PROGRESS_OBSERVED'))
        self.assertEqual([c['action'] for c in self.objects.commands], ['equipment_enchant', 'equipment_close', 'equipment_close'])
        self.assertIsNone(self.objects.observations[0])
        self.assertEqual(self.objects.observations[1]['action'], 'equipment_observe')
        journal = self.journal(result)
        self.assertIsNone(journal['unresolved'])
        self.assertEqual(sum(e['kind'] == 'intent' for e in journal['events']), 3)

    def test_unknown_ack_retains_intent_and_fresh_instance_cannot_retry(self):
        self.objects.unknown = True
        first = self.workflow.equipment(self.target())
        self.assertEqual(first.status, 'UNKNOWN')
        self.assertEqual(self.journal(first)['unresolved']['action'], 'equipment_enchant')
        fresh = DevelopmentWorkflow(self.objects, self.objects.token, self.temp.name, wait=lambda: None)
        second = fresh.equipment(self.target())
        self.assertEqual(second.status, 'BLOCKED')
        self.assertIn('development_unresolved_requires_review', second.reason)
        self.assertEqual(len(self.objects.commands), 1)

    def test_ack_is_not_completion_and_pending_never_resubmitted(self):
        self.objects.no_result = True
        result = self.workflow.equipment(self.target())
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'development_observation_timeout'))
        self.assertEqual(len(self.objects.commands), 1)
        self.assertIsNotNone(self.journal(result)['unresolved'])

    def test_wrong_server_receipt_blocks_further_commands(self):
        self.objects.bad_receipt = True
        result = self.workflow.equipment(self.target())
        self.assertEqual((result.status, result.reason), ('BLOCKED', 'equipment_progress_receipt_unverified'))
        self.assertEqual(len(self.objects.commands), 1)
        self.assertIsNotNone(self.journal(result)['unresolved'])

    def test_stop_after_invocation_does_not_even_close_ui(self):
        self.objects.stop_callback = lambda: setattr(self, 'stopped', True)
        result = self.workflow.equipment(self.target())
        self.assertEqual(result.status, 'STOPPED')
        self.assertEqual([c['action'] for c in self.objects.commands], ['equipment_enchant'])
        self.assertIsNotNone(self.journal(result)['unresolved'])

    def test_stop_before_first_step_has_no_io(self):
        self.stopped = True
        self.assertEqual(self.workflow.equipment(self.target()).status, 'STOPPED')
        self.assertEqual(self.objects.observations, [])
        self.assertEqual(self.objects.commands, [])

    def test_resource_budget_blocks_before_consumption(self):
        result = self.workflow.equipment(self.target(max_money=9))
        self.assertEqual(result.reason, 'resource_budget_exhausted')
        self.assertEqual(self.objects.commands, [])

    def test_spirit_premium_fallback_and_zero_fruit_never_train(self):
        self.objects.dev['spirit']['food_type'] = 1
        result = self.workflow.spirit(SpiritTarget(2, max_food=10))
        self.assertEqual(result.reason, 'premium_fallback_or_no_food')
        self.assertEqual(self.objects.commands, [])
        with self.assertRaises(ValueError):
            SpiritTarget(2, max_food=10, max_gold=1)

    def test_claim_reobserves_real_task_list_and_closes_before_campaign_yield(self):
        result = self.workflow.growth_pass()
        self.assertEqual((result.status, result.reason), ('NEEDS_CAMPAIGN', 'CAMPAIGN_TROOP'))
        self.assertEqual([c['action'] for c in self.objects.commands],
            ['growth_check', 'growth_open', 'growth_claim', 'growth_close'])
        targets = {f['target_id'] for f in self.objects.observations if f and f['action'] == 'growth_observe'}
        self.assertEqual(targets, {42, 91})
        self.assertFalse(self.objects.dev['growth']['window_visible'])

    def test_unsupported_quest_is_concrete_block_and_sends_nothing(self):
        self.objects.dev['growth']['tasks'] = [quest(42, kind=5)]
        result = self.workflow.growth_pass()
        self.assertEqual((result.status, result.reason), ('BLOCKED', 'NeedsCapability: SOUL_GATHER'))
        self.assertEqual(self.objects.commands, [])

    def test_best_equipped_existing_candidate_and_bounded_evolution(self):
        snap = equipment()
        snap['candidates'][0]['in_current_formation'] = True
        snap['candidates'].extend([dict(equipment_id=9, level=10, owner_id=65535, slot=0, in_current_formation=False),
            dict(equipment_id=8, level=2, owner_id=104, slot=1, cost=dict(up=1), in_current_formation=True, owner_slot_matches=True),
            dict(equipment_id=10, level=7, owner_id=110, slot=1, cost=dict(up=0), in_current_formation=False)])
        target = DevelopmentWorkflow.equipment_for_quest(snap, 9)
        self.assertEqual((target.equipment_id, target.desired_level, target.mode, target.max_requests), (8, 3, 'evolve', 1))
        self.assertEqual((target.max_money, target.max_stone), (1000, 20))

    def test_ui_wait_is_bounded_and_never_sends(self):
        self.objects.dev['gate'] = 'WAITING_UI'
        result = self.workflow.equipment(self.target())
        self.assertEqual(result.reason, 'development_ui_wait_timeout')
        self.assertEqual(self.objects.commands, [])


class CoordinatorTests(unittest.TestCase):
    def test_shared_runner_busy_gate_and_stop_release_owner(self):
        with tempfile.TemporaryDirectory() as directory:
            objects = DevObjects(directory)
            objects.no_result = True
            runner = TaskRunner()
            coordinator = DevelopmentCoordinator(objects, runner, directory)
            self.assertTrue(coordinator.start('equipment', EquipmentTarget(7, 2, max_money=10, max_stone=2)))
            deadline = time.monotonic() + 1
            while not objects.commands and time.monotonic() < deadline:
                time.sleep(.005)
            self.assertFalse(coordinator.start())
            self.assertFalse(runner.start('overlap', lambda ctx: None))
            self.assertTrue(coordinator.stop())
            runner.join(1)
            self.assertFalse(coordinator.is_active)
            self.assertTrue(objects.released)
            self.assertTrue(objects.stopping)
            self.assertEqual(len(objects.commands), 1)


class CampaignDevelopmentTests(unittest.TestCase):
    def combined(self, directory):
        objects = CampaignObjects(['LOSS', 'LOSS', 'LOSS'])
        development = DevObjects(directory)
        objects.observe_development = development.observe_development
        objects.submit_development = development.submit_development
        return objects, development

    def test_growth_needs_campaign_does_not_record_a_result_or_change_loss_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            objects, development = self.combined(directory)
            development.dev['growth']['tasks'] = [quest(42, kind=8)]
            runner = TaskRunner()
            coordinator = CampaignCoordinator(objects, runner, Path(directory), poll_seconds=.002)
            try:
                self.assertTrue(coordinator.start())
                eventually(lambda: not coordinator.is_active)
                runner.join(.5)
                self.assertEqual(coordinator.snapshot['mode'], 'loss_limit')
                self.assertEqual(coordinator.snapshot['losses'], 3)
                self.assertEqual(sum(action == 'start' for action, _ in objects.commands), 3)
                self.assertEqual(development.commands, [])
            finally:
                coordinator.close()
                runner.join(1)

    def test_unsettled_growth_holds_campaign_and_explicit_resume_cannot_bypass_it(self):
        with tempfile.TemporaryDirectory() as directory:
            objects, development = self.combined(directory)
            development.no_result = True
            runner = TaskRunner()
            coordinator = CampaignCoordinator(objects, runner, Path(directory), poll_seconds=.002)
            original = DevelopmentWorkflow.__init__
            from unittest.mock import patch
            def bounded(workflow, *args, **kwargs):
                kwargs['timeout'] = .02
                original(workflow, *args, **kwargs)
            try:
                with patch.object(DevelopmentWorkflow, '__init__', bounded):
                    self.assertTrue(coordinator.start())
                    eventually(lambda: coordinator.is_paused)
                    self.assertEqual(objects.commands, [])
                    self.assertEqual(coordinator.snapshot['losses'], 0)
                    self.assertEqual(len(development.commands), 1)
                    self.assertTrue(coordinator.resume())
                    eventually(lambda: coordinator.is_paused)
                    self.assertEqual(objects.commands, [])
                    self.assertEqual(len(development.commands), 1)
            finally:
                coordinator.close()
                runner.join(1)


if __name__ == '__main__':
    unittest.main()
