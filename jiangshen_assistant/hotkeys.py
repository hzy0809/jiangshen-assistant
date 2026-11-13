"""Owned Windows F8 registration with a latched stop event.

The message thread owns both registration and release. UI polling consumes an
event instead of sampling key state, so a completed short press stays pending.
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
import sys
import threading
from typing import Callable, Protocol


WM_HOTKEY = 0x0312
WM_QUIT = 0x0012
MOD_NOREPEAT = 0x4000
VK_F8 = 0x77
_HOTKEY_ID = 1  # Thread-local because RegisterHotKey uses a NULL window handle.


class HotkeyError(RuntimeError):
    """The global stop shortcut could not be registered or released."""


class _MessageAPI(Protocol):
    def prepare(self) -> int: ...
    def register(self) -> None: ...
    def get_message(self) -> tuple[int, int] | None: ...
    def post_quit(self, thread_id: int) -> None: ...
    def unregister(self) -> None: ...


class _WindowsHotkeyAPI:
    def __init__(self) -> None:
        if sys.platform != "win32":
            raise HotkeyError("全局 F8 停止快捷键仅支持 Windows。")
        self.user = ctypes.WinDLL("user32", use_last_error=True)
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        signatures = [
            (self.user, "PeekMessageW", [ctypes.POINTER(wintypes.MSG), wintypes.HWND,
                                         wintypes.UINT, wintypes.UINT, wintypes.UINT], wintypes.BOOL),
            (self.user, "RegisterHotKey", [wintypes.HWND, ctypes.c_int, wintypes.UINT,
                                           wintypes.UINT], wintypes.BOOL),
            (self.user, "UnregisterHotKey", [wintypes.HWND, ctypes.c_int], wintypes.BOOL),
            (self.user, "GetMessageW", [ctypes.POINTER(wintypes.MSG), wintypes.HWND,
                                        wintypes.UINT, wintypes.UINT], wintypes.BOOL),
            (self.user, "PostThreadMessageW", [wintypes.DWORD, wintypes.UINT,
                                               wintypes.WPARAM, wintypes.LPARAM], wintypes.BOOL),
            (self.kernel, "GetCurrentThreadId", [], wintypes.DWORD),
        ]
        for dll, name, args, result in signatures:
            function = getattr(dll, name)
            function.argtypes, function.restype = args, result

    @staticmethod
    def _error(action: str) -> HotkeyError:
        return HotkeyError(f"{action}（Windows 错误 {ctypes.get_last_error()}）。")

    def prepare(self) -> int:
        # Ensure PostThreadMessage can wake this thread even before GetMessage.
        message = wintypes.MSG()
        self.user.PeekMessageW(ctypes.byref(message), None, 0, 0, 0)
        return int(self.kernel.GetCurrentThreadId())

    def register(self) -> None:
        if not self.user.RegisterHotKey(None, _HOTKEY_ID, MOD_NOREPEAT, VK_F8):
            raise self._error("无法注册全局 F8；快捷键可能已被其他程序占用")

    def get_message(self) -> tuple[int, int] | None:
        message = wintypes.MSG()
        result = self.user.GetMessageW(ctypes.byref(message), None, 0, 0)
        if result == -1:
            raise self._error("全局 F8 消息接收失败")
        if result == 0:
            return None
        return int(message.message), int(message.wParam)

    def post_quit(self, thread_id: int) -> None:
        if not self.user.PostThreadMessageW(thread_id, WM_QUIT, 0, 0):
            raise self._error("无法停止全局 F8 消息线程")

    def unregister(self) -> None:
        if not self.user.UnregisterHotKey(None, _HOTKEY_ID):
            raise self._error("无法释放全局 F8 注册")


class GlobalStopHotkey:
    """One-shot lifecycle; repeated starts/closes and stop events are idempotent.

    api_factory is injectable so tests never need real registration or input.
    A failed start/close raises, and error remains readable for UI reporting.
    """

    def __init__(self, api_factory: Callable[[], _MessageAPI] = _WindowsHotkeyAPI,
                 timeout: float = 2.0) -> None:
        self._api_factory = api_factory
        self._timeout = timeout
        self._lifecycle = threading.Lock()
        self._state = threading.Lock()
        self._ready = threading.Event()
        self._closing = threading.Event()
        self._thread: threading.Thread | None = None
        self._api: _MessageAPI | None = None
        self._thread_id: int | None = None
        self._registered = False
        self._pending = False
        self._error: str | None = None

    @property
    def registered(self) -> bool:
        with self._state:
            return self._registered

    @property
    def error(self) -> str | None:
        with self._state:
            return self._error

    def _record_error(self, error: Exception) -> None:
        with self._state:
            detail = str(error) or type(error).__name__
            self._error = f"{self._error}；{detail}" if self._error else detail

    def start(self) -> None:
        with self._lifecycle:
            if self._closing.is_set():
                raise HotkeyError("全局 F8 已关闭。")
            if self._thread is None:
                if self.error:
                    raise HotkeyError(self.error)
                try:
                    thread = threading.Thread(target=self._run, name="global-stop-F8", daemon=True)
                    thread.start()
                except Exception as cause:
                    error = HotkeyError(f"无法启动全局 F8 消息线程：{cause}")
                    self._record_error(error)
                    raise error from cause
                # Only retain a join target after the thread actually started.
                self._thread = thread
            if not self._ready.wait(self._timeout):
                self._closing.set()
                error = HotkeyError("全局 F8 注册超时。")
                self._record_error(error)
                raise error
            if not self.registered:
                raise HotkeyError(self.error or "全局 F8 消息线程已停止。")

    def consume(self) -> bool:
        with self._state:
            pending, self._pending = self._pending, False
            return pending

    def close(self) -> None:
        with self._lifecycle:
            self._closing.set()
            thread = self._thread
            if thread is None:
                return
            if thread.is_alive() and self._ready.wait(self._timeout):
                with self._state:
                    api, thread_id = self._api, self._thread_id
                if api is not None and thread_id is not None and thread.is_alive():
                    try:
                        api.post_quit(thread_id)
                    except Exception as error:
                        # The receiver may have exited immediately before the post.
                        if thread.is_alive():
                            self._record_error(error)
            thread.join(self._timeout)
            if thread.is_alive():
                error = HotkeyError("全局 F8 消息线程未能退出；注册释放尚未确认。")
                self._record_error(error)
                raise error
            if self.error:
                raise HotkeyError(self.error)

    def _run(self) -> None:
        registered = False
        api = None
        try:
            api = self._api_factory()
            thread_id = api.prepare()
            with self._state:
                self._api, self._thread_id = api, thread_id
            if self._closing.is_set():
                return
            api.register()
            registered = True
            with self._state:
                self._registered = True
            self._ready.set()
            while not self._closing.is_set():
                message = api.get_message()
                if message is None:
                    if not self._closing.is_set():
                        raise HotkeyError("全局 F8 消息线程意外退出。")
                    break
                if message == (WM_HOTKEY, _HOTKEY_ID):
                    with self._state:
                        self._pending = True
        except Exception as error:
            self._record_error(error)
        finally:
            if registered and api is not None:
                try:
                    api.unregister()
                except Exception as error:
                    self._record_error(error)
            with self._state:
                self._registered = False
            self._ready.set()
