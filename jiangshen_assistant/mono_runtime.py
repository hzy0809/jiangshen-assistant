"""Version-pinned, bounded read-only Mono state access for the supported client.

Only declared metadata and allowlisted numeric player state are read. No runtime
invocation or process-memory mutation is available in this module.
"""
from __future__ import annotations

import ctypes as C
from ctypes import wintypes as W
import hashlib
import json
from pathlib import Path
import struct
import sys
from datetime import datetime, timezone
from dataclasses import dataclass
import threading

INSTALL = Path(r"C:\Users\yu\AppData\Local\JiangShen\pc")
MONO = INSTALL / "js_Data" / "Mono" / "mono.dll"
MONO_SHA256 = "51941db23fd29380039924d1a44964fec18ab2a80e72f14410fe0163d2cae500"
JS_SHA256 = "5cc05ce27aa4d2fc0474603b4867fb00e3247fa437e459ef36a7075eab4bbc70"
MAX_READ_BYTES = 65536
MAX_READ_CALLS = 1024
MAX_ASSEMBLIES = 96


class Refusal(RuntimeError):
    pass


class TargetGone(Refusal):
    """The requested process is no longer present."""


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
        if type(pid) is not int or pid <= 0:
            raise Refusal("Invalid target process id")
        if sys.platform != "win32":
            raise Refusal("Live mode requires Windows")
        self.k = C.WinDLL("kernel32", use_last_error=True)
        # Exact allowlist: process query, zero-time exit query, read memory,
        # module enumeration, close.
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
        self.k.WaitForSingleObject.argtypes = [W.HANDLE, W.DWORD]
        self.k.WaitForSingleObject.restype = W.DWORD
        self.handle = self.k.OpenProcess(0x100000 | 0x0400 | 0x0010, False, pid)  # SYNCHRONIZE | QUERY_INFORMATION | VM_READ
        if not self.handle:
            error = C.get_last_error()
            if error in (87, 1168):
                raise TargetGone("目标游戏进程已退出，请重新选择窗口。")
            raise Refusal(f"OpenProcess failed: {error}")
        self.pid = pid
        self.read_calls = self.read_bytes = 0

    def close(self):
        if self.handle:
            self.k.CloseHandle(self.handle)
            self.handle = None

    def process_creation_filetime(self) -> int:
        # An exited process can remain openable while another handle retains
        # its kernel object. Its creation time alone does not prove liveness.
        # A zero-time wait also handles a process whose exit code is 259.
        status = self.k.WaitForSingleObject(self.handle, 0)
        if status == 0:  # WAIT_OBJECT_0: the process has exited.
            raise TargetGone("目标游戏进程已退出，请重新选择窗口。")
        if status != 0x102:  # WAIT_TIMEOUT: still running.
            raise Refusal(f"Process exit query failed: {C.get_last_error()}")
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



TARGETS = (
    ("Assembly-CSharp-firstpass", "", "Global", 0x0200014f),
    ("Assembly-CSharp", "", "MainUI", 0x020001af),
    ("Assembly-CSharp-firstpass", "Database", "Character", 0x02000071),
    ("pbdata", "Database", "_character", 0x0200002f),
)
# Exact snippets from the pinned native DLL. No relocated absolute operands.
TYPE_PROOFS = {
    "image_embedded_cache": (0x177e7, "ff750c8d87a002000050e80018ffff"),
    "cache_lookup": (0x9021, "ff750cff1633d2f7760c8b46148b3c90eb1057ff5604593b450c741157ff56088b385985ff75eb33c05f5e5dc38bc7ebf8"),
    "cache_key_callback": (0x37c54, "558bec8b45088b40385dc3"),
    "cache_next_callback": (0x37c5f, "558bec8b450805a80000005dc3"),
    "cache_hash_callback": (0x2228, "8b442404c3"),
}


def _type_layout(pe: PE) -> dict:
    for name, (rva, expected_hex) in TYPE_PROOFS.items():
        expected = bytes.fromhex(expected_hex)
        off = pe.offset(rva)
        if pe.data[off:off+len(expected)] != expected:
            raise Refusal(f"Unsupported class cache proof: {name}")
    accessors = {}
    for name in ("mono_class_get_image", "mono_class_get_name", "mono_class_get_namespace", "mono_class_get_parent"):
        code = pe.code(name, 11)
        if code[:8] != bytes.fromhex("558bec8b45088b40") or code[9:] != b"\x5d\xc3":
            raise Refusal(f"Unsupported class accessor: {name}")
        accessors[name] = {"rva": pe.exports[name], "offset": code[8], "bytes": code.hex()}
    return {"cache_embedded_offset": 0x2a0, "cache_header_size": 24,
            "cache_expected_callback_rvas": [0x2228, 0x37c54, 0x37c5f],
            "class_token_offset": 0x38, "class_next_offset": 0xa8,
            "max_chain_nodes": 64, "accessors": accessors,
            "proofs": {name: {"rva": rva, "bytes": code} for name,(rva,code) in TYPE_PROOFS.items()}}


def validate_expected_string(reader: Reader, pointer: int, expected: str, purpose: str):
    data = expected.encode("ascii") + b"\0"
    if reader.read(pointer, len(data), purpose) != data:
        raise Refusal(f"Class metadata identity differs: {purpose}")


def cached_class(reader: Reader, mono_base: int, image: int, info: dict,
                 namespace: str, name: str, token: int) -> dict:
    cache = image + info["cache_embedded_offset"]
    header = reader.read(cache, info["cache_header_size"], "class cache metadata header")
    h = struct.unpack("<6I", header)
    if list(h[:3]) != [mono_base + rva for rva in info["cache_expected_callback_rvas"]]:
        raise Refusal("Class cache callbacks differ from pinned native implementation")
    count, entries, buckets = h[3:]
    if not 1 <= count <= 131071 or not 1 <= entries <= 65535:
        raise Refusal("Class cache size outside bounds or not initialized")
    bucket_slot = buckets + (token % count) * 4
    first = reader.pointer(bucket_slot, "exact TypeDef hash bucket")
    node, visited, found = first, set(), 0
    while node:
        if node in visited or len(visited) >= info["max_chain_nodes"]:
            raise Refusal("Type cache collision chain cycle or limit")
        visited.add(node)
        node_token = reader.pointer(node + info["class_token_offset"], "cached TypeDef token")
        if node_token == token:
            found = node
            break
        node = reader.pointer(node + info["class_next_offset"], "class cache collision next")
    if reader.read(cache, info["cache_header_size"], "class cache header consistency") != header:
        raise Refusal("Class cache changed during observation")
    if reader.pointer(bucket_slot, "type bucket consistency") != first:
        raise Refusal("Class cache bucket changed during observation")
    identity = {"namespace": namespace, "name": name, "token": hex(token), "bucket_nodes_visited": len(visited)}
    if not found:
        return {**identity, "status": "UNVERIFIED", "reason": "exact type not currently in native class cache; no initialization attempted"}
    access = info["accessors"]
    actual_image = reader.pointer(found + access["mono_class_get_image"]["offset"], "class declaring image")
    if actual_image != image:
        raise Refusal("Cached class declaring image mismatch")
    for accessor, expected, purpose in (("mono_class_get_name",name,"whitelisted class name"),
                                         ("mono_class_get_namespace",namespace,"whitelisted class namespace")):
        pointer = reader.pointer(found + access[accessor]["offset"], purpose + " pointer")
        validate_expected_string(reader, pointer, expected, purpose)
    parent = reader.pointer(found + access["mono_class_get_parent"]["offset"], "class parent metadata pointer")
    if reader.pointer(found + info["class_token_offset"], "matched TypeDef token consistency") != token:
        raise Refusal("Matched class token changed")
    return {**identity, "status": "PASS", "class": hex(found), "image": hex(image), "parent": hex(parent),
            "cache_bucket_count": count, "cache_entry_count": entries}


def _read_types(pid: int, pe: PE, info: dict) -> dict:
    first = live_root(pid, pe, offline_layout(pe))
    reader = Reader(pid)
    try:
        if reader.process_creation_filetime() != first["process_creation_filetime"]:
            raise Refusal("Process identity changed between observations")
        mono = first["modules"]["mono.dll"]
        current = [m for m in reader.modules() if m["name"].lower() == "mono.dll"]
        if current != [mono]:
            raise Refusal("Mono module identity changed")
        root_slot = int(first["root_domain_slot"],16)
        if reader.pointer(root_slot, "root domain identity before types") != int(first["root_domain"],16):
            raise Refusal("Root domain changed before class query")
        for proof in [*info["proofs"].values(), *info["accessors"].values()]:
            expected = bytes.fromhex(proof["bytes"])
            if reader.read(mono["base"] + proof["rva"],len(expected),"pinned native class metadata proof") != expected:
                raise Refusal("Loaded native class metadata instructions differ")
        images = {}
        for assembly in first["assemblies"]:
            name = assembly["name"]
            if name.endswith(".dll"):
                name = name[:-4]
            if name in images:
                raise Refusal("Ambiguous loaded assembly image name")
            images[name] = int(assembly["image"],16)
        classes = []
        for assembly, namespace, name, token in TARGETS:
            if assembly not in images:
                classes.append({"assembly":assembly,"namespace":namespace,"name":name,"token":hex(token),
                                "status":"UNVERIFIED","reason":"declaring assembly not loaded"})
                continue
            result = cached_class(reader,mono["base"],images[assembly],info,namespace,name,token)
            classes.append({"assembly":assembly,**result})
        passed = [c for c in classes if c["status"] == "PASS"]
        character = next((c for c in passed if c["name"] == "Character"),None)
        parent = next((c for c in passed if c["name"] == "_character"),None)
        if character and parent and character["parent"] != parent["class"]:
            raise Refusal("DataCharacter parent does not match validated Data_character")
        return {"status":"PASS" if len(passed)==len(TARGETS) else "UNVERIFIED",
                "pid":pid,"process_creation_filetime":first["process_creation_filetime"],
                "root_domain":first["root_domain"],"mono_base":hex(mono["base"]),"classes":classes,
                "assembly_stage_read_calls":first["read_calls"],"assembly_stage_requested_bytes":first["requested_read_bytes"],
                "type_stage_read_calls":reader.read_calls,"type_stage_requested_bytes":reader.read_bytes,
                "live_game_scalars":"UNVERIFIED: disabled; no object/static field data read"}
    finally:
        reader.close()



MANAGED_HASHES = {
    "Assembly-CSharp.dll": "c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911",
    "Assembly-CSharp-firstpass.dll": "29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87",
    "pbdata.dll": "905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab",
}
FIELD_SPECS = {
    "character_": {"ordinal":41,"metadata_token":0x04000957,"signature":"06-12-81-C4","mono_type":0x12,"flags":0x16},
    "id": {"ordinal":0,"metadata_token":0x040001f6,"signature":"06-0B","mono_type":0x0b,"flags":0x06,"format":"<Q"},
    "level": {"ordinal":14,"metadata_token":0x04000204,"signature":"06-06","mono_type":0x06,"flags":0x06,"format":"<h"},
    "stamina": {"ordinal":18,"metadata_token":0x04000208,"signature":"06-06","mono_type":0x06,"flags":0x06,"format":"<h"},
    "gold": {"ordinal":20,"metadata_token":0x0400020a,"signature":"06-08","mono_type":0x08,"flags":0x06,"format":"<i"},
    "bind_gold": {"ordinal":21,"metadata_token":0x0400020b,"signature":"06-08","mono_type":0x08,"flags":0x06,"format":"<i"},
    "money": {"ordinal":22,"metadata_token":0x0400020c,"signature":"06-08","mono_type":0x08,"flags":0x06,"format":"<i"},
    "mana": {"ordinal":33,"metadata_token":0x04000217,"signature":"06-08","mono_type":0x08,"flags":0x06,"format":"<i"},
    "money64": {"ordinal":269,"metadata_token":0x04000303,"signature":"06-0A","mono_type":0x0a,"flags":0x06,"format":"<q"},
}
PLAYER_FIELDS = tuple(name for name in FIELD_SPECS if name != "character_")
# Only relocation-free native instruction slices. Their offsets and semantics
# are recorded in runtime-native-disassembly.txt. The slots below are derived
# from exact native mono_domain_foreach absolute operands in offline_layout().
SCALAR_PROOFS = {
    "class_field_array": (0x1a26f,"807e2000597522837e6400741c8b4674eb138b4d088b5164c1e20403517483c0103bc273048907"),
    "class_runtime_info_domain_vtable": (0x61318,"8b86a40000008b550885c074198b4a60570fb7383bf95f7c0d8d44880483380074048b00eb0d"),
    "ordinary_static_field_address": (0x60fa1,"8b400c03c1"),
    "ordinary_instance_field_address": (0x5eef5,"8b400c034508"),
    "class_initialized_fast_path": (0x6157f,"f6471c02740733c0e96f030000"),
    "class_init_success_failure_bits": (0x618b8,"8b471ca804750683c80289471c"),
}


def _scalar_layout(pe: PE) -> dict:
    for name,(rva,expected) in SCALAR_PROOFS.items():
        code=bytes.fromhex(expected)
        off=pe.offset(rva)
        if pe.data[off:off+len(code)] != code:
            raise Refusal(f"Unsupported native scalar layout: {name}")
    accessors={}
    for name in ("mono_field_get_name","mono_field_get_parent","mono_field_get_offset","mono_domain_get_id"):
        code=pe.code(name,11)
        if code[:8] != bytes.fromhex("558bec8b45088b40") or code[9:] != b"\x5d\xc3":
            raise Refusal(f"Unsupported scalar accessor: {name}")
        accessors[name]={"rva":pe.exports[name],"offset":code[8],"bytes":code.hex()}
    exact={
        "mono_field_get_type":"558bec8b45088b005dc3",
        "mono_type_get_type":"558bec8b45080fbe40065dc3",
        "mono_type_is_byref":"558bec8b45088b4004c1e81e83e0015dc3",
        "mono_type_get_class":"558bec8b45088b005dc3",
        "mono_field_get_flags":"558bec8b45088b000fb740045dc3",
        "mono_object_get_class":"558bec8b45088b008b005dc3",
        "mono_object_get_domain":"558bec8b45088b008b40085dc3",
    }
    for name,expected in exact.items():
        if pe.code(name,len(bytes.fromhex(expected))).hex()!=expected:
            raise Refusal(f"Unsupported scalar accessor: {name}")
        accessors[name]={"rva":pe.exports[name],"bytes":expected}
    domain_count_rva=pe.exports["mono_domain_foreach"]+0x12
    count_code=pe.data[pe.offset(domain_count_rva):pe.offset(domain_count_rva)+7]
    if count_code[:3]!=bytes.fromhex("0fb73d"):
        raise Refusal("Unsupported native domain-count instruction")
    domain_array_rva=pe.exports["mono_domain_foreach"]+0x33
    array_code=pe.data[pe.offset(domain_array_rva):pe.offset(domain_array_rva)+6]
    if array_code[:2]!=bytes.fromhex("ff35"):
        raise Refusal("Unsupported native domain-array instruction")
    slots={"count_slot_rva":u32(count_code,3)-pe.base,"array_slot_rva":u32(array_code,2)-pe.base}
    for rva in slots.values():
        if not 0<=rva<pe.image_size-4:
            raise Refusal("Domain registry slot outside image")
    hashes={}
    for name,expected in MANAGED_HASHES.items():
        observed=hashlib.sha256((INSTALL/"js_Data"/"Managed"/name).read_bytes()).hexdigest()
        if observed!=expected:
            raise Refusal(f"Managed assembly hash changed: {name}")
        hashes[name]=observed
    return {"accessors":accessors,"proofs":{n:{"rva":r,"bytes":c} for n,(r,c) in SCALAR_PROOFS.items()},
            "domain_registry":{**slots,"count_instruction_rva":domain_count_rva,"count_instruction":count_code.hex(),
                               "array_instruction_rva":domain_array_rva,"array_instruction":array_code.hex()},
            "managed_sha256":hashes,"field_specs":FIELD_SPECS,
            "class_failure_offset":0x20,"field_count_offset":0x64,"field_array_offset":0x74,"field_stride":16,
            "class_runtime_info_offset":0xa4,"vtable_class_offset":0,"vtable_domain_offset":8,"vtable_static_data_offset":12,
            "vtable_init_flags_offset":0x1c,"initialized_mask":2,"init_failed_mask":4}


def read_field(reader: Reader, info: dict, klass: int, name: str) -> dict:
    spec=FIELD_SPECS[name]
    if reader.read(klass+info["class_failure_offset"],1,"class failure metadata")!=b"\0":
        raise Refusal("Class load failure marker is set")
    count=reader.pointer(klass+info["field_count_offset"],"class declared field count")
    array=reader.pointer(klass+info["field_array_offset"],"already-populated class fields")
    if not spec["ordinal"]<count<=1024 or not array:
        raise Refusal(f"Whitelisted declared field unavailable: {name}")
    address=array+spec["ordinal"]*info["field_stride"]
    raw=reader.read(address,16,"exact whitelisted field descriptor")
    typeptr,nameptr,parent,offset=struct.unpack("<4I",raw)
    if parent!=klass:
        raise Refusal(f"Field declaring class mismatch: {name}")
    validate_expected_string(reader,nameptr,name,"whitelisted field "+name)
    type_raw=reader.read(typeptr,8,"whitelisted MonoType metadata")
    type_data,attrs=struct.unpack_from("<IH",type_raw)
    if attrs!=spec["flags"] or type_raw[6]!=spec["mono_type"] or u32(type_raw,4)&0xc0000000:
        raise Refusal(f"Field type/flags/byref mismatch: {name}")
    if offset>4096:
        raise Refusal(f"Field offset special or outside bounded object/static layout: {name}")
    if reader.read(address,16,"field descriptor consistency")!=raw:
        raise Refusal("Field descriptor changed")
    return {"name":name,"descriptor":hex(address),"type_pointer":hex(typeptr),"type_data":type_data,
            "offset":offset,"type_enum":type_raw[6],"flags":attrs,"declared_ordinal":spec["ordinal"],
            "offline_field_token":hex(spec["metadata_token"]),"offline_signature":spec["signature"]}


def domain_registry(reader: Reader,mono_base: int,info: dict) -> tuple[list[dict],dict]:
    registry=info["domain_registry"]
    count_slot=mono_base+registry["count_slot_rva"]
    array_slot=mono_base+registry["array_slot_rva"]
    count_raw=reader.read(count_slot,2,"registered Mono domain count")
    count=struct.unpack("<H",count_raw)[0]
    array=reader.pointer(array_slot,"registered Mono domain array")
    if not 1<=count<=32 or not array:
        raise Refusal("Mono domain registry outside fixed bound")
    raw=reader.read(array,count*4,"registered Mono domain pointers")
    domains=[]
    ids=set()
    for index in range(count):
        ptr=u32(raw,index*4)
        if not ptr:
            continue
        ident=reader.pointer(ptr+info["accessors"]["mono_domain_get_id"]["offset"],"registered Mono domain id")
        if ident in ids or ident>255:
            raise Refusal("Duplicate or unsupported Mono domain id")
        ids.add(ident)
        domains.append({"pointer":ptr,"id":ident})
    if not domains:
        raise Refusal("No registered Mono domain")
    return domains,{"count_slot":count_slot,"count_raw":count_raw,"array_slot":array_slot,"array":array,"raw":raw}


def validate_domains_unchanged(reader: Reader,seal: dict):
    if reader.read(seal["count_slot"],2,"domain count consistency")!=seal["count_raw"] or reader.pointer(seal["array_slot"],"domain array identity consistency")!=seal["array"] or reader.read(seal["array"],len(seal["raw"]),"domain array contents consistency")!=seal["raw"]:
        raise Refusal("Mono domain registry changed during observation")


def _read_scalars(pid: int,pe: PE,info: dict,expected_creation: int | None = None) -> dict:
    class_stage=_read_types(pid,pe,_type_layout(pe))
    if class_stage["status"]!="PASS":
        raise Refusal("Required types not all cached; no scalar reads")
    if expected_creation is not None and class_stage["process_creation_filetime"] != expected_creation:
        raise Refusal("目标 PID 已被复用，请重新绑定游戏窗口。")
    klass={c["name"]:int(c["class"],16) for c in class_stage["classes"]}
    mono_base=int(class_stage["mono_base"],16)
    reader=Reader(pid)
    try:
        if reader.process_creation_filetime()!=class_stage["process_creation_filetime"]:
            raise Refusal("Process identity changed before scalar stage")
        modules=[m for m in reader.modules() if m["name"].lower()=="mono.dll"]
        if len(modules)!=1 or modules[0]["base"]!=mono_base or Path(modules[0]["path"]).resolve()!=MONO.resolve():
            raise Refusal("Mono module changed before scalar stage")
        for proof in [*info["proofs"].values(),*info["accessors"].values()]:
            code=bytes.fromhex(proof["bytes"])
            if reader.read(mono_base+proof["rva"],len(code),"pinned native scalar layout proof")!=code:
                raise Refusal("Loaded scalar layout instructions differ")
        registry=info["domain_registry"]
        for prefix,operand in (("count",3),("array",2)):
            code=bytearray.fromhex(registry[prefix+"_instruction"])
            struct.pack_into("<I",code,operand,mono_base+registry[prefix+"_slot_rva"])
            if reader.read(mono_base+registry[prefix+"_instruction_rva"],len(code),"pinned native domain registry proof")!=code:
                raise Refusal("Loaded native domain registry instructions differ")
        fields={"character_":read_field(reader,info,klass["Global"],"character_")}
        for name in PLAYER_FIELDS:
            fields[name]=read_field(reader,info,klass["_character"],name)
        if fields["character_"]["type_data"]!=klass["Character"]:
            raise Refusal("Global.character_ declared type is not validated DataCharacter")
        domains,seal=domain_registry(reader,mono_base,info)
        runtime_info=reader.pointer(klass["Global"]+info["class_runtime_info_offset"],"Global existing runtime_info")
        if not runtime_info:
            raise Refusal("Global has no existing runtime_info; no initialization attempted")
        max_domain=struct.unpack("<H",reader.read(runtime_info,2,"runtime_info domain capacity"))[0]
        if max_domain>255:
            raise Refusal("Global runtime_info domain capacity outside fixed bound")
        candidates=[]
        for domain in domains:
            if domain["id"]>max_domain:
                continue
            vtable=reader.pointer(runtime_info+4+domain["id"]*4,"Global existing per-domain vtable")
            if not vtable:
                continue
            if reader.pointer(vtable,"Global vtable class")!=klass["Global"] or reader.pointer(vtable+8,"Global vtable domain")!=domain["pointer"]:
                raise Refusal("Existing Global vtable class/domain mismatch")
            init_flags=reader.pointer(vtable+info["vtable_init_flags_offset"],"Global class initializer state")
            if init_flags & (info["initialized_mask"]|info["init_failed_mask"]) != info["initialized_mask"]:
                continue
            static_data=reader.pointer(vtable+12,"Global existing static field data")
            if not static_data:
                continue
            reference_slot=static_data+fields["character_"]["offset"]
            character=reader.pointer(reference_slot,"whitelisted Global.character_ reference")
            if not character:
                continue
            object_vtable=reader.pointer(character,"whitelisted Character object vtable")
            if reader.pointer(object_vtable,"Character object class")!=klass["Character"] or reader.pointer(object_vtable+8,"Character object domain")!=domain["pointer"]:
                raise Refusal("Character object class/domain differs from validated type")
            candidates.append({"domain":domain,"global_vtable":vtable,"static_data":static_data,
                               "reference_slot":reference_slot,"character":character,"object_vtable":object_vtable,
                               "global_init_flags":init_flags})
        if len(candidates)!=1:
            raise Refusal("Expected exactly one initialized nonnull Character reference across registered domains")
        selected=candidates[0]
        observed_at=datetime.now(timezone.utc).isoformat()
        values={}
        for name in PLAYER_FIELDS:
            fmt=FIELD_SPECS[name]["format"]
            values[name]=struct.unpack(fmt,reader.read(selected["character"]+fields[name]["offset"],struct.calcsize(fmt),"whitelisted numeric Character."+name))[0]
        # The external snapshot cannot freeze the GC or game thread. Refuse on
        # any observed reference/vtable/domain change and disclose non-atomicity.
        if reader.pointer(selected["reference_slot"],"Character reference consistency")!=selected["character"] or reader.pointer(selected["character"],"Character object vtable consistency")!=selected["object_vtable"] or reader.pointer(selected["object_vtable"],"Character class consistency")!=klass["Character"]:
            raise Refusal("Character reference/type changed during scalar observation")
        if reader.pointer(klass["Global"]+info["class_runtime_info_offset"],"Global runtime_info consistency")!=runtime_info:
            raise Refusal("Global runtime_info changed")
        if reader.pointer(selected["global_vtable"]+info["vtable_init_flags_offset"],"Global initialized state consistency") & 6 != 2:
            raise Refusal("Global class initialization state changed")
        validate_domains_unchanged(reader,seal)
        return {"status":"PASS","pid":pid,"process_creation_filetime":class_stage["process_creation_filetime"],
                "observed_at_utc":observed_at,"completed_at_utc":datetime.now(timezone.utc).isoformat(),
                "domain":{"id":selected["domain"]["id"],"pointer":hex(selected["domain"]["pointer"])},
                "global_class_initialized":True,"global_init_flags":hex(selected["global_init_flags"]),
                "character_object":hex(selected["character"]),"fields":fields,"values":values,
                "scalar_stage_read_calls":reader.read_calls,"scalar_stage_requested_bytes":reader.read_bytes,
                "type_stage_read_calls":class_stage["type_stage_read_calls"],"assembly_stage_read_calls":class_stage["assembly_stage_read_calls"],
                "limitation":"External bounded reads are not an atomic game-thread snapshot; identity and domain remained consistent during observed reads"}
    finally:
        reader.close()


@dataclass(frozen=True)
class ProcessIdentity:
    pid: int
    creation_time: int
    hwnd: int
    tid: int

    def as_dict(self) -> dict:
        return {"pid": self.pid, "process_creation_filetime": self.creation_time,
                "hwnd": self.hwnd, "tid": self.tid}


def process_creation(pid: int) -> int:
    reader = Reader(pid)
    try:
        return reader.process_creation_filetime()
    finally:
        reader.close()


def process_status(pid: int, creation_time: int) -> str:
    """Return same/gone/unknown; access failure is never evidence of exit."""
    try:
        return "same" if process_creation(pid) == creation_time else "gone"
    except TargetGone:
        return "gone"
    except (OSError, Refusal):
        return "unknown"


def identity_for(window) -> ProcessIdentity:
    """Capture and validate HWND/PID/owner thread plus process creation identity."""
    if sys.platform != "win32":
        raise Refusal("对象连接仅支持 Windows。")
    pid, hwnd = window.process_id, window.hwnd
    if type(pid) is not int or pid <= 0 or type(hwnd) is not int or not 0 < hwnd <= 0xffffffff:
        raise Refusal("游戏窗口身份无效。")
    user = C.WinDLL("user32", use_last_error=True)
    user.IsWindow.argtypes, user.IsWindow.restype = [W.HWND], W.BOOL
    user.GetWindowThreadProcessId.argtypes = [W.HWND, C.POINTER(W.DWORD)]
    user.GetWindowThreadProcessId.restype = W.DWORD
    if not user.IsWindow(hwnd):
        raise Refusal("游戏窗口已关闭，请重新绑定。")
    owner = W.DWORD()
    tid = int(user.GetWindowThreadProcessId(hwnd, C.byref(owner)))
    if not tid or owner.value != pid:
        raise Refusal("窗口所属进程已改变，请重新绑定。")
    reader = Reader(pid)
    try:
        creation = reader.process_creation_filetime()
        candidates = [m for m in reader.modules() if m["name"].casefold() == "js.exe"]
        if (len(candidates) != 1 or Path(candidates[0]["path"]).resolve() != (INSTALL/"js.exe").resolve()
                or hashlib.sha256((INSTALL/"js.exe").read_bytes()).hexdigest() != JS_SHA256):
            raise Refusal("当前游戏客户端版本或安装路径不受支持。")
    finally:
        reader.close()
    owner_after = W.DWORD()
    if int(user.GetWindowThreadProcessId(hwnd, C.byref(owner_after))) != tid or owner_after.value != pid:
        raise Refusal("窗口身份在读取期间改变，请重新绑定。")
    return ProcessIdentity(pid, creation, hwnd, tid)


def validate_identity(window, expected: ProcessIdentity) -> None:
    if identity_for(window) != expected:
        raise Refusal("目标窗口、进程或主线程身份已改变，请重新绑定。")


class MonoRuntime:
    """Reusable passive reader. Hash/layout cache is invalidated by file metadata."""
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._seal = None
        self._pe = None
        self._layout = None

    def read(self, pid: int, expected_creation: int | None = None) -> dict:
        with self._lock:
            paths = [MONO, INSTALL/"js.exe", *(INSTALL/"js_Data"/"Managed"/n for n in MANAGED_HASHES)]
            try:
                seal = tuple((str(p), p.stat().st_size, p.stat().st_mtime_ns, p.stat().st_ctime_ns) for p in paths)
                if seal != self._seal:
                    pe = PE(MONO, MONO_SHA256)
                    info = _scalar_layout(pe)
                    self._pe, self._layout, self._seal = pe, info, seal
                if expected_creation is None:
                    expected_creation = process_creation(pid)
                result = _read_scalars(pid, self._pe, self._layout, expected_creation)
            except (OSError, ValueError, KeyError, struct.error) as exc:
                self._seal = None
                raise Refusal(f"读取游戏状态失败：{exc}") from exc
            values = result["values"]
            return {"pid": pid, "process_creation_filetime": result["process_creation_filetime"],
                    "observed_at": result["observed_at_utc"],
                    "character_id": str(values["id"]),
                    "level": values["level"], "stamina": values["stamina"], "gold": values["gold"],
                    "bind_gold": values["bind_gold"], "money": values["money"] + values["money64"],
                    "mana": values["mana"]}


def read(pid: int) -> dict:
    return MonoRuntime().read(pid)

