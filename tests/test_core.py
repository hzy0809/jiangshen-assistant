import queue
import threading
import unittest

from jiangshen_assistant.core import RunnerState, TASK_CATALOG, TaskRunner


class TaskRunnerTests(unittest.TestCase):
    def setUp(self):
        self.runner = TaskRunner()

    def tearDown(self):
        self.runner.stop()
        self.runner.join(1)
        self.assertFalse(self.runner.is_active, "test left a task running")

    def events(self):
        events = []
        while True:
            try:
                events.append(self.runner.events.get_nowait())
            except queue.Empty:
                return events

    def test_double_start_refused_and_wait_cancelled(self):
        entered = threading.Event()

        def task(context):
            entered.set()
            context.wait(60)

        self.assertTrue(self.runner.start("waiting", task))
        self.assertTrue(entered.wait(1))
        self.assertFalse(self.runner.start("duplicate", lambda context: None))
        self.assertTrue(self.runner.stop())
        self.assertFalse(self.runner.stop())
        self.runner.join(0.5)
        self.assertEqual(self.runner.state, RunnerState.IDLE)
        events = self.events()
        self.assertIn("stopping", [e.state for e in events if e.kind == "state"])
        self.assertEqual([e.message for e in events if e.kind == "done"], ["stopped"])

    def test_paused_checkpoint_is_interruptible_by_stop(self):
        entered = threading.Event()
        proceed = threading.Event()
        passed = threading.Event()

        def task(context):
            entered.set()
            proceed.wait(1)
            context.checkpoint()
            passed.set()

        self.assertTrue(self.runner.start("checkpoint", task))
        self.assertTrue(entered.wait(1))
        self.assertTrue(self.runner.pause())
        proceed.set()
        self.assertFalse(passed.wait(0.05))
        self.assertEqual(self.runner.state, RunnerState.PAUSED)
        self.assertTrue(self.runner.stop())
        self.runner.join(0.5)
        self.assertFalse(passed.is_set())
        self.assertEqual(self.runner.state, RunnerState.IDLE)
        self.assertEqual([e.message for e in self.events() if e.kind == "done"], ["stopped"])

    def test_wait_freezes_while_paused_and_resumes(self):
        entered = threading.Event()
        proceed = threading.Event()
        finished = threading.Event()

        def task(context):
            entered.set()
            proceed.wait(1)
            context.wait(0.2)
            finished.set()

        self.runner.start("pause wait", task)
        self.assertTrue(entered.wait(1))
        self.assertTrue(self.runner.pause())
        proceed.set()
        self.assertFalse(finished.wait(0.25))
        self.assertFalse(self.runner.pause())
        self.assertTrue(self.runner.resume())
        self.assertFalse(self.runner.resume())
        self.assertFalse(finished.wait(0.04))
        self.assertTrue(finished.wait(1))
        self.runner.join(0.5)
        self.assertEqual([e.message for e in self.events() if e.kind == "done"], ["completed"])

    def test_stop_interrupts_wait_while_paused(self):
        entered = threading.Event()

        def task(context):
            entered.set()
            context.wait(60)

        self.runner.start("pause cancel", task)
        self.assertTrue(entered.wait(1))
        self.assertTrue(self.runner.pause())
        self.assertTrue(self.runner.stop())
        self.runner.join(0.5)
        self.assertFalse(self.runner.is_active)

    def test_exception_emits_error_and_returns_idle_then_can_restart(self):
        def fail(context):
            raise RuntimeError("expected failure")

        self.assertTrue(self.runner.start("failure", fail))
        self.runner.join(1)
        self.assertEqual(self.runner.state, RunnerState.IDLE)
        events = self.events()
        error = next(e for e in events if e.kind == "log")
        self.assertEqual(error.level, "ERROR")
        self.assertIn("RuntimeError: expected failure", error.message)
        self.assertEqual(events[-1].kind, "done")
        self.assertEqual(events[-1].message, "failed")
        self.assertEqual(events[-1].state, "idle")
        self.assertTrue(self.runner.start("again", lambda context: context.log("hello")))
        self.runner.join(1)
        events = self.events()
        self.assertTrue(all(event.timestamp for event in events))
        self.assertEqual([e.message for e in events if e.kind == "done"], ["completed"])
        self.assertIn("hello", [e.message for e in events if e.kind == "log"])

    def test_inactive_transitions_refused_and_catalog_matches_available_campaign(self):
        self.assertFalse(self.runner.pause())
        self.assertFalse(self.runner.resume())
        self.assertFalse(self.runner.stop())
        self.runner.join()
        self.assertEqual({item.id for item in TASK_CATALOG}, {"daily_routine", "battle_progress"})
        self.assertEqual({item.category for item in TASK_CATALOG}, {"daily", "battle"})
        self.assertEqual([item.id for item in TASK_CATALOG if item.available], ['battle_progress'])

    def test_reconciler_observes_paused_and_cancelled_flags_without_blocking(self):
        entered, proceed, finished = threading.Event(), threading.Event(), threading.Event()
        flags = []
        def task(context):
            entered.set()
            proceed.wait(1)
            flags.append((context.is_paused, context.cancelled))
            finished.set()
        self.runner.start('reconciler', task, checkpoints=False)
        self.assertTrue(entered.wait(1))
        self.runner.pause()
        proceed.set()
        self.assertTrue(finished.wait(1))
        self.runner.join(1)
        self.assertEqual(flags, [(True, False)])
        self.assertEqual(self.runner.state, RunnerState.IDLE)


if __name__ == "__main__":
    unittest.main()
