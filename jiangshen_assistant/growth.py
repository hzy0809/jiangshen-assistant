"""Growth quests adapt requirements to registered capabilities, never to writes.

Condition observed, local finished/claimed flags, and correlated server receipts
are separate evidence. There is deliberately no invented spirit quest type.
"""
from dataclasses import dataclass
from typing import Any, Iterable, Mapping

from .development import DevelopmentCommand, _Policy, _int, _mapping


CAPABILITIES = {
    1: 'STAR_TECH', 2: 'EQUIPMENT_WEAR', 3: 'EQUIPMENT_UPGRADE',
    4: 'ARTIFACT_WEAR', 5: 'SOUL_GATHER', 6: 'GENERAL_RECRUIT',
    7: 'CHARACTER_LEVEL', 8: 'CAMPAIGN_TROOP', 9: 'FORMATION_GENERAL',
    10: 'CAPITAL_BUILDING', 11: 'CAMPAIGN_CHAPTER',
    12: 'EQUIPMENT_PART_CHANGE', 13: 'CAPITAL_MINE', 14: 'CAPITAL_PRODUCT',
}


@dataclass(frozen=True)
class GrowthQuest:
    quest_id: int
    isfinish: int
    isgot: int
    type: int
    param0: int
    param1: int
    exp: int
    money: int
    condition_met: bool | None
    name: str = ''
    description: str = ''


@dataclass(frozen=True)
class GrowthDispatch:
    capability: str
    quest_id: int
    param0: int
    param1: int


@dataclass(frozen=True)
class GrowthOutcome:
    status: str
    reason: str
    quest: GrowthQuest | None = None
    command: DevelopmentCommand | None = None
    dispatch: GrowthDispatch | None = None
    capability: str = ''
    evidence: str = 'UNVERIFIED'
    requests: int = 0


def _quests(snapshot: Mapping[str, Any]) -> tuple[GrowthQuest, ...]:
    raw = snapshot.get('tasks')
    if not isinstance(raw, (list, tuple)) or len(raw) > 128:
        raise ValueError('growth_task_list_unknown')
    result: list[GrowthQuest] = []
    seen: set[int] = set()
    for item in raw:
        q = _mapping(item)
        template = _mapping(q.get('template'))
        qid = q.get('quest_id')
        if (not _int(qid, 1) or qid > 32767 or qid in seen or
                template.get('quest_id') != qid or
                type(q.get('isfinish')) is not int or q.get('isfinish') not in (0, 1) or
                type(q.get('isgot')) is not int or q.get('isgot') not in (0, 1) or
                not _int(template.get('type'), 1) or
                type(template.get('param0')) is not int or type(template.get('param1')) is not int or
                not _int(template.get('exp')) or not _int(template.get('money')) or
                q.get('condition_met') is not None and type(q.get('condition_met')) is not bool):
            raise ValueError('growth_task_or_template_unknown')
        seen.add(qid)
        result.append(GrowthQuest(qid, q['isfinish'], q['isgot'], template['type'],
            template['param0'], template['param1'], template['exp'], template['money'],
            q.get('condition_met'), str(template.get('name') or ''), str(template.get('desc') or '')))
    return tuple(result)


def select_growth_task(snapshot: Mapping[str, Any]) -> GrowthQuest | None:
    """Match MainUI: reverse unfinished, otherwise forward unclaimed; no ID+1."""
    quests = _quests(snapshot)
    return next((q for q in reversed(quests) if q.isfinish != 1),
                next((q for q in quests if q.isgot != 1), None))


class GrowthTaskDispatcher(_Policy):
    def __init__(self, operation_id: str, context_key: str,
                 capabilities: Iterable[str] = (), max_requests: int = 10):
        super().__init__(operation_id, context_key)
        if not _int(max_requests, 1) or max_requests > 10:
            raise ValueError('growth max_requests must be 1..10')
        registered = frozenset(capabilities)
        if not registered <= set(CAPABILITIES.values()):
            raise ValueError('unknown growth capability')
        self._capabilities = registered
        self._max_requests = max_requests
        self._quest: GrowthQuest | None = None
        self._bound_quest: GrowthQuest | None = None
        self._claim_confirmed = False

    @property
    def capabilities(self) -> frozenset[str]:
        return self._capabilities

    @property
    def max_requests(self) -> int:
        return self._max_requests

    def _accept(self, snapshot: Mapping[str, Any], op: Mapping[str, Any], action: str) -> str:
        if action in ('growth_check', 'growth_claim'):
            opcode = 810 if action == 'growth_check' else 811
            if type(op.get('receipt_opcode')) is not int or op.get('receipt_opcode') != opcode or op.get('server') != 'PASS':
                return 'growth_receipt_unverified'
            try:
                rows = _quests(snapshot)
            except ValueError:
                return 'growth_postcondition_unknown'
            matching = [q for q in rows if q.quest_id == self._bound_quest.quest_id]
            if len(matching) != 1 or matching[0].isfinish != 1:
                return 'growth_finished_postcondition_missing'
            if action == 'growth_claim':
                if type(op.get('receipt_result')) is not int or op.get('receipt_result') != 0:
                    return 'growth_claim_result_unverified'
                if matching[0].isgot != 1:
                    return 'growth_claimed_postcondition_missing'
                self._claim_confirmed = True
                self._evidence = 'SERVER_CONFIRMED'
            else:
                self._evidence = 'SERVER_FINISH_CONFIRMED'
        elif action == 'growth_open':
            # Show may submit a combined 810 for OTHER quests. That scan is
            # owned native evidence, but its target membership is not exported;
            # retain the bound quest's existing evidence until exact check/claim.
            if snapshot.get('available_action') == action:
                return 'growth_open_no_progress'
        return ''

    def _result(self, status: str, reason: str, *, command: DevelopmentCommand | None = None,
                dispatch: GrowthDispatch | None = None, capability: str = '') -> GrowthOutcome:
        return GrowthOutcome(status, reason, self._quest, command, dispatch,
                             capability, self._evidence, self._requests)

    def plan(self, snapshot: Mapping[str, Any], *, revision: int, context_key: str) -> GrowthOutcome:
        early = self._admit(snapshot, revision, context_key)
        if early:
            return self._result(early.status, early.reason, command=early.command)
        try:
            selected = select_growth_task(snapshot)
            if self._bound_quest is not None and snapshot.get('window_visible') is True and snapshot.get('ui_selected_quest_id') == self._bound_quest.quest_id:
                selected = next((q for q in _quests(snapshot) if q.quest_id == self._bound_quest.quest_id), selected)
        except ValueError as exc:
            self._block(str(exc))
            return self._result('BLOCKED', str(exc))
        self._quest = selected
        if self._claim_confirmed:
            # One dispatcher owns one quest transaction. A fresh operation must
            # bind the newly selected task after the controller handles rewards.
            self._done = True
            self._quest = next((q for q in _quests(snapshot) if q.quest_id == self._bound_quest.quest_id), self._bound_quest)
            return self._result('DONE', 'quest_claim_server_confirmed')
        if selected is None:
            self._evidence = 'LOCAL_OBSERVED'
            self._done = True
            return self._result('DONE', 'no_unfinished_or_unclaimed_task_observed')
        if (self._bound_quest is not None and selected.quest_id != self._bound_quest.quest_id and
                self._evidence == 'SERVER_FINISH_CONFIRMED' and snapshot.get('window_visible') is False):
            self._done = True
            self._quest = next((q for q in _quests(snapshot) if q.quest_id == self._bound_quest.quest_id), self._bound_quest)
            return self._result('DONE', 'quest_finish_server_confirmed')
        effective_selected = snapshot.get('ui_selected_quest_id') if snapshot.get('window_visible') is True else snapshot.get('selected_quest_id')
        if effective_selected != selected.quest_id:
            return self._result('BLOCKED', 'selected_task_binding_mismatch')
        if self._bound_quest is not None and (
                self._bound_quest.quest_id, self._bound_quest.type, self._bound_quest.param0, self._bound_quest.param1) != (
                selected.quest_id, selected.type, selected.param0, selected.param1):
            self._block('quest_target_changed')
            return self._result('BLOCKED', 'quest_target_changed')
        self._bound_quest = selected
        capability = CAPABILITIES.get(selected.type)
        if capability is None:
            return self._result('BLOCKED', 'NeedsCapability', capability=f'QUEST_TYPE_{selected.type}')
        action = snapshot.get('available_action')
        # A native descriptor cannot manufacture a mapper or different target.
        row = next(q for q in snapshot['tasks'] if q['quest_id'] == selected.quest_id)
        if row.get('dispatch') not in (None, capability):
            return self._result('BLOCKED', 'native_dispatch_mismatch')
        if selected.isgot == 1:
            return self._result('BLOCKED', 'local_claim_unverified')
        if selected.isfinish == 1:
            if action not in ('growth_open', 'growth_claim'):
                return self._result('BLOCKED', 'native_action_outside_capability')
            if action == 'growth_claim' and (
                    snapshot.get('window_visible') is not True or snapshot.get('window_enabled') is not True or
                    snapshot.get('show_window') is not True or selected.exp <= 0 and selected.money <= 0):
                return self._result('BLOCKED', 'claim_button_gate_unknown')
        elif selected.condition_met is True:
            if action not in ('growth_open', 'growth_check'):
                return self._result('BLOCKED', 'native_action_outside_capability')
        else:
            # Dispatch only capabilities the coordinator actually implements.
            if capability not in self.capabilities:
                return self._result('BLOCKED', 'NeedsCapability', capability=capability)
            if selected.condition_met is None and selected.type not in (5, 10, 13, 14):
                return self._result('BLOCKED', 'requirement_data_unknown', capability=capability)
            return self._result('DISPATCH', 'requirement_needs_capability',
                dispatch=GrowthDispatch(capability, selected.quest_id, selected.param0, selected.param1), capability=capability)
        prepared = self._issue(str(action), revision, target_id=selected.quest_id,
            desired_level=0, max_requests=self.max_requests, request=True)
        return self._result(prepared.status, prepared.reason, command=prepared.command)
