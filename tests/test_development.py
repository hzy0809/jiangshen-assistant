import copy
import unittest

from jiangshen_assistant.development import EquipmentTarget, EquipmentUpgrade, SpiritTarget, SpiritUpgrade


OP = '0123456789abcdef0123456789abcdef'
CTX = 'process/session/root/character'


def operation(command=None, **changes):
    op = dict(status='IDLE', action='', operation_id='', sequence=0, server='UNVERIFIED')
    if command:
        op.update(status='OBSERVED', action=command.action, operation_id=OP, sequence=12345,
                  expected_revision=command.expected_revision, target_id=command.target_id,
                  desired_level=command.desired_level, mode=command.mode)
    op.update(changes)
    return op


def equipment(**changes):
    result = dict(schema=1, gate='PASS', target_id=7, desired_level=3, mode='enchant',
        available_money=1000, available_stone=20, available_action='equipment_enchant',
        candidates=[dict(equipment_id=7, owner_id=0, slot=0, level=1, main_id=1, next_main_id=2,
                         cost=dict(money=10, stone=2, up=0, cost_status='OBSERVED'))],
        ui=dict(bound_equipment_id=7, owner_id=0, slot=0, page=0, sended=False), operation=operation())
    result.update(changes)
    return result


def spirit(**changes):
    result = dict(schema=1, gate='PASS', desired_level=3, level=1, experience=2, food=10, food_type=0,
        next_exp=100, refreshing=False, available_action='spirit_train',
        cost=dict(food=None, gold=0, cost_status='UNVERIFIED'), operation=operation())
    result.update(changes)
    return result


class EquipmentTests(unittest.TestCase):
    def make(self, **changes):
        values = dict(equipment_id=7, desired_level=3, max_requests=2, max_money=20, max_stone=4)
        values.update(changes)
        return EquipmentUpgrade(OP, CTX, EquipmentTarget(**values))

    def plan(self, policy, snapshot, revision=1):
        return policy.plan(snapshot, revision=revision, context_key=CTX)

    def progress(self, command, level=2, **changes):
        snap = equipment(operation=operation(command, receipt_opcode=1104, receipt_id=7,
            receipt_level=level, server_progress_observed=True))
        snap['candidates'][0]['level'] = level
        snap.update(changes)
        return snap

    def test_already_met_does_not_request_and_done_is_idempotent(self):
        policy, snap = self.make(), equipment(available_action='')
        snap['candidates'][0]['level'] = 3
        out = self.plan(policy, snap)
        self.assertEqual((out.status, out.requests, out.evidence), ('DONE', 0, 'LOCAL_OBSERVED'))
        self.assertEqual(self.plan(policy, equipment(), 2).status, 'DONE')

    def test_each_exact_progress_requotes_and_never_confirms_unknown_result_enum(self):
        policy = self.make()
        first = self.plan(policy, equipment()).command
        self.assertEqual(self.plan(policy, equipment(), 1).status, 'WAITING')
        second = self.plan(policy, self.progress(first), 2).command
        self.assertEqual(second.nonce, 2)
        self.assertEqual((second.max_money, second.max_stone), (20, 4))
        done = self.plan(policy, self.progress(second, 3, available_action=''), 3)
        self.assertEqual((done.status, done.requests, done.reserved_money, done.evidence),
                         ('DONE', 2, 20, 'SERVER_PROGRESS_OBSERVED'))

    def test_cumulative_quote_budget_blocks_next_request(self):
        policy = self.make(max_money=19)
        first = self.plan(policy, equipment()).command
        out = self.plan(policy, self.progress(first), 2)
        self.assertEqual((out.status, out.reason, out.requests), ('BLOCKED', 'resource_budget_exhausted', 1))

    def test_normal_reinforce_parent_close_requires_each_panel_to_disappear(self):
        policy = self.make()
        snap = equipment(available_action='equipment_close')
        snap['candidates'][0]['level'] = 3
        snap['ui'].update(reinforce=dict(instance_id=22, visible=True), equipment=dict(instance_id=21, visible=False))
        first = self.plan(policy, snap).command
        self.assertEqual(first.action, 'equipment_close')
        snap['operation'] = operation(first)
        snap['ui']['reinforce']['visible'] = False
        snap['ui']['equipment']['visible'] = True
        second = self.plan(policy, snap, 2).command
        self.assertEqual(second.action, 'equipment_close')
        snap['operation'] = operation(second)
        snap['ui']['equipment']['visible'] = False
        snap['available_action'] = ''
        self.assertEqual(self.plan(policy, snap, 3).status, 'DONE')

    def test_repeating_close_without_panel_progress_is_blocked(self):
        policy = self.make()
        snap = equipment(available_action='equipment_close')
        snap['candidates'][0]['level'] = 3
        snap['ui'].update(reinforce=dict(instance_id=22, visible=True), equipment=dict(instance_id=21, visible=False))
        first = self.plan(policy, snap).command
        snap['operation'] = operation(first)
        self.assertEqual(self.plan(policy, snap, 2).reason, 'ui_step_no_progress')

    def test_request_budget_is_hard(self):
        policy = self.make(max_requests=1)
        command = self.plan(policy, equipment()).command
        self.assertEqual(self.plan(policy, self.progress(command), 2).reason, 'request_budget_exhausted')

    def test_uncaptured_response_never_retries(self):
        policy = self.make()
        command = self.plan(policy, equipment()).command
        unknown = equipment(operation=operation(command, status='UNKNOWN'))
        self.assertEqual(self.plan(policy, unknown, 2).reason, 'receipt_unknown')
        self.assertEqual(self.plan(policy, self.progress(command), 3).status, 'BLOCKED')

    def test_missing_own_revision_does_not_release_prepared_step(self):
        policy = self.make()
        command = self.plan(policy, equipment()).command
        snap = self.progress(command)
        snap['operation']['expected_revision'] = command.expected_revision - 1
        out = self.plan(policy, snap, 2)
        self.assertEqual((out.status, out.requests), ('WAITING', 1))

    def test_no_progress_or_wrong_receipt_id_fails_closed(self):
        for change in ({'receipt_id': 8}, {'receipt_level': 1}, {'server_progress_observed': False}):
            with self.subTest(change=change):
                policy = self.make()
                command = self.plan(policy, equipment()).command
                snap = self.progress(command)
                snap['operation'].update(change)
                self.assertEqual(self.plan(policy, snap, 2).status, 'BLOCKED')

    def test_mode_is_explicit_and_not_derived_from_button_name(self):
        snap = equipment(mode='evolve')
        snap['candidates'][0]['cost']['up'] = 1
        self.assertEqual(self.plan(self.make(mode='evolve'), snap).status, 'COMMAND')
        snap['mode'] = 'enchant'
        self.assertEqual(self.plan(self.make(), snap).reason, 'equipment_mode_mismatch')

    def test_unknown_cost_resources_target_or_binding_blocks(self):
        mutations = [lambda s: s['candidates'][0]['cost'].update(money=None),
                     lambda s: s.pop('available_money'),
                     lambda s: s.update(target_id=8),
                     lambda s: s['ui'].update(bound_equipment_id=8),
                     lambda s: s['candidates'].append(copy.deepcopy(s['candidates'][0]))]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                snap = equipment()
                mutation(snap)
                self.assertEqual(self.plan(self.make(), snap).status, 'BLOCKED')

    def test_unknown_gate_and_outside_action_block(self):
        for changes in ({'gate': 'NEW_SUCCESS'}, {'available_action': 'buy_money'},
                        {'operation': operation(status='NEW_SUCCESS')}):
            self.assertEqual(self.plan(self.make(), equipment(**changes)).status, 'BLOCKED')

    def test_ui_open_is_single_use_and_does_not_spend_request_budget(self):
        policy = self.make()
        command = self.plan(policy, equipment(available_action='equipment_open')).command
        self.assertEqual(self.plan(policy, equipment(available_action='equipment_open'), 2).status, 'WAITING')
        out = self.plan(policy, equipment(operation=operation(command), available_action='reinforce_open'), 3)
        self.assertEqual((out.status, out.requests, out.command.action), ('COMMAND', 0, 'reinforce_open'))

    def test_changed_context_is_terminal_and_target_immutable(self):
        policy = self.make()
        self.assertEqual(policy.plan(equipment(), revision=1, context_key='other').reason, 'context_changed')
        self.assertEqual(self.plan(policy, equipment(), 2).status, 'BLOCKED')
        with self.assertRaises(AttributeError):
            policy.target = EquipmentTarget(8, 3)

    def test_original_command_goal_echo_required(self):
        policy = self.make()
        command = self.plan(policy, equipment()).command
        snap = self.progress(command)
        snap['operation']['desired_level'] = 90
        self.assertEqual(self.plan(policy, snap, 2).reason, 'operation_goal_mismatch')

    def test_completed_ui_branch_without_transition_does_not_repeat(self):
        policy = self.make()
        command = self.plan(policy, equipment(available_action='equipment_open')).command
        out = self.plan(policy, equipment(available_action='equipment_open', operation=operation(command)), 2)
        self.assertEqual((out.status, out.reason, out.requests), ('BLOCKED', 'ui_step_no_progress', 0))

    def test_payload_has_only_fixed_native_fields(self):
        command = self.plan(self.make(), equipment()).command
        self.assertEqual(set(command.to_payload()), {'action', 'expected_revision', 'operation_id', 'target_id',
            'desired_level', 'max_requests', 'max_money', 'max_stone', 'max_food', 'mode'})


class SpiritTests(unittest.TestCase):
    def make(self, **changes):
        values = dict(desired_level=3, max_requests=3, max_food=10)
        values.update(changes)
        return SpiritUpgrade(OP, CTX, SpiritTarget(**values))

    def plan(self, policy, snap, revision=1):
        return policy.plan(snap, revision=revision, context_key=CTX)

    def response(self, command, *, level=1, exp=5, food=9, **changes):
        snap = spirit(level=level, experience=exp, food=food, operation=operation(command,
            receipt_opcode=1136, receipt_result=0, server='PASS', receipt_level=level,
            receipt_experience=exp, receipt_food=1, receipt_gold=0))
        snap.update(changes)
        return snap

    def test_training_experience_progress_does_not_require_level_change(self):
        policy = self.make()
        command = self.plan(policy, spirit()).command
        out = self.plan(policy, self.response(command), 2)
        self.assertEqual((out.status, out.requests, out.evidence), ('COMMAND', 2, 'SERVER_CONFIRMED'))

    def test_target_reached_stops_without_extra_training(self):
        policy = self.make()
        command = self.plan(policy, spirit()).command
        out = self.plan(policy, self.response(command, level=3, exp=0, available_action=''), 2)
        self.assertEqual((out.status, out.requests), ('DONE', 1))

    def test_strict_food_budget_without_quote_blocks_before_request(self):
        out = self.plan(self.make(max_food=9), spirit())
        self.assertEqual((out.reason, out.requests), ('quote_unavailable', 0))

    def test_observed_quote_permits_strict_smaller_budget(self):
        snap = spirit(cost=dict(food=1, gold=0, cost_status='OBSERVED'))
        out = self.plan(self.make(max_food=1), snap)
        self.assertEqual((out.status, out.reserved_food), ('COMMAND', 1))

    def test_receipt_exceeding_observed_quote_is_not_adopted(self):
        policy = self.make()
        command = self.plan(policy, spirit(cost=dict(food=1, gold=0, cost_status='OBSERVED'))).command
        snap = self.response(command, food=8)
        snap['operation']['receipt_food'] = 2
        self.assertEqual(self.plan(policy, snap, 2).reason, 'food_consumption_mismatch')

    def test_all_current_food_reservation_reconciles_actual_response(self):
        policy = self.make()
        command = self.plan(policy, spirit()).command
        self.assertEqual(policy._food, 10)
        out = self.plan(policy, self.response(command), 2)
        self.assertEqual(out.reserved_food, 10)  # actual1 + remaining pool9

    def test_auto_premium_fallback_stops_next_step(self):
        policy = self.make()
        command = self.plan(policy, spirit(food=1)).command
        snap = self.response(command, food=0, food_type=1)
        out = self.plan(policy, snap, 2)
        self.assertEqual((out.status, out.reason, out.requests), ('BLOCKED', 'premium_fallback_or_no_food', 1))

    def test_gold_receipt_and_unknown_result_are_never_success(self):
        for values in ({'receipt_gold': 1}, {'receipt_result': 4}, {'server': 'UNVERIFIED'}):
            policy = self.make()
            command = self.plan(policy, spirit()).command
            snap = self.response(command)
            snap['operation'].update(values)
            self.assertEqual(self.plan(policy, snap, 2).status, 'BLOCKED')

    def test_no_progress_does_not_loop(self):
        policy = self.make()
        command = self.plan(policy, spirit()).command
        self.assertEqual(self.plan(policy, self.response(command, exp=2), 2).reason, 'no_progress')

    def test_rankup_and_normal_confirmation_no_extra_request(self):
        policy = self.make(desired_level=11, max_food=0)
        command = self.plan(policy, spirit(desired_level=11, level=10, experience=100, food=0,
            food_type=1, available_action='spirit_rankup')).command
        snap = spirit(desired_level=11, level=11, experience=0, food=0, food_type=1, available_action='spirit_rankup_confirm',
            operation=operation(command, receipt_opcode=1139, receipt_result=0, server='PASS',
                                receipt_level=11, receipt_experience=0, receipt_food=0, receipt_gold=0))
        confirm = self.plan(policy, snap, 2)
        self.assertEqual((confirm.command.action, confirm.requests), ('spirit_rankup_confirm', 1))
        snap.update(available_action='', operation=operation(confirm.command))
        self.assertEqual(self.plan(policy, snap, 3).status, 'DONE')

    def test_train_cannot_replace_required_rankup(self):
        self.assertEqual(self.plan(self.make(desired_level=11), spirit(desired_level=11, level=10, experience=100)).reason,
                         'spirit_stage_action_mismatch')

    def test_unknown_receipt_has_no_replay(self):
        policy = self.make()
        command = self.plan(policy, spirit()).command
        self.assertEqual(self.plan(policy, spirit(operation=operation(command, status='UNKNOWN')), 2).status, 'BLOCKED')
        self.assertEqual(self.plan(policy, self.response(command), 3).status, 'BLOCKED')

    def test_unknown_data_foreign_action_and_false_result_do_not_succeed(self):
        for change in ({'desired_level': 40}, {'available_action': 'spirit_gold_train'},
                       {'food_type': 1}, {'next_exp': None}):
            self.assertEqual(self.plan(self.make(), spirit(**change)).status, 'BLOCKED')
        policy = self.make()
        command = self.plan(policy, spirit()).command
        snap = self.response(command)
        snap['operation']['receipt_result'] = False
        self.assertEqual(self.plan(policy, snap, 2).reason, 'spirit_receipt_unverified')

    def test_invalid_targets_and_premium_budget_rejected(self):
        for factory in (lambda: SpiritTarget(101), lambda: SpiritTarget(5, max_gold=1),
                        lambda: EquipmentTarget(True, 3), lambda: EquipmentTarget(1, 2, mode='forge'),
                        lambda: EquipmentUpgrade('bad', CTX, EquipmentTarget(1, 2))):
            with self.assertRaises(ValueError):
                factory()


if __name__ == '__main__':
    unittest.main()
