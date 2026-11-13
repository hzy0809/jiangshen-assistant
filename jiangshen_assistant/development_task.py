"""On-demand development tasks planned from the current game observation.

ACKs describe invocation. Only the following typed observation can confirm an
operation in this run. Logs are diagnostic output, never startup authority.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass, replace
from functools import wraps
from pathlib import Path
import copy
import json
import os
import threading
import time
import uuid

from .development import (DevelopmentCommand, EquipmentTarget, EquipmentUpgrade,
                          SpiritTarget, SpiritUpgrade, TributeTarget, SpiritTribute,
                          DailySpiritTribute, StarTarget, StarActivation, _Policy)
from .growth import GrowthTaskDispatcher, select_growth_task, _quests
from .objects import CampaignAdmissionClosed
from .scene_navigation import SceneGoal, ensure_scene, scene_context_from_state
from .event_stream import EventMailbox
from .workflow_wait import WakeSignal


@dataclass(frozen=True)
class WorkflowResult:
    status: str
    reason: str
    evidence: str = 'UNVERIFIED'
    ledger_path: str = ''
    progress: str = ''
    cleanup: str = ''


class _GrowthClose(_Policy):
    def __init__(self, operation_id, context_key, quest_id):
        super().__init__(operation_id, context_key)
        self.quest_id = quest_id

    def plan(self, snapshot, *, revision, context_key):
        early = self._admit(snapshot, revision, context_key)
        if early:
            return early
        if snapshot.get('window_visible') is False:
            return self._out('DONE', 'growth_window_closed')
        if snapshot.get('window_enabled') is not True or snapshot.get('show_window') is not True:
            return self._block('normal_growth_close_unavailable')
        return self._issue('growth_close', revision, target_id=self.quest_id, desired_level=0, max_requests=10)

    def _accept(self, snapshot, op, action):
        return '' if snapshot.get('window_visible') is False else 'growth_close_postcondition_missing'


class _CurrentGrowthDispatcher(GrowthTaskDispatcher):
    """Keep one quest bound while the normal UI advances its selection."""

    def __init__(self, operation_id, context_key, quest):
        super().__init__(operation_id, context_key, ('EQUIPMENT_UPGRADE',))
        self._bound_quest = quest

    def plan(self, snapshot, *, revision, context_key):
        # Retain the native selection, action and receipts. Restrict only the
        # quest list considered by the dispatcher, so it cannot claim a successor.
        bound = dict(snapshot, tasks=[row for row in snapshot.get('tasks', [])
            if row.get('quest_id') == self._bound_quest.quest_id])
        if len(bound['tasks']) != 1:
            return self._block('current_growth_task_missing')
        outcome = super().plan(bound, revision=revision, context_key=context_key)
        if self._claim_confirmed:
            # The claim receipt is settled even while its normal reward UI
            # loads. Hand it to the separate confirmation policy immediately.
            self._done = True
            return self._result('DONE', 'quest_claim_server_confirmed')
        return outcome


class _GrowthWeaponPreparation(_Policy):
    """One free normal empty weapon slot action before quest 3's upgrades."""
    def __init__(self, operation_id, context_key, quest):
        super().__init__(operation_id, context_key)
        self.quest = quest
        self._equipment_id = None
        self._resources = None

    @property
    def equipment_id(self):
        return self._equipment_id

    def _accept(self, snapshot, op, action):
        requirement = snapshot.get('requirement') or {}
        if action == 'growth_weapon_wear':
            if (op.get('receipt_opcode') != 856 or op.get('receipt_result') != 0 or
                    op.get('server') != 'PASS' or op.get('receipt_id') != self._equipment_id or
                    requirement.get('equipment_id') != self._equipment_id or
                    requirement.get('owner_slot_matches') is not True):
                return 'growth_weapon_receipt_or_slot_unverified'
            self._evidence = 'SERVER_CONFIRMED'
            return ''
        if action == 'growth_weapon_close':
            return '' if (requirement.get('ui') or {}).get('equipment', {}).get('visible') is False else 'growth_weapon_close_unverified'
        return super()._accept(snapshot, op, action)

    def plan(self, snapshot, *, revision, context_key):
        early = self._admit(snapshot, revision, context_key)
        if early:
            return early
        requirement = snapshot.get('requirement') or {}
        q = self.quest
        if (q.quest_id, q.type, q.param0, q.param1) != (3, 3, 5, 0):
            return self._out('UNSUPPORTED', 'growth_weapon_initial_quest_required')
        if tuple(requirement.get(key) for key in ('quest_id', 'type', 'param0', 'param1')) != (3, 3, 5, 0):
            return self._out('UNSUPPORTED', 'growth_weapon_preparation_unavailable') if self._sequence == 0 else self._block('growth_weapon_binding_changed')
        if requirement.get('phase') == 'BLOCKED' and self._sequence == 0:
            return self._out('UNSUPPORTED', str(snapshot.get('reason') or 'growth_weapon_preparation_unavailable'))
        if requirement.get('owner_id') != 0 or requirement.get('slot') != 0 or requirement.get('in_current_formation') is not True:
            return self._block('growth_weapon_owner_or_slot_changed')
        equipment_id = requirement.get('equipment_id')
        if type(equipment_id) is not int or not 1 <= equipment_id <= 255:
            return self._block('growth_weapon_candidate_unknown')
        if self._equipment_id is None:
            self._equipment_id = equipment_id
        elif self._equipment_id != equipment_id:
            return self._block('growth_weapon_candidate_changed')
        resources = tuple(requirement.get(key) for key in ('available_money', 'available_stone', 'gold'))
        if any(type(value) is not int or value < 0 for value in resources):
            return self._block('growth_weapon_resources_unknown')
        if self._resources is None:
            self._resources = resources
        elif resources != self._resources:
            return self._block('growth_weapon_free_action_resources_changed')
        if requirement.get('phase') == 'DONE':
            receipt = requirement.get('wear_receipt') or {}
            if (self._evidence != 'SERVER_CONFIRMED' or requirement.get('owner_slot_matches') is not True or
                    receipt.get('operation_id') != self.operation_id or receipt.get('receipt_id') != equipment_id or
                    receipt.get('receipt_opcode') != 856 or receipt.get('receipt_result') != 0 or receipt.get('server') != 'PASS'):
                return self._block('growth_weapon_completion_unverified')
            self._done = True
            return self._out('DONE', 'growth_weapon_equipped')
        action = snapshot.get('available_action')
        if action not in ('growth_weapon_open', 'growth_weapon_wear', 'growth_weapon_close'):
            return self._block(str(snapshot.get('reason') or 'growth_weapon_action_unavailable'))
        if action == 'growth_weapon_wear' and (requirement.get('slot_empty') is not True or requirement.get('candidate_unowned') is not True):
            return self._block('growth_weapon_empty_slot_or_inventory_changed')
        return self._issue(action, revision, target_id=q.quest_id, desired_level=0,
            max_requests=1, request=action == 'growth_weapon_wear')


class _GrowthRewardConfirm(_Policy):
    def __init__(self, operation_id, context_key, quest_id, *, claim_operation_id, quest):
        super().__init__(operation_id, context_key)
        self.quest_id = quest_id
        self.claim_operation_id = claim_operation_id
        self._quest_identity = (quest.quest_id, quest.type, quest.param0, quest.param1)
        self._dialog_id = None
        self._claim_sequence = None
        self._confirmed = False
        self._evidence = 'SERVER_CONFIRMED'

    def plan(self, snapshot, *, revision, context_key):
        early = self._admit(snapshot, revision, context_key)
        if early and (early.status != 'WAITING' or self._pending is not None or
                early.reason in ('fresh_snapshot_required', 'own_command_observation_required')):
            return early
        if self._confirmed:
            self._done = True
            return self._out('DONE', 'growth_reward_confirmed')
        dialog, loading = snapshot.get('reward_dialog') or {}, snapshot.get('reward_wait') or {}
        row = next((q for q in _quests(snapshot) if q.quest_id == self.quest_id), None)
        if (row is None or row.isfinish != 1 or row.isgot != 1 or
                (row.quest_id, row.type, row.param0, row.param1) != self._quest_identity or
                loading.get('claim_confirmed') is not True or loading.get('bound') is not True or
                loading.get('quest_id') != self.quest_id or
                loading.get('claim_operation_id') != self.claim_operation_id or
                type(loading.get('claim_sequence')) is not int or loading['claim_sequence'] < 1 or
                type(dialog.get('instance_id')) is not int or dialog['instance_id'] == 0 or
                loading.get('dialog_instance_id') != dialog['instance_id']):
            return self._block('current_growth_reward_claim_or_binding_changed')
        if self._dialog_id is None:
            self._dialog_id, self._claim_sequence = dialog['instance_id'], loading['claim_sequence']
        elif (dialog['instance_id'], loading['claim_sequence']) != (self._dialog_id, self._claim_sequence):
            return self._block('current_growth_reward_dialog_changed')
        if loading.get('phase') == 'LOADING':
            if (snapshot.get('gate') != 'WAITING_UI' or loading.get('loading') is not True or
                    loading.get('collecting') is not False or loading.get('show_visible_pending') is not True or
                    type(loading.get('asset_names_count')) is not int or loading['asset_names_count'] < 1 or
                    snapshot.get('available_action') != ''):
                return self._block('current_growth_reward_loading_unverified')
            return self._out('WAITING_UI', 'growth_claim_reward_assets_loading')
        if early:
            return early
        if (snapshot.get('reward_quest_id') != self.quest_id or
                dialog.get('visible') is not True or dialog.get('enabled') is not True or
                loading.get('phase') != 'READY' or loading.get('loading') is not False or
                loading.get('collecting') is not False or
                snapshot.get('available_action') != 'growth_reward_confirm'):
            return self._block('current_growth_reward_dialog_unavailable')
        return self._issue('growth_reward_confirm', revision, target_id=self.quest_id,
            desired_level=0, max_requests=10)

    def _accept(self, snapshot, op, action):
        dialog = snapshot.get('reward_dialog') or {}
        row = next((q for q in _quests(snapshot) if q.quest_id == self.quest_id), None)
        if (action != 'growth_reward_confirm' or row is None or row.isfinish != 1 or row.isgot != 1 or
                (row.quest_id, row.type, row.param0, row.param1) != self._quest_identity or
                dialog.get('visible') is not False or
                dialog.get('instance_id') not in (0, self._dialog_id)):
            return 'growth_reward_confirm_postcondition_missing'
        self._confirmed = True
        self._evidence = 'LOCAL_OBSERVED'
        return ''


class _FinishedGrowthClaim(_Policy):
    """An explicitly selected completed quest; never complete a requirement."""
    def __init__(self, operation_id, context_key, quest, *, explicit_target=False):
        super().__init__(operation_id, context_key)
        self.quest = quest
        self._explicit_target = explicit_target
        self._confirmed = False

    def _accept(self, snapshot, op, action):
        row = next((q for q in _quests(snapshot) if q.quest_id == self.quest.quest_id), None)
        if row is None or row.isfinish != 1 or (row.type, row.param0, row.param1) != (
                self.quest.type, self.quest.param0, self.quest.param1):
            return 'finished_growth_postcondition_missing'
        if action == 'growth_open':
            return '' if (snapshot.get('window_visible') is True and snapshot.get('window_enabled') is True and
                snapshot.get('show_window') is True and snapshot.get('ui_selected_quest_id') == row.quest_id
                and row.isgot == 0) else 'finished_growth_open_postcondition_missing'
        if (action != 'growth_claim' or op.get('receipt_opcode') != 811 or
                op.get('receipt_result') != 0 or op.get('server') != 'PASS' or row.isgot != 1):
            return 'finished_growth_claim_receipt_unverified'
        self._confirmed = True
        self._evidence = 'SERVER_CONFIRMED'
        return ''

    def plan(self, snapshot, *, revision, context_key):
        early = self._admit(snapshot, revision, context_key)
        if self._confirmed:
            self._done = True
            return self._out('DONE', 'quest_claim_server_confirmed')
        if early:
            return early
        bound = self.quest
        current = next((q for q in _quests(snapshot) if q.quest_id == bound.quest_id), None)
        if (current is None or current.isfinish != 1 or current.isgot != 0 or
                (current.quest_id, current.type, current.param0, current.param1) != (
                bound.quest_id, bound.type, bound.param0, bound.param1)):
            return self._block('finished_growth_target_changed')
        action = snapshot.get('available_action')
        if snapshot.get('window_visible') is True:
            if (snapshot.get('ui_selected_quest_id') != bound.quest_id or
                    snapshot.get('window_enabled') is not True or snapshot.get('show_window') is not True or
                    action != 'growth_claim'):
                return self._block('finished_growth_ui_target_changed')
        elif snapshot.get('window_visible') is not False or action != 'growth_open':
            return self._block('finished_growth_normal_open_unavailable')
        elif not self._explicit_target and snapshot.get('selected_quest_id') != bound.quest_id:
            return self._block('finished_growth_selected_target_changed')
        return self._issue(action, revision, target_id=bound.quest_id, desired_level=0,
            max_requests=10, request=True)


class _GrowthRequirement(_Policy):
    """One selected quest, one bounded normal artifact/soul transaction."""
    def __init__(self, operation_id, context_key, quest):
        super().__init__(operation_id, context_key)
        self.quest = quest

    def _accept(self, snapshot, op, action):
        requirement = snapshot.get('requirement') or {}
        expected = {'growth_soul_start': 517, 'growth_soul_grow': 519,
                    'growth_soul_harvest': 518, 'growth_artifact_wear': 863, 'growth_artifact_grant': 818,
                    'growth_recruit_confirm': 305, 'growth_recruit_save': 298}
        if action in expected:
            if op.get('receipt_opcode') != expected[action] or op.get('server') != 'PASS':
                return 'growth_requirement_receipt_unverified'
            if action == 'growth_soul_start' and op.get('receipt_result') != 1:
                return 'soul_free_start_result_unverified'
            if action == 'growth_artifact_wear' and (op.get('receipt_result') != 0 or
                    op.get('receipt_id') != requirement.get('artifact_id')):
                return 'artifact_equip_receipt_mismatch'
            if action == 'growth_artifact_grant' and (op.get('receipt_result') != 0 or
                    requirement.get('artifact_id') != 1 or requirement.get('reputation_level') != 1 or
                    requirement.get('grant_stage') != 1):
                return 'artifact_initial_grant_receipt_mismatch'
            if action == 'growth_recruit_confirm' and (op.get('receipt_result') != 1 or op.get('receipt_id') != 71 or
                    op.get('receipt_level') != 0 or op.get('receipt_food') != 1 or requirement.get('general_count') != 5 or
                    not self._before or requirement.get('blue_souls') != self._before[0] - 250):
                return 'recruit71_receipt_or_blue_delta_unverified'
            if action == 'growth_recruit_save' and op.get('receipt_result') != 1:
                return 'recruit71_save_receipt_unverified'
            return ''
        if action == 'growth_recruit_choose':
            return '' if ((requirement.get('ui') or {}).get('dialog') or {}).get('visible') is True else 'recruit71_confirmation_missing'
        if action == 'growth_artifact_view':
            return '' if (requirement.get('grant_stage') == 2 and
                ((requirement.get('ui') or {}).get('special') or {}).get('visible') is False) else 'artifact_view_postcondition_missing'
        if action == 'growth_guide_open':
            return '' if (snapshot.get('window_visible') is True and
                snapshot.get('ui_selected_quest_id') == self.quest.quest_id and
                snapshot.get('show_window') is False) else 'growth_guide_postcondition_missing'
        if action == 'growth_requirement_close':
            return '' if all((requirement.get('ui', {}).get(name) or {}).get('visible') is False
                for name in ('recruit', 'soul', 'equipment')) else 'growth_requirement_close_postcondition_missing'
        return super()._accept(snapshot, op, action)

    def plan(self, snapshot, *, revision, context_key):
        early = self._admit(snapshot, revision, context_key)
        if early:
            if early.status == 'BLOCKED' and self._sequence == 0 and early.reason in (
                    'artifact_equipment_feature_locked', 'artifact_reputation_feature_locked',
                    'artifact_owned_candidate_unavailable', 'soul_recruit_or_gather_feature_locked',
                    'artifact_initial_rank_grant_unavailable', 'artifact_initial_reputation_insufficient',
                    'recruit71_initial_party_or_slot_unavailable', 'recruit71_blue_budget_insufficient', 'recruit71_candidate_unavailable', 'recruit71_campaign_required',
                    'soul_free_single_blue_unavailable', 'soul_growth_coin_budget_insufficient',
                    'soul_single_blue_3000_coin_mode_unavailable', 'soul_exactly_one_unfinished_quest_required',
                    'soul_existing_gather_session_not_owned', 'growth_guide_would_check_other_quest'):
                return self._out('UNSUPPORTED', early.reason)
            return early
        requirement = snapshot.get('requirement') or {}
        q = self.quest
        if tuple(requirement.get(key) for key in ('quest_id', 'type', 'param0', 'param1')) != (
                q.quest_id, q.type, q.param0, q.param1):
            return self._block('growth_requirement_binding_changed')
        current = next((row for row in _quests(snapshot) if row.quest_id == q.quest_id), None)
        if current is None or (current.type, current.param0, current.param1) != (q.type, q.param0, q.param1):
            return self._block('current_growth_task_changed')
        if requirement.get('phase') == 'BLOCKED' and self._sequence == 0:
            return self._out('UNSUPPORTED', str(snapshot.get('reason') or 'growth_requirement_unavailable'))
        if requirement.get('phase') == 'DONE':
            finish = requirement.get('finish_receipt') or {}
            if (current.isfinish != 1 or finish.get('status') != 'OBSERVED' or
                    finish.get('server') != 'PASS' or finish.get('receipt_opcode') != 810 or
                    finish.get('target_id') != q.quest_id or finish.get('operation_id') != self.operation_id):
                return self._block('growth_requirement_finish_unverified')
            self._evidence = 'SERVER_FINISH_CONFIRMED'
            self._done = True
            return self._out('DONE', 'growth_requirement_completed')
        action = snapshot.get('available_action')
        actions = {
            4: {'growth_artifact_open', 'growth_artifact_wear', 'growth_reputation_open', 'growth_artifact_grant', 'growth_artifact_view'},
            5: {'growth_soul_recruit', 'growth_soul_open', 'growth_soul_start', 'growth_soul_grow', 'growth_soul_harvest'},
            6: {'growth_recruit_open', 'growth_recruit_right', 'growth_recruit_left', 'growth_recruit_choose', 'growth_recruit_confirm', 'growth_recruit_deploy', 'growth_recruit_save'},
        }
        allowed = {'growth_guide_open', 'growth_requirement_close'} | actions.get(q.type, set())
        if action not in allowed:
            return self._block(str(snapshot.get('reason') or 'growth_requirement_action_unavailable'))
        if action == 'growth_soul_start' and (requirement.get('rate') != 1 or
                type(requirement.get('used_times')) is not int or type(requirement.get('free_times')) is not int or
                requirement['used_times'] >= requirement['free_times']):
            return self._block('soul_free_single_blue_unavailable')
        if action == 'growth_soul_grow' and (requirement.get('grow_cost') != 3000 or
                requirement.get('rate') != 1 or requirement.get('mode') != 1 or
                self._money != 0 or requirement.get('available_money', -1) < 3000):
            return self._block('soul_growth_coin_budget_changed')
        if action == 'growth_artifact_grant' and (requirement.get('reputation_level') != 0 or
                requirement.get('artifact_id') != 1 or requirement.get('grant_stage') != 0 or requirement.get('reputation_unlocked') is not True):
            return self._block('artifact_initial_grant_target_changed')
        if action == 'growth_recruit_confirm':
            if requirement.get('general_count') != 4 or type(requirement.get('blue_souls')) is not int or requirement['blue_souls'] < 250:
                return self._block('recruit71_budget_or_party_changed')
            self._before = (requirement['blue_souls'],)
        request = action in ('growth_soul_start', 'growth_soul_grow', 'growth_soul_harvest', 'growth_artifact_wear', 'growth_artifact_grant', 'growth_recruit_confirm', 'growth_recruit_save')
        return self._issue(action, revision, target_id=q.quest_id, desired_level=0,
            max_requests=4, max_money=3000 if q.type == 5 else 0,
            request=request, money=3000 if action == 'growth_soul_grow' else 0)


def _workflow_scope(method=None, *, native_entry_panels=()):
    """Keep one subscription and reuse explicitly supported native panels."""
    if method is None:
        return lambda wrapped: _workflow_scope(wrapped, native_entry_panels=native_entry_panels)
    @wraps(method)
    def scoped(self, *args, **kwargs):
        self._scope_depth += 1
        try:
            self._subscribe()
            if self._scope_depth == 1 and self.pending is None:
                module = args[0] if args else kwargs.get('module')
                native_panel_visible = False
                if module in native_entry_panels:
                    observed = self.objects.observe_scene(self.token)
                    state = observed.get('state') or {}
                    native = (state.get('development') or {}).get(module) or {}
                    panel = (native.get('ui') or {}).get(module) or {}
                    native_panel_visible = (observed.get('context_key') == self.token.context_key
                        and not observed.get('stopping') and panel.get('visible') is True
                        and not scene_context_from_state(state).get('pending_operation'))
                # A visible supported panel goes to its typed native planner;
                # it is not a generic home-scene or mutation admission witness.
                if not native_panel_visible:
                    navigation = ensure_scene(self.objects, self.token, SceneGoal.HOME, stopped=self.stopped,
                        paused=self.paused, wait=self.wait, timeout=self.timeout, clock=self.clock,
                        publish=lambda kind, **values: self._record(kind, **values))
                    if navigation.status != 'READY':
                        reason = ('development_user_stopped' if navigation.status == 'STOPPED'
                            and self.stopped() else navigation.reason)
                        return self._result(navigation.status, reason)
            return method(self, *args, **kwargs)
        finally:
            self._scope_depth -= 1
            if self._scope_depth == 0:
                self.close()
    return scoped


class DevelopmentWorkflow:
    """Synchronous reusable executor; caller owns connection and pause/stop."""

    def __init__(self, objects, token, data_dir, *, stopped=lambda: False,
                 paused=lambda: False, wait=None, publish=None, timeout=30., clock=time.monotonic,
                 subscription=None):
        self.objects, self.token = objects, token
        self.data_dir = Path(data_dir) / 'development_runs'
        self.stopped, self.paused = stopped, paused
        self._wake = WakeSignal()
        self.wait = wait or self._wait
        self._subscription = subscription
        self._owns_subscription = False
        self._scope_depth = 0
        self.publish = publish or (lambda result: None)
        self.timeout, self.clock = timeout, clock
        self.pending = None
        self._journal = None
        self._path = None
        self._events_path = None
        self._journal_write_error = None
        self._progress = ''
        self._active_daily_tribute = None

    def _wait(self):
        self._wake.wait(1.)

    def _subscribe(self):
        if self._subscription is None:
            self._subscription = self.objects.subscribe_workflow(self.token, notify=self._wake.set)
            self._owns_subscription = True

    def close(self):
        if self._owns_subscription and self._subscription is not None:
            self._subscription.close()
            self._subscription = None
            self._owns_subscription = False

    def _record(self, kind, **values):
        if self._journal is None or self._journal_write_error is not None:
            return
        try:
            event = {'kind': kind, 'at': time.time(), **copy.deepcopy(values)}
            if self._events_path is not None:
                self._record_tribute(event)
                return
            self._journal['events'].append(event)
            self._write_checkpoint()
        except Exception as exc:
            # Diagnostics cannot authorize, block, or retry a game action.
            self._journal_write_error = '日志保存失败: ' + str(exc)

    def _write_checkpoint(self):
        self.data_dir.mkdir(parents=True, exist_ok=True)
        temporary = self._path.with_suffix('.tmp')
        with temporary.open('w', encoding='utf-8') as stream:
            json.dump(self._journal, stream, ensure_ascii=False, sort_keys=True)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, self._path)

    @staticmethod
    def _tribute_summary(event):
        """Bound checkpoint history; original evidence stays in the JSONL log."""
        def scalars(value):
            return {key: item[:512] if isinstance(item, str) else item
                    for key, item in value.items()
                    if item is None or type(item) in (str, int, float, bool)}

        def observed(value):
            summary = scalars(value)
            if isinstance(value.get('operation'), dict):
                summary['operation'] = scalars(value['operation'])
            return summary

        summary = scalars(event)
        for key in ('command', 'policy_state', 'result', 'receipt'):
            if isinstance(event.get(key), dict):
                summary[key] = scalars(event[key])
        for key in ('before', 'observed'):
            if isinstance(event.get(key), dict):
                summary[key] = observed(event[key])
        if isinstance(event.get('wire'), dict):
            wire = event['wire']
            ack = wire.get('ack') or {}
            summary['wire'] = scalars(wire)
            summary['wire']['ack'] = scalars(ack)
            summary['wire']['ack']['result'] = {'development': {'last_command': scalars(
                ((ack.get('result') or {}).get('development') or {}).get('last_command') or {})}}
        return summary

    def _record_tribute(self, event):
        # Append each full event once; keep the checkpoint's event tail bounded.
        self.data_dir.mkdir(parents=True, exist_ok=True)
        sequence = self._journal['event_log']['count'] + 1
        detailed = dict(event, sequence=sequence,
                        operation_id=self._journal['operation_id'])
        encoded = (json.dumps(detailed, ensure_ascii=False, sort_keys=True) + '\n').encode('utf-8')
        with self._events_path.open('ab') as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
            durable_bytes = stream.tell()
        summary = self._tribute_summary(detailed)
        self._journal['events'] = (self._journal['events'] + [summary])[-8:]
        self._journal['event_log'].update(count=sequence, durable_bytes=durable_bytes)
        counts = self._journal['event_counts']
        kind = event['kind']
        counts[kind] = counts.get(kind, 0) + 1
        if kind == 'intent':
            self._journal['pending_intent_sequence'] = sequence
            self._journal['policy_state'] = copy.deepcopy(event['policy_state'])
        elif kind in ('settled', 'not_submitted', 'not_invoked'):
            self._journal['pending_intent_sequence'] = None
        elif kind == 'observation':
            self._journal['latest_observation'] = summary['observed']
        elif kind == 'result':
            self._journal['latest_result'] = copy.deepcopy(event['result'])
        self._write_checkpoint()

    def _begin(self, operation_id, module, goal):
        """Create this run's diagnostic output without reading prior runs."""
        self._journal = None
        self._path = self.data_dir / (operation_id + '.json')
        self._events_path = None
        self._journal_write_error = None
        self._progress = ''
        if module in ('tribute', 'star'):
            self._events_path = self.data_dir / (operation_id + '.events.jsonl')
        self._journal = {'schema': 1, 'operation_id': operation_id,
            'context_key': self.token.context_key, 'module': module, 'goal': goal,
            'unresolved': None, 'events': []}
        if self._events_path is not None:
            self._journal.update(schema=2, events_format='recent-summary-v1',
                event_log={'format': 'development-events-jsonl-v1',
                           'path': self._events_path.name, 'count': 0, 'durable_bytes': 0},
                event_counts={}, pending_intent_sequence=None, policy_state=None,
                latest_observation=None, latest_result=None)
        self._record('created')

    def _result(self, status, reason, evidence='UNVERIFIED'):
        result = WorkflowResult(status, reason, evidence, str(self._path or ''), self._progress)
        self._record('result', result=asdict(result))
        if self._journal_write_error is not None:
            warning = self._journal_write_error
            result = WorkflowResult(status, reason, evidence, str(self._path or ''),
                (self._progress + ' · ' + warning).strip(' ·'))
        self.publish(result)
        return result

    def _observe(self, fields=None):
        self._subscribe()
        self._subscription.drain()
        # A typed observation binds the native target for this operation. The
        # watcher publishes later state changes without another game request.
        observed = (self.objects.observe_development(self.token, fields=fields)
                    if fields is not None else self._subscription.latest())
        if observed.get('context_key') != self.token.context_key:
            raise RuntimeError('development_context_changed')
        if observed.get('stopping'):
            raise CampaignAdmissionClosed('development_stopping')
        development = observed['development']
        if type(development.get('revision')) is not int or development['revision'] < 1:
            raise RuntimeError('development_revision_unknown')
        return development

    def _fields(self, module, operation_id, target, revision):
        if module == 'equipment':
            return DevelopmentCommand('equipment_observe', revision, operation_id,
                target.equipment_id, target.desired_level, target.max_requests,
                target.max_money, target.max_stone, mode=target.mode).to_payload()
        if module == 'spirit':
            return DevelopmentCommand('spirit_observe', revision, operation_id, 0,
                target.desired_level, target.max_requests, max_food=target.max_food).to_payload()
        if module == 'tribute':
            return DevelopmentCommand('tribute_observe', revision, operation_id, 0,
                target.desired_level, 2147483647 if target.use_all else target.max_requests,
                max_food=2147483647, mode='fruit').to_payload()
        if module == 'star':
            return DevelopmentCommand('star_observe', revision, operation_id, 0, 0, 196,
                mode='practice', max_practice=2147483647 if target.max_practice is None
                else target.max_practice).to_payload()
        return DevelopmentCommand('growth_observe', revision, operation_id,
            target or 0, 0, 10).to_payload()

    def _admission(self):
        while self.paused() and not self.stopped():
            policy = self._active_daily_tribute
            if policy is not None and self.pending is None and policy._pending is None:
                policy.request_checkpoint()
            self.wait()
        return not self.stopped()

    def step(self, policy, module, development):
        """Plan from this observation and submit once; never retry an unknown."""
        if not self._admission():
            return self._result('STOPPED', 'user_stop_pending_retained' if self.pending
                else 'development_user_stopped', getattr(policy, '_evidence', 'UNVERIFIED'))
        outcome = policy.plan(development[module], revision=development['revision'],
                              context_key=self.token.context_key)
        if self.pending is not None:
            command = self.pending.to_payload()
            op = development[module].get('operation') or {}
            exact = all(op.get(k) == command.get(k) for k in
                ('operation_id', 'expected_revision', 'action', 'target_id', 'desired_level', 'mode'))
            # The pure policy clears its pending command only after checking
            # the exact receipt and postcondition. A later quote/budget gate
            # can block a new step without making that settled call uncertain.
            accepted = policy._pending is None or (outcome.status == 'COMMAND' and
                policy._pending.nonce != self.pending.nonce)
            if exact and op.get('status') == 'OBSERVED':
                self._record('observation', observed=development[module], status=outcome.status, reason=outcome.reason)
                if accepted:
                    self.pending = None
                    self._journal['unresolved'] = None
                    self._record('settled')
        if outcome.status != 'COMMAND':
            if outcome.status == 'BLOCKED' and self.pending is not None:
                return self._result('UNKNOWN', outcome.reason, outcome.evidence)
            return outcome
        command = outcome.command
        if self.pending is not None:
            return self._result('UNKNOWN', 'prior_command_unresolved')
        self.pending = command
        self._journal['unresolved'] = command.to_payload()
        self._record('intent', before=development[module], command=command.to_payload(), nonce=command.nonce,
            policy_state={key: copy.deepcopy(getattr(policy, key)) for key in
                ('_last_revision', '_requests', '_money', '_stone', '_food', '_practice', '_sequence', '_before')})
        if isinstance(policy, DailySpiritTribute) and self.paused() and not self.stopped():
            policy.defer_prepared(command)
            self.pending = None
            self._journal['unresolved'] = None
            self._record('not_submitted', reason='daily_tribute_paused_before_submit')
            return policy._out('WAITING', 'daily_tribute_paused_before_submit')
        if not self._admission():
            self._journal['unresolved'] = None
            self.pending = None
            self._record('not_submitted', reason='user_stop')
            return self._result('STOPPED', 'development_user_stopped',
                                getattr(policy, '_evidence', 'UNVERIFIED'))
        try:
            wire = self.objects.submit_development(self.token, **command.to_payload())
            self._record('ack', wire=wire)
            if wire.get('cancelled_before_submit') is True:
                self.pending = None
                self._journal['unresolved'] = None
                self._record('not_submitted', reason='cancelled_before_submit')
                return self._result('STOPPED' if self.stopped() else 'BLOCKED',
                    'development_user_stopped' if self.stopped() else 'cancelled_before_submit',
                    getattr(policy, '_evidence', 'UNVERIFIED'))
            ack = wire.get('ack') or {}
            receipt = ((ack.get('result') or {}).get('development') or {}).get('last_command') or {}
            seq = wire.get('requested_seq', wire.get('seq'))
            if (type(seq) is not int or ack.get('command_seq') != seq or
                    receipt.get('sequence') != seq or receipt.get('action') != command.action or
                    receipt.get('operation_id') != command.operation_id):
                return self._result('UNKNOWN', 'development_ack_correlation_failed')
            if ack.get('status') == 'REJECTED' and receipt.get('status') in ('REJECTED', 'NOT_INVOKED'):
                self.pending = None
                self._journal['unresolved'] = None
                self._record('not_invoked', receipt=receipt)
                if (module == 'equipment' and isinstance(policy, EquipmentUpgrade)
                        and command.action == 'equipment_close'
                        and receipt.get('reason') == 'development_context_revision_or_pending'):
                    return self._replan_equipment_close(policy, development, command)
                return self._result('BLOCKED', str(receipt.get('reason') or 'native_rejected'))
            if ack.get('status') != 'COMPLETED' or receipt.get('status') != 'INVOKED':
                return self._result('UNKNOWN', 'development_invocation_unconfirmed')
        except CampaignAdmissionClosed as exc:
            self.pending = None
            self._journal['unresolved'] = None
            self._record('not_submitted', reason=str(exc))
            return self._result('STOPPED' if self.stopped() else 'BLOCKED',
                'development_user_stopped' if self.stopped() else str(exc),
                getattr(policy, '_evidence', 'UNVERIFIED'))
        except Exception as exc:
            self._record('io_unknown', reason=str(exc))
            return self._result('UNKNOWN', str(exc))
        return outcome

    def _replan_equipment_close(self, policy, before, command):
        """Replan one definitely uninvoked close, never a resource request."""
        closing = policy._closing_ui
        replanned = getattr(policy, '_close_replanned', set())
        if (closing is None or closing in replanned or policy._pending != command
                or command.operation_id != policy.operation_id):
            return self._result('BLOCKED', 'equipment_close_replan_limit_or_binding')
        name, instance_id = closing
        original = before['equipment']
        operation = original.get('operation') or {}
        candidate = policy._candidate(original)
        ui = original.get('ui') or {}
        identity = ('equipment_id', 'owner_id', 'slot', 'level', 'main_id', 'next_main_id')
        operation_identity = ('operation_id', 'sequence', 'expected_revision', 'action',
                              'target_id', 'desired_level', 'mode', 'status', 'opcode')
        if (operation.get('status') not in ('IDLE', 'OBSERVED')
                or operation.get('operation_id') not in ('', command.operation_id)
                or candidate.get('equipment_id') != command.target_id
                or type(candidate.get('level')) is not int
                or candidate['level'] < command.desired_level
                or candidate.get('in_current_formation') is not True
                or candidate.get('owner_slot_matches') is not True):
            return self._result('BLOCKED', 'equipment_close_replan_identity_unknown')
        deadline = self.clock() + min(5., max(.1, self.timeout))
        self._record('equipment_close_replan_wait', rejected_revision=command.expected_revision,
            panel=name, panel_instance_id=instance_id)
        while True:
            if not self._admission():
                return self._result('STOPPED', 'user_stop')
            fresh = self._observe()
            snapshot = fresh['equipment']
            current_op = snapshot.get('operation') or {}
            current_candidate = policy._candidate(snapshot)
            current_ui = snapshot.get('ui') or {}
            panel = current_ui.get(name) or {}
            if (fresh.get('character_id') != before.get('character_id')
                    or any(snapshot.get(key) != original.get(key)
                           for key in ('schema', 'target_id', 'desired_level', 'mode'))
                    or any(current_op.get(key) != operation.get(key) for key in operation_identity)
                    or any(current_candidate.get(key) != candidate.get(key) for key in identity)
                    or current_candidate.get('in_current_formation') is not True
                    or current_candidate.get('owner_slot_matches') is not True
                    or any(current_ui.get(key) != ui.get(key)
                           for key in ('bound_equipment_id', 'owner_id', 'slot', 'page'))
                    or panel.get('instance_id') != instance_id or panel.get('visible') is not True):
                return self._result('BLOCKED', 'equipment_close_replan_binding_changed')
            gate = snapshot.get('gate')
            if (fresh.get('gate') != 'PASS' or gate not in ('PASS', 'WAITING_NETWORK')
                    or gate == 'WAITING_NETWORK' and snapshot.get('reason') != 'equipment_network_not_idle'):
                return self._result('BLOCKED', 'equipment_close_replan_gate_changed')
            settled = all((fresh.get(module, {}).get('operation') or {}).get('status') in ('IDLE', 'OBSERVED')
                          for module in ('growth', 'equipment', 'spirit', 'tribute', 'star'))
            if (fresh['revision'] > command.expected_revision and gate == 'PASS' and settled
                    and panel.get('enabled') is True and current_ui.get('sended') is False
                    and snapshot.get('available_action') == 'equipment_close'):
                # The exact rejection above proves this pending command did
                # not run. Keep its operation/budgets, and let the normal policy
                # create a new single-use close from the freshly admitted UI.
                policy._close_replanned = replanned | {closing}
                policy._pending = None
                policy._closing_ui = None
                self._record('equipment_close_replanned', rejected_revision=command.expected_revision,
                    revision=fresh['revision'], panel=name, panel_instance_id=instance_id)
                return self.step(policy, 'equipment', fresh)
            if self.clock() >= deadline:
                return self._result('BLOCKED', 'equipment_close_replan_timeout')
            self.wait()

    def _cleanup_tribute(self, policy):
        """Release owned panels; replan once only after a proven noninvocation."""
        if (not isinstance(policy, SpiritTribute) or self.stopped() or self.paused()
                or policy._max_food is None or self._journal is None
                or self._journal.get('operation_id') != policy.operation_id):
            return

        def matches(operation, command):
            payload = command.to_payload()
            return all(operation.get(key) == payload.get(key) for key in
                ('operation_id', 'expected_revision', 'action', 'target_id', 'desired_level', 'mode'))

        try:
            development = self._observe()
            snapshot = development['tribute']
            operation = snapshot.get('operation') or {}
            if operation.get('operation_id') != policy.operation_id:
                return
            if self.pending is not None:
                # An acknowledged local open can leave the policy waiting for
                # a panel snapshot. It has no outstanding server mutation.
                if (self.pending.action not in ('tribute_open', 'tribute_panel_open')
                        or operation.get('status') != 'OBSERVED' or operation.get('opcode') != 0
                        or not matches(operation, self.pending)):
                    return
                self.pending = None
                self._journal['unresolved'] = None
                self._record('settled', ui_invocation_only=True, reason='tribute_failure_cleanup')
            if operation.get('status') not in ('OBSERVED', 'ERROR'):
                return
            closed = set()
            replanned = set()
            while len(closed) < 2 and not self.stopped() and not self.paused():
                snapshot = development['tribute']
                operation = snapshot.get('operation') or {}
                action = snapshot.get('cleanup_action')
                if action not in ('tribute_close', 'tribute_parent_close'):
                    return
                if action in closed or operation.get('operation_id') != policy.operation_id:
                    self._record('tribute_cleanup_unconfirmed', reason='cleanup_ownership_or_progress_changed')
                    return
                name = 'spirit' if action == 'tribute_close' else 'parent'
                panel = (snapshot.get('ui') or {}).get(name) or {}
                if (panel.get('visible') is not True or panel.get('enabled') is not True
                        or type(panel.get('instance_id')) is not int or panel['instance_id'] == 0):
                    return
                policy._sequence += 1
                command = DevelopmentCommand(action, development['revision'], policy.operation_id,
                    0, policy.target.desired_level, policy.effective_max_requests,
                    max_food=policy._max_food, mode='fruit', nonce=policy._sequence,
                    context_key=self.token.context_key)
                self.pending = command
                self._journal['unresolved'] = command.to_payload()
                self._record('intent', cleanup=True, before=snapshot, command=command.to_payload(),
                    nonce=command.nonce, policy_state={key: copy.deepcopy(getattr(policy, key)) for key in
                        ('_last_revision', '_requests', '_money', '_stone', '_food', '_practice', '_sequence', '_before')})
                if self.stopped() or self.paused():
                    self.pending = None
                    self._journal['unresolved'] = None
                    self._record('not_submitted', cleanup=True, reason='user_stop_or_pause')
                    return
                wire = self.objects.submit_development(self.token, **command.to_payload())
                self._record('ack', cleanup=True, wire=wire)
                ack = wire.get('ack') or {}
                receipt = ((ack.get('result') or {}).get('development') or {}).get('last_command') or {}
                sequence = wire.get('requested_seq', wire.get('seq'))
                exact = (type(sequence) is int and ack.get('command_seq') == sequence
                    and receipt.get('sequence') == sequence and receipt.get('action') == action
                    and receipt.get('operation_id') == policy.operation_id)
                if wire.get('cancelled_before_submit') is True or (exact and
                        ack.get('status') == 'REJECTED' and receipt.get('status') in ('REJECTED', 'NOT_INVOKED')):
                    self.pending = None
                    self._journal['unresolved'] = None
                    self._record('not_invoked', cleanup=True, reason=receipt.get('reason', 'cancelled_before_submit'))
                    # A changing UI snapshot can invalidate the close's
                    # revision before native admission. Only its exact
                    # REJECTED receipt proves no close ran; never resubmit an
                    # unknown or possibly invoked command.
                    if (wire.get('cancelled_before_submit') is not True and exact
                            and ack.get('status') == 'REJECTED' and receipt.get('status') == 'REJECTED'
                            and receipt.get('reason') == 'development_context_revision_or_pending'
                            and action not in replanned):
                        fresh = self._observe()
                        refreshed = fresh['tribute']
                        refreshed_op = refreshed.get('operation') or {}
                        refreshed_panel = (refreshed.get('ui') or {}).get(name) or {}
                        same_operation = all(refreshed_op.get(key) == operation.get(key) for key in
                            ('operation_id', 'sequence', 'expected_revision', 'action',
                             'target_id', 'desired_level', 'mode', 'status', 'opcode'))
                        if (fresh.get('gate') == 'PASS' and fresh['revision'] > command.expected_revision
                                and same_operation and refreshed.get('cleanup_action') == action
                                and refreshed_panel.get('instance_id') == panel['instance_id']
                                and refreshed_panel.get('visible') is True
                                and refreshed_panel.get('enabled') is True):
                            replanned.add(action)
                            development = fresh
                            self._record('tribute_cleanup_replanned', reason='rejected_revision_changed',
                                rejected_revision=command.expected_revision, revision=fresh['revision'],
                                panel_instance_id=panel['instance_id'])
                            continue
                    return
                if not exact or ack.get('status') != 'COMPLETED' or receipt.get('status') != 'INVOKED':
                    self._record('tribute_cleanup_unconfirmed', reason='cleanup_invocation_unconfirmed')
                    return
                deadline = self.clock() + min(5., max(.1, self.timeout))
                while not self.stopped() and not self.paused():
                    development = self._observe()
                    snapshot = development['tribute']
                    operation = snapshot.get('operation') or {}
                    after = (snapshot.get('ui') or {}).get(name) or {}
                    if (matches(operation, command) and operation.get('status') == 'OBSERVED'
                            and operation.get('opcode') == 0 and after.get('visible') is False
                            and after.get('instance_id') in (0, panel['instance_id'])):
                        self.pending = None
                        self._journal['unresolved'] = None
                        self._record('observation', cleanup=True, observed=snapshot)
                        self._record('settled', cleanup=True)
                        closed.add(action)
                        break
                    if self.clock() >= deadline:
                        self._record('tribute_cleanup_unconfirmed', reason='cleanup_close_observation_timeout')
                        return
                    self.wait()
                if self.pending is not None:
                    return
        except Exception as exc:
            # Preserve the original outcome; cleanup evidence is separate.
            self._record('tribute_cleanup_unconfirmed', reason=str(exc))

    @_workflow_scope(native_entry_panels=('star',))
    def execute(self, module, target, *, policy=None, tribute_checkpoint=None):
        """Run one operation; daily tribute checkpoints keep its owned UI open.

        tribute_checkpoint(policy) observes role, cycle and account progress
        before opening and after each confirmed tribute. Return None to admit
        the next step, or WorkflowResult to end without sending another request.
        """
        previous = self._active_daily_tribute
        self._active_daily_tribute = policy if module == 'tribute' and isinstance(policy, DailySpiritTribute) else None
        try:
            result = self._execute(module, target, policy=policy, tribute_checkpoint=tribute_checkpoint)
            if isinstance(policy, _GrowthRewardConfirm) and result.status != 'DONE':
                self._progress = '领取已成功，但奖励确认未完成'
                self._record('growth_reward_confirmation_incomplete', quest_id=policy.quest_id,
                    claim_operation_id=policy.claim_operation_id, result=asdict(result))
                return self._result(result.status,
                    'growth_claim_succeeded_reward_confirmation_incomplete: ' + result.reason,
                    'SERVER_CONFIRMED')
            if self._active_daily_tribute is None or not isinstance(result, WorkflowResult):
                return result
            cleanup = 'UNVERIFIED'
            try:
                snapshot = self._observe()['tribute']
                ui = snapshot.get('ui') or {}
                if (self.pending is None and ui.get('nested_owned') is False and
                        all((ui.get(name) or {}).get('visible') is False
                            for name in ('spirit', 'parent', 'rankup'))):
                    cleanup = 'CONFIRMED'
            except Exception as exc:
                self._record('tribute_cleanup_unconfirmed', reason=str(exc))
            status, reason = result.status, result.reason
            if status == 'DONE' and cleanup != 'CONFIRMED':
                status, reason = 'UNKNOWN', 'daily_tribute_cleanup_unconfirmed'
            result = replace(result, status=status, reason=reason, cleanup=cleanup)
            self._record('result', result=asdict(result))
            self.publish(result)
            return result
        finally:
            self._active_daily_tribute = previous

    def _execute(self, module, target, *, policy=None, tribute_checkpoint=None):
        if not self._admission():
            return WorkflowResult('STOPPED', 'development_user_stopped')
        if self.pending is not None:
            return self._result('UNKNOWN', 'prior_command_unresolved')
        operation_id = policy.operation_id if policy else uuid.uuid4().hex
        goal = asdict(target) if module != 'growth' else {'quest_id': target or 0}
        try:
            self._begin(operation_id, module, goal)
            initial = self._observe()
            fields = self._fields(module, operation_id, target, initial['revision'])
            policy_types = {'equipment': EquipmentUpgrade, 'spirit': SpiritUpgrade,
                            'tribute': SpiritTribute, 'star': StarActivation}
            policy = policy or policy_types[module](operation_id, self.token.context_key, target)
            deadline = self.clock() + self.timeout
            development = self._observe(fields)
            last_revision = None
            while True:
                if not self._admission():
                    return self._result('STOPPED', 'user_stop_pending_retained' if self.pending
                        else 'development_user_stopped', getattr(policy, '_evidence', 'UNVERIFIED'))
                if isinstance(policy, DailySpiritTribute) and policy._checkpoint_due and self.pending is None:
                    last_revision = None
                if development['revision'] == last_revision:
                    if self.clock() >= deadline:
                        if (development.get('gate') in ('WAITING_UI', 'WAITING_NETWORK', 'WAITING')
                                and self.pending is None):
                            return self._result('BLOCKED', 'development_ui_wait_timeout')
                        return self._result('UNKNOWN' if self.pending else 'BLOCKED', 'development_observation_timeout')
                    self.wait()
                    development = self._observe()
                    continue
                last_revision = development['revision']
                if (development.get('gate') in ('WAITING_UI', 'WAITING_NETWORK', 'WAITING') and self.pending is None
                        and not isinstance(policy, _GrowthRewardConfirm)):
                    if self.clock() >= deadline:
                        return self._result('BLOCKED', 'development_ui_wait_timeout')
                    self.wait()
                    development = self._observe()
                    continue
                outcome = self.step(policy, module, development)
                if isinstance(outcome, WorkflowResult):
                    return outcome
                if module == 'star':
                    star = development[module]
                    self._progress = (f"已确认激活 {policy.activations} 次 · 星灵等级 {star.get('level')} · "
                        f"剩余战魂 {star.get('practice')} · 已用战魂 {policy.spent_practice}")
                    self.publish(WorkflowResult('RUNNING', 'star_running', outcome.evidence,
                                                str(self._path or ''), self._progress))
                if module == 'tribute':
                    tribute = development[module]
                    request_progress = (f"已发起 {outcome.requests} 次" if target.use_all else
                                        f"已发起 {outcome.requests}/{policy.effective_max_requests} 次")
                    level = tribute.get('level')
                    stage = (f"第{(level - 1) // 10 + 1}阶 第{(level - 1) % 10 + 1}星"
                             if type(level) is int and level > 0 else '阶数待确认')
                    if target.desired_level:
                        stage += (f" · 目标第{(target.desired_level - 1) // 10 + 1}阶 "
                                  f"第{(target.desired_level - 1) % 10 + 1}星")
                    spent = (outcome.reserved_food if policy._pending is None else
                             tribute.get('spent_food', 0))
                    food_progress = f"已用桃子 {spent}"
                    self._progress = (request_progress + ' · ' +
                                stage + f" · 经验 {tribute.get('experience')} · "
                                f"剩余桃子 {tribute.get('food')} · " + food_progress)
                    self.publish(WorkflowResult('RUNNING', 'tribute_running', outcome.evidence,
                                                str(self._path or ''), self._progress))
                if outcome.status == 'CHECKPOINT':
                    if (module != 'tribute' or not isinstance(policy, DailySpiritTribute)
                            or tribute_checkpoint is None):
                        return self._result('BLOCKED', 'daily_tribute_checkpoint_required', outcome.evidence)
                    self._record('tribute_checkpoint', confirmed_tributes=policy.confirmed_tributes,
                        spent_food=policy.spent_food, receipt=policy.last_receipt)
                    checkpoint = tribute_checkpoint(policy)
                    if isinstance(checkpoint, WorkflowResult):
                        return self._result(checkpoint.status, checkpoint.reason, checkpoint.evidence)
                    if checkpoint is not None:
                        return self._result('BLOCKED', 'daily_tribute_checkpoint_result_invalid', outcome.evidence)
                    if self.stopped():
                        return self._result('STOPPED', 'development_user_stopped', outcome.evidence)
                    if self.paused():
                        last_revision = None
                        continue
                    policy.confirm_checkpoint()
                    # The account push can change development's revision while
                    # the checkpoint is waiting. Always plan from a fresh read.
                    development = self._observe()
                    last_revision = None
                    deadline = self.clock() + self.timeout
                    continue
                if outcome.status in ('DONE', 'BLOCKED', 'UNSUPPORTED', 'DISPATCH'):
                    if outcome.status == 'DISPATCH':
                        self._record('dispatch', outcome=asdict(outcome))
                        return outcome
                    reason = outcome.reason
                    if getattr(outcome, 'capability', '') and outcome.status == 'BLOCKED':
                        reason += ': ' + outcome.capability
                    return self._result(outcome.status, reason, outcome.evidence)
                if outcome.status == 'COMMAND':
                    # A subsequent COMMAND proves the prior pending step was
                    # accepted by the policy. step must see an empty I/O slot.
                    deadline = self.clock() + self.timeout
                if self.clock() >= deadline:
                    return self._result('UNKNOWN' if self.pending else 'BLOCKED', 'development_observation_timeout')
                self.wait()
                development = self._observe()
        except CampaignAdmissionClosed as exc:
            if self.stopped() and self.pending is None:
                return self._result('STOPPED', 'development_user_stopped',
                                    getattr(policy, '_evidence', 'UNVERIFIED'))
            return self._result('UNKNOWN' if self.pending else 'BLOCKED', str(exc))
        except Exception as exc:
            return self._result('UNKNOWN' if self.pending else 'BLOCKED', str(exc))
        finally:
            if module == 'tribute':
                self._cleanup_tribute(policy)

    def equipment(self, target):
        return self.execute('equipment', target)

    def spirit(self, target):
        return self.execute('spirit', target)

    def tribute(self, target):
        return self.execute('tribute', target)

    def star(self, target):
        return self.execute('star', target)

    @staticmethod
    def equipment_for_quest(snapshot, desired_level, *, equipment_id=None):
        if type(desired_level) is not int or not 1 <= desired_level <= 255:
            raise ValueError('equipment_goal_unknown')
        candidates = [c for c in snapshot.get('candidates', []) if
            type(c.get('level')) is int and 0 <= c['level'] <= 255 and c.get('slot') in (0, 1) and
            c.get('in_current_formation') is True and
            c.get('owner_slot_matches') is True and
            type(c.get('owner_id')) is int and 0 <= c['owner_id'] < 65535 and
            type(c.get('equipment_id')) is int and 1 <= c['equipment_id'] <= 255 and
            (equipment_id is None or c['equipment_id'] == equipment_id)]
        if not candidates:
            raise ValueError('no_owned_equipped_candidate')
        candidate = max(candidates, key=lambda c: c['level'])
        if candidate['level'] >= desired_level:
            return EquipmentTarget(candidate['equipment_id'], desired_level)
        money, stone = snapshot.get('available_money'), snapshot.get('available_stone')
        if type(money) is not int or type(stone) is not int or min(money, stone) < 0:
            raise ValueError('resources_unknown')
        difference = desired_level - candidate['level']
        if difference < 1 or difference > 100:
            raise ValueError('equipment_goal_difference_outside_budget')
        up = (candidate.get('cost') or {}).get('up')
        if type(up) is not int or up not in (0, 1):
            raise ValueError('quote_unavailable')
        # An evolution is an explicit single-step goal. Reobserve the quest
        # before constructing a new mode; a policy never crosses modes.
        return EquipmentTarget(candidate['equipment_id'], candidate['level'] + 1 if up else desired_level,
            'evolve' if up else 'enchant', 1 if up else difference, money, stone)

    def _growth_campaign(self, quest):
        """Use the ordinary map entry once, then wait for the observed scene."""
        submitted = False
        try:
            if not self._admission():
                return self._result('STOPPED', 'user_stop')
            self.objects.observe_campaign(self.token)
            # The typed observation refreshes the shared state but returns
            # only the campaign projection. Read all admission evidence from
            # one fresh workflow snapshot belonging to this same token.
            observed = self.objects.workflow_snapshot(self.token)
            if observed.get('context_key') != self.token.context_key or observed.get('stopping'):
                return self._result('BLOCKED', 'growth_navigation_context_changed')
            state = observed.get('state') or {}
            common = state.get('snapshot') or {}
            root = common.get('root_level_id')
            if root == 'Campaign':
                return None
            if root != 'Capital' or self.token.scope != 'campaign':
                return self._result('BLOCKED', 'growth_navigation_source_unavailable')
            snap = observed.get('campaign') or {}
            development = observed.get('development') or {}
            if self.pending is not None or any((development.get(name, {}).get('operation') or {}).get('status')
                    not in ('IDLE', 'OBSERVED') for name in ('growth', 'equipment', 'spirit', 'tribute', 'star')):
                return self._result('UNKNOWN', 'growth_navigation_prior_operation_unresolved')
            attempt = snap.get('attempt') or {}
            if (snap.get('phase') != 'MAIN' or snap.get('available') is not True or snap.get('gate') != 'PASS' or
                    common.get('safe') is not True or common.get('main_enabled') is not True or
                    attempt.get('status') not in ('NONE', 'SETTLED') or
                    type(snap.get('revision')) is not int or snap['revision'] < 1):
                return self._result('BLOCKED', 'growth_navigation_normal_entry_unavailable')
            identity = (quest.quest_id, quest.type, quest.param0, quest.param1)
            current = next((q for q in _quests(development.get('growth') or {}) if q.quest_id == quest.quest_id), None)
            if current is None or (current.quest_id, current.type, current.param0, current.param1) != identity or current.isgot != 0:
                return self._result('BLOCKED', 'growth_navigation_task_changed')
            self._begin(uuid.uuid4().hex, 'growth', {'quest_id': quest.quest_id,
                'navigation': 'Campaign', 'quest_identity': identity})
            fields = {'action': 'enter', 'expected_revision': snap['revision'],
                'campaign_id': 0, 'troop_id': 0, 'attempt_id': ''}
            self._journal['unresolved'] = dict(fields)
            self._record('navigation_intent', command=fields, source_root=root,
                before_campaign=snap, before_growth=development['growth'])
            if not self._admission():
                self._journal['unresolved'] = None
                self._record('navigation_not_submitted', reason='user_stop')
                return self._result('STOPPED', 'user_stop')
            submitted = True
            wire = self.objects.submit_campaign(self.token, **fields)
            self._record('navigation_ack', wire=wire)
            if wire.get('cancelled_before_submit') is True:
                submitted = False
                self._journal['unresolved'] = None
                return self._result('BLOCKED', 'growth_navigation_cancelled_before_submit')
            ack = wire.get('ack') or {}
            receipt = (ack.get('result') or {}).get('campaign_command') or {}
            seq = wire.get('requested_seq', wire.get('seq'))
            if (type(seq) is not int or ack.get('command_seq') != seq or receipt.get('command_seq') != seq or
                    receipt.get('action') != 'enter' or receipt.get('attempt_id') != ''):
                return self._result('UNKNOWN', 'growth_navigation_ack_correlation_failed')
            if ack.get('status') == 'REJECTED' and receipt.get('invoked') is False:
                submitted = False
                self._journal['unresolved'] = None
                return self._result('BLOCKED', str(receipt.get('reason') or 'growth_navigation_rejected'))
            if ack.get('status') != 'COMPLETED' or receipt.get('invoked') is not True:
                return self._result('UNKNOWN', 'growth_navigation_invocation_unconfirmed')
            self._progress = '正在进入征战地图'
            self.publish(WorkflowResult('RUNNING', 'growth_navigation', ledger_path=str(self._path), progress=self._progress))
            deadline = self.clock() + self.timeout
            last_observation = None
            while True:
                if not self._admission():
                    return self._result('STOPPED', 'user_stop_navigation_pending_retained')
                self._subscription.drain()
                observed = self._subscription.latest()
                if (observed.get('context_key') != self.token.context_key or observed.get('stopping') or
                        observed.get('admission_open') is not True):
                    return self._result('UNKNOWN', 'growth_navigation_context_or_admission_changed')
                state = observed.get('state') or {}
                common = state.get('snapshot') or {}
                fresh = observed.get('campaign') or {}
                ui = fresh.get('ui_context') or {}
                signature = (common.get('root_level_id'), fresh.get('revision'), fresh.get('phase'), fresh.get('gate'))
                if signature != last_observation:
                    self._record('navigation_observation', root=common.get('root_level_id'), campaign=fresh)
                    last_observation = signature
                if (common.get('root_level_id') == 'Campaign' and ui.get('current_root_level') == 'Campaign' and
                        ui.get('current_root_active') is True and fresh.get('phase') == 'CAMPAIGN' and
                        fresh.get('available') is True and fresh.get('gate') == 'PASS' and
                        common.get('main_enabled') is True and ui.get('global_input_enabled') is True and
                        type(fresh.get('revision')) is int and fresh['revision'] > snap['revision']):
                    self._journal['unresolved'] = None
                    self._record('navigation_completed', evidence='LOCAL_OBSERVED', target_root='Campaign')
                    self._progress = ''
                    return None
                if self.clock() >= deadline:
                    return self._result('UNKNOWN', 'growth_navigation_observation_timeout')
                self.wait()
        except CampaignAdmissionClosed as exc:
            if self._journal is not None:
                self._journal['unresolved'] = None
            self._record('navigation_not_submitted', reason=str(exc))
            return self._result('STOPPED' if self.stopped() else 'BLOCKED', str(exc))
        except Exception as exc:
            self._record('navigation_error', reason=str(exc))
            return self._result('UNKNOWN' if submitted else 'BLOCKED', str(exc))

    @_workflow_scope
    def current_growth_task(self, *, quest_id=None):
        """Complete only the selected quest, dismiss its reward, return to map.

        Equipment, owned-artifact wearing and one bounded free-blue soul run
        use their ordinary UI policies. Campaign requirements return to caller.
        The caller owns the next campaign action and the next growth-task call.
        """
        try:
            if not self._admission():
                return self._result('STOPPED', 'user_stop')
            if self.pending is not None:
                return self._result('UNKNOWN', 'prior_command_unresolved')
            development = self._observe()
            growth = development['growth']
            quests = _quests(growth)
            if growth.get('window_visible') is True:
                selected = next((q for q in quests
                    if q.quest_id == growth.get('ui_selected_quest_id')), None)
                if selected is None:
                    return self._result('BLOCKED', 'current_growth_ui_target_unknown')
            else:
                selected = select_growth_task(growth)
            if selected is None:
                return self._result('DONE', 'no_current_growth_task', 'LOCAL_OBSERVED')
            if quest_id is not None and (type(quest_id) is not int or quest_id != selected.quest_id):
                return self._result('BLOCKED', 'current_growth_task_changed')
            identity = (selected.quest_id, selected.type, selected.param0, selected.param1)
            protagonist_weapon = identity == (3, 3, 5, 0)
            equipment_id = None

            def quest_equipment(snapshot):
                if not protagonist_weapon:
                    return snapshot
                return dict(snapshot, candidates=[c for c in snapshot.get('candidates', [])
                    if c.get('owner_id') == 0 and c.get('slot') == 0])

            if selected.isgot == 1:
                return self._result('BLOCKED', 'local_claim_unverified')
            navigation = self._growth_campaign(selected)
            if navigation is not None:
                return navigation
            # A normal scene change invalidates old UI references and quotes.
            # Keep this quest identity, but read its actual current state anew.
            development = self._observe()
            growth = development['growth']
            current = next((q for q in _quests(growth) if q.quest_id == selected.quest_id), None)
            if current is None or (current.quest_id, current.type, current.param0, current.param1) != identity or current.isgot != 0:
                return self._result('BLOCKED', 'current_growth_task_changed')
            selected = current
            if selected.isfinish != 1 and selected.condition_met is not True:
                campaign = {7: 'CHARACTER_LEVEL', 8: 'CAMPAIGN_TROOP', 11: 'CAMPAIGN_CHAPTER'}
                if selected.type in campaign:
                    if growth.get('window_visible') is True:
                        result = self.close_growth(growth['ui_selected_quest_id'])
                        if result.status != 'DONE':
                            return result
                    return self._result('NEEDS_CAMPAIGN', campaign[selected.type])
                if selected.type in (4, 5, 6):
                    policy = _GrowthRequirement(uuid.uuid4().hex, self.token.context_key, selected)
                    result = self.execute('growth', selected.quest_id, policy=policy)
                    if result.status != 'DONE':
                        return result
                    if selected.type == 6:
                        return self.claim_finished_growth(quest_id=selected.quest_id)
                elif selected.type != 3:
                    return self._result('UNSUPPORTED', 'current_growth_type_' + str(selected.type))
                # Read the quest before consulting growth.gate: an already open
                # equipment panel legitimately blocks growth UI actions.
                if selected.type == 3 and growth.get('window_visible') is True:
                    result = self.close_growth(growth['ui_selected_quest_id'])
                    if result.status != 'DONE':
                        return result
                previous_level = None
                weapon_prepared = False
                for _ in range(100) if selected.type == 3 else ():
                    if not self._admission():
                        return self._result('STOPPED', 'user_stop')
                    development = self._observe()
                    current = next((q for q in _quests(development['growth'])
                        if q.quest_id == selected.quest_id), None)
                    if current is None or (current.quest_id, current.type, current.param0, current.param1) != identity:
                        return self._result('BLOCKED', 'current_growth_task_changed')
                    if current.isfinish == 1 or current.condition_met is True:
                        break
                    try:
                        target = self.equipment_for_quest(quest_equipment(development['equipment']),
                            selected.param0, equipment_id=equipment_id)
                    except ValueError as exc:
                        if str(exc) != 'no_owned_equipped_candidate' or weapon_prepared or equipment_id is not None:
                            raise
                        preparation = _GrowthWeaponPreparation(uuid.uuid4().hex, self.token.context_key, selected)
                        result = self.execute('growth', selected.quest_id, policy=preparation)
                        if result.status != 'DONE':
                            return result
                        equipment_id = preparation.equipment_id
                        weapon_prepared = True
                        self._record('growth_weapon_preparation', result=asdict(result))
                        continue
                    equipment_id = target.equipment_id
                    candidate = next(c for c in development['equipment']['candidates']
                        if c['equipment_id'] == equipment_id)
                    if weapon_prepared and (candidate.get('owner_id'), candidate.get('slot')) != (0, 0):
                        return self._result('BLOCKED', 'growth_weapon_prepared_owner_changed')
                    if previous_level is not None and candidate['level'] <= previous_level:
                        return self._result('BLOCKED', 'current_growth_equipment_no_progress')
                    previous_level = candidate['level']
                    result = self.equipment(target)
                    if result.status == 'DONE':
                        continue
                    if (result.status == 'BLOCKED' and self.pending is None and result.reason in (
                            'equipment_mode_mismatch', 'equipment_mode_evolve_required',
                            'equipment_mode_enchant_required')):
                        continue
                    return result
                else:
                    if selected.type == 3:
                        return self._result('BLOCKED', 'current_growth_equipment_step_limit')
            development = self._observe()
            equipment = development['equipment']
            ui = equipment.get('ui') or {}
            if selected.type == 3 and any((ui.get(name) or {}).get('visible') is True
                    for name in ('reinforce', 'equipment')):
                # An already satisfied target still needs its ordinary return
                # chain. This target can close panels but cannot spend resources.
                target = self.equipment_for_quest(quest_equipment(equipment), selected.param0,
                    equipment_id=equipment_id if protagonist_weapon else None)
                candidate = next(c for c in equipment['candidates']
                    if c['equipment_id'] == target.equipment_id)
                if candidate['level'] < selected.param0:
                    return self._result('BLOCKED', 'current_growth_equipment_close_target_unknown')
                result = self.equipment(target)
                if result.status != 'DONE':
                    return result
            if selected.type == 3:
                # Equipment completion can finish another tutorial quest and
                # move MainUI's selection. Re-read both flags and the real UI
                # target before binding any check/claim to the original quest.
                growth = self._observe()['growth']
                current = next((q for q in _quests(growth) if q.quest_id == selected.quest_id), None)
                if (current is None or (current.quest_id, current.type, current.param0, current.param1) != identity
                        or current.isgot != 0):
                    return self._result('BLOCKED', 'current_growth_task_changed')
                actual_selection = growth.get('ui_selected_quest_id') if growth.get('window_visible') is True else growth.get('selected_quest_id')
                if current.isfinish == 1:
                    # Normal equipment/tutorial callbacks can finish several
                    # quests and move MainUI to another unclaimed row. Bind a
                    # fresh explicit claim to this observed finished quest;
                    # do not repeat its upgrade/check or fake the UI selection.
                    self._record('current_growth_finished_claim_rebound', quest_id=current.quest_id,
                        selected_quest_id=actual_selection, isfinish=current.isfinish, isgot=current.isgot)
                    return self.claim_finished_growth(quest_id=current.quest_id)
                if actual_selection != selected.quest_id:
                    self._record('current_growth_selection_required', quest_id=selected.quest_id,
                        selected_quest_id=actual_selection, isfinish=current.isfinish, isgot=current.isgot)
                    return self._result('BLOCKED', 'current_growth_selection_required')
                selected = current
            # A single bound dispatcher owns check/claim. Automatic tutorial
            # completion may already have opened the correct task window.
            policy = _CurrentGrowthDispatcher(uuid.uuid4().hex, self.token.context_key, selected)
            result = self.execute('growth', selected.quest_id, policy=policy)
            if result.status != 'DONE':
                if result.status == 'DISPATCH':
                    return self._result('BLOCKED', 'current_growth_requirement_not_met')
                return result
            if result.reason != 'quest_claim_server_confirmed' or result.evidence != 'SERVER_CONFIRMED':
                return self._result('BLOCKED', 'current_growth_claim_not_confirmed', result.evidence)
            claim_evidence = result.evidence
            reward = _GrowthRewardConfirm(uuid.uuid4().hex, self.token.context_key, selected.quest_id,
                claim_operation_id=policy.operation_id, quest=selected)
            result = self.execute('growth', selected.quest_id, policy=reward)
            if result.status != 'DONE':
                return result
            fresh = self._observe()['growth']
            if fresh.get('window_visible') is True:
                # Claiming may select the next unclaimed row. Close that actual
                # window selection without checking or claiming its task.
                close_id = fresh.get('ui_selected_quest_id')
                if type(close_id) is not int or close_id < 1:
                    return self._result('BLOCKED', 'current_growth_close_target_unknown')
                result = self.close_growth(close_id)
                if result.status != 'DONE':
                    return result
            elif fresh.get('window_visible') is not False:
                return self._result('BLOCKED', 'current_growth_close_unverified')
            return self._result('DONE', 'current_growth_task_completed', claim_evidence)
        except Exception as exc:
            return self._result('STOPPED' if self.stopped() else 'BLOCKED', str(exc))

    @_workflow_scope
    def claim_finished_growth(self, *, quest_id=None):
        """Claim the observed finished set in game selection order, or one ID."""
        remaining, completed = {}, []
        reopened = set()
        try:
            if not self._admission():
                return self._result('STOPPED', 'user_stop')
            if self.pending is not None:
                return self._result('UNKNOWN', 'prior_command_unresolved')
            if quest_id is not None and (type(quest_id) is not int or not 1 <= quest_id <= 32767):
                return self._result('BLOCKED', 'finished_growth_target_changed')
            growth = self._observe()['growth']
            remaining = {q.quest_id: q for q in _quests(growth) if q.isfinish == 1 and q.isgot == 0}
            if not remaining:
                return self._result('BLOCKED', 'no_finished_unclaimed_growth_task')
            if quest_id is not None:
                if quest_id not in remaining:
                    return self._result('BLOCKED', 'finished_growth_target_changed')
                remaining = {quest_id: remaining[quest_id]}
            # This finite set is the authorization boundary. A callback may add
            # another task, but this workflow never completes its requirements
            # or claims a task that was not finished at admission.
            while remaining:
                if not self._admission():
                    return self._result('STOPPED', 'user_stop')
                if self.pending is not None:
                    return self._result('UNKNOWN', 'prior_command_unresolved')
                self._observe()
                observed = self.objects.workflow_snapshot(self.token)
                if (observed.get('context_key') != self.token.context_key or observed.get('stopping')
                        or observed.get('admission_open') is not True):
                    return self._result('UNKNOWN', 'finished_growth_context_unconfirmed')
                development = observed['development']
                growth = development['growth']
                visible = growth.get('window_visible')
                if visible is not True and visible is not False:
                    return self._result('BLOCKED', 'finished_growth_selection_unknown')
                selected_id = (growth.get('ui_selected_quest_id') if visible is True
                    else growth.get('selected_quest_id'))
                target_id = quest_id if quest_id is not None else selected_id
                if type(target_id) is not int or target_id not in remaining:
                    self._record('finished_growth_selection_required', selected_quest_id=selected_id,
                        remaining_quest_ids=list(remaining))
                    return self._result('BLOCKED', 'finished_growth_selection_required')
                quest = remaining[target_id]
                current = next((q for q in _quests(growth) if q.quest_id == target_id), None)
                identity = (quest.quest_id, quest.type, quest.param0, quest.param1)
                if (current is None or current.isfinish != 1 or current.isgot != 0 or
                        (current.quest_id, current.type, current.param0, current.param1) != identity):
                    return self._result('BLOCKED', 'finished_growth_target_changed')
                if quest_id is not None and visible is True and selected_id != target_id:
                    # A normal tutorial callback may leave another row open.
                    # Close that real row without checking/claiming it, then
                    # reobserve before the explicit finished target is opened.
                    if target_id in reopened:
                        return self._result('BLOCKED', 'finished_growth_reopen_no_progress')
                    if (type(selected_id) is not int or selected_id < 1
                            or not any(q.quest_id == selected_id for q in _quests(growth))):
                        return self._result('UNKNOWN', 'finished_growth_open_selection_unconfirmed')
                    reward = growth.get('reward_dialog') or {}
                    reward_wait = growth.get('reward_wait') or {}
                    business = (observed.get('state') or {}).get('business') or {}
                    settled = all((development.get(module, {}).get('operation') or {}).get('status') in ('IDLE', 'OBSERVED')
                                  for module in ('growth', 'equipment', 'spirit', 'tribute', 'star'))
                    if (development.get('gate') != 'PASS' or growth.get('gate') != 'PASS'
                            or growth.get('window_enabled') is not True or growth.get('show_window') is not True
                            or reward.get('visible') is not False or growth.get('reward_quest_id') != 0
                            or reward_wait.get('phase') != 'NONE' or reward_wait.get('claim_confirmed') is not False
                            or reward_wait.get('bound') is not False
                            or any(reward_wait.get(field) is not None and reward_wait.get(field) is not False
                                   for field in ('loading', 'collecting', 'show_visible_pending'))
                            or not settled or not all(business.get(field) is False for field in (
                                'pending_attempt', 'pending_development', 'pending_player_refresh'))):
                        return self._result('UNKNOWN', 'finished_growth_reopen_state_unconfirmed')
                    reopened.add(target_id)
                    self._record('finished_growth_close_other_selection', quest_id=target_id,
                        selected_quest_id=selected_id, quest_identity=identity)
                    result = self.close_growth(selected_id)
                    if result.status != 'DONE':
                        return result
                    continue
                policy = _FinishedGrowthClaim(uuid.uuid4().hex, self.token.context_key, current,
                    explicit_target=quest_id is not None)
                result = self.execute('growth', target_id, policy=policy)
                if result.status != 'DONE':
                    return result
                if result.reason != 'quest_claim_server_confirmed' or result.evidence != 'SERVER_CONFIRMED':
                    return self._result('BLOCKED', 'finished_growth_claim_not_confirmed', result.evidence)
                claim = dict(quest_id=target_id, quest_type=quest.type, param0=quest.param0,
                    param1=quest.param1, claim_operation_id=policy.operation_id,
                    claim_ledger_path=result.ledger_path, evidence=result.evidence)
                result = self.execute('growth', target_id,
                    policy=_GrowthRewardConfirm(uuid.uuid4().hex, self.token.context_key, target_id,
                        claim_operation_id=policy.operation_id, quest=current))
                if result.status != 'DONE':
                    return result
                claim['reward_ledger_path'] = result.ledger_path
                fresh = self._observe()['growth']
                if fresh.get('window_visible') is True:
                    close_id = fresh.get('ui_selected_quest_id')
                    if type(close_id) is not int or close_id < 1:
                        return self._result('BLOCKED', 'finished_growth_close_target_unknown')
                    result = self.close_growth(close_id)
                    if result.status != 'DONE':
                        return result
                    claim['close_ledger_path'] = result.ledger_path
                elif fresh.get('window_visible') is not False:
                    return self._result('BLOCKED', 'finished_growth_close_unverified')
                # A new operation may start only after reward/close settled and
                # a fresh publication proves this exact quest remains claimed.
                growth = self._observe()['growth']
                current = next((q for q in _quests(growth) if q.quest_id == target_id), None)
                if (growth.get('window_visible') is not False or current is None or
                        current.isfinish != 1 or current.isgot != 1 or
                        (current.quest_id, current.type, current.param0, current.param1) != identity):
                    return self._result('BLOCKED', 'finished_growth_cleanup_or_claim_unverified')
                del remaining[target_id]
                completed.append(claim)
                self._record('finished_growth_claim_completed', **claim)
            claimed_ids = ', '.join(str(claim['quest_id']) for claim in completed)
            return self._result('DONE', 'finished_growth_task_claimed: ' + claimed_ids, 'SERVER_CONFIRMED')
        except Exception as exc:
            return self._result('STOPPED' if self.stopped() else 'BLOCKED', str(exc))
        finally:
            if completed:
                self._record('finished_growth_claims_summary', claims=completed,
                    remaining_quest_ids=list(remaining))

    @_workflow_scope
    def growth_pass(self, *, max_quests=32):
        seen = set()
        for _ in range(max_quests):
            if not self._admission():
                return WorkflowResult('STOPPED', 'user_stop')
            development = self._observe()
            quest = select_growth_task(development['growth'])
            if quest is None:
                if development['growth'].get('window_visible') is not False:
                    return self._result('BLOCKED', 'growth_window_open_without_selected_task')
                return self._result('DONE', 'no_unfinished_or_unclaimed_task_observed', 'LOCAL_OBSERVED')
            operation_id = uuid.uuid4().hex
            policy = GrowthTaskDispatcher(operation_id, self.token.context_key,
                ('EQUIPMENT_UPGRADE', 'CHARACTER_LEVEL', 'CAMPAIGN_TROOP', 'CAMPAIGN_CHAPTER'))
            result = self.execute('growth', quest.quest_id, policy=policy)
            if result.status == 'DISPATCH':
                closed = self.close_growth(quest.quest_id)
                if closed.status != 'DONE':
                    return closed
                if result.dispatch.capability in ('CHARACTER_LEVEL', 'CAMPAIGN_TROOP', 'CAMPAIGN_CHAPTER'):
                    return self._result('NEEDS_CAMPAIGN', result.dispatch.capability)
                if result.dispatch.capability == 'EQUIPMENT_UPGRADE':
                    fresh = self._observe()
                    try:
                        target = self.equipment_for_quest(fresh['equipment'], result.dispatch.param0)
                    except ValueError as exc:
                        return self._result('BLOCKED', str(exc))
                    result = self.equipment(target)
                    if (result.status == 'BLOCKED' and result.reason == 'equipment_mode_mismatch' and
                            self.pending is None):
                        # The last exact request settled, and a newly quoted
                        # evolution now requires a separate explicit goal.
                        continue
                    if result.status == 'DONE':
                        continue
                return result
            if result.status != 'DONE':
                return result
            if result.reason == 'quest_claim_server_confirmed':
                closed = self.close_growth(quest.quest_id)
                if closed.status != 'DONE':
                    return closed
                if quest.quest_id in seen:
                    return self._result('BLOCKED', 'claimed_quest_selected_again')
                seen.add(quest.quest_id)
                continue
            if result.reason == 'quest_finish_server_confirmed':
                continue
            return result
        return self._result('BLOCKED', 'growth_pass_limit_reached')

    def close_growth(self, quest_id):
        policy = _GrowthClose(uuid.uuid4().hex, self.token.context_key, quest_id)
        return self.execute('growth', quest_id, policy=policy)


class DevelopmentCoordinator:
    """Standalone GUI task using the same ownership and cleanup as campaign."""
    def __init__(self, objects, runner, data_dir):
        self.objects, self.runner, self.data_dir = objects, runner, data_dir
        self.events = EventMailbox('development')
        self._lock = threading.RLock()
        self._wake = WakeSignal()
        self._active = self._paused = self._stopping = self._closing = False
        self._token = None
        self._snapshot = asdict(WorkflowResult('IDLE', 'ready'))

    @property
    def is_active(self):
        with self._lock:
            return self._active

    @property
    def is_paused(self):
        with self._lock:
            return self._paused

    @property
    def is_shutdown(self):
        return not self.is_active

    @property
    def snapshot(self):
        with self._lock:
            return copy.deepcopy(self._snapshot)

    def _publish(self, result):
        with self._lock:
            self._snapshot = asdict(result)
        self.events.put(result)

    def start(self, module='growth', target=None, *, expected_character_id=None,
              expected_user_id=None, expected_zone_id=None):
        for expected in (expected_character_id, expected_user_id, expected_zone_id):
            if expected is not None and (
                    not isinstance(expected, str) or not expected.isascii()
                    or not expected.isdecimal() or not expected.strip('0')):
                return False
        targets = {'equipment': EquipmentTarget, 'spirit': SpiritTarget,
                   'tribute': TributeTarget, 'star': StarTarget}
        if module in targets:
            if type(target) is not targets[module]:
                return False
        elif module not in ('growth', 'claim_growth') or target is not None:
            return False
        with self._lock:
            if self._active or self._closing or self.runner.is_active or self.objects.has_campaign_owner:
                return False
            self._active, self._paused, self._stopping = True, False, False
            self._wake.clear()
            accepted = self.runner.start('星灵激活' if module == 'star' else
                '灵兽上贡' if module == 'tribute' else '成长与培养',
                lambda ctx: self._run(ctx, module, target, expected_character_id,
                                      expected_user_id, expected_zone_id), checkpoints=False)
            if not accepted:
                self._active = False
            return accepted

    def pause(self):
        with self._lock:
            if not self._active or self._stopping:
                return False
            self._paused = True
            if self._token:
                self.objects.pause_campaign(self._token)
            self.runner.pause()
            self._wake.set()
            return True

    def resume(self):
        with self._lock:
            if not self._active or not self._paused or self._stopping:
                return False
            if self._token and not self.objects.resume_campaign(self._token):
                return False
            self._paused = False
            self.runner.resume()
            self._wake.set()
            return True

    def stop(self):
        with self._lock:
            if not self._active:
                return False
            self._stopping, self._paused = True, False
            if self._token:
                self.objects.stop_campaign(self._token)
            self.runner.stop()
            self._wake.set()
            return True

    def close(self):
        self._closing = True
        self.stop()

    def _wait(self, timeout=1.):
        self._wake.wait(timeout)

    def _run(self, ctx, module, target, expected_character_id=None, expected_user_id=None,
             expected_zone_id=None):
        token = connection = subscription = None
        try:
            self._publish(WorkflowResult('CONNECTING', 'connecting'))
            deadline = time.monotonic() + 30
            if self.objects.bridge_state != 'ready':
                connection = self.objects.begin_campaign_connection()
                if connection is None:
                    raise RuntimeError('object_connection_unavailable')
            while self.objects.bridge_state != 'ready' or self.objects.is_busy:
                if self._stopping or ctx.cancelled:
                    return
                if time.monotonic() >= deadline or (self.objects.bridge_state in ('error', 'unknown', 'disconnected') and not self.objects.is_busy):
                    raise RuntimeError('object_connection_failed_or_timeout')
                self._wait(.1)
            if self._stopping or ctx.cancelled:
                return
            token = self.objects.acquire_campaign(uuid.uuid4().hex)
            for field, expected in (('character', expected_character_id),
                                    ('user', expected_user_id), ('zone', expected_zone_id)):
                if expected is None:
                    continue
                try:
                    actual = json.loads(token.context_key)[field + '_id']
                except (AttributeError, KeyError, TypeError, ValueError) as exc:
                    raise RuntimeError('development_expected_' + field + '_context_invalid') from exc
                if actual != expected:
                    raise RuntimeError('development_expected_' + field + '_mismatch')
            with self._lock:
                self._token = token
                if self._stopping:
                    self.objects.stop_campaign(token)
                elif self._paused:
                    self.objects.pause_campaign(token)
            subscription = self.objects.subscribe_workflow(token, notify=self._wake.set)
            workflow = DevelopmentWorkflow(self.objects, token, self.data_dir,
                stopped=lambda: self._stopping or ctx.cancelled, paused=lambda: self._paused,
                wait=self._wait, publish=self._publish, subscription=subscription)
            self._publish(WorkflowResult('RUNNING', '领取已完成成长任务' if module == 'claim_growth' else module))
            result = (workflow.current_growth_task() if module == 'growth' else
                workflow.claim_finished_growth() if module == 'claim_growth' else workflow.execute(module, target))
            self._publish(result)
        except Exception as exc:
            self._publish(WorkflowResult('BLOCKED', str(exc)))
        finally:
            try:
                try:
                    if subscription is not None:
                        subscription.close()
                finally:
                    if token:
                        self.objects.release_campaign(token, cleanup=True)
                    elif connection is not None:
                        self.objects.cancel_campaign_connection(connection)
            except Exception as exc:
                self._publish(WorkflowResult('UNKNOWN', 'cleanup_unconfirmed: ' + str(exc)))
            finally:
                with self._lock:
                    self._active = self._paused = False
                    self._token = None
