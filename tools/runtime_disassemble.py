"""Bounded offline x86 disassembly of installed Mono; no process access."""
import json
import struct
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "research" / "unity-object-layer"
sys.path.insert(0, str(OUT / "tools" / "pydeps"))
from capstone import Cs, CS_ARCH_X86, CS_MODE_32


def main():
    module = json.loads((OUT / "runtime-binary-inventory.json").read_text(encoding="utf-8"))[1]
    data = Path(module["path"]).read_bytes()
    pe = struct.unpack_from("<I", data, 0x3c)[0]
    imagebase = struct.unpack_from("<I", data, pe+24+28)[0]
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    funcs = {e["name"]:int(e["rva"],16) for e in module["exports"]}
    requests = {name:(funcs[name],size) for name,size in [
        ("mono_assembly_foreach",67), ("mono_get_root_domain",6),
        ("mono_domain_foreach",112), ("mono_assembly_get_image",11),
        ("mono_image_get_name",11), ("mono_object_get_class",12),
        ("mono_object_get_domain",13), ("mono_domain_get_id",11),
        ("mono_class_get_image",11), ("mono_field_get_parent",11),
        ("mono_type_get_type",12), ("mono_type_is_byref",17),
        ("mono_type_get_class",10),
        ("mono_class_get_fields",100), ("mono_class_get_field_from_name",25),
        ("mono_field_get_flags",14), ("mono_field_get_offset",11),
        ("mono_field_get_name",11), ("mono_field_get_type",10),
        ("mono_class_get_name",11), ("mono_class_get_namespace",11),
        ("mono_class_get_parent",11), ("mono_class_get_flags",11),
        ("mono_field_static_get_value",130), ("mono_class_vtable",21),
        ("mono_field_get_value",105), ("mono_class_get_methods",120),
        ("mono_thread_get_main",6),
    ]}
    requests.update({
        "list_foreach_helper":(0x4ed0,26), "list_copy_helper":(0x4f06,90),
        "class_vtable_full":(0x612d0,0x7e),
        "class_get":(0x19154,0x15), "class_get_full":(0x1900e,0x146),
        "class_get_typedef":(0x17799,0x100),
        "class_cache_lookup":(0x8ff6,0x5c),
        "class_cache_init":(0x8fae,0x31),
        "class_cache_callbacks":(0x37c54,0x4f),
        "class_cache_hash":(0x2228,5),
        "runtime_class_init":(0x62c71,0x11),
        "runtime_class_init_full":(0x61575,0x389),
    })
    # Optional command line addresses permit bounded follow-up on a discovered callee.
    for spec in sys.argv[1:]:
        label,rva,size = spec.split(":")
        requests[label]=(int(rva,0),int(size,0))
    lines=[f"Module SHA256: {module['sha256']}",f"Preferred image base: 0x{imagebase:08x}","All addresses below are offline RVAs. Runtime absolute addresses require the loaded module base."]
    for name,(rva,size) in requests.items():
        offset = next(raw+rva-va for _,va,vs,raw,rawsize in module["sections"] if va<=rva<va+rawsize)
        lines.extend(["",f"## {name} RVA 0x{rva:08x}; requested bytes {size}"])
        for ins in md.disasm(data[offset:offset+size],imagebase+rva):
            lines.append(f"{ins.address-imagebase:08x}  {ins.bytes.hex(' '):25} {ins.mnemonic:8} {ins.op_str}")
    (OUT / "runtime-native-disassembly.txt").write_text("\n".join(lines)+"\n",encoding="utf-8")
    print(f"Wrote {len(lines)} lines to runtime-native-disassembly.txt")


if __name__ == "__main__":
    main()
