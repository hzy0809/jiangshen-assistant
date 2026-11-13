"""Installed-launcher login, with exact ownership and automatic session cleanup.

This module neither reads profile storage nor authenticates against a network
endpoint. The caller supplies credentials in memory, and the only credential
transport is the private standard-input pipe of ``launcher_uia.ps1``. That
helper writes only a fixed, credential-free result vocabulary.

``begin``/``pause``/``resume``/``stop``/``cleanup`` enqueue work; ``poll`` is a
memory-only snapshot suitable for Tk's ``after`` callback. Call ``resume`` only
after a person has resolved the displayed reason. A shared authenticated page
needs ``human_login_confirmed=True``; a person launching a server needs
``manual_server_confirmed=True`` as well. Neither attestation proves the role.

GAME_READY is provisional: bind the returned HWND/PID/creation identity and
independently compare the actual character ID and server before running tasks.
There is no role inference, token replay, direct js.exe launch, coordinate
fallback or automatic retry of an uncertain login/server invocation. Existing
installed game/login sessions are closed automatically before starting a new
launcher. They are never adopted as the newly authenticated game.

``stop(close_owned=False)`` deliberately leaves live owned processes running.
Cleanup requires ``cleanup_confirmed=True`` after game/bridge workers release
ownership. It uses normal close followed by a bounded exact-handle termination
fallback; an exit must be observed before cleanup is reported complete.
Keep the driver alive until cleanup or the explicit handoff is complete.
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
from dataclasses import dataclass, replace
import hashlib
import json
import ntpath
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import uuid


__all__ = ["LauncherLoginDriver", "LauncherLoginError", "LauncherProcess", "LauncherStatus"]

_TABS = frozenset(("tab_nav_web", "tab_nav_sd", "tab_nav_mobile"))
_TITLE = "将神登陆器"
_MESSAGES = {
    "STARTING": "正在检查已有会话并启动正常登录器。",
    "EXISTING_SESSION": "正在自动关闭已核实的已有游戏和登录器，确认退出后继续登录。",
    "FOREIGN_SESSION": "出现无法证明归属的游戏或登录器，已停止本次登录；未操作身份不明的进程。",
    "SESSION_CLEANUP_FAILED": "已有会话的自动退出未确认，本次登录未继续；详细身份和退出凭据已保留。",
    "LAUNCHER_LOADING": "等待本次启动的登录器窗口。",
    "LAUNCHER_EXITED": "本次启动的登录器已退出。请在系统界面处理启动问题后重新开始。",
    "LAUNCHER_WINDOW": "无法唯一确认本次登录器窗口。请在界面处理多余窗口后继续。",
    "PAGE_LOADING": "等待登录器中的正常登录页面。",
    "SHARED_LOGIN": "登录器已显示共享登录状态。请本人在登录器确认或更换账号，再确认登录已由本人处理后继续。",
    "LOGOUT_SUBMITTING": "正在退出登录器中已有的账号。",
    "LOGOUT_PENDING": "等待登录器退出已有账号。",
    "LOGOUT_UNCERTAIN": "无法确认已有账号是否退出；不会重复退出或提交登录。",
    "CAPTCHA": "登录器要求验证码。请本人在正常登录器完成验证码和登录，再确认登录已由本人处理后继续。",
    "HUMAN_LOGIN": "请本人在正常登录器完成登录，再确认登录已由本人处理后继续。",
    "LOGIN_SUBMITTING": "正在通过正常登录表单提交一次登录。",
    "LOGIN_PENDING": "等待正常登录表单的登录结果。",
    "LOGIN_UNCERTAIN": "本次登录提交的结果无法确认；不会再次提交。请本人在正常登录器处理后继续。",
    "SELECTING_SERVER": "正在正常登录器选择指定服务器。",
    "SERVER_TAB_REQUIRED": "指定服务器未在当前页面显示。请在登录器显示该服务器，或明确选择服务器分类后继续。",
    "SERVER_NOT_VISIBLE": "指定服务器链接未唯一显示。请本人在登录器显示准确服务器后继续。",
    "SERVER_UNCERTAIN": "服务器启动操作的结果无法确认；不会重复点击。请本人确认或在登录器启动准确服务器后继续。",
    "GAME_LOADING": "等待本次登录器启动的游戏窗口。",
    "GAME_NOT_STARTED": "尚未确认本次登录器启动的游戏。请本人在登录器检查后继续。",
    "UNEXPECTED_GAME": "本次登录器已启动游戏，但账号或服务器选择未经确认。请本人确认正常登录和准确服务器后继续。",
    "GAME_WINDOW": "无法唯一确认本次游戏主窗口。请本人在界面处理窗口后继续。",
    "GAME_READY": "已取得本次启动的游戏窗口身份；必须先核对实际角色 ID 和服务器。",
    "GAME_EXITED": "本次游戏已退出；不会自动重新登录或切换服务器。",
    "PAUSING": "正在暂停；已发出的界面调用可能正在完成。",
    "PAUSED": "已暂停，不会发起新的登录或服务器选择。",
    "STOPPING": "正在停止登录自动化；已发出的界面调用可能正在完成。",
    "STOPPED": "已停止登录自动化。",
    "PROCESSES_RETAINED": "已停止自动化，保留本次启动且仍运行的窗口。",
    "CLEANUP_CONFIRMATION": "已停止自动化。请控制器确认游戏任务和连接清理完成后再关闭本次窗口。",
    "CLOSING": "正在正常关闭本次拥有的窗口并确认退出。",
    "CLOSE_WINDOW_UNAVAILABLE": "本次进程没有可唯一确认的正常关闭窗口。请本人在系统界面关闭后再次清理。",
    "CLOSE_NOT_CONFIRMED": "尚未确认本次窗口退出。请本人处理关闭提示后再次清理；助手不会强制结束进程。",
    "AUXILIARY_PROCESS_RETAINED": "本次启动的辅助进程仍运行；请本人处理后再次清理，助手不会强制结束它。",
    "CLEANED": "本次拥有的进程已确认退出。",
    "UNSUPPORTED_PLATFORM": "正常登录驱动仅支持 Windows。",
    "INSTALLATION_UNAVAILABLE": "正常登录器或游戏安装文件不可用。请在系统界面修复安装后重试。",
    "IDENTITY_UNAVAILABLE": "无法确认进程路径和创建身份；自动化已停止，请本人处理窗口。",
    "UNEXPECTED_PROCESS_PATH": "本次进程路径不符合已指定的安装路径；自动化已停止，请本人处理窗口。",
    "ORIGIN_UNVERIFIED": "登录器未公开可核实的官方网站地址。请本人通过正常登录器完成登录和准确服务器选择，再确认后继续。",
    "FORM_UNSUPPORTED": "正常登录表单未公开所需的安全自动化控件。请本人在正常登录器完成登录后继续。",
    "PATTERN_UNSUPPORTED": "登录器控件不支持所需的标准自动化操作。请本人在正常登录器完成当前步骤后继续。",
    "AUTOMATION_UNAVAILABLE": "登录器标准自动化暂不可用。请本人在正常登录器处理当前步骤后继续。",
    "HELPER_SETUP_FAILED": "登录器辅助组件初始化失败；本次界面操作未执行。",
    "HELPER_COMPILE_FAILED": "登录器辅助组件编译失败；本次界面操作未执行。",
    "HELPER_EXECUTE_FAILED": "登录器辅助组件未能执行；当前步骤未确认。",
    "HELPER_DISPATCH_FAILED": "登录器辅助进程启动失败；当前步骤未确认。",
    "HELPER_TIMEOUT": "登录器界面调用未及时返回。不会重复提交登录或启动服务器；请本人确认界面后继续。",
    "HELPER_PROTOCOL": "登录器辅助进程未返回可确认的结果；请本人处理当前界面后继续。",
    "HELPER_NOT_EXITED": "尚未确认本次界面辅助进程退出；不会继续登录、选服或关闭游戏。请本人处理后再次停止自动化。",
    "DRIVER_UNAVAILABLE": "正常登录驱动无法继续；已停止新操作，请本人处理本次窗口。",
}


class LauncherLoginError(RuntimeError):
    """A fixed message; input and underlying exceptions must never be attached."""


@dataclass(frozen=True)
class LauncherProcess:
    pid: int
    creation_time: int
    exe_path: str
    hwnd: int = 0
    tid: int = 0
    kind: str = "child"

    def as_dict(self) -> dict:
        return {"pid": self.pid, "process_creation_filetime": self.creation_time,
                "exe_path": self.exe_path, "hwnd": self.hwnd, "tid": self.tid,
                "kind": self.kind}


@dataclass(frozen=True)
class LauncherStatus:
    state: str = "IDLE"
    code: str = "IDLE"
    message: str = "尚未开始正常登录。"
    operation_id: str | None = None
    launcher: LauncherProcess | None = None
    game: LauncherProcess | None = None
    expected_server: str | None = None
    server_tab: str | None = None
    login_provisional: bool = False
    server_selection: str = "NONE"
    needs_role_binding: bool = False
    human_required: bool = False
    cleanup_complete: bool = True
    owned_processes: tuple[LauncherProcess, ...] = ()
    session_cleanup: object | None = None

    def as_dict(self) -> dict:
        # Explicit whitelist: adding an internal field cannot expose it here.
        return {"state": self.state, "code": self.code, "message": self.message,
                "operation_id": self.operation_id,
                "launcher": self.launcher.as_dict() if self.launcher else None,
                "game": self.game.as_dict() if self.game else None,
                "expected_server": self.expected_server, "server_tab": self.server_tab,
                "login_provisional": self.login_provisional,
                "server_selection": self.server_selection,
                "needs_role_binding": self.needs_role_binding,
                "human_required": self.human_required,
                "cleanup_complete": self.cleanup_complete,
                "owned_processes": [item.as_dict() for item in self.owned_processes],
                "session_cleanup": self.session_cleanup.as_dict() if self.session_cleanup else None}


class _Cancelled(Exception):
    pass


class _HelperUnreaped(Exception):
    pass


class _Unavailable(Exception):
    def __init__(self, code: str = "IDENTITY_UNAVAILABLE") -> None:
        self.code = code
        super().__init__(code)


@dataclass
class _ProcessRecord:
    identity: LauncherProcess
    handle: object
    parent_pid: int


def _same_path(left: str | Path, right: str | Path) -> bool:
    def canonical(value):
        text = str(value)
        if text.startswith("\\\\?\\"):
            text = text[4:]
        return ntpath.normcase(ntpath.normpath(text))
    return canonical(left) == canonical(right)


class _NativeWindows:
    """Held process handles establish lifetime; snapshots only discover children."""

    def __init__(self, launcher: Path, game: Path) -> None:
        self.launcher_path, self.game_path = launcher, game
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.user = ctypes.WinDLL("user32", use_last_error=True)
        self.records: dict[tuple[int, int], _ProcessRecord] = {}
        self.unidentified: list[tuple[int, object]] = []
        self._callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        class ProcessEntry(ctypes.Structure):
            _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                        ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                        ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                        ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", wintypes.LONG),
                        ("dwFlags", wintypes.DWORD), ("szExeFile", wintypes.WCHAR * 260)]

        class StartupInfo(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("lpReserved", wintypes.LPWSTR),
                        ("lpDesktop", wintypes.LPWSTR), ("lpTitle", wintypes.LPWSTR),
                        ("dwX", wintypes.DWORD), ("dwY", wintypes.DWORD),
                        ("dwXSize", wintypes.DWORD), ("dwYSize", wintypes.DWORD),
                        ("dwXCountChars", wintypes.DWORD), ("dwYCountChars", wintypes.DWORD),
                        ("dwFillAttribute", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                        ("wShowWindow", wintypes.WORD), ("cbReserved2", wintypes.WORD),
                        ("lpReserved2", ctypes.POINTER(ctypes.c_byte)),
                        ("hStdInput", wintypes.HANDLE), ("hStdOutput", wintypes.HANDLE),
                        ("hStdError", wintypes.HANDLE)]

        class ProcessInformation(ctypes.Structure):
            _fields_ = [("hProcess", wintypes.HANDLE), ("hThread", wintypes.HANDLE),
                        ("dwProcessId", wintypes.DWORD), ("dwThreadId", wintypes.DWORD)]

        class SecurityAttributes(ctypes.Structure):
            _fields_ = [("nLength", wintypes.DWORD), ("lpSecurityDescriptor", ctypes.c_void_p),
                        ("bInheritHandle", wintypes.BOOL)]

        self.ProcessEntry, self.StartupInfo = ProcessEntry, StartupInfo
        self.ProcessInformation, self.SecurityAttributes = ProcessInformation, SecurityAttributes
        declarations = (
            (self.kernel, "CreateToolhelp32Snapshot", [wintypes.DWORD, wintypes.DWORD], wintypes.HANDLE),
            (self.kernel, "Process32FirstW", [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)], wintypes.BOOL),
            (self.kernel, "Process32NextW", [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)], wintypes.BOOL),
            (self.kernel, "OpenProcess", [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            (self.kernel, "CloseHandle", [wintypes.HANDLE], wintypes.BOOL),
            (self.kernel, "GetProcessTimes", [wintypes.HANDLE, *([ctypes.POINTER(wintypes.FILETIME)] * 4)], wintypes.BOOL),
            (self.kernel, "GetSystemTimeAsFileTime", [ctypes.POINTER(wintypes.FILETIME)], None),
            (self.kernel, "QueryFullProcessImageNameW", [wintypes.HANDLE, wintypes.DWORD,
                                                       wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)], wintypes.BOOL),
            (self.kernel, "WaitForSingleObject", [wintypes.HANDLE, wintypes.DWORD], wintypes.DWORD),
            (self.kernel, "CreateProcessW", [wintypes.LPCWSTR, wintypes.LPWSTR, ctypes.c_void_p,
                                            ctypes.c_void_p, wintypes.BOOL, wintypes.DWORD,
                                            ctypes.c_void_p, wintypes.LPCWSTR, ctypes.POINTER(StartupInfo),
                                            ctypes.POINTER(ProcessInformation)], wintypes.BOOL),
            (self.kernel, "CreateEventW", [ctypes.POINTER(SecurityAttributes), wintypes.BOOL,
                                          wintypes.BOOL, wintypes.LPCWSTR], wintypes.HANDLE),
            (self.kernel, "SetEvent", [wintypes.HANDLE], wintypes.BOOL),
            (self.user, "EnumWindows", [self._callback_type, wintypes.LPARAM], wintypes.BOOL),
            (self.user, "IsWindowVisible", [wintypes.HWND], wintypes.BOOL),
            (self.user, "IsWindow", [wintypes.HWND], wintypes.BOOL),
            (self.user, "GetWindow", [wintypes.HWND, wintypes.UINT], wintypes.HWND),
            (self.user, "GetWindowThreadProcessId", [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)], wintypes.DWORD),
            (self.user, "GetWindowTextW", [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int], ctypes.c_int),
            (self.user, "GetClientRect", [wintypes.HWND, ctypes.POINTER(wintypes.RECT)], wintypes.BOOL),
            (self.user, "PostMessageW", [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM], wintypes.BOOL),
        )
        for library, name, arguments, result in declarations:
            function = getattr(library, name)
            function.argtypes, function.restype = arguments, result

    @staticmethod
    def _filetime(value) -> int:
        return (int(value.dwHighDateTime) << 32) | int(value.dwLowDateTime)

    def times(self, record: _ProcessRecord) -> tuple[int, int]:
        values = [wintypes.FILETIME() for _ in range(4)]
        if not self.kernel.GetProcessTimes(record.handle, *(ctypes.byref(item) for item in values)):
            raise _Unavailable()
        return self._filetime(values[0]), self._filetime(values[1])

    def alive(self, record: _ProcessRecord) -> bool:
        state = self.kernel.WaitForSingleObject(record.handle, 0)
        if state not in (0, 258):
            raise _Unavailable()
        return state == 258

    def snapshot(self) -> tuple[int, list[tuple[int, int, str]]]:
        stamp = wintypes.FILETIME()
        self.kernel.GetSystemTimeAsFileTime(ctypes.byref(stamp))
        handle = self.kernel.CreateToolhelp32Snapshot(2, 0)
        if not handle or handle == ctypes.c_void_p(-1).value:
            raise _Unavailable()
        rows = []
        try:
            entry = self.ProcessEntry()
            entry.dwSize = ctypes.sizeof(entry)
            if not self.kernel.Process32FirstW(handle, ctypes.byref(entry)):
                raise _Unavailable()
            while True:
                rows.append((int(entry.th32ProcessID), int(entry.th32ParentProcessID), entry.szExeFile))
                if not self.kernel.Process32NextW(handle, ctypes.byref(entry)):
                    if ctypes.get_last_error() != 18:  # ERROR_NO_MORE_FILES
                        raise _Unavailable()
                    break
            return self._filetime(stamp), rows
        finally:
            self.kernel.CloseHandle(handle)

    def _record(self, pid: int, handle, parent: int) -> _ProcessRecord:
        provisional = _ProcessRecord(LauncherProcess(pid, 0, ""), handle, parent)
        creation, _ = self.times(provisional)
        buffer, size = ctypes.create_unicode_buffer(32768), wintypes.DWORD(32768)
        if not self.kernel.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            raise _Unavailable()
        path = buffer.value
        kind = ("launcher" if _same_path(path, self.launcher_path) else
                "game" if _same_path(path, self.game_path) else "child")
        # The game can reopen the installed launcher from a second local
        # path on normal exit. Only consider that path for a direct child of
        # an already held game process; discover() still verifies lifetimes.
        if (kind == "child" and ntpath.basename(path).casefold() == self.launcher_path.name.casefold()
                and any(record.identity.pid == parent and record.identity.kind == "game"
                        for record in self.records.values())):
            try:
                if (hashlib.sha256(Path(path).read_bytes()).digest() ==
                        hashlib.sha256(self.launcher_path.read_bytes()).digest()):
                    kind = "launcher"
            except OSError:
                raise _Unavailable("IDENTITY_UNAVAILABLE") from None
        return _ProcessRecord(LauncherProcess(pid, creation, path, kind=kind), handle, parent)

    def start(self) -> _ProcessRecord:
        startup, info = self.StartupInfo(), self.ProcessInformation()
        startup.cb = ctypes.sizeof(startup)
        command = ctypes.create_unicode_buffer('"' + str(self.launcher_path) + '"')
        if not self.kernel.CreateProcessW(str(self.launcher_path), command, None, None, False,
                                         0, None, str(self.launcher_path.parent),
                                         ctypes.byref(startup), ctypes.byref(info)):
            raise _Unavailable("INSTALLATION_UNAVAILABLE")
        self.kernel.CloseHandle(info.hThread)
        self.unidentified.append((int(info.dwProcessId), info.hProcess))
        record = self._record(int(info.dwProcessId), info.hProcess, os.getpid())
        self.records[(record.identity.pid, record.identity.creation_time)] = record
        self.unidentified.remove((int(info.dwProcessId), info.hProcess))
        if record.identity.kind != "launcher":
            raise _Unavailable("UNEXPECTED_PROCESS_PATH")
        return record

    def discover(self) -> bool:
        """False means a relevant foreign process exists; never adopt it.

        A child must have been created during a held parent's exact lifetime.
        The pre-snapshot time also rejects a PID reused after the snapshot row
        was captured. Missing short-lived intermediates remain unowned.
        """
        stamp, rows = self.snapshot()
        parents = {item.identity.pid: item for item in self.records.values()}
        pending = list(rows)
        progress = True
        while progress:
            progress = False
            for row in pending[:]:
                pid, parent_pid, name = row
                parent = parents.get(parent_pid)
                if parent is None or pid in parents:
                    continue
                handle = self.kernel.OpenProcess(0x101000, False, pid)
                if not handle:
                    # An exited discovery row cannot establish ownership.
                    if ctypes.get_last_error() == 87:
                        pending.remove(row)
                        continue
                    raise _Unavailable()
                keep = False
                try:
                    candidate = self._record(pid, handle, parent_pid)
                    if candidate.identity.creation_time > stamp:
                        raise _Unavailable("AUTOMATION_UNAVAILABLE")
                    if ntpath.basename(candidate.identity.exe_path).casefold() != name.casefold():
                        raise _Unavailable("AUTOMATION_UNAVAILABLE")
                    born, died = self.times(parent)
                    child_born = candidate.identity.creation_time
                    if born <= child_born and (not died or child_born <= died):
                        key = (pid, child_born)
                        self.records[key] = candidate
                        parents[pid] = candidate
                        keep, progress = True, True
                finally:
                    if not keep:
                        self.kernel.CloseHandle(handle)
                pending.remove(row)
        names = {self.launcher_path.name.casefold(), self.game_path.name.casefold()}
        for pid, parent, name in rows:
            if name.casefold() not in names:
                continue
            owned = parents.get(pid)
            if owned is None:
                handle = self.kernel.OpenProcess(0x101000, False, pid)
                if not handle:
                    raise _Unavailable()
                try:
                    candidate = self._record(pid, handle, parent)
                    if candidate.identity.creation_time > stamp:
                        raise _Unavailable()
                    if (not _same_path(candidate.identity.exe_path, self.launcher_path)
                            and not _same_path(candidate.identity.exe_path, self.game_path)):
                        continue
                finally:
                    self.kernel.CloseHandle(handle)
                return False
            if not self.alive(owned):
                return False
            if owned.identity.kind not in ("launcher", "game"):
                raise _Unavailable("UNEXPECTED_PROCESS_PATH")
        return True

    def windows(self, record: _ProcessRecord) -> list[LauncherProcess]:
        if not self.alive(record):
            return []
        result = []

        @self._callback_type
        def visit(hwnd, _):
            if not self.user.IsWindowVisible(hwnd) or self.user.GetWindow(hwnd, 4):
                return True  # GW_OWNER: modal/owned windows are not main windows.
            pid, rect = wintypes.DWORD(), wintypes.RECT()
            tid = self.user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value != record.identity.pid or not tid:
                return True
            if not self.user.GetClientRect(hwnd, ctypes.byref(rect)) or rect.right <= 0 or rect.bottom <= 0:
                return True
            if record.identity.kind == "launcher":
                title = ctypes.create_unicode_buffer(256)
                self.user.GetWindowTextW(hwnd, title, len(title))
                if title.value != _TITLE:
                    return True
            result.append(replace(record.identity, hwnd=int(hwnd), tid=int(tid)))
            return True

        if not self.user.EnumWindows(visit, 0):
            raise _Unavailable()
        return result if self.alive(record) else []

    def close_window(self, identity: LauncherProcess) -> None:
        record = self.records.get((identity.pid, identity.creation_time))
        if record is None or record.identity.kind not in ("launcher", "game") or not identity.hwnd:
            raise _Unavailable()
        if not self.alive(record):
            return
        pid = wintypes.DWORD()
        tid = self.user.GetWindowThreadProcessId(identity.hwnd, ctypes.byref(pid))
        if (not self.user.IsWindow(identity.hwnd) or pid.value != identity.pid or tid != identity.tid
                or self.times(record)[0] != identity.creation_time):
            raise _Unavailable()
        if not self.user.PostMessageW(identity.hwnd, 0x0010, 0, 0):  # WM_CLOSE, never force.
            raise _Unavailable()

    def cancel_event(self):
        attributes = self.SecurityAttributes(ctypes.sizeof(self.SecurityAttributes), None, True)
        handle = self.kernel.CreateEventW(ctypes.byref(attributes), True, False, None)
        if not handle:
            raise _Unavailable()
        return handle

    def close_handles(self) -> None:
        for record in self.records.values():
            self.kernel.CloseHandle(record.handle)
        for _, handle in self.unidentified:
            self.kernel.CloseHandle(handle)
        self.records.clear()
        self.unidentified.clear()

    def __del__(self):
        try:
            self.close_handles()
        except Exception:
            pass


class LauncherLoginDriver:
    """One serial login operation; all UI/process work runs on its worker."""

    def __init__(self, launcher_path: str | Path | None = None,
                 game_path: str | Path | None = None, *, helper_timeout: float = 12.0,
                 launch_timeout: float = 35.0, login_timeout: float = 60.0,
                 game_timeout: float = 75.0, close_timeout: float = 20.0,
                 diagnostics_dir: str | Path | None = None) -> None:
        install = Path(os.environ.get("LOCALAPPDATA", "")) / "JiangShen"
        self._launcher_path = Path(launcher_path) if launcher_path is not None else install / "launcher" / "launcher" / "Launcher.exe"
        self._game_path = Path(game_path) if game_path is not None else install / "pc" / "js.exe"
        self._helper_path = Path(__file__).with_name("launcher_msaa.ps1")
        self._helper_timeout, self._launch_timeout = helper_timeout, launch_timeout
        self._login_timeout, self._game_timeout, self._close_timeout = login_timeout, game_timeout, close_timeout
        self._diagnostics_dir = diagnostics_dir
        self._session_closer = None
        self._lock, self._wake = threading.RLock(), threading.Event()
        self._status = LauncherStatus()
        self._thread: threading.Thread | None = None
        self._worker_terminal = False
        self._native: _NativeWindows | None = None
        self._helper = None
        self._cancel_handle = None
        self._credentials: list[str] | None = None
        self._pause_requested = self._stop_requested = self._close_requested = False
        self._cleanup_confirmed = self._waiting = False
        self._human_login = self._human_server = False
        self._root: _ProcessRecord | None = None
        self._login_attempted = self._server_attempted = False
        self._logout_attempted = self._logout_submitted = False
        self._login_submitted = False
        self._picker_attempted = self._tab_attempted = False
        self._closed: set[tuple[int, int]] = set()
        self._deadline = 0.0
        self._origin_pending, self._origin_deadline = False, 0.0
        self._stage = "preflight"

    def poll(self) -> LauncherStatus:
        with self._lock:
            return self._status

    def begin(self, *, login_name: str, password: str, server_name: str,
              server_tab: str | None = None) -> LauncherStatus:
        """Start once without blocking Tk. Never include inputs in exceptions."""
        if (type(login_name) is not str or not login_name or len(login_name) > 512
                or type(password) is not str or not password or len(password) > 4096
                or type(server_name) is not str or not server_name.strip() or len(server_name) > 120
                or any(character in server_name for character in "\r\n\0")
                or "\0" in login_name or "\0" in password
                or (server_tab is not None and (type(server_tab) is not str or server_tab not in _TABS))):
            raise LauncherLoginError("登录请求不完整或字段不受支持。") from None
        with self._lock:
            if self._thread is not None and self._thread.is_alive() and not self._worker_terminal:
                raise LauncherLoginError("已有正常登录操作；请先停止并完成本次清理。") from None
            if self._native is not None:
                raise LauncherLoginError("本次进程所有权尚未清理；请先完成清理或保留当前驱动。") from None
            self._credentials = [login_name, password]
            self._pause_requested = self._stop_requested = self._close_requested = False
            self._cleanup_confirmed = self._waiting = False
            self._human_login = self._human_server = False
            self._root = None
            self._login_attempted = self._server_attempted = False
            self._logout_attempted = self._logout_submitted = False
            self._login_submitted = False
            self._picker_attempted = self._tab_attempted = False
            self._closed.clear()
            self._stage, self._deadline = "preflight", 0.0
            self._origin_pending, self._origin_deadline = False, 0.0
            self._status = LauncherStatus(state="STARTING", code="STARTING", message=_MESSAGES["STARTING"],
                                          operation_id=str(uuid.uuid4()), expected_server=server_name,
                                          server_tab=server_tab)
            self._start_worker()
            return self._status

    def pause(self) -> LauncherStatus:
        with self._lock:
            if self._thread is None or not self._thread.is_alive() or self._worker_terminal or self._stop_requested:
                return self._status
            self._pause_requested = True
            if self._session_closer is not None:
                self._session_closer.pause()
            self._cancel_pending()
            self._set("PAUSING", "PAUSING")
            self._wake.set()
            return self._status

    def resume(self, *, human_login_confirmed: bool = False,
               manual_server_confirmed: bool = False) -> LauncherStatus:
        """A person, rather than an automatic retry loop, resolves WAITING."""
        if type(human_login_confirmed) is not bool or type(manual_server_confirmed) is not bool:
            raise LauncherLoginError("人工确认开关必须为布尔值。") from None
        with self._lock:
            if self._thread is None or not self._thread.is_alive() or self._worker_terminal or self._stop_requested:
                return self._status
            self._human_login |= human_login_confirmed
            self._human_server |= manual_server_confirmed
            if human_login_confirmed:
                self._forget_credentials()
            self._pause_requested = self._waiting = False
            if self._session_closer is not None:
                self._session_closer.resume()
            self._deadline = time.monotonic() + (self._game_timeout if self._server_attempted else self._login_timeout)
            self._set("WAITING_GAME" if self._server_attempted else "STARTING",
                      "GAME_LOADING" if self._server_attempted else "PAGE_LOADING", human_required=False)
            self._wake.set()
            return self._status

    def stop(self, *, close_owned: bool = False, cleanup_confirmed: bool = False) -> LauncherStatus:
        if type(close_owned) is not bool or type(cleanup_confirmed) is not bool:
            raise LauncherLoginError("停止和清理开关必须为布尔值。") from None
        with self._lock:
            self._forget_credentials()
            self._stop_requested, self._pause_requested, self._waiting = True, False, False
            self._close_requested, self._cleanup_confirmed = close_owned, cleanup_confirmed
            if self._session_closer is not None and not (close_owned and cleanup_confirmed):
                self._session_closer.stop()
            elif self._session_closer is not None:
                self._session_closer.resume()
            self._cancel_pending()
            self._set("STOPPING", "STOPPING")
            if self._thread is None or not self._thread.is_alive() or self._worker_terminal:
                self._start_worker()
            self._wake.set()
            return self._status

    def cleanup(self, *, cleanup_confirmed: bool) -> LauncherStatus:
        """After controller cleanup, request normal close and observed exit."""
        return self.stop(close_owned=True, cleanup_confirmed=cleanup_confirmed)

    def cleanup_failed_session(self) -> LauncherStatus:
        """Close owned processes after workers stop when bridge state is unknown.

        The controller must retain the unknown task result and ensure that no
        worker still owns the connection. Only observed process exit completes
        this cleanup; it does not establish the result of a game operation.
        """
        return self.stop(close_owned=True, cleanup_confirmed=True)

    def _start_worker(self) -> None:
        self._worker_terminal = False
        self._thread = threading.Thread(target=self._run, name="jiangshen-normal-launcher", daemon=True)
        self._thread.start()

    def _set(self, state: str, code: str, **fields) -> None:
        with self._lock:
            self._status = replace(self._status, state=state, code=code,
                                   message=_MESSAGES.get(code, _MESSAGES["DRIVER_UNAVAILABLE"]), **fields)

    def _wait_human(self, code: str, *, blocked: bool = False) -> None:
        with self._lock:
            self._waiting = True
            self._set("BLOCKED" if blocked else "WAITING", code, human_required=True)

    def _forget_credentials(self) -> None:
        with self._lock:
            if self._credentials is not None:
                self._credentials[:] = ["", ""]
                self._credentials = None

    def _cancel_pending(self) -> None:
        if self._native is not None and self._cancel_handle is not None:
            self._native.kernel.SetEvent(self._cancel_handle)

    def _gate(self) -> None:
        # The worker and request methods share this gate. PAUSED is acknowledged
        # only after any previous helper has returned or has been terminated.
        if self._pause_requested or self._stop_requested:
            raise _Cancelled()

    def _run(self) -> None:
        try:
            while True:
                with self._lock:
                    stopping, paused, waiting = self._stop_requested, self._pause_requested, self._waiting
                if stopping:
                    self._finish_stop()
                    return
                if paused:
                    self._set("PAUSED", "PAUSED", human_required=False)
                elif not waiting:
                    try:
                        self._advance()
                    except _Cancelled:
                        pass
                    except _Unavailable as error:
                        if error.code == "FOREIGN_SESSION":
                            self._forget_credentials()
                            self._set("BLOCKED", error.code, human_required=False)
                            self._waiting = True
                        else:
                            self._wait_human(error.code)
                self._wake.wait(0.6)
                self._wake.clear()
        except _HelperUnreaped:
            with self._lock:
                self._forget_credentials()
                self._waiting = self._worker_terminal = True
                self._set("BLOCKED", "HELPER_NOT_EXITED", human_required=True)
        except Exception:
            # No exception text/traceback may reach status or diagnostics.
            with self._lock:
                self._forget_credentials()
                self._waiting = True
                self._worker_terminal = True
                self._set("BLOCKED", "DRIVER_UNAVAILABLE", human_required=True)

    def _refresh(self) -> tuple[list[_ProcessRecord], list[_ProcessRecord]]:
        native = self._native
        if native is None:
            raise _Unavailable()
        if not native.discover():
            raise _Unavailable("FOREIGN_SESSION")
        alive = [record for record in native.records.values() if native.alive(record)]
        launchers = [record for record in alive if record.identity.kind == "launcher"]
        games = [record for record in alive if record.identity.kind == "game"]
        with self._lock:
            self._status = replace(self._status, owned_processes=tuple(record.identity for record in alive),
                                   cleanup_complete=not alive and not native.unidentified)
        return launchers, games

    def _advance(self) -> None:
        if self._stage == "preflight":
            if sys.platform != "win32":
                self._forget_credentials()
                self._wait_human("UNSUPPORTED_PLATFORM", blocked=True)
                return
            if (not self._launcher_path.is_absolute() or not self._game_path.is_absolute()
                    or self._launcher_path.name.casefold() != "launcher.exe"
                    or self._game_path.name.casefold() != "js.exe"
                    or not self._launcher_path.is_file() or not self._game_path.is_file()
                    or not self._helper_path.is_file()):
                self._wait_human("INSTALLATION_UNAVAILABLE", blocked=True)
                return
            from .account_session_cleanup import AutomaticSessionCloser
            self._session_closer = AutomaticSessionCloser(
                self._launcher_path, self._game_path, diagnostics_dir=self._diagnostics_dir,
                close_timeout=min(10.0, self._close_timeout))
            with self._lock:
                self._gate()
                self._session_closer.begin()
                self._stage = "preflight_cleanup"
                self._set("STARTING", "EXISTING_SESSION", cleanup_complete=False, human_required=False)
            return

        if self._stage == "preflight_cleanup":
            cleanup = self._session_closer.poll()
            with self._lock:
                self._status = replace(self._status, session_cleanup=cleanup)
            if cleanup.observing:
                return
            if cleanup.state != "CLOSED" or not cleanup.complete:
                self._forget_credentials()
                self._waiting = True
                self._set("BLOCKED", "SESSION_CLEANUP_FAILED", cleanup_complete=False, human_required=False)
                return
            self._stage = "launch"

        if self._stage == "launch":
            if self._native is None:
                self._native = _NativeWindows(self._launcher_path, self._game_path)
            if not self._native.discover():
                raise _Unavailable("FOREIGN_SESSION")
            with self._lock:
                self._gate()
                self._status = replace(self._status, cleanup_complete=False)
            # This is an in-flight launch until the worker acknowledges pause
            # or stop. Do not hold the snapshot lock across OS process startup.
            self._root = self._native.start()
            with self._lock:
                self._status = replace(self._status, launcher=self._root.identity,
                                       owned_processes=(self._root.identity,), cleanup_complete=False)
            self._stage, self._deadline = "page", time.monotonic() + self._launch_timeout
            self._origin_pending, self._origin_deadline = True, self._deadline
            self._set("STARTING", "LAUNCHER_LOADING", human_required=False)
            return

        launchers, games = self._refresh()
        if len(games) > 1:
            self._wait_human("GAME_WINDOW")
            return
        if games:
            windows = self._native.windows(games[0])
            if len(windows) != 1:
                if time.monotonic() < self._deadline and not windows:
                    self._set("WAITING_GAME", "GAME_LOADING", human_required=False)
                else:
                    self._wait_human("GAME_WINDOW")
                return
            game = windows[0]
            with self._lock:
                self._status = replace(self._status, game=game, cleanup_complete=False)
            if (self._status.server_selection != "UI_EXACT" and not self._human_server
                    or not self._status.login_provisional and not self._human_login):
                self._wait_human("UNEXPECTED_GAME")
                return
            self._forget_credentials()
            self._stage = "ready"
            selection = self._status.server_selection if self._status.server_selection == "UI_EXACT" else "HUMAN_CONFIRMED"
            self._set("GAME_READY", "GAME_READY", game=game, login_provisional=True,
                      server_selection=selection, needs_role_binding=True, human_required=False)
            return
        if self._stage == "ready":
            self._wait_human("GAME_EXITED")
            return
        if self._server_attempted:
            if time.monotonic() >= self._deadline:
                self._wait_human("GAME_NOT_STARTED")
            else:
                self._set("WAITING_GAME", "GAME_LOADING", human_required=False)
            return
        candidates = [window for record in launchers for window in self._native.windows(record)]
        if len(candidates) != 1:
            if not launchers and self._root is not None and not self._native.alive(self._root):
                self._forget_credentials()
                self._wait_human("LAUNCHER_EXITED")
            elif time.monotonic() < self._deadline and not candidates:
                self._set("STARTING", "LAUNCHER_LOADING", human_required=False)
            else:
                self._wait_human("LAUNCHER_WINDOW")
            return
        launcher = candidates[0]
        with self._lock:
            self._status = replace(self._status, launcher=launcher)
        page = self._rpc("observe", launcher)
        code = page["code"]
        with self._lock:
            self._gate()
            if (code == "ORIGIN_UNVERIFIED" and self._origin_pending
                    and self._stage == "page"
                    and not (self._login_attempted or self._logout_attempted or self._server_attempted
                             or self._picker_attempted or self._tab_attempted
                             or self._human_login or self._human_server)
                    and time.monotonic() < self._origin_deadline):
                # The browser/document pane may not exist yet. Only repeat the
                # credential-free observation; this never authorizes an action.
                # Keep the original startup deadline even after pause/resume.
                self._set("STARTING", "PAGE_LOADING", human_required=False)
                return
            # A resolved observation or expiry ends startup grace permanently.
            # Subsequent origin failures require the existing human intervention.
            self._origin_pending = False
        if (code == "SHARED_LOGIN" or code == "OK" and page["authenticated"]) and not self._login_submitted and not self._human_login:
            if not self._logout_attempted and self._credentials is not None:
                with self._lock:
                    self._gate()
                    self._logout_attempted = True
                self._set("LOGOUT_SUBMITTING", "LOGOUT_SUBMITTING", human_required=False)
                result = self._rpc("logout", launcher)
                self._deadline = time.monotonic() + self._login_timeout
                if result["code"] == "LOGOUT_SUBMITTED":
                    self._logout_submitted = True
                    self._set("WAITING_LOGOUT", "LOGOUT_PENDING", human_required=False)
                else:
                    self._forget_credentials()
                    self._wait_human(result["code"])
            elif self._logout_submitted and time.monotonic() < self._deadline:
                self._set("WAITING_LOGOUT", "LOGOUT_PENDING", human_required=False)
            else:
                self._forget_credentials()
                self._wait_human("LOGOUT_UNCERTAIN")
            return
        if code == "FORM_UNSUPPORTED" and not self._login_attempted and time.monotonic() < self._deadline:
            self._set("STARTING", "PAGE_LOADING", human_required=False)
            return
        if code != "OK":
            self._wait_human(code)
            return
        if page["captcha"]:
            self._forget_credentials()
            self._wait_human("CAPTCHA")
            return
        if not page["authenticated"]:
            if self._human_login:
                self._wait_human("HUMAN_LOGIN")
                return
            if self._login_attempted:
                if time.monotonic() >= self._deadline:
                    self._forget_credentials()
                    self._wait_human("LOGIN_UNCERTAIN")
                else:
                    self._set("WAITING_LOGIN", "LOGIN_PENDING", human_required=False)
                return
            if not page["login_form"]:
                if time.monotonic() < self._deadline:
                    self._set("STARTING", "PAGE_LOADING", human_required=False)
                else:
                    self._wait_human("FORM_UNSUPPORTED")
                return
            if self._credentials is None:
                self._wait_human("HUMAN_LOGIN")
                return
            with self._lock:
                self._gate()
                self._login_attempted = True  # Uncertain dispatch is never retried.
                secret = self._credentials
            self._set("LOGIN_SUBMITTING", "LOGIN_SUBMITTING", human_required=False)
            try:
                result = self._rpc("submit_login", launcher, secret)
            finally:
                with self._lock:
                    self._forget_credentials()
            self._deadline = time.monotonic() + self._login_timeout
            if result["code"] != "LOGIN_SUBMITTED":
                self._wait_human(result["code"])
            else:
                self._login_submitted = True
                self._set("WAITING_LOGIN", "LOGIN_PENDING", human_required=False)
            return
        if not self._login_submitted and not self._human_login:
            self._forget_credentials()
            self._wait_human("SHARED_LOGIN")
            return
        self._forget_credentials()
        with self._lock:
            self._status = replace(self._status, login_provisional=True)
        if page["server_count"] == 1:
            with self._lock:
                self._gate()
                self._server_attempted = True
            self._set("SELECTING_SERVER", "SELECTING_SERVER", human_required=False)
            result = self._rpc("launch_server", launcher)
            self._deadline = time.monotonic() + self._game_timeout
            if result["code"] == "SERVER_INVOKED":
                with self._lock:
                    self._status = replace(self._status, server_selection="UI_EXACT")
                self._set("WAITING_GAME", "GAME_LOADING", human_required=False)
            else:
                if result["code"] != "AUTOMATION_UNAVAILABLE":
                    # The helper's fixed precondition results occur before an
                    # Invoke. A later explicit human resume may try the now
                    # visible exact link; uncertain dispatch is never retried.
                    self._server_attempted = False
                self._wait_human(result["code"])
            return
        if page["server_count"] > 1:
            self._wait_human("SERVER_NOT_VISIBLE")
            return
        if not self._picker_attempted and page["picker"]:
            self._picker_attempted = True
            result = self._rpc("open_picker", launcher)
            if result["code"] != "PICKER_OPENED":
                self._wait_human(result["code"])
            return
        tab = self._status.server_tab
        if tab is not None and not self._tab_attempted:
            self._tab_attempted = True
            result = self._rpc("select_tab", launcher)
            if result["code"] != "TAB_SELECTED":
                self._wait_human(result["code"])
            return
        self._wait_human("SERVER_TAB_REQUIRED" if tab is None else "SERVER_NOT_VISIBLE")

    def _rpc(self, action: str, launcher: LauncherProcess, secret: list[str] | None = None) -> dict:
        native = self._native
        if native is None:
            raise _Unavailable()
        root = Path(os.environ.get("SystemRoot", "C:\\Windows"))
        system = "Sysnative" if ctypes.sizeof(ctypes.c_void_p) == 4 and os.environ.get("PROCESSOR_ARCHITEW6432") else "System32"
        powershell = root / system / "WindowsPowerShell" / "v1.0" / "powershell.exe"
        process, cancel, request, payload = None, None, None, None
        try:
            with self._lock:
                self._gate()
                cancel = native.cancel_event()
                startup = subprocess.STARTUPINFO()
                startup.lpAttributeList = {"handle_list": [int(cancel)]}
                request = {"action": action, "target": launcher.as_dict(),
                           "server_name": self._status.expected_server,
                           "server_tab": self._status.server_tab, "cancel_handle": int(cancel)}
                if secret is not None:
                    request["login_name"], request["password"] = secret
                payload = json.dumps(request, ensure_ascii=True, separators=(",", ":")) + "\n"
                # Publish cancellation before process startup. If a pause or
                # stop arrives while CreateProcess is in flight, the inherited
                # event is already signalled before any helper UI mutation.
                self._cancel_handle = cancel
            process = subprocess.Popen(
                [str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive", "-Sta",
                 "-ExecutionPolicy", "Bypass", "-File", str(self._helper_path)],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                text=True, encoding="utf-8", errors="strict", close_fds=True,
                startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW,
                cwd=str(self._helper_path.parent))
            with self._lock:
                self._helper = process
                if self._pause_requested or self._stop_requested:
                    native.kernel.SetEvent(cancel)
            exchanged, outcome = threading.Event(), {}

            def exchange(input_text=payload, child=process):
                # Windows communicate writes stdin synchronously before its
                # timeout wait. Keep even a blocked private pipe off the driver
                # worker so pause/stop retain a bounded cancellation path.
                try:
                    output, _ = child.communicate(input=input_text)
                    outcome["output"], outcome["ok"] = output, True
                except BaseException:
                    outcome["ok"] = False
                finally:
                    exchanged.set()

            threading.Thread(target=exchange, name="jiangshen-launcher-private-io", daemon=True).start()
            expires = time.monotonic() + self._helper_timeout
            while not exchanged.wait(0.1):
                with self._lock:
                    self._gate()
                if time.monotonic() >= expires:
                    raise _Unavailable("HELPER_TIMEOUT")
            with self._lock:
                self._gate()
            if not outcome.get("ok"):
                raise _Unavailable("HELPER_PROTOCOL")
            output = outcome.get("output")
            if process.returncode != 0 or not output or len(output) > 4096:
                raise _Unavailable("HELPER_PROTOCOL")
            try:
                result = json.loads(output)
            except (ValueError, UnicodeError, RecursionError):
                raise _Unavailable("HELPER_PROTOCOL") from None
            allowed = {"OK", "LOGIN_SUBMITTED", "LOGOUT_SUBMITTED", "SERVER_INVOKED", "PICKER_OPENED", "TAB_SELECTED",
                       "CANCELLED", "CAPTCHA", "ORIGIN_UNVERIFIED", "FORM_UNSUPPORTED",
                       "PATTERN_UNSUPPORTED", "AUTOMATION_UNAVAILABLE", "IDENTITY_UNAVAILABLE",
                       "HELPER_SETUP_FAILED", "HELPER_COMPILE_FAILED", "HELPER_EXECUTE_FAILED",
                       "SHARED_LOGIN", "HUMAN_LOGIN", "SERVER_NOT_VISIBLE"}
            if (type(result) is not dict or set(result) != {"code", "authenticated", "captcha", "login_form", "server_count", "picker"}
                    or type(result.get("code")) is not str or result["code"] not in allowed
                    or any(type(result.get(key)) is not bool for key in ("authenticated", "captcha", "login_form", "picker"))
                    or type(result.get("server_count")) is not int or not 0 <= result["server_count"] <= 2):
                raise _Unavailable("HELPER_PROTOCOL")
            if result["code"] == "CANCELLED":
                raise _Cancelled()
            return result
        except (_Unavailable, _Cancelled):
            raise
        except Exception:
            raise _Unavailable("HELPER_DISPATCH_FAILED") from None
        finally:
            # No request, stdout, exception or stderr is logged or persisted.
            if request is not None:
                request.clear()
            payload = None
            gone = process is None or self._reap_helper(process, cancel)
            with self._lock:
                if gone and (self._helper is process or process is None):
                    self._helper, self._cancel_handle = None, None
                if gone and cancel is not None:
                    native.kernel.CloseHandle(cancel)
                if not gone:
                    # Preserve the private helper/event for a subsequent stop
                    # attempt; never acknowledge PAUSED while it is still live.
                    self._helper, self._cancel_handle = process, cancel
            if not gone:
                raise _HelperUnreaped() from None

    def _reap_helper(self, process, cancel) -> bool:
        native = self._native
        if native is None:
            return False
        if cancel is not None:
            native.kernel.SetEvent(cancel)
        try:
            if process.poll() is None:
                process.kill()  # Only our private helper, never launcher/game.
                process.wait(timeout=2.0)
        except Exception:
            pass
        try:
            return process.poll() is not None
        except Exception:
            return False

    def _finish_stop(self) -> None:
        self._forget_credentials()
        native = self._native
        with self._lock:
            helper, cancel = self._helper, self._cancel_handle
        if helper is not None:
            if not self._reap_helper(helper, cancel):
                raise _HelperUnreaped()
            with self._lock:
                self._helper, self._cancel_handle = None, None
                if native is not None and cancel is not None:
                    native.kernel.CloseHandle(cancel)
        closer = self._session_closer
        if closer is not None and closer.poll().observing:
            if not self._close_requested or not self._cleanup_confirmed:
                closer.stop()
            while closer.poll().observing:
                self._wake.wait(0.1)
                self._wake.clear()
            with self._lock:
                self._status = replace(self._status, session_cleanup=closer.poll())
        if native is None:
            with self._lock:
                self._worker_terminal = True
                complete = closer is None or closer.poll().complete
                self._set("STOPPED", "STOPPED", cleanup_complete=complete, human_required=False)
            return
        if not native.records and not native.unidentified:
            # A preflight refusal never acquired a launcher/game. Releasing
            # these empty resources requires no action on a foreign session.
            native.close_handles()
            self._native = None
            with self._lock:
                self._worker_terminal = True
                self._set("STOPPED", "CLEANED", cleanup_complete=True, human_required=False)
            return
        if not self._close_requested or not self._cleanup_confirmed:
            self._retain_processes()
            return
        from .account_session_cleanup import AutomaticSessionCloser
        closer = AutomaticSessionCloser(
            self._launcher_path, self._game_path, diagnostics_dir=self._diagnostics_dir,
            close_timeout=min(10.0, self._close_timeout))
        self._session_closer = closer
        closer.begin()
        self._set("STOPPING", "CLOSING", human_required=False)
        while True:
            with self._lock:
                retain = not self._close_requested or not self._cleanup_confirmed
            if retain:
                closer.stop()
                if closer.poll().observing:
                    self._wake.wait(0.1)
                    self._wake.clear()
                    continue
                self._retain_processes()
                return
            live = [record for record in native.records.values() if native.alive(record)]
            result = closer.poll()
            with self._lock:
                self._status = replace(self._status, owned_processes=tuple(record.identity for record in live),
                                       session_cleanup=result)
            if result.observing:
                self._wake.wait(0.1)
                self._wake.clear()
                continue
            if result.state == "CLOSED" and result.complete and not live and not native.unidentified:
                native.close_handles()
                self._native = None
                with self._lock:
                    self._worker_terminal = True
                    self._set("STOPPED", "CLEANED", cleanup_complete=True, human_required=False)
                return
            with self._lock:
                self._worker_terminal = self._waiting = True
                self._set("BLOCKED", "SESSION_CLEANUP_FAILED", cleanup_complete=False, human_required=False)
            return

    def _stop_wait(self, code: str) -> None:
        # No further worker operations follow this publication. A new explicit
        # cleanup request may start immediately, even before this thread returns.
        with self._lock:
            self._worker_terminal = True
            self._wait_human(code)

    def _retain_processes(self) -> None:
        native = self._native
        if native is not None:
            isolated = native.discover() if self._root is not None else True
            live = [record.identity for record in native.records.values() if native.alive(record)]
            complete = not live and not native.unidentified and isolated
            code = ("CLEANUP_CONFIRMATION" if self._close_requested and not self._cleanup_confirmed
                    else "PROCESSES_RETAINED" if not complete else "STOPPED")
            if complete:
                native.close_handles()
                self._native = None
            with self._lock:
                self._worker_terminal = True
                self._set("WAITING" if code == "CLEANUP_CONFIRMATION" else "STOPPED", code,
                          owned_processes=tuple(live), cleanup_complete=complete,
                          human_required=code == "CLEANUP_CONFIRMATION")
