from dataclasses import asdict
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from jiangshen_assistant.settings import AppSettings, SettingsStore


class SettingsStoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name) / "app-data"
        self.store = SettingsStore(self.directory)

    def tearDown(self):
        self.temporary.cleanup()

    def write_raw(self, raw):
        self.directory.mkdir(parents=True, exist_ok=True)
        self.store.path.write_bytes(raw)

    def test_defaults_do_not_create_a_file(self):
        self.assertEqual(self.store.load(), AppSettings())
        self.assertEqual(self.store.last_warning, "")
        self.assertFalse(self.directory.exists())

    def test_atomic_roundtrip_with_unicode_and_schema(self):
        expected = AppSettings("将神", "将神 - 测试窗口", 1250, "F8")
        self.store.save(expected)
        self.assertEqual(self.store.load(), expected)
        document = json.loads(self.store.path.read_text(encoding="utf-8"))
        self.assertEqual(document, {"schema_version": 1, **asdict(expected)})
        self.assertEqual(list(self.directory.glob("*.tmp")), [])

    def test_failed_atomic_replace_retains_previous_file_and_cleans_temporary(self):
        self.store.save(AppSettings(window_title="before"))
        original = self.store.path.read_bytes()
        with patch("jiangshen_assistant.settings.os.replace", side_effect=OSError("blocked")):
            with self.assertRaisesRegex(OSError, "blocked"):
                self.store.save(AppSettings(window_title="after"))
        self.assertEqual(self.store.path.read_bytes(), original)
        self.assertEqual(list(self.directory.glob("*.tmp")), [])

    def test_malformed_and_future_versions_preserve_original_and_backup(self):
        samples = [
            b"{broken", b"\xff", b"[]", b'{"schema_version": 2, "future": true}',
            b"[" * 1500 + b"]" * 1500,
        ]
        for raw in samples:
            with self.subTest(raw=raw):
                self.write_raw(raw)
                self.assertEqual(self.store.load(), AppSettings())
                self.assertTrue(self.store.last_warning)
                self.assertEqual(self.store.path.read_bytes(), raw)
                backups = list(self.directory.glob("settings.invalid-*.json"))
                self.assertTrue(any(path.read_bytes() == raw for path in backups))
                self.store.save(AppSettings(window_title="recovered"))
                self.assertEqual(self.store.load().window_title, "recovered")
                self.assertTrue(any(path.read_bytes() == raw for path in backups))

    def test_invalid_types_and_ranges_are_rejected(self):
        invalid_values = [
            {"schema_version": True},
            {"schema_version": 1, "window_keyword": 10},
            {"schema_version": 1, "window_title": None},
            {"schema_version": 1, "preview_interval_ms": True},
            {"schema_version": 1, "preview_interval_ms": 1.5},
            {"schema_version": 1, "preview_interval_ms": -1},
            {"schema_version": 1, "preview_interval_ms": 60001},
            {"schema_version": 1, "stop_hotkey": "F9"},
        ]
        for document in invalid_values:
            with self.subTest(document=document):
                self.write_raw(json.dumps(document).encode("utf-8"))
                self.assertEqual(self.store.load(), AppSettings())
                self.assertTrue(self.store.last_warning)

    def test_save_rejects_invalid_values_without_modifying_existing_file(self):
        self.store.save(AppSettings(window_title="before"))
        original = self.store.path.read_bytes()
        with self.assertRaises(ValueError):
            self.store.save(AppSettings(preview_interval_ms=True))
        self.assertEqual(self.store.path.read_bytes(), original)

    def test_save_without_load_preserves_unsupported_existing_document(self):
        raw = b'{"schema_version":99,"new_feature":"retain"}'
        self.write_raw(raw)
        self.store.save(AppSettings())
        backups = list(self.directory.glob("settings.invalid-*.json"))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_bytes(), raw)

    def test_failed_backup_blocks_replacement_of_only_original(self):
        raw = b"{broken"
        self.write_raw(raw)
        with patch.object(self.store, "_preserve", side_effect=OSError("disk full")):
            self.assertEqual(self.store.load(), AppSettings())
            self.assertIn("disk full", self.store.last_warning)
            with self.assertRaisesRegex(OSError, "disk full"):
                self.store.save(AppSettings())
        self.assertEqual(self.store.path.read_bytes(), raw)


if __name__ == "__main__":
    unittest.main()
