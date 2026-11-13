import unittest

from jiangshen_assistant.growth import CAPABILITIES, GrowthTaskDispatcher, select_growth_task


OP = '0123456789abcdef0123456789abcdef'
CTX = 'process/session/root/character'


def task(qid=50, kind=3, *, finished=0, got=0, condition=False, **template):
    data = dict(quest_id=qid, type=kind, param0=4, param1=2, exp=10, money=0)
    data.update(template)
    return dict(quest_id=qid, isfinish=finished, isgot=got, template=data,
                condition_met=condition, dispatch=CAPABILITIES.get(kind))


def snapshot(*tasks, **changes):
    rows = list(tasks) if tasks else [task()]
    selected = select_growth_task(dict(tasks=rows))
    result = dict(schema=1, gate='PASS', tasks=rows,
        selected_quest_id=selected.quest_id if selected else 0,
        ui_selected_quest_id=selected.quest_id if selected else 0,
        window_visible=True, window_enabled=True, show_window=True,
        available_action='', operation=dict(status='IDLE', operation_id=''))
    result.update(changes)
    return result


def response(command, *, opcode=810, result=0, status='OBSERVED', server='PASS'):
    return dict(status=status, operation_id=OP, sequence=12345, action=command.action,
                expected_revision=command.expected_revision, target_id=command.target_id,
                desired_level=command.desired_level, mode=command.mode,
                receipt_opcode=opcode, receipt_result=result, server=server)


class GrowthTests(unittest.TestCase):
    def make(self, capabilities=(), **changes):
        return GrowthTaskDispatcher(OP, CTX, capabilities, **changes)

    def plan(self, policy, snap, revision=1):
        return policy.plan(snap, revision=revision, context_key=CTX)

    def test_selection_reverse_unfinished_then_forward_unclaimed(self):
        rows = [task(22), task(5), task(99, finished=1)]
        self.assertEqual(select_growth_task(dict(tasks=rows)).quest_id, 5)
        rows = [task(22, finished=1, got=1), task(5, finished=1), task(99, finished=1)]
        self.assertEqual(select_growth_task(dict(tasks=rows)).quest_id, 5)

    def test_mapping_has_exact_fourteen_types_and_no_spirit(self):
        self.assertEqual(set(CAPABILITIES), set(range(1, 15)))
        self.assertNotIn('SPIRIT', CAPABILITIES.values())
        for kind, name in CAPABILITIES.items():
            out = self.plan(self.make((name,)), snapshot(task(kind=kind)))
            self.assertEqual((out.status, out.dispatch.capability), ('DISPATCH', name))

    def test_unregistered_capability_is_structured_need(self):
        out = self.plan(self.make(), snapshot())
        self.assertEqual((out.status, out.reason, out.capability, out.command),
                         ('BLOCKED', 'NeedsCapability', 'EQUIPMENT_UPGRADE', None))

    def test_equipment_requirement_exports_actual_params(self):
        out = self.plan(self.make(('EQUIPMENT_UPGRADE',)), snapshot(task(7, param0=17, param1=9)))
        self.assertEqual((out.dispatch.quest_id, out.dispatch.param0, out.dispatch.param1), (7, 17, 9))

    def test_observed_condition_uses_normal_check_not_upgrade(self):
        out = self.plan(self.make(), snapshot(task(condition=True), available_action='growth_check'))
        self.assertEqual((out.status, out.command.action, out.command.target_id), ('COMMAND', 'growth_check', 50))
        self.assertIsNone(out.dispatch)

    def test_local_finished_is_not_server_finish_proof(self):
        out = self.plan(self.make(), snapshot(task(finished=1), available_action='growth_claim'))
        self.assertEqual((out.command.action, out.evidence), ('growth_claim', 'UNVERIFIED'))

    def test_check_then_claim_requires_distinct_correlated_receipt(self):
        policy = self.make()
        check = self.plan(policy, snapshot(task(condition=True), available_action='growth_check')).command
        claim = self.plan(policy, snapshot(task(finished=1), available_action='growth_claim',
            operation=response(check)), 2)
        self.assertEqual((claim.command.action, claim.evidence), ('growth_claim', 'SERVER_FINISH_CONFIRMED'))
        done = self.plan(policy, snapshot(task(finished=1, got=1),
            operation=response(claim.command, opcode=811)), 3)
        self.assertEqual((done.status, done.reason, done.evidence),
                         ('DONE', 'quest_claim_server_confirmed', 'SERVER_CONFIRMED'))

    def test_client_ignores_bad_award_result_but_domain_does_not(self):
        policy = self.make()
        command = self.plan(policy, snapshot(task(finished=1), available_action='growth_claim')).command
        out = self.plan(policy, snapshot(task(finished=1, got=1), operation=response(command, opcode=811, result=1)), 2)
        self.assertEqual((out.status, out.reason), ('BLOCKED', 'growth_claim_result_unverified'))

    def test_hidden_check_can_finish_before_background_selection_changes(self):
        policy = self.make()
        check = self.plan(policy, snapshot(task(40), task(50, condition=True), available_action='growth_check', window_visible=False)).command
        result = self.plan(policy, snapshot(task(40), task(50, finished=1), window_visible=False,
            operation=response(check)), 2)
        self.assertEqual((result.status, result.reason, result.evidence),
            ('DONE', 'quest_finish_server_confirmed', 'SERVER_FINISH_CONFIRMED'))
        self.assertEqual(result.quest.quest_id, 50)

    def test_visible_bound_quest_can_claim_after_background_selection_changes(self):
        policy = self.make()
        check = self.plan(policy, snapshot(task(40), task(50, condition=True), available_action='growth_check')).command
        result = self.plan(policy, snapshot(task(40), task(50, finished=1), ui_selected_quest_id=50,
            operation=response(check), available_action='growth_claim'), 2)
        self.assertEqual((result.status, result.command.target_id, result.command.action), ('COMMAND', 50, 'growth_claim'))

    def test_combined_scan_does_not_confirm_bound_quest_without_membership(self):
        policy = self.make()
        opened = self.plan(policy, snapshot(task(finished=1), available_action='growth_open', window_visible=False)).command
        claim = self.plan(policy, snapshot(task(finished=1), available_action='growth_claim',
            operation=response(opened, opcode=810)), 2)
        self.assertEqual((claim.command.action, claim.evidence), ('growth_claim', 'UNVERIFIED'))

    def test_unknown_claim_response_blocks_no_repeat(self):
        policy = self.make()
        command = self.plan(policy, snapshot(task(finished=1), available_action='growth_claim')).command
        out = self.plan(policy, snapshot(operation=response(command, status='UNKNOWN')), 2)
        self.assertEqual((out.status, out.requests), ('BLOCKED', 1))
        self.assertEqual(self.plan(policy, snapshot(), 3).status, 'BLOCKED')

    def test_claim_receipt_without_matching_local_postcondition_not_done(self):
        policy = self.make()
        command = self.plan(policy, snapshot(task(finished=1), available_action='growth_claim')).command
        out = self.plan(policy, snapshot(task(finished=1), operation=response(command, opcode=811)), 2)
        self.assertEqual((out.status, out.reason), ('BLOCKED', 'growth_claimed_postcondition_missing'))

    def test_finish_receipt_without_normal_finish_postcondition_not_rechecked(self):
        policy = self.make()
        command = self.plan(policy, snapshot(task(condition=True), available_action='growth_check')).command
        out = self.plan(policy, snapshot(task(condition=True), available_action='growth_check', operation=response(command)), 2)
        self.assertEqual((out.status, out.reason, out.requests), ('BLOCKED', 'growth_finished_postcondition_missing', 1))

    def test_missing_old_or_foreign_receipt_cannot_release_claim(self):
        policy = self.make()
        command = self.plan(policy, snapshot(task(finished=1), available_action='growth_claim')).command
        old = response(command, opcode=811)
        old['expected_revision'] = 0
        self.assertEqual(self.plan(policy, snapshot(operation=old), 2).status, 'WAITING')

    def test_changed_quest_target_rejected(self):
        policy = self.make(('EQUIPMENT_UPGRADE',))
        self.assertEqual(self.plan(policy, snapshot()).status, 'DISPATCH')
        self.assertEqual(self.plan(policy, snapshot(task(param0=18)), 2).reason, 'quest_target_changed')

    def test_unsupported_type_unknown_mapper_native_action_and_selection(self):
        self.assertEqual(self.plan(self.make(), snapshot(task(kind=15))).capability, 'QUEST_TYPE_15')
        self.assertEqual(self.plan(self.make(), snapshot(task(condition=True), available_action='finishQuest')).status, 'BLOCKED')
        self.assertEqual(self.plan(self.make(), snapshot(ui_selected_quest_id=99)).reason, 'selected_task_binding_mismatch')
        row = task()
        row['dispatch'] = 'SPIRIT'
        self.assertEqual(self.plan(self.make(), snapshot(row)).reason, 'native_dispatch_mismatch')

    def test_unknown_condition_is_not_false_requirement(self):
        out = self.plan(self.make(('EQUIPMENT_UPGRADE',)), snapshot(task(condition=None)))
        self.assertEqual((out.status, out.reason), ('BLOCKED', 'requirement_data_unknown'))

    def test_no_tasks_done_is_local_observation(self):
        snap = snapshot()
        snap.update(tasks=[], selected_quest_id=0, ui_selected_quest_id=0)
        out = self.plan(self.make(), snap)
        self.assertEqual((out.status, out.evidence), ('DONE', 'LOCAL_OBSERVED'))

    def test_claim_button_gates_and_budget(self):
        out = self.plan(self.make(), snapshot(task(finished=1), available_action='growth_claim', show_window=False))
        self.assertEqual(out.reason, 'claim_button_gate_unknown')
        policy = self.make(max_requests=1)
        command = self.plan(policy, snapshot(task(condition=True), available_action='growth_check')).command
        out = self.plan(policy, snapshot(task(finished=1), available_action='growth_claim', operation=response(command)), 2)
        self.assertEqual(out.reason, 'request_budget_exhausted')

    def test_invalid_data_or_unknown_status_fails_closed(self):
        snap = snapshot()
        snap['tasks'][0]['template']['quest_id'] = 80
        self.assertEqual(self.plan(self.make(), snap).reason, 'growth_task_or_template_unknown')
        self.assertEqual(self.plan(self.make(), snapshot(operation=dict(status='SUCCESS'))).status, 'BLOCKED')


if __name__ == '__main__':
    unittest.main()
