from types import SimpleNamespace
from unittest import TestCase
from unittest.mock import Mock, patch
import queue
import tkinter as tk

from jiangshen_assistant.app import AssistantApp
from jiangshen_assistant.daily_workflow import DailyItemResult, DailyResult


class DailyUiTests(TestCase):
    def test_donation_choices_default_to_100000_coins(self):
        class Shell(tk.Frame):
            label = AssistantApp.label
            button = AssistantApp.button
            card = AssistantApp.card
            _battle_actions = AssistantApp._battle_actions
            _build_daily = AssistantApp._build_daily

            def page(self, key, title):
                return tk.Frame(self, bg='#0c121c')

            def start_daily(self): pass
            def toggle_daily_pause(self): pass
            def stop(self): pass

        root = tk.Tk()
        root.withdraw()
        shell = Shell(root)
        try:
            shell._build_daily()
            self.assertEqual(shell.daily_donation.get(), '10 万钱币')
            self.assertEqual(tuple(shell.daily_donation_combo.cget('values')),
                             ('1000 钱币', '10 万钱币', '80 元宝'))
        finally:
            shell.daily_donation = None
            shell.destroy()
            root.destroy()

    def test_explicit_donation_choice_is_bound_on_start(self):
        daily = SimpleNamespace(start=Mock(return_value=True))
        shell = SimpleNamespace(closing=False, bound=object(), daily=daily,
            daily_donation=SimpleNamespace(get=lambda: '80 元宝'), log=Mock(),
            _update_daily_controls=Mock(), _update_run_controls=Mock())
        with patch('jiangshen_assistant.app.DAILY_ACTIONS_READY', True):
            AssistantApp.start_daily(shell)
        daily.start.assert_called_once_with(3)

    def test_unverified_feature_gate_refuses_to_start(self):
        daily = SimpleNamespace(start=Mock())
        shell = SimpleNamespace(closing=False, bound=object(), daily=daily)
        with patch('jiangshen_assistant.app.DAILY_ACTIONS_READY', False):
            AssistantApp.start_daily(shell)
        daily.start.assert_not_called()

    def test_unsupported_items_are_reported_instead_of_called_complete(self):
        events = queue.Queue()
        events.put(DailyResult('PARTIAL', 'daily_items_uncompleted', items=(
            DailyItemResult('task', 5, 20, '', 'UNSUPPORTED', 'daily_task_route_unsupported', 0, 1),)))
        shell = SimpleNamespace(daily=SimpleNamespace(events=events), daily_status=Mock(),
            daily_results=Mock(), log=Mock(), _update_daily_controls=Mock())
        AssistantApp._poll_daily(shell)
        text = shell.daily_results.insert.call_args.args[1]
        self.assertIn('同盟捐献', text)
        self.assertIn('尚未接入', text)
        self.assertNotIn('已完成', text)

    def test_stop_routes_to_active_daily_owner(self):
        daily = SimpleNamespace(is_active=True, stop=Mock())
        objects = SimpleNamespace(stop=Mock())
        shell = SimpleNamespace(daily=daily, objects=objects, log=Mock(),
            _update_daily_controls=Mock(), _update_object_controls=Mock(), _update_run_controls=Mock())
        AssistantApp.stop(shell)
        daily.stop.assert_called_once_with()
        objects.stop.assert_not_called()

    def test_close_waits_for_daily_owner_before_closing_objects(self):
        shell = SimpleNamespace(_poll_objects=Mock(), _poll_campaign=Mock(), _poll_challenge=Mock(),
            campaign=SimpleNamespace(is_shutdown=True), challenge=SimpleNamespace(is_shutdown=True),
            daily=SimpleNamespace(is_shutdown=False), objects=SimpleNamespace(close=Mock()),
            _finish_close=Mock(), after=Mock())
        AssistantApp._finish_close(shell)
        shell.objects.close.assert_not_called()
        shell.after.assert_called_once_with(80, shell._finish_close)
