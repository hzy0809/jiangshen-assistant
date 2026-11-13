"""Pure-data adapter checks: no windows are touched and no input is sent."""

from dataclasses import FrozenInstanceError
from pathlib import Path
import struct
import subprocess
import sys
import unittest
from unittest.mock import patch
import zlib

from jiangshen_assistant.windows import (
    WindowError, WindowInfo, WindowsAdapter, _capture_executable, _encode_png, _validate_hwnd,
)


def decode_chunks(png):
    chunks = []
    offset = 8
    while offset < len(png):
        length = struct.unpack_from(">I", png, offset)[0]
        kind = png[offset + 4:offset + 8]
        data = png[offset + 8:offset + 8 + length]
        crc = struct.unpack_from(">I", png, offset + 8 + length)[0]
        if crc != zlib.crc32(kind + data) & 0xffffffff:
            raise AssertionError("Invalid PNG CRC")
        chunks.append((kind, data))
        offset += length + 12
    if offset != len(png):
        raise AssertionError("Trailing PNG bytes")
    return chunks


class PngTests(unittest.TestCase):
    def test_bgra_becomes_top_down_rgb_ignoring_gdi_alpha(self):
        pixels = bytes((0, 0, 255, 0, 0, 255, 0, 0,
                        255, 0, 0, 255, 255, 255, 255, 12))
        png = _encode_png(2, 2, pixels)
        self.assertEqual(png[:8], b"\x89PNG\r\n\x1a\n")
        chunks = decode_chunks(png)
        self.assertEqual([kind for kind, _ in chunks], [b"IHDR", b"IDAT", b"IEND"])
        self.assertEqual(struct.unpack(">IIBBBBB", chunks[0][1]), (2, 2, 8, 2, 0, 0, 0))
        self.assertEqual(zlib.decompress(chunks[1][1]),
                         b"\x00\xff\x00\x00\x00\xff\x00\x00\x00\x00\xff\xff\xff\xff")

    def test_rejects_invalid_dimensions_and_buffer(self):
        for width, height in [(0, 1), (1, -1), (True, 1), (1.0, 1), (10000, 10000)]:
            with self.subTest(width=width, height=height), self.assertRaises(WindowError):
                _encode_png(width, height, b"")
        for buffer in [b"", b"123", b"12345", bytearray(4), None]:
            with self.subTest(buffer=buffer), self.assertRaises(WindowError):
                _encode_png(1, 1, buffer)


class ContractTests(unittest.TestCase):
    def test_capture_helper_uses_console_sibling_of_pythonw(self):
        for name in ("pythonw.exe", "PYTHONW.EXE"):
            executable = str(Path("runtime") / name)
            with self.subTest(name=name), patch.object(sys, "executable", executable):
                with patch.object(Path, "is_file", return_value=True):
                    self.assertEqual(_capture_executable(), str(Path("runtime") / "python.exe"))
                with patch.object(Path, "is_file", return_value=False):
                    with self.assertRaisesRegex(WindowError, "python.exe"):
                        _capture_executable()

    def test_capture_helper_preserves_console_interpreter(self):
        executable = str(Path("runtime") / "python.exe")
        with patch.object(sys, "executable", executable):
            self.assertEqual(_capture_executable(), executable)
        with patch.object(sys, "executable", ""):
            with self.assertRaises(WindowError):
                _capture_executable()

    def test_direct_capture_script_import_still_supports_helper_entrypoint(self):
        # Invalid arguments stop before any Windows API or capture is created.
        script = Path(__file__).resolve().parents[1] / "jiangshen_assistant" / "windows.py"
        result = subprocess.run([sys.executable, str(script)], capture_output=True, timeout=5)
        self.assertEqual(result.returncode, 1)
        self.assertIn("仅提供内部只读截图入口", result.stderr.decode("utf-8"))

    def test_window_info_is_immutable_and_label_identifies_target(self):
        info = WindowInfo(123, "将神", 456, "js.exe", 1280, 720)
        for value in ("将神", "js.exe", "456", "1280", "720", "123"):
            self.assertIn(value, info.label)
        with self.assertRaises(FrozenInstanceError):
            info.hwnd = 999

    def test_hwnd_parameters_reject_lossy_or_invalid_values(self):
        for value in [None, True, False, 0, -1, "123", 1.5, 1 << 100]:
            with self.subTest(value=value), self.assertRaises(WindowError):
                _validate_hwnd(value)
        _validate_hwnd(123)

    def test_non_windows_import_and_construction_are_safe(self):
        with patch.object(sys, "platform", "linux"):
            adapter = WindowsAdapter()
            calls = [adapter.enable_dpi_awareness, adapter.find_windows,
                     lambda: adapter.inspect(123), lambda: adapter.capture_png(123),
                     lambda: adapter.foreground_matches(123), adapter.stop_pressed]
            for call in calls:
                with self.subTest(call=call), self.assertRaisesRegex(WindowError, "Windows"):
                    call()


if __name__ == "__main__":
    unittest.main()
