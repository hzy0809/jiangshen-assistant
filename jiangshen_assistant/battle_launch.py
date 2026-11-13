"""Out-of-band, monotonic cancellation for native battle launch callbacks.

The named mutex covers the native synchronous Send enqueue, not its response.
Cancellation sets an epoch event before draining that same mutex. Old epochs
are never reset, including after resume. No bridge request/ACK is involved.
"""
from __future__ import annotations

import ctypes
from ctypes import wintypes
import threading
import uuid


class BattleLaunchError(RuntimeError):
    pass


class BattleLaunchAdmission:
    def __init__(self, nonce: str) -> None:
        if len(nonce) != 32 or any(c not in '0123456789abcdef' for c in nonce):
            raise BattleLaunchError('battle_launch_session_identity')
        self.nonce = nonce
        self._lock = threading.RLock()
        self._epoch = ''
        self._events: dict[str, int] = {}
        self._closed = False
        self._fault = ''
        kernel = self._kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateMutexW.argtypes = (ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR)
        kernel.CreateMutexW.restype = wintypes.HANDLE
        kernel.CreateEventW.argtypes = (ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR)
        kernel.CreateEventW.restype = wintypes.HANDLE
        kernel.SetEvent.argtypes = (wintypes.HANDLE,)
        kernel.SetEvent.restype = wintypes.BOOL
        kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.ReleaseMutex.argtypes = (wintypes.HANDLE,)
        kernel.ReleaseMutex.restype = wintypes.BOOL
        kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel.CloseHandle.restype = wintypes.BOOL
        ctypes.set_last_error(0)
        self._mutex = kernel.CreateMutexW(None, False, f'Local\\JSAssistantBattleLaunch-{nonce}')
        if not self._mutex or ctypes.get_last_error() == 183:
            if self._mutex:
                kernel.CloseHandle(self._mutex)
            self._mutex = None
            raise BattleLaunchError('battle_launch_mutex_create_or_collision')

    def open_epoch(self) -> str:
        with self._lock:
            if self._closed or self._fault:
                raise BattleLaunchError(self._fault or 'battle_launch_closed')
            if self._epoch:
                self.cancel_and_drain()
            epoch = uuid.uuid4().hex
            ctypes.set_last_error(0)
            handle = self._kernel.CreateEventW(None, True, False,
                f'Local\\JSAssistantBattleCancel-{self.nonce}-{epoch}')
            if not handle or ctypes.get_last_error() == 183:
                if handle:
                    self._kernel.CloseHandle(handle)
                self._fault = 'battle_launch_event_create_or_collision'
                raise BattleLaunchError(self._fault)
            self._events[epoch] = handle
            self._epoch = epoch
            return epoch

    @property
    def epoch(self) -> str:
        with self._lock:
            if not self._epoch or self._closed or self._fault:
                raise BattleLaunchError(self._fault or 'battle_launch_admission_closed')
            return self._epoch

    def cancel_and_drain(self, timeout_ms: int = 5000) -> None:
        with self._lock:
            # Closing local admission precedes SetEvent and any bounded wait.
            epoch, self._epoch = self._epoch, ''
            if epoch and not self._kernel.SetEvent(self._events[epoch]):
                self._fault = 'battle_launch_cancel_event_failed'
                raise BattleLaunchError(self._fault)
            if self._fault:
                raise BattleLaunchError(self._fault)
            result = self._kernel.WaitForSingleObject(self._mutex, timeout_ms)
            if result in (0, 0x80):
                released = self._kernel.ReleaseMutex(self._mutex)
                if result == 0 and released:
                    return
            self._fault = ('battle_launch_mutex_abandoned' if result == 0x80 else
                           'battle_launch_cancel_drain_unverified')
            raise BattleLaunchError(self._fault)

    def close(self, *, confirmed_target_exit: bool = False) -> None:
        with self._lock:
            if self._closed:
                return
            try:
                if confirmed_target_exit:
                    for handle in self._events.values():
                        self._kernel.SetEvent(handle)
                else:
                    self.cancel_and_drain()
            finally:
                self._closed = True
                # Cancellation tombstones remain until all local references to
                # the session are retired; native tokens retain their own handles.
                for handle in self._events.values():
                    self._kernel.CloseHandle(handle)
                self._events.clear()
                if self._mutex:
                    self._kernel.CloseHandle(self._mutex)
                    self._mutex = None

    def __del__(self):
        # A discarded UNKNOWN session cannot acknowledge cancellation. Signal
        # every tombstone best-effort and release only this process's handles;
        # retained native wrappers still fail their immutable lifetime lease.
        kernel = getattr(self, '_kernel', None)
        if kernel is None:
            return
        for handle in getattr(self, '_events', {}).values():
            kernel.SetEvent(handle)
            kernel.CloseHandle(handle)
        self._events = {}
        if getattr(self, '_mutex', None):
            kernel.CloseHandle(self._mutex)
            self._mutex = None
