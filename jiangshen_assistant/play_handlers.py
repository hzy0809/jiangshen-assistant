"""Gameplay handlers compute one next-step decision; flows execute asynchronously."""
from __future__ import annotations

from copy import deepcopy
from dataclasses import asdict, replace

from .development import EquipmentTarget, TributeTarget, StarTarget
from .play_workflows import DEFINITIONS, FlowDecision, DecisionKind


class CoordinatorHandler:
    """A finite owner-thread step starts/observes an existing game flow worker."""
    def __init__(self, runtime, backend_name: str):
        self.runtime, self.name = runtime, backend_name

    @property
    def backend(self):
        return self.runtime.backends[self.name]

    def advance(self, instance, world, notification=None):
        if not instance.started:
            return FlowDecision(DecisionKind.START_FLOW, scene_goal=DEFINITIONS[instance.kind].scene_goal)
        if getattr(self.backend, 'is_yielded', False):
            return FlowDecision(DecisionKind.AWAIT, reason='等待执行权', dependency='session_control')
        if not self.backend.is_active and not self.backend.runner.is_active:
            return FlowDecision(DecisionKind.FINISH, result=self.snapshot())
        snapshot = self.snapshot(compact=True)
        if snapshot.get('navigation_status') in ('WAITING', 'PREPARING'):
            return FlowDecision(DecisionKind.AWAIT, reason='准备玩法场景', dependency='scene')
        return FlowDecision(DecisionKind.OBSERVE, result=snapshot)

    def start(self, instance) -> bool:
        args = deepcopy(instance.options.get('args', ()))
        kwargs = deepcopy(instance.options.get('kwargs', {}))
        policy = instance.resource_policy
        if instance.resource_policy_explicit:
            if instance.kind == 'soul':
                kwargs['coin_budget'] = policy.limit('money')
            elif instance.kind in ('equipment', 'tribute', 'star'):
                target = args[1] if len(args) > 1 else kwargs.get('target')
                if isinstance(target, EquipmentTarget):
                    money, stone = policy.limit('money'), policy.limit('stone')
                    target = replace(target, max_money=0x7fffffff if money is None else min(money, 0x7fffffff),
                                     max_stone=0x7fffffff if stone is None else stone)
                elif isinstance(target, TributeTarget):
                    limit = policy.limit('peaches')
                    if limit == 0:
                        self.runtime.finish(instance, 'BLOCKED', '本次资源策略未允许使用桃子。')
                        return False
                    if limit is not None:
                        target = replace(target, max_requests=min(target.max_requests, limit), use_all=False)
                elif isinstance(target, StarTarget):
                    target = replace(target, max_practice=policy.limit('practice'))
                if len(args) > 1:
                    args = list(args)
                    args[1] = target
                else:
                    kwargs['target'] = target
            elif self.name == 'daily':
                donation = kwargs.get('donation_type', 2)
                resource = 'gold' if donation == 3 else 'money'
                donation_cost = {1: 1000, 2: 100000, 3: 80}[donation]
                selected = kwargs.get('selected_items', ())
                if 'guild_donation' in selected and (resource not in policy.allowed_resources
                        or policy.limit(resource) is not None and policy.limit(resource) < donation_cost):
                    self.runtime.finish(instance, 'BLOCKED', '同盟捐献超出本次选择的资源范围。')
                    return False
                if kwargs.get('tribute_options') and 'tribute' in selected:
                    if policy.limit('peaches') == 0:
                        self.runtime.finish(instance, 'BLOCKED', '本次资源策略未允许使用桃子。')
                        return False
                    limit = policy.limit('peaches')
                    if limit is not None:
                        tribute = (asdict(kwargs['tribute_options']) if isinstance(kwargs['tribute_options'], TributeTarget)
                                   else dict(kwargs['tribute_options']))
                        tribute.update(max_requests=min(tribute.get('max_requests', limit), limit), use_all=False)
                        kwargs['tribute_options'] = tribute
        # The executor sees the current policy at each later mutation boundary.
        # Adapters that cannot implement purchase/acceleration never invent one.
        self.backend.resource_policy_provider = lambda: self.runtime.current_policy(instance.id)
        if instance.kind == 'soul_quote':
            return bool(self.backend.refresh_quote())
        if instance.kind == 'soul_harvest':
            return bool(self.backend.harvest_existing(**kwargs))
        return bool(self.backend.start(*args, **kwargs))

    def snapshot(self, *, compact=False) -> dict:
        summary = getattr(self.backend, 'summary_snapshot', None) if compact else None
        return deepcopy(summary if summary is not None else self.backend.snapshot)

