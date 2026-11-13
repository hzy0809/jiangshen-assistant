"""Global stop lifecycle tests; only in-memory APIs, never OS input or UI."""

import ctypes
from ctypes import wintypes
from queue import Queue
import sys
import threading
import unittest
from unittest.mock import patch

from jiangshen_assistant.hotkeys import (
    GlobalStopHotkey, HotkeyError, MOD_NOREPEAT, VK_F8, WM_HOTKEY, WM_QUIT,
    _WindowsHotkeyAPI,
)
from jiangshen_assistant.windows import WindowError, WindowsAdapter


class FakeMessageAPI:
    def __init__(self, register_error=None, unregister_error=None):
        self.messages = Queue()
        self.calls = []
        self.register_error = register_error
        self.unregister_error = unregister_error
        self.released = threading.Event()

    def prepare(self):
        self.calls.append(("prepare", threading.get_ident()))
        return 123

    def register(self):
        self.calls.append(("register", threading.get_ident()))
        if self.register_error:
            raise self.register_error

    def get_message(self):
        while True:
            message = self.messages.get(timeout=5)
            if isinstance(message, threading.Event):
                message.set()
                continue
            if isinstance(message, Exception):
                raise message
            return message

    def flush(self):
        barrier = threading.Event()
        self.messages.put(barrier)
        if not barrier.wait(2):
            raise AssertionError("Fake message thread did not drain queued events")

    def post_quit(self, thread_id):
        self.calls.append(("quit", threading.get_ident(), thread_id))
        self.messages.put(None)

    def unregister(self):
        self.calls.append(("unregister", threading.get_ident()))
        self.released.set()
        if self.unregister_error:
            raise self.unregister_error


class HotkeyLifecycleTests(unittest.TestCase):
    def hotkey(self, api=None):
        api = api or FakeMessageAPI()
        hotkey = GlobalStopHotkey(lambda: api)

        def cleanup():
            try:
                hotkey.close()
            except HotkeyError:
                pass

        self.addCleanup(cleanup)
        return hotkey, api

    def test_short_press_stays_latched_until_later_poll_and_repeats_coalesce(self):
        hotkey, api = self.hotkey()
        hotkey.start()
        # Both press and release may have finished before the GUI next polls.
        api.messages.put((WM_HOTKEY, 1))
        api.messages.put((WM_HOTKEY, 1))
        api.flush()
        self.assertTrue(hotkey.consume())
        self.assertFalse(hotkey.consume())
        api.messages.put((WM_HOTKEY, 1))
        api.flush()
        self.assertTrue(hotkey.consume())
        self.assertFalse(hotkey.consume())

    def test_unrelated_messages_cannot_stop(self):
        hotkey, api = self.hotkey()
        hotkey.start()
        api.messages.put((WM_HOTKEY, 2))
        api.messages.put((0x100, 1))
        api.flush()
        self.assertFalse(hotkey.consume())

    def test_owned_registration_and_release_share_thread_and_close_joins(self):
        hotkey, api = self.hotkey()
        hotkey.start()
        hotkey.start()
        self.assertTrue(hotkey.registered)
        hotkey.close()
        hotkey.close()
        self.assertFalse(hotkey.registered)
        self.assertFalse(hotkey._thread.is_alive())
        self.assertIsNone(hotkey.error)
        register = [call for call in api.calls if call[0] == "register"]
        release = [call for call in api.calls if call[0] == "unregister"]
        self.assertEqual(len(register), 1)
        self.assertEqual(len(release), 1)
        self.assertEqual(register[0][1], release[0][1])
        self.assertNotEqual(register[0][1], threading.get_ident())
        self.assertEqual([call[2] for call in api.calls if call[0] == "quit"], [123])
        with self.assertRaises(HotkeyError):
            hotkey.start()

    def test_close_without_start_does_not_register(self):
        hotkey, api = self.hotkey()
        hotkey.close()
        hotkey.close()
        self.assertEqual(api.calls, [])

    def test_registration_failure_is_visible_and_does_not_unregister_unowned_id(self):
        hotkey, api = self.hotkey(FakeMessageAPI(register_error=HotkeyError("F8 occupied 1409")))
        with self.assertRaisesRegex(HotkeyError, "1409"):
            hotkey.start()
        self.assertFalse(hotkey.registered)
        self.assertIn("1409", hotkey.error)
        with self.assertRaisesRegex(HotkeyError, "1409"):
            hotkey.close()
        self.assertFalse(hotkey._thread.is_alive())
        self.assertFalse(any(call[0] == "unregister" for call in api.calls))

    def test_message_loop_failure_is_visible_and_releases_registration(self):
        hotkey, api = self.hotkey()
        hotkey.start()
        api.messages.put(HotkeyError("GetMessage failed 5"))
        self.assertTrue(api.released.wait(2))
        with self.assertRaisesRegex(HotkeyError, "GetMessage failed 5"):
            hotkey.close()
        self.assertFalse(hotkey.registered)
        self.assertFalse(hotkey._thread.is_alive())

    def test_unexpected_quit_is_failure_instead_of_silent_global_stop_loss(self):
        hotkey, api = self.hotkey()
        hotkey.start()
        api.messages.put(None)
        self.assertTrue(api.released.wait(2))
        with self.assertRaisesRegex(HotkeyError, "意外退出"):
            hotkey.close()

    def test_unregister_failure_is_reported_after_thread_termination(self):
        hotkey, _ = self.hotkey(FakeMessageAPI(unregister_error=HotkeyError("release failed")))
        hotkey.start()
        with self.assertRaisesRegex(HotkeyError, "release failed"):
            hotkey.close()
        self.assertFalse(hotkey._thread.is_alive())

    def test_startup_timeout_cancels_late_registration_and_still_releases_it(self):
        entered, proceed = threading.Event(), threading.Event()

        class SlowRegisterAPI(FakeMessageAPI):
            def register(self):
                entered.set()
                if not proceed.wait(2):
                    raise AssertionError("Test did not release registration")
                super().register()

        api = SlowRegisterAPI()
        hotkey = GlobalStopHotkey(lambda: api, timeout=0.02)
        try:
            with self.assertRaisesRegex(HotkeyError, "超时"):
                hotkey.start()
            self.assertTrue(entered.is_set())
        finally:
            proceed.set()
        self.assertTrue(api.released.wait(2))
        with self.assertRaisesRegex(HotkeyError, "超时"):
            hotkey.close()
        self.assertFalse(hotkey._thread.is_alive())
        self.assertFalse(hotkey.registered)
        self.assertEqual([call[0] for call in api.calls].count("unregister"), 1)

    def test_failed_quit_post_is_visible_and_does_not_claim_release(self):
        api = FakeMessageAPI()
        hotkey = GlobalStopHotkey(lambda: api, timeout=0.02)
        hotkey.start()
        # Synchronize so the receiver is blocked in get_message before close.
        api.flush()
        with patch.object(api, "post_quit", side_effect=HotkeyError("post failed")):
            try:
                with self.assertRaisesRegex(HotkeyError, "释放尚未确认"):
                    hotkey.close()
                self.assertIn("post failed", hotkey.error)
                self.assertTrue(hotkey.registered)
            finally:
                # Release only the in-memory fake receiver, never an OS thread.
                api.messages.put(None)
                self.assertTrue(api.released.wait(2))
        with self.assertRaisesRegex(HotkeyError, "post failed"):
            hotkey.close()
        self.assertFalse(hotkey._thread.is_alive())

    def test_non_windows_construction_safe_but_start_reports_unsupported(self):
        with patch.object(sys, "platform", "linux"):
            hotkey = GlobalStopHotkey()
            with self.assertRaisesRegex(HotkeyError, "Windows"):
                hotkey.start()
            with self.assertRaisesRegex(HotkeyError, "Windows"):
                hotkey.close()
        self.assertFalse(hotkey._thread.is_alive())

    def test_adapter_consumes_latched_event_and_exposes_status_and_cleanup(self):
        hotkey, api = self.hotkey()
        adapter = WindowsAdapter()
        with patch("jiangshen_assistant.windows.GlobalStopHotkey", return_value=hotkey):
            adapter.start_stop_hotkey()
        self.assertTrue(adapter.stop_hotkey_registered)
        self.assertIsNone(adapter.stop_hotkey_error)
        api.messages.put((WM_HOTKEY, 1))
        api.flush()
        self.assertTrue(adapter.stop_pressed())
        self.assertFalse(adapter.stop_pressed())
        adapter.close()
        self.assertFalse(adapter.stop_hotkey_registered)

    def test_adapter_registration_failure_is_window_error(self):
        hotkey, _ = self.hotkey(FakeMessageAPI(register_error=HotkeyError("occupied")))
        adapter = WindowsAdapter()
        with patch("jiangshen_assistant.windows.GlobalStopHotkey", return_value=hotkey):
            with self.assertRaisesRegex(WindowError, "occupied"):
                adapter.start_stop_hotkey()
        self.assertFalse(adapter.stop_hotkey_registered)
        self.assertEqual(adapter.stop_hotkey_error, "occupied")
        with self.assertRaisesRegex(WindowError, "occupied"):
            adapter.stop_pressed()
        with self.assertRaisesRegex(WindowError, "occupied"):
            adapter.close()

    def test_thread_start_failure_is_window_error_and_close_remains_idempotent(self):
        adapter = WindowsAdapter()
        with patch("jiangshen_assistant.hotkeys.threading.Thread") as thread_class:
            thread = thread_class.return_value
            thread.start.side_effect = RuntimeError("cannot start new thread")
            with self.assertRaisesRegex(WindowError, "cannot start new thread"):
                adapter.start_stop_hotkey()
            self.assertFalse(adapter.stop_hotkey_registered)
            self.assertIn("cannot start new thread", adapter.stop_hotkey_error)
            self.assertIsNone(adapter._stop_hotkey._thread)
            # Polling cannot retry a failed registration or leak RuntimeError.
            with self.assertRaisesRegex(WindowError, "cannot start new thread"):
                adapter.stop_pressed()
            adapter.close()
            adapter.close()
            thread_class.assert_called_once()
            thread.start.assert_called_once()
            thread.join.assert_not_called()


class FakeFunction:
    def __init__(self, result=1):
        self.result = result
        self.calls = []

    def __call__(self, *args):
        self.calls.append(args)
        return self.result


class FakeDLL:
    def __getattr__(self, name):
        function = FakeFunction()
        setattr(self, name, function)
        return function


class WindowsBindingTests(unittest.TestCase):
    def setUp(self):
        self.user, self.kernel = FakeDLL(), FakeDLL()
        with patch.object(sys, "platform", "win32"), patch.object(
                ctypes, "WinDLL", side_effect=[self.user, self.kernel], create=True):
            self.api = _WindowsHotkeyAPI()

    def test_register_uses_no_repeat_f8_and_thread_queue_is_created(self):
        self.kernel.GetCurrentThreadId.result = 456
        self.assertEqual(self.api.prepare(), 456)
        self.assertEqual(len(self.user.PeekMessageW.calls), 1)
        self.api.register()
        self.assertEqual(self.user.RegisterHotKey.calls, [(None, 1, MOD_NOREPEAT, VK_F8)])
        self.api.post_quit(456)
        self.assertEqual(self.user.PostThreadMessageW.calls, [(456, WM_QUIT, 0, 0)])
        self.api.unregister()
        self.assertEqual(self.user.UnregisterHotKey.calls, [(None, 1)])

    def test_get_message_handles_quit_error_and_event(self):
        self.user.GetMessageW.result = 0
        self.assertIsNone(self.api.get_message())
        self.user.GetMessageW.result = -1
        with patch.object(ctypes, "get_last_error", return_value=5, create=True):
            with self.assertRaisesRegex(HotkeyError, "5"):
                self.api.get_message()

        def message_result(pointer, *args):
            message = ctypes.cast(pointer, ctypes.POINTER(wintypes.MSG)).contents
            message.message, message.wParam = WM_HOTKEY, 1
            return 1

        self.user.GetMessageW = message_result
        self.assertEqual(self.api.get_message(), (WM_HOTKEY, 1))

    def test_native_failures_retain_windows_error(self):
        for name, call in [("RegisterHotKey", self.api.register),
                           ("UnregisterHotKey", self.api.unregister),
                           ("PostThreadMessageW", lambda: self.api.post_quit(123))]:
            function = getattr(self.user, name)
            function.result = 0
            with self.subTest(name=name), patch.object(
                    ctypes, "get_last_error", return_value=1409, create=True):
                with self.assertRaisesRegex(HotkeyError, "1409"):
                    call()


if __name__ == "__main__":
    unittest.main()
