"""Pure, single-step development policies. Nothing here performs an action.

Keep one instance per immutable operation and submit a returned command once.
An uncertain result ends that run without retry. A new explicit task creates a
new policy from the current game observation. Snapshots use the native schema.
"""
from dataclasses import dataclass
from typing import Any, Mapping
import re


def _int(value: object, minimum: int = 0) -> bool:
    return type(value) is int and value >= minimum


def _mapping(value: object) -> Mapping[str, Any]:
    return value if isinstance(value, Mapping) else {}


@dataclass(frozen=True)
class EquipmentTarget:
    equipment_id: int
    desired_level: int
    mode: str = 'enchant'
    max_requests: int = 1
    max_money: int = 0
    max_stone: int = 0

    def __post_init__(self) -> None:
        if (not _int(self.equipment_id, 1) or self.equipment_id > 255 or
                not _int(self.desired_level, 1) or self.desired_level > 255 or
                self.mode not in ('enchant', 'evolve') or
                not _int(self.max_requests, 1) or self.max_requests > 100 or
                not _int(self.max_money) or not _int(self.max_stone)):
            raise ValueError('invalid equipment target or budget')


@dataclass(frozen=True)
class SpiritTarget:
    desired_level: int
    max_requests: int = 1
    max_food: int = 0
    max_gold: int = 0

    def __post_init__(self) -> None:
        if (not _int(self.desired_level, 1) or self.desired_level > 100 or
                not _int(self.max_requests, 1) or self.max_requests > 100 or
                not _int(self.max_food) or self.max_gold != 0 or
                type(self.max_gold) is not int):
            raise ValueError('invalid spirit target or fruit-only budget')


@dataclass(frozen=True)
class TributeTarget:
    max_requests: int = 100
    use_all: bool = False
    target_tier: int = 0
    target_level: int = 0

    def __post_init__(self) -> None:
        if (type(self.use_all) is not bool or
                not self.use_all and (not _int(self.max_requests, 1) or self.max_requests > 100)):
            raise ValueError('tribute request limit must be between 1 and 100')
        if not _int(self.target_tier) or self.target_tier > 10:
            raise ValueError('tribute target tier must be 0 or between 1 and 10')
        if not _int(self.target_level) or self.target_level > 100:
            raise ValueError('tribute target level must be 0 or between 1 and 100')
        if (self.target_tier and self.target_level and
                self.target_level != (self.target_tier - 1) * 10 + 1):
            raise ValueError('conflicting tribute target tier and level')

    @property
    def desired_level(self) -> int:
        return self.target_level or ((self.target_tier - 1) * 10 + 1 if self.target_tier else 0)


@dataclass(frozen=True)
class StarTarget:
    """Use current battle souls; an optional ceiling can only reduce that stock."""
    max_practice: int | None = None

    def __post_init__(self) -> None:
        if self.max_practice is not None and (not _int(self.max_practice) or self.max_practice > 2147483647):
            raise ValueError('invalid battle-soul budget')


@dataclass(frozen=True)
class DevelopmentCommand:
    action: str
    expected_revision: int
    operation_id: str
    target_id: int
    desired_level: int
    max_requests: int
    max_money: int = 0
    max_stone: int = 0
    max_food: int = 0
    mode: str = ''
    nonce: int = 1
    context_key: str = ''
    max_practice: int | None = None

    def to_payload(self) -> dict[str, object]:
        """The port supplies its global sequence; nonce/context stay local."""
        payload = {key: getattr(self, key) for key in (
            'action', 'expected_revision', 'operation_id', 'target_id',
            'desired_level', 'max_requests', 'max_money', 'max_stone',
            'max_food', 'mode')}
        if self.action.startswith('star_'):
            payload['max_practice'] = self.max_practice
        return payload


@dataclass(frozen=True)
class DevelopmentOutcome:
    status: str
    reason: str
    command: DevelopmentCommand | None = None
    evidence: str = 'UNVERIFIED'
    requests: int = 0
    reserved_money: int = 0
    reserved_stone: int = 0
    reserved_food: int = 0
    reserved_practice: int = 0


class _Policy:
    def __init__(self, operation_id: str, context_key: str) -> None:
        if not isinstance(operation_id, str) or not re.fullmatch('[0-9a-fA-F]{32}', operation_id):
            raise ValueError('operation_id must be 32 hexadecimal characters')
        if not isinstance(context_key, str) or not context_key:
            raise ValueError('context_key required')
        self._operation_id, self._context_key = operation_id, context_key
        self._pending: DevelopmentCommand | None = None
        self._sequence = self._last_revision = self._requests = 0
        self._money = self._stone = self._food = 0
        self._practice = 0
        self._blocked = ''
        self._done = False
        self._evidence = 'UNVERIFIED'
        self._before: tuple[int, ...] = ()

    @property
    def operation_id(self) -> str:
        return self._operation_id

    @property
    def context_key(self) -> str:
        return self._context_key

    def _out(self, status: str, reason: str, command: DevelopmentCommand | None = None) -> DevelopmentOutcome:
        return DevelopmentOutcome(status, reason, command, self._evidence,
                                  self._requests, self._money, self._stone, self._food, self._practice)

    def _block(self, reason: str) -> DevelopmentOutcome:
        self._blocked = reason
        return self._out('BLOCKED', reason)

    def _admit(self, snapshot: Mapping[str, Any], revision: int, context_key: str) -> DevelopmentOutcome | None:
        if self._blocked:
            return self._out('BLOCKED', self._blocked)
        if context_key != self.context_key:
            return self._block('context_changed')
        if not _int(revision, 1):
            return self._block('revision_unknown')
        if self._done:
            return self._out('DONE', 'target_already_met')
        if revision <= self._last_revision:
            return self._out('WAITING', 'fresh_snapshot_required')
        self._last_revision = revision
        if type(snapshot.get('schema')) is not int or snapshot.get('schema') != 1:
            return self._block('snapshot_schema_unknown')
        op = _mapping(snapshot.get('operation'))
        if self._pending:
            pending = self._pending
            if (op.get('operation_id') != self.operation_id or op.get('expected_revision') != pending.expected_revision or
                    op.get('action') != pending.action):
                return self._out('WAITING', 'own_command_observation_required')
            if (op.get('target_id') != pending.target_id or op.get('desired_level') != pending.desired_level or
                    op.get('mode') != pending.mode):
                return self._block('operation_goal_mismatch')
            status = op.get('status')
            if status == 'WAITING':
                return self._out('WAITING', 'own_request_pending')
            if status != 'OBSERVED':
                return self._block('receipt_unknown' if status == 'UNKNOWN' else 'operation_error_or_unknown')
            failure = self._accept(snapshot, op, pending.action)
            if failure:
                return self._block(failure)
            self._pending = None
        elif op.get('status') not in ('IDLE', 'OBSERVED'):
            return self._block('foreign_or_unresolved_operation')
        gate = snapshot.get('gate')
        if gate != 'PASS':
            if gate in ('WAITING', 'WAITING_UI', 'WAITING_NETWORK'):
                return self._out('WAITING', str(snapshot.get('reason') or gate))
            return self._block(str(snapshot.get('reason') or 'native_gate_unknown'))
        return None

    def _accept(self, snapshot: Mapping[str, Any], op: Mapping[str, Any], action: str) -> str:
        if snapshot.get('available_action') == action:
            return 'ui_step_no_progress'
        return ''

    def _issue(self, action: str, revision: int, *, target_id: int, desired_level: int,
               max_requests: int, max_money: int = 0, max_stone: int = 0,
               max_food: int = 0, mode: str = '', request: bool = False,
               money: int = 0, stone: int = 0, food: int = 0,
               max_practice: int | None = None, practice: int = 0) -> DevelopmentOutcome:
        if request and self._requests >= max_requests:
            return self._block('request_budget_exhausted')
        self._sequence += 1
        self._pending = DevelopmentCommand(action, revision, self.operation_id,
            target_id, desired_level, max_requests, max_money, max_stone,
            max_food, mode, self._sequence, self.context_key, max_practice)
        if request:
            self._requests += 1
            self._money += money
            self._stone += stone
            self._food += food
            self._practice += practice
        return self._out('COMMAND', 'single_use_command_prepared', self._pending)


class StarActivation(_Policy):
    """Sequential normal activation from this run's live battle-soul stock."""
    _terminal_reasons = frozenset(('star_maxed', 'star_no_next_activation',
        'star_insufficient_practice', 'star_request_budget_reached', 'star_practice_budget_reached'))

    def __init__(self, operation_id: str, context_key: str, target: StarTarget):
        super().__init__(operation_id, context_key)
        self._target = target
        self._max_practice: int | None = None
        self._spent_practice = self._activations = 0
        self._reserved_cost = 0
        self._dialog_id: int | None = None
        self._closing_id: int | None = None

    @property
    def activations(self) -> int:
        return self._activations

    @property
    def spent_practice(self) -> int:
        return self._spent_practice

    def _accept(self, snapshot: Mapping[str, Any], op: Mapping[str, Any], action: str) -> str:
        ui = _mapping(snapshot.get('ui'))
        if action in ('star_reward_confirm', 'star_close'):
            panel = _mapping(ui.get('special' if action == 'star_reward_confirm' else 'star'))
            instance_id = self._dialog_id if action == 'star_reward_confirm' else self._closing_id
            if (instance_id is None or panel.get('visible') is not False or
                    panel.get('instance_id') not in (0, instance_id)):
                return 'star_reward_confirm_unverified' if action == 'star_reward_confirm' else 'star_close_unverified'
            self._dialog_id = self._closing_id = None
            return ''
        if action != 'star_activate':
            return super()._accept(snapshot, op, action)
        level, practice, gold = snapshot.get('level'), snapshot.get('practice'), snapshot.get('gold')
        if (op.get('opcode') != 514 or op.get('receipt_opcode') != 514 or
                op.get('receipt_result') is not None or op.get('server') != 'PASS' or
                op.get('server_progress_observed') is not True or
                not _int(level) or not _int(practice) or not _int(gold) or
                op.get('receipt_level') != level or op.get('receipt_practice') != practice):
            return 'star_receipt_unverified'
        if level != self._before[0] + 1:
            return 'star_no_progress'
        if gold != self._before[2]:
            return 'star_gold_changed'
        spent = self._spent_practice + self._reserved_cost
        if (practice != self._before[1] - self._reserved_cost or
                snapshot.get('spent_practice') != spent or self._max_practice is None or spent > self._max_practice):
            return 'star_consumption_mismatch'
        self._spent_practice = spent
        self._activations += 1
        self._evidence = 'SERVER_CONFIRMED'
        return ''

    def plan(self, snapshot: Mapping[str, Any], *, revision: int, context_key: str) -> DevelopmentOutcome:
        early = self._admit(snapshot, revision, context_key)
        if early:
            return early
        level, practice, gold = snapshot.get('level'), snapshot.get('practice'), snapshot.get('gold')
        if (not _int(level) or level > 196 or not _int(practice) or practice > 2147483647 or not _int(gold)):
            return self._block('star_data_unknown')
        if self._max_practice is None:
            self._max_practice = min(practice, self._target.max_practice if self._target.max_practice is not None else practice)
        action, reason = snapshot.get('available_action'), snapshot.get('reason')
        ui = _mapping(snapshot.get('ui'))
        if snapshot.get('done') is True:
            if reason not in self._terminal_reasons:
                return self._block('star_terminal_reason_unknown')
            if not action:
                # A natural stop need not dismiss the game's cap notification
                # or change an existing panel's selected page.
                self._done = True
                if self._evidence == 'UNVERIFIED':
                    self._evidence = 'LOCAL_OBSERVED'
                return self._out('DONE', str(reason))
            if action not in ('star_close', 'star_reward_confirm'):
                return self._block('star_action_unavailable')
        elif action == 'star_close':
            return self._block('star_action_unavailable')
        if action not in ('star_open', 'star_activate', 'star_reward_confirm', 'star_close'):
            return self._block(str(reason or 'star_action_unavailable'))
        reserve = 0
        if action == 'star_activate':
            cost = snapshot.get('next_cost')
            if not _int(cost):
                return self._block('star_cost_unknown')
            if practice < cost:
                return self._block('star_insufficient_practice')
            if self._practice + cost > self._max_practice:
                return self._block('star_practice_budget_reached')
            if self._requests >= 196 or level >= 196 or ui.get('animating') is not False:
                return self._block('star_action_unavailable')
            self._before = (level, practice, gold)
            self._reserved_cost = reserve = cost
        if action in ('star_reward_confirm', 'star_close'):
            panel = _mapping(ui.get('special' if action == 'star_reward_confirm' else 'star'))
            if (panel.get('visible') is not True or panel.get('enabled') is not True or
                    type(panel.get('instance_id')) is not int or panel['instance_id'] == 0):
                return self._block('star_reward_confirm_unverified' if action == 'star_reward_confirm' else 'star_close_unverified')
            if action == 'star_reward_confirm':
                self._dialog_id = panel['instance_id']
            else:
                self._closing_id = panel['instance_id']
        return self._issue(str(action), revision, target_id=0, desired_level=0,
            max_requests=196, mode='practice', max_practice=self._max_practice,
            request=action == 'star_activate', practice=reserve)


class EquipmentUpgrade(_Policy):
    def __init__(self, operation_id: str, context_key: str, target: EquipmentTarget):
        super().__init__(operation_id, context_key)
        self._target = target
        self._closing_ui: tuple[str, int] | None = None

    @property
    def target(self) -> EquipmentTarget:
        return self._target

    def _candidate(self, snapshot: Mapping[str, Any]) -> Mapping[str, Any]:
        candidates = snapshot.get('candidates')
        if not isinstance(candidates, (list, tuple)):
            return {}
        found = [c for c in candidates if isinstance(c, Mapping) and c.get('equipment_id') == self.target.equipment_id]
        return found[0] if len(found) == 1 else {}

    def _accept(self, snapshot: Mapping[str, Any], op: Mapping[str, Any], action: str) -> str:
        if action == 'equipment_close':
            if self._closing_ui is None:
                return 'equipment_close_identity_unknown'
            name, instance_id = self._closing_ui
            panel = _mapping(_mapping(snapshot.get('ui')).get(name))
            if panel.get('instance_id') != instance_id or panel.get('visible') is not False:
                return 'ui_step_no_progress'
            self._closing_ui = None
            return ''
        if action != 'equipment_enchant':
            return super()._accept(snapshot, op, action)
        candidate = self._candidate(snapshot)
        level = candidate.get('level')
        if (type(op.get('receipt_opcode')) is not int or op.get('receipt_opcode') != 1104 or
                type(op.get('receipt_id')) is not int or op.get('receipt_id') != self.target.equipment_id or
                op.get('server_progress_observed') is not True or not _int(level) or
                level <= self._before[0] or op.get('receipt_level') != level):
            return 'equipment_progress_receipt_unverified'
        # The pinned callback does not establish a successful result enum.
        self._evidence = 'SERVER_PROGRESS_OBSERVED'
        return ''

    def plan(self, snapshot: Mapping[str, Any], *, revision: int, context_key: str) -> DevelopmentOutcome:
        early = self._admit(snapshot, revision, context_key)
        if early:
            return early
        t = self.target
        if (snapshot.get('target_id') != t.equipment_id or snapshot.get('desired_level') != t.desired_level or
                snapshot.get('mode') != t.mode):
            return self._block('target_binding_mismatch')
        c = self._candidate(snapshot)
        level = c.get('level')
        if not _int(level) or c.get('slot') not in (0, 1) or not _int(c.get('owner_id')) or c.get('owner_id') == 65535:
            return self._block('equipment_data_unknown')
        action = snapshot.get('available_action')
        if level >= t.desired_level:
            if self._evidence == 'UNVERIFIED':
                self._evidence = 'LOCAL_OBSERVED'
            if action != 'equipment_close':
                self._done = True
                return self._out('DONE', 'target_level_met')
        allowed = ('equipment_open', 'reinforce_open', 'enchant_tab', 'equipment_enchant', 'equipment_close')
        if not action:
            return self._block(str(snapshot.get('reason') or 'native_action_unavailable'))
        if action not in allowed or action == 'equipment_close' and level < t.desired_level:
            return self._block('native_action_outside_capability')
        money = stone = 0
        if action == 'equipment_close':
            ui = _mapping(snapshot.get('ui'))
            self._closing_ui = None
            for name in ('reinforce', 'equipment'):
                panel = _mapping(ui.get(name))
                if panel.get('visible') is True and type(panel.get('instance_id')) is int and panel['instance_id'] != 0:
                    self._closing_ui = name, panel['instance_id']
                    break
            if self._closing_ui is None:
                return self._block('equipment_close_identity_unknown')
        if action == 'equipment_enchant':
            ui = _mapping(snapshot.get('ui'))
            if (ui.get('bound_equipment_id') != t.equipment_id or ui.get('owner_id') != c.get('owner_id') or
                    ui.get('slot') != c.get('slot') or ui.get('page') != 0 or ui.get('sended') is not False):
                return self._block('equipment_ui_binding_mismatch')
            quote = _mapping(c.get('cost'))
            money, stone, up = quote.get('money'), quote.get('stone'), quote.get('up')
            if quote.get('cost_status') != 'OBSERVED' or not _int(money) or not _int(stone) or type(up) is not int or up not in (0, 1):
                return self._block('quote_unavailable')
            if (up == 1) != (t.mode == 'evolve'):
                return self._block('equipment_mode_mismatch')
            if up == 1 and not _int(c.get('next_main_id'), 1):
                return self._block('evolution_template_unknown')
            if not _int(snapshot.get('available_money')) or not _int(snapshot.get('available_stone')):
                return self._block('resources_unknown')
            if snapshot['available_money'] < money or snapshot['available_stone'] < stone:
                return self._block('resource_insufficient')
            if self._money + money > t.max_money or self._stone + stone > t.max_stone:
                return self._block('resource_budget_exhausted')
            self._before = (level,)
        return self._issue(action, revision, target_id=t.equipment_id,
            desired_level=t.desired_level, max_requests=t.max_requests,
            max_money=t.max_money, max_stone=t.max_stone, mode=t.mode,
            request=action == 'equipment_enchant', money=money, stone=stone)


class SpiritUpgrade(_Policy):
    def __init__(self, operation_id: str, context_key: str, target: SpiritTarget):
        super().__init__(operation_id, context_key)
        self._target = target

    @property
    def target(self) -> SpiritTarget:
        return self._target

    def _accept(self, snapshot: Mapping[str, Any], op: Mapping[str, Any], action: str) -> str:
        if action not in ('spirit_train', 'spirit_rankup'):
            return super()._accept(snapshot, op, action)
        opcode = 1136 if action == 'spirit_train' else 1139
        level, exp = snapshot.get('level'), snapshot.get('experience')
        if (type(op.get('receipt_opcode')) is not int or op.get('receipt_opcode') != opcode or
                type(op.get('receipt_result')) is not int or op.get('receipt_result') != 0 or
                op.get('server') != 'PASS' or not _int(level, 1) or not _int(exp) or
                op.get('receipt_level') != level or op.get('receipt_experience') != exp):
            return 'spirit_receipt_unverified'
        if level < self._before[0] or level == self._before[0] and exp <= self._before[1]:
            return 'no_progress'
        if action == 'spirit_rankup' and level <= self._before[0]:
            return 'rankup_no_level_progress'
        if action == 'spirit_train':
            food, gold = op.get('receipt_food'), op.get('receipt_gold')
            if not _int(food) or type(gold) is not int or gold != 0:
                return 'premium_or_unknown_consumption'
            if (food > self._reserved_step_food or food > self._before[2] or snapshot.get('food') != self._before[2] - food or
                    self._food - self._reserved_step_food + food > self.target.max_food):
                return 'food_consumption_mismatch'
            self._food = self._food - self._reserved_step_food + food
        elif (type(op.get('receipt_food')) is not int or op.get('receipt_food') != 0 or
              type(op.get('receipt_gold')) is not int or op.get('receipt_gold') != 0 or
              snapshot.get('food') != self._before[2]):
            return 'rankup_resource_postcondition_unknown'
        self._evidence = 'SERVER_CONFIRMED'
        return ''

    def plan(self, snapshot: Mapping[str, Any], *, revision: int, context_key: str) -> DevelopmentOutcome:
        early = self._admit(snapshot, revision, context_key)
        if early:
            return early
        if snapshot.get('desired_level') != self.target.desired_level:
            return self._block('target_binding_mismatch')
        level, exp = snapshot.get('level'), snapshot.get('experience')
        if not _int(level, 1) or not _int(exp):
            return self._block('spirit_data_unknown')
        action = snapshot.get('available_action')
        if level >= self.target.desired_level:
            if self._evidence == 'UNVERIFIED':
                self._evidence = 'LOCAL_OBSERVED'
            if action not in ('spirit_rankup_confirm', 'spirit_close'):
                self._done = True
                return self._out('DONE', 'target_level_met')
        allowed = ('spirit_open', 'spirit_train', 'spirit_rankup', 'spirit_rankup_confirm', 'spirit_close')
        if not action:
            return self._block(str(snapshot.get('reason') or 'native_action_unavailable'))
        if action not in allowed or action == 'spirit_close' and level < self.target.desired_level:
            return self._block('native_action_outside_capability')
        reserve = 0
        if action in ('spirit_train', 'spirit_rankup'):
            next_exp = snapshot.get('next_exp')
            if level >= 100:
                return self._block('level_limit')
            if not _int(next_exp, 1) or snapshot.get('refreshing') is not False:
                return self._block('spirit_progress_gate_unknown')
            rank = level % 10 == 0 and exp >= next_exp
            if (action == 'spirit_rankup') != rank:
                return self._block('spirit_stage_action_mismatch')
            food = snapshot.get('food')
            if not _int(food):
                return self._block('resources_unknown')
            if action == 'spirit_train':
                if snapshot.get('food_type') != 0 or food <= 0:
                    return self._block('premium_fallback_or_no_food')
                cost = _mapping(snapshot.get('cost'))
                if type(cost.get('gold')) is not int or cost.get('gold') != 0:
                    return self._block('premium_or_unknown_quote')
                quote = cost.get('food')
                if cost.get('cost_status') == 'OBSERVED' and _int(quote, 1):
                    reserve = quote
                    if reserve > food:
                        return self._block('resource_insufficient')
                else:
                    # Without a per-request quote, only the entire currently
                    # available fruit pool is a defensible prior upper bound.
                    reserve = food
                    if self._food + reserve > self.target.max_food:
                        return self._block('quote_unavailable')
                if self._food + reserve > self.target.max_food:
                    return self._block('food_budget_exhausted')
            self._before = (level, exp, food)
            self._reserved_step_food = reserve
        return self._issue(action, revision, target_id=0,
            desired_level=self.target.desired_level, max_requests=self.target.max_requests,
            max_food=self.target.max_food, request=action in ('spirit_train', 'spirit_rankup'), food=reserve)


class SpiritTribute(_Policy):
    """One peach per tribute, bounded by target, requests and initial stock."""
    _terminal_reasons = frozenset((
        'tribute_peaches_exhausted', 'tribute_request_budget_reached',
        'tribute_level_cap_reached', 'tribute_rankup_required', 'tribute_target_reached'))

    def __init__(self, operation_id: str, context_key: str, target: TributeTarget):
        super().__init__(operation_id, context_key)
        self._target = target
        self._max_food: int | None = None
        self._request_limit: int | None = None
        self._reserved_step_food = 0
        self._closing_ui: tuple[str, int] | None = None
        self._confirm_dialog_id: int | None = None

    @property
    def target(self) -> TributeTarget:
        return self._target

    @property
    def effective_max_requests(self) -> int:
        if self._request_limit is not None:
            return self._request_limit
        return 2147483647 if self.target.use_all else self.target.max_requests

    def _accept(self, snapshot: Mapping[str, Any], op: Mapping[str, Any], action: str) -> str:
        if action == 'tribute_rankup_confirm':
            dialog = _mapping(_mapping(snapshot.get('ui')).get('rankup'))
            if (self._confirm_dialog_id is None or dialog.get('visible') is not False or
                    dialog.get('instance_id') not in (0, self._confirm_dialog_id)):
                return 'tribute_rankup_confirm_unverified'
            self._confirm_dialog_id = None
            return ''
        if action in ('tribute_close', 'tribute_parent_close'):
            if self._closing_ui is None:
                return 'tribute_close_identity_unknown'
            name, instance_id = self._closing_ui
            panel = _mapping(_mapping(snapshot.get('ui')).get(name))
            if panel.get('visible') is not False or panel.get('instance_id') not in (0, instance_id):
                return 'tribute_close_postcondition_missing'
            self._closing_ui = None
            return ''
        if action not in ('tribute_train', 'tribute_rankup'):
            return super()._accept(snapshot, op, action)
        level, exp = snapshot.get('level'), snapshot.get('experience')
        opcode = 1139 if action == 'tribute_rankup' else 1136
        if (type(op.get('receipt_opcode')) is not int or op.get('receipt_opcode') != opcode or
                type(op.get('receipt_result')) is not int or op.get('receipt_result') != 0 or
                op.get('server') != 'PASS' or not _int(level, 1) or not _int(exp) or
                op.get('receipt_level') != level or op.get('receipt_experience') != exp):
            return 'tribute_receipt_unverified'
        if level < self._before[0] or level == self._before[0] and exp <= self._before[1]:
            return 'tribute_no_progress'
        food, gold = op.get('receipt_food'), op.get('receipt_gold')
        if action == 'tribute_rankup':
            if (level <= self._before[0] or type(food) is not int or food != 0 or
                    type(gold) is not int or gold != 0 or
                    snapshot.get('food') != self._before[2] or snapshot.get('gold') != self._before[3]):
                return 'tribute_rankup_postcondition_unverified'
            self._evidence = 'SERVER_CONFIRMED'
            return ''
        if type(gold) is not int or gold != 0 or snapshot.get('gold') != self._before[3]:
            return 'tribute_premium_or_unknown_consumption'
        if type(food) is not int or food != 1:
            return 'tribute_food_consumption_mismatch'
        spent = self._food - self._reserved_step_food + food
        if (food > self._reserved_step_food or food > self._before[2] or
                snapshot.get('food') != self._before[2] - food or
                self._max_food is None or spent > self._max_food):
            return 'tribute_food_consumption_mismatch'
        self._food = spent
        self._evidence = 'SERVER_CONFIRMED'
        return ''

    def plan(self, snapshot: Mapping[str, Any], *, revision: int, context_key: str) -> DevelopmentOutcome:
        early = self._admit(snapshot, revision, context_key)
        if early:
            return early
        if snapshot.get('desired_level') != self.target.desired_level:
            return self._block('tribute_target_binding_mismatch')
        level, exp, food = snapshot.get('level'), snapshot.get('experience'), snapshot.get('food')
        if (not _int(level, 1) or not _int(exp) or not _int(food) or food > 2147483647 or
                not _int(snapshot.get('gold'))):
            return self._block('tribute_data_unknown')
        if self._max_food is None:
            # The user-confirmed rule is one peach per normal tribute. Pin
            # current stock for the native resource guard without turning a
            # smaller request cap into a smaller native stock allowance.
            self._max_food = food
            # Initial stock is a finite cap for the use-all option.
            self._request_limit = max(1, self._max_food) if self.target.use_all else self.target.max_requests
        action = snapshot.get('available_action')
        terminal = snapshot.get('done') is True
        reason = snapshot.get('reason')
        if terminal:
            if reason not in self._terminal_reasons:
                return self._block('tribute_terminal_reason_unknown')
            if reason == 'tribute_target_reached' and (not self.target.desired_level or level < self.target.desired_level):
                return self._block('tribute_target_binding_mismatch')
            if not action:
                ui = _mapping(snapshot.get('ui'))
                if (_mapping(ui.get('spirit')).get('visible') is not False or
                        _mapping(ui.get('parent')).get('visible') is not False or
                        _mapping(ui.get('rankup')).get('visible') is not False or
                        ui.get('nested_owned') is not False):
                    return self._block('tribute_cleanup_unverified')
                if self._evidence == 'UNVERIFIED':
                    self._evidence = 'LOCAL_OBSERVED'
                self._done = True
                return self._out('DONE', reason)
            if action not in ('tribute_rankup_confirm', 'tribute_close', 'tribute_parent_close'):
                return self._block('tribute_terminal_action_unavailable')
        elif action in ('tribute_close', 'tribute_parent_close'):
            return self._block('tribute_unexpected_close')
        elif food == 0:
            # Never open a panel just to discover the premium fallback.
            return self._block('tribute_peaches_exhausted')
        elif action != 'tribute_rankup_confirm' and self._food + food > self._max_food:
            # Match the native stock-pinning guard if inventory increases.
            return self._block('tribute_food_budget_unavailable')
        allowed = ('tribute_open', 'tribute_panel_open', 'tribute_train', 'tribute_rankup', 'tribute_rankup_confirm',
                   'tribute_close', 'tribute_parent_close')
        if action not in allowed:
            return self._block(str(reason or 'tribute_action_unavailable'))
        reserve = 0
        if action in ('tribute_train', 'tribute_rankup'):
            if food <= 0:
                return self._block('tribute_peaches_exhausted')
            if action == 'tribute_train' and snapshot.get('food_type') != 0:
                return self._block('tribute_premium_mode_forbidden')
            if self._requests >= self.effective_max_requests:
                return self._block('tribute_request_budget_reached')
            if level >= 100:
                return self._block('tribute_level_cap_reached')
            if self.target.desired_level and level >= self.target.desired_level:
                return self._block('tribute_target_reached')
            next_exp = snapshot.get('next_exp')
            if not _int(next_exp, 1) or snapshot.get('refreshing') is not False:
                return self._block('tribute_progress_gate_unknown')
            rankup_required = level % 10 == 0 and exp >= next_exp
            if action == 'tribute_rankup' and not rankup_required:
                return self._block('tribute_rankup_gate_unknown')
            if action == 'tribute_train' and rankup_required:
                return self._block('tribute_rankup_required')
            quote = _mapping(snapshot.get('cost'))
            if type(quote.get('gold')) is not int or quote.get('gold') != 0:
                return self._block('tribute_premium_or_unknown_quote')
            reserve = 1 if action == 'tribute_train' else 0
            if self._food + reserve > self._max_food:
                return self._block('tribute_food_budget_exhausted')
            self._before = (level, exp, food, snapshot['gold'])
            self._reserved_step_food = reserve
        if action == 'tribute_rankup_confirm':
            dialog = _mapping(_mapping(snapshot.get('ui')).get('rankup'))
            if (dialog.get('visible') is not True or
                    dialog.get('enabled') is not True or type(dialog.get('instance_id')) is not int or
                    dialog['instance_id'] == 0):
                return self._block('tribute_rankup_confirm_unverified')
            self._confirm_dialog_id = dialog['instance_id']
        if action in ('tribute_close', 'tribute_parent_close'):
            name = 'spirit' if action == 'tribute_close' else 'parent'
            panel = _mapping(_mapping(snapshot.get('ui')).get(name))
            if (panel.get('visible') is not True or panel.get('enabled') is not True or
                    type(panel.get('instance_id')) is not int or panel['instance_id'] == 0):
                return self._block('tribute_close_identity_unknown')
            self._closing_ui = name, panel['instance_id']
        return self._issue(action, revision, target_id=0, desired_level=self.target.desired_level,
            max_requests=self.effective_max_requests, max_food=self._max_food, mode='fruit',
            request=action == 'tribute_train', food=reserve)


class DailySpiritTribute(SpiritTribute):
    """One owned daily session, with a progress checkpoint after each receipt.

    The installed game sends only the fruit selector. Its response determines
    the actual fruit charge; the bool/count arguments do not quote one peach.
    Standalone cultivation keeps SpiritTribute's existing one-peach contract.
    """

    def __init__(self, operation_id: str, context_key: str, target: TributeTarget):
        super().__init__(operation_id, context_key, target)
        self._checkpoint_due = True
        self._checkpoint_action = None
        self._confirmed_tributes = self._spent_food = 0
        self._last_receipt: dict[str, Any] | None = None

    @property
    def confirmed_tributes(self) -> int:
        return self._confirmed_tributes

    @property
    def spent_food(self) -> int:
        return self._spent_food

    @property
    def last_receipt(self) -> Mapping[str, Any] | None:
        return dict(self._last_receipt) if self._last_receipt is not None else None

    def confirm_checkpoint(self) -> None:
        if self._pending is not None or not self._checkpoint_due:
            raise RuntimeError('tribute_checkpoint_not_ready')
        self._checkpoint_due = False

    def request_checkpoint(self) -> None:
        if self._pending is not None:
            raise RuntimeError('tribute_checkpoint_command_pending')
        if not self._checkpoint_due:
            self._last_revision = max(0, self._last_revision - 1)
            self._checkpoint_due = True

    def defer_prepared(self, command: DevelopmentCommand) -> None:
        """Discard a proven unsubmitted command when pause closes admission."""
        if self._pending is not command:
            raise RuntimeError('tribute_prepared_command_changed')
        if command.action == 'tribute_train':
            self._requests -= 1
            self._food -= self._reserved_step_food
        self._pending = None
        self.request_checkpoint()

    def _admit(self, snapshot, revision, context_key):
        early = super()._admit(snapshot, revision, context_key)
        if early:
            return early
        action = snapshot.get('available_action')
        if action == 'tribute_train' and self._checkpoint_action != action:
            self._checkpoint_due = True
        if self._checkpoint_due:
            # No command is prepared at a checkpoint. After the caller checks
            # daily progress, a fresh read of this unchanged native revision
            # can authorize the next step without waiting for another event.
            self._last_revision = revision - 1
            self._checkpoint_action = action
            return self._out('CHECKPOINT', 'daily_tribute_progress_required')
        level, exp, next_exp = (snapshot.get(key) for key in ('level', 'experience', 'next_exp'))
        rankup_before_open = (action in ('tribute_open', 'tribute_panel_open') and
            all(type(value) is int for value in (level, exp, next_exp)) and
            level % 10 == 0 and next_exp > 0 and exp >= next_exp)
        if action in ('tribute_rankup', 'tribute_rankup_confirm') or rankup_before_open:
            return self._block('tribute_rankup_required')
        return None

    def _accept(self, snapshot, op, action):
        if action != 'tribute_train':
            return super()._accept(snapshot, op, action)
        level, exp = snapshot.get('level'), snapshot.get('experience')
        if (op.get('opcode') != 1136 or op.get('receipt_opcode') != 1136 or
                type(op.get('receipt_result')) is not int or op.get('receipt_result') != 0 or
                op.get('server') != 'PASS' or op.get('server_progress_observed') is not True or
                not _int(level, 1) or level > 100 or not _int(exp) or
                not _int(snapshot.get('food')) or not _int(snapshot.get('gold')) or
                not _int(snapshot.get('spent_food')) or not _int(snapshot.get('tribute_requests'), 1) or
                op.get('receipt_level') != level or op.get('receipt_experience') != exp):
            return 'tribute_receipt_unverified'
        if level < self._before[0] or level == self._before[0] and exp <= self._before[1]:
            return 'tribute_no_progress'
        food, gold = op.get('receipt_food'), op.get('receipt_gold')
        if type(gold) is not int or gold != 0 or snapshot.get('gold') != self._before[3]:
            return 'tribute_premium_or_unknown_consumption'
        spent = self._spent_food + food if _int(food, 1) else None
        if (spent is None or food > self._before[2] or
                snapshot.get('food') != self._before[2] - food or
                self._max_food is None or spent > self._max_food or
                snapshot.get('spent_food') != spent or
                snapshot.get('tribute_requests') != self._confirmed_tributes + 1):
            return 'tribute_food_consumption_mismatch'
        self._food = self._spent_food = spent
        self._confirmed_tributes += 1
        self._last_receipt = {key: op.get(key) for key in (
            'operation_id', 'sequence', 'expected_revision', 'action', 'opcode',
            'receipt_opcode', 'receipt_result', 'receipt_food', 'receipt_gold',
            'receipt_level', 'receipt_experience', 'server', 'server_progress_observed')}
        self._checkpoint_due = True
        self._evidence = 'SERVER_CONFIRMED'
        return ''
