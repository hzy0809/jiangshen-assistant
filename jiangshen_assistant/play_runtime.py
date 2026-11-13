"""Owner-thread gameplay scheduling over existing, scoped game capabilities.

The scheduler accepts goals while another activity is running. Executors have
separate cooperative runners, but a single game-session owner admits mutations.
Battle workers can park at a confirmed handoff point without losing their run.
"""
from __future__ import annotations

from copy import deepcopy
from dataclasses import replace
import json
import os
from pathlib import Path
import tempfile
import threading
import time

from .account_profiles import DAILY_CATALOG
from .event_stream import EventMailbox
from .play_handlers import CoordinatorHandler
from .play_workflows import (DEFINITIONS, DecisionKind, ResourcePolicy, WorkflowInstance,
                             WorkflowPolicyStore, WorkflowState)
from .workflow_events import readable_reason
from .snapshot_projection import compact_projection


_DAILY_ARGUMENTS = ('donation_type', 'selected_items', 'expected_character_id',
                    'expected_user_id', 'expected_zone_id', 'defer_cooldowns',
                    'tribute_options', 'shop_duration', 'enabled_items', 'retry_source',
                    'popup_scope', 'wait_for_gift', 'gift_wait_until')
_TERMINAL_BAD = frozenset(('UNKNOWN', 'BLOCKED', 'UNAVAILABLE', 'STOPPED', 'PARTIAL', 'FAILED', 'UNSUPPORTED'))




class ManagedCoordinator:
    """Keep page/account entry APIs while routing every start to one scheduler."""
    def __init__(self, runtime, name):
        self.runtime, self.name = runtime, name
        self._last_id = ''
        self._account_id = ''
        self._fixed_id = ''

    @property
    def backend(self):
        return self.runtime.backends[self.name]

    def __getattr__(self, name):
        return getattr(self.backend, name)

    def _instance(self):
        identifier = self._fixed_id or self._last_id
        return self.runtime.instances.get(identifier)

    def for_account(self):
        handle = ManagedCoordinator(self.runtime, self.name)
        handle._fixed_id = self._account_id
        return handle

    def start(self, *args, **kwargs):
        kind = args[0] if self.name == 'development' and args else self.name
        if self.name == 'daily' and kwargs.get('point_reward_only') is True:
            kind = 'point_reward'
        source = 'account_plan' if any(kwargs.get('expected_' + key + '_id')
                                      for key in ('character', 'user', 'zone')) else 'manual'
        selected = getattr(self.runtime.app, 'workflow_placement', None)
        placement = ('queue' if source == 'account_plan' else
                     getattr(self.runtime, 'submission_placement', None) or
                     ('queue' if selected is not None and selected.get() == '加入队列' else 'switch'))
        instance = self.runtime.submit(kind, {'args': args, 'kwargs': kwargs}, source=source,
                                       placement=placement,
                                       resource_policy=getattr(self.runtime, 'submission_policy', None))
        self._last_id = instance.id
        if source == 'account_plan':
            self._account_id = instance.id
        return True

    def refresh_quote(self):
        instance = self.runtime.submit('soul_quote', {'args': (), 'kwargs': {}}, placement='queue')
        self._last_id = instance.id
        return True

    def harvest_existing(self, **kwargs):
        instance = self.runtime.submit('soul_harvest', {'args': (), 'kwargs': kwargs}, placement='switch')
        self._last_id = instance.id
        return True

    @property
    def is_active(self):
        instance = self._instance()
        return instance is not None and instance.state != WorkflowState.CLOSED

    @property
    def is_paused(self):
        instance = self._instance()
        return instance is not None and instance.desired_control == 'pause'

    @property
    def is_shutdown(self):
        return self.backend.is_shutdown and not any(
            DEFINITIONS[item.kind].backend == self.name and item.state != WorkflowState.CLOSED
            for item in self.runtime.instances.values())

    @property
    def snapshot(self):
        instance = self._instance()
        if instance is None:
            return self.backend.snapshot
        value = deepcopy(instance.result or instance.latest)
        if not value:
            value = deepcopy(self.backend.snapshot) if instance.started else {}
        if not value:
            value = {'mode': 'idle', 'status': 'IDLE', 'reason': ''}
        value.update(workflow_id=instance.id, workflow_state=instance.state.value,
                     workflow_stage=instance.stage, workflow_message=instance.message)
        return value

    @property
    def summary_snapshot(self):
        instance = self._instance()
        if instance is None:
            summary = getattr(self.backend, 'summary_snapshot', None)
            return summary if summary is not None else compact_projection(self.backend.snapshot)
        stored = instance.result or instance.latest
        if stored:
            value = compact_projection(stored)
        elif instance.started:
            summary = getattr(self.backend, 'summary_snapshot', None)
            value = summary if summary is not None else compact_projection(self.backend.snapshot)
        else:
            value = {}
        if not value:
            value = {'mode': 'idle', 'status': 'IDLE', 'reason': ''}
        value.update(workflow_id=instance.id, workflow_state=instance.state.value,
                     workflow_stage=instance.stage, workflow_message=instance.message)
        return value

    def pause(self, *args, **kwargs):
        instance = self._instance()
        return bool(instance and self.runtime.control(instance.id, 'pause'))

    def resume(self, *args, **kwargs):
        instance = self._instance()
        return bool(instance and self.runtime.control(instance.id, 'resume'))

    def stop(self, *args, **kwargs):
        instance = self._instance()
        return bool(instance and self.runtime.control(instance.id, 'stop'))

    def close(self):
        for instance in list(self.runtime.instances.values()):
            if DEFINITIONS[instance.kind].backend == self.name and instance.state != WorkflowState.CLOSED:
                self.runtime.control(instance.id, 'stop')
        self.backend.close()


class PlayWorkflowRuntime:
    def __init__(self, app, backends: dict):
        self.app, self.backends = app, backends
        self._owner = threading.get_ident()
        self.events = EventMailbox('play_workflows')
        self.policies = WorkflowPolicyStore(app.data_dir)
        self.instances: dict[str, WorkflowInstance] = {}
        self.handlers = {name: CoordinatorHandler(self, name) for name in backends}
        self.proxies = {name: ManagedCoordinator(self, name) for name in backends}
        self.active_id = ''
        self._backend_owners: dict[str, str] = {}
        self._suspended: list[str] = []
        self._headers: dict[str, tuple] = {}
        self._requests: dict[str, str] = {}
        self._closing = False
        self._ticking = False
        self._scene: dict = {}
        self._session_connection = None
        self._session_lease = None
        self.journal_error = ''

    def _require_owner(self):
        if threading.get_ident() != self._owner:
            raise RuntimeError('玩法调度只能在应用主线程推进。')

    def _account(self, source):
        if source == 'account_plan':
            return self.app.account_plan.workflow_account
        identity = self.app.objects.current_identity()
        if identity:
            identity = {**identity, 'role_id': identity.get('character_id')}
            for profile in getattr(getattr(self.app, 'account_page', None), 'document', {}).get('profiles', ()):
                if all(profile.get(key) and str(profile[key]) == str(identity.get(key))
                       for key in ('user_id', 'zone_id', 'role_id')):
                    return {'id': profile['id'], 'label': profile['label']}
        return {'id': '', 'label': '当前角色'}

    def submit(self, kind: str, options: dict | None = None, *, source='manual',
               placement='switch', account_id='', resource_policy=None, request_id=''):
        self._require_owner()
        if self._closing or self.app.closing:
            raise RuntimeError('助手正在关闭，未接受新的玩法。')
        if request_id and request_id in self._requests:
            return self.instances[self._requests[request_id]]
        if kind not in DEFINITIONS:
            raise ValueError('不支持的玩法。')
        account = self._account(source)
        account_id = account_id or account['id']
        options = deepcopy(options or {'args': (), 'kwargs': {}})
        if kind == 'daily':
            kwargs = dict(zip(_DAILY_ARGUMENTS, options.get('args', ())))
            kwargs.update(options.get('kwargs', {}))
            selected = kwargs.get('selected_items')
            if selected is not None and (not selected or set(selected) - set(DAILY_CATALOG)):
                raise ValueError('请选择有效的日常项目。')
            options = {'args': (), 'kwargs': kwargs}
        explicit = resource_policy is not None or self.policies.has(kind, account_id)
        policy = (ResourcePolicy.from_dict(resource_policy) if resource_policy is not None
                  else self.policies.get(kind, account_id))
        if not explicit and kind == 'daily' and options.get('kwargs', {}).get('donation_type') == 3:
            policy = replace(policy, allowed_resources=tuple(dict.fromkeys((*policy.allowed_resources, 'gold'))))
        run_id = getattr(self.app.account_plan, '_run_id', '') if source == 'account_plan' else ''
        instance = WorkflowInstance.create(kind, options, policy, source=source, account_id=account_id,
            account_label=account['label'], placement=placement, run_id=run_id, explicit=explicit)
        self.instances[instance.id] = instance
        if request_id:
            self._requests[request_id] = instance.id
        if kind == 'daily':
            self._expand_daily(instance)
        if source == 'manual':
            self.app.auto_connection.resume()
        self._publish(instance)
        self.wake()
        return instance

    def _expand_daily(self, parent):
        original = parent.options['kwargs']
        selected = original.get('selected_items')
        selected = set(DAILY_CATALOG) if selected is None else set(selected)
        if not selected:
            raise ValueError('请选择本轮日常项目。')
        unknown = selected - set(DAILY_CATALOG)
        if unknown:
            raise ValueError('日常项目无效。')
        enabled = original.get('enabled_items')
        enabled = set(selected) if enabled is None else set(enabled)
        # Dependency order is explicit; one child is dispatched at a time.
        keys = [key for key in DAILY_CATALOG if key in selected]
        if 'divinity' in keys and 'mingshi' in keys:
            keys.remove('divinity')
            keys.insert(keys.index('mingshi'), 'divinity')
        for key in keys:
            kind = 'daily.' + key
            kwargs = deepcopy(original)
            kwargs.update(selected_items=frozenset((key,)), enabled_items=frozenset(enabled),
                          defer_cooldowns=True)
            if original.get('wait_for_gift') and key != 'online_gift':
                kwargs.update(wait_for_gift=False, gift_wait_until=None)
            saved = self.policies.has(kind, parent.account_id)
            policy = self.policies.get(kind, parent.account_id) if saved else parent.resource_policy
            child = WorkflowInstance.create(kind, {'args': (), 'kwargs': kwargs}, policy,
                source=parent.source, account_id=parent.account_id, account_label=parent.account_label,
                placement=parent.placement, run_id=parent.run_id, parent_id=parent.id,
                explicit=saved or parent.resource_policy_explicit)
            self.instances[child.id] = child
            parent.child_ids.append(child.id)
            self._publish(child)
        parent.state, parent.stage, parent.message = WorkflowState.ACTIVE, 'plan', '正在安排日常玩法'

    def wake(self):
        runtime = getattr(self.app, 'event_runtime', None)
        if runtime is not None and not self._closing:
            runtime.scheduler.call_later('play.step', 0, self.tick)

    def on_notification(self, source: str, payload=None):
        self._require_owner()
        # Game observations and executor completion wake the relevant scheduler;
        # receipts still belong to their original coordinator/operation.
        self.wake()

    def current_policy(self, identifier):
        instance = self.instances.get(identifier)
        if instance is None or not instance.resource_policy_explicit:
            return None
        return {**instance.resource_policy.to_dict(), 'budget_owner': instance.id,
                'policy_version': instance.policy_version}

    def policy_for(self, kind, account_id=''):
        return self.policies.get(kind, account_id).to_dict()

    def resources_for(self, kind):
        return list(DEFINITIONS[kind].resources)

    def capabilities_for(self, kind):
        return {'allow_purchase': kind in ('soul', 'daily.soul', 'daily'),
                'allow_accelerate': False, 'allow_replenish': False}

    def save_policy(self, kind, value, account_id=''):
        self._require_owner()
        return self.policies.save(kind, value, account_id).to_dict()

    def update_policy(self, identifier, value):
        self._require_owner()
        instance = self.instances[identifier]
        if instance.state == WorkflowState.CLOSED:
            raise ValueError('本次玩法已结束，请为下一次执行保存资源策略。')
        instance.resource_policy = ResourcePolicy.from_dict(value)
        instance.resource_policy_explicit = True
        instance.policy_version += 1
        for child_id in instance.child_ids:
            child = self.instances[child_id]
            if child.state != WorkflowState.CLOSED:
                child.resource_policy = instance.resource_policy
                child.resource_policy_explicit = True
                child.policy_version += 1
        self._publish(instance)
        return True

    def control(self, identifier: str, action: str):
        self._require_owner()
        instance = self.instances.get(identifier)
        if instance is None or instance.state == WorkflowState.CLOSED:
            return False
        if action not in ('pause', 'resume', 'stop'):
            raise ValueError('不支持的玩法控制。')
        if action == 'pause' and instance.desired_control == 'pause':
            return True
        if action == 'resume' and instance.desired_control == 'run':
            return True
        if instance.child_ids:
            instance.desired_control = {'pause': 'pause', 'resume': 'run', 'stop': 'stop'}[action]
            for child_id in instance.child_ids:
                if self.instances[child_id].state != WorkflowState.CLOSED:
                    self.control(child_id, action)
            if action == 'pause':
                instance.state, instance.message = WorkflowState.PAUSED, '日常计划已暂停'
            elif action == 'stop':
                instance.state, instance.message = WorkflowState.STOPPING, '正在停止日常计划'
            else:
                instance.state, instance.message = WorkflowState.ACTIVE, '继续安排日常玩法'
            self._publish(instance)
            self.wake()
            return True
        backend = self.backends[DEFINITIONS[instance.kind].backend]
        owned = self._backend_owners.get(DEFINITIONS[instance.kind].backend) == instance.id
        if action == 'stop':
            instance.desired_control, instance.stop_requested = 'stop', True
            if instance.started and owned:
                instance.state, instance.message = WorkflowState.STOPPING, '正在核对在途操作并收尾'
                backend.stop()
            else:
                self.finish(instance, 'STOPPED', '用户已停止，未再提交动作。')
        elif action == 'pause':
            instance.desired_control = 'pause'
            if instance.started and owned and not getattr(backend, 'is_yielded', False):
                backend.pause()
            instance.state, instance.message = WorkflowState.PAUSED, '已暂停后续操作'
        else:
            instance.desired_control = 'run'
            if instance.suspended:
                if backend.is_paused:
                    backend.resume()
                instance.state, instance.message = WorkflowState.WAITING, '等待恢复执行权'
            elif instance.started and owned:
                if not backend.resume():
                    return False
                instance.state, instance.message = WorkflowState.ACTIVE, '继续执行'
            else:
                instance.state, instance.message = WorkflowState.QUEUED, '等待执行'
                instance.next_check_at = None
        self._publish(instance)
        self.wake()
        return True

    def stop_all(self):
        self._require_owner()
        for instance in list(self.instances.values()):
            if not instance.parent_id and instance.state != WorkflowState.CLOSED:
                self.control(instance.id, 'stop')

    def _publish(self, instance):
        header = (instance.state.value, instance.stage, instance.message,
                  instance.progress, instance.desired_control, instance.policy_version)
        if self._headers.get(instance.id) == header:
            return
        self._headers[instance.id] = header
        self.events.put({'workflow_id': instance.id, 'run_id': instance.run_id,
                         'kind': 'workflow_changed', 'state': instance.state.value,
                         'stage': instance.stage, 'message': instance.message})
        self._journal(instance)

    def _journal(self, instance):
        path = Path(self.app.data_dir) / 'workflow-runs' / (instance.id + '.json')
        temporary = None
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=path.parent,
                                             prefix='.workflow-', suffix='.tmp', delete=False) as stream:
                temporary = stream.name
                json.dump(instance.public_view(), stream, ensure_ascii=False, indent=2)
                stream.flush()
            os.replace(temporary, path)
        except (OSError, ValueError, TypeError) as error:
            # This is a projection journal. Domain start intents and receipts
            # continue to own mutation admission; a log cannot replace them.
            self.journal_error = type(error).__name__
        finally:
            if temporary is not None:
                try:
                    Path(temporary).unlink(missing_ok=True)
                except OSError:
                    pass

    def finish(self, instance, status, reason):
        instance.result = {**deepcopy(instance.latest), 'status': status, 'reason': reason}
        instance.state, instance.stage = WorkflowState.CLOSED, 'closed'
        result_messages = {
            'DONE': '本次目标已完成。', 'COMPLETE': '本次目标已完成。',
            'COMPLETED': '本次目标已完成。', 'PARTIAL': '本次目标仅部分完成。',
            'UNKNOWN': '本次操作的结果尚未确认，后续操作已停止。',
            'STOPPED': '本次执行已停止。', 'FAILED': '执行中断，本次目标未完成。',
            'BLOCKED': '本次目标尚未完成，执行已中断。',
            'UNAVAILABLE': '当前条件不满足，本次未继续执行。',
            'UNSUPPORTED': '助手暂不支持自动完成此项。',
        }
        instance.message = readable_reason(reason, result_messages.get(str(status).upper(), '本次执行已结束。'))
        instance.next_check_at = None
        if self.active_id == instance.id:
            self.active_id = ''
        backend_name = DEFINITIONS[instance.kind].backend
        if self._backend_owners.get(backend_name) == instance.id:
            self._backend_owners.pop(backend_name, None)
        self._publish(instance)

    def _observe_instance(self, instance):
        definition = DEFINITIONS[instance.kind]
        backend = self.backends[definition.backend]
        snapshot = self.handlers[definition.backend].snapshot(compact=True)
        instance.latest = snapshot
        navigation = snapshot.get('navigation_status')
        flow = snapshot.get('navigation_flow') or snapshot.get('flow') or {}
        if navigation and navigation != 'READY':
            instance.stage = 'preparing_scene'
            instance.message = readable_reason(snapshot.get('navigation_reason', ''),
                '等待当前战斗收尾' if self._scene.get('phase') in ('BATTLE', 'RESULT') else '正在准备玩法场景')
            instance.state = WorkflowState.WAITING if navigation == 'WAITING' else WorkflowState.ACTIVE
        elif snapshot.get('stopping') or instance.desired_control == 'stop':
            instance.stage, instance.message = 'settling', '正在完成在途操作与收尾'
            instance.state = WorkflowState.STOPPING
        elif getattr(backend, 'is_yielded', False):
            instance.suspended = True
            instance.stage, instance.message = 'yielded', '已让出场景，等待恢复'
            instance.state = WorkflowState.PAUSED if instance.desired_control == 'pause' else WorkflowState.WAITING
            if instance.id not in self._suspended:
                self._suspended.append(instance.id)
            if self.active_id == instance.id:
                self.active_id = ''
        elif instance.desired_control == 'pause':
            instance.state, instance.message = WorkflowState.PAUSED, '已暂停后续操作'
        else:
            instance.state = WorkflowState.ACTIVE
            phase = snapshot.get('phase', '')
            instance.stage = 'battle' if phase == 'BATTLE' else 'running'
            instance.message = '等待本场战斗结果' if phase == 'BATTLE' else '正在执行' + definition.label
        if flow and isinstance(flow, dict) and flow.get('phase'):
            instance.stage = 'preparing_scene' if navigation else instance.stage
        if instance.kind in ('soul', 'soul_harvest'):
            instance.progress = (f"蓝魂 {snapshot.get('blue_completed', 0)}/{snapshot.get('blue_requested', 0)}，"
                                 f"紫魂 {snapshot.get('purple_completed', 0)}/{snapshot.get('purple_requested', 0)}")
        elif definition.battle:
            count = snapshot.get('performed_in_run', snapshot.get('settled_in_run'))
            instance.progress = (f"已完成 {count} 次" if count is not None else
                                 f"本轮失败 {snapshot.get('losses', 0)} 次")
        else:
            instance.progress = str(snapshot.get('progress') or '')
        self._publish(instance)

    def _complete_executor(self, instance):
        snapshot = deepcopy(instance.latest)
        status = snapshot.get('status') or snapshot.get('mode', 'completed')
        reason = str(snapshot.get('reason') or '本次玩法已结束')
        if status == 'WAITING' and instance.kind.startswith('daily.'):
            parent = self.instances.get(instance.parent_id)
            deferred = bool(parent and parent.options['kwargs'].get('defer_cooldowns'))
            if not deferred and instance.desired_control == 'run':
                remaining = snapshot.get('cooldown_remaining_seconds')
                seconds = max(1.0, float(remaining)) if type(remaining) in (int, float) else 60.0
                instance.next_check_at = time.monotonic() + seconds
                instance.state, instance.stage = WorkflowState.WAITING, 'cooldown'
                instance.message = '等待冷却，届时重新读取游戏状态'
                instance.started = False
                self._backend_owners.pop('daily', None)
                if self.active_id == instance.id:
                    self.active_id = ''
                self._publish(instance)
                return
        self.finish(instance, str(status), reason)

    def _aggregate_daily(self, parent):
        children = [self.instances[identifier] for identifier in parent.child_ids]
        if not children:
            return
        items, ledgers, waits, releases, retries = {}, [], [], [], []
        popup_status, popup_reason = 'IDLE', ''
        cleanup_pending = requires_online = False
        statuses = []
        for child in children:
            result = child.result or child.latest
            if not result:
                continue
            status = result.get('status', '')
            statuses.append(status)
            for item in result.get('items', ()):
                if item.get('status') == 'SKIPPED':
                    continue
                key = (item.get('kind'), item.get('category_id'), item.get('task_id'), item.get('name'))
                items[key] = deepcopy(item)
            for value in result.get('ledger_paths', ()):
                if value not in ledgers:
                    ledgers.append(value)
            waits.extend(deepcopy(result.get('cooldown_items', ())))
            releases.extend(deepcopy(result.get('cooldown_releases', ())))
            retries.extend(deepcopy(result.get('route_retry_evidence', ())))
            cleanup_pending = cleanup_pending or result.get('cleanup_pending') is True
            requires_online = requires_online or result.get('requires_online') is True
            if result.get('popup_status') not in (None, '', 'IDLE'):
                popup_status, popup_reason = result.get('popup_status'), result.get('popup_reason', '')
        finished = all(child.state == WorkflowState.CLOSED for child in children)
        active = any(child.state in (WorkflowState.ACTIVE, WorkflowState.READY) for child in children)
        if finished:
            status = ('UNKNOWN' if 'UNKNOWN' in statuses else 'WAITING' if 'WAITING' in statuses else
                      'STOPPED' if parent.desired_control == 'stop' else
                      'PARTIAL' if any(value in _TERMINAL_BAD for value in statuses) else 'DONE')
        else:
            status = 'RUNNING' if active else 'WAITING'
        reason = ('daily_cooldown_deferred' if status == 'WAITING' else
                  'user_stop' if status == 'STOPPED' else 'daily_items_observed_complete' if status == 'DONE'
                  else 'daily_workflows_pending' if not finished else 'daily_workflows_partial')
        previous = deepcopy(parent.latest)
        parent.latest = {**previous, 'status': status, 'reason': reason,
                        'character_id': next((child.latest.get('character_id') for child in children
                                              if child.latest.get('character_id')), ''),
                        'items': list(items.values()), 'ledger_paths': ledgers,
                        'popup_status': popup_status, 'popup_reason': popup_reason,
                        'cleanup_pending': cleanup_pending, 'requires_online': requires_online,
                        'cooldown_items': waits, 'cooldown_releases': releases,
                        'route_retry_evidence': retries,
                        'cooldown_remaining_seconds': min((
                            max(0, child.next_check_at - time.monotonic()) for child in children
                            if child.next_check_at is not None), default=None),
                        'cooldown_cleanup': 'VERIFIED_CLOSED' if not cleanup_pending else 'UNVERIFIED'}
        parent.progress = f"{sum(child.state == WorkflowState.CLOSED for child in children)}/{len(children)} 项"
        if finished:
            self.finish(parent, status, reason)
        elif parent.desired_control == 'pause':
            parent.state, parent.message = WorkflowState.PAUSED, '日常计划已暂停'
            self._publish(parent)
        else:
            parent.state = WorkflowState.ACTIVE if active else WorkflowState.WAITING
            parent.stage = 'plan'
            parent.message = '正在执行日常玩法' if active else '等待日常执行条件'
            self._publish(parent)

    def _candidate(self, now):
        candidates = []
        for item in self.instances.values():
            if item.child_ids or item.state == WorkflowState.CLOSED or item.desired_control != 'run':
                continue
            parent = self.instances.get(item.parent_id)
            if parent is not None and parent.desired_control != 'run':
                continue
            if item.started and not item.suspended:
                continue
            if item.next_check_at is not None and item.next_check_at > now:
                continue
            if item.parent_id:
                # Only the parent's preceding runnable siblings establish
                # order. A cooldown releases the worker for later siblings.
                siblings = parent.child_ids[:parent.child_ids.index(item.id)]
                if any(self.instances[sibling].state not in (WorkflowState.CLOSED, WorkflowState.WAITING,
                                                             WorkflowState.PAUSED) for sibling in siblings):
                    continue
            name = DEFINITIONS[item.kind].backend
            owner = self._backend_owners.get(name)
            if owner and owner != item.id and not (
                    owner == self.active_id and item.source == 'manual' and item.placement == 'switch'):
                continue
            # Waiting online tasks retain their account session, not the
            # executor: another task for that account may still proceed.
            if any(other.account_id and item.account_id and other.account_id != item.account_id
                   and other.latest.get('requires_online') is True and other.state != WorkflowState.CLOSED
                   for other in self.instances.values()):
                continue
            priority = 0 if item.source == 'manual' and item.placement == 'switch' and not item.suspended else 1
            candidates.append((priority, item.created_monotonic, item))
        return min(candidates, key=lambda value: value[:2])[2] if candidates else None

    def tick(self):
        self._require_owner()
        if self._ticking:
            return
        self._ticking = True
        try:
            scene_reader = getattr(self.app.objects, 'scene_context', None)
            self._scene = scene_reader() if scene_reader else {'label': '场景待更新', 'phase': 'UNKNOWN'}
            for name, identifier in list(self._backend_owners.items()):
                instance = self.instances[identifier]
                backend = self.backends[name]
                if instance.state == WorkflowState.CLOSED:
                    continue
                decision = self.handlers[name].advance(instance, self._scene)
                self._observe_instance(instance)
                if decision.kind == DecisionKind.FINISH:
                    self._complete_executor(instance)
            for parent in list(self.instances.values()):
                if parent.child_ids and parent.state != WorkflowState.CLOSED:
                    self._aggregate_daily(parent)
            if self._closing:
                return
            current = self.instances.get(self.active_id)
            candidate = self._candidate(time.monotonic())
            if current and current.state != WorkflowState.CLOSED:
                if candidate and candidate.id != current.id and candidate.placement == 'switch' and candidate.source == 'manual':
                    backend = self.backends[DEFINITIONS[current.kind].backend]
                    if DEFINITIONS[candidate.kind].backend == DEFINITIONS[current.kind].backend:
                        # An explicit new run of the same gameplay replaces the
                        # old run after its normal reconciliation, never reuses it.
                        if not current.stop_requested:
                            self.control(current.id, 'stop')
                    elif hasattr(backend, 'request_yield'):
                        backend.request_yield()
                        candidate.state, candidate.stage = WorkflowState.WAITING, 'handoff'
                        candidate.message = '等待当前战斗收尾后切换'
                        self._publish(candidate)
                    else:
                        candidate.state, candidate.stage = WorkflowState.WAITING, 'executor'
                        candidate.message = '等待当前操作完成'
                        self._publish(candidate)
                return
            if candidate is None:
                return
            if not self.app.account_plan.workflow_session_ready:
                candidate.state, candidate.stage = WorkflowState.WAITING, 'session'
                candidate.message = '等待账号会话准备完成'
                self._publish(candidate)
                return
            # Diagnostics shares the application but has no gameplay token.
            # It must finish before a gameplay starts using its bound window.
            if self.app.runner.create('diagnostics').is_active:
                candidate.state, candidate.stage = WorkflowState.WAITING, 'executor'
                candidate.message = '等待只读自检结束'
                self._publish(candidate)
                return
            backend_name = DEFINITIONS[candidate.kind].backend
            backend = self.backends[backend_name]
            if self.app.objects.has_campaign_owner or self.app.objects.is_busy:
                candidate.state, candidate.stage = WorkflowState.WAITING, 'handoff'
                candidate.message = '等待当前场景操作释放'
                self._publish(candidate)
                return
            connection = self.app.objects.connection_status
            if connection.get('state') != 'ready':
                if (connection.get('recovery') or {}).get('process_restart_required') is True:
                    self.finish(candidate, 'BLOCKED', 'game_process_restart_required')
                    return
                if connection.get('state') in ('disconnected', 'error', 'unknown') and not connection.get('busy'):
                    capability = self.app.objects.begin_campaign_connection()
                    if capability is not None:
                        self._session_connection, self._session_lease = capability, None
                candidate.state, candidate.stage = WorkflowState.WAITING, 'session'
                candidate.message = '正在连接当前游戏会话'
                self._publish(candidate)
                return
            if candidate.suspended:
                self.app.objects.resource_policy_provider = lambda: self.current_policy(candidate.id)
                if not backend.resume_yield():
                    candidate.state, candidate.message = WorkflowState.WAITING, '等待原玩法恢复'
                    self._publish(candidate)
                    return
                candidate.suspended = False
                if candidate.id in self._suspended:
                    self._suspended.remove(candidate.id)
                self.active_id = candidate.id
                candidate.state, candidate.stage, candidate.message = WorkflowState.ACTIVE, 'resuming', '恢复原玩法进度'
                self._publish(candidate)
                return
            if backend.is_active or backend.runner.is_active:
                return
            if self._session_connection is not None and self._session_lease is None:
                self._session_lease = self.app.objects.claim_workflow_session(self._session_connection)
                if self._session_lease is not None:
                    self._session_connection = None
            candidate.state, candidate.stage = WorkflowState.READY, 'preparing_scene'
            candidate.message = '正在准备' + DEFINITIONS[candidate.kind].label + '场景'
            self._publish(candidate)
            try:
                self.app.objects.resource_policy_provider = lambda: self.current_policy(candidate.id)
                accepted = self.handlers[backend_name].start(candidate)
            except Exception as error:
                self.finish(candidate, 'BLOCKED', f'玩法启动未完成：{type(error).__name__}: {error}')
                return
            if not accepted:
                if candidate.state != WorkflowState.CLOSED:
                    self.finish(candidate, 'BLOCKED', '当前玩法参数或执行条件未被接受。')
                return
            candidate.started, candidate.next_check_at = True, None
            self._backend_owners[backend_name] = candidate.id
            self.active_id = candidate.id
            candidate.state = WorkflowState.ACTIVE
            self._publish(candidate)
        finally:
            self._ticking = False
            runtime = getattr(self.app, 'event_runtime', None)
            pending = any(item.state != WorkflowState.CLOSED for item in self.instances.values())
            if (not pending and self._session_connection is not None and not self.app.account_plan.active
                    and not self.app.objects.has_campaign_owner and not self.app.objects.is_busy):
                capability, self._session_connection = self._session_connection, None
                self.app.objects.cancel_campaign_connection(capability)
            if (not pending and self._session_lease is not None and not self.app.account_plan.active
                    and not self.app.objects.has_campaign_owner and not self.app.objects.is_busy):
                capability, self._session_lease = self._session_lease, None
                self.app.objects.release_workflow_session(capability)
            if runtime is not None and pending and not self._closing:
                # Executor/scene notifications normally wake immediately. This
                # named watchdog also observes worker-finally and live deadlines.
                runtime.scheduler.call_later('play.watch', .25, self.tick)

    @property
    def snapshot(self):
        return self._snapshot_view(compact=False)

    @property
    def summary_snapshot(self):
        return self._snapshot_view(compact=True)

    def _snapshot_view(self, *, compact):
        scene = deepcopy(self._scene)
        scene.setdefault('label', str(scene.get('root') or scene.get('scene') or '场景待更新'))
        return {'active_id': self.active_id, 'scene': scene,
                'instances': [item.public_view(compact=compact) for item in self.instances.values()],
                'policy_error': self.policies.error, 'journal_error': self.journal_error,
                'session_activity': {'active': self.app.account_plan.active,
                    'phase': self.app.account_plan.scheduling_phase,
                    'account': self.app.account_plan.workflow_account['label']},
                'resource_capabilities': {
                    'purchase': ['soul', 'daily.soul'], 'accelerate': [], 'replenish': []},
                'pending_count': sum(item.state != WorkflowState.CLOSED and not item.child_ids
                                     for item in self.instances.values()),
                'closed': self._closing}

    @property
    def is_active(self):
        return any(item.state != WorkflowState.CLOSED for item in self.instances.values())

    def close(self):
        self.stop_all()
        self._closing = True
