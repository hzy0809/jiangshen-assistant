"""Windows directory notifications for the native bridge's atomic receipts."""
from __future__ import annotations

import ctypes
from ctypes import wintypes
from pathlib import Path
import threading
import math
import time


_READ_RETRY_DELAY = 0.1


class StateNotifications:
    def __init__(self, directory: Path, on_change, on_error) -> None:
        self.directory = str(directory)
        self.on_change, self.on_error = on_change, on_error
        self._lock = threading.Lock()
        self._stop = self._change = None
        self._thread = None
        self._kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        k = self._kernel
        k.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
        k.CreateEventW.restype = wintypes.HANDLE
        k.SetEvent.argtypes = [wintypes.HANDLE]
        k.SetEvent.restype = wintypes.BOOL
        k.CloseHandle.argtypes = [wintypes.HANDLE]
        k.CloseHandle.restype = wintypes.BOOL
        k.FindFirstChangeNotificationW.argtypes = [wintypes.LPCWSTR, wintypes.BOOL, wintypes.DWORD]
        k.FindFirstChangeNotificationW.restype = wintypes.HANDLE
        k.FindNextChangeNotification.argtypes = [wintypes.HANDLE]
        k.FindNextChangeNotification.restype = wintypes.BOOL
        k.FindCloseChangeNotification.argtypes = [wintypes.HANDLE]
        k.FindCloseChangeNotification.restype = wintypes.BOOL
        k.WaitForMultipleObjects.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE), wintypes.BOOL, wintypes.DWORD]
        k.WaitForMultipleObjects.restype = wintypes.DWORD

    @property
    def active(self) -> bool:
        with self._lock:
            return self._stop is not None and self._thread is not None and self._thread.is_alive()

    def start(self) -> None:
        k = self._kernel
        stop = k.CreateEventW(None, True, False, None)
        if not stop:
            raise ctypes.WinError(ctypes.get_last_error())
        change = k.FindFirstChangeNotificationW(self.directory, False, 0x1 | 0x8 | 0x10)
        if change in (None, ctypes.c_void_p(-1).value):
            error = ctypes.WinError(ctypes.get_last_error())
            k.CloseHandle(stop)
            raise error
        with self._lock:
            self._stop, self._change = stop, change
            self._thread = threading.Thread(target=self._run, name='native-state-notifications', daemon=True)
            thread = self._thread
        try:
            thread.start()
        except Exception:
            with self._lock:
                self._stop = self._change = None
            k.FindCloseChangeNotification(change)
            k.CloseHandle(stop)
            raise

    def close(self) -> None:
        # Signalling is nonblocking. Never join a reader while the caller owns
        # a bridge or controller lock that its final callback may still need.
        with self._lock:
            if self._stop is not None:
                self._kernel.SetEvent(self._stop)

    def _run(self) -> None:
        with self._lock:
            stop, change = self._stop, self._change
        k = self._kernel
        handles = (wintypes.HANDLE * 2)(stop, change)
        retry_at = None

        def refresh():
            nonlocal retry_at
            # False means the atomic snapshot is temporarily unavailable. Keep
            # a timer as well as directory notifications: notifications coalesce,
            # and releasing an incompatible file handle need not signal one.
            retry_at = (time.monotonic() + _READ_RETRY_DELAY
                        if self.on_change() is False else None)

        try:
            # The watcher is installed before reading, so a state change in
            # this first read cannot fall into an observation/subscribe gap.
            refresh()
            while True:
                timeout = (0xFFFFFFFF if retry_at is None else
                           max(0, math.ceil((retry_at - time.monotonic()) * 1000)))
                result = k.WaitForMultipleObjects(2, handles, False, timeout)
                if result == 0:
                    return
                if result == 1:
                    if not k.FindNextChangeNotification(change):
                        raise ctypes.WinError(ctypes.get_last_error())
                elif result != 0x102:  # WAIT_TIMEOUT
                    raise ctypes.WinError(ctypes.get_last_error())
                # A busy directory must not turn the recovery timer into a hot
                # loop. Rearm notifications immediately, then honor the delay.
                if retry_at is None or time.monotonic() >= retry_at:
                    refresh()
        except Exception as exc:
            self.on_error(exc)
        finally:
            with self._lock:
                self._stop = self._change = None
            k.FindCloseChangeNotification(change)
            k.CloseHandle(stop)
