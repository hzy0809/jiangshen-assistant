"""Gameplay definitions, execution instances and user-owned resource policy.

Definitions describe goals; each explicit request creates a new instance.
Facts and receipts are never substituted for current game observations.
"""
from __future__ import annotations

from copy import deepcopy
from dataclasses import asdict, dataclass, field
from datetime import datetime, timedelta, timezone
from enum import Enum
import json
import os
from pathlib import Path
import tempfile
import time
import uuid

from .account_profiles import DAILY_CATALOG


RESOURCE_NAMES = {'money': '钱币', 'gold': '元宝', 'peaches': '桃子',
                  'practice': '战魂', 'stamina': '体力', 'stone': '强化石', 'mana': '灵气',
                  'experience': '经验', 'contribution': '同盟贡献',
                  'silver_pick': '银镐', 'gold_pick': '金镐'}


class WorkflowState(str, Enum):
    QUEUED = 'QUEUED'
    READY = 'READY'
    ACTIVE = 'ACTIVE'
    WAITING = 'WAITING'
    PAUSED = 'PAUSED'
    STOPPING = 'STOPPING'
    CLOSED = 'CLOSED'


class DecisionKind(str, Enum):
    START_FLOW = 'start_flow'
    OBSERVE = 'observe'
    AWAIT = 'await'
    FINISH = 'finish'


@dataclass(frozen=True)
class FlowDecision:
    kind: DecisionKind
    reason: str = ''
    dependency: str = ''
    scene_goal: str = ''
    result: dict = field(default_factory=dict)


@dataclass(frozen=True)
class ResourcePolicy:
    allowed_resources: tuple[str, ...] = ()
    limits: dict[str, int | None] = field(default_factory=dict)
    allow_purchase: bool = False
    allow_accelerate: bool = False
    allow_replenish: bool = False

    @classmethod
    def from_dict(cls, value: dict | None) -> ResourcePolicy:
        value = {} if value is None else value
        if not isinstance(value, dict) or set(value) - {
                'allowed_resources', 'limits', 'allow_purchase', 'allow_accelerate', 'allow_replenish'}:
            raise ValueError('资源策略字段无效。')
        allowed = value.get('allowed_resources', [])
        if not isinstance(allowed, (list, tuple)) or any(key not in RESOURCE_NAMES for key in allowed):
            raise ValueError('请选择有效的资源类型。')
        limits = value.get('limits', {})
        if not isinstance(limits, dict) or set(limits) - set(RESOURCE_NAMES):
            raise ValueError('资源上限字段无效。')
        if any(amount is not None and (type(amount) is not int or amount < 0)
               for amount in limits.values()):
            raise ValueError('资源上限必须是非负整数，留空表示不限。')
        switches = [value.get(key, False) for key in
                    ('allow_purchase', 'allow_accelerate', 'allow_replenish')]
        if any(type(switch) is not bool for switch in switches):
            raise ValueError('购买、加速和补足选项必须是开关。')
        return cls(tuple(dict.fromkeys(allowed)), deepcopy(limits), *switches)

    def to_dict(self) -> dict:
        result = asdict(self)
        result['allowed_resources'] = list(self.allowed_resources)
        return result

    def limit(self, resource: str) -> int | None:
        return self.limits.get(resource) if resource in self.allowed_resources else 0


@dataclass(frozen=True)
class GameplayDefinition:
    kind: str
    label: str
    backend: str
    page: str
    scene_goal: str
    resources: tuple[str, ...] = ()
    battle: bool = False


DEFINITIONS = {
    'campaign': GameplayDefinition('campaign', '征战', 'campaign', 'battle', 'campaign', ('stamina',), True),
    'challenge': GameplayDefinition('challenge', '普通挑战', 'challenge', 'challenge', 'challenge', ('stamina',), True),
    'elite': GameplayDefinition('elite', '精英挑战', 'elite', 'accounts', 'elite', ('stamina', 'gold'), True),
    'soul': GameplayDefinition('soul', '聚魂', 'soul', 'soul', 'home', ('money', 'gold')),
    'soul_quote': GameplayDefinition('soul_quote', '聚魂报价', 'soul', 'soul', 'home'),
    'soul_harvest': GameplayDefinition('soul_harvest', '收获当前牌面', 'soul', 'soul', 'current'),
    'equipment': GameplayDefinition('equipment', '装备培养', 'development', 'development', 'home', ('money', 'stone')),
    'tribute': GameplayDefinition('tribute', '灵兽上贡', 'development', 'development', 'home', ('peaches', 'gold')),
    'star': GameplayDefinition('star', '星灵', 'development', 'development', 'home', ('practice',)),
    'growth': GameplayDefinition('growth', '成长任务', 'development', 'development', 'campaign', ('money', 'stone', 'peaches', 'practice', 'stamina')),
    'claim_growth': GameplayDefinition('claim_growth', '领取成长奖励', 'development', 'development', 'campaign'),
    'daily': GameplayDefinition('daily', '日常计划', 'daily', 'daily', 'home', tuple(RESOURCE_NAMES)),
    'point_reward': GameplayDefinition('point_reward', '积分宝箱', 'daily', 'daily', 'home'),
}
_BATTLE_DAILY = frozenset(('divinity', 'arena', 'point_race', 'grave', 'trial', 'training',
                           'friend_monster', 'home_monster', 'revenge'))
for _key, _label in DAILY_CATALOG.items():
    _resources = {'soul': ('money', 'gold'), 'tribute': ('peaches', 'gold'),
                  'guild_donation': ('money', 'gold'), 'city_shop': ('mana',),
                  'refine': ('experience', 'money', 'gold'), 'stone': ('silver_pick', 'gold_pick', 'gold'),
                  'guild_lottery': ('contribution', 'gold'),
                  'copy': ('gold',), 'online_reward': ('gold',)}.get(
                      _key, ('stamina', 'gold') if _key in _BATTLE_DAILY else ())
    DEFINITIONS['daily.' + _key] = GameplayDefinition(
        'daily.' + _key, _label, 'daily', 'daily', 'home', _resources, _key in _BATTLE_DAILY)


@dataclass
class WorkflowInstance:
    id: str
    run_id: str
    kind: str
    source: str
    account_id: str
    account_label: str
    options: dict
    resource_policy: ResourcePolicy
    resource_policy_explicit: bool
    placement: str
    state: WorkflowState = WorkflowState.QUEUED
    desired_control: str = 'run'
    stage: str = 'queued'
    message: str = '等待执行'
    progress: str = ''
    parent_id: str = ''
    child_ids: list[str] = field(default_factory=list)
    latest: dict = field(default_factory=dict)
    result: dict = field(default_factory=dict)
    created_at: str = field(default_factory=lambda: datetime.now(timezone.utc).isoformat())
    created_monotonic: float = field(default_factory=time.monotonic)
    next_check_at: float | None = None
    started: bool = False
    suspended: bool = False
    stop_requested: bool = False
    policy_version: int = 1

    @classmethod
    def create(cls, kind: str, options: dict, policy: ResourcePolicy, *, source='manual',
               account_id='', account_label='', placement='switch', run_id='', explicit=False,
               parent_id='') -> WorkflowInstance:
        if kind not in DEFINITIONS:
            raise ValueError('不支持的玩法。')
        if placement not in ('switch', 'queue'):
            raise ValueError('启动方式必须是 switch 或 queue。')
        return cls(uuid.uuid4().hex, run_id or uuid.uuid4().hex, kind, source, account_id,
                   account_label, deepcopy(options), policy, explicit, placement, parent_id=parent_id)

    def public_view(self, *, compact=False) -> dict:
        from .snapshot_projection import compact_projection
        definition = DEFINITIONS[self.kind]
        args, kwargs = self.options.get('args', ()), self.options.get('kwargs', {})
        target = definition.label
        if self.kind == 'soul':
            blue = args[0] if args else kwargs.get('blue_count', 0)
            purple = args[1] if len(args) > 1 else kwargs.get('purple_count', 0)
            target = f'蓝魂 {blue} 次，紫魂 {purple} 次'
        elif self.kind == 'elite':
            target = f"完成 {kwargs.get('target', 10)} 次精英挑战"
        elif self.kind in ('equipment', 'tribute', 'star'):
            goal = args[1] if len(args) > 1 else kwargs.get('target')
            if self.kind == 'equipment' and goal is not None:
                target = f"装备 {goal.equipment_id} 达到 {goal.desired_level} 级"
            elif self.kind == 'tribute' and goal is not None:
                target = '使用本轮现有桃子' if goal.use_all else f'上贡最多 {goal.max_requests} 次'
            elif self.kind == 'star':
                target = '按本轮战魂策略激活星灵'
        elif self.kind == 'daily':
            target = f'完成所选 {len(self.child_ids)} 项玩法'
        remaining = None if self.next_check_at is None else max(0.0, self.next_check_at - time.monotonic())
        next_check = None if remaining is None else (
            datetime.now(timezone.utc) + timedelta(seconds=remaining)).isoformat()
        return {'id': self.id, 'run_id': self.run_id, 'kind': self.kind, 'label': definition.label,
                'account_id': self.account_id, 'account_label': self.account_label,
                'source': self.source, 'state': self.state.value, 'stage': self.stage,
                'message': self.message, 'progress': self.progress, 'target': target, 'parent_id': self.parent_id,
                'child_ids': list(self.child_ids), 'desired_control': self.desired_control,
                'created_at': self.created_at, 'next_check_at': next_check,
                'seconds_until_check': remaining,
                'resources': self.resource_policy.to_dict(), 'policy_version': self.policy_version,
                'flows': [deepcopy(value) for value in
                          (self.latest.get('flow'), self.latest.get('navigation_flow'))
                          if isinstance(value, dict) and value],
                'result': compact_projection(self.result) if compact else deepcopy(self.result), 'can_pause': self.state not in
                    (WorkflowState.PAUSED, WorkflowState.STOPPING, WorkflowState.CLOSED),
                'can_resume': self.state == WorkflowState.PAUSED,
                'can_stop': self.state not in (WorkflowState.STOPPING, WorkflowState.CLOSED)}


class WorkflowPolicyStore:
    """Independent policy storage; account credentials never enter this file."""
    def __init__(self, data_dir: Path):
        self.path = Path(data_dir) / 'workflow-policies.json'
        self.values: dict[str, dict] = {}
        self.error = ''
        try:
            document = json.loads(self.path.read_text(encoding='utf-8-sig'))
            if document.get('schema_version') != 1 or not isinstance(document.get('policies'), dict):
                raise ValueError('资源策略文件格式无效。')
            for key, policy in document['policies'].items():
                if not isinstance(key, str):
                    raise ValueError('资源策略标识无效。')
                self.values[key] = ResourcePolicy.from_dict(policy).to_dict()
        except FileNotFoundError:
            pass
        except (OSError, ValueError, TypeError) as error:
            self.error = str(error)

    @staticmethod
    def key(kind: str, account_id: str) -> str:
        return json.dumps([account_id, kind], ensure_ascii=False, separators=(',', ':'))

    def has(self, kind: str, account_id='') -> bool:
        return self.key(kind, account_id) in self.values

    def get(self, kind: str, account_id='') -> ResourcePolicy:
        value = self.values.get(self.key(kind, account_id))
        if value is not None:
            return ResourcePolicy.from_dict(value)
        # These are inherited gameplay capabilities, not new permission to buy.
        resources = tuple(key for key in DEFINITIONS[kind].resources if key != 'gold')
        return ResourcePolicy(resources)

    def save(self, kind: str, policy: dict, account_id='') -> ResourcePolicy:
        if self.error:
            raise ValueError('资源策略文件未成功读取，未覆盖原文件：' + self.error)
        parsed = ResourcePolicy.from_dict(policy)
        values = {**self.values, self.key(kind, account_id): parsed.to_dict()}
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=self.path.parent,
                                             prefix='.workflow-policies-', suffix='.tmp', delete=False) as stream:
                temporary = stream.name
                json.dump({'schema_version': 1, 'policies': values}, stream, ensure_ascii=False, indent=2)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.path)
        finally:
            if temporary is not None:
                Path(temporary).unlink(missing_ok=True)
        self.values = values
        return parsed
