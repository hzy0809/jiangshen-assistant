"""Reusable resource permissions editor; persistence belongs to its caller."""
from __future__ import annotations

import copy
import re
import tkinter as tk
from collections.abc import Callable, Iterable
from typing import Any

from .theme import ACCENT, BG, LINE, MUTED, PANEL, PANEL_ALT, RED, TEXT


RESOURCE_LABELS = {
    'money': '金钱', 'gold': '元宝', 'peaches': '桃子', 'practice': '战魂',
    'stamina': '体力', 'stone': '强化石', 'mana': '灵气',
    'experience': '经验', 'contribution': '同盟贡献',
    'silver_pick': '银镐', 'gold_pick': '金镐',
}
_SWITCHES = {
    'allow_purchase': '允许购买',
    'allow_accelerate': '允许加速',
    'allow_replenish': '允许补足',
}
_UNSUPPORTED_SWITCHES = {
    'allow_purchase': '购买（暂不支持）',
    'allow_accelerate': '加速（暂不支持）',
    'allow_replenish': '补足（暂不支持）',
}


class ResourcePolicyEditor(tk.Frame):
    """Edit explicit permissions with optional nonnegative spending limits.

    ``on_change(value)`` runs on the Tk thread after a valid committed change.
    Limits commit on focus loss or Return; ``get_value`` also validates pending
    input. Changing the visible resource subset never changes hidden values.
    """

    def __init__(self, parent, app, *, on_change: Callable[[dict], Any] | None = None):
        super().__init__(parent, name='resource_policy_editor', bg=PANEL)
        self.app = app
        self.on_change = on_change
        self._enabled = True
        self._capabilities = {key: True for key in _SWITCHES}
        self._loading = False
        self._resources = tuple(RESOURCE_LABELS)
        self._last_value = None
        self.allowed_vars = {}
        self.limit_vars = {}
        self.resource_rows = {}
        self.resource_checks = {}
        self.limit_entries = {}
        self.switch_vars = {}
        self.switch_checks = {}
        self.columnconfigure(0, weight=1)
        tk.Label(self, text='允许消耗', bg=PANEL, fg=MUTED, anchor='w').grid(
            row=0, column=0, sticky='ew', pady=(0, 6))
        tk.Label(self, text='上限（空为不限）', bg=PANEL, fg=MUTED, anchor='e').grid(
            row=0, column=1, sticky='e', pady=(0, 6))
        for index, (key, label) in enumerate(RESOURCE_LABELS.items(), start=1):
            row = tk.Frame(self, name=f'resource_{key}', bg=PANEL)
            row.grid(row=index, column=0, columnspan=2, sticky='ew', pady=2)
            row.columnconfigure(0, weight=1)
            allowed = tk.BooleanVar(self, value=False)
            limit = tk.StringVar(self, value='')
            check = self._check(row, f'allow_{key}', label, allowed)
            check.grid(row=0, column=0, sticky='w')
            entry = tk.Entry(row, name=f'limit_{key}', textvariable=limit, width=12,
                             bg=PANEL_ALT, fg=TEXT, insertbackground=TEXT,
                             disabledbackground=PANEL, disabledforeground=MUTED,
                             relief='flat', justify='right', highlightthickness=1,
                             highlightbackground=LINE, highlightcolor=ACCENT)
            entry.grid(row=0, column=1, sticky='e', ipady=5)
            entry.bind('<FocusOut>', self._commit)
            entry.bind('<Return>', self._commit)
            self.allowed_vars[key] = allowed
            self.limit_vars[key] = limit
            self.resource_rows[key] = row
            self.resource_checks[key] = check
            self.limit_entries[key] = entry
        switches = tk.Frame(self, name='resource_actions', bg=PANEL)
        switches.grid(row=len(RESOURCE_LABELS) + 1, column=0, columnspan=2,
                      sticky='ew', pady=(10, 0))
        for index, (key, label) in enumerate(_SWITCHES.items()):
            variable = tk.BooleanVar(self, value=False)
            check = self._check(switches, key, label, variable)
            check.grid(row=index, column=0, sticky='w', pady=1)
            self.switch_vars[key] = variable
            self.switch_checks[key] = check
        self.error_label = tk.Label(self, name='resource_policy_error', text='', bg=PANEL,
                                    fg=RED, anchor='w', justify='left', wraplength=300)
        self.error_label.grid(row=len(RESOURCE_LABELS) + 2, column=0, columnspan=2,
                              sticky='ew', pady=(6, 0))
        self.bind('<Configure>', lambda event: self.error_label.configure(
            wraplength=max(160, event.width)))
        self.set_value({})

    def _check(self, parent, name, text, variable):
        return tk.Checkbutton(parent, name=name, text=text, variable=variable,
                              command=self._commit, bg=PANEL, fg=TEXT,
                              activebackground=PANEL, activeforeground=TEXT,
                              selectcolor=BG, disabledforeground=MUTED,
                              highlightthickness=1, highlightbackground=PANEL,
                              highlightcolor=ACCENT, anchor='w', padx=2, pady=3)

    @staticmethod
    def _validate_value(value: dict) -> dict:
        if not isinstance(value, dict):
            raise ValueError('资源策略必须是对象')
        allowed = value.get('allowed_resources', [])
        if not isinstance(allowed, list) or any(
                not isinstance(key, str) or key not in RESOURCE_LABELS for key in allowed):
            raise ValueError('请选择有效的资源')
        limits = value.get('limits', {})
        if not isinstance(limits, dict):
            raise ValueError('资源上限必须是对象')
        for key, limit in limits.items():
            if key not in RESOURCE_LABELS:
                raise ValueError('资源上限包含未知资源')
            if limit is not None and (type(limit) is not int or limit < 0):
                raise ValueError(f'{RESOURCE_LABELS[key]}上限需填写非负整数')
        result = {'allowed_resources': [key for key in RESOURCE_LABELS if key in allowed],
                  'limits': {key: limits.get(key) for key in RESOURCE_LABELS if key in allowed}}
        for key, label in _SWITCHES.items():
            flag = value.get(key, False)
            if type(flag) is not bool:
                raise ValueError(f'{label}必须为开关值')
            result[key] = flag
        return result

    def set_value(self, value: dict) -> None:
        normalized = self._validate_value(value)
        self._loading = True
        try:
            for key in RESOURCE_LABELS:
                self.allowed_vars[key].set(key in normalized['allowed_resources'])
                limit = normalized['limits'].get(key)
                self.limit_vars[key].set('' if limit is None else str(limit))
            for key in _SWITCHES:
                self.switch_vars[key].set(normalized[key])
            self.error_label.configure(text='')
            self._last_value = copy.deepcopy(normalized)
            self._sync_enabled()
        finally:
            self._loading = False

    def get_value(self) -> dict:
        allowed = [key for key in RESOURCE_LABELS if self.allowed_vars[key].get()]
        limits = {}
        for key in allowed:
            text = self.limit_vars[key].get().strip()
            if text and not re.fullmatch(r'[0-9]+', text):
                raise ValueError(f'{RESOURCE_LABELS[key]}上限需填写非负整数，或留空')
            limits[key] = int(text) if text else None
        return self._validate_value({
            'allowed_resources': allowed, 'limits': limits,
            **{key: variable.get() for key, variable in self.switch_vars.items()},
        })

    def set_resources(self, keys: Iterable[str]) -> None:
        if isinstance(keys, (str, bytes)):
            raise ValueError('资源列表需按资源名称逐项提供')
        keys = tuple(keys)
        if any(key not in RESOURCE_LABELS for key in keys):
            raise ValueError('资源列表包含未知资源')
        self._resources = tuple(key for key in RESOURCE_LABELS if key in keys)
        for key, row in self.resource_rows.items():
            if key in self._resources:
                row.grid()
            else:
                row.grid_remove()

    def set_enabled(self, enabled: bool) -> None:
        if type(enabled) is not bool:
            raise ValueError('启用状态必须为开关值')
        self._enabled = enabled
        self._sync_enabled()

    def set_capabilities(self, capabilities: dict) -> None:
        """Disable unsupported actions while retaining their saved values.

        Capability metadata describes the available execution ports. It never
        grants or removes resource permission and does not trigger on_change.
        Older callers that omit this method retain the original switch behavior.
        """
        if not isinstance(capabilities, dict) or any(
                key not in _SWITCHES or type(value) is not bool
                for key, value in capabilities.items()):
            raise ValueError('购买、加速和补足能力必须为开关值')
        self._capabilities = {key: capabilities.get(key, False) for key in _SWITCHES}
        self._sync_enabled()

    def _sync_enabled(self) -> None:
        state = 'normal' if self._enabled else 'disabled'
        for key, check in self.resource_checks.items():
            check.configure(state=state)
            self.limit_entries[key].configure(
                state='normal' if self._enabled and self.allowed_vars[key].get() else 'disabled')
        for key, check in self.switch_checks.items():
            supported = self._capabilities[key]
            check.configure(state=state if supported else 'disabled',
                            text=_SWITCHES[key] if supported else _UNSUPPORTED_SWITCHES[key])

    def _commit(self, _event=None) -> None:
        if self._loading or not self._enabled:
            return
        self._sync_enabled()
        try:
            value = self.get_value()
        except ValueError as exc:
            self.error_label.configure(text=str(exc))
            return
        self.error_label.configure(text='')
        if value != self._last_value:
            self._last_value = copy.deepcopy(value)
            if self.on_change is not None:
                self.on_change(copy.deepcopy(value))
