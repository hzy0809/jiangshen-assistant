"""Offline PE/export/seal and saved synthetic receipt checks; never loads a DLL."""
import hashlib
import importlib.util
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "build"
spec = importlib.util.spec_from_file_location(
    "existing_pe_inspector", HERE.parents[3] / "tools" / "runtime_binary_inventory.py")
pe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pe)
required = {"JSB_Arm", "JSB_Fire", "JSB_Cancel", "JSB_ReadStatus", "JSB_Close"}
forbidden = {"CreateRemoteThread", "VirtualAllocEx", "WriteProcessMemory",
             "SetThreadContext", "SuspendThread", "TerminateThread"}
results = []
for name in ("bridge_selftest.dll", "bridge_selftest.exe", "bridge.dll", "bridge_host.exe"):
    path = OUT / name
    if not path.exists():
        continue
    info = pe.inspect(path)
    assert info["machine"] == "0x014c" and info["optional_magic"] == "0x010b", name
    exports = {e["name"] for e in info["exports"]}
    if path.suffix == ".dll":
        assert required <= exports, (name, required - exports)
    imports = {n for d in info["imports"] for n in d["names"]}
    assert not forbidden & imports, (name, forbidden & imports)
    results.append({"file": name, "machine": info["machine"], "sha256": info["sha256"],
                    "exports": sorted(exports), "import_dlls": [d["dll"] for d in info["imports"]]})
receipt = [json.loads(line) for line in (OUT / "synthetic-results.jsonl").read_text(encoding="utf-8-sig").splitlines() if line]
events = {r["event"]: r for r in receipt}
assert events["synthetic_summary"]["pass"] is True
one = events["synthetic_one_shot"]
assert one["tid"] == one["callback_tid"] and one["calls"] == 1 and one["callback_finished"] == 1
assert events["synthetic_cancelled"]["calls"] == 0
pending = events["synthetic_timeout_pending"]
assert pending["state"] == 2 and pending["callback_finished"] == 0 and pending["hook_removed"] == 1
finished = events["synthetic_timeout_finished"]
assert finished["state"] == 3 and finished["callback_finished"] == 1 and finished["calls"] == 1
sealed = None
manifest_path = OUT / "build-manifest.json"
if manifest_path.exists():
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    sealed = hashlib.sha256((OUT / "JSUnityProbe.dll").read_bytes()).hexdigest()
    assert manifest["payload_sha256"] == sealed
    assert f'"{sealed}"' in (OUT / "payload_seal.h").read_text(encoding="ascii")
    for item in manifest["artifacts"]:
        assert item["sha256"] == hashlib.sha256((OUT / item["file"]).read_bytes()).hexdigest()
    for item in manifest["sources"]:
        assert item["sha256"] == hashlib.sha256((HERE / item["file"]).read_bytes()).hexdigest()
output = {"status": "PASS", "scope": "offline PE/export/seal + saved independent synthetic receipts",
          "game_access": False, "payload_sha256": sealed, "artifacts": results,
          "synthetic_pid": one["pid"], "synthetic_tid": one["tid"],
          "unverified": ["live Mono API execution", "Unity Update readiness", "UI action", "game cleanup"]}
(OUT / "offline-validation.json").write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"status": "PASS", "artifacts_checked": len(results), "payload_sha256": sealed}))
