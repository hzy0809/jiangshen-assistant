"""Desktop event handlers, called exclusively by the Tk owner thread.

Handlers project observed facts into views and logs. Domain coordinators keep
command admission and settlement policies; application_runtime owns scheduling.
"""
from __future__ import annotations

import time

from .account_profiles import DAILY_CATALOG, DAILY_CATEGORY_LABELS
from .theme import GOLD, GREEN, MUTED, RED
from .workflow_events import (WorkflowEventKind, readable_message, readable_reason,
                              workflow_command_ui_message, workflow_log_message,
                              workflow_notice_ui_message, workflow_ui_message)


class ApplicationHandlers:
    def __init__(self, app) -> None:
        self.app = app


    def soul(self, event) -> None:
        app = self.app
        result = app.soul.summary_snapshot
        status = result.get('status', '')
        title = {'IDLE': '待命', 'CONNECTING': '正在连接', 'QUOTING': '正在读取报价',
                 'QUOTED': '报价已更新', 'RUNNING': '执行中',
                 'DONE': '已完成', 'COMPLETED': '已完成', 'PARTIAL': '部分完成',
                 'UNAVAILABLE': '暂不可做', 'BLOCKED': '本轮未完成',
                 'UNKNOWN': '结果未确认', 'STOPPED': '已停止'}.get(status, status)
        blue = result.get('blue_completed', 0)
        purple = result.get('purple_completed', 0)
        requested_blue = result.get('blue_requested', 0)
        requested_purple = result.get('purple_requested', 0)
        spent = result.get('coins_spent', 0)
        detail = (f'蓝魂 {blue}/{requested_blue} · 紫魂 {purple}/{requested_purple} · 已确认支出 {spent:,} 钱币'
                  if type(spent) is int else f'蓝魂 {blue}/{requested_blue} · 紫魂 {purple}/{requested_purple}')
        reason_code = result.get('reason', '')
        reason_fallback = {
            'UNKNOWN': '本次聚魂的结果尚未确认，后续聚魂已停止。',
            'BLOCKED': '本次聚魂已停止，目标尚未完成。',
            'UNAVAILABLE': '当前条件不满足，本次未继续聚魂。',
            'PARTIAL': '本次只完成部分聚魂目标。',
        }.get(status, '')
        reason = readable_reason(reason_code, reason_fallback) if reason_code else ''
        show_detail = status not in ('IDLE', 'QUOTING', 'QUOTED')
        app.soul_status.configure(text=title + ('  ·  ' + detail if show_detail else '') + ('\n' + reason if reason else ''),
            fg=RED if status in ('BLOCKED', 'UNKNOWN') else MUTED)
        if status not in ('IDLE', 'CONNECTING', 'QUOTING', 'QUOTED', 'RUNNING') and result != app._soul_last_event:
            app.log('聚魂：' + title + ' · ' + detail,
                     'WARNING' if status in ('BLOCKED', 'UNKNOWN', 'PARTIAL') else 'INFO',
                     file_message='聚魂：' + status + ' · ' + str(result.get('reason', '')) + ' · ' + detail)
        app._soul_last_event = result
        app._show_soul_quote(result.get('quote'))
        app._update_soul_controls()


    def daily(self, result) -> None:
        app = self.app
        groups = DAILY_CATEGORY_LABELS
        names = {3: '领取竞技宝箱', 5: '聚魂', 6: '培养灵兽', 7: '收获铭石', 8: '击败天神',
                 9: '排位竞技场挑战', 10: '协助好友降妖', 11: '送花献礼', 12: '参加答题',
                 13: '参加比武大会', 14: '名将试炼', 15: '主城降妖', 16: '采石',
                 17: '地宫探宝', 18: '同盟抽奖', 20: '同盟捐献', 21: '炼魂',
                 22: '木人试炼', 23: '消灭训练妖魔', 24: '复仇魔王战斗与收取', 109: '分组积分赛挑战',
                 201: '精彩活动', 202: 'VIP福利', 203: '限时活动', 301: '每日签到', 302: '领俸', 303: '抄录'}
        statuses = {'ALREADY_COMPLETE': '已完成', 'ALREADY_CLAIMED': '已领取', 'COMPLETED': '已完成',
                    'NO_FREE_REWARD': '暂无可领', 'CONDITION_NOT_MET': '未开放',
                    'UNAVAILABLE': '暂不可做', 'UNSUPPORTED': '尚未接入', 'PENDING': '待执行',
                    'UNKNOWN': '结果未确认', 'BLOCKED': '未完成', 'STOPPED': '已停止', 'PARTIAL': '部分完成',
                    'SKIPPED': '未启用'}
        title = {'CONNECTING': '正在连接', 'RUNNING': '执行中', 'DONE': '已完成',
                 'PARTIAL': '部分未完成', 'BLOCKED': '本轮未完成', 'UNKNOWN': '结果未确认',
                 'STOPPED': '已停止', 'WAITING': '等待中', 'UNAVAILABLE': '暂不可做',
                 'PROCESSED': '已处理，等待确认'}.get(result.status, '待命')
        app.daily_status.configure(text=title, fg=RED if result.status in ('UNKNOWN', 'BLOCKED') else GOLD)
        lines = []
        for item in result.items:
            if item.status == 'SKIPPED':
                continue
            name = '分类宝箱' if item.kind == 'category_reward' else names.get(item.task_id, item.name or '未知任务')
            progress = f' {item.current}/{item.required}' if item.current is not None and item.required is not None else ''
            if item.category_id == 3 and item.task_id == 9 and item.money_current is not None and item.reputation_current is not None:
                progress = (f' 钱币 {item.money_current}/{item.money_limit}'
                            f'  声望 {item.reputation_current}/{item.reputation_limit}')
            why = readable_reason(item.reason, '')
            if item.task_id == 301 and item.checkin_state_observed and item.checkin_today == 1:
                why = '界面清理待确认' if item.checkin_cleanup == 'UNVERIFIED' else ''
            if item.category_id == 2 and item.task_id == 5 and item.soul_blue_completed is not None:
                progress += f'  本次蓝 {item.soul_blue_completed} 次、紫 {item.soul_purple_completed} 次'
                skip_names = {'soul_color_locked': '免费资格未开放', 'soul_free_exhausted': '免费次数已用完',
                              'soul_daily_color_done': '今日已聚魂'}
                skipped = [f"{'蓝魂' if color == 'blue' else '紫魂'}{skip_names.get(reason, '暂不可做')}"
                           for color, reason in item.soul_skipped]
                if skipped:
                    why = '；'.join(skipped)
            group = ('主城' if item.kind == 'city_activity' else
                     '竞技' if item.kind == 'activity' and item.task_id in (9, 109)
                     else groups.get(item.category_id, '日常'))
            display_status = statuses.get(item.status, item.status)
            if item.task_id == 301 and item.status in ('COMPLETED', 'ALREADY_CLAIMED'):
                display_status = '本次已签到' if item.status == 'COMPLETED' else '今日已签到'
            elif item.kind == 'sign_page_reward' and item.status == 'COMPLETED':
                display_status = '已领取'
            lines.append(f"{group} · {name}{progress}  {display_status}" + (f'（{why}）' if why else ''))
        if result.popup_status in ('BLOCKED', 'WAITING', 'UNKNOWN'):
            lines.insert(0, '奖励窗口  待确认')
        app.daily_results.configure(state='normal')
        app.daily_results.delete('1.0', 'end')
        app.daily_results.insert('1.0', '\n'.join(lines))
        app.daily_results.configure(state='disabled')
        if result.status not in ('RUNNING', 'CONNECTING'):
            app.log('日常：' + title, 'WARNING' if result.status in ('PARTIAL', 'BLOCKED', 'UNKNOWN') else 'INFO',
                     file_message='日常：' + result.status + ' · ' + result.reason)
        app._update_daily_controls()


    def challenge(self, event) -> None:
        app = self.app
        if event.message:
            if event.message.startswith(('征战状态：', '挑战状态：')):
                state = event.data or {}
                mode = {'idle': '待命', 'active': '自动挑战中', 'paused': '已暂停', 'blocked': '本轮已停止',
                        'stopped': '已停止', 'loss_limit': '累计三败停止'}.get(state.get('mode'), '状态已更新')
                why = readable_reason(state['reason']) if state.get('reason') else ''
                message = '挑战：' + mode + ('；' + why if why else '。')
            elif event.message.startswith(('停止征战：', '停止挑战：')):
                message = '停止挑战：' + readable_reason(event.message.partition('：')[2], '已请求停止后续挑战。')
            else:
                message = readable_message(event.message.replace('征战', '挑战'), level=event.level)
            app.log(message, event.level, file_message=event.message)
        self.refresh_challenge()


    def refresh_challenge(self) -> None:
        app = self.app
        data = app.challenge.snapshot
        losses = data.get('losses', 0)
        mode, phase = data.get('mode', 'idle'), data.get('phase', '')
        reason = str(data.get('reason') or '')
        app.challenge_values['losses'].configure(text=f'{losses} / 3', fg=RED if losses >= 3 else GOLD)
        target = data.get('stage_label') or ('当前普通关卡' if data.get('stage') else '等待识别')
        app.challenge_values['stage'].configure(text=target)
        label = {'idle': '待命', 'active': '自动挑战中', 'paused': '已暂停', 'blocked': '本轮已停止',
                 'stopped': '已停止', 'loss_limit': '累计三败停止'}.get(mode, '正在连接')
        if mode == 'active' and phase == 'BATTLE':
            label = '等待本场结算'
        if app.challenge.is_active and data.get('stopping'):
            label = '正在停止后续挑战'
        app.challenge_values['status'].configure(text=label, fg=RED if mode in ('blocked', 'loss_limit') else GOLD)
        detail = readable_reason(reason, '') if reason else (
            '正在推进挑战' if app.challenge.is_active else app._task_idle_detail())
        app.challenge_detail.configure(text=detail, fg=RED if mode == 'blocked' else MUTED)
        app._update_challenge_controls()


    def elite(self, event) -> None:
        app = self.app
        if event.message:
            app.log(readable_message(event.message, level=event.level), event.level, file_message=event.message)


    def development(self, result) -> None:
        app = self.app
        app._development_has_status = True
        status = {'DONE': '本次目标已达', 'NEEDS_CAMPAIGN': '需要继续征战', 'BLOCKED': '本轮已停止，请检查当前界面', 'UNKNOWN': '本轮结果未确认，已停止', 'STOPPED': '已停止', 'RUNNING': '执行中', 'CONNECTING': '连接中', 'NEEDS_NAVIGATION': '等待进入征战'}.get(result.status, '状态已更新')
        reason = readable_reason(result.reason, '') if result.reason else ''
        app.development_status.configure(text=status + (' · ' + reason if reason else '') + ('\n' + result.progress if result.progress else ''), fg=RED if result.status in ('BLOCKED', 'UNKNOWN') else MUTED)
        raw_status = status if status != '状态已更新' else result.status
        progress = (' · ' + result.progress) if result.progress else ''
        if result.status != 'RUNNING':
            app._visible_log_states.pop(('development',), None)
        app.log('成长与培养：' + status + (' · ' + reason if reason else '') + progress,
                 'WARNING' if result.status in ('BLOCKED', 'UNKNOWN') else 'INFO',
                 file_message='成长与培养：' + raw_status + ' · ' + result.reason + progress,
                 ui_state_key=('development',) if result.status == 'RUNNING' else None)
        if not app._development_has_status:
            app.development_status.configure(text=app._task_idle_detail(), fg=MUTED)
        if hasattr(app, 'workflow_page'):
            app._render_workflow_state()
        app._update_development_controls()


    def campaign(self, event) -> None:
        app = self.app
        if event.message:
            if event.message.startswith('征战状态：'):
                state = event.data or {}
                mode = {'idle': '待命', 'active': '自动推进中', 'paused': '已暂停', 'blocked': '需要确认', 'stopped': '已停止', 'loss_limit': '连续三败停止'}.get(state.get('mode'), '状态已更新')
                reason = state.get('reason')
                stage = str(state.get('stage') or '').split(':')
                location = ' · '.join(label + ' ' + number for label, number in zip(('章节', '敌军'), stage) if number.isdecimal() and int(number) > 0)
                message = (location + ' · ' if location else '') + '征战状态：' + mode + ('；' + readable_reason(reason) if reason else '。')
            elif event.message.startswith('停止征战：'):
                message = '停止征战：' + readable_reason(event.message.partition('：')[2], '已请求停止后续发起。')
            else:
                message = readable_message(event.message, level=event.level)
            app.log(message, event.level, file_message=event.message)
        self.refresh_campaign()


    def refresh_campaign(self) -> None:
        app = self.app
        data = app.campaign.snapshot
        mode, phase = str(data.get("mode", "idle")), str(data.get("phase", ""))
        losses = data.get("losses", 0)
        app.campaign_values["losses"].configure(text=f"{losses} / 3", fg=RED if losses >= 3 else GOLD)
        stage = str(data.get("stage", ""))
        parts = stage.split(":")
        target = "等待识别"
        if len(parts) == 2 and parts[0] != "0":
            target = f"章节 {parts[0]} · 等待选择" if parts[1] == "0" else f"章节 {parts[0]} · 敌军 {parts[1]}"
        app.campaign_values["stage"].configure(text=target)
        labels = {"idle": "待命", "active": "自动推进中", "paused": "已暂停", "blocked": "需要确认", "stopped": "已停止", "loss_limit": "连续三败停止"}
        label = "等待本场结算" if mode == "active" and phase == "BATTLE" else labels.get(mode, "正在连接")
        if app.campaign.is_active and data.get("stopping"):
            label = "正在停止后续发起"
        if data.get("cleanup_state") == "unknown" or data.get("cleanup_error"):
            label = "接口清理待确认"
        app.campaign_values["status"].configure(text=label, fg=RED if mode in ("blocked", "loss_limit") else GOLD)
        reason = str(data.get("reason", ""))
        details = {
            "three_consecutive_losses": "连续失败已达三次，已停止发起战斗。",
            "unknown_guide": "检测到尚未接入的教学，请在游戏内处理后继续。",
            "no_eligible_stage": "暂未发现可继续挑战的敌军，请检查当前章节或教学。",
            "user_pause": "已暂停后续发起，当前战斗仍会继续结算。",
            "user_stop": "已请求停止后续发起；若有在途战斗，正在核对结算。",
            "assistant_shutdown": "助手已关闭本轮任务。再次启动将读取当前游戏页面。",
            "task_cancelled": "任务已取消，停止后续发起。",
            "explicit_start_required": "准备就绪，可开始征战。",
            "observed_loss_recorded": "已按本场小败结果补记一次失败，可继续征战。",
            "game_transition_timeout": "游戏过场等待超时，已暂停后续发起；请确认当前界面。",
        }
        if reason:
            detail = details.get(reason, readable_reason(reason, ''))
        elif app.campaign.is_active:
            detail = "等待当前战斗结算。" if phase == "BATTLE" else "正在按游戏当前进度推进。"
        else:
            detail = app._task_idle_detail()
        if reason == "user_stop" and not app.campaign.is_active:
            detail = "本轮已停止，当前战斗结果尚未确认；请在游戏中核对本场结果。" if data.get("pending") else "征战已停止。再次启动将读取当前游戏页面。"
        if data.get("cleanup_state") == "unknown" or data.get("cleanup_error"):
            detail += " 连接尚未完成清理，请先在连接页停止并恢复；仍未恢复时正常退出游戏。"
        app.campaign_detail.configure(text=detail, fg=RED if mode == "blocked" else MUTED)
        ledger = data.get("ledger_path")
        app.campaign_ledger.configure(text="运行记录已保存" if ledger else "")
        app._update_campaign_controls()


    def objects(self, event) -> None:
        app = self.app
        data = event.data or {}
        if event.kind != "log" and data.get("generation") != app.objects.generation:
            return
        if event.kind == "player":
            if not app.bound or (data.get("pid") is not None and data["pid"] != app.bound.process_id):
                return
            app.object_snapshot = data
            app.object_seen_monotonic = time.monotonic()
        elif event.kind == "state_error":
            app.object_snapshot = None
            app.object_seen_monotonic = 0.0
        elif event.kind == "bridge":
            app.object_connection = data
        elif event.kind == "log":
            workflow_event = data.get("workflow_event")
            notice = data.get("workflow_notice")
            ui_state_key = None
            visible = True
            if workflow_event is not None:
                message, raw = workflow_ui_message(workflow_event), workflow_log_message(workflow_event)
                if workflow_event.kind == WorkflowEventKind.STATE_CHANGED:
                    ui_state_key = ('workflow', workflow_event.context_key, workflow_event.run_id,
                                    workflow_event.generation, workflow_event.kind.value)
                elif workflow_event.kind in (WorkflowEventKind.GROWTH_TASK_CHANGED, WorkflowEventKind.EQUIPMENT_CHANGED,
                                              WorkflowEventKind.TRIBUTE_CHANGED, WorkflowEventKind.STAR_CHANGED):
                    module = {WorkflowEventKind.GROWTH_TASK_CHANGED: 'growth',
                              WorkflowEventKind.EQUIPMENT_CHANGED: 'equipment',
                              WorkflowEventKind.TRIBUTE_CHANGED: 'tribute',
                              WorkflowEventKind.STAR_CHANGED: 'star'}[workflow_event.kind]
                    domain = (workflow_event.snapshot.get('development') or {}).get(module) or {}
                    operation = domain.get('operation') or {}
                    ui_state_key = ('workflow', workflow_event.context_key, workflow_event.run_id,
                                    workflow_event.generation, workflow_event.kind.value,
                                    operation.get('operation_id'), operation.get('sequence'))
                visible = bool(message)
                if not visible and event.level != 'INFO':
                    message, visible = '游戏操作尚未取得确认，请在游戏中核对本次结果。', True
            elif data.get("workflow_command"):
                command = data.get("workflow_command_info") or {}
                message, raw = workflow_command_ui_message(command), event.message
                visible = command.get('stage') != 'admitted' or event.level != 'INFO'
            elif notice:
                message = workflow_notice_ui_message(notice)
                raw = event.message
            else:
                message = raw = event.message
            app.log(message, event.level, file_message=raw, visible=visible, ui_state_key=ui_state_key,
                     update_status=workflow_event is None and not data.get("workflow_command") and not notice)
        self.refresh_player_age()


    def refresh_player_age(self) -> None:
        app = self.app
        if app.object_snapshot is not None and time.monotonic() - app.object_seen_monotonic > 4:
            app.object_snapshot = None
            app.object_seen_monotonic = 0.0
        app._update_object_controls()
        app._update_run_controls()


    def runner(self, event) -> None:
        app = self.app
        if event.kind == 'log':
            app.log(event.message, event.level, event.timestamp)
        elif event.kind == 'done':
            if getattr(event, 'source', '') in ('', 'diagnostics'):
                result = {'completed': '自检已结束', 'stopped': '自检已停止',
                          'failed': '自检未通过，请确认游戏窗口有效、已绑定且处于可操作状态。'}.get(event.message, event.message)
                app.log(result, event.level)
            else:
                app.log('', event.level, visible=False, update_status=False,
                        file_message=f'执行步骤结束 source={event.source} outcome={event.message}')
        if event.kind in ('state', 'done'):
            self.refresh_controls()

    def refresh_controls(self) -> None:
        app = self.app
        app._update_run_controls()
        app._update_object_controls()
        app._update_campaign_controls()
        app._update_challenge_controls()
        app._update_development_controls()
        app._update_daily_controls()
        app._update_soul_controls()
        self.refresh_campaign()
        self.refresh_challenge()
        if not app._development_has_status:
            app.development_status.configure(text=app._task_idle_detail(), fg=MUTED)
