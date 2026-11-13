"""Bounded external Mono metadata reader. Default mode is entirely offline.

Live mode requires an explicit PID, an exact installed-image match, and --live.
No managed function is called. Only process/module query and read APIs exist here.
The root slot and field offsets are derived from pinned exported accessor bytes.
Assembly traversal follows the exact pinned native foreach implementation.
"""
from __future__ import annotations

import argparse
import ctypes as C
from ctypes import wintypes as W
import hashlib
import json
from pathlib import Path
import struct
import sys
from datetime import datetime, timezone

INSTALL = Path(r"C:\Users\yu\AppData\Local\JiangShen\pc")
MONO = INSTALL / "js_Data" / "Mono" / "mono.dll"
MONO_SHA256 = "51941db23fd29380039924d1a44964fec18ab2a80e72f14410fe0163d2cae500"
JS_SHA256 = "5cc05ce27aa4d2fc0474603b4867fb00e3247fa437e459ef36a7075eab4bbc70"
MAX_READ_BYTES = 65536
MAX_READ_CALLS = 1024
MAX_ASSEMBLIES = 96


class Refusal(RuntimeError):
    pass


def u32(data: bytes, offset: int = 0) -> int:
    return struct.unpack_from("<I", data, offset)[0]


class PE:
    """Read file-backed PE32 exports; does not load a DLL."""
    def __init__(self, path: Path, expected_hash: str):
        self.path = path
        self.data = path.read_bytes()
        self.sha256 = hashlib.sha256(self.data).hexdigest()
        if self.sha256 != expected_hash:
            raise Refusal(f"Pinned module hash mismatch: {path}")
        data = self.data
        pe = u32(data, 0x3c)
        if data[:2] != b"MZ" or data[pe:pe+4] != b"PE\0\0":
            raise Refusal("Invalid PE signature")
        if struct.unpack_from("<H", data, pe + 4)[0] != 0x14c:
            raise Refusal("Expected x86 PE")
        count = struct.unpack_from("<H", data, pe + 6)[0]
        opt_size = struct.unpack_from("<H", data, pe + 20)[0]
        opt = pe + 24
        if struct.unpack_from("<H", data, opt)[0] != 0x10b:
            raise Refusal("Expected PE32 optional header")
        self.base = u32(data, opt + 28)
        self.image_size = u32(data, opt + 56)
        self.sections = []
        for i in range(count):
            off = opt + opt_size + i * 40
            self.sections.append((u32(data, off+12), u32(data, off+16), u32(data, off+20)))
        exp_rva = u32(data, opt + 96)
        exp = self.offset(exp_rva)
        n_names = u32(data, exp + 24)
        funcs, names, ords = (u32(data, exp + x) for x in (28, 32, 36))
        self.exports = {}
        for i in range(n_names):
            name = self.cstring(u32(data, self.offset(names + i*4)))
            ordinal = struct.unpack_from("<H", data, self.offset(ords + i*2))[0]
            self.exports[name] = u32(data, self.offset(funcs + ordinal*4))

    def offset(self, rva: int) -> int:
        for start, raw_size, raw in self.sections:
            if start <= rva < start + raw_size:
                return raw + rva - start
        raise Refusal(f"RVA not file backed: {rva:#x}")

    def cstring(self, rva: int) -> str:
        offset = self.offset(rva)
        end = self.data.find(b"\0", offset, offset + 256)
        if end < 0:
            raise Refusal("Unterminated export name")
        return self.data[offset:end].decode("ascii")

    def code(self, name: str, size: int) -> bytes:
        off = self.offset(self.exports[name])
        return self.data[off:off+size]


def offline_layout(pe: PE) -> dict:
    code = pe.code("mono_get_root_domain", 6)
    if code[0] != 0xa1 or code[5] != 0xc3:
        raise Refusal("Unsupported mono_get_root_domain accessor")
    slot_rva = u32(code, 1) - pe.base
    if not 0 <= slot_rva < pe.image_size - 4:
        raise Refusal("Root-domain slot is outside image")
    result = {"root_domain_slot_rva": slot_rva, "accessors": {}}
    for name in ("mono_assembly_get_image", "mono_image_get_name", "mono_class_get_name", "mono_class_get_namespace"):
        code = pe.code(name, 11)
        if code[:8] != bytes.fromhex("558bec8b45088b40") or code[9:] != b"\x5d\xc3":
            raise Refusal(f"Unsupported accessor: {name}")
        result["accessors"][name] = {"rva": pe.exports[name], "offset": code[8], "bytes": code.hex()}
    result["accessors"]["mono_get_root_domain"] = {"rva": pe.exports["mono_get_root_domain"], "bytes": pe.code("mono_get_root_domain", 6).hex()}
    # At this pinned mono_assembly_foreach call site: push user_data; push
    # callback; push [loaded_assemblies]; call the native foreach below.
    # The helper explicitly reads node data at +0 and next at +4.
    callsite_rva = pe.exports["mono_assembly_foreach"] + 0x26
    callsite = pe.data[pe.offset(callsite_rva):pe.offset(callsite_rva)+23]
    if callsite[:13] != bytes.fromhex("ff750cff7508ff35f8231f10e8"):
        raise Refusal("Unsupported mono_assembly_foreach call site")
    helper_rva = callsite_rva + 17 + struct.unpack_from("<i", callsite, 13)[0]
    helper = bytes.fromhex("568b742408eb0fff742410ff36ff5424148b7604595985f675ed5ec3")
    if pe.data[pe.offset(helper_rva):pe.offset(helper_rva)+len(helper)] != helper:
        raise Refusal("Unsupported native assembly list foreach")
    assembly_slot_rva = u32(callsite, 8) - pe.base
    if not 0 <= assembly_slot_rva < pe.image_size - 4:
        raise Refusal("Assembly slot is outside image")
    result.update({"assembly_slot_rva": assembly_slot_rva,
                   "assembly_foreach_callsite_rva": callsite_rva,
                   "assembly_foreach_callsite_bytes": callsite[:17].hex(),
                   "list_foreach_rva": helper_rva, "list_foreach_bytes": helper.hex(),
                   "list_data_offset": 0, "list_next_offset": 4})
    return result


class MODULEENTRY32W(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("th32ModuleID", W.DWORD),
                ("th32ProcessID", W.DWORD), ("GlblcntUsage", W.DWORD),
                ("ProccntUsage", W.DWORD), ("modBaseAddr", C.c_void_p),
                ("modBaseSize", W.DWORD), ("hModule", W.HMODULE),
                ("szModule", W.WCHAR * 256), ("szExePath", W.WCHAR * 260)]


class Reader:
    def __init__(self, pid: int):
        if sys.platform != "win32":
            raise Refusal("Live mode requires Windows")
        self.k = C.WinDLL("kernel32", use_last_error=True)
        # Exact allowlist: process query, read memory, module enumeration, close.
        self.k.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
        self.k.OpenProcess.restype = W.HANDLE
        self.k.ReadProcessMemory.argtypes = [W.HANDLE, C.c_void_p, C.c_void_p, C.c_size_t, C.POINTER(C.c_size_t)]
        self.k.ReadProcessMemory.restype = W.BOOL
        self.k.CloseHandle.argtypes = [W.HANDLE]
        self.k.CloseHandle.restype = W.BOOL
        self.k.CreateToolhelp32Snapshot.argtypes = [W.DWORD, W.DWORD]
        self.k.CreateToolhelp32Snapshot.restype = W.HANDLE
        for name in ("Module32FirstW", "Module32NextW"):
            fun = getattr(self.k, name)
            fun.argtypes = [W.HANDLE, C.POINTER(MODULEENTRY32W)]
            fun.restype = W.BOOL
        self.k.GetProcessTimes.argtypes = [W.HANDLE] + [C.POINTER(W.FILETIME)] * 4
        self.k.GetProcessTimes.restype = W.BOOL
        self.handle = self.k.OpenProcess(0x0400 | 0x0010, False, pid)  # QUERY_INFORMATION | VM_READ
        if not self.handle:
            raise Refusal(f"OpenProcess failed: {C.get_last_error()}")
        self.pid = pid
        self.read_calls = self.read_bytes = 0

    def close(self):
        if self.handle:
            self.k.CloseHandle(self.handle)
            self.handle = None

    def process_creation_filetime(self) -> int:
        times = [W.FILETIME() for _ in range(4)]
        if not self.k.GetProcessTimes(self.handle, *(C.byref(t) for t in times)):
            raise Refusal(f"GetProcessTimes failed: {C.get_last_error()}")
        return times[0].dwLowDateTime | (times[0].dwHighDateTime << 32)

    def modules(self) -> list[dict]:
        snap = self.k.CreateToolhelp32Snapshot(0x8 | 0x10, self.pid)
        if snap == C.c_void_p(-1).value:
            raise Refusal(f"Module snapshot failed: {C.get_last_error()}")
        try:
            item = MODULEENTRY32W()
            item.dwSize = C.sizeof(item)
            ok = self.k.Module32FirstW(snap, C.byref(item))
            result = []
            seen = set()
            while ok:
                entry = {"name": item.szModule, "path": item.szExePath,
                         "base": int(item.modBaseAddr or 0), "size": item.modBaseSize}
                key = (entry["name"].casefold(), str(Path(entry["path"]).resolve()).casefold(),
                       entry["base"], entry["size"])
                if key not in seen:
                    result.append(entry)
                    seen.add(key)
                if len(result) > 512:
                    raise Refusal("Module count limit exceeded")
                ok = self.k.Module32NextW(snap, C.byref(item))
            if not result:
                raise Refusal("No modules returned")
            if C.get_last_error() != 18:  # ERROR_NO_MORE_FILES
                raise Refusal(f"Module enumeration incomplete: {C.get_last_error()}")
            return result
        finally:
            self.k.CloseHandle(snap)

    def read(self, address: int, size: int, purpose: str) -> bytes:
        if not purpose or not 0x10000 <= address <= 0xffffffff - size or not 1 <= size <= 256:
            raise Refusal("Read address/size/purpose outside bounds")
        if self.read_calls + 1 > MAX_READ_CALLS or self.read_bytes + size > MAX_READ_BYTES:
            raise Refusal("Read budget exceeded")
        self.read_calls += 1
        self.read_bytes += size
        out = C.create_string_buffer(size)
        actual = C.c_size_t()
        if not self.k.ReadProcessMemory(self.handle, C.c_void_p(address), out, size, C.byref(actual)) or actual.value != size:
            raise Refusal(f"ReadProcessMemory failed for {purpose}: error {C.get_last_error()}, read {actual.value}/{size}")
        return out.raw

    def pointer(self, address: int, purpose: str) -> int:
        return u32(self.read(address, 4, purpose))

    def metadata_name(self, address: int) -> str:
        # Only a pointer from mono_image_get_name is passed here. Byte reads
        # stop exactly at NUL, avoiding adjacent non-metadata bytes.
        out = bytearray()
        for i in range(128):
            item = self.read(address + i, 1, "assembly image metadata name")
            if item == b"\0":
                name = out.decode("ascii")
                if not name or any(not (c.isalnum() or c in "._-+") for c in name):
                    raise Refusal("Assembly metadata name has unsupported characters")
                return name
            out += item
        raise Refusal("Assembly metadata name exceeds limit")


def live_root(pid: int, pe: PE, layout: dict) -> dict:
    reader = Reader(pid)
    try:
        modules = reader.modules()
        expected = {"js.exe": INSTALL / "js.exe", "mono.dll": MONO}
        chosen = {}
        for name, path in expected.items():
            hits = [m for m in modules if m["name"].lower() == name]
            if len(hits) != 1 or Path(hits[0]["path"]).resolve() != path.resolve():
                raise Refusal(f"Expected exactly one module at installed path: {name}")
            chosen[name] = hits[0]
        if hashlib.sha256((INSTALL / "js.exe").read_bytes()).hexdigest() != JS_SHA256:
            raise Refusal("Pinned executable hash mismatch")
        mono = chosen["mono.dll"]
        if mono["size"] != pe.image_size:
            raise Refusal("Loaded Mono image size mismatch")
        # Compare the accessor bytes in the loaded image, adjusting the absolute
        # operand for loader relocation. Never call the accessor.
        rva = layout["accessors"]["mono_get_root_domain"]["rva"]
        slot = mono["base"] + layout["root_domain_slot_rva"]
        expected_code = b"\xa1" + struct.pack("<I", slot) + b"\xc3"
        if reader.read(mono["base"] + rva, 6, "pinned root accessor") != expected_code:
            raise Refusal("Loaded root accessor differs from pinned relocated bytes")
        root_before = u32(reader.read(slot, 4, "root domain pointer"))
        if not 0x10000 <= root_before <= 0xfffffffb:
            raise Refusal("Root domain pointer null or outside x86 user bounds")
        root_after = u32(reader.read(slot, 4, "root domain pointer consistency"))
        if root_after != root_before:
            raise Refusal("Root domain changed during bounded observation")
        # Revalidate every accessor used, the relocated list root operand, and
        # the full native traversal helper before following any list pointers.
        for name in ("mono_assembly_get_image", "mono_image_get_name"):
            access = layout["accessors"][name]
            original = bytes.fromhex(access["bytes"])
            if reader.read(mono["base"] + access["rva"], len(original), name) != original:
                raise Refusal(f"Loaded accessor differs: {name}")
        callsite = bytearray.fromhex(layout["assembly_foreach_callsite_bytes"])
        assembly_slot = mono["base"] + layout["assembly_slot_rva"]
        struct.pack_into("<I", callsite, 8, assembly_slot)
        if reader.read(mono["base"] + layout["assembly_foreach_callsite_rva"], len(callsite), "assembly list root instructions") != callsite:
            raise Refusal("Loaded assembly list root instructions differ")
        helper = bytes.fromhex(layout["list_foreach_bytes"])
        if reader.read(mono["base"] + layout["list_foreach_rva"], len(helper), "native foreach instructions") != helper:
            raise Refusal("Loaded list foreach differs")
        first = reader.pointer(assembly_slot, "loaded assembly list root")
        node, seen, assemblies = first, set(), []
        while node:
            if node in seen or len(seen) >= MAX_ASSEMBLIES:
                raise Refusal("Assembly list cycle or node budget exceeded")
            seen.add(node)
            pair = reader.read(node, 8, "assembly list node data/next")
            assembly, next_node = u32(pair), u32(pair, 4)
            image = reader.pointer(assembly + layout["accessors"]["mono_assembly_get_image"]["offset"], "assembly image pointer")
            name_pointer = reader.pointer(image + layout["accessors"]["mono_image_get_name"]["offset"], "image name pointer")
            name = reader.metadata_name(name_pointer)
            assemblies.append({"name": name, "assembly": hex(assembly), "image": hex(image)})
            if reader.read(node, 8, "assembly node consistency") != pair:
                raise Refusal("Assembly list changed during observation")
            node = next_node
        if reader.pointer(assembly_slot, "assembly list root consistency") != first:
            raise Refusal("Assembly list root changed during observation")
        return {"status": "PASS", "coverage": "root domain pointer and assembly image metadata only; no domain fields or game state read",
                "pid": pid, "process_creation_filetime": reader.process_creation_filetime(),
                "modules": chosen, "root_domain": hex(root_before), "root_domain_slot": hex(slot),
                "read_calls": reader.read_calls, "requested_read_bytes": reader.read_bytes,
                "assemblies": assemblies,
                "assembly_metadata": "PASS: bounded native loaded-assembly list and image names",
                "type_metadata": "UNVERIFIED: class traversal not enabled",
                "live_game_scalars": "UNVERIFIED: no scalar whitelist enabled"}
    finally:
        reader.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true", help="Explicitly allow bounded live read after code review")
    parser.add_argument("--pid", type=int)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    destination = None
    if args.output:
        allowed = Path(__file__).resolve().parents[1]
        destination = args.output.resolve()
        if destination.parent != allowed or not destination.name.startswith("live-") or destination.suffix != ".json":
            parser.error("Output must be live-*.json directly under unity-object-layer")
    result = {"utc": datetime.now(timezone.utc).isoformat(), "mode": "live" if args.live else "offline"}
    try:
        pe = PE(MONO, MONO_SHA256)
        layout = offline_layout(pe)
        result.update({"mono_path": str(MONO), "mono_sha256": pe.sha256, "layout": layout})
        if args.live:
            if args.pid is None or args.pid <= 0:
                raise Refusal("Live mode requires explicit positive --pid")
            result.update(live_root(args.pid, pe, layout))
        else:
            if args.pid is not None:
                raise Refusal("--pid without --live is not accepted")
            result.update({"status": "PASS", "coverage": "offline pinned export decoding only; no process APIs called"})
    except (OSError, ValueError, KeyError, struct.error, Refusal) as exc:
        result.update({"status": "REFUSED", "error": str(exc)})
    serialized = json.dumps(result, indent=2)
    if destination:
        destination.write_text(serialized + "\n", encoding="utf-8")
    print(serialized)
    return 0 if result["status"] == "PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
