"""Tk projection of play instances and the shared game scene."""
from __future__ import annotations

import copy
import re
import tkinter as tk
from tkinter import ttk

from .resource_policy_view import ResourcePolicyEditor
from .theme import ACCENT, BG, GOLD, LINE, MUTED, PANEL, PANEL_ALT, RED, TEXT
from .workflow_events import readable_reason


_STATE_LABELS = {
    'QUEUED': '排队中', 'READY': '准备就绪', 'ACTIVE': '执行中',
    'WAITING': '等待中', 'PAUSED': '已暂停', 'STOPPING': '正在停止', 'CLOSED': '已结束',
}
_KIND_PAGES = {
    'battle': 'battle', 'campaign': 'battle', 'challenge': 'challenge', 'elite': 'accounts',
    'development': 'development', 'tribute': 'development', 'equipment': 'development',
    'star': 'development', 'growth': 'development', 'claim_growth': 'development',
    'daily': 'daily', 'soul': 'soul', 'soul_quote': 'soul',
    'point_reward': 'daily',
}
_KIND_LABELS = {
    'battle': '征战', 'campaign': '征战', 'challenge': '挑战', 'elite': '精英挑战',
    'development': '培养', 'tribute': '灵兽上贡', 'equipment': '装备培养',
    'star': '星灵', 'growth': '成长任务', 'claim_growth': '领取成长奖励',
    'daily': '日常计划', 'soul': '聚魂', 'soul_quote': '聚魂报价',
    'point_reward': '积分宝箱',
}
_SOURCE_LABELS = {'account_plan': '账号计划', 'manual': '手动启动'}
_STAGE_LABELS = {
    'queued': '等待执行', 'preparing_scene': '准备场景', 'running': '执行中',
    'battle': '战斗中', 'settling': '完成结算与收尾', 'yielded': '等待恢复',
    'cooldown': '等待冷却', 'plan': '执行计划', 'handoff': '等待场景交接',
    'executor': '等待当前操作完成', 'closed': '已结束',
}
_CLOSED_VISIBLE_LIMIT = 30
_SCENE_LABELS = {'campaign': '征战', 'capital': '主城', 'anecdote': '挑战', 'battle': '战斗', 'pointrace': '积分赛'}
_SCENE_PHASE_LABELS = {
    'UNKNOWN': '待确认', 'DISCONNECTED': '未连接', 'LOADING': '加载中',
    'READY': '可操作', 'IDLE': '待命', 'NAVIGATING': '正在前往',
    'ENTERING': '正在进入', 'EXITING': '正在退出', 'RECOVERING': '正在恢复',
    'BLOCKED': '等待处理', 'BATTLE': '战斗中', 'SETTLING': '结算中',
    'RESULT': '战斗结算', 'SCENARIO': '场景中',
}


def _text(value, fallback='') -> str:
    return str(value) if value is not None and value != '' else fallback


def _page_for(kind) -> str | None:
    if isinstance(kind, str) and kind.startswith('daily.'):
        return 'daily'
    return _KIND_PAGES.get(kind)


def _human_message(value, fallback='') -> str:
    """Format a user-facing message through the shared reason catalog."""
    if not isinstance(value, str) or not value.strip():
        return fallback
    return readable_reason(value.strip(), fallback)


def _stage(value) -> str:
    if not isinstance(value, str) or not value:
        return ''
    return _STAGE_LABELS.get(value.lower()) or _human_message(value)


def _progress(value) -> str:
    if isinstance(value, dict):
        current = value.get('current', value.get('completed'))
        target = value.get('target', value.get('required'))
        if type(current) in (int, float) and type(target) in (int, float):
            return f'{current}/{target}'
        if type(current) in (int, float):
            return f'已完成 {current}'
        if type(value.get('remaining')) in (int, float):
            return f"剩余 {value['remaining']}"
        return ''
    if type(value) in (int, float):
        return str(value)
    if isinstance(value, str):
        if re.fullmatch(r'[0-9\s/:.?+%\-]+', value):
            return value
        return _human_message(value) if value else ''
    return ''


class WorkflowPage:
    """All methods run on the Tk thread; this page never submits game actions."""

    def __init__(self, app):
        self.app = app
        self._snapshot = {}
        self._instances = {}
        self._selected_id = ''
        self._policy_key = None
        self._policy_ready = False
        self._policy_dirty = False
        self._policy_drafts = {}
        self._build()
        self.update_status(app.play_runtime.snapshot)

    def _build(self) -> None:
        self.page = self.app.page('workflows', '运行')
        self.page.columnconfigure(0, weight=1)
        self.page.rowconfigure(1, weight=1)
        scene = self.app.card(self.page, name='scene_summary')
        scene.grid(row=0, column=0, sticky='ew', pady=(0, 12))
        scene.columnconfigure(0, weight=1)
        self.scene_label = self.app.label(scene, '尚未连接游戏', name='current_scene',
                                          size=12, bold=True, anchor='w')
        self.scene_label.grid(row=0, column=0, sticky='ew', padx=16, pady=(12, 4))
        self.current_work = self.app.label(scene, '当前没有执行中的任务', name='current_work',
                                           color=GOLD, anchor='w', justify='left', wraplength=700)
        self.current_work.grid(row=1, column=0, sticky='ew', padx=16, pady=(0, 12))
        scene.bind('<Configure>', lambda event: self.current_work.configure(
            wraplength=max(200, event.width - 32)))

        panels = tk.PanedWindow(self.page, name='workflow_panels', orient='horizontal',
                                bg=BG, sashwidth=8, borderwidth=0)
        panels.grid(row=1, column=0, sticky='nsew')
        queue = tk.Frame(panels, name='workflow_queue', bg=PANEL)
        detail = tk.Frame(panels, name='workflow_detail', bg=PANEL)
        panels.add(queue, minsize=300, stretch='always')
        panels.add(detail, minsize=280, width=340, stretch='always')
        queue.columnconfigure(0, weight=1)
        queue.rowconfigure(1, weight=1)
        self.queue_heading = self.app.label(queue, '运行任务', name='queue_heading',
                                            size=12, bold=True, anchor='w')
        self.queue_heading.grid(row=0, column=0, sticky='ew', padx=12, pady=12)
        style = ttk.Style(self.app)
        style.configure('Workflow.Treeview', background=PANEL_ALT, fieldbackground=PANEL_ALT,
                        foreground=TEXT, borderwidth=0, rowheight=34)
        style.configure('Workflow.Treeview.Heading', background=PANEL, foreground=TEXT,
                        relief='flat')
        style.map('Workflow.Treeview', background=[('selected', LINE)],
                  foreground=[('selected', TEXT)])
        style.configure('Workflow.TNotebook', background=PANEL, borderwidth=0)
        style.configure('Workflow.TNotebook.Tab', background=PANEL, foreground=MUTED,
                        padding=(14, 8))
        style.map('Workflow.TNotebook.Tab', background=[('selected', PANEL_ALT)],
                  foreground=[('selected', TEXT)])
        table = tk.Frame(queue, name='instance_table', bg=PANEL)
        table.grid(row=1, column=0, sticky='nsew', padx=8)
        table.columnconfigure(0, weight=1)
        table.rowconfigure(0, weight=1)
        self.instances = ttk.Treeview(table, name='workflow_instances', show='headings',
                                      columns=('play', 'account', 'state', 'stage'),
                                      selectmode='browse', style='Workflow.Treeview', height=8)
        for key, label, width in (('play', '玩法', 90), ('account', '账号', 90),
                                   ('state', '状态', 80), ('stage', '阶段 / 进度', 160)):
            self.instances.heading(key, text=label)
            self.instances.column(key, width=width, minwidth=65, stretch=True, anchor='w')
        self.instances.grid(row=0, column=0, sticky='nsew')
        vertical = ttk.Scrollbar(table, name='instance_scroll', orient='vertical',
                                 command=self.instances.yview)
        vertical.grid(row=0, column=1, sticky='ns')
        horizontal = ttk.Scrollbar(table, name='instance_horizontal_scroll', orient='horizontal',
                                   command=self.instances.xview)
        horizontal.grid(row=1, column=0, sticky='ew')
        self.instances.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        self.instances.bind('<<TreeviewSelect>>', self._selection_changed)
        self.instances.bind('<Double-1>', self._open_selection)
        self.empty_label = self.app.label(queue, '从玩法页开始任务', name='workflow_empty',
                                          color=MUTED, anchor='w')
        self.empty_label.grid(row=2, column=0, sticky='ew', padx=12, pady=10)

        detail.columnconfigure(0, weight=1)
        detail.rowconfigure(3, weight=1)
        self.selected_heading = self.app.label(detail, '选择任务', name='selected_workflow',
                                               size=12, bold=True, anchor='w', wraplength=300)
        self.selected_heading.grid(row=0, column=0, sticky='ew', padx=14, pady=(12, 6))
        self.selected_status = self.app.label(detail, '', name='selected_workflow_status',
                                              color=GOLD, anchor='w', justify='left', wraplength=300)
        self.selected_status.grid(row=1, column=0, sticky='ew', padx=14)
        self.selected_message = self.app.label(detail, '', name='workflow_wait_reason',
                                               color=MUTED, anchor='w', justify='left', wraplength=300)
        self.selected_message.grid(row=2, column=0, sticky='ew', padx=14, pady=(6, 12))

        self.sections = ttk.Notebook(detail, name='workflow_sections', style='Workflow.TNotebook')
        self.sections.grid(row=3, column=0, sticky='nsew', padx=10)
        task_section = tk.Frame(self.sections, name='workflow_actions', bg=PANEL)
        resource_section = tk.Frame(self.sections, name='workflow_resources', bg=PANEL)
        self.sections.add(task_section, text='任务')
        self.sections.add(resource_section, text='资源')
        self.target_label = self.app.label(task_section, '', name='workflow_target',
                                           anchor='nw', justify='left', wraplength=290)
        self.target_label.pack(fill='x', padx=6, pady=(14, 10))
        controls = tk.Frame(task_section, name='workflow_controls', bg=PANEL)
        controls.pack(fill='x', padx=6, pady=(0, 12))
        controls.columnconfigure((0, 1, 2), weight=1)
        self.pause_button = self.app.button(controls, '暂停', lambda: self._control('pause'),
                                            name='pause_workflow')
        self.pause_button.grid(row=0, column=0, sticky='ew', padx=(0, 5))
        self.resume_button = self.app.button(controls, '继续', lambda: self._control('resume'),
                                             name='resume_workflow', primary=True)
        self.resume_button.grid(row=0, column=1, sticky='ew', padx=(0, 5))
        self.stop_button = self.app.button(controls, '停止', lambda: self._control('stop'),
                                           name='stop_workflow', danger=True)
        self.stop_button.grid(row=0, column=2, sticky='ew')
        self.open_button = self.app.button(task_section, '打开玩法页', self._open_selection,
                                           name='open_workflow_page')
        self.open_button.pack(fill='x', padx=6)

        resource_section.columnconfigure(0, weight=1)
        resource_section.rowconfigure(0, weight=1)
        canvas = tk.Canvas(resource_section, name='policy_scroll_area', bg=PANEL,
                            highlightthickness=0, borderwidth=0)
        canvas.grid(row=0, column=0, sticky='nsew')
        scroll = ttk.Scrollbar(resource_section, name='policy_scroll', orient='vertical',
                               command=canvas.yview)
        scroll.grid(row=0, column=1, sticky='ns')
        canvas.configure(yscrollcommand=scroll.set)
        self.policy_editor = ResourcePolicyEditor(canvas, self.app, on_change=self._policy_changed)
        editor_window = canvas.create_window((0, 0), window=self.policy_editor, anchor='nw')
        self.policy_editor.bind('<Configure>', lambda _event: canvas.configure(
            scrollregion=canvas.bbox('all')), add='+')
        canvas.bind('<Configure>', lambda event: canvas.itemconfigure(
            editor_window, width=max(1, event.width)))
        def scroll_policy(event):
            if event.delta:
                canvas.yview_scroll(-1 if event.delta > 0 else 1, 'units')
            return 'break'
        canvas.bind('<MouseWheel>', scroll_policy)
        for widget in (self.policy_editor, *self.policy_editor.resource_rows.values(),
                       *self.policy_editor.resource_checks.values(), *self.policy_editor.limit_entries.values(),
                       *self.policy_editor.switch_checks.values()):
            widget.bind('<MouseWheel>', scroll_policy)
        policy_controls = tk.Frame(resource_section, name='policy_controls', bg=PANEL)
        policy_controls.grid(row=1, column=0, columnspan=2, sticky='ew', pady=(8, 4))
        self.save_policy_button = self.app.button(policy_controls, '保存策略', self._save_policy,
                                                  name='save_resource_policy')
        self.save_policy_button.pack(fill='x', pady=(0, 5))
        self.apply_policy_button = self.app.button(policy_controls, '应用于此任务', self._apply_policy,
                                                   name='apply_workflow_resource_policy', primary=True)
        self.apply_policy_button.pack(fill='x')
        self.message = self.app.label(detail, '', name='workflow_message', color=MUTED,
                                      anchor='w', justify='left', wraplength=300)
        self.message.grid(row=4, column=0, sticky='ew', padx=14, pady=(8, 12))
        detail.bind('<Configure>', lambda event: [widget.configure(wraplength=max(180, event.width - 28))
                    for widget in (self.selected_heading, self.selected_status,
                                   self.selected_message, self.target_label, self.message)])

    def update_status(self, snapshot: dict) -> None:
        """Project a runtime snapshot without changing it or controlling a run."""
        self._snapshot = copy.deepcopy(snapshot)
        raw = snapshot.get('instances', [])
        records = [item for item in raw if isinstance(item, dict) and item.get('id')]
        live = [item for item in records if item.get('state') != 'CLOSED']
        closed = [item for item in records if item.get('state') == 'CLOSED']
        # Runtime order is creation order. Keep the newest closed records and
        # retain a selected older record while it is still in the snapshot.
        visible = {str(item['id']) for item in live + closed[-_CLOSED_VISIBLE_LIMIT:]}
        if any(str(item['id']) == self._selected_id for item in closed):
            visible.add(self._selected_id)
        records = [item for item in records if str(item['id']) in visible]
        self._instances = {str(item['id']): copy.deepcopy(item) for item in records}
        old_ids = set(self.instances.get_children())
        for index, (instance_id, item) in enumerate(self._instances.items()):
            stage = _stage(item.get('stage'))
            progress = _progress(item.get('progress'))
            values = (_text(item.get('label'), _KIND_LABELS.get(item.get('kind'), '任务')),
                      _text(item.get('account_label'), '当前账号'),
                      _STATE_LABELS.get(item.get('state'), '状态待确认'),
                      ' · '.join(part for part in (stage, progress) if part))
            if instance_id in old_ids:
                if self.instances.item(instance_id, 'values') != values:
                    self.instances.item(instance_id, values=values)
                self.instances.move(instance_id, '', index)
                old_ids.remove(instance_id)
            else:
                self.instances.insert('', index, iid=instance_id, values=values)
        for instance_id in old_ids:
            self.instances.delete(instance_id)
        active_id = str(snapshot.get('active_id') or '')
        active = self._instances.get(active_id)
        scene = snapshot.get('scene') or {}
        root = _SCENE_LABELS.get(str(scene.get('root') or '').lower(), '')
        label = scene.get('label')
        scene_text = (_SCENE_LABELS.get(label.lower(), '') if isinstance(label, str) else '')
        connection = scene.get('connection')
        if not self.app.bound:
            scene_text = '未绑定游戏'
        elif connection in ('disconnected', 'error'):
            scene_text = '游戏连接不可用'
        elif connection == 'connecting':
            scene_text = '正在连接游戏'
        elif connection == 'stopping':
            scene_text = '游戏连接正在关闭'
        elif connection == 'unknown':
            scene_text = '游戏连接状态待确认'
        else:
            scene_text = scene_text or _human_message(label) or root
            if scene_text in ('场景待更新', '场景待确认'):
                scene_text = ''
            if scene.get('loading'):
                scene_text = scene_text + ' · 加载中' if scene_text else '游戏场景加载中'
            else:
                scene_text = scene_text or '场景待更新'
                phase = str(scene.get('phase') or '')
                phase_text = _SCENE_PHASE_LABELS.get(phase.upper(), '') or _human_message(phase)
                if phase_text:
                    scene_text += ' · ' + phase_text
        self.scene_label.configure(text=scene_text)
        if active:
            current = _text(active.get('account_label'), '当前账号') + ' · ' + _text(
                active.get('label'), _KIND_LABELS.get(active.get('kind'), '任务'))
            current += ' · ' + _STATE_LABELS.get(active.get('state'), '状态待确认')
        else:
            current = '当前没有执行中的任务'
        scene_reason = _human_message(scene.get('reason'))
        if scene_reason:
            current += '\n' + scene_reason
        self.current_work.configure(text=current)
        self.queue_heading.configure(text=f'运行任务（{len(live)}）' if live else '运行任务')
        hidden = len(closed) - sum(item.get('state') == 'CLOSED' for item in records)
        self.empty_label.configure(text=('从玩法页开始任务' if not records else
                                        f'另有 {hidden} 项已结束任务' if hidden > 0 else ''))
        if self._selected_id not in self._instances:
            self._selected_id = active_id if active_id in self._instances else next(iter(self._instances), '')
        if self._selected_id:
            if self.instances.selection() != (self._selected_id,):
                self.instances.selection_set(self._selected_id)
                self.instances.focus(self._selected_id)
        self._render_selection()

    def _selection_changed(self, _event=None) -> None:
        selected = self.instances.selection()
        next_id = selected[0] if selected else ''
        if next_id != self._selected_id:
            self._selected_id = next_id
            self.message.configure(text='')
        self._render_selection()

    def _render_selection(self) -> None:
        item = self._instances.get(self._selected_id)
        for action, button in (('pause', self.pause_button), ('resume', self.resume_button),
                                ('stop', self.stop_button)):
            button.configure(state='normal' if item and item.get(f'can_{action}') is True else 'disabled')
        self.open_button.configure(state='normal' if item and _page_for(item.get('kind')) else 'disabled')
        if not item:
            self.selected_heading.configure(text='选择任务')
            self.selected_status.configure(text='')
            self.selected_message.configure(text='')
            self.target_label.configure(text='从玩法页开始任务后，可在这里查看进度和控制运行。')
            self._policy_key = None
            self._policy_ready = False
            self.policy_editor.set_value({})
            self.policy_editor.set_enabled(False)
            self._sync_policy_controls()
            return
        label = _text(item.get('label'), _KIND_LABELS.get(item.get('kind'), '任务'))
        self.selected_heading.configure(text=label + ' · ' + _text(item.get('account_label'), '当前账号'))
        state = _STATE_LABELS.get(item.get('state'), '状态待确认')
        stage = _stage(item.get('stage'))
        self.selected_status.configure(text=' · '.join(part for part in (state, stage) if part))
        reason = _human_message(item.get('message'))
        self.selected_message.configure(text=reason)
        target = _human_message(item.get('target'), label)
        progress = _progress(item.get('progress'))
        source = _SOURCE_LABELS.get(item.get('source'), '来源待确认')
        details = ['目标：' + target, '来源：' + source]
        parent = self._instances.get(str(item.get('parent_id') or ''))
        if parent:
            details.append('所属计划：' + _text(parent.get('label'), '日常计划'))
        if progress:
            details.append('进度：' + progress)
        self.target_label.configure(text='\n'.join(details))
        key = (str(item.get('kind') or ''), str(item.get('account_id') or ''))
        if key != self._policy_key:
            self._load_policy(key)
        self._sync_policy_controls()

    def _load_policy(self, key) -> None:
        self._policy_key = key
        self._policy_ready = False
        self._policy_dirty = False
        kind, account_id = key
        self.policy_editor.set_enabled(False)
        try:
            value = self._policy_drafts.get(key)
            if value is None:
                value = self.app.play_runtime.policy_for(kind, account_id=account_id)
            self.policy_editor.set_resources(self.app.play_runtime.resources_for(kind))
            capabilities_for = getattr(self.app.play_runtime, 'capabilities_for', None)
            if callable(capabilities_for):
                self.policy_editor.set_capabilities(capabilities_for(kind))
            self.policy_editor.set_value(value)
        except (ValueError, RuntimeError, OSError) as exc:
            self._notify('无法读取资源策略：' + str(exc), error=True)
            return
        self._policy_dirty = key in self._policy_drafts
        self._policy_ready = True
        self.policy_editor.set_enabled(True)

    def _policy_changed(self, value: dict) -> None:
        if self._policy_key is not None:
            self._policy_drafts[self._policy_key] = copy.deepcopy(value)
            self._policy_dirty = True
            self._notify('策略尚未保存')
            self._sync_policy_controls()

    def _sync_policy_controls(self) -> None:
        selected = self._instances.get(self._selected_id)
        self.save_policy_button.configure(state='normal' if self._policy_ready else 'disabled')
        self.apply_policy_button.configure(state='normal' if self._policy_ready and selected
                                           and selected.get('state') not in ('CLOSED', 'STOPPING') else 'disabled')

    def _save_policy(self) -> None:
        if not self._policy_ready or self._policy_key is None:
            return
        try:
            value = self.policy_editor.get_value()
            kind, account_id = self._policy_key
            result = self.app.play_runtime.save_policy(kind, value, account_id=account_id)
            if result is False:
                raise RuntimeError('保存未完成')
        except (ValueError, RuntimeError, OSError) as exc:
            self._notify(str(exc), error=True)
            return
        self._policy_drafts.pop(self._policy_key, None)
        self._policy_dirty = False
        self._notify('策略已保存，后续启动时使用')

    def _apply_policy(self) -> None:
        item = self._instances.get(self._selected_id)
        if not self._policy_ready or not item or item.get('state') in ('CLOSED', 'STOPPING'):
            return
        try:
            result = self.app.play_runtime.update_policy(self._selected_id, self.policy_editor.get_value())
            if result is False:
                raise RuntimeError('任务已变化，请查看当前状态')
        except (ValueError, RuntimeError, OSError) as exc:
            self._notify(str(exc), error=True)
            return
        self._notify('已应用于此任务的后续动作')

    def _control(self, action: str) -> None:
        item = self._instances.get(self._selected_id)
        if not item or item.get(f'can_{action}') is not True:
            return
        try:
            result = self.app.play_runtime.control(self._selected_id, action)
            if result is False:
                raise RuntimeError('任务已变化，请查看当前状态')
        except (ValueError, RuntimeError, OSError) as exc:
            self._notify(str(exc), error=True)
            return
        self._notify({'pause': '已请求暂停', 'resume': '已请求继续', 'stop': '已请求停止'}[action])

    def _open_selection(self, _event=None) -> None:
        item = self._instances.get(self._selected_id)
        page = _page_for(item.get('kind')) if item else None
        if page:
            self.app.show_page(page)

    def _notify(self, message: str, *, error: bool = False) -> None:
        self.message.configure(text=_human_message(message, '操作未完成' if error else ''),
                               fg=RED if error else MUTED)
