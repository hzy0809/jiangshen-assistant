"""Read packaged PE headers/exports/imports and seals without loading DLLs."""
from pathlib import Path
import hashlib
import json
import struct
import sys

HERE = Path(__file__).resolve().parent
PACKAGE = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parents[1] / "jiangshen_assistant" / "runtime" / "win32"
METADATA = Path(sys.argv[2]) if len(sys.argv) > 2 else HERE / "build" / "managed-metadata.json"
OUTPUT = Path(sys.argv[3]) if len(sys.argv) > 3 else HERE / "build" / "asset-verification.json"
manifest = json.loads((PACKAGE / "manifest.json").read_text(encoding="utf-8-sig"))
assert manifest["protocol"] == 3 and manifest["native_protocol"] == 2
assert manifest["campaign_interface"] == "scoped-campaign-native-flow-v2"
assert manifest["daily_interface"] == "activity-ui-observation-v1"
assert manifest["daily_guild_interface"] == "scoped-guild-daily-v1"
assert manifest["daily_refresh_interface"] == "scoped-daily-refresh-v1"
assert manifest["daily_reward_interface"] == "scoped-daily-reward-v1"
assert type(manifest["daily_reward_enabled"]) is bool
assert manifest["daily_timed_interface"] == "scoped-daily-timed-v1"
assert type(manifest["daily_timed_enabled"]) is bool
assert manifest["daily_stone_interface"] == "scoped-daily-stone-v1"
assert type(manifest["daily_stone_enabled"]) is bool
assert manifest["daily_mingshi_interface"] == "scoped-daily-mingshi-v1"
assert type(manifest["daily_mingshi_enabled"]) is bool
assert manifest["daily_arena_interface"] == "scoped-daily-arena-v1"
assert type(manifest["daily_arena_enabled"]) is bool
assert manifest["daily_soul_interface"] == "scoped-daily-soul-v1"
assert type(manifest["daily_soul_enabled"]) is bool
source_identity = "".join(manifest["sources"].values())
if manifest["daily_reward_enabled"]:
    source_identity += "daily_reward=enabled"
if manifest["daily_timed_enabled"]:
    source_identity += "daily_timed=enabled"
if manifest["daily_stone_enabled"]:
    source_identity += "daily_stone=enabled"
if manifest["daily_mingshi_enabled"]:
    source_identity += "daily_mingshi=enabled"
if manifest["daily_arena_enabled"]:
    assert not any(manifest[f"daily_{name}_enabled"] for name in ("reward", "timed", "stone", "mingshi", "soul"))
    source_identity += "daily_arena=enabled"
if manifest["daily_soul_enabled"]:
    assert not any(manifest[f"daily_{name}_enabled"] for name in ("reward", "timed", "stone", "mingshi", "arena"))
    source_identity += "daily_soul=enabled"
assert hashlib.sha256(source_identity.encode("ascii")).hexdigest() == manifest["build_id"]
assert manifest["bridge_id"] == "jiangshen-assistant-win32-v1"
expected_files = {"bridge.dll", "bridge_host.exe", "JSAssistantBridge.dll"}
assert set(manifest["files"]) == expected_files
required = {"JSB_Arm", "JSB_Fire", "JSB_Cancel", "JSB_ReadStatus", "JSB_Close"}
forbidden = {"CreateRemoteThread", "WriteProcessMemory", "VirtualAllocEx", "SetThreadContext", "TerminateThread"}


def inspect(path):
    data = path.read_bytes()
    u16 = lambda pos: struct.unpack_from("<H", data, pos)[0]
    u32 = lambda pos: struct.unpack_from("<I", data, pos)[0]
    pe = u32(0x3C)
    assert data[pe:pe + 4] == b"PE\0\0" and u16(pe + 4) == 0x14C
    opt = pe + 24
    assert u16(opt) == 0x10B
    sec = opt + u16(pe + 20)
    sections = []
    for index in range(u16(pe + 6)):
        p = sec + index * 40
        sections.append((u32(p + 12), max(u32(p + 8), u32(p + 16)), u32(p + 20)))

    def offset(rva):
        for start, size, raw in sections:
            if start <= rva < start + size:
                return raw + rva - start
        if rva < u32(opt + 60):
            return rva
        raise AssertionError(f"unmapped RVA {rva:x}")

    def string(pos):
        return data[pos:data.index(b"\0", pos)].decode("ascii")

    exports, imports = [], {}
    if u32(opt + 96):
        table = offset(u32(opt + 96))
        names = offset(u32(table + 32))
        exports = [string(offset(u32(names + n * 4))) for n in range(u32(table + 24))]
    if u32(opt + 104):
        table = offset(u32(opt + 104))
        while any(data[table:table + 20]):
            dll = string(offset(u32(table + 12)))
            thunk = offset(u32(table) or u32(table + 16))
            names = []
            while u32(thunk):
                value = u32(thunk)
                names.append(f"ordinal:{value & 0xffff}" if value & 0x80000000 else string(offset(value) + 2))
                thunk += 4
            imports[dll] = names
            table += 20
    assert not forbidden.intersection(n for names in imports.values() for n in names)
    return data, {"file": path.name, "machine": "0x014c", "exports": exports, "import_dlls": list(imports)}


artifacts = []
for filename, expected_hash in manifest["files"].items():
    data, result = inspect(PACKAGE / filename)
    assert hashlib.sha256(data).hexdigest() == expected_hash, filename
    result["sha256"] = expected_hash
    if filename == "bridge.dll":
        assert required <= set(result["exports"])
        assert manifest["files"]["JSAssistantBridge.dll"].encode() in data
        assert manifest["managed_assembly_name"].encode() in data
    artifacts.append(result)
for filename, expected_hash in manifest["sources"].items():
    assert hashlib.sha256((HERE / filename).read_bytes()).hexdigest() == expected_hash, filename
metadata = json.loads(METADATA.read_text(encoding="utf-8-sig"))
assert metadata["status"] == "PASS" and all(metadata["checks"].values())
assert metadata["payload_sha256"] == manifest["files"]["JSAssistantBridge.dll"]
results = {"status": "PASS", "scope": "packaged files and exact current sources; no game access",
           "build_id": manifest["build_id"], "artifacts": artifacts}
OUTPUT.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"status": "PASS", "build_id": manifest["build_id"], "artifacts_checked": len(artifacts)}))
