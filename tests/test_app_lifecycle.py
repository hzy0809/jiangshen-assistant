"""Exercise window selection and shutdown decisions without Tk or game input."""
import unittest
import queue
from types import SimpleNamespace
from unittest.mock import Mock

from jiangshen_assistant.app import AssistantApp
from jiangshen_assistant.settings import AppSettings
from jiangshen_assistant.windows import WindowError, WindowInfo
from jiangshen_assistant.objects import ObjectEvent


class WindowBindingTests(unittest.TestCase):
    def shell(self, windows, bound=None):
        return SimpleNamespace(
            closing=False, runner=SimpleNamespace(is_active=False),
            objects=SimpleNamespace(is_busy=False, has_campaign_owner=False),
            settings=AppSettings(window_title="将神同名区服"),
            adapter=SimpleNamespace(find_windows=Mock(return_value=windows)),
            window_combo=Mock(), bound=bound, bind_selected=Mock(),
            _clear_binding=Mock(), log=Mock(),
        )

    def test_same_title_clients_do_not_silently_choose_first(self):
        clients = [WindowInfo(11, "将神同名区服", 101, "js.exe", 800, 600),
                   WindowInfo(22, "将神同名区服", 202, "js.exe", 800, 600)]
        shell = self.shell(clients)
        AssistantApp.refresh_windows(shell)
        shell.bind_selected.assert_not_called()
        shell._clear_binding.assert_called_once()
        shell = self.shell(clients, clients[1])
        AssistantApp.refresh_windows(shell)
        shell.window_combo.current.assert_called_once_with(1)
        shell.bind_selected.assert_called_once()

    def test_launcher_is_never_auto_bound_or_previewed(self):
        shell = self.shell([WindowInfo(11, "将神登陆器", 101, "Launcher.exe", 800, 600)])
        AssistantApp.refresh_windows(shell)
        shell.bind_selected.assert_not_called()
        self.assertEqual(shell.windows, [])
        shell._clear_binding.assert_called_once_with("检测到登录器，请进入游戏后刷新")

    def test_reused_handle_with_new_pid_requires_explicit_selection(self):
        previous = WindowInfo(11, "将神同名区服", 101, "js.exe", 800, 600)
        clients = [WindowInfo(11, "将神同名区服", 999, "js.exe", 800, 600),
                   WindowInfo(22, "将神同名区服", 202, "js.exe", 800, 600)]
        shell = self.shell(clients, previous)
        AssistantApp.refresh_windows(shell)
        shell.bind_selected.assert_not_called()


class ShutdownTests(unittest.TestCase):
    def test_close_waits_for_task_and_preview_before_destroying(self):
        handler = Mock()
        shell = SimpleNamespace(
            closing=False, runner=SimpleNamespace(is_active=True, stop=Mock()),
            objects=SimpleNamespace(is_shutdown=False, close=Mock()),
            campaign=SimpleNamespace(is_shutdown=True, close=Mock()),
            _objects_close_requested=False,
            adapter=SimpleNamespace(close=Mock()),
            preview_thread=Mock(), footer_status=Mock(), after=Mock(),
            log=Mock(), logger=SimpleNamespace(handlers=[handler]), destroy=Mock(),
            _poll_objects=Mock(), _poll_campaign=Mock(), _update_object_controls=Mock(), _update_run_controls=Mock(),
        )
        shell.preview_thread.is_alive.return_value = True
        shell._finish_close = lambda: AssistantApp._finish_close(shell)
        AssistantApp.close(shell)
        self.assertTrue(shell.closing)
        shell.runner.stop.assert_called_once()
        shell.destroy.assert_not_called()
        shell.after.assert_called_once()
        shell.runner.is_active = False
        shell._finish_close()
        shell.destroy.assert_not_called()
        shell.preview_thread.is_alive.return_value = False
        shell._finish_close()
        shell.destroy.assert_not_called()
        shell.objects.is_shutdown = True
        shell._finish_close()
        shell.destroy.assert_called_once()
        handler.close.assert_called_once()
        shell.objects.close.assert_called_once()
        shell.adapter.close.assert_called_once()
        shell.campaign.close.assert_called_once()

    def test_stop_reaches_object_call_when_task_runner_is_idle(self):
        shell = SimpleNamespace(
            objects=SimpleNamespace(stop=Mock(return_value=True)),
            campaign=SimpleNamespace(is_active=False),
            runner=SimpleNamespace(stop=Mock(return_value=False)),
            _update_object_controls=Mock(), _update_run_controls=Mock(), log=Mock(),
        )
        AssistantApp.stop(shell)
        shell.objects.stop.assert_called_once()
        shell.runner.stop.assert_called_once()
        shell.log.assert_called_once()

    def test_closing_blocks_new_work(self):
        shell = SimpleNamespace(closing=True)
        for method in (AssistantApp.start_check, AssistantApp.refresh_windows,
                       AssistantApp.bind_selected):
            method(shell)  # No runner, adapter or UI touched after closing starts.

    def test_hotkey_cleanup_error_does_not_skip_object_cleanup(self):
        shell = SimpleNamespace(
            closing=False, runner=SimpleNamespace(is_active=False, stop=Mock()),
            objects=SimpleNamespace(is_shutdown=True, close=Mock()),
            campaign=SimpleNamespace(is_shutdown=True, close=Mock()),
            _objects_close_requested=False,
            adapter=SimpleNamespace(close=Mock(side_effect=WindowError("quit not confirmed"))),
            preview_thread=None, footer_status=Mock(), after=Mock(), log=Mock(),
            logger=SimpleNamespace(handlers=[]), destroy=Mock(),
            _poll_objects=Mock(), _poll_campaign=Mock(), _update_object_controls=Mock(), _update_run_controls=Mock(),
        )
        shell._finish_close = lambda: AssistantApp._finish_close(shell)
        AssistantApp.close(shell)
        shell.objects.close.assert_called_once()
        shell.destroy.assert_called_once()
        self.assertFalse(shell.global_stop_available)
        self.assertTrue(any("释放未确认" in c.args[0] for c in shell.log.call_args_list))


class GlobalStopUiTests(unittest.TestCase):
    def shell(self):
        shell = SimpleNamespace(
            closing=False, global_stop_available=False,
            adapter=SimpleNamespace(start_stop_hotkey=Mock(), stop_pressed=Mock(return_value=False)),
            stop_key_label=Mock(), stop_key_settings=Mock(), log=Mock(), stop=Mock(),
            runner=SimpleNamespace(events=queue.Queue()), ui_events=queue.Queue(),
            _poll_objects=Mock(), _poll_campaign=Mock(), _poll=Mock(), after=Mock(),
        )
        shell._stop_hotkey_failed = lambda reason: AssistantApp._stop_hotkey_failed(shell, reason)
        return shell

    def test_registration_failure_is_visible_and_does_not_keep_retrying(self):
        shell = self.shell()
        shell.adapter.start_stop_hotkey.side_effect = WindowError("already registered")
        AssistantApp._start_stop_hotkey(shell)
        self.assertFalse(shell.global_stop_available)
        self.assertIn("不可用", shell.stop_key_label.configure.call_args.kwargs["text"])
        AssistantApp._poll(shell)
        shell.adapter.start_stop_hotkey.assert_called_once()
        shell.adapter.stop_pressed.assert_not_called()
        shell.log.assert_called_once()

    def test_registered_short_press_events_are_each_delivered_to_stop(self):
        shell = self.shell()
        AssistantApp._start_stop_hotkey(shell)
        self.assertTrue(shell.global_stop_available)
        shell.adapter.stop_pressed.side_effect = [True, True, False]
        for _ in range(3):
            AssistantApp._poll(shell)
        self.assertEqual(shell.stop.call_count, 2)
        shell.log.assert_not_called()

    def test_runtime_hotkey_failure_is_visible_once_and_stops_polling(self):
        shell = self.shell()
        shell.global_stop_available = True
        shell.adapter.stop_pressed.side_effect = WindowError("message thread failed")
        AssistantApp._poll(shell)
        AssistantApp._poll(shell)
        self.assertFalse(shell.global_stop_available)
        shell.adapter.stop_pressed.assert_called_once()
        shell.log.assert_called_once()


class CampaignUiSafetyTests(unittest.TestCase):
    def test_f8_routes_to_campaign_without_tearing_down_observation(self):
        shell = SimpleNamespace(
            campaign=SimpleNamespace(is_active=True, stop=Mock(return_value=True)),
            objects=SimpleNamespace(stop=Mock()), runner=SimpleNamespace(stop=Mock()),
            _update_campaign_controls=Mock(), _update_object_controls=Mock(),
            _update_run_controls=Mock(), log=Mock(),
        )
        AssistantApp.stop(shell)
        shell.campaign.stop.assert_called_once()
        shell.objects.stop.assert_not_called()
        shell.runner.stop.assert_not_called()

    def test_close_waits_for_campaign_before_closing_object_observer(self):
        shell = SimpleNamespace(
            closing=False, runner=SimpleNamespace(is_active=False, stop=Mock()),
            campaign=SimpleNamespace(is_shutdown=False, close=Mock()),
            objects=SimpleNamespace(is_shutdown=False, close=Mock()),
            adapter=SimpleNamespace(close=Mock()), _objects_close_requested=False,
            preview_thread=None, footer_status=Mock(), after=Mock(), log=Mock(),
            logger=SimpleNamespace(handlers=[]), destroy=Mock(),
            _poll_objects=Mock(), _poll_campaign=Mock(),
            _update_object_controls=Mock(), _update_run_controls=Mock(),
        )
        shell._finish_close = lambda: AssistantApp._finish_close(shell)
        AssistantApp.close(shell)
        shell.campaign.close.assert_called_once()
        shell.objects.close.assert_not_called()
        shell.destroy.assert_not_called()
        shell.campaign.is_shutdown = True
        shell._finish_close()
        shell.objects.close.assert_called_once()
        shell.destroy.assert_not_called()
        shell.objects.is_shutdown = True
        shell._finish_close()
        shell.objects.close.assert_called_once()
        shell.destroy.assert_called_once()

    def test_campaign_pause_uses_its_admission_gate(self):
        shell = SimpleNamespace(
            campaign=SimpleNamespace(is_active=True, is_paused=False),
            toggle_campaign_pause=Mock(), runner=Mock(),
        )
        AssistantApp.toggle_pause(shell)
        shell.toggle_campaign_pause.assert_called_once()
        shell.runner.pause.assert_not_called()

    def test_cleanup_owner_blocks_manual_work_even_after_runner_is_idle(self):
        shell = SimpleNamespace(
            closing=False, runner=SimpleNamespace(is_active=False),
            objects=SimpleNamespace(is_busy=False, has_campaign_owner=True),
            preview_busy=False,
        )
        for method in (AssistantApp.refresh_windows, AssistantApp.bind_selected,
                       AssistantApp.start_check):
            method(shell)

    def test_recovery_record_exposes_resume_instead_of_new_run(self):
        shell = SimpleNamespace(
            closing=False, bound=object(), object_snapshot={"level": 86},
            campaign=SimpleNamespace(is_active=False, is_paused=True,
                snapshot={"run_id": "saved-run", "losses": 2}),
            runner=SimpleNamespace(is_active=False),
            objects=SimpleNamespace(is_busy=False, has_campaign_owner=False, bridge_state="disconnected"),
            campaign_start_button=Mock(), campaign_pause_button=Mock(), campaign_stop_button=Mock(),
        )
        AssistantApp._update_campaign_controls(shell)
        self.assertEqual(shell.campaign_start_button.configure.call_args.kwargs["state"], "disabled")
        self.assertEqual(shell.campaign_pause_button.configure.call_args.kwargs["state"], "normal")
        self.assertEqual(shell.campaign_pause_button.configure.call_args.kwargs["text"], "继续")


class ObjectUiTests(unittest.TestCase):
    def shell(self):
        return SimpleNamespace(
            objects=SimpleNamespace(events=queue.Queue(), generation=2,
                                    bridge_state="ready", is_busy=False, can_verify=True, has_campaign_owner=False),
            campaign=SimpleNamespace(is_active=False),
            bound=WindowInfo(11, "将神", 101, "js.exe", 800, 600),
            object_snapshot=None, object_seen_monotonic=0,
            object_values={key: Mock() for key in ("level", "stamina", "gold")},
            object_state_badge=Mock(), object_observed_at=Mock(),
            runtime_badge=Mock(), runtime_gate=Mock(), object_action_result=Mock(),
            _update_object_controls=Mock(), _update_run_controls=Mock(), log=Mock(),
        )

    def test_late_ready_and_action_events_cannot_reopen_old_generation(self):
        shell = self.shell()
        shell.objects.events.put(ObjectEvent("bridge", data={"generation": 1, "state": "ready"}))
        shell.objects.events.put(ObjectEvent("action", data={"generation": 1, "state": "complete"}))
        AssistantApp._poll_objects(shell)
        shell.runtime_badge.configure.assert_not_called()
        shell.object_action_result.configure.assert_not_called()

    def test_expired_player_snapshot_is_cleared_without_waiting_for_worker(self):
        shell = self.shell()
        shell.object_snapshot = {"level": 86}
        AssistantApp._poll_objects(shell)
        self.assertIsNone(shell.object_snapshot)
        for widget in shell.object_values.values():
            self.assertEqual(widget.configure.call_args.kwargs["text"], "—")

    def test_stale_state_blocks_verification_and_unknown_keeps_stop_available(self):
        shell = self.shell()
        shell.closing = False
        shell.runner = SimpleNamespace(is_active=False)
        shell.runtime_connect_button = Mock()
        shell.runtime_verify_button = Mock()
        shell.runtime_disconnect_button = Mock()
        shell.object_refresh_button = Mock()
        shell.object_stop_button = Mock()
        AssistantApp._update_object_controls(shell)
        self.assertEqual(shell.runtime_verify_button.configure.call_args.kwargs["state"], "disabled")
        shell.objects.bridge_state = "unknown"
        AssistantApp._update_object_controls(shell)
        self.assertEqual(shell.object_stop_button.configure.call_args.kwargs["state"], "normal")


if __name__ == "__main__":
    unittest.main()
