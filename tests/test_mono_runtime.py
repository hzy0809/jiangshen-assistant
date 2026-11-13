"""Offline numeric-schema and identity checks; never open a live process."""
import struct
import unittest
from unittest.mock import Mock, patch

from jiangshen_assistant import mono_runtime as mono


class Memory:
    def __init__(self):
        self.values = {}

    def read(self, address, size, purpose):
        return self.values[(address, size)]

    def pointer(self, address, purpose):
        return mono.u32(self.read(address, 4, purpose))


class FieldTests(unittest.TestCase):
    def fixture(self, name="level"):
        memory = Memory()
        klass, array, typ, label = 0x500000, 0x600000, 0x700000, 0x710000
        spec = mono.FIELD_SPECS[name]
        descriptor = array + spec["ordinal"] * 16
        info = {"class_failure_offset": 0x20, "field_count_offset": 0x64,
                "field_array_offset": 0x74, "field_stride": 16}
        memory.values[(klass+0x20, 1)] = b"\0"
        memory.values[(klass+0x64, 4)] = struct.pack("<I", 300)
        memory.values[(klass+0x74, 4)] = struct.pack("<I", array)
        memory.values[(descriptor, 16)] = struct.pack("<4I", typ, label, klass, 96)
        memory.values[(label, len(name)+1)] = name.encode("ascii") + b"\0"
        memory.values[(typ, 8)] = struct.pack("<IHBB", 0, spec["flags"], spec["mono_type"], 0)
        return memory, info, klass, descriptor, typ

    def test_all_numeric_fields_use_exact_own_ordinals_and_types(self):
        expected = {"level": (14, 6), "stamina": (18, 6), "gold": (20, 8),
                    "bind_gold": (21, 8), "money": (22, 8), "mana": (33, 8), "money64": (269, 10)}
        for name, (ordinal, kind) in expected.items():
            with self.subTest(name=name):
                memory, info, klass, _, _ = self.fixture(name)
                field = mono.read_field(memory, info, klass, name)
                self.assertEqual((field["declared_ordinal"], field["type_enum"]), (ordinal, kind))

    def test_field_identity_type_flags_and_special_offset_fail_closed(self):
        for condition in ("parent", "type", "static", "byref", "offset", "name"):
            with self.subTest(condition=condition):
                memory, info, klass, desc, typ = self.fixture()
                if condition == "parent":
                    memory.values[(desc, 16)] = struct.pack("<4I", typ, 0x710000, klass+16, 96)
                elif condition == "offset":
                    memory.values[(desc, 16)] = struct.pack("<4I", typ, 0x710000, klass, 0xffffffff)
                elif condition == "name":
                    memory.values[(0x710000, 6)] = b"other\0"
                else:
                    memory.values[(typ, 8)] = struct.pack("<IHBB", 0, 22 if condition == "static" else 6,
                                                         8 if condition == "type" else 6, 64 if condition == "byref" else 0)
                with self.assertRaises(mono.Refusal):
                    mono.read_field(memory, info, klass, "level")

    def test_reader_rejects_out_of_bounds_before_any_kernel_api(self):
        reader = mono.Reader.__new__(mono.Reader)
        reader.read_calls = reader.read_bytes = 0
        for address, size in ((0, 4), (0x10000, 257), (0xffffffff, 4)):
            with self.assertRaises(mono.Refusal):
                reader.read(address, size, "offline negative")
        reader.read_calls = mono.MAX_READ_CALLS
        with self.assertRaises(mono.Refusal):
            reader.read(0x10000, 4, "offline exhausted budget")


class IdentityTests(unittest.TestCase):
    def test_access_failure_is_unknown_not_process_exit(self):
        with patch.object(mono, "process_creation", side_effect=mono.Refusal("access denied")):
            self.assertEqual(mono.process_status(100, 123), "unknown")
        with patch.object(mono, "process_creation", side_effect=mono.TargetGone("gone")):
            self.assertEqual(mono.process_status(100, 123), "gone")
        with patch.object(mono, "process_creation", return_value=124):
            self.assertEqual(mono.process_status(100, 123), "gone")

    def test_pid_reuse_refuses_before_scalar_reader_is_opened(self):
        with patch.object(mono, "_read_types", return_value={"status": "PASS", "process_creation_filetime": 999}), \
                patch.object(mono, "_type_layout", return_value={}), patch.object(mono, "Reader") as reader:
            with self.assertRaises(mono.Refusal):
                mono._read_scalars(100, Mock(), {}, expected_creation=123)
            reader.assert_not_called()

    def test_public_result_adds_both_money_fields(self):
        runtime = mono.MonoRuntime()
        fake_path = Mock()
        fake_path.stat.return_value = Mock(st_size=1, st_mtime_ns=2, st_ctime_ns=3)
        fake_path.__truediv__ = Mock(return_value=fake_path)
        fake_result = {"process_creation_filetime": 123, "observed_at_utc": "2026-10-31T00:00:00+00:00",
                       "values": {"level": 86, "stamina": 20, "gold": 375, "bind_gold": 2,
                                  "money": 3, "money64": 1 << 35, "mana": 5}}
        with patch.object(mono, "MONO", fake_path), patch.object(mono, "INSTALL", fake_path), \
                patch.object(mono, "PE"), patch.object(mono, "_scalar_layout", return_value={}), \
                patch.object(mono, "_read_scalars", return_value=fake_result) as read:
            result = runtime.read(100, expected_creation=123)
        self.assertEqual(result["money"], 3 + (1 << 35))
        self.assertEqual(result["observed_at"], fake_result["observed_at_utc"])
        read.assert_called_once()


if __name__ == "__main__":
    unittest.main()
