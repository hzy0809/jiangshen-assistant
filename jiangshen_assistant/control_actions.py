"""Application commands dispatched exclusively on the Tk main thread."""
from __future__ import annotations

from copy import deepcopy
from dataclasses import asdict
import os
import time

from .core import RunnerState
from .development import EquipmentTarget, TributeTarget
from .account_profiles import public_view, SHOP_DURATIONS, validate_document
from .snapshot_projection import compact_projection as _compact_projection


_WORKFLOWS = ('daily', 'soul', 'challenge', 'elite', 'development', 'campaign')
_TASKS = {'battle': 'campaign', 'growth-claim': 'claim_growth', 'point-reward': 'point_reward'}
_DONATIONS = {'1000': '1000 钱币', '100000': '10 万钱币', '80gold': '80 元宝',
              1: '1000 钱币', 2: '10 万钱币', 3: '80 元宝'}


def _fields(params, allowed):
    unknown = set(params) - set(allowed)
    if unknown:
        raise ValueError('不支持的参数：' + ', '.join(sorted(map(str, unknown))))


def _integer(value, name, minimum, maximum=None):
    if type(value) is not int or value < minimum or maximum is not None and value > maximum:
        limit = f'{minimum}–{maximum}' if maximum is not None else f'不小于 {minimum}'
        raise ValueError(f'{name} 必须是 {limit} 的整数。')
    return value


def _active(app):
    if getattr(app, 'account_plan', None) and app.account_plan.active:
        return 'account_plan', app.account_plan
    for name in _WORKFLOWS:
        coordinator = getattr(app, name)
        if coordinator.is_active:
            return name, coordinator
    return ('check', app.runner) if app.runner.is_active else ('', None)


def _compact_play_workflows(value):
    projected = dict(value)
    projected['instances'] = [
        dict(instance, result=_compact_projection(instance['result']))
        if 'result' in instance else instance
        for instance in value['instances']
    ]
    return projected


def _status(app, *, compact=False):
    active, coordinator = _active(app)
    workflows = {}
    for name in _WORKFLOWS:
        workflow = getattr(app, name)
        summary = getattr(workflow, 'summary_snapshot', None) if compact else None
        workflows[name] = dict(active=workflow.is_active, paused=workflow.is_paused,
                               snapshot=summary if summary is not None else workflow.snapshot)
        if compact:
            workflows[name]['snapshot'] = _compact_projection(workflows[name]['snapshot'])
    return {
        'pid': os.getpid(), 'closing': app.closing,
        'shutdown_requested': bool(getattr(app, '_control_shutdown_requested', False)),
        'current_page': app.current_page, 'current_development': app.current_development,
        'runner_state': app.runner.state.value, 'active_task': active,
        'paused': bool(coordinator and (app.runner.state == RunnerState.PAUSED
                                       or getattr(coordinator, 'is_paused', False))),
        'bound': asdict(app.bound) if app.bound else None,
        'windows': [dict(index=index, **asdict(window)) for index, window in enumerate(app.windows)],
        'connection': (_compact_projection(app.objects.connection_status) if compact
                       else app.objects.connection_status),
        'auto_connection': app.auto_connection.snapshot,
        'available_pages': list(app.pages),
        'player': app.object_snapshot,
        'player_age_seconds': round(max(0, time.monotonic() - app.object_seen_monotonic), 3)
            if app.object_snapshot is not None else None,
        'workflows': workflows,
        'daily_selection': {
            'items': {key: bool(variable.get()) for key, variable in app.daily_item_vars.items()},
            'donation_type': app.settings.daily_donation_type,
            'shop_duration': app.settings.daily_shop_duration,
        },
        'account_plan': app.account_plan.snapshot,
        'account_profiles': public_view(app.account_page.document),
        'account_profiles_error': app.account_page.error,
        'elite_choices': app.objects.current_elite_choices(),
        'event_flow': app.event_runtime.snapshot,
        'play_workflows': (app.play_runtime.summary_snapshot if compact
                           else app.play_runtime.snapshot),
    }


def _equipment_values(app, options):
    names = ('equipment_id', 'desired_level', 'mode', 'max_requests', 'max_money', 'max_stone')
    _fields(options, names)
    fields = app.development_inputs['equipment']
    values = {}
    for name in names:
        if name in options:
            value = options[name]
        else:
            value = fields[name].get().strip()
            if name != 'mode':
                try:
                    value = int(value)
                except ValueError as exc:
                    raise ValueError(f'请提供有效的 {name}。') from exc
        if name == 'mode':
            if not isinstance(value, str):
                raise ValueError('mode 必须是 enchant 或 evolve。')
            value = {'强化': 'enchant', '升阶': 'evolve'}.get(value, value)
        else:
            maximum = 255 if name in ('equipment_id', 'desired_level') else 100 if name == 'max_requests' else None
            _integer(value, name, 0 if name in ('max_money', 'max_stone') else 1, maximum)
        values[name] = value
    EquipmentTarget(**values)
    return values


def _tribute_values(app, options):
    _fields(options, ('max_requests', 'use_all', 'target_tier', 'target_star', 'target_level'))
    if 'use_all' in options and type(options['use_all']) is not bool:
        raise ValueError('use_all 必须是 true 或 false。')
    for name, maximum in (('max_requests', 100), ('target_tier', 10),
                          ('target_star', 10), ('target_level', 100)):
        if name in options:
            _integer(options[name], name, 1 if name in ('max_requests', 'target_star') else 0, maximum)
    if 'target_level' in options and ('target_tier' in options or 'target_star' in options):
        raise ValueError('target_level 与 target_tier/target_star 只能选择一组。')
    use_all = options.get('use_all', not app.tribute_requests_enabled.get())
    if 'max_requests' in options and 'use_all' not in options:
        use_all = False
    try:
        requests = (options['max_requests'] if 'max_requests' in options else
                    100 if use_all else int(app.development_inputs['tribute']['max_requests'].get().strip()))
        enabled = app.tribute_target_enabled.get()
        tier = int(app.tribute_target_tier.get())
        star = int(app.tribute_target_star.get())
    except ValueError as exc:
        raise ValueError('当前上贡参数无效，请提供有效的上限和目标。') from exc
    if 'target_level' in options:
        level = options['target_level']
        enabled = level > 0
        if enabled:
            tier, star = (level - 1) // 10 + 1, (level - 1) % 10 + 1
    elif 'target_tier' in options or 'target_star' in options:
        tier, star = options.get('target_tier', tier), options.get('target_star', star)
        if tier == 0 and 'target_star' in options:
            raise ValueError('target_star 需要 1–10 的 target_tier。')
        enabled = tier > 0
    if enabled:
        _integer(tier, 'target_tier', 1, 10)
        _integer(star, 'target_star', 1, 10)
    level = (tier - 1) * 10 + star if enabled else 0
    TributeTarget(max_requests=requests, use_all=use_all, target_level=level)
    return requests, use_all, enabled, tier, star


def _set_tribute_toggle(app, name, variable, enabled):
    if bool(variable.get()) != enabled:
        # Invoke the existing checkbox command so the visible fields and variables
        # change together, just as they do for an interactive selection.
        form = app.development_pages['tribute'].children['form']
        form.children['limits'].children['enable_' + name].invoke()


def _account_plan_document(document, options):
    """Restrict one start to login or an enabled task, without saving config."""
    if not options:
        return document
    if set(options) not in ({'account_id', 'daily_item'}, {'account_id', 'campaign_only'},
                           {'account_id', 'growth_only'}, {'account_id', 'daily_item', 'wait_for_gift'},
                           {'account_id', 'point_reward_only'}, {'account_id', 'login_only'}):
        raise ValueError('账号任务 options 必须包含 account_id 及一种范围；wait_for_gift 仅适用于 daily_item。')
    account_id = options['account_id']
    if type(account_id) is not str or not account_id:
        raise ValueError('account_id 必须是已选择账号的配置 ID 文本。')
    campaign_only = 'campaign_only' in options or 'growth_only' in options
    point_reward_only = 'point_reward_only' in options
    login_only = 'login_only' in options
    daily_items = frozenset()
    if campaign_only:
        mode = 'campaign_only' if 'campaign_only' in options else 'growth_only'
        if options[mode] is not True:
            raise ValueError(mode + ' 必须是 true。')
    elif login_only:
        if options['login_only'] is not True:
            raise ValueError('login_only 必须是 true。')
    elif point_reward_only:
        if options['point_reward_only'] is not True:
            raise ValueError('point_reward_only 必须是 true。')
    else:
        item = options['daily_item']
        if type(item) is not str or item not in ('online_gift', 'divinity', 'mingshi', 'grave'):
            raise ValueError('daily_item 仅支持 online_gift、divinity、mingshi、grave。')
        daily_items = frozenset(('divinity', 'mingshi')) if item == 'mingshi' else frozenset((item,))
    if 'wait_for_gift' in options and options['wait_for_gift'] is not True:
        raise ValueError('wait_for_gift 必须是 true。')
    if 'wait_for_gift' in options and options.get('daily_item') != 'online_gift':
        raise ValueError('wait_for_gift 仅适用于 online_gift。')
    scoped = deepcopy(document)
    # Reject malformed documents and duplicate IDs, but retain the raw flags:
    # validation can migrate an absent online_gift flag to enabled.
    validate_document(scoped)
    if account_id not in scoped['selected_ids']:
        raise ValueError('指定账号必须已存在并位于当前选择列表。')
    profile = next(profile for profile in scoped['profiles'] if profile['id'] == account_id)
    if campaign_only:
        if profile['tasks']['campaign']['enabled'] is not True:
            raise ValueError('指定账号必须已明确启用征战。')
    elif not login_only:
        daily = profile['tasks']['daily']
        required = frozenset(('sign',)) if point_reward_only else daily_items
        if daily['enabled'] is not True or any(daily['options']['items'].get(key) is not True for key in required):
            raise ValueError('指定账号必须已明确启用日常及本次所选项目；收获铭石还须启用击败天神。')
    scoped['selected_ids'] = [account_id]
    for profile in scoped['profiles']:
        for key, task in profile['tasks'].items():
            if login_only or key != ('campaign' if campaign_only else 'daily'):
                task['enabled'] = False
        for key in profile['tasks']['daily']['options']['items']:
            selected = frozenset(('sign',)) if point_reward_only else daily_items
            if login_only or campaign_only or key not in selected:
                profile['tasks']['daily']['options']['items'][key] = False
    return scoped


def _run(app, params, *, compact=False):
    _fields(params, ('task', 'donation', 'options', 'placement', 'resource_policy'))
    placement = params.get('placement')
    if placement is not None and placement not in ('queue', 'switch'):
        raise ValueError('placement 必须是 queue 或 switch。')
    policy = params.get('resource_policy')
    if policy is not None:
        from .play_workflows import ResourcePolicy
        ResourcePolicy.from_dict(policy)
    app.play_runtime.submission_placement = placement
    app.play_runtime.submission_policy = policy
    try:
        return _run_ui(app, {key: value for key, value in params.items()
                             if key not in ('placement', 'resource_policy')}, compact=compact)
    finally:
        app.play_runtime.submission_placement = None
        app.play_runtime.submission_policy = None


def _run_ui(app, params, *, compact=False):
    _fields(params, ('task', 'donation', 'options'))
    task = params.get('task')
    if not isinstance(task, str):
        raise ValueError('task 必须是任务名称。')
    task = _TASKS.get(task, task)
    if task not in ('account_plan', 'campaign', 'challenge', 'daily', 'soul', 'check', 'star', 'tribute',
                    'growth', 'claim_growth', 'equipment', 'point_reward'):
        raise ValueError('不支持的任务：' + task)
    options = params.get('options', {})
    if not isinstance(options, dict):
        raise ValueError('options 必须是对象。')
    if task != 'daily' and 'donation' in params:
        raise ValueError('donation 仅适用于 daily。')
    if task not in ('account_plan', 'equipment', 'tribute', 'soul', 'daily') and options:
        raise ValueError(task + ' 不接受 options。')
    shop_duration = None
    if task == 'daily':
        _fields(options, ('shop_duration',))
        if 'shop_duration' in options:
            shop_duration = options['shop_duration']
            if type(shop_duration) is not int or shop_duration not in SHOP_DURATIONS:
                raise ValueError('shop_duration 必须为 0、1200、3600 或 43200 秒。')
    if task == 'account_plan':
        if 'donation' in params:
            raise ValueError('账号任务的捐献选项由账号配置决定。')
        document = _account_plan_document(app.account_page.document, options)
        if options.get('login_only') is True:
            accepted, reason = app.account_plan.start(document, login_only=True)
        elif options.get('growth_only') is True:
            accepted, reason = app.account_plan.start(document, growth_only=True)
        elif options.get('wait_for_gift') is True:
            accepted, reason = app.account_plan.start(document, wait_for_gift=True)
        elif options.get('point_reward_only') is True:
            accepted, reason = app.account_plan.start(document, point_reward_only=True)
        else:
            accepted, reason = app.account_plan.start(document)
        if not accepted:
            raise RuntimeError(reason)
        app.show_page('accounts')
        status = _status(app, compact=compact)
        if options:
            for profile in status['account_profiles']['profiles']:
                profile.pop('login_name', None)
        return dict(accepted=True, action='run', task=task, status=status)
    donation = None
    if 'donation' in params:
        value = params['donation']
        if type(value) not in (str, int) or value not in _DONATIONS:
            raise ValueError('donation 必须是 1000、100000、80gold，或数值 1/2/3。')
        donation = _DONATIONS[value]
    values = (_equipment_values(app, options) if task == 'equipment' else
              _tribute_values(app, options) if task == 'tribute' else None)
    soul_policy = None
    if task == 'soul':
        policy_fields = ('policy_version', 'max_growths', 'protect_level', 'protect_count',
                         'high_level_count', 'model_path', 'explore_growths', 'exploration_mode',
                         'exploration_money_limit', 'exploration_loss_limit', 'frontier_money_limit')
        _fields(options, ('blue_count', 'purple_count', *policy_fields))
        if any(key in options for key in policy_fields):
            from .soul_strategy import SoulPolicy
            arguments = {key: options[key] for key in policy_fields if key in options}
            if 'policy_version' in arguments:
                arguments['version'] = arguments.pop('policy_version')
            soul_policy = SoulPolicy(**arguments)
        try:
            blue = options['blue_count'] if 'blue_count' in options else int(app.soul_blue_count.get())
            purple = options['purple_count'] if 'purple_count' in options else int(app.soul_purple_count.get())
        except ValueError as exc:
            raise ValueError('请输入有效的聚魂数量。') from exc
        _integer(blue, 'blue_count', 0, 100)
        _integer(purple, 'purple_count', 0, 100)
        if not 1 <= blue + purple <= 100:
            raise ValueError('聚魂总次数必须为 1–100。')
        values = (blue, purple)
    if app.closing:
        raise RuntimeError('助手正在关闭。')
    if not app.bound:
        raise RuntimeError('请先绑定游戏窗口。')
    if task == 'check' and app.runner.is_active:
        raise RuntimeError('请等待当前执行完成后再运行只读自检。')
    if task == 'daily' and not app.selected_daily_items():
        raise ValueError('请先在日常页面勾选至少一项；选择会自动保存。')
    if task == 'equipment':
        for name, value in values.items():
            app.development_inputs['equipment'][name].set(
                {'enchant': '强化', 'evolve': '升阶'}[value] if name == 'mode' else str(value))
    elif task == 'tribute':
        requests, use_all, enabled, tier, star = values
        app.development_inputs['tribute']['max_requests'].set(str(requests))
        app.tribute_target_tier.set(str(tier))
        app.tribute_target_star.set(str(star))
        _set_tribute_toggle(app, 'requests', app.tribute_requests_enabled, not use_all)
        _set_tribute_toggle(app, 'target', app.tribute_target_enabled, enabled)
    elif task == 'soul':
        app.soul_blue_count.set(str(values[0]))
        app.soul_purple_count.set(str(values[1]))
    if task in ('campaign', 'challenge', 'daily', 'soul', 'check', 'point_reward'):
        app.show_page({'campaign': 'battle', 'check': 'diagnostics', 'point_reward': 'daily'}.get(task, task))
        if donation is not None:
            app.daily_donation.set(donation)
        if shop_duration is not None:
            app.daily_shop_duration.set(SHOP_DURATIONS[shop_duration])
        start = {'campaign': app.start_campaign, 'challenge': app.start_challenge,
                 'daily': app.start_daily, 'soul': app.start_soul, 'check': app.start_check,
                 'point_reward': app.start_point_reward}[task]
        accepted = start(policy=soul_policy) if task == 'soul' and soul_policy is not None else start()
    else:
        app.show_page('development')
        app.show_development('growth' if task == 'claim_growth' else task)
        accepted = app.start_development(task)
    if not accepted:
        raise RuntimeError('任务未被接受，请检查游戏连接与当前任务状态。')
    return dict(accepted=True, action='run', task=task, status=_status(app, compact=compact))


def _pause_resume(app, method, *, compact=False):
    identifier = app.play_runtime.active_id
    if identifier:
        if not app.play_runtime.control(identifier, method):
            raise RuntimeError('当前玩法暂不能' + ('暂停。' if method == 'pause' else '继续。'))
        return dict(accepted=True, action=method, workflow_id=identifier, status=_status(app, compact=compact))
    name, coordinator = _active(app)
    if coordinator is None:
        raise RuntimeError('当前没有运行中的任务。')
    if app.runner.state == RunnerState.STOPPING:
        raise RuntimeError('当前任务正在停止。')
    paused = (app.runner.state == RunnerState.PAUSED
              or getattr(coordinator, 'is_paused', False))
    already = (paused if name == 'account_plan' else paused and app.runner.state == RunnerState.PAUSED) if method == 'pause' else not paused
    if not already and not getattr(coordinator, method)():
        raise RuntimeError('当前任务暂不能暂停。' if method == 'pause' else '当前任务暂不能继续。')
    if name == 'account_plan':
        app.account_page.update_status(app.account_plan.snapshot)
    elif name != 'check':
        getattr(app, '_update_' + name + '_controls')()
    app._update_run_controls()
    return dict(accepted=True, action=method, already=already, status=_status(app, compact=compact))


def _bind(app, params, *, compact=False):
    _fields(params, ('hwnd', 'index'))
    if len(params) > 1:
        raise ValueError('hwnd 与 index 只能提供一个。')
    if (app.account_plan.active or app.runner.is_active or app.objects.is_busy or app.objects.has_campaign_owner
            or any(getattr(app, name).is_active for name in _WORKFLOWS)):
        raise RuntimeError('当前任务或接口仍被占用，暂不能重新绑定。')
    if 'index' in params:
        index = _integer(params['index'], 'index', 0)
    elif 'hwnd' in params:
        hwnd = _integer(params['hwnd'], 'hwnd', 1)
        index = next((i for i, window in enumerate(app.windows) if window.hwnd == hwnd), -1)
        if index < 0:
            raise ValueError('未找到指定的游戏窗口，请先用 bind 刷新窗口列表。')
    else:
        app.refresh_windows()
        return dict(accepted=True, action='bind', status=_status(app, compact=compact))
    if index >= len(app.windows):
        raise ValueError('窗口序号超出 status.windows 的范围。')
    target = app.windows[index]
    if not app.auto_connection.choose(target.hwnd):
        raise RuntimeError('当前暂不能选择该游戏窗口，请等待自动发现完成。')
    return dict(accepted=True, action='bind', status=_status(app, compact=compact))


def dispatch(app, method, params):
    """Return JSON-compatible results; transport wraps ValueError/RuntimeError.

    ``run`` accepts the GUI task names, optional daily donation/shop duration,
    and optional equipment/tribute parameters. Unspecified fields retain GUI values.
    ``account_plan`` optionally accepts account_id together with exactly one of
    daily_item=online_gift, campaign_only=true or growth_only=true, restricting
    only this start to the already enabled selected gift or campaign. The last
    mode delegates battle-free growth handling to the account plan coordinator.
    Only the gift scope additionally accepts wait_for_gift=true for one bounded
    online wait for the initially observed tier; saved configuration is untouched.
    The separate login_only=true scope verifies one selected account and leaves
    its game open without dispatching tasks or saving account configuration.
    An accepted command starts a request; its later progress is read via status.
    """
    if not isinstance(method, str) or not isinstance(params, dict):
        raise ValueError('method 必须是字符串，params 必须是对象。')
    if method not in ('status', 'run', 'soul-quote', 'soul-harvest', 'reconnect', 'recover-soul', 'pause', 'resume', 'stop', 'bind', 'shutdown',
                      'workflows', 'workflow-control', 'workflow-policy'):
        raise ValueError('不支持的命令：' + method)
    compact = False
    if method in ('status', 'run', 'soul-quote', 'soul-harvest', 'reconnect', 'recover-soul', 'pause', 'resume', 'stop', 'bind',
                  'workflows', 'workflow-control'):
        compact = params.get('compact', False)
        if type(compact) is not bool:
            raise ValueError('compact 必须是 true 或 false。')
        params = {key: value for key, value in params.items() if key != 'compact'}
    if method not in ('status', 'run', 'bind', 'workflow-control', 'workflow-policy'):
        _fields(params, ())
    if method == 'status':
        _fields(params, ())
        return _status(app, compact=compact)
    if method == 'workflows':
        return app.play_runtime.summary_snapshot if compact else app.play_runtime.snapshot
    if app.closing or getattr(app, '_control_shutdown_requested', False):
        if method == 'shutdown':
            return dict(accepted=True, action='shutdown', pid=os.getpid(), already=True)
        raise RuntimeError('助手正在关闭。')
    if method == 'run':
        return _run(app, params, compact=compact)
    if method == 'workflow-control':
        _fields(params, ('workflow_id', 'action'))
        identifier, action = params.get('workflow_id'), params.get('action')
        if not isinstance(identifier, str) or not app.play_runtime.control(identifier, action):
            raise RuntimeError('玩法不存在、已结束或暂不能执行此操作。')
        workflows = app.play_runtime.summary_snapshot if compact else app.play_runtime.snapshot
        return dict(accepted=True, workflow_id=identifier, action=action,
                    workflows=workflows)
    if method == 'workflow-policy':
        _fields(params, ('kind', 'account_id', 'policy'))
        from .play_workflows import DEFINITIONS
        kind, account_id = params.get('kind'), params.get('account_id', '')
        if kind not in DEFINITIONS or not isinstance(account_id, str):
            raise ValueError('玩法或账号标识无效。')
        if 'policy' in params:
            value = app.play_runtime.save_policy(kind, params['policy'], account_id)
        else:
            value = app.play_runtime.policy_for(kind, account_id)
        return dict(kind=kind, account_id=account_id, policy=value,
                    resources=app.play_runtime.resources_for(kind),
                    capabilities=app.play_runtime.capabilities_for(kind))
    if method == 'soul-quote':
        if not app.bound or not app.refresh_soul_quote():
            raise RuntimeError('聚魂报价暂不可读取，请检查游戏连接与当前任务状态。')
        return dict(accepted=True, action='soul-quote', status=_status(app, compact=compact))
    if method == 'soul-harvest':
        if not app.bound or not app.harvest_soul_board():
            raise RuntimeError('收获当前牌面暂未接受，请检查窗口和正在执行的操作。')
        return dict(accepted=True, action='soul-harvest', status=_status(app, compact=compact))
    if method in ('reconnect', 'recover-soul'):
        app.auto_connection.resume()
        if not app.bound or not app.objects.connect():
            raise RuntimeError('重连暂未接受，请等待当前连接操作结束或绑定游戏窗口。')
        return dict(accepted=True, action=method, status=_status(app, compact=compact))
    if method in ('pause', 'resume'):
        return _pause_resume(app, method, compact=compact)
    if method == 'bind':
        return _bind(app, params, compact=compact)
    if method == 'stop':
        app.stop()
        return dict(accepted=True, action='stop', status=_status(app, compact=compact))
    # Finish the transport response before close retires the control endpoint.
    app._control_shutdown_requested = True
    app.after_idle(app.close)
    return dict(accepted=True, action='shutdown', pid=os.getpid())
