"""Account plan editor; game actions stay in the existing coordinators."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import re
import time
import tkinter as tk
from tkinter import ttk

from .account_profiles import AccountProfileError, AccountStore, DAILY_CATALOG, DAILY_CATEGORY_LABELS, DAILY_GROUPS, SHOP_DURATIONS, TASK_CATALOG, new_profile
from .theme import ACCENT, BG, GOLD, GREEN, LINE, MUTED, PANEL, PANEL_ALT, RED, TEXT
from .workflow_events import readable_reason

_IDENTITY_FIELDS = (
    ('label', '名称'), ('login_name', '登录账号'), ('password', '密码'),
    ('server_name', '区服名称'), ('role_name', '角色名称'),
    ('user_id', '账号 ID（选填）'), ('zone_id', '区服 ID（选填）'), ('role_id', '角色 ID（选填）'),
)
_SERVER_TABS = {'未指定': '', '网页版': 'tab_nav_web', '三端版': 'tab_nav_sd', '移动版': 'tab_nav_mobile'}
_VISIBLE_TASKS = tuple(key for key in TASK_CATALOG if key not in ('growth', 'claim_growth'))
_OPTION_FIELDS = {
    'elite': (('target', '每日目标次数', 'int'), ('campaign_id', '关卡编号（0 为未选择）', 'int'),
              ('star', '星级（0 为未选择）', 'int')),
    'soul': (('blue_count', '蓝魂次数', 'int'), ('purple_count', '紫魂次数', 'int')),
    'tribute': (('max_requests', '上贡次数上限', 'int'), ('use_all', '使用全部现有桃子', 'bool'),
                ('target_level', '目标等级（0 为不限定）', 'int')),
    'equipment': (('equipment_id', '装备编号', 'int'), ('desired_level', '目标等级', 'int'),
                  ('mode', '培养方式', 'mode'), ('max_requests', '次数上限', 'int'),
                  ('max_money', '铜钱上限', 'int'), ('max_stone', '强化石上限', 'int')),
    'star': (('max_practice', '战魂上限（留空为不限）', 'optional_int'),),
}
DAILY_BLOCKED = {}
DAILY_ITEM_NOTES = {
    **DAILY_BLOCKED,
    'sign': '含俸禄、竞技宝箱和积分宝箱',
    'events': '仅领取免费奖励；七进七出需要消耗资源或成本未知时跳过。',
    'mingshi': '需要同时启用“击败天神”。',
    'tribute': '使用“灵兽上贡”的目标和次数上限，每次消耗 1 颗桃子。',
}
_STATE_TEXT = {
    'IDLE': '待命', 'QUEUED': '排队中', 'RUNNING': '执行中', 'WAITING': '等待中',
    'BLOCKED': '已阻塞', 'UNAVAILABLE': '暂不可用', 'NOT_OPEN': '尚未开放',
    'SKIPPED': '未启用', 'COMPLETED': '已完成', 'ALREADY_COMPLETE': '已完成',
    'PROCESSED': '已处理（未回包）',
    'ALREADY_CLAIMED': '已领取', 'NO_FREE_REWARD': '暂无可领', 'CONDITION_NOT_MET': '条件未满足',
    'PENDING': '待处理', 'PARTIAL': '部分完成', 'FAILED': '失败',
    'UNKNOWN': '结果未知', 'STOPPED': '已停止', 'PAUSED': '已暂停',
    'DISPATCHING': '正在派发', 'STOPPING': '正在停止', 'RECOVERING': '正在恢复',
    'UNSUPPORTED': '尚未支持', 'UNOBSERVED': '尚未观察到', 'LOCKED': '尚未解锁',
    'EXCLUDED': '不在本轮范围', 'COOLDOWN': '冷却中', 'WAITING_COOLDOWN': '冷却中',
    'FREE_LIMIT_REACHED': '免费次数已用完', 'READY': '准备就绪', 'CLAIMED': '已领取',
    'OPENING': '正在打开', 'CLOSING': '正在关闭', 'DONE': '已完成', 'COMPLETE': '已完成',
    'LOSS_LIMIT': '失败次数达到上限', 'ACTIVE': '执行中',
}
def _display_reason(reason):
    if not reason:
        return ''
    return readable_reason(reason, '')


def _display_state(status):
    return _STATE_TEXT.get(str(status or '').upper(), '状态尚未确认')


def _display_record_name(value, fallback):
    text = str(value or '').replace('VIP', '贵宾').replace('vip', '贵宾')
    if any('\u4e00' <= char <= '\u9fff' for char in text) and not re.search(r'[A-Za-z_]', text):
        return text
    return fallback


def _display_item_name(item):
    if item.get('kind') == 'category_reward':
        category = DAILY_CATEGORY_LABELS.get(item.get('category_id'))
        return category + '宝箱' if category else '日常分类宝箱'
    return _display_record_name(item.get('name'), '任务项')


def _display_number(value):
    return str(value) if type(value) in (int, float) else '?'


def _display_progress(value):
    if type(value) in (int, float):
        return str(value)
    if isinstance(value, dict):
        parts = [label + ' ' + _display_number(value[key]) for key, label in (
            ('current', '当前'), ('required', '要求'), ('completed', '已完成'),
            ('target', '目标'), ('remaining', '剩余')) if type(value.get(key)) in (int, float)]
        mode = value.get('status') or value.get('mode')
        if mode:
            parts.append(_display_state(mode))
        return '，'.join(parts) or '进度尚未确认'
    text = str(value or '')
    if re.fullmatch(r'[0-9\s/:.?+%\-]+', text):
        return text
    if text.upper() in _STATE_TEXT:
        return _display_state(text)
    return _display_record_name(text, '进度尚未确认')


class AccountPage:
    """One editor for profile identity, task switches and their options."""

    def __init__(self, app):
        self.app = app
        self.store = AccountStore(app.data_dir)
        self.error = ''
        try:
            self.document = self.store.load()
        except AccountProfileError as exc:
            self.document = {'schema_version': 1, 'selected_ids': [], 'profiles': []}
            self.error = str(exc)
        self._saved_document = copy.deepcopy(self.document) if not self.error else None
        self.current_id = ''
        self.selected_account_id = tk.StringVar(app, name='selected_account_id', value='')
        self._record_snapshot = app.account_plan.snapshot
        self._record_rendered = None
        self._record_source = None
        self.profile = None
        self._loading = False
        self._task = ''
        self._identity_pending = False
        self._save_pending = None
        self._selection_save_pending = None
        self._options_dirty = False
        self.identity_vars = {}
        self.enabled_vars = {}
        self.option_vars = {}
        self.daily_vars = {}
        self.selected_daily_policy_kind = tk.StringVar(app, name='account_daily_policy_kind', value='')
        self.daily_policy_choice = tk.StringVar(app, name='account_daily_policy_choice', value='')
        self.daily_policy_picker = None
        self.daily_resource_policy_button = None
        self.account_selection_vars = {}
        self.account_checkbuttons = {}
        self.clear_password = tk.BooleanVar(app, name='account_clear_password', value=False)
        self.elite_choice = tk.StringVar(app, name='account_elite_choice', value='')
        self._elite_choices = {}
        self._build()
        self._refresh_accounts()
        if self.document['profiles']:
            self._select_account(self.document['profiles'][0]['id'])
        if self.error:
            self._message(self.error, error=True)

    def _build(self):
        page = self.app.page('accounts', '账号')
        outer = tk.PanedWindow(page, name='account_panels', orient='horizontal', sashwidth=6,
                               bg=BG, borderwidth=0, relief='flat')
        outer.pack(fill='both', expand=True)
        left = tk.Frame(outer, name='account_list_panel', bg=PANEL, width=230)
        right = tk.Frame(outer, name='account_editor_panel', bg=PANEL)
        outer.add(left, minsize=180, width=230, stretch='never')
        outer.add(right, minsize=400, stretch='always')
        left.grid_rowconfigure(1, weight=1)
        left.grid_columnconfigure(0, weight=1)
        right.grid_rowconfigure(1, weight=1)
        right.grid_columnconfigure(0, weight=1)
        self.account_count = tk.Label(left, name='account_count', text='账号', bg=PANEL, fg=TEXT,
                                      anchor='w', font=('Microsoft YaHei UI', 11, 'bold'))
        self.account_count.grid(row=0, column=0, sticky='ew', padx=12, pady=12)
        style = ttk.Style(self.app)
        style.configure('Account.Treeview', background=PANEL_ALT, fieldbackground=PANEL_ALT,
                        foreground=TEXT, bordercolor=PANEL, rowheight=34)
        style.configure('Account.Treeview.Heading', background=PANEL, foreground=TEXT, relief='flat')
        style.map('Account.Treeview', background=[('selected', LINE)], foreground=[('selected', TEXT)])
        style.configure('Account.TNotebook', background=PANEL, borderwidth=0)
        style.configure('Account.TNotebook.Tab', background=PANEL, foreground=MUTED, padding=(18, 10))
        style.map('Account.TNotebook.Tab', background=[('selected', PANEL_ALT)],
                  foreground=[('selected', TEXT)])
        list_frame = tk.Frame(left, name='account_list', bg=PANEL)
        list_frame.grid(row=1, column=0, sticky='nsew', padx=8)
        list_frame.grid_rowconfigure(0, weight=1)
        list_frame.grid_columnconfigure(0, weight=1)
        self.account_canvas = tk.Canvas(list_frame, name='account_profiles', bg=PANEL_ALT,
                                        highlightthickness=0, borderwidth=0)
        self.account_canvas.grid(row=0, column=0, sticky='nsew')
        list_scroll = ttk.Scrollbar(list_frame, orient='vertical', command=self.account_canvas.yview)
        list_scroll.grid(row=0, column=1, sticky='ns')
        self.account_canvas.configure(yscrollcommand=list_scroll.set)
        self.account_rows = tk.Frame(self.account_canvas, name='rows', bg=PANEL_ALT)
        self.account_rows_window = self.account_canvas.create_window((0, 0), window=self.account_rows, anchor='nw')
        self.account_rows.bind('<Configure>', lambda _event: self.account_canvas.configure(
            scrollregion=self.account_canvas.bbox('all')))
        self.account_canvas.bind('<Configure>', lambda event: self.account_canvas.itemconfigure(
            self.account_rows_window, width=event.width))
        self.account_canvas.bind('<MouseWheel>', lambda event: self._scroll_canvas(event, self.account_canvas))
        tk.Button(left, name='add_account', text='新增账号', command=self.add,
                  bg=PANEL_ALT, fg=TEXT, relief='flat', pady=9).grid(row=2, column=0, sticky='ew', padx=10, pady=12)

        header = tk.Frame(right, name='account_actions', bg=PANEL)
        header.grid(row=0, column=0, sticky='ew', padx=16, pady=12)
        header.grid_columnconfigure(0, weight=1)
        self.account_heading = tk.Label(header, name='selected_account_heading', text='选择或新增账号',
                                        bg=PANEL, fg=TEXT, anchor='w', width=1,
                                        font=('Microsoft YaHei UI', 13, 'bold'))
        self.account_heading.grid(row=0, column=0, sticky='ew')
        self.save_button = tk.Button(header, name='save_account', text='保存', command=self.save,
                                      bg=PANEL_ALT, fg=TEXT, relief='flat', padx=16, pady=7)
        self.save_button.grid(row=0, column=1)

        self.sections = ttk.Notebook(right, name='account_sections', style='Account.TNotebook')
        self.sections.grid(row=1, column=0, sticky='nsew', padx=16)
        self.task_section = tk.Frame(self.sections, name='task_configuration', bg=PANEL_ALT)
        self.identity_section = tk.Frame(self.sections, name='account_identity', bg=PANEL_ALT)
        self.record_section = tk.Frame(self.sections, name='execution_records', bg=PANEL_ALT)
        self.sections.add(self.task_section, text='任务配置')
        self.sections.add(self.identity_section, text='账号信息')
        self.sections.add(self.record_section, text='执行记录')
        self.selected_section = tk.StringVar(self.app, name='selected_account_section', value='task_configuration')
        self.sections.bind('<<NotebookTabChanged>>', self._section_changed)
        self._build_task_section()
        self._build_identity_section()
        self._build_record_section()

        footer = tk.Frame(right, name='account_status_panel', bg=PANEL)
        footer.grid(row=2, column=0, sticky='ew', padx=16, pady=(10, 4))
        self.plan_status = tk.Label(footer, name='account_plan_status', text='待命', bg=PANEL, fg=GOLD,
                                    anchor='w', justify='left')
        self.plan_status.pack(fill='x')
        self.message = tk.Label(footer, name='account_message', text='', bg=PANEL, fg=MUTED,
                                anchor='w', justify='left', wraplength=700)
        self.message.pack(fill='x', pady=(4, 0))
        footer.bind('<Configure>', lambda event: (self.message.configure(wraplength=max(200, event.width)),
                                                  self.plan_status.configure(wraplength=max(200, event.width))))
        controls = tk.Frame(right, name='plan_controls', bg=PANEL)
        controls.grid(row=3, column=0, sticky='ew', padx=16, pady=(4, 12))
        controls.grid_columnconfigure(3, weight=1)
        self.start_button = tk.Button(controls, name='start_account_plan', text='开始一轮', command=self.start,
                                      bg=ACCENT, fg=BG, relief='flat', padx=18, pady=9)
        self.start_button.grid(row=0, column=0, padx=(0, 8), sticky='w')
        self.pause_button = tk.Button(controls, name='pause_account_plan', text='暂停', command=self.pause,
                                      bg=PANEL_ALT, fg=TEXT, relief='flat', padx=14, pady=9)
        self.pause_button.grid(row=0, column=1, padx=(0, 8), sticky='w')
        self.stop_button = tk.Button(controls, name='stop_account_plan', text='停止', command=self.stop,
                                     bg=PANEL_ALT, fg=RED, relief='flat', padx=14, pady=9)
        self.stop_button.grid(row=0, column=2, sticky='w')
        self.human_button = tk.Button(controls, name='confirm_login_action', text='人工操作已完成',
                                      command=self.confirm_login, bg=ACCENT, fg=BG, relief='flat', padx=14, pady=9)
        self.human_button.grid(row=1, column=0, columnspan=4, sticky='ew', pady=(8, 0))
        self.human_button.grid_remove()
        self.save_button.configure(state='disabled')

    def _section_changed(self, _event=None):
        selected = self.sections.select()
        if selected:
            self.selected_section.set(selected.rsplit('.', 1)[-1])

    def _build_task_section(self):
        center = tk.PanedWindow(self.task_section, name='task_panels', orient='horizontal', sashwidth=6,
                                bg=PANEL_ALT, borderwidth=0, relief='flat')
        center.pack(fill='both', expand=True, padx=10, pady=12)
        tasks = tk.Frame(center, name='tasks_panel', bg=PANEL_ALT, width=142)
        options = tk.Frame(center, name='options_panel', bg=PANEL_ALT)
        center.add(tasks, minsize=115, width=142, stretch='never')
        center.add(options, minsize=240, stretch='always')
        task_scroll = ttk.Scrollbar(tasks, orient='vertical')
        task_scroll.pack(side='right', fill='y')
        self.task_list = tk.Listbox(tasks, name='account_task_list', exportselection=False,
                                    bg=PANEL_ALT, fg=TEXT, selectbackground=LINE,
                                    selectforeground=TEXT, relief='flat', borderwidth=0, height=8,
                                    width=12, activestyle='none', yscrollcommand=task_scroll.set)
        self.task_list.pack(fill='both', expand=True, padx=(0, 6))
        task_scroll.configure(command=self.task_list.yview)
        self.task_list.bind('<<ListboxSelect>>', self._select_task)
        policy_actions = tk.Frame(options, name='account_resource_policy_actions', bg=PANEL_ALT)
        policy_actions.pack(fill='x', padx=12, pady=(0, 10))
        self.resource_policy_context = tk.Label(policy_actions, name='account_resource_policy_context',
                                                text='请先选择账号', bg=PANEL_ALT, fg=MUTED,
                                                anchor='w', width=1)
        self.resource_policy_context.pack(side='left', fill='x', expand=True)
        self.resource_policy_button = tk.Button(policy_actions, name='open_account_resource_policy',
            text='资源策略', command=self._open_task_resource_policy, state='disabled',
            bg=PANEL, fg=TEXT, disabledforeground=MUTED, relief='flat', padx=12, pady=6)
        self.resource_policy_button.pack(side='right', padx=(8, 0))
        self.option_canvas = tk.Canvas(options, name='account_options_canvas', bg=PANEL_ALT,
                                       highlightthickness=0, borderwidth=0, height=1, width=1)
        scrollbar = ttk.Scrollbar(options, orient='vertical', command=self.option_canvas.yview)
        scrollbar.pack(side='right', fill='y')
        self.option_canvas.configure(yscrollcommand=scrollbar.set)
        self.option_canvas.pack(side='left', fill='both', expand=True)
        self.option_frame = tk.Frame(self.option_canvas, name='account_task_options', bg=PANEL_ALT)
        self.option_window = self.option_canvas.create_window((0, 0), window=self.option_frame, anchor='nw')
        self.option_canvas.bind('<Configure>', self._resize_options)
        self.option_canvas.bind('<MouseWheel>', self._scroll_options)
        self.option_frame.bind('<Configure>', lambda _e: self.option_canvas.configure(scrollregion=self.option_canvas.bbox('all')))
        tk.Label(self.option_frame, text='选择左侧任务', bg=PANEL_ALT, fg=MUTED).pack(anchor='w', padx=12, pady=12)

    def _build_identity_section(self):
        canvas = tk.Canvas(self.identity_section, name='identity_canvas', bg=PANEL_ALT,
                            highlightthickness=0, height=1, width=1)
        scroll = ttk.Scrollbar(self.identity_section, orient='vertical', command=canvas.yview)
        scroll.pack(side='right', fill='y')
        canvas.configure(yscrollcommand=scroll.set)
        canvas.pack(side='left', fill='both', expand=True)
        identity = tk.Frame(canvas, name='identity_fields', bg=PANEL_ALT)
        window = canvas.create_window((0, 0), window=identity, anchor='nw')
        self._identity_cells = []
        for key, title in _IDENTITY_FIELDS:
            cell = tk.Frame(identity, name=key, bg=PANEL_ALT)
            self._identity_cells.append(cell)
            tk.Label(cell, text=title, bg=PANEL_ALT, fg=MUTED, anchor='w').pack(fill='x', pady=(0, 5))
            value = tk.StringVar(self.app, name='account_' + key, value='')
            self.identity_vars[key] = value
            field = tk.Frame(cell, bg=PANEL_ALT)
            field.pack(fill='x')
            entry = tk.Entry(field, name='value', textvariable=value, show='•' if key == 'password' else '',
                             width=18, bg=PANEL, fg=TEXT, insertbackground=TEXT, relief='flat')
            if key == 'server_name':
                entry.pack(side='left', fill='x', expand=True, ipady=7)
                self.identity_vars['server_tab'] = tk.StringVar(self.app, name='account_server_tab', value='未指定')
                ttk.Combobox(field, name='server_tab', textvariable=self.identity_vars['server_tab'],
                             values=tuple(_SERVER_TABS), state='readonly', width=7).pack(side='right', padx=(8, 0))
            else:
                entry.pack(fill='x', ipady=7)
            if key == 'password':
                tk.Checkbutton(cell, name='clear_password', text='清除已存密码', variable=self.clear_password,
                               bg=PANEL_ALT, fg=TEXT, activebackground=PANEL_ALT, activeforeground=TEXT,
                               selectcolor=PANEL).pack(anchor='w', pady=(6, 0))
                tk.Label(cell, text='留空保留已存密码', bg=PANEL_ALT, fg=MUTED, anchor='w').pack(fill='x')
        self.identity_actions = tk.Frame(identity, name='identity_actions', bg=PANEL_ALT)
        tk.Button(self.identity_actions, name='use_current_identity', text='读取当前角色', command=self.use_current_identity,
                  bg=PANEL, fg=TEXT, relief='flat', padx=14, pady=8).pack(side='left')
        canvas.bind('<Configure>', lambda event: self._resize_identity(event, canvas, window, identity))
        identity.bind('<Configure>', lambda _event: canvas.configure(scrollregion=canvas.bbox('all')))
        self._bind_canvas_wheel(identity, canvas)
        canvas.bind('<MouseWheel>', lambda event: self._scroll_canvas(event, canvas))

    def _resize_identity(self, event, canvas, window, identity):
        canvas.itemconfigure(window, width=event.width)
        columns = 2 if event.width >= 550 else 1
        for column in range(2):
            identity.grid_columnconfigure(column, weight=1 if column < columns else 0,
                                           uniform='identity' if column < columns else '')
        self.identity_actions.grid(row=0, column=0, columnspan=columns, sticky='ew', padx=16, pady=(16, 4))
        for index, cell in enumerate(self._identity_cells):
            cell.grid(row=1 + index // columns, column=index % columns, sticky='new', padx=16, pady=12)

    def _build_record_section(self):
        actions = tk.Frame(self.record_section, name='record_actions', bg=PANEL_ALT)
        actions.pack(fill='x', padx=16, pady=12)
        self.record_summary = tk.Label(actions, name='record_summary', text='请选择账号',
                                       bg=PANEL_ALT, fg=MUTED, anchor='w', justify='left',
                                       font=('Microsoft YaHei UI', 11, 'bold'))
        self.record_summary.pack(side='left', fill='x', expand=True, padx=(0, 12))
        self.recent_records_button = tk.Button(actions, name='view_recent_account_round', text='最近一轮',
                  command=self.show_recent_records, bg=PANEL, fg=TEXT, relief='flat', padx=12, pady=6)
        self.recent_records_button.pack(side='right')
        actions.bind('<Configure>', lambda event: self.record_summary.configure(
            wraplength=max(120, event.width - 112)))
        result_frame = tk.Frame(self.record_section, name='account_results_panel', bg=PANEL_ALT)
        result_frame.pack(fill='both', expand=True, padx=16, pady=(0, 16))
        result_scroll = ttk.Scrollbar(result_frame, orient='vertical')
        result_scroll.pack(side='right', fill='y')
        self.results = tk.Text(result_frame, name='account_results', height=1, width=1, state='disabled',
                               bg=PANEL, fg=TEXT, relief='flat', wrap='word', spacing2=4, spacing3=7,
                               font=('Microsoft YaHei UI', 11), cursor='arrow',
                               selectbackground=LINE, selectforeground=TEXT,
                               yscrollcommand=result_scroll.set, padx=18, pady=16)
        self.results.pack(fill='both', expand=True)
        result_scroll.configure(command=self.results.yview)
        self.results.tag_configure('task', font=('Microsoft YaHei UI', 12, 'bold'),
                                   spacing1=16, spacing3=9)
        self.results.tag_configure('state', font=('Microsoft YaHei UI', 11, 'bold'))
        self.results.tag_configure('item', lmargin1=12, lmargin2=12, spacing1=8, spacing3=5)
        self.results.tag_configure('detail', foreground=MUTED, lmargin1=12, lmargin2=12)
        self.results.tag_configure('account_detail', foreground=MUTED, spacing3=12)
        self.results.tag_configure('time', foreground=MUTED, font=('Microsoft YaHei UI', 10),
                                   lmargin1=12, lmargin2=12, spacing3=8)
        for name, color in (('success', GREEN), ('attention', GOLD), ('error', RED),
                            ('active', ACCENT), ('muted', MUTED)):
            self.results.tag_configure(name, foreground=color)
        self._render_records()

    def _resize_options(self, event):
        self.option_canvas.itemconfigure(self.option_window, width=event.width)
        if self._task == 'daily' and hasattr(self, '_daily_groups'):
            self._layout_daily_items(event.width)

    def _bind_canvas_wheel(self, widget, canvas):
        widget.bind('<MouseWheel>', lambda event: self._scroll_canvas(event, canvas))
        for child in widget.winfo_children():
            self._bind_canvas_wheel(child, canvas)

    @staticmethod
    def _scroll_canvas(event, canvas):
        if canvas.bbox('all') and canvas.bbox('all')[3] > canvas.winfo_height():
            canvas.yview_scroll(-int(event.delta / 120), 'units')
        return 'break'

    def _message(self, message, *, error=False):
        self.message.configure(text=message, fg=RED if error else MUTED)

    def _refresh_accounts(self):
        active = self.current_id
        self._loading = True
        try:
            for child in self.account_rows.winfo_children():
                child.destroy()
            self.account_selection_vars = {}
            self.account_checkbuttons = {}
            selected = set(self.document['selected_ids'])
            for profile in self.document['profiles']:
                profile_id = profile['id']
                label = profile['label'] or profile['login_name'] or '未命名账号'
                role = profile['role_name']
                title = label + (' / ' + role if role else '')
                row = tk.Frame(self.account_rows, name='account_' + profile_id.replace('-', '_'), bg=PANEL_ALT)
                row.pack(fill='x')
                variable = tk.BooleanVar(self.app, value=profile_id in selected)
                self.account_selection_vars[profile_id] = variable
                checkbox = tk.Checkbutton(row, name='include_account_' + profile_id.replace('-', '_'),
                    text='', variable=variable,
                    command=lambda pid=profile_id: self._toggle_account(pid),
                    bg=PANEL_ALT, fg=TEXT, activebackground=PANEL_ALT, activeforeground=TEXT,
                    selectcolor=PANEL,
                    state='disabled' if self.error or self.app.account_plan.active or self.app.closing else 'normal')
                self.account_checkbuttons[profile_id] = checkbox
                checkbox.pack(side='left')
                button = tk.Button(row, name='edit_account', text=title, anchor='w',
                    command=lambda pid=profile_id: self._select_account(pid),
                    bg=LINE if profile_id == active else PANEL_ALT, fg=TEXT,
                    activebackground=LINE, activeforeground=TEXT, relief='flat')
                button.pack(side='left', fill='x', expand=True)
                server = tk.Label(row, name='server', text=profile['server_name'], bg=PANEL_ALT, fg=MUTED)
                server.pack(side='right', padx=(2, 4))
                self._bind_canvas_wheel(row, self.account_canvas)
            self.account_count.configure(text=f"账号 · {len(self.document['profiles'])}")
            profile = next((profile for profile in self.document['profiles'] if profile['id'] == active), None)
            if profile is not None:
                title = profile['label'] or profile['role_name'] or '未命名账号'
                self.account_heading.configure(text=title)
            else:
                self.account_heading.configure(text='选择或新增账号')
            self.save_button.configure(state='normal' if profile is not None else 'disabled')
            self._sync_resource_policy_controls()
        finally:
            self._loading = False

    def _toggle_account(self, profile_id):
        variable = self.account_selection_vars.get(profile_id)
        if self._loading or self.error or variable is None:
            return
        if self.app.account_plan.active or self.app.closing:
            variable.set(profile_id in self.document['selected_ids'])
            return
        selected = [value for value in self.document['selected_ids'] if value != profile_id]
        if variable.get():
            selected.append(profile_id)
        self.document['selected_ids'] = selected
        if self._selection_save_pending is not None:
            self.app.after_cancel(self._selection_save_pending)
        self._selection_save_pending = self.app.after(300, self._save_selection)

    def _save_selection(self):
        self._selection_save_pending = None
        return self._persist_document()

    def _persist_document(self):
        try:
            document = self.store.validate_document(self.document)
            if document != self._saved_document:
                if self.app.account_plan.active:
                    return False
                self.store.save(document)
                self._saved_document = copy.deepcopy(document)
            self.document = document
        except AccountProfileError as exc:
            self._message(str(exc), error=True)
            return False
        return True

    def _make_vars(self, profile):
        self.enabled_vars = {}
        self.option_vars = {}
        self.daily_vars = {}
        for key, task in profile['tasks'].items():
            self.enabled_vars[key] = tk.BooleanVar(self.app, value=task['enabled'])
            self.option_vars[key] = {}
            for name, value in task['options'].items():
                if name == 'items':
                    self.daily_vars = {item: tk.BooleanVar(self.app, value=enabled)
                                       for item, enabled in value.items()}
                elif type(value) is bool:
                    self.option_vars[key][name] = tk.BooleanVar(self.app, value=value)
                else:
                    display = {'enchant': '强化', 'evolve': '升阶'}.get(value, value) if name == 'mode' else value
                    self.option_vars[key][name] = tk.StringVar(self.app, value='' if display is None else str(display))
        variables = [*self.enabled_vars.values(), *self.daily_vars.values()]
        variables.extend(variable for options in self.option_vars.values() for variable in options.values())
        for variable in variables:
            variable.trace_add('write', lambda *_args: self._queue_option_save())

    def _queue_option_save(self):
        if self._loading or self.profile is None:
            return
        self._options_dirty = True
        if self._save_pending is not None:
            self.app.after_cancel(self._save_pending)
        self._message('任务配置尚未保存…')
        profile_id = self.current_id
        self._save_pending = self.app.after(300, lambda: self._save_options(profile_id))

    def _save_options(self, profile_id):
        self._save_pending = None
        if profile_id != self.current_id:
            return False
        return self.save()

    def flush_pending_options(self):
        if self._selection_save_pending is not None:
            self.app.after_cancel(self._selection_save_pending)
            if not self._save_selection():
                return False
        if self._save_pending is None:
            if self._options_dirty or self.document != self._saved_document:
                return self.save() if self.profile is not None else self._persist_document()
            return True
        self.app.after_cancel(self._save_pending)
        return self._save_options(self.current_id)

    def _load_profile(self, profile):
        self._loading = True
        self.profile = copy.deepcopy(profile)
        self.current_id = profile['id']
        self.selected_account_id.set(self.current_id)
        for key, _label in _IDENTITY_FIELDS:
            self.identity_vars[key].set('' if key == 'password' else profile[key])
        self.identity_vars['server_tab'].set(next((name for name, value in _SERVER_TABS.items()
                                                    if value == profile['server_tab']), '未指定'))
        self.clear_password.set(False)
        self._make_vars(profile)
        self._refresh_task_list()
        self.task_list.selection_clear(0, 'end')
        self.task_list.selection_set(0)
        self._task = ''
        self._select_task()
        self._refresh_accounts()
        self._render_records()
        self._loading = False
        self._message('已载入账号配置。')

    def _select_account(self, profile_id):
        if self._loading:
            return
        if profile_id == self.current_id:
            return
        if self.app.account_plan.active:
            # Viewing another account must not persist editor state while the
            # plan runs. Keep real edits in memory for the next idle save.
            if self.profile is not None and not self._capture():
                self._refresh_accounts()
                return
            self._options_dirty = self._options_dirty or self.document != self._saved_document
            for attribute in ('_save_pending', '_selection_save_pending'):
                pending = getattr(self, attribute)
                if pending is not None:
                    self.app.after_cancel(pending)
                    setattr(self, attribute, None)
        else:
            if not self.flush_pending_options():
                self._refresh_accounts()
                return
            if self.profile is not None and not self.save():
                self._refresh_accounts()
                return
        profile = next((p for p in self.document['profiles'] if p['id'] == profile_id), None)
        if profile is not None:
            self._load_profile(profile)

    def _refresh_task_list(self):
        current = self._task
        self.task_list.delete(0, 'end')
        for key in _VISIBLE_TASKS:
            title = TASK_CATALOG[key]
            self.task_list.insert('end', ('☑ ' if self.enabled_vars.get(key) and self.enabled_vars[key].get() else '☐ ') + title)
        if current in _VISIBLE_TASKS:
            self.task_list.selection_set(_VISIBLE_TASKS.index(current))

    def _select_task(self, _event=None):
        selection = self.task_list.curselection()
        if self.profile is None or not selection:
            return
        key = _VISIBLE_TASKS[selection[0]]
        self._task = key
        for child in self.option_frame.winfo_children():
            child.destroy()
        self.daily_policy_picker = None
        self.daily_resource_policy_button = None
        self._daily_groups = []
        enable = tk.Checkbutton(self.option_frame, name='enable_' + key, text='启用 ' + TASK_CATALOG[key],
                                variable=self.enabled_vars[key], command=self._refresh_task_list,
                                bg=PANEL_ALT, fg=TEXT, activebackground=PANEL_ALT, activeforeground=TEXT,
                                selectcolor=PANEL)
        if key == 'daily':
            enable.pack(anchor='w', padx=12, pady=(4, 12))
            self._daily_options()
        else:
            enable.pack(anchor='w', padx=8, pady=8)
            for name, title, kind in _OPTION_FIELDS.get(key, ()):
                self._option(key, name, title, kind)
            if key == 'elite':
                self.elite_choice.set('')
                self._elite_choices = {}
                picker = tk.Frame(self.option_frame, name='elite_candidates', bg=PANEL_ALT)
                picker.pack(fill='x', padx=10, pady=5)
                tk.Button(picker, name='refresh', text='读取游戏可选关卡', command=self._read_elite_choices,
                          bg=PANEL, fg=TEXT, relief='flat').pack(anchor='w')
                self.elite_choice_box = ttk.Combobox(picker, name='choice', textvariable=self.elite_choice,
                                                     values=(), state='readonly')
                self.elite_choice_box.pack(fill='x', pady=(4, 0))
                self.elite_choice_box.bind('<<ComboboxSelected>>', self._select_elite_choice)
            if key == 'soul':
                tk.Label(self.option_frame, text='免费次数用完后，所选次数可能消耗钱币。',
                         bg=PANEL_ALT, fg=MUTED, justify='left', anchor='w', wraplength=420).pack(fill='x', padx=10, pady=6)
        self._bind_option_wheel(self.option_frame)
        self.option_canvas.yview_moveto(0)
        self._sync_resource_policy_controls()

    def _policy_account_selected(self):
        return bool(self.current_id and self.profile is not None
                    and self.profile.get('id') == self.current_id
                    and any(profile.get('id') == self.current_id
                            for profile in self.document['profiles']))

    def _sync_resource_policy_controls(self):
        selected = self._policy_account_selected()
        enabled = selected and not self.error and not self.app.closing
        title = TASK_CATALOG.get(self._task, '请选择玩法') if selected else '请先选择账号'
        self.resource_policy_context.configure(text=title)
        self.resource_policy_button.configure(
            state='normal' if enabled and self._task in TASK_CATALOG else 'disabled')
        if self.daily_policy_picker is not None:
            self.daily_policy_picker.configure(state='readonly' if enabled else 'disabled')
        if self.daily_resource_policy_button is not None:
            kind = self.selected_daily_policy_kind.get()
            self.daily_resource_policy_button.configure(
                state='normal' if enabled and kind.startswith('daily.')
                and kind[6:] in DAILY_CATALOG else 'disabled')

    def _open_resource_policy(self, kind):
        if not self._policy_account_selected():
            self._message('请先选择账号', error=True)
            return
        if self.error or self.app.closing:
            self._message('当前账号配置不可编辑' if self.error else '助手正在退出', error=True)
            return
        valid = kind in TASK_CATALOG or (kind.startswith('daily.') and kind[6:] in DAILY_CATALOG)
        if not valid:
            self._message('请选择玩法', error=True)
            return
        self.app.show_resource_policy(kind, account_id=self.current_id)

    def _open_task_resource_policy(self):
        self._open_resource_policy(self._task)

    def _select_daily_resource_policy(self, _event=None):
        index = self.daily_policy_picker.current() if self.daily_policy_picker is not None else -1
        keys = tuple(DAILY_CATALOG)
        self.selected_daily_policy_kind.set('daily.' + keys[index] if 0 <= index < len(keys) else '')
        self._sync_resource_policy_controls()

    def _open_daily_resource_policy(self):
        self._open_resource_policy(self.selected_daily_policy_kind.get())

    def _bind_option_wheel(self, widget):
        widget.bind('<MouseWheel>', self._scroll_options)
        for child in widget.winfo_children():
            self._bind_option_wheel(child)

    def _scroll_options(self, event):
        return self._scroll_canvas(event, self.option_canvas)

    def _read_elite_choices(self):
        if self.app.account_plan.active:
            self._message('本轮任务运行中，暂不能读取其他选择。', error=True)
            return
        state = self.app.objects.current_elite_choices()
        if not state or state['phase'] != 'CHALLENGE_LIST' or state['gate'] != 'PASS':
            self._message('请在当前游戏打开精英关卡列表并连接接口后读取。', error=True)
            return
        identity = state['identity']
        expected = (self.identity_vars['user_id'].get().strip(), self.identity_vars['zone_id'].get().strip(),
                    self.identity_vars['role_id'].get().strip())
        if not all(expected) or expected != (identity['user_id'], identity['zone_id'], identity['character_id']):
            self._message('游戏角色与当前账号配置不一致，请先核对身份。', error=True)
            return
        self._elite_choices = {f"关卡 {row['campaign_id']} · {row['star']} 星": row for row in state['choices']}
        self.elite_choice_box.configure(values=tuple(self._elite_choices))
        self.elite_choice.set('')
        self._message('请选择当前游戏可进入的关卡和星级。' if self._elite_choices else '当前列表没有可进入的精英关卡。')

    def _select_elite_choice(self, _event=None):
        row = self._elite_choices.get(self.elite_choice.get())
        if row:
            self.option_vars['elite']['campaign_id'].set(str(row['campaign_id']))
            self.option_vars['elite']['star'].set(str(row['star']))

    def _option(self, task, name, title, kind):
        row = tk.Frame(self.option_frame, name='option_' + name, bg=PANEL_ALT)
        row.pack(fill='x', padx=10, pady=4)
        variable = self.option_vars[task][name]
        if kind == 'bool':
            tk.Checkbutton(row, name='value', text=title, variable=variable, bg=PANEL_ALT, fg=TEXT,
                           activebackground=PANEL_ALT, activeforeground=TEXT, selectcolor=PANEL).pack(anchor='w')
            return
        tk.Label(row, text=title, bg=PANEL_ALT, fg=MUTED, anchor='w').pack(anchor='w')
        if kind == 'mode':
            ttk.Combobox(row, name='value', textvariable=variable, values=('强化', '升阶'),
                         state='readonly', width=18).pack(anchor='w')
        else:
            tk.Entry(row, name='value', textvariable=variable, width=18, bg=PANEL, fg=TEXT,
                     insertbackground=TEXT, relief='flat').pack(anchor='w', ipady=3)

    def _daily_options(self):
        policy_actions = tk.Frame(self.option_frame, name='daily_resource_policy_actions', bg=PANEL_ALT)
        policy_actions.pack(fill='x', padx=12, pady=(0, 12))
        keys = tuple(DAILY_CATALOG)
        selected = self.selected_daily_policy_kind.get()
        selected_key = selected[6:] if selected.startswith('daily.') else ''
        if selected_key not in DAILY_CATALOG:
            selected_key = next((key for key in keys if self.daily_vars[key].get()), keys[0])
        self.daily_resource_policy_button = tk.Button(policy_actions, name='open_daily_resource_policy',
            text='资源策略', command=self._open_daily_resource_policy,
            bg=PANEL, fg=TEXT, disabledforeground=MUTED, relief='flat', padx=12, pady=6)
        self.daily_resource_policy_button.pack(side='right', padx=(8, 0))
        self.daily_policy_picker = ttk.Combobox(policy_actions, name='daily_resource_policy_kind',
            textvariable=self.daily_policy_choice, values=tuple(DAILY_CATALOG.values()),
            state='readonly', width=12)
        self.daily_policy_picker.pack(side='left', fill='x', expand=True)
        self.daily_policy_picker.current(keys.index(selected_key))
        self.daily_policy_picker.bind('<<ComboboxSelected>>', self._select_daily_resource_policy)
        self._select_daily_resource_policy()
        actions = tk.Frame(self.option_frame, name='daily_selection_actions', bg=PANEL_ALT)
        actions.pack(fill='x', padx=12, pady=(0, 14))
        tk.Button(actions, name='select_all', text='全选', command=lambda: self._set_daily_items(True),
                  bg=PANEL, fg=TEXT, relief='flat').pack(side='left', padx=(0, 8))
        tk.Button(actions, name='clear_all', text='清空', command=lambda: self._set_daily_items(False),
                  bg=PANEL, fg=TEXT, relief='flat').pack(side='left')
        tk.Button(actions, name='tribute_options', text='上贡选项', command=self._show_tribute_options,
                  bg=PANEL, fg=TEXT, relief='flat').pack(side='right')
        notes = {
            'sign': DAILY_ITEM_NOTES['sign'],
            'events': '仅免费奖励',
            'mingshi': '需启用击败天神',
            'tribute': '使用本账号的上贡目标和次数上限',
        }
        self._daily_groups = []
        for group_index, (title, keys) in enumerate(DAILY_GROUPS):
            panel = tk.Frame(self.option_frame, name='daily_group_' + str(group_index), bg=PANEL)
            panel.pack(fill='x', padx=12, pady=(0, 16))
            tk.Label(panel, text=title, bg=PANEL, fg=TEXT, anchor='w',
                     font=('Microsoft YaHei UI', 10, 'bold')).pack(fill='x', padx=14, pady=(12, 8))
            items = tk.Frame(panel, name='items', bg=PANEL)
            items.pack(fill='x', padx=8, pady=(0, 10))
            cells = []
            for key in keys:
                cell = tk.Frame(items, name='item_' + key, bg=PANEL)
                checkbox = tk.Checkbutton(cell, name='daily_item_' + key, text=DAILY_CATALOG[key],
                    variable=self.daily_vars[key], bg=PANEL, fg=TEXT,
                    activebackground=PANEL, activeforeground=TEXT, selectcolor=PANEL_ALT, anchor='w')
                if key == 'guild_donation':
                    checkbox.pack(side='left', padx=4, pady=5)
                    row = tk.Frame(cell, name='option_donation_type', bg=PANEL)
                    row.pack(side='left')
                    tk.Label(row, text='档位', bg=PANEL, fg=MUTED).pack(side='left', padx=(0, 6))
                    donations = {'1000 钱币': '1', '10 万钱币': '2', '80 元宝': '3'}
                    donation_display = tk.StringVar(self.app, value=next(
                        title for title, value in donations.items()
                        if value == self.option_vars['daily']['donation_type'].get()))
                    donation_picker = ttk.Combobox(row, name='value', textvariable=donation_display,
                                                   values=tuple(donations), state='readonly', width=14)
                    donation_picker.pack(side='left')
                    donation_picker.bind('<<ComboboxSelected>>', lambda _event: self.option_vars['daily']['donation_type'].set(
                        donations[donation_display.get()]))
                elif key == 'city_shop':
                    checkbox.pack(side='left', padx=4, pady=5)
                    row = tk.Frame(cell, name='option_shop_duration', bg=PANEL)
                    row.pack(side='left')
                    tk.Label(row, text='生产', bg=PANEL, fg=MUTED).pack(side='left', padx=(0, 6))
                    choices = {title: str(seconds) for seconds, title in SHOP_DURATIONS.items()}
                    display = tk.StringVar(self.app, value=next(
                        title for title, seconds in choices.items()
                        if seconds == self.option_vars['daily']['shop_duration'].get()))
                    picker = ttk.Combobox(row, name='value', textvariable=display,
                                          values=tuple(choices), state='readonly', width=16)
                    picker.pack(side='left')
                    picker.bind('<<ComboboxSelected>>', lambda _event: self.option_vars['daily']['shop_duration'].set(
                        choices[display.get()]))
                else:
                    checkbox.pack(anchor='w', fill='x', padx=4, pady=(5, 5))
                if key in notes:
                    note = tk.Label(cell, name='dependency_note', text=notes[key], bg=PANEL, fg=MUTED,
                                    justify='left', anchor='w', wraplength=240, font=('Microsoft YaHei UI', 9))
                    note.pack(fill='x', padx=(28, 8), pady=(0, 5))
                cells.append(cell)
            self._daily_groups.append((items, cells))
        self._layout_daily_items(self.option_canvas.winfo_width())

    def _layout_daily_items(self, width):
        columns = 2 if width >= 420 else 1
        for panel, cells in self._daily_groups:
            for column in range(2):
                panel.grid_columnconfigure(column, weight=1 if column < columns else 0,
                                            uniform='daily_group' if column < columns else '')
            position = 0
            for cell in cells:
                span = columns if cell.winfo_name() in ('item_guild_donation', 'item_city_shop') else 1
                if span == columns and position % columns:
                    position += columns - position % columns
                cell.grid(row=position // columns, column=position % columns, columnspan=span,
                          sticky='new', padx=4, pady=3)
                position += span
                if 'dependency_note' in cell.children:
                    cell.children['dependency_note'].configure(wraplength=max(140, (width - 100) // columns))

    def _set_daily_items(self, enabled):
        for variable in self.daily_vars.values():
            variable.set(enabled)

    def _show_tribute_options(self):
        self.task_list.selection_clear(0, 'end')
        self.task_list.selection_set(_VISIBLE_TASKS.index('tribute'))
        self._select_task()
        self.option_canvas.yview_moveto(0)

    def _capture(self):
        if self.profile is None:
            return True
        profile = copy.deepcopy(self.profile)
        for key, _label in _IDENTITY_FIELDS:
            if key != 'password':
                value = self.identity_vars[key].get()
                if value != profile[key]:
                    profile[key] = value.strip()
        profile['server_tab'] = _SERVER_TABS.get(self.identity_vars['server_tab'].get(), '')
        password = self.identity_vars['password'].get()
        if self.clear_password.get():
            profile['password'] = ''
        elif password:
            profile['password'] = password
        try:
            for key, task in profile['tasks'].items():
                task['enabled'] = bool(self.enabled_vars[key].get())
                for name, variable in self.option_vars[key].items():
                    old = task['options'][name]
                    raw = variable.get()
                    if type(old) is bool:
                        task['options'][name] = bool(raw)
                    elif name == 'mode':
                        task['options'][name] = {'强化': 'enchant', '升阶': 'evolve'}.get(raw, raw)
                    elif name == 'max_practice' and not str(raw).strip():
                        task['options'][name] = None
                    else:
                        task['options'][name] = int(str(raw).strip())
            profile['tasks']['daily']['options']['items'] = {key: bool(var.get()) for key, var in self.daily_vars.items()}
            candidate = copy.deepcopy(self.document)
            candidate['profiles'] = [profile if p['id'] == profile['id'] else p for p in candidate['profiles']]
            self.document = self.store.validate_document(candidate)
        except (ValueError, TypeError, AccountProfileError):
            self._message('账号或任务选项无效，请检查数字和选择。', error=True)
            return False
        self.profile = profile
        return True

    def add(self):
        if self.error or not self.flush_pending_options() or not self._capture():
            return
        profile = new_profile()
        self.document['profiles'].append(profile)
        self.current_id = profile['id']
        self._refresh_accounts()
        self._load_profile(profile)

    def save(self):
        if self.error or self.profile is None or not self._capture():
            return False
        if not self._persist_document():
            return False
        self._options_dirty = False
        if self._selection_save_pending is not None:
            self.app.after_cancel(self._selection_save_pending)
            self._selection_save_pending = None
        self.identity_vars['password'].set('')
        self.clear_password.set(False)
        self._refresh_accounts()
        self._message('已保存账号及任务选择。')
        return True

    def use_current_identity(self):
        if self.app.account_plan.active:
            self._message('本轮任务运行中，不能更换或读取绑定身份。', error=True)
            return
        identity = self.app.objects.current_identity()
        if not identity or not self.app.bound:
            if (self.app.bound and not self._identity_pending and not self.app.runner.is_active
                    and not self.app.objects.is_busy and self.app.objects.connect()):
                self._identity_pending = True
                profile_id = self.current_id
                self._message('正在读取当前游戏身份。')
                self.app.after(200, lambda: self._finish_identity(profile_id, time.monotonic() + 30))
            else:
                self._message('当前游戏身份尚未读取，请连接游戏后再使用。', error=True)
            return
        self._apply_identity(identity)

    def _finish_identity(self, profile_id, deadline):
        if self.app.closing or profile_id != self.current_id:
            self._identity_pending = False
            return
        identity = self.app.objects.current_identity()
        if identity:
            self._identity_pending = False
            self._apply_identity(identity)
        elif time.monotonic() >= deadline or self.app.objects.connection_status['state'] in ('error', 'unknown'):
            self._identity_pending = False
            self._message('无法确认当前游戏身份。', error=True)
        else:
            self.app.after(200, lambda: self._finish_identity(profile_id, deadline))

    def _apply_identity(self, identity):
        for field in ('user_id', 'zone_id', 'character_id'):
            self.identity_vars['role_id' if field == 'character_id' else field].set(identity[field])
        if not self.identity_vars['server_name'].get().strip():
            self.identity_vars['server_name'].set(self.app.bound.title.rsplit('-', 1)[-1])
        self._message('已读取当前角色身份；请核对账号和区服后保存。')

    def start(self):
        if not self.save():
            return
        accepted, reason = self.app.account_plan.start(self.document)
        if not accepted:
            self._message(reason, error=True)
        else:
            self.sections.select(self.record_section)
            self._message('')

    def pause(self):
        plan = self.app.account_plan
        plan.resume() if plan.paused else plan.pause()

    def confirm_login(self):
        if not self.app.account_plan.confirm_human_action():
            self._message('当前没有等待确认的登录操作。')

    def stop(self):
        self.app.stop()

    def show_recent_records(self):
        """Display saved outcomes without restoring a plan or replaying actions."""
        if self.app.account_plan.active:
            return
        try:
            raw = (Path(self.app.data_dir) / 'account-plan-last.json').read_bytes()
            if len(raw) > 8 * 1024 * 1024:
                raise ValueError('record size')
            snapshot = json.loads(raw.decode('utf-8-sig'))
            accounts = snapshot.get('accounts') if isinstance(snapshot, dict) else None
            if (not isinstance(accounts, list) or not all(isinstance(account, dict)
                    and isinstance(account.get('id'), str) and isinstance(account.get('tasks'), list)
                    and all(isinstance(task, dict) for task in account['tasks']) for account in accounts)):
                raise ValueError('record shape')
        except FileNotFoundError:
            self._message('尚无已保存的执行记录。')
            return
        except PermissionError:
            self._message('没有读取执行记录的权限，请检查助手数据目录的访问权限。', error=True)
            return
        except (OSError, UnicodeError, ValueError):
            self._message('已保存的执行记录无法读取，当前任务和账号配置未被修改。', error=True)
            return
        # Historical activity is never current execution authority. Only the
        # record pane receives these facts; the live coordinator stays idle.
        self._render_records({**snapshot, 'active': False, 'paused': False})
        self._message('')

    def update_status(self, snapshot):
        self._sync_resource_policy_controls()
        active = snapshot.get('active') is True
        self.recent_records_button.configure(state='disabled' if active or self.app.closing else 'normal')
        self.start_button.configure(state='disabled' if active or self.app.closing else 'normal')
        status = snapshot.get('status', 'IDLE')
        plan_state = ('等待处理' if active and snapshot.get('human_required') else
                      '已暂停' if snapshot.get('paused') else
                      _display_state(status))
        accounts = snapshot.get('accounts', [])
        selected_count = len(accounts) if active else len(self.document['selected_ids'])
        selection_state = 'disabled' if active or self.error or self.app.closing else 'normal'
        for checkbox in self.account_checkbuttons.values():
            checkbox.configure(state=selection_state)
        current = next((account for account in accounts if account.get('id') == snapshot.get('current_account')), None)
        reason = ((current or {}).get('reason') or snapshot.get('reason') or '')
        if reason == '上次运行记录':
            reason = ''
        detail = _display_reason(reason)
        current_label = (current or {}).get('label') or '未命名账号'
        self.plan_status.configure(text=f'本轮{plan_state} · {selected_count} 个账号'
                                   + (f' · {current_label}' if active and current else '')
                                   + (f'：{detail}' if detail else ''))
        self.pause_button.configure(state='normal' if active else 'disabled',
                                    text='继续' if snapshot.get('paused') else '暂停')
        code = snapshot.get('human_code', '')
        if code in ('SHARED_LOGIN', 'CAPTCHA', 'HUMAN_LOGIN', 'LOGIN_UNCERTAIN', 'FORM_UNSUPPORTED'):
            confirm_text = '已完成登录'
        elif code in ('ORIGIN_UNVERIFIED', 'UNEXPECTED_GAME', 'PATTERN_UNSUPPORTED',
                      'AUTOMATION_UNAVAILABLE', 'HELPER_TIMEOUT', 'HELPER_PROTOCOL'):
            confirm_text = '已确认登录和服务器'
        elif code == 'SERVER_UNCERTAIN':
            confirm_text = '已确认服务器'
        else:
            confirm_text = '人工操作已完成'
        self.human_button.configure(state='normal' if active and snapshot.get('human_required') else 'disabled',
                                    text=confirm_text)
        if active and snapshot.get('human_required'):
            self.human_button.grid()
        else:
            self.human_button.grid_remove()
        self.stop_button.configure(state='normal' if active else 'disabled')
        self._render_records(snapshot)

    @staticmethod
    def _record_state_tag(status):
        if status in ('COMPLETED', 'ALREADY_COMPLETE', 'ALREADY_CLAIMED'):
            return 'success'
        if status in ('FAILED', 'BLOCKED', 'UNKNOWN'):
            return 'error'
        if status in ('WAITING', 'PAUSED', 'PARTIAL', 'PROCESSED', 'UNAVAILABLE', 'NOT_OPEN',
                      'CONDITION_NOT_MET', 'STOPPED', 'STOPPING'):
            return 'attention'
        if status in ('RUNNING', 'DISPATCHING', 'RECOVERING'):
            return 'active'
        return 'muted'

    @staticmethod
    def _record_time(value):
        # These are route check / resume times, never task completion times.
        if type(value) not in (int, float) or value <= 0:
            return ''
        try:
            return time.strftime('%m-%d %H:%M:%S', time.localtime(value))
        except (OverflowError, OSError, ValueError):
            return ''

    def _record_segments(self, account):
        segments = []
        reason = _display_reason(account.get('reason'))
        if reason:
            segments.append((reason + '\n', ('account_detail',)))
        tasks = [task for task in account.get('tasks', []) if task.get('status') != 'SKIPPED']
        for task in tasks:
            status = task.get('status', 'IDLE')
            title = TASK_CATALOG.get(task.get('key')) or _display_record_name(task.get('label'), '任务')
            segments.append((title + '    ', ('task',)))
            segments.append((_display_state(status) + '\n',
                             ('task', 'state', self._record_state_tag(status))))
            reason_code = task.get('reason')
            if (task.get('key') == 'daily' and reason_code == 'daily_items_observed_complete'
                    and status not in ('COMPLETED', 'ALREADY_COMPLETE', 'DONE')):
                # Older rounds may retain the last successful subset's
                # reason. Do not present it as completion of the whole plan.
                reason_code = {
                    'UNKNOWN': 'daily_workflows_unknown', 'PARTIAL': 'daily_workflows_partial',
                    'WAITING': 'daily_cooldown_deferred', 'RUNNING': 'daily_workflows_running',
                }.get(status, '')
            reason = _display_reason(reason_code)
            if reason:
                segments.append((reason + '\n', ('detail',)))
            progress = task.get('progress') or {}
            progress_text = ''
            if task.get('key') == 'elite' and progress.get('completed') is not None:
                progress_text = (f"今日 {_display_number(progress['completed'])}/{_display_number(progress.get('target'))}，"
                                 f"剩余 {_display_number(progress.get('remaining'))}")
            elif progress.get('stage'):
                progress_text = '当前关卡 ' + _display_progress(progress['stage'])
            elif progress.get('progress'):
                progress_text = _display_progress(progress['progress'])
            if progress_text:
                segments.append((progress_text + '\n', ('detail',)))
            for key, route in task.get('routes', {}).items():
                if route.get('status') != 'WAITING':
                    continue
                ready = self._record_time(route.get('ready_at'))
                timing = []
                if ready:
                    timing.append('预计可继续 ' + ready)
                if timing:
                    if route.get('requires_online') and route.get('status') == 'WAITING':
                        timing.append('需保持在线')
                    title = _display_record_name(DAILY_CATALOG.get(key), '日常项目')
                    segments.append((title + '：' + '，'.join(timing) + '\n', ('time',)))
            items = task.get('items', [])
            names = {}
            for item in items:
                name = _display_item_name(item)
                names[name] = names.get(name, 0) + 1
            for item in items:
                status = item.get('status', '')
                item_status = _display_state(status)
                if item.get('page_confirmed') is True and status == 'COMPLETED':
                    item_status = '页面已确认完成'
                elif item.get('task_id') == 301 and status in ('COMPLETED', 'ALREADY_CLAIMED'):
                    item_status = '本次已签到' if status == 'COMPLETED' else '今日已签到'
                elif item.get('kind') == 'sign_page_reward' and status == 'COMPLETED':
                    item_status = '已领取'
                name = _display_item_name(item)
                if names.get(name, 0) > 1:
                    kind = {'task': '任务进度', 'activity': '活动进度',
                            'reward_activity': '奖励进度', 'reward_detail': '领取结果',
                            'sign_page_reward': '领奖'}.get(item.get('kind'))
                    if kind:
                        name += f'（{kind}）'
                segments.append((name + '    ', ('item',)))
                segments.append((item_status, ('item', 'state', self._record_state_tag(status))))
                progress_text = (f"    {_display_number(item['current'])}/{_display_number(item['required'])}"
                                 if item.get('current') is not None and item.get('required') is not None else '')
                segments.append((progress_text + '\n', ('item',)))
                detail = _display_reason(item.get('reason'))
                if item.get('kind') == 'sign_page_reward' and status in (
                        'COMPLETED', 'ALREADY_CLAIMED', 'NO_FREE_REWARD', 'CONDITION_NOT_MET'):
                    detail = ''
                elif item.get('task_id') == 301 and item.get('reason') in (
                        'sign_salary_current_day_already_signed', 'sign_salary_checkin_state_confirmed'):
                    detail = ''
                if detail:
                    segments.append((detail + '\n', ('detail',)))
        if not tasks:
            segments.append(('暂无已启用任务的执行记录。', ('muted',)))
        return segments

    def _render_records(self, snapshot=None):
        if snapshot is not None:
            self._record_snapshot = snapshot
        snapshot = self._record_snapshot
        accounts = snapshot.get('accounts', [])
        # Editing selection and batch checkboxes have different jobs. Only an
        # exact record id establishes ownership; labels and global logs do not.
        account = next((record for record in accounts
                        if self.current_id and record.get('id') == self.current_id), None)
        summary = '本轮任务状态' if snapshot.get('active') else '最近一次任务状态' if accounts else '执行记录'
        color = MUTED
        if not self.current_id:
            segments = [('请选择左侧账号。', ('muted',))]
        elif account is None:
            message = '本轮暂无该账号的执行记录。' if snapshot.get('active') else '尚无该账号的执行记录。'
            segments = [(message, ('muted',))]
        else:
            status = account.get('status', 'IDLE')
            if (snapshot.get('active') and snapshot.get('paused')
                    and snapshot.get('current_account') == self.current_id
                    and status in ('RUNNING', 'WAITING')):
                status = 'PAUSED'
            summary += ' · ' + _display_state(status)
            color = {'success': GREEN, 'attention': GOLD, 'error': RED,
                     'active': ACCENT, 'muted': MUTED}[self._record_state_tag(status)]
            segments = self._record_segments(account)
        source = (self.current_id, snapshot.get('run_id', ''))
        rendered = (source, summary, tuple(segments))
        if rendered == self._record_rendered:
            return
        position = self.results.yview()[0] if source == self._record_source else 0.0
        self.record_summary.configure(text=summary, fg=color)
        self.results.configure(state='normal')
        self.results.delete('1.0', 'end')
        for text, tags in segments:
            self.results.insert('end', text, tags)
        self.results.configure(state='disabled')
        self.results.yview_moveto(position)
        self._record_rendered = rendered
        self._record_source = source
