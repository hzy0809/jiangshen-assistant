"""Tk desktop shell. Game-specific workflows belong in separate task modules."""
from __future__ import annotations

import argparse
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
import time
import tkinter as tk
from tkinter import ttk
from typing import Any

from .control_server import ControlServer
from .core import RunnerState, RunnerGroup
from .application_runtime import ApplicationRuntime
from .play_runtime import PlayWorkflowRuntime
from .workflow_page import WorkflowPage
from .resource_policy_view import ResourcePolicyEditor
from .campaign_task import CampaignCoordinator
from .challenge_task import ChallengeCoordinator
from .elite_task import EliteCoordinator
from .account_page import AccountPage, DAILY_BLOCKED, DAILY_ITEM_NOTES
from .account_profiles import DAILY_CATALOG, DAILY_GROUPS, SHOP_DURATIONS
from .account_plan import AccountPlanCoordinator
from .development_task import DevelopmentCoordinator
from .daily_coordinator import DailyCoordinator
from .soul_coordinator import SoulCoordinator
from .development import EquipmentTarget, TributeTarget, StarTarget
from .objects import ObjectController
from .mono_runtime import identity_for
from .settings import AppSettings, SettingsStore
from .theme import ACCENT, ACCENT_HOVER, BG, GOLD, GREEN, LINE, MUTED, PANEL, PANEL_ALT, RAIL, RED, TEXT
from .windows import WindowError, WindowInfo, WindowsAdapter
from .workflow_events import readable_message, readable_reason

ROOT = Path(__file__).resolve().parent.parent
DAILY_ACTIONS_READY = True


class AssistantApp(tk.Tk):
    def __init__(self, data_dir: Path = ROOT / "data") -> None:
        adapter = WindowsAdapter()
        adapter.enable_dpi_awareness()
        super().__init__()
        self.title("将神助手")
        self.geometry("1180x860")
        self.minsize(1060, 820)
        self.configure(bg=BG)
        self.option_add("*Font", ("Microsoft YaHei UI", 10))
        self.adapter = adapter
        self.runner = RunnerGroup()
        self.store = SettingsStore(data_dir)
        self.settings = self.store.load()
        self.data_dir = data_dir
        self.control_server = ControlServer(data_dir)
        self.objects = ObjectController(ROOT, data_dir)
        self.campaign = CampaignCoordinator(self.objects, self.runner.create('campaign'), data_dir)
        self.challenge = ChallengeCoordinator(self.objects, self.runner.create('challenge'), data_dir)
        self.elite = EliteCoordinator(self.objects, self.runner.create('elite'), data_dir)
        self.development = DevelopmentCoordinator(self.objects, self.runner.create('development'), data_dir)
        self.daily = DailyCoordinator(self.objects, self.runner.create('daily'), data_dir)
        self.soul = SoulCoordinator(self.objects, self.runner.create('soul'), data_dir)
        self.account_plan = AccountPlanCoordinator(self)
        self.play_runtime = PlayWorkflowRuntime(self, {
            name: getattr(self, name) for name in
            ('campaign', 'challenge', 'elite', 'development', 'daily', 'soul')})
        for name, coordinator in self.play_runtime.proxies.items():
            setattr(self, name, coordinator)
        self._objects_close_requested = False
        self.object_snapshot: dict[str, Any] | None = None
        self.object_seen_monotonic = 0.0
        self.object_connection: dict[str, Any] = {"state": "disconnected", "can_verify": False}
        self.windows: list[WindowInfo] = []
        self.bound: WindowInfo | None = None
        self.closing = False
        self.global_stop_available = False
        self.log_lines: list[tuple[str, str]] = []
        self._visible_log_states: dict[tuple, str] = {}
        self.nav_buttons: dict[str, tk.Button] = {}
        self.pages: dict[str, tk.Frame] = {}
        self.page_titles: dict[str, str] = {}
        self.current_page = "battle"
        self.selected_page = tk.StringVar(self, name="selected_page", value=self.current_page)
        self._configure_logging()
        self._configure_styles()
        self._build_shell()
        self._build_diagnostics()
        self._build_campaign()
        self._build_challenge()
        self._build_daily()
        self._build_soul()
        self.account_page = AccountPage(self)
        self._build_development()
        self._build_logs()
        self._build_settings()
        self.workflow_page = WorkflowPage(self)
        self.show_page("battle")
        self.protocol("WM_DELETE_WINDOW", self.close)
        self.bind("<Escape>", lambda _: self.stop())
        self.bind("<F8>", lambda _: None if self.global_stop_available else self.stop())
        self.event_runtime = ApplicationRuntime(self)
        self._events_running = True
        self.after(0, self._poll)
        self.log("助手已启动")
        if self.store.last_warning:
            self.log(self.store.last_warning, "WARNING")
        self._start_stop_hotkey()
        if not self.control_server.start():
            self.log(f"命令行控制未启动：{self.control_server.status['error']}", "WARNING")

    def _start_stop_hotkey(self) -> None:
        try:
            self.adapter.start_stop_hotkey()
        except WindowError as exc:
            self._stop_hotkey_failed(str(exc))
            return
        self.global_stop_available = True
        self.stop_key_label.configure(text="F8 停止", fg=GOLD)
        self.stop_key_settings.configure(text="停止快捷键：F8（全局） · Esc（助手窗口内）", fg=GOLD)

    def _stop_hotkey_failed(self, reason: str) -> None:
        self.global_stop_available = False
        self.stop_key_label.configure(text="F8 不可用", fg=RED)
        self.stop_key_settings.configure(text="全局 F8 不可用；请用停止按钮或助手内 Esc。", fg=RED)
        self.log(f"全局 F8 停止快捷键不可用：{reason}。请使用停止按钮或助手内 Esc。", "WARNING")

    def _configure_logging(self) -> None:
        self.logger = logging.getLogger(f"jiangshen.{id(self)}")
        self.logger.setLevel(logging.INFO)
        self.logger.propagate = False
        try:
            log_dir = self.data_dir / "logs"
            log_dir.mkdir(parents=True, exist_ok=True)
            handler = RotatingFileHandler(log_dir / "assistant.log", maxBytes=1_000_000, backupCount=3, encoding="utf-8")
            handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s", "%Y-%m-%d %H:%M:%S"))
            self.logger.addHandler(handler)
        except OSError as exc:
            self.logger.addHandler(logging.NullHandler())
            self.after(600, lambda error=str(exc): self.log(f"日志无法写入磁盘：{error}", "WARNING"))

    def _configure_styles(self) -> None:
        style = ttk.Style(self)
        style.theme_use("clam")
        style.configure("TCombobox", fieldbackground=PANEL_ALT, background=PANEL_ALT, foreground=TEXT, bordercolor=LINE, arrowcolor=ACCENT, padding=7)
        style.map("TCombobox", fieldbackground=[("readonly", PANEL_ALT)], foreground=[("readonly", TEXT)], selectbackground=[("readonly", PANEL_ALT)], selectforeground=[("readonly", TEXT)])
        style.configure("TSpinbox", fieldbackground=PANEL_ALT, background=PANEL_ALT, foreground=TEXT,
                        bordercolor=LINE, arrowcolor=ACCENT, padding=7)
        style.configure("TProgressbar", troughcolor=BG, background=ACCENT, bordercolor=BG, lightcolor=ACCENT, darkcolor=ACCENT)
        for name in ('Vertical.TScrollbar', 'Horizontal.TScrollbar'):
            style.configure(name, background=LINE, troughcolor=PANEL, bordercolor=PANEL,
                            arrowcolor=MUTED, lightcolor=LINE, darkcolor=LINE, relief='flat')
            style.map(name, background=[('active', PANEL_ALT)])
        self.option_add("*TCombobox*Listbox.background", PANEL_ALT)
        self.option_add("*TCombobox*Listbox.foreground", TEXT)
        self.option_add("*TCombobox*Listbox.selectBackground", LINE)

    def label(self, parent: tk.Misc, text: str = "", *, size: int = 10, color: str = TEXT, bold: bool = False, **kwargs: Any) -> tk.Label:
        return tk.Label(parent, text=text, font=("Microsoft YaHei UI", size, "bold" if bold else "normal"), bg=parent.cget("bg"), fg=color, **kwargs)

    def button(self, parent: tk.Misc, text: str, command: Any, *, primary: bool = False, danger: bool = False, **kwargs: Any) -> tk.Button:
        return tk.Button(parent, text=text, command=command, bg=ACCENT if primary else PANEL_ALT,
                         fg=BG if primary else (RED if danger else TEXT),
                         activebackground=ACCENT_HOVER if primary else LINE,
                         activeforeground=BG if primary else TEXT,
                         disabledforeground='#5d7d6e' if primary else '#7d9991',
                         highlightbackground=parent.cget('bg'), highlightcolor=ACCENT, highlightthickness=1,
                         relief="flat", bd=0, padx=16, pady=9, cursor="hand2",
                         font=("Microsoft YaHei UI", 10, "bold" if primary else "normal"), **kwargs)

    def card(self, parent: tk.Misc, **kwargs: Any) -> tk.Frame:
        return tk.Frame(parent, bg=PANEL, highlightbackground=LINE, highlightthickness=1, **kwargs)

    def _build_shell(self) -> None:
        sidebar = tk.Frame(self, name="navigation", width=160, bg=RAIL)
        sidebar.pack(side="left", fill="y")
        sidebar.pack_propagate(False)
        brand = tk.Frame(sidebar, bg=RAIL)
        brand.pack(fill="x", padx=22, pady=(20, 16))
        self.label(brand, "将神助手", size=16, color=TEXT, bold=True).pack(anchor="w")
        tk.Frame(sidebar, bg=LINE, height=1).pack(fill="x", padx=20, pady=(0, 10))
        for index, (key, name) in enumerate([("workflows", "运行"), ("accounts", "账号"), ("battle", "征战"), ("challenge", "挑战"), ("development", "培养"), ("daily", "日常"), ("soul", "聚魂"), ("diagnostics", "诊断"), ("logs", "日志"), ("settings", "设置")], start=1):
            if key == "diagnostics":
                tk.Frame(sidebar, bg=LINE, height=1).pack(fill="x", padx=20, pady=12)
            button = self.button(sidebar, name, lambda k=key: self.show_page(k), name=f"navigate_{key}", anchor="w")
            button.configure(bg=RAIL, padx=22, pady=8)
            button.pack(fill="x", padx=12, pady=2)
            self.nav_buttons[key] = button
            shortcut = {'workflows': 'r', 'accounts': 0, 'battle': 1, 'challenge': 2, 'development': 3,
                        'diagnostics': 5, 'logs': 6, 'settings': 7, 'daily': 8, 'soul': 9}[key]
            self.bind(f"<Control-Key-{shortcut}>", lambda _, k=key: self.show_page(k))
        bottom = tk.Frame(sidebar, bg=RAIL)
        bottom.pack(side="bottom", fill="x", padx=22, pady=12)
        self.stop_key_label = self.label(bottom, "F8 准备中", color=MUTED, bold=True)
        self.stop_key_label.pack(anchor="w")
        body = tk.Frame(self, bg=BG)
        body.pack(side="left", fill="both", expand=True)
        top = tk.Frame(body, bg=BG)
        top.pack(fill="x", padx=28, pady=(18, 16))
        self.page_heading = self.label(top, '', name='page_title', size=19, bold=True, anchor='w')
        self.page_heading.pack(side='left', fill='x', expand=True)
        self.connection_badge = self.label(top, "●  正在查找游戏", name="connection_state", color=MUTED)
        self.connection_badge.pack(side="right")
        self.window_combo = ttk.Combobox(top, name="game_window", width=32, state="readonly")
        self.window_combo.bind("<<ComboboxSelected>>", self.choose_game_window)
        execution = tk.Frame(body, name='workflow_bar', bg=BG)
        execution.pack(fill='x', padx=28, pady=(0, 12))
        self.workflow_scene_status = self.label(execution, '场景待更新', name='workflow_scene_status', color=MUTED,
                                                anchor='w', wraplength=520)
        self.workflow_scene_status.pack(side='left', fill='x', expand=True)
        self.workflow_placement = tk.StringVar(self, name='workflow_placement', value='本次操作后切换')
        self.workflow_placement_combo = ttk.Combobox(execution, name='workflow_placement', width=17,
            textvariable=self.workflow_placement, values=('本次操作后切换', '加入队列'), state='readonly')
        self.workflow_placement_combo.pack(side='right')
        self.content = tk.Frame(body, name="pages", bg=BG)
        footer = tk.Frame(body, bg=BG)
        footer.pack(side="bottom", fill="x", padx=28, pady=(12, 15))
        self.footer_status = self.label(footer, "就绪", name="latest_status", size=9, color=MUTED)
        self.footer_status.pack(side="left")
        self.content.pack(fill="both", expand=True, padx=28)
        self.content.grid_rowconfigure(0, weight=1)
        self.content.grid_columnconfigure(0, weight=1)

    def page(self, key: str, title: str) -> tk.Frame:
        page = tk.Frame(self.content, name=key, bg=BG)
        page.grid(row=0, column=0, sticky="nsew")
        self.pages[key] = page
        self.page_titles[key] = title
        return page

    def _build_diagnostics(self) -> None:
        page = self.page("diagnostics", "诊断")
        run = self.card(page)
        run.pack(fill="x")
        run_inner = tk.Frame(run, bg=PANEL)
        run_inner.pack(fill="both", expand=True, padx=22, pady=22)
        heading = tk.Frame(run_inner, bg=PANEL)
        heading.pack(fill="x")
        self.label(heading, "窗口与调度自检", size=13, bold=True).pack(side="left")
        self.run_state_label = self.label(heading, "●  待机", name="diagnostic_state", color=GREEN)
        self.run_state_label.pack(side="right")
        self.progress = ttk.Progressbar(run_inner, name="diagnostic_progress", mode="indeterminate")
        self.progress.pack(fill="x", pady=(24, 24))
        run_controls = tk.Frame(run_inner, bg=PANEL)
        run_controls.pack(fill="x")
        self.start_button = self.button(run_controls, "只读自检", self.start_check, name="start_diagnostic", primary=True, state="disabled")
        self.start_button.pack(side="left", padx=(0, 10))
        self.pause_button = self.button(run_controls, "暂停", self.toggle_pause, name="pause_diagnostic", state="disabled")
        self.pause_button.pack(side="left")
        self.stop_button = self.button(run_controls, "停止 · F8", self.stop, name="stop_diagnostic", danger=True, state="disabled")
        self.stop_button.pack(side="right")

    def render_auto_connection(self, snapshot: dict) -> None:
        stage = snapshot.get('stage', 'discovering')
        connected = self.objects.bridge_state == 'ready' and stage != 'blocked'
        label = '游戏已连接' if connected else snapshot.get('label', '正在查找游戏')
        color = GREEN if connected else RED if stage == 'blocked' else MUTED if stage in (
            'waiting_game', 'stopped', 'closed') else GOLD
        self.connection_badge.configure(text='●  ' + label, fg=color)
        show_selector = len(self.windows) > 1 or stage == 'selecting_window'
        if show_selector and not self.window_combo.winfo_manager():
            self.window_combo.pack(side='right', padx=(10, 16))
        elif not show_selector and self.window_combo.winfo_manager():
            self.window_combo.pack_forget()

    def choose_game_window(self, _event=None) -> None:
        index = self.window_combo.current()
        if 0 <= index < len(self.windows):
            if not self.auto_connection.choose(self.windows[index].hwnd):
                self.log('当前任务正在使用游戏，暂不能切换窗口。', 'WARNING')

    def _update_object_controls(self) -> None:
        manager = getattr(self, 'auto_connection', None)
        if manager is not None:
            self.render_auto_connection(manager.snapshot)

    def _build_campaign(self) -> None:
        page = self.page("battle", "征战")
        self.campaign_values = self._battle_status(page, "campaign", "连败 · 3 次停止")
        card = self.card(page, name="campaign_panel")
        card.pack(fill="x")
        inner = tk.Frame(card, bg=PANEL)
        inner.pack(fill="x", padx=20, pady=20)
        self.campaign_start_button, self.campaign_pause_button, self.campaign_stop_button = self._battle_actions(
            inner, "campaign", "开始征战", self.start_campaign, self.toggle_campaign_pause)
        tk.Frame(inner, bg=LINE, height=1).pack(fill="x", pady=18)
        self.campaign_detail = self.label(inner, "请先绑定游戏", name="campaign_detail", color=MUTED, wraplength=790, justify="left", anchor="w")
        self.campaign_detail.pack(fill="x")
        self.campaign_ledger = self.label(inner, "", name="campaign_record", size=9, color=MUTED, wraplength=790, justify="left", anchor="w")
        self.campaign_ledger.pack(fill="x", pady=(12, 0))
        inner.bind("<Configure>", lambda event: [label.configure(wraplength=max(160, event.width))
                   for label in (self.campaign_detail, self.campaign_ledger)])

    def _battle_status(self, page: tk.Frame, prefix: str, loss_title: str) -> dict[str, tk.Label]:
        stats = self.card(page, name=f"{prefix}_status")
        stats.pack(fill="x", pady=(0, 14))
        values = {}
        cells = []
        for index, (key, title, initial) in enumerate([
            ("stage", "当前目标", "等待识别"),
            ("status", "任务状态", "待命"),
            ("losses", loss_title, "0 / 3"),
        ]):
            tile = tk.Frame(stats, name=key, bg=PANEL)
            title_label = self.label(tile, title, size=10, color=MUTED, anchor="w", justify="left")
            title_label.pack(fill="x", pady=(0, 8))
            value = self.label(tile, initial, name="value", size=16, color=GOLD if key == "losses" else TEXT,
                               bold=True, wraplength=245, anchor="w", justify="left")
            value.pack(fill="x")
            values[key] = value
            cells.append((tile, title_label, value))

        def layout_status(event: tk.Event) -> None:
            columns = 3 if event.width >= 600 else 1
            for column in range(3):
                stats.columnconfigure(column, weight=1 if column < columns else 0,
                                      uniform=f"{prefix}_stat" if column < columns else "")
            for index, (tile, title_label, value) in enumerate(cells):
                tile.grid(row=index // columns, column=index % columns, sticky="nsew", padx=20,
                          pady=(18 if index < columns else 0, 18))
                wrap = max(140, event.width // columns - 44)
                title_label.configure(wraplength=wrap)
                value.configure(wraplength=wrap)

        stats.bind("<Configure>", layout_status)
        return values

    def _battle_actions(self, parent: tk.Frame, prefix: str, start_text: str, start: Any, pause: Any) -> tuple[tk.Button, tk.Button, tk.Button]:
        buttons = tk.Frame(parent, name=f"{prefix}_actions", bg=PANEL)
        buttons.pack(fill="x")
        start_button = self.button(buttons, start_text, start, name=f"start_{prefix}", primary=True, state="disabled")
        pause_button = self.button(buttons, "暂停后续发起", pause, name=f"pause_{prefix}", state="disabled")
        stop_button = self.button(buttons, "停止此玩法", lambda: self.stop_play(prefix), name=f"stop_{prefix}", danger=True, state="disabled")
        def layout_actions(event: tk.Event) -> None:
            compact = event.width < 600
            for column in range(3):
                buttons.columnconfigure(column, weight=1 if column < (2 if compact else 3) else 0,
                                        uniform="actions" if column < (2 if compact else 3) else "")
            start_button.grid(row=0, column=0, sticky="ew", padx=(0, 8))
            pause_button.grid(row=0, column=1, sticky="ew", padx=(0, 0 if compact else 8))
            stop_button.grid(row=1 if compact else 0, column=0 if compact else 2,
                             columnspan=2 if compact else 1, sticky="ew", pady=(8 if compact else 0, 0))
            wrap = max(110, event.width // (2 if compact else 3) - 40)
            for button in (start_button, pause_button, stop_button):
                button.configure(wraplength=wrap)

        buttons.bind("<Configure>", layout_actions)
        policy_row = tk.Frame(parent, name=f'{prefix}_policy_actions', bg=PANEL)
        policy_row.pack(fill='x', pady=(10, 0))
        self.button(policy_row, '资源策略', lambda: self.show_resource_policy(prefix),
                    name=f'resource_policy_{prefix}').pack(side='right')
        return start_button, pause_button, stop_button

    def start_campaign(self) -> bool:
        if self.closing or not self.bound:
            return False
        accepted = self.campaign.start()
        if accepted:
            self.log("征战已加入调度，连续失败三次后停止。")
        else:
            self.log("暂不能开始征战，请检查当前任务和游戏连接状态。", "WARNING")
        self._update_campaign_controls()
        self._update_object_controls()
        self._update_run_controls()
        return accepted

    def _build_challenge(self) -> None:
        page = self.page('challenge', '挑战')
        self.challenge_values = self._battle_status(page, 'challenge', '累计失败 · 3 次停止')
        card = self.card(page, name='challenge_panel')
        card.pack(fill='x')
        inner = tk.Frame(card, bg=PANEL)
        inner.pack(fill='x', padx=20, pady=20)
        self.challenge_start_button, self.challenge_pause_button, self.challenge_stop_button = self._battle_actions(
            inner, 'challenge', '开始挑战', self.start_challenge, self.toggle_challenge_pause)
        tk.Frame(inner, bg=LINE, height=1).pack(fill='x', pady=18)
        self.challenge_detail = self.label(inner, '请先绑定游戏', name='challenge_detail', color=MUTED,
                                            wraplength=790, justify='left', anchor='w')
        self.challenge_detail.pack(fill='x')
        inner.bind('<Configure>', lambda event: self.challenge_detail.configure(wraplength=max(160, event.width)))

    def start_challenge(self) -> bool:
        if self.closing or not self.bound:
            return False
        accepted = self.challenge.start()
        if accepted:
            self.log('挑战已加入调度，本轮累计失败三次后停止。')
        else:
            self.log('暂不能开始挑战，请检查当前任务和游戏连接状态。', 'WARNING')
        self._update_challenge_controls()
        self._update_run_controls()
        return accepted

    def _build_daily(self) -> None:
        page = self.page('daily', '日常')
        panel = self.card(page, name='daily_panel')
        panel.pack(fill='both', expand=True)
        inner = tk.Frame(panel, bg=PANEL)
        inner.pack(fill='both', expand=True, padx=23, pady=20)
        self.daily_donation = tk.StringVar(self,
            value={1: '1000 钱币', 2: '10 万钱币', 3: '80 元宝'}[self.settings.daily_donation_type],
            name='daily_donation')
        self.daily_shop_duration = tk.StringVar(self, value=SHOP_DURATIONS[self.settings.daily_shop_duration],
                                                name='daily_shop_duration')
        self.daily_status = self.label(inner, '待命' if DAILY_ACTIONS_READY else '日常功能尚未完成',
                                       name='daily_status', color=MUTED, anchor='w')
        self.daily_status.pack(fill='x', pady=(0, 8))
        self.daily_start_button, self.daily_pause_button, self.daily_stop_button = self._battle_actions(
            inner, 'daily', '执行日常', self.start_daily, self.toggle_daily_pause)
        body = tk.PanedWindow(inner, name='daily_columns', orient='vertical', sashwidth=10,
                              bg=PANEL, relief='flat', borderwidth=0)
        body.pack(fill='both', expand=True, pady=(16, 0))
        selection = tk.Frame(body, name='daily_selection', bg=PANEL)
        results = tk.Frame(body, name='daily_result_panel', bg=PANEL)
        body.add(selection, minsize=220, stretch='always')
        body.add(results, minsize=95, stretch='never')
        actions = tk.Frame(selection, name='selection_actions', bg=PANEL)
        actions.pack(fill='x', pady=(0, 12))
        self.label(actions, '日常选择', size=12, bold=True).pack(side='left', padx=(0, 18))
        self.daily_select_all_button = self.button(actions, '全选', lambda: self._set_daily_items(True),
                                                   name='select_all_daily')
        self.daily_select_all_button.pack(side='left', padx=(0, 8))
        self.daily_clear_button = self.button(actions, '清空', lambda: self._set_daily_items(False),
                                              name='clear_daily')
        self.daily_clear_button.pack(side='left')
        self.point_reward_button = self.button(actions, '领取积分宝箱', self.start_point_reward,
                                               name='claim_point_reward')
        self.point_reward_button.pack(side='left', padx=(12, 0))
        self.daily_selection_note = self.label(actions, '', name='daily_selection_note', color=MUTED)
        self.daily_selection_note.pack(side='right')
        choices = tk.Canvas(selection, name='daily_choices', bg=PANEL, height=340,
                            highlightthickness=0, borderwidth=0)
        choice_scroll = ttk.Scrollbar(selection, orient='vertical', command=choices.yview)
        choice_scroll.pack(side='right', fill='y')
        choices.configure(yscrollcommand=choice_scroll.set)
        choices.pack(side='left', fill='both', expand=True, padx=(0, 8))
        choice_frame = tk.Frame(choices, name='daily_items', bg=PANEL)
        choice_window = choices.create_window((0, 0), window=choice_frame, anchor='nw')
        choice_frame.bind('<Configure>', lambda _event: choices.configure(scrollregion=choices.bbox('all')))
        self.daily_item_vars = {}
        self.daily_item_checks = {}
        group_views = []
        for index, (title, keys) in enumerate(DAILY_GROUPS):
            group = tk.Frame(choice_frame, name=f'daily_group_{index}', bg=PANEL_ALT,
                             highlightbackground=LINE, highlightthickness=1)
            heading = self.label(group, title, size=11, bold=True, anchor='w')
            heading.pack(fill='x', padx=16, pady=(14, 8))
            items = tk.Frame(group, bg=PANEL_ALT)
            items.pack(fill='x', padx=12, pady=(0, 12))
            checks = []
            for key in keys:
                variable = tk.BooleanVar(self, name='daily_item_' + key, value=self.settings.daily_items[key])
                self.daily_item_vars[key] = variable
                checkbox = tk.Checkbutton(choice_frame, name='daily_item_' + key,
                    text=DAILY_CATALOG[key] + ('（暂不可用）' if key in DAILY_BLOCKED else ''), variable=variable,
                    command=self._daily_selection_changed, bg=PANEL_ALT, fg=TEXT,
                    activebackground=PANEL_ALT, activeforeground=TEXT, selectcolor=PANEL,
                    anchor='w', justify='left', padx=4, pady=7)
                self.daily_item_checks[key] = checkbox
                checks.append(checkbox)
            if 'tribute' in keys:
                tribute_options = tk.Frame(group, bg=PANEL_ALT)
                tribute_options.pack(fill='x', padx=16, pady=(0, 14))
                tribute_options.grid_columnconfigure(0, weight=1)
                self.button(tribute_options, '上贡选项', self._show_daily_tribute_options,
                            name='daily_tribute_options').grid(row=0, column=0, sticky='w')
                self.daily_tribute_budget = self.label(tribute_options, '', name='tribute_budget', color=GOLD,
                                                       justify='left', anchor='w')
                self.daily_tribute_budget.grid(row=1, column=0, sticky='ew', pady=(8, 0))
            if 'guild_donation' in keys:
                donation_options = tk.Frame(group, bg=PANEL_ALT)
                donation_options.pack(fill='x', padx=16, pady=(0, 14))
                self.label(donation_options, '捐献档位', color=MUTED).pack(side='left', padx=(0, 10))
                self.daily_donation_combo = ttk.Combobox(donation_options, name='daily_donation_choice',
                    textvariable=self.daily_donation, values=('1000 钱币', '10 万钱币', '80 元宝'),
                    state='readonly', width=13)
                self.daily_donation_combo.pack(side='left')
            if 'city_shop' in keys:
                shop_options = tk.Frame(group, bg=PANEL_ALT)
                shop_options.pack(fill='x', padx=16, pady=(0, 14))
                self.label(shop_options, '商铺生产', color=MUTED).pack(side='left', padx=(0, 10))
                self.daily_shop_combo = ttk.Combobox(shop_options, name='daily_shop_choice',
                    textvariable=self.daily_shop_duration, values=tuple(SHOP_DURATIONS.values()),
                    state='readonly', width=16)
                self.daily_shop_combo.pack(side='left')
            group_views.append((group, heading, items, checks))

        def layout_choices(event):
            choices.itemconfigure(choice_window, width=event.width)
            columns = 2 if event.width >= 760 else 1
            group_width = (event.width - 12 * (columns - 1)) // columns
            item_columns = 2 if group_width >= 350 else 1
            choice_frame.grid_columnconfigure(0, weight=1, uniform='daily_groups' if columns == 2 else '')
            choice_frame.grid_columnconfigure(1, weight=1 if columns == 2 else 0,
                                               uniform='daily_groups' if columns == 2 else '')
            for index, (group, heading, items, checks) in enumerate(group_views):
                group.grid(row=index // columns, column=index % columns, sticky='new',
                           padx=(0, 12 if columns == 2 and index % columns == 0 else 0), pady=(0, 14))
                heading.configure(wraplength=max(120, group_width - 32))
                items.grid_columnconfigure(0, weight=1, uniform='choices' if item_columns == 2 else '')
                items.grid_columnconfigure(1, weight=1 if item_columns == 2 else 0,
                                           uniform='choices' if item_columns == 2 else '')
                for item_index, checkbox in enumerate(checks):
                    checkbox.configure(wraplength=max(100, (group_width - 32) // item_columns - 30))
                    checkbox.grid(in_=items, row=item_index // item_columns, column=item_index % item_columns,
                                  sticky='ew', padx=(0, 8 if item_columns == 2 and item_index % 2 == 0 else 0))
            self.daily_tribute_budget.configure(wraplength=max(120, group_width - 32))

        choices.bind('<Configure>', layout_choices)

        def scroll_choices(event):
            choices.yview_scroll(-int(event.delta / 120), 'units')
            return 'break'

        def bind_choice_scroll(widget):
            widget.bind('<MouseWheel>', scroll_choices)
            for child in widget.winfo_children():
                bind_choice_scroll(child)

        choices.bind('<MouseWheel>', scroll_choices)
        bind_choice_scroll(choice_frame)
        self.label(results, '本轮结果', size=12, bold=True).pack(anchor='w', pady=(8, 10))
        output = tk.Frame(results, bg=PANEL)
        output.pack(fill='both', expand=True)
        scroll = ttk.Scrollbar(output, orient='vertical')
        scroll.pack(side='right', fill='y')
        self.daily_results = tk.Text(output, name='daily_results', bg=PANEL_ALT, fg=TEXT,
            relief='flat', borderwidth=0, wrap='word', height=5, padx=16, pady=10, spacing3=7,
            state='disabled', yscrollcommand=scroll.set, font=('Microsoft YaHei UI', 10))
        self.daily_results.pack(side='left', fill='both', expand=True)
        scroll.configure(command=self.daily_results.yview)
        self.daily_donation.trace_add('write', lambda *_args: self._daily_selection_changed())
        self.daily_shop_duration.trace_add('write', lambda *_args: self._daily_selection_changed())
        self._render_daily_selection()

    def selected_daily_items(self) -> frozenset[str]:
        return frozenset(key for key, variable in self.daily_item_vars.items() if variable.get())

    def _show_daily_tribute_options(self) -> None:
        self.show_page('development')
        self.show_development('tribute')

    def _set_daily_items(self, enabled: bool) -> None:
        if self.daily.is_active or self.account_plan.active or self.closing:
            return
        for variable in self.daily_item_vars.values():
            variable.set(enabled)
        self._daily_selection_changed()

    def _save_daily_preferences(self) -> bool:
        donation = {'1000 钱币': 1, '10 万钱币': 2, '80 元宝': 3}.get(self.daily_donation.get())
        shop_duration = next((seconds for seconds, label in SHOP_DURATIONS.items()
                              if label == self.daily_shop_duration.get()), None)
        if donation is None or shop_duration is None:
            return False
        self.settings.daily_items = {key: bool(variable.get()) for key, variable in self.daily_item_vars.items()}
        self.settings.daily_donation_type = donation
        self.settings.daily_shop_duration = shop_duration
        return self._save_preferences()

    def _daily_selection_changed(self) -> None:
        saved = self._save_daily_preferences()
        if not self.daily.is_active:
            self._render_daily_selection()
        self.daily_selection_note.configure(text=f'已选 {len(self.selected_daily_items())}/{len(DAILY_CATALOG)} · '
                                            + ('已保存' if saved else '保存失败'), fg=MUTED if saved else RED)
        self._update_daily_controls()

    def _render_daily_selection(self) -> None:
        selected = self.selected_daily_items()
        self.daily_selection_note.configure(text=f'已选 {len(selected)}/{len(DAILY_CATALOG)}')
        lines = [f"{title} · 待执行"
                 + (f'（{DAILY_ITEM_NOTES[key]}）' if key in selected and key in DAILY_ITEM_NOTES else '')
                 for key, title in DAILY_CATALOG.items() if key in selected]
        self.daily_results.configure(state='normal')
        self.daily_results.delete('1.0', 'end')
        self.daily_results.insert('1.0', '\n'.join(lines))
        self.daily_results.configure(state='disabled')
        self.daily_status.configure(text='待命' if selected else '请选择日常项目', fg=MUTED)

    def _build_soul(self) -> None:
        page = self.page('soul', '聚魂')
        panel = self.card(page, name='soul_panel')
        panel.pack(fill='x')
        inner = tk.Frame(panel, bg=PANEL)
        inner.pack(fill='x', padx=20, pady=20)
        counts = tk.Frame(inner, name='soul_counts', bg=PANEL)
        counts.pack(fill='x', pady=(0, 20))
        counts.columnconfigure((0, 1), weight=1, uniform='soul_count')
        self.soul_blue_count = tk.StringVar(self, value='1', name='soul_blue_count')
        self.soul_purple_count = tk.StringVar(self, value='1', name='soul_purple_count')
        self.label(counts, '蓝魂次数', bold=True).grid(row=0, column=0, sticky='w', pady=(0, 9))
        self.soul_blue_input = ttk.Spinbox(counts, name='blue_count', from_=0, to=100,
                                          textvariable=self.soul_blue_count, width=8)
        self.soul_blue_input.grid(row=1, column=0, sticky='ew', padx=(0, 12))
        self.label(counts, '紫魂次数', bold=True).grid(row=0, column=1, sticky='w', pady=(0, 9))
        self.soul_purple_input = ttk.Spinbox(counts, name='purple_count', from_=0, to=100,
                                            textvariable=self.soul_purple_count, width=8)
        self.soul_purple_input.grid(row=1, column=1, sticky='ew')
        self.soul_start_button, self.soul_pause_button, self.soul_stop_button = self._battle_actions(
            inner, 'soul', '开始聚魂', self.start_soul, self.toggle_soul_pause)
        self.soul_harvest_button = self.button(inner, '收获当前牌面', self.harvest_soul_board,
                                               name='harvest_soul_board', state='disabled')
        self.soul_harvest_button.pack(fill='x', pady=(10, 0))
        quote_panel = self.card(page, name='soul_quote_panel')
        quote_panel.pack(fill='x', pady=(14, 0))
        quote_inner = tk.Frame(quote_panel, bg=PANEL)
        quote_inner.pack(fill='x', padx=20, pady=18)
        quote_row = tk.Frame(quote_inner, bg=PANEL)
        quote_row.pack(fill='x', pady=(0, 14))
        self.label(quote_row, '当前报价', size=12, bold=True).pack(side='left')
        self.soul_quote_button = self.button(quote_row, '刷新报价', self.refresh_soul_quote,
                                             name='refresh_soul_quote')
        self.soul_quote_button.pack(side='right', padx=(12, 0))
        self.soul_quote = self.label(quote_inner, '报价尚未读取', name='soul_quote', color=MUTED,
                                     anchor='w', justify='left', wraplength=650)
        self.soul_quote.pack(fill='x')
        self.soul_status = self.label(page, '待命', name='soul_status', color=MUTED,
                                      anchor='w', justify='left', wraplength=760)
        self.soul_status.pack(fill='x', pady=(16, 0))
        quote_inner.bind('<Configure>', lambda event: self.soul_quote.configure(wraplength=max(160, event.width)))
        page.bind('<Configure>', lambda event: self.soul_status.configure(wraplength=max(160, event.width)), add='+')
        self._soul_last_event = None

    def refresh_soul_quote(self) -> bool:
        if self.closing or not self.bound:
            return False
        accepted = self.soul.refresh_quote()
        if not accepted:
            self.log('暂不能读取聚魂报价，请检查当前任务和游戏连接状态。', 'WARNING')
        self._update_soul_controls()
        self._update_run_controls()
        return accepted

    def start_soul(self, *, policy=None) -> bool:
        if self.closing or not self.bound:
            return False
        try:
            blue, purple = int(self.soul_blue_count.get()), int(self.soul_purple_count.get())
        except ValueError:
            blue = purple = -1
        if not (0 <= blue <= 100 and 0 <= purple <= 100 and 1 <= blue + purple <= 100):
            self.soul_status.configure(text='请输入合计 1 至 100 次的蓝魂和紫魂数量。', fg=GOLD)
            return False
        accepted = self.soul.start(blue, purple, policy=policy)
        if accepted:
            self.log(f'聚魂已加入调度：蓝魂 {blue} 次，紫魂 {purple} 次。')
        else:
            self.log('暂不能开始聚魂，请检查当前任务和游戏连接状态。', 'WARNING')
        self._update_soul_controls()
        self._update_run_controls()
        return accepted

    def harvest_soul_board(self) -> bool:
        if self.closing or not self.bound:
            return False
        accepted = self.soul.harvest_existing()
        if accepted:
            self.log('收获当前牌面已加入调度。')
        self._update_soul_controls()
        self._update_run_controls()
        return accepted

    def toggle_soul_pause(self) -> None:
        if self.closing:
            return
        self.soul.resume() if self.soul.is_paused else self.soul.pause()
        self._update_soul_controls()
        self._update_run_controls()

    def _update_soul_controls(self) -> None:
        if not hasattr(self, 'soul_start_button'):
            return
        active, paused = self.soul.is_active, self.soul.is_paused
        available = self._task_start_available()
        self.soul_start_button.configure(state='normal' if available else 'disabled')
        self.soul_quote_button.configure(state='normal' if available else 'disabled')
        self.soul_harvest_button.configure(state='normal' if available else 'disabled')
        for field in (self.soul_blue_input, self.soul_purple_input):
            field.configure(state='disabled' if self.closing else 'normal')
        self.soul_pause_button.configure(text='继续' if paused else '暂停后续发起',
            state='normal' if active and not self.closing and self.runner.state != RunnerState.STOPPING else 'disabled')
        self.soul_stop_button.configure(state='normal' if active and not self.closing else 'disabled')


    def _show_soul_quote(self, quote) -> None:
        # Filled only from the current stock observation, never from a stored estimate.
        if not isinstance(quote, dict):
            self.soul_quote.configure(text='报价尚未读取')
            return
        if quote.get('available') is not True:
            reason = readable_reason(quote.get('reason', ''), '暂不可读取')
            self.soul_quote.configure(text='报价：' + reason)
            return
        rows = []
        for color, name in (('blue', '蓝魂'), ('purple', '紫魂')):
            entry = quote.get(color)
            if not isinstance(entry, dict) or entry.get('known') is not True:
                rows.append(name + '：报价未知')
            elif entry.get('start_available') is not True:
                rows.append(name + ('：暂不可聚魂' if entry.get('start_available') is False else '：资格未知'))
            else:
                free, start, paid = entry.get('free_remaining'), entry.get('start_cost'), entry.get('paid_start_cost')
                free_unlocked = entry.get('free_unlocked')
                free_text = ('免费资格未解锁' if free_unlocked is False else
                             f'免费剩余 {free} 次' if free_unlocked is True and type(free) is int else '免费次数未知')
                start_text = f'{start:,}' if type(start) is int else '未知'
                paid_text = f'{paid:,}' if type(paid) is int else '未知'
                rows.append(f'{name}：{free_text}，当前开局 {start_text} 钱币，普通价格 {paid_text} 钱币')
        growth = quote.get('growth_cost')
        coins = quote.get('coins')
        rows.append(('当前钱币：' + f'{coins:,}  ·  ' if type(coins) is int else '') +
                    '成长：' + (f'{growth:,} 钱币/次' if type(growth) is int else '报价未知') + '，次数按盘面决定')
        self.soul_quote.configure(text='\n'.join(rows) + '\n执行时重新核对报价。')

    def start_point_reward(self) -> bool:
        if self.closing or not self.bound or not DAILY_ACTIONS_READY:
            return False
        kwargs = dict(selected_items=('sign',), enabled_items=('sign',), point_reward_only=True)
        identity = self.objects.current_identity()
        if self.account_plan.active and identity is None:
            self.daily_status.configure(text='请等待当前账号进入游戏后再领取积分宝箱。', fg=GOLD)
            return False
        if identity is not None:
            for field in ('character_id', 'user_id', 'zone_id'):
                if identity.get(field):
                    kwargs['expected_' + field] = str(identity[field])
        placement = (getattr(self.play_runtime, 'submission_placement', None) or
                     ('queue' if self.workflow_placement.get() == '加入队列' else 'switch'))
        instance = self.play_runtime.submit('point_reward', {'args': (), 'kwargs': kwargs},
            placement=placement, resource_policy=getattr(self.play_runtime, 'submission_policy', None))
        self.play_runtime.proxies['daily']._last_id = instance.id
        self.log('积分宝箱已加入调度。')
        self._update_daily_controls()
        self._update_run_controls()
        return True

    def start_daily(self) -> bool:
        if self.closing or not self.bound or not DAILY_ACTIONS_READY:
            return False
        selected_items = self.selected_daily_items()
        if not selected_items:
            self.daily_status.configure(text='请选择至少一项日常项目', fg=GOLD)
            self.log('未选择日常项目，本次未启动。', 'WARNING')
            return False
        try:
            tribute_options = self._tribute_target() if 'tribute' in selected_items else None
        except (TypeError, ValueError):
            self.daily_status.configure(text='请先在培养 → 灵兽上贡设置有效目标和上限', fg=GOLD)
            return False
        if not self._save_daily_preferences():
            self.daily_status.configure(text='日常选择保存失败，本次未启动', fg=RED)
            return False
        selected = {'1000 钱币': 1, '10 万钱币': 2, '80 元宝': 3}.get(self.daily_donation.get())
        accepted = selected is not None and self.daily.start(
            selected, selected_items=selected_items, tribute_options=tribute_options,
            shop_duration=self.settings.daily_shop_duration)
        if not accepted:
            self.log('暂不能开始日常，请检查当前任务和连接状态。', 'WARNING')
        else:
            self.log('日常计划已加入调度：' + '、'.join(DAILY_CATALOG[key] for key in DAILY_CATALOG if key in selected_items))
        self._update_daily_controls()
        self._update_run_controls()
        return accepted

    def toggle_daily_pause(self) -> None:
        if self.closing:
            return
        self.daily.resume() if self.daily.is_paused else self.daily.pause()
        self._update_daily_controls()
        self._update_run_controls()

    def _update_daily_controls(self) -> None:
        if not hasattr(self, 'daily_start_button'):
            return
        active, paused = self.daily.is_active, self.daily.is_paused
        available = DAILY_ACTIONS_READY and self._task_start_available() and bool(self.selected_daily_items())
        self.daily_start_button.configure(state='normal' if available else 'disabled')
        self.point_reward_button.configure(state='normal' if DAILY_ACTIONS_READY and self.bound and not self.closing else 'disabled')
        editing = not self.account_plan.active and not self.closing
        self.daily_donation_combo.configure(state='readonly' if editing else 'disabled')
        self.daily_shop_combo.configure(state='readonly' if editing else 'disabled')
        for checkbox in self.daily_item_checks.values():
            checkbox.configure(state='normal' if editing else 'disabled')
        if self.daily_item_vars['tribute'].get():
            self.daily_tribute_budget.grid()
        else:
            self.daily_tribute_budget.grid_remove()
        self.daily_select_all_button.configure(state='normal' if editing else 'disabled')
        self.daily_clear_button.configure(state='normal' if editing else 'disabled')
        if hasattr(self, 'tribute_target_enabled'):
            try:
                target = self._tribute_target()
                budget = '全部现有桃子' if target.use_all else f'最多 {target.max_requests} 颗桃子'
                goal = (f'目标 {(target.desired_level - 1) // 10 + 1} 阶 '
                        f'{(target.desired_level - 1) % 10 + 1} 星') if target.desired_level else '不限定目标'
                self.daily_tribute_budget.configure(text=f'上贡设置：{budget}，{goal}')
            except (TypeError, ValueError):
                self.daily_tribute_budget.configure(text='上贡设置无效，请先修改上贡选项')
        self.daily_pause_button.configure(text='继续' if paused else '暂停后续发起',
            state='normal' if active and not self.closing and self.runner.state != RunnerState.STOPPING else 'disabled')
        self.daily_stop_button.configure(state='normal' if active and not self.closing else 'disabled')


    def toggle_challenge_pause(self) -> None:
        if self.closing:
            return
        changed = self.challenge.resume() if self.challenge.is_paused else self.challenge.pause()
        if not changed:
            self.log('当前挑战暂不能暂停或继续，请查看任务状态。', 'WARNING')
        self._update_challenge_controls()
        self._update_run_controls()

    def _update_challenge_controls(self) -> None:
        active, paused = self.challenge.is_active, self.challenge.is_paused
        stopping = active and self.challenge.snapshot.get('stopping') is True
        available = self._task_start_available()
        self.challenge_start_button.configure(state='normal' if available else 'disabled')
        self.challenge_pause_button.configure(text='继续挑战' if paused else '暂停后续发起',
            state='normal' if not self.closing and not stopping and (active or paused) else 'disabled')
        self.challenge_stop_button.configure(state='normal' if active and not self.closing else 'disabled')



    def _build_development(self) -> None:
        page = self.page('development', '培养')
        self.current_development = 'star'
        self.selected_development = tk.StringVar(self, name='selected_development', value='star')
        self.development_tabs: dict[str, tk.Button] = {}
        self.development_pages: dict[str, tk.Frame] = {}
        self.development_inputs: dict[str, dict[str, tk.StringVar]] = {}
        self._development_has_status = False
        self._development_input_error_workflow = ''
        choices = tk.Frame(page, name='development_navigation', bg=BG)
        choices.pack(fill='x', pady=(0, 14))
        for module, title in (('star', '星灵'), ('tribute', '灵兽上贡'), ('growth', '成长任务'),
                              ('equipment', '装备')):
            button = self.button(choices, title, lambda selected=module: self.show_development(selected),
                                 name=f'select_{module}')
            self.development_tabs[module] = button

        def layout_tabs(event: tk.Event) -> None:
            columns = 4 if event.width >= 540 else 2
            for column in range(4):
                choices.columnconfigure(column, weight=1 if column < columns else 0,
                                        uniform='development_tabs' if column < columns else '')
            for index, button in enumerate(self.development_tabs.values()):
                button.grid(row=index // columns, column=index % columns, sticky='ew',
                            padx=(0, 8 if index % columns < columns - 1 else 0), pady=(0, 8))

        choices.bind('<Configure>', layout_tabs)
        controls = tk.Frame(page, name='development_controls', bg=BG)
        controls.pack(side='bottom', fill='x', pady=(14, 0))
        self.development_pause_button = self.button(controls, '暂停', self.toggle_development_pause,
                                                     name='pause_development', state='disabled')
        self.development_pause_button.pack(side='left')
        self.development_stop_button = self.button(controls, '停止培养 · F8', self.stop,
                                                    name='stop_development', danger=True, state='disabled')
        self.development_stop_button.pack(side='right')
        self.development_status = self.label(page, '请先绑定游戏', name='development_state', color=MUTED,
                                             wraplength=790, justify='left', anchor='w')
        self.development_status.pack(side='bottom', fill='x', pady=(14, 0))
        page.bind('<Configure>', lambda event: self.development_status.configure(wraplength=max(160, event.width)), add='+')
        viewport = tk.Frame(page, name='development_viewport', bg=BG)
        viewport.pack(fill='both', expand=True)
        canvas = tk.Canvas(viewport, name='development_canvas', bg=BG, highlightthickness=0, height=1)
        scroll = ttk.Scrollbar(viewport, name='development_scroll', orient='vertical', command=canvas.yview)
        scroll.pack(side='right', fill='y')
        canvas.pack(side='left', fill='both', expand=True)
        canvas.configure(yscrollcommand=scroll.set)
        content = tk.Frame(canvas, name='development_panels', bg=BG)
        content_window = canvas.create_window((0, 0), window=content, anchor='nw')
        canvas.bind('<Configure>', lambda event: canvas.itemconfigure(content_window, width=event.width))
        content.bind('<Configure>', lambda _event: canvas.configure(scrollregion=canvas.bbox('all')))
        content.grid_columnconfigure(0, weight=1)
        content.grid_rowconfigure(0, weight=1)
        for module in self.development_tabs:
            panel = self.card(content, name=module)
            panel.grid(row=0, column=0, sticky='nsew')
            inner = tk.Frame(panel, name='form', bg=PANEL)
            inner.pack(fill='both', expand=True, padx=20, pady=20)
            self.development_pages[module] = panel
        # Each function owns one panel; hidden forms retain their entered budgets.
        star = self.development_pages['star'].children['form']
        self.label(star, '激活全部星灵', size=14, bold=True).pack(anchor='w')
        star_note = self.label(star, '使用现有战魂，全部激活或战魂不足时停止', color=MUTED,
                               wraplength=650, justify='left', anchor='w')
        star_note.pack(fill='x', pady=(10, 0))
        star.bind('<Configure>', lambda event: star_note.configure(wraplength=max(160, event.width)))
        self.development_inputs['star'] = {}
        self.star_start_button = self.button(star, '激活星灵', lambda: self.start_development('star'),
                                              name='start_star', primary=True, state='disabled')
        self.star_start_button.pack(anchor='w', pady=(24, 0))

        tribute = self.development_pages['tribute'].children['form']
        self.label(tribute, '每次消耗 1 颗桃子', color=MUTED).pack(anchor='w', pady=(0, 8))
        limits = tk.Frame(tribute, name='limits', bg=PANEL)
        limits.pack(fill='x')
        limits.columnconfigure(0, weight=1)
        self.tribute_target_enabled = tk.BooleanVar(value=False)
        self.tribute_target_tier = tk.StringVar(value='2')
        self.tribute_target_star = tk.StringVar(value='1')
        target_row = tk.Frame(limits, name='target', bg=PANEL)
        target_row.grid(row=1, column=0, sticky='w', padx=(24, 0), pady=(0, 12))
        tier_selection = ttk.Combobox(target_row, name='tier', textvariable=self.tribute_target_tier,
                                     values=tuple(str(tier) for tier in range(1, 11)), state='disabled', width=5)
        tier_selection.pack(side='left')
        self.label(target_row, '阶', color=MUTED).pack(side='left', padx=(6, 12))
        star_selection = ttk.Combobox(target_row, name='star', textvariable=self.tribute_target_star,
                                     values=tuple(str(star) for star in range(1, 11)), state='disabled', width=5)
        star_selection.pack(side='left')
        self.label(target_row, '星', color=MUTED).pack(side='left', padx=(6, 0))
        self.tribute_requests_enabled = tk.BooleanVar(value=True)
        tribute_requests = tk.StringVar(value='100')
        field = tk.Frame(limits, name='requests', bg=PANEL)
        field.grid(row=3, column=0, sticky='w', padx=(24, 0), pady=(0, 12))
        entry = tk.Entry(field, name='value', textvariable=tribute_requests, width=10, bg=PANEL_ALT, fg=TEXT,
                         disabledbackground=PANEL_ALT, disabledforeground=MUTED,
                         insertbackground=TEXT, relief='flat')
        entry.pack(side='left', ipady=5)
        self.label(field, '颗桃子（1–100）', color=MUTED).pack(side='left', padx=(8, 0))
        self._limit_toggle(limits, 'requests', '设置上贡上限', self.tribute_requests_enabled, (entry,), row=2)
        self._limit_toggle(limits, 'target', '设置目标阶星', self.tribute_target_enabled,
                           (tier_selection, star_selection), row=0, enabled_state='readonly')
        self.development_inputs['tribute'] = {'max_requests': tribute_requests}
        self.tribute_start_button = self.button(tribute, '开始上贡', lambda: self.start_development('tribute'),
                                                 name='start_tribute', primary=True, state='disabled')
        self.tribute_start_button.pack(anchor='w', pady=(12, 0))

        growth = self.development_pages['growth'].children['form']
        self.growth_start_button = self.button(growth, '执行当前成长任务', lambda: self.start_development('growth'),
                                                name='start_growth', primary=True, state='disabled')
        self.growth_start_button.pack(anchor='w', pady=(0, 12))
        self.growth_claim_button = self.button(growth, '领取已完成奖励', lambda: self.start_development('claim_growth'),
                                                name='claim_growth_reward', state='disabled')
        self.growth_claim_button.pack(anchor='w')

        equipment = self.development_pages['equipment'].children['form']
        row = tk.Frame(equipment, name='equipment_parameters', bg=PANEL)
        row.pack(fill='x')
        values = {}
        equipment_cells = []
        target_heading = self.label(row, '培养目标', size=12, bold=True)
        budget_heading = self.label(row, '资源上限', size=12, bold=True)
        fields = [('equipment_id', '装备编号（1–255）', ''), ('desired_level', '目标等级（1–255）', ''),
                  ('mode', '培养方式', '强化'), ('max_requests', '次数上限（1–100）', ''),
                  ('max_money', '铜钱上限', ''), ('max_stone', '强化石上限', '')]
        for column, (key, title, initial) in enumerate(fields):
            cell = tk.Frame(row, name=key, bg=PANEL)
            self.label(cell, title, size=10, color=MUTED).pack(anchor='w', pady=(0, 8))
            value = tk.StringVar(value=initial)
            values[key] = value
            if key == 'mode':
                widget = ttk.Combobox(cell, name='value', textvariable=value, values=('强化', '升阶'),
                                      state='readonly', width=15)
            else:
                widget = tk.Entry(cell, name='value', textvariable=value, width=17, bg=PANEL_ALT,
                                  fg=TEXT, insertbackground=TEXT, relief='flat')
            widget.pack(fill='x', ipady=5 if key != 'mode' else 0)
            equipment_cells.append(cell)

        def layout_equipment(event: tk.Event) -> None:
            columns = 2 if event.width >= 500 else 1
            for column in range(2):
                row.columnconfigure(column, weight=1 if column < columns else 0,
                                    uniform='parameter' if column < columns else '')
            target_rows = (3 + columns - 1) // columns
            target_heading.grid(row=0, column=0, columnspan=columns, sticky='w', pady=(0, 16))
            budget_heading.grid(row=target_rows + 1, column=0, columnspan=columns, sticky='w', pady=(6, 16))
            for index, cell in enumerate(equipment_cells):
                offset = 1 if index < 3 else target_rows + 2
                item = index if index < 3 else index - 3
                cell.grid(row=offset + item // columns, column=item % columns, sticky='ew',
                          padx=(0, 18 if columns == 2 and item % columns == 0 else 0), pady=(0, 18))

        row.bind('<Configure>', layout_equipment)
        self.development_inputs['equipment'] = values
        self.equipment_start_button = self.button(equipment, '开始装备培养', lambda: self.start_development('equipment'),
                                                   name='start_equipment', primary=True, state='disabled')
        self.equipment_start_button.pack(anchor='w', pady=(4, 0))

        def scroll_form(event: tk.Event) -> str:
            canvas.yview_scroll(-int(event.delta / 120), 'units')
            return 'break'

        def bind_form_scroll(widget: tk.Misc) -> None:
            if not isinstance(widget, ttk.Combobox):
                widget.bind('<MouseWheel>', scroll_form, add='+')
            for child in widget.winfo_children():
                bind_form_scroll(child)

        canvas.bind('<MouseWheel>', scroll_form)
        bind_form_scroll(content)
        self.show_development('star')

    @staticmethod
    def _limit_toggle(parent: tk.Misc, name: str, title: str, enabled: tk.BooleanVar,
                      fields: tuple[tk.Widget, ...], *, row: int, enabled_state: str = 'normal') -> None:
        def toggle() -> None:
            for field in fields:
                field.configure(state=enabled_state if enabled.get() else 'disabled')
        tk.Checkbutton(parent, name=f'enable_{name}', text=title, variable=enabled, command=toggle,
                       bg=PANEL, fg=TEXT, activebackground=PANEL, activeforeground=TEXT,
                       selectcolor=PANEL_ALT, anchor='w', padx=0, pady=4,
                       highlightthickness=0).grid(row=row, column=0, sticky='w', pady=(4, 8))

    def show_development(self, module: str) -> None:
        for panel in self.development_pages.values():
            panel.grid_remove()
        self.development_pages[module].grid()
        self.current_development = module
        self.selected_development.set(module)
        for name, button in self.development_tabs.items():
            button.configure(bg=ACCENT if name == module else PANEL_ALT, fg=BG if name == module else TEXT)
        self._update_title()

    def _tribute_target(self) -> TributeTarget:
        target_level = 0
        if self.tribute_target_enabled.get():
            tier, star = int(self.tribute_target_tier.get()), int(self.tribute_target_star.get())
            if not 1 <= tier <= 10 or not 1 <= star <= 10:
                raise ValueError('invalid tribute stage')
            target_level = (tier - 1) * 10 + star
        return TributeTarget(
            use_all=not self.tribute_requests_enabled.get(),
            max_requests=int(self.development_inputs['tribute']['max_requests'].get().strip())
                if self.tribute_requests_enabled.get() else 100,
            target_level=target_level)

    def start_development(self, module: str) -> bool:
        if self.closing or not self.bound:
            return False
        try:
            if module == 'tribute':
                target = self._tribute_target()
            else:
                values = {key: value.get().strip() if key == 'mode' else int(value.get().strip())
                    for key, value in self.development_inputs.get(module, {}).items()}
                if 'mode' in values:
                    values['mode'] = {'强化': 'enchant', '升阶': 'evolve'}.get(values['mode'], values['mode'])
                targets = {'equipment': EquipmentTarget, 'star': StarTarget}
                target = targets[module](**values) if module in targets else None
        except (ValueError, TypeError) as exc:
            self._development_has_status = True
            instance = self.development._instance()
            self._development_input_error_workflow = (
                instance.id if instance and instance.state.value == 'CLOSED' else '')
            self.development_status.configure(text='目标阶、星请选择 1–10，上贡上限为 1–100。' if module == 'tribute' else '请填写有效的装备编号、目标等级、培养次数和资源上限。', fg=GOLD)
            self.log('培养参数无效：' + str(exc), 'WARNING')
            return False
        accepted = self.development.start(module, target)
        if not accepted:
            self.log('当前已有任务或接口被占用，暂不能开始培养。', 'WARNING')
        else:
            self._development_has_status = True
            self._development_input_error_workflow = ''
            self.development_status.configure(text='正在准备培养', fg=GOLD)
        self._update_development_controls()
        self._update_run_controls()
        return accepted

    def toggle_development_pause(self) -> None:
        if self.development.is_paused:
            self.development.resume()
        else:
            self.development.pause()
        self._update_development_controls()

    def _update_development_controls(self) -> None:
        if not hasattr(self, 'growth_start_button'):
            return
        active, paused = self.development.is_active, self.development.is_paused
        available = self._task_start_available()
        for button in (self.star_start_button, self.growth_start_button, self.growth_claim_button, self.equipment_start_button):
            button.configure(state='normal' if available else 'disabled')
        self.tribute_start_button.configure(state='normal' if available else 'disabled')
        self.development_pause_button.configure(text='继续' if paused else '暂停', state='normal' if active and not self.closing and self.runner.state != RunnerState.STOPPING else 'disabled')
        self.development_stop_button.configure(state='normal' if active and not self.closing else 'disabled')


    def toggle_campaign_pause(self) -> None:
        if self.closing:
            return
        changed = self.campaign.resume() if self.campaign.is_paused else self.campaign.pause()
        if not changed:
            self.log("当前征战暂不能暂停或继续，请查看任务状态。", "WARNING")
        self._update_campaign_controls()
        self._update_run_controls()

    def _update_campaign_controls(self) -> None:
        if not hasattr(self, "campaign_start_button"):
            return
        active, paused = self.campaign.is_active, self.campaign.is_paused
        data = self.campaign.snapshot
        stopping = active and data.get("stopping") is True
        self.campaign_start_button.configure(text="新一轮征战" if data.get("run_id") else "开始征战", state="normal" if self._task_start_available() else "disabled")
        self.campaign_pause_button.configure(text="继续征战" if paused else "暂停后续发起", state="normal" if not self.closing and not stopping and (active or paused) else "disabled")
        self.campaign_stop_button.configure(state="normal" if active and not self.closing else "disabled")


    def _task_start_available(self) -> bool:
        return bool(self.bound) and not self.closing

    def _task_idle_detail(self) -> str:
        if not self.bound:
            return "请先绑定游戏"
        if self.runner.is_active:
            return "等待当前任务结束"
        return "就绪"


    def _build_logs(self) -> None:
        page = self.page("logs", "日志")
        card = self.card(page)
        card.pack(fill="both", expand=True)
        self.log_text = tk.Text(card, name="runtime_log", bg=PANEL, fg=TEXT, insertbackground=TEXT,
                                selectbackground=LINE, selectforeground=TEXT, relief="flat", borderwidth=0,
                                padx=22, pady=20, height=1, wrap="word", spacing1=2, spacing2=3,
                                spacing3=8, font=("Microsoft YaHei UI", 11), state="disabled")
        scroll = ttk.Scrollbar(card, command=self.log_text.yview)
        scroll.pack(side="right", fill="y", padx=(0, 7), pady=10)
        self.log_text.configure(yscrollcommand=scroll.set)
        self.log_text.pack(fill="both", expand=True)
        self.log_text.tag_configure("WARNING", foreground=GOLD)
        self.log_text.tag_configure("ERROR", foreground=RED)
        self.log_text.tag_configure("CRITICAL", foreground=RED)
        self.log_text.tag_configure("INFO", foreground=TEXT)
        self.log_text.tag_configure("TIME", foreground=MUTED)

    def _build_settings(self) -> None:
        page = self.page("settings", "设置")
        card = self.card(page)
        card.pack(fill="x")
        inner = tk.Frame(card, bg=PANEL)
        inner.pack(fill="x", padx=22, pady=22)
        self.label(inner, "窗口标题关键词", size=13, bold=True).pack(anchor="w")
        self.keyword_var = tk.StringVar(value=self.settings.window_keyword)
        entry = tk.Entry(inner, name="window_keyword", textvariable=self.keyword_var, bg=PANEL_ALT,
                         fg=TEXT, insertbackground=TEXT, selectbackground=LINE, selectforeground=TEXT,
                         relief="flat", highlightthickness=1, highlightbackground=LINE,
                         highlightcolor=GREEN, font=("Microsoft YaHei UI", 11))
        entry.pack(fill="x", ipady=10, pady=(14, 20))
        actions = tk.Frame(inner, bg=PANEL)
        actions.pack(fill="x")
        self.button(actions, "保存设置", self.save_settings, name="save_settings", primary=True).pack(side="left")
        self.settings_note = self.label(inner, "", color=MUTED, anchor="w", justify="left", wraplength=650)
        self.settings_note.pack(fill="x", pady=(14, 0))
        card.bind("<Configure>", lambda event: self.settings_note.configure(wraplength=max(240, event.width - 44)))
        self.stop_key_settings = self.label(page, "正在准备停止快捷键…", color=MUTED, anchor="w",
                                            justify="left", wraplength=800)
        self.stop_key_settings.pack(fill="x", pady=(22, 0))
        def fit_settings_text(event: tk.Event) -> None:
            self.stop_key_settings.configure(wraplength=max(240, event.width))
        page.bind("<Configure>", fit_settings_text)

    def show_page(self, key: str) -> None:
        for page in self.pages.values():
            page.grid_remove()
        self.pages[key].grid()
        self.current_page = key
        self.selected_page.set(key)
        self.page_heading.configure(text=self.page_titles[key])
        for name, button in self.nav_buttons.items():
            button.configure(bg=PANEL if name == key else RAIL, fg=ACCENT if name == key else MUTED)
        self._update_title()

    def _update_title(self) -> None:
        title = self.nav_buttons[self.current_page].cget("text")
        if self.current_page == "development":
            title += " · " + self.development_tabs[self.current_development].cget("text")
        self.title(f"将神助手 · {title}")

    def _append_log_line(self, message: str, level: str, stamp: str) -> None:
        message = readable_message(message, level=level)
        if not message.strip():
            return
        level_text = {"INFO": "", "WARNING": "注意", "ERROR": "错误", "DEBUG": "调试", "CRITICAL": "严重"}.get(level, "")
        severity = f"{level_text}  " if level_text else ""
        line = f"{stamp}  {severity}{message}\n"
        self.log_lines.append((line, level))
        self.log_lines = self.log_lines[-600:]
        if hasattr(self, "log_text"):
            at_bottom = self.log_text.yview()[1] >= 0.999
            self.log_text.configure(state="normal")
            self.log_text.insert("end", f"{stamp}  ", "TIME")
            self.log_text.insert("end", severity + message + "\n", level)
            if int(self.log_text.index("end-1c").split(".")[0]) > 700:
                self.log_text.delete("1.0", "100.0")
            if at_bottom:
                self.log_text.see("end")
            self.log_text.configure(state="disabled")

    def log(self, message: str, level: str = "INFO", timestamp: str = "", *, update_status: bool = True,
            file_message: str | None = None, visible: bool = True, ui_state_key: tuple | None = None) -> None:
        from datetime import datetime
        try:
            stamp = datetime.fromisoformat(timestamp).astimezone().strftime("%H:%M:%S") if timestamp else datetime.now().strftime("%H:%M:%S")
        except ValueError:
            stamp = timestamp
        raw = message if file_message is None else file_message
        message = readable_message(message, level=level)
        visible = visible and bool(message.strip())
        if visible and ui_state_key is not None and level == 'INFO':
            if self._visible_log_states.get(ui_state_key) == message:
                visible = False
            else:
                self._visible_log_states[ui_state_key] = message
                if len(self._visible_log_states) > 512:
                    del self._visible_log_states[next(iter(self._visible_log_states))]
        if visible:
            self._append_log_line(message, level, stamp)
        self.logger.log(getattr(logging, level, logging.INFO), raw)
        if visible and update_status:
            self.footer_status.configure(text=message[:75], fg=RED if level == "ERROR" else MUTED)

    def _binding_busy(self, *, plan_internal: bool = False) -> bool:
        status = self.objects.connection_status
        return bool(self.closing or self.runner.is_active or self.objects.is_busy
                    or self.objects.has_campaign_owner or status.get('prelogin_bridge')
                    or status.get('prelogin_unknown')
                    or self.account_plan.active and not plan_internal)

    def apply_auto_windows(self, windows: list[WindowInfo], selected_hwnd: int | None = None) -> bool:
        if self._binding_busy():
            return False
        self.windows = list(windows)
        self.window_combo.configure(values=[window.label for window in self.windows])
        selected = next((window for window in self.windows if window.hwnd == selected_hwnd), None)
        if selected is not None:
            if not self.bind_game_window(selected, automatic=True):
                return False
            self.window_combo.current(self.windows.index(selected))
        else:
            if self.bound and not any((window.hwnd, window.process_id) ==
                                      (self.bound.hwnd, self.bound.process_id) for window in self.windows):
                self._clear_binding()
            self.window_combo.set('请选择游戏窗口' if self.windows else '')
        return True

    def refresh_windows(self, *, plan_internal: bool = False) -> None:
        if self._binding_busy(plan_internal=plan_internal):
            return
        try:
            windows = [window for window in self.adapter.find_windows(self.settings.window_keyword)
                       if window.process_name.casefold() == 'js.exe']
            self.windows = windows
            self.window_combo.configure(values=[window.label for window in windows])
            selected = next((window for window in windows if self.bound and
                             (window.hwnd, window.process_id) == (self.bound.hwnd, self.bound.process_id)), None)
            if selected is None and len(windows) == 1:
                selected = windows[0]
            if selected is not None:
                self.bind_game_window(selected, plan_internal=plan_internal)
                self.window_combo.current(windows.index(selected))
            else:
                if self.bound:
                    self._clear_binding()
                self.window_combo.set('请选择游戏窗口' if windows else '')
            if not plan_internal:
                self.auto_connection.resume()
        except (WindowError, OSError) as exc:
            self.log(f'查找窗口失败：{exc}', 'ERROR')

    def _clear_binding(self) -> None:
        self.objects.unbind()
        self.bound = None
        self.object_snapshot = None
        self.object_seen_monotonic = 0.0
        self._update_object_controls()
        self._update_run_controls()

    def bind_game_window(self, window: WindowInfo, *, plan_internal: bool = False,
                         automatic: bool = False) -> bool:
        if self._binding_busy(plan_internal=plan_internal):
            return False
        try:
            current = self.adapter.inspect(window.hwnd)
            if current.process_name.casefold() != 'js.exe' or current.process_id != window.process_id:
                raise WindowError('游戏窗口已变化，正在等待新的窗口。')
            changed = not self.bound or (self.bound.hwnd, self.bound.process_id) != (current.hwnd, current.process_id)
            self.objects.bind(current)
            self.bound = current
            self.object_connection = self.objects.connection_status
            if changed:
                self.object_snapshot = None
                self.object_seen_monotonic = 0.0
                self.settings.window_title = current.title
                self._save_preferences()
                self.log(f'已绑定：{current.title}')
            self._update_object_controls()
            self._update_run_controls()
            return True
        except (WindowError, OSError) as exc:
            if not automatic:
                self.log(f'绑定失败：{exc}', 'ERROR')
            return False

    def _checked_window(self, bound: WindowInfo) -> WindowInfo:
        current = self.adapter.inspect(bound.hwnd)
        if current.process_id != bound.process_id or current.process_name != bound.process_name:
            raise WindowError("原窗口已失效，请重新绑定游戏。")
        return current

    def bind_owned_game(self, game: dict) -> bool:
        """Bind only the exact game process created by this account operation."""
        if (self.closing or self.runner.is_active or self.objects.is_busy or self.objects.has_campaign_owner
                or not isinstance(game, dict)):
            return False
        try:
            hwnd, pid, creation = game['hwnd'], game['pid'], game['process_creation_filetime']
            if any(type(value) is not int or value <= 0 for value in (hwnd, pid, creation)):
                return False
            candidates = [window for window in self.adapter.find_windows(self.settings.window_keyword)
                          if window.hwnd == hwnd and window.process_id == pid and window.process_name.casefold() == 'js.exe']
            if len(candidates) != 1:
                return False
            identity = identity_for(candidates[0])
            if identity.pid != pid or identity.hwnd != hwnd or identity.creation_time != creation:
                return False
            self.windows = candidates
            self.window_combo.configure(values=[window.label for window in candidates])
            self.window_combo.current(0)
            return self.bind_game_window(candidates[0], plan_internal=True)
        except (KeyError, ValueError, OSError, RuntimeError):
            return False

    def start_check(self) -> bool:
        if self.closing or self.objects.is_busy or self.objects.has_campaign_owner:
            return False
        if not self.bound:
            self.log("请先绑定游戏窗口，再运行自检。", "WARNING")
            return False
        bound = self.bound
        def check(context: Any) -> None:
            context.log("开始框架自检：全程只读，不向游戏发送输入。")
            context.wait(0.6)
            current = self._checked_window(bound)
            context.log(f"窗口连接正常：{current.width} × {current.height}，PID {current.process_id}。")
            context.wait(0.6)
            context.checkpoint()
            context.log("框架自检通过：绑定窗口身份与任务调度正常。")
        accepted = self.runner.start("框架自检", check)
        self._update_run_controls()
        return accepted

    def toggle_pause(self) -> None:
        if self.account_plan.active:
            self.account_plan.resume() if self.account_plan.paused else self.account_plan.pause()
            return
        if getattr(self, 'soul', None) and self.soul.is_active:
            self.toggle_soul_pause()
            return
        if getattr(self, 'daily', None) and self.daily.is_active:
            self.toggle_daily_pause()
            return
        if self.challenge.is_active or self.challenge.is_paused:
            self.toggle_challenge_pause()
            return
        if getattr(self, 'development', None) and self.development.is_active:
            self.toggle_development_pause()
            return
        if self.campaign.is_active or self.campaign.is_paused:
            self.toggle_campaign_pause()
            return
        if self.runner.state == RunnerState.PAUSED:
            self.runner.resume()
        else:
            self.runner.pause()
        self._update_run_controls()

    def stop_play(self, kind: str) -> None:
        name = {'battle': 'campaign', 'growth': 'development', 'equipment': 'development',
                'tribute': 'development', 'star': 'development'}.get(kind, kind)
        coordinator = getattr(self, name, None)
        if coordinator is not None:
            coordinator.stop()
        self._render_workflow_state()

    def show_resource_policy(self, kind: str, account_id: str = '') -> None:
        kind = {'battle': 'campaign', 'development': 'growth', 'growth-claim': 'claim_growth'}.get(kind, kind)
        if kind not in self.play_runtime_definition_keys():
            kind = 'growth'
        dialog = tk.Toplevel(self, name='resource_policy_dialog')
        dialog.title('日常默认资源策略（按玩法）' if kind == 'daily' else '资源策略')
        dialog.configure(bg=BG)
        dialog.transient(self)
        editor = ResourcePolicyEditor(dialog, self)
        editor.pack(fill='both', expand=True, padx=20, pady=16)
        editor.set_resources(self.play_runtime.resources_for(kind))
        editor.set_capabilities(self.play_runtime.capabilities_for(kind))
        editor.set_value(self.play_runtime.policy_for(kind, account_id))
        note = self.label(dialog, '', color=RED, anchor='w', wraplength=500)
        note.pack(fill='x', padx=20)
        actions = tk.Frame(dialog, bg=BG)
        actions.pack(fill='x', padx=20, pady=16)
        def save():
            try:
                self.play_runtime.save_policy(kind, editor.get_value(), account_id)
            except (ValueError, RuntimeError, OSError) as error:
                note.configure(text=str(error))
                return
            dialog.destroy()
        self.button(actions, '保存', save, name='save_resource_policy', primary=True).pack(side='right')
        self.button(actions, '取消', dialog.destroy, name='cancel_resource_policy').pack(side='right', padx=8)

    @staticmethod
    def play_runtime_definition_keys():
        from .play_workflows import DEFINITIONS
        return DEFINITIONS

    def _render_workflow_state(self) -> None:
        snapshot = self.play_runtime.summary_snapshot
        self.workflow_page.update_status(snapshot)
        scene = snapshot.get('scene') or {}
        phase = {'BATTLE': '战斗中', 'RESULT': '战后收尾', 'SCENARIO': '剧情中',
                 'CAMPAIGN': '关卡地图', 'MAIN': '主界面', 'UNKNOWN': '待确认'}.get(scene.get('phase'), '')
        root = {'Capital': '主城', 'Campaign': '征战', 'Anecdote': '挑战', 'Battle': '战斗', 'PointRace': '积分赛'}.get(
            scene.get('root'), '场景待更新')
        active = next((item for item in snapshot['instances'] if item['id'] == snapshot['active_id']), None)
        text = root + (' · ' + phase if phase else '')
        manager = getattr(self, 'auto_connection', None)
        connection = manager.snapshot if manager is not None else {}
        if self.objects.bridge_state != 'ready' and connection.get('stage') != 'waiting_owner':
            text = connection.get('message') or text
        if active:
            text += '    ' + active['label'] + '：' + active['message']
        elif snapshot['pending_count']:
            text += f"    {snapshot['pending_count']} 项等待执行"
        self.workflow_scene_status.configure(text=text)
        for source, label_name in (('soul', 'soul_status'), ('daily', 'daily_status'),
                                   ('development', 'development_status')):
            proxy = getattr(self, source)
            instance = proxy._instance()
            if instance and instance.state.value not in ('CLOSED', 'ACTIVE') and hasattr(self, label_name):
                getattr(self, label_name).configure(text=instance.message, fg=GOLD)
            elif (source == 'development' and instance and instance.state.value == 'CLOSED'
                  and instance.id != self._development_input_error_workflow):
                # Executor callbacks can render STOPPING before runtime closure.
                # Project closure too; runner idle alone does not end cleanup.
                result = instance.result or instance.latest
                progress = instance.progress or str(result.get('progress') or '')
                self._development_has_status = True
                self.development_status.configure(
                    text=instance.message + ('\n' + progress if progress else ''),
                    fg=RED if result.get('status') in ('BLOCKED', 'UNKNOWN', 'FAILED') else MUTED)
        for source, values_name in (('campaign', 'campaign_values'), ('challenge', 'challenge_values')):
            instance = getattr(self, source)._instance()
            if instance and instance.state.value in ('QUEUED', 'READY', 'WAITING', 'PAUSED', 'STOPPING'):
                getattr(self, values_name)['status'].configure(text=instance.message)
        daily = self.daily._instance()
        if daily is not None and daily.child_ids and daily.latest:
            states = {'COMPLETED': '已完成', 'ALREADY_COMPLETE': '已完成', 'ALREADY_CLAIMED': '已领取',
                      'PENDING': '等待中', 'WAITING': '等待中', 'UNKNOWN': '结果待确认', 'BLOCKED': '等待处理',
                      'UNAVAILABLE': '暂不可做', 'NO_FREE_REWARD': '暂无可领', 'SKIPPED': '未启用'}
            lines = []
            for item in daily.latest.get('items', ()):
                if item.get('status') == 'SKIPPED':
                    continue
                name = item.get('name') or '日常玩法'
                status = states.get(item.get('status'), '状态已更新')
                current, required = item.get('current'), item.get('required')
                progress = f' {current}/{required}' if current is not None and required is not None else ''
                lines.append(name + progress + '  ' + status)
            self.daily_results.configure(state='normal')
            self.daily_results.delete('1.0', 'end')
            self.daily_results.insert('1.0', '\n'.join(lines))
            self.daily_results.configure(state='disabled')

    def stop(self) -> None:
        self.auto_connection.hold()
        self.play_runtime.stop_all()
        if self.account_plan.active:
            self.account_plan.stop()
        self.runner.stop()
        if not self.objects.has_campaign_owner:
            self.objects.stop()
        self._render_workflow_state()
        self._update_object_controls()
        self._update_run_controls()

    def _update_run_controls(self) -> None:
        state = self.runner.state
        active = self.runner.is_active
        object_busy = self.objects.is_busy or self.objects.has_campaign_owner
        runtime_state = self.objects.bridge_state
        any_busy = active or object_busy or self.closing
        names = {"idle": "待机", "running": "运行中", "paused": "已暂停", "stopping": "正在停止"}
        label = "对象连接处理中" if object_busy and state == RunnerState.IDLE else names[state.value]
        self.run_state_label.configure(text=f"●  {label}", fg=GREEN if state == RunnerState.IDLE and not object_busy else GOLD)
        self.start_button.configure(state="disabled" if any_busy or not self.bound else "normal")
        self.pause_button.configure(text="继续" if state == RunnerState.PAUSED else "暂停", state="normal" if state in (RunnerState.RUNNING, RunnerState.PAUSED) else "disabled")
        self.stop_button.configure(state="normal" if not self.closing and (state in (RunnerState.RUNNING, RunnerState.PAUSED) or object_busy or runtime_state in ("ready", "connecting", "stopping", "unknown")) else "disabled")
        self.window_combo.configure(state="disabled" if any_busy or self.account_plan.active else "readonly")
        if state == RunnerState.RUNNING:
            if not getattr(self, "_progress_active", False):
                self.progress.start(18)
                self._progress_active = True
        else:
            if getattr(self, "_progress_active", False):
                self.progress.stop()
                self._progress_active = False

    def _save_preferences(self) -> bool:
        try:
            self.store.save(self.settings)
            return True
        except (OSError, ValueError) as exc:
            self.log(f"配置保存失败：{exc}", "ERROR")
            return False

    def save_settings(self) -> None:
        keyword = self.keyword_var.get().strip()
        if not keyword or len(keyword) > 256:
            self.settings_note.configure(text="请输入 1 到 256 字的窗口关键词。", fg=GOLD)
            return
        self.settings.window_keyword = keyword
        if self._save_preferences():
            self.settings_note.configure(text="已保存，正在自动查找匹配游戏。", fg=GREEN)
            self.log("设置已保存。")

    def _poll(self) -> None:
        # Tk pumps ready sources and named deadlines; it never scans workflows.
        if not self._events_running:
            return
        self.event_runtime.pump()
        if self._events_running:
            delay_ms = max(1, int(self.event_runtime.next_delay() * 1000))
            self.after(delay_ms, self._poll)

    def close(self) -> None:
        if self.closing:
            return
        if not self.account_page.flush_pending_options():
            self._control_shutdown_requested = False
            self.show_page('accounts')
            self.log('账号任务配置未保存；请修正选项或重试保存后再关闭。', 'WARNING')
            return
        self.closing = True
        self.control_server.close()
        self.play_runtime.close()
        self.account_plan.stop()
        self.campaign.close()
        self.challenge.close()
        self.elite.close()
        if getattr(self, 'development', None):
            self.development.close()
        if getattr(self, 'daily', None):
            self.daily.close()
        if getattr(self, 'soul', None):
            self.soul.close()
        self.runner.stop()
        try:
            self.adapter.close()
        except WindowError as exc:
            self.log(f"停止快捷键释放未确认：{exc}。进程退出后由系统回收。", "WARNING")
        self.global_stop_available = False
        self._update_object_controls()
        self._update_run_controls()
        self.footer_status.configure(text="正在停止后续发起、核对在途战斗并清理接口…")
        self.event_runtime.begin_shutdown()

    def _finish_close(self) -> None:
        self.play_runtime.tick()
        self.account_plan.tick()
        # Account shutdown owns durable task admission and launcher cleanup.
        # Keep ticking it until its bounded stop has reached a terminal state.
        if self.account_plan.title_worker_pending or self.account_plan.is_active:
            self.event_runtime.schedule_shutdown_check()
            return
        if (not self.campaign.is_shutdown or not self.challenge.is_shutdown or not self.elite.is_shutdown or
                (getattr(self, 'development', None) and not self.development.is_shutdown) or
                (getattr(self, 'daily', None) and not self.daily.is_shutdown) or
                (getattr(self, 'soul', None) and not self.soul.is_shutdown)):
            self.event_runtime.schedule_shutdown_check()
            return
        if not self._objects_close_requested:
            self._objects_close_requested = True
            self.objects.close()
        if self.runner.is_active or not self.objects.is_shutdown:
            self.event_runtime.schedule_shutdown_check()
            return
        if self.event_runtime.has_pending_events:
            self.event_runtime.schedule_shutdown_check(.01)
            return
        self.log("助手已关闭。")
        for handler in self.logger.handlers:
            handler.close()
        self._events_running = False
        self.event_runtime.close()
        self.destroy()


def main() -> None:
    parser = argparse.ArgumentParser(description="将神助手桌面框架")
    parser.add_argument("--data-dir", type=Path, default=ROOT / "data", help="本地配置和日志目录")
    args = parser.parse_args()
    app = AssistantApp(args.data_dir)
    app.mainloop()
