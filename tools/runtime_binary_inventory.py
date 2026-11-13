"""Offline PE headers, exports/imports and bounded string evidence; never loads DLLs."""
import hashlib
import json
import re
import struct
from pathlib import Path

ROOT = Path(r"C:\Users\yu\AppData\Local\JiangShen\pc")
OUT = Path(__file__).resolve().parents[1] / "research" / "unity-object-layer"


def inspect(path):
    data = path.read_bytes()
    u16 = lambda p: struct.unpack_from("<H", data, p)[0]
    u32 = lambda p: struct.unpack_from("<I", data, p)[0]
    pe = u32(0x3C)
    if data[pe:pe+4] != b"PE\0\0":
        raise ValueError("Not PE")
    opt = pe + 24
    assert u16(opt) == 0x10B, "Only PE32 handled"
    sections = []
    sec = opt + u16(pe+20)
    for i in range(u16(pe+6)):
        p = sec+i*40
        sections.append((data[p:p+8].rstrip(b"\0").decode(), u32(p+12), u32(p+8), u32(p+20), u32(p+16)))

    def offset(rva):
        for _, va, vs, raw, size in sections:
            if va <= rva < va+max(vs, size):
                return raw+rva-va
        if rva < u32(opt+60):
            return rva
        raise ValueError(f"Unmapped RVA {rva:x}")

    def cstr(p):
        return data[p:data.index(b"\0", p)].decode("ascii", errors="replace")

    def export_target(rva):
        for name, va, vs, raw, size in sections:
            if va <= rva < va+max(vs, size):
                backed = rva-va < size
                p = raw+rva-va if backed else None
                return {"section":name, "file_backed":backed, "file_offset":p, "first16":data[p:p+16].hex() if backed else None}
        return {"section":None, "file_backed":False, "file_offset":None, "first16":None}

    exports = []
    er, es = u32(opt+96), u32(opt+100)
    if er:
        ep = offset(er)
        names, ords, funcs = offset(u32(ep+32)), offset(u32(ep+36)), offset(u32(ep+28))
        base = u32(ep+16)
        for i in range(u32(ep+24)):
            ordinal = u16(ords+i*2)
            rva = u32(funcs+ordinal*4)
            exports.append({"name": cstr(offset(u32(names+i*4))), "ordinal": base+ordinal, "rva": f"0x{rva:08x}", **export_target(rva)})
    imports = []
    ir = u32(opt+104)
    if ir:
        ip = offset(ir)
        while any(data[ip:ip+20]):
            names = []
            thunk = offset(u32(ip) or u32(ip+16))
            while u32(thunk):
                value = u32(thunk)
                names.append(f"ordinal:{value & 0xffff}" if value & 0x80000000 else cstr(offset(value)+2))
                thunk += 4
            imports.append({"dll": cstr(offset(u32(ip+12))), "names": names})
            ip += 20
    ascii_strings = [{"offset": m.start(), "text": m.group().decode("ascii")} for m in re.finditer(rb"[\x20-\x7e]{5,}", data)]
    filters = [r"(?i)mono.{0,50}(version|2\.6|2\.8|2\.0|debug|thread|profiler|agent)", r"(?i)(Unity Player|UnityEngine|4\.1\.5|debugger-agent|mono\.dll|MONO_|UNITY_|thread.*main|main.*thread)", r"(?i)(development build|script debugging|debugger|Assembly-CSharp|mscorlib|v2\.0\.50727|v4\.0\.30319|Included Boehm|Architecture:.*x86)"]
    selected = [s for s in ascii_strings if any(re.search(f, s["text"]) for f in filters)]
    managed = None
    clr_rva = u32(opt+96+14*8)
    if clr_rva:
        clr = offset(clr_rva)
        meta = offset(u32(clr+8))
        if data[meta:meta+4] != b"BSJB":
            raise ValueError("Invalid managed metadata signature")
        managed = {"metadata_version":data[meta+16:meta+16+u32(meta+12)].rstrip(b"\0").decode("ascii"), "corflags":f"0x{u32(clr+16):08x}"}
    return {"path": str(path), "size":len(data), "sha256":hashlib.sha256(data).hexdigest(), "machine":f"0x{u16(pe+4):04x}", "optional_magic":f"0x{u16(opt):04x}", "imagebase":f"0x{u32(opt+28):08x}", "timestamp":u32(pe+8), "sections":sections, "managed":managed, "exports":exports, "imports":imports, "selected_ascii_strings":selected}


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    results = [inspect(ROOT / p) for p in ["js.exe", "js_Data/Mono/mono.dll", "js_Data/Managed/UnityEngine.dll", "js_Data/Managed/mscorlib.dll"]]
    (OUT / "runtime-binary-inventory.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    for r in results:
        print(r["path"], r["machine"], r["sha256"], "exports",len(r["exports"]))
        for s in r["selected_ascii_strings"]:
            if re.search(r"(?i)(2\.6|2\.8|4\.1\.5|v2\.0|v4\.0|debugger-agent|MONO_ENV|main thread|MONO_PATH|MONO_OPTIONS)",s["text"]):
                print(hex(s["offset"]),s["text"][:300])


if __name__ == "__main__":
    main()
