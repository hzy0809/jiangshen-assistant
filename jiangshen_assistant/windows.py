"""Read-only Windows discovery, client capture, and emergency-stop events.

No input-injection APIs are exposed here. PrintWindow runs in a disposable
process because the target application handles that synchronous request.
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import ntpath
import os
from pathlib import Path
import struct
import subprocess
import sys
import zlib

if __package__:
    from .hotkeys import GlobalStopHotkey, HotkeyError
else:  # The capture subprocess invokes this file directly.
    from hotkeys import GlobalStopHotkey, HotkeyError

__all__ = ["WindowInfo", "WindowError", "WindowsAdapter"]


class WindowError(RuntimeError):
    """A window cannot safely be inspected or captured."""


@dataclass(frozen=True)
class WindowInfo:
    hwnd: int
    title: str
    process_id: int
    process_name: str
    width: int
    height: int

    @property
    def label(self) -> str:
        return (f"{self.title} | {self.process_name} (PID {self.process_id})"
                f" | {self.width}×{self.height} | HWND {self.hwnd}")


_MAX_PIXELS = 32_000_000
_CAPTURE_TIMEOUT = 4.0


def _capture_executable() -> str:
    """Use the console interpreter so the helper has binary standard streams."""
    if not sys.executable:
        raise WindowError("无法确定只读截图进程使用的 Python 解释器。")
    executable = Path(sys.executable)
    if executable.name.casefold() == "pythonw.exe":
        executable = executable.with_name("python.exe")
        if not executable.is_file():
            raise WindowError("截图需要与 pythonw.exe 同目录的 python.exe，请修复 Python 安装。")
    return str(executable)


def _validate_hwnd(hwnd: int) -> None:
    if type(hwnd) is not int or hwnd <= 0 or hwnd >= 1 << (8 * ctypes.sizeof(ctypes.c_void_p)):
        raise WindowError("窗口句柄无效，请重新选择窗口。")


def _validate_size(width: int, height: int) -> None:
    if (type(width) is not int or type(height) is not int
            or width <= 0 or height <= 0 or width * height > _MAX_PIXELS):
        raise WindowError("窗口客户区尺寸无效或过大，请恢复窗口后重试。")


def _encode_png(width: int, height: int, bgra: bytes) -> bytes:
    """Encode top-down, tightly packed BGRA pixels as an opaque RGB PNG."""
    _validate_size(width, height)
    if not isinstance(bgra, bytes) or len(bgra) != width * height * 4:
        raise WindowError("截图像素缓冲区大小无效。")
    compressor = zlib.compressobj()
    compressed = bytearray()
    stride = width * 4
    for offset in range(0, len(bgra), stride):
        row = bgra[offset:offset + stride]
        rgb = bytearray(width * 3)
        rgb[0::3], rgb[1::3], rgb[2::3] = row[2::4], row[1::4], row[0::4]
        compressed.extend(compressor.compress(b"\x00" + rgb))
    compressed.extend(compressor.flush())

    def chunk(kind: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + kind + data
                + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", bytes(compressed)) + chunk(b"IEND", b""))


class _BitmapInfoHeader(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
                ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
                ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]


class _BitmapInfo(ctypes.Structure):
    _fields_ = [("bmiHeader", _BitmapInfoHeader), ("bmiColors", wintypes.DWORD * 1)]


class _Win32:
    def __init__(self) -> None:
        if sys.platform != "win32":
            raise WindowError("窗口识别和截图仅支持 Windows。")
        self.user = ctypes.WinDLL("user32", use_last_error=True)
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.gdi = ctypes.WinDLL("gdi32", use_last_error=True)
        self.enum_callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        signatures = [
            (self.user, "EnumWindows", [self.enum_callback, wintypes.LPARAM], wintypes.BOOL),
            (self.user, "IsWindow", [wintypes.HWND], wintypes.BOOL),
            (self.user, "IsWindowVisible", [wintypes.HWND], wintypes.BOOL),
            (self.user, "IsIconic", [wintypes.HWND], wintypes.BOOL),
            (self.user, "IsHungAppWindow", [wintypes.HWND], wintypes.BOOL),
            (self.user, "GetWindowTextLengthW", [wintypes.HWND], ctypes.c_int),
            (self.user, "GetWindowTextW", [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int], ctypes.c_int),
            (self.user, "GetWindowThreadProcessId", [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)], wintypes.DWORD),
            (self.user, "GetClientRect", [wintypes.HWND, ctypes.POINTER(wintypes.RECT)], wintypes.BOOL),
            (self.user, "GetForegroundWindow", [], wintypes.HWND),
            (self.user, "GetDC", [wintypes.HWND], wintypes.HDC),
            (self.user, "ReleaseDC", [wintypes.HWND, wintypes.HDC], ctypes.c_int),
            (self.user, "PrintWindow", [wintypes.HWND, wintypes.HDC, wintypes.UINT], wintypes.BOOL),
            (self.kernel, "OpenProcess", [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            (self.kernel, "CloseHandle", [wintypes.HANDLE], wintypes.BOOL),
            (self.kernel, "QueryFullProcessImageNameW", [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)], wintypes.BOOL),
            (self.gdi, "CreateCompatibleDC", [wintypes.HDC], wintypes.HDC),
            (self.gdi, "CreateDIBSection", [wintypes.HDC, ctypes.POINTER(_BitmapInfo), wintypes.UINT, ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.DWORD], wintypes.HBITMAP),
            (self.gdi, "SelectObject", [wintypes.HDC, wintypes.HGDIOBJ], wintypes.HGDIOBJ),
            (self.gdi, "DeleteObject", [wintypes.HGDIOBJ], wintypes.BOOL),
            (self.gdi, "DeleteDC", [wintypes.HDC], wintypes.BOOL),
            (self.gdi, "GdiFlush", [], wintypes.BOOL),
        ]
        for dll, name, argtypes, restype in signatures:
            function = getattr(dll, name)
            function.argtypes, function.restype = argtypes, restype


class WindowsAdapter:
    def __init__(self) -> None:
        self._binding: _Win32 | None = None
        self._stop_hotkey: GlobalStopHotkey | None = None

    def _api(self) -> _Win32:
        if self._binding is None:
            self._binding = _Win32()
        return self._binding

    def enable_dpi_awareness(self) -> None:
        """Call before creating Tk so client dimensions use physical pixels."""
        api = self._api()
        modern = getattr(api.user, "SetProcessDpiAwarenessContext", None)
        if modern is not None:
            modern.argtypes, modern.restype = [ctypes.c_void_p], wintypes.BOOL
            if modern(ctypes.c_void_p(-4)) or ctypes.get_last_error() == 5:
                # ERROR_ACCESS_DENIED means a manifest/earlier call already set it.
                return
        legacy = api.user.SetProcessDPIAware
        legacy.argtypes, legacy.restype = [], wintypes.BOOL
        if not legacy() and ctypes.get_last_error() != 5:
            raise WindowError("无法启用 Windows DPI 感知。")

    def _process_name(self, pid: int) -> str:
        api = self._api()
        handle = api.kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            return f"PID {pid}"
        try:
            path = ctypes.create_unicode_buffer(32768)
            size = wintypes.DWORD(len(path))
            if api.kernel.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(size)):
                return ntpath.basename(path.value)
            return f"PID {pid}"
        finally:
            api.kernel.CloseHandle(handle)

    def inspect(self, hwnd: int) -> WindowInfo:
        api = self._api()
        _validate_hwnd(hwnd)
        if not api.user.IsWindow(hwnd):
            raise WindowError("所选窗口已关闭，请重新选择。")
        pid = wintypes.DWORD()
        api.user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        rect = wintypes.RECT()
        if not pid.value or not api.user.GetClientRect(hwnd, ctypes.byref(rect)):
            raise WindowError("无法读取窗口信息，请重新选择。")
        title = ctypes.create_unicode_buffer(api.user.GetWindowTextLengthW(hwnd) + 1)
        api.user.GetWindowTextW(hwnd, title, len(title))
        return WindowInfo(hwnd, title.value, pid.value, self._process_name(pid.value),
                          rect.right - rect.left, rect.bottom - rect.top)

    def find_windows(self, keyword: str = "将神") -> list[WindowInfo]:
        api = self._api()
        if not isinstance(keyword, str):
            raise WindowError("窗口搜索关键词必须是文字。")
        windows: list[WindowInfo] = []

        @api.enum_callback
        def visit(hwnd: int, _unused: int) -> bool:
            if not api.user.IsWindowVisible(hwnd) or not api.user.GetWindowTextLengthW(hwnd):
                return True
            try:
                info = self.inspect(int(hwnd))
                if (info.process_id != os.getpid() and info.title.strip()
                        and keyword.casefold() in (info.title + " " + info.process_name).casefold()):
                    windows.append(info)
            except WindowError:
                pass  # A window may close while EnumWindows is visiting it.
            return True

        if not api.user.EnumWindows(visit, 0):
            raise WindowError("无法枚举 Windows 窗口。")
        return sorted(windows, key=lambda info: (info.title.casefold(), info.hwnd))

    def foreground_matches(self, hwnd: int) -> bool:
        api = self._api()
        _validate_hwnd(hwnd)
        return bool(api.user.IsWindow(hwnd) and api.user.GetForegroundWindow() == hwnd)

    def start_stop_hotkey(self) -> None:
        """Register F8 and report failure before advertising global stopping."""
        if self._stop_hotkey is None:
            self._stop_hotkey = GlobalStopHotkey()
        try:
            self._stop_hotkey.start()
        except HotkeyError as error:
            raise WindowError(str(error)) from error

    @property
    def stop_hotkey_registered(self) -> bool:
        return self._stop_hotkey is not None and self._stop_hotkey.registered

    @property
    def stop_hotkey_error(self) -> str | None:
        return self._stop_hotkey.error if self._stop_hotkey is not None else None

    def stop_pressed(self) -> bool:
        self.start_stop_hotkey()
        assert self._stop_hotkey is not None
        return self._stop_hotkey.consume()

    def close(self) -> None:
        """Release only resources owned by this adapter; safe before startup."""
        if self._stop_hotkey is not None:
            try:
                self._stop_hotkey.close()
            except HotkeyError as error:
                raise WindowError(str(error)) from error

    def capture_png(self, hwnd: int) -> bytes:
        info = self.inspect(hwnd)
        self._check_capture_window(info)
        # Never leave an unkillable PrintWindow call in a GUI/worker thread.
        # This helper captures only the selected client's DC, never the desktop.
        try:
            result = subprocess.run(
                [_capture_executable(), "-u", str(Path(__file__).resolve()), "--capture",
                 str(hwnd), str(info.process_id)],
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                timeout=_CAPTURE_TIMEOUT, creationflags=subprocess.CREATE_NO_WINDOW,
                check=False,
            )
        except subprocess.TimeoutExpired as error:
            raise WindowError("窗口截图超时，请确认游戏响应正常后重试。") from error
        except OSError as error:
            raise WindowError("无法启动只读截图进程。") from error
        if result.returncode:
            detail = result.stderr.decode("utf-8", errors="replace").strip()
            raise WindowError(detail or "窗口截图失败。")
        if not result.stdout.startswith(b"\x89PNG\r\n\x1a\n"):
            raise WindowError("截图进程未返回有效 PNG。")
        current = self.inspect(hwnd)
        self._check_capture_window(current)
        if (current.process_id, current.width, current.height) != (info.process_id, info.width, info.height):
            raise WindowError("截图期间窗口发生变化，请重试。")
        return result.stdout

    def _check_capture_window(self, info: WindowInfo) -> None:
        api = self._api()
        if not api.user.IsWindowVisible(info.hwnd) or api.user.IsIconic(info.hwnd):
            raise WindowError("请先显示并恢复所选游戏窗口，再进行截图。")
        if api.user.IsHungAppWindow(info.hwnd):
            raise WindowError("所选窗口未响应，暂时无法截图。")
        _validate_size(info.width, info.height)

    def _capture_local(self, hwnd: int, expected_pid: int) -> bytes:
        """Internal helper-process implementation. Never call on the GUI thread."""
        api = self._api()
        info = self.inspect(hwnd)
        self._check_capture_window(info)
        if info.process_id != expected_pid:
            raise WindowError("窗口所属进程发生变化，请重新选择。")
        dc = api.user.GetDC(hwnd)
        if not dc:
            raise WindowError("无法读取窗口设备上下文。")
        memory_dc = bitmap = previous = None
        try:
            memory_dc = api.gdi.CreateCompatibleDC(dc)
            if not memory_dc:
                raise WindowError("无法创建截图设备上下文。")
            bitmap_info = _BitmapInfo()
            header = bitmap_info.bmiHeader
            header.biSize = ctypes.sizeof(_BitmapInfoHeader)
            header.biWidth, header.biHeight = info.width, -info.height
            header.biPlanes, header.biBitCount = 1, 32
            pixels = ctypes.c_void_p()
            bitmap = api.gdi.CreateDIBSection(dc, ctypes.byref(bitmap_info), 0,
                                             ctypes.byref(pixels), None, 0)
            if not bitmap or not pixels.value:
                raise WindowError("无法分配截图像素缓冲区。")
            ctypes.memset(pixels, 0, info.width * info.height * 4)
            previous = api.gdi.SelectObject(memory_dc, bitmap)
            if not previous or previous == ctypes.c_void_p(-1).value:
                previous = None
                raise WindowError("无法选择截图位图。")
            # PW_CLIENTONLY. Rendering stays with the owning application.
            if not api.user.PrintWindow(hwnd, memory_dc, 1):
                raise WindowError("游戏窗口不支持当前截图方式。")
            if not api.gdi.GdiFlush():
                raise WindowError("窗口截图像素同步失败。")
            bgra = ctypes.string_at(pixels, info.width * info.height * 4)
            if not (any(bgra[0::4]) or any(bgra[1::4]) or any(bgra[2::4])):
                raise WindowError("窗口截图为全黑，请恢复游戏画面后重试；当前渲染方式可能不支持截图。")
            current = self.inspect(hwnd)
            self._check_capture_window(current)
            if (current.process_id, current.width, current.height) != (expected_pid, info.width, info.height):
                raise WindowError("截图期间窗口发生变化，请重试。")
            return _encode_png(info.width, info.height, bgra)
        finally:
            if previous:
                api.gdi.SelectObject(memory_dc, previous)
            if bitmap:
                api.gdi.DeleteObject(bitmap)
            if memory_dc:
                api.gdi.DeleteDC(memory_dc)
            api.user.ReleaseDC(hwnd, dc)


def _capture_main() -> int:
    try:
        if len(sys.argv) != 4 or sys.argv[1] != "--capture":
            raise WindowError("此模块仅提供内部只读截图入口。")
        adapter = WindowsAdapter()
        adapter.enable_dpi_awareness()
        image = adapter._capture_local(int(sys.argv[2]), int(sys.argv[3]))
        sys.stdout.buffer.write(image)
        return 0
    except (WindowError, ValueError) as error:
        sys.stderr.buffer.write(str(error).encode("utf-8"))
        return 1


if __name__ == "__main__":
    raise SystemExit(_capture_main())
