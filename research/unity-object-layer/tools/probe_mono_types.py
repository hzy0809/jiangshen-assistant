"""Read exact already-cached Mono type metadata; no object or scalar reads.

Default is offline. Live mode composes the reviewed metadata reader, then walks
one cache bucket per whitelisted TypeDef token. Never calls native Mono methods.
See runtime-native-disassembly.txt and object-map-typedef-tokens.txt provenance.
"""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import struct
import probe_mono_readonly as base

TARGETS = (
    ("Assembly-CSharp-firstpass", "", "Global", 0x0200014f),
    ("Assembly-CSharp", "", "MainUI", 0x020001af),
    ("Assembly-CSharp-firstpass", "Database", "Character", 0x02000071),
    ("pbdata", "Database", "_character", 0x0200002f),
)
# Exact snippets from the pinned native DLL. No relocated absolute operands.
PROOFS = {
    "image_embedded_cache": (0x177e7, "ff750c8d87a002000050e80018ffff"),
    "cache_lookup": (0x9021, "ff750cff1633d2f7760c8b46148b3c90eb1057ff5604593b450c741157ff56088b385985ff75eb33c05f5e5dc38bc7ebf8"),
    "cache_key_callback": (0x37c54, "558bec8b45088b40385dc3"),
    "cache_next_callback": (0x37c5f, "558bec8b450805a80000005dc3"),
    "cache_hash_callback": (0x2228, "8b442404c3"),
}


def layout(pe: base.PE) -> dict:
    for name, (rva, expected_hex) in PROOFS.items():
        expected = bytes.fromhex(expected_hex)
        off = pe.offset(rva)
        if pe.data[off:off+len(expected)] != expected:
            raise base.Refusal(f"Unsupported class cache proof: {name}")
    accessors = {}
    for name in ("mono_class_get_image", "mono_class_get_name", "mono_class_get_namespace", "mono_class_get_parent"):
        code = pe.code(name, 11)
        if code[:8] != bytes.fromhex("558bec8b45088b40") or code[9:] != b"\x5d\xc3":
            raise base.Refusal(f"Unsupported class accessor: {name}")
        accessors[name] = {"rva": pe.exports[name], "offset": code[8], "bytes": code.hex()}
    return {"cache_embedded_offset": 0x2a0, "cache_header_size": 24,
            "cache_expected_callback_rvas": [0x2228, 0x37c54, 0x37c5f],
            "class_token_offset": 0x38, "class_next_offset": 0xa8,
            "max_chain_nodes": 64, "accessors": accessors,
            "proofs": {name: {"rva": rva, "bytes": code} for name,(rva,code) in PROOFS.items()}}


def validate_expected_string(reader: base.Reader, pointer: int, expected: str, purpose: str):
    data = expected.encode("ascii") + b"\0"
    if reader.read(pointer, len(data), purpose) != data:
        raise base.Refusal(f"Class metadata identity differs: {purpose}")


def cached_class(reader: base.Reader, mono_base: int, image: int, info: dict,
                 namespace: str, name: str, token: int) -> dict:
    cache = image + info["cache_embedded_offset"]
    header = reader.read(cache, info["cache_header_size"], "class cache metadata header")
    h = struct.unpack("<6I", header)
    if list(h[:3]) != [mono_base + rva for rva in info["cache_expected_callback_rvas"]]:
        raise base.Refusal("Class cache callbacks differ from pinned native implementation")
    count, entries, buckets = h[3:]
    if not 1 <= count <= 131071 or not 1 <= entries <= 65535:
        raise base.Refusal("Class cache size outside bounds or not initialized")
    bucket_slot = buckets + (token % count) * 4
    first = reader.pointer(bucket_slot, "exact TypeDef hash bucket")
    node, visited, found = first, set(), 0
    while node:
        if node in visited or len(visited) >= info["max_chain_nodes"]:
            raise base.Refusal("Type cache collision chain cycle or limit")
        visited.add(node)
        node_token = reader.pointer(node + info["class_token_offset"], "cached TypeDef token")
        if node_token == token:
            found = node
            break
        node = reader.pointer(node + info["class_next_offset"], "class cache collision next")
    if reader.read(cache, info["cache_header_size"], "class cache header consistency") != header:
        raise base.Refusal("Class cache changed during observation")
    if reader.pointer(bucket_slot, "type bucket consistency") != first:
        raise base.Refusal("Class cache bucket changed during observation")
    identity = {"namespace": namespace, "name": name, "token": hex(token), "bucket_nodes_visited": len(visited)}
    if not found:
        return {**identity, "status": "UNVERIFIED", "reason": "exact type not currently in native class cache; no initialization attempted"}
    access = info["accessors"]
    actual_image = reader.pointer(found + access["mono_class_get_image"]["offset"], "class declaring image")
    if actual_image != image:
        raise base.Refusal("Cached class declaring image mismatch")
    for accessor, expected, purpose in (("mono_class_get_name",name,"whitelisted class name"),
                                         ("mono_class_get_namespace",namespace,"whitelisted class namespace")):
        pointer = reader.pointer(found + access[accessor]["offset"], purpose + " pointer")
        validate_expected_string(reader, pointer, expected, purpose)
    parent = reader.pointer(found + access["mono_class_get_parent"]["offset"], "class parent metadata pointer")
    if reader.pointer(found + info["class_token_offset"], "matched TypeDef token consistency") != token:
        raise base.Refusal("Matched class token changed")
    return {**identity, "status": "PASS", "class": hex(found), "image": hex(image), "parent": hex(parent),
            "cache_bucket_count": count, "cache_entry_count": entries}


def live(pid: int, pe: base.PE, info: dict) -> dict:
    first = base.live_root(pid, pe, base.offline_layout(pe))
    reader = base.Reader(pid)
    try:
        if reader.process_creation_filetime() != first["process_creation_filetime"]:
            raise base.Refusal("Process identity changed between observations")
        mono = first["modules"]["mono.dll"]
        current = [m for m in reader.modules() if m["name"].lower() == "mono.dll"]
        if current != [mono]:
            raise base.Refusal("Mono module identity changed")
        root_slot = int(first["root_domain_slot"],16)
        if reader.pointer(root_slot, "root domain identity before types") != int(first["root_domain"],16):
            raise base.Refusal("Root domain changed before class query")
        for proof in [*info["proofs"].values(), *info["accessors"].values()]:
            expected = bytes.fromhex(proof["bytes"])
            if reader.read(mono["base"] + proof["rva"],len(expected),"pinned native class metadata proof") != expected:
                raise base.Refusal("Loaded native class metadata instructions differ")
        images = {}
        for assembly in first["assemblies"]:
            name = assembly["name"]
            if name.endswith(".dll"):
                name = name[:-4]
            if name in images:
                raise base.Refusal("Ambiguous loaded assembly image name")
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
            raise base.Refusal("Database.Character parent does not match validated Database._character")
        return {"status":"PASS" if len(passed)==len(TARGETS) else "UNVERIFIED",
                "pid":pid,"process_creation_filetime":first["process_creation_filetime"],
                "root_domain":first["root_domain"],"mono_base":hex(mono["base"]),"classes":classes,
                "assembly_stage_read_calls":first["read_calls"],"assembly_stage_requested_bytes":first["requested_read_bytes"],
                "type_stage_read_calls":reader.read_calls,"type_stage_requested_bytes":reader.read_bytes,
                "live_game_scalars":"UNVERIFIED: disabled; no object/static field data read"}
    finally:
        reader.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live",action="store_true")
    parser.add_argument("--pid",type=int)
    parser.add_argument("--output",type=Path)
    args=parser.parse_args()
    output = args.output.resolve() if args.output else None
    if output and (output.parent != Path(__file__).resolve().parents[1] or not output.name.startswith("live-") or output.suffix != ".json"):
        parser.error("Output must be live-*.json directly under unity-object-layer")
    result={"utc":datetime.now(timezone.utc).isoformat(),"mode":"live" if args.live else "offline"}
    try:
        pe=base.PE(base.MONO,base.MONO_SHA256)
        info=layout(pe)
        result.update({"mono_sha256":pe.sha256,"layout":info,"whitelisted_types":[{"assembly":a,"namespace":n,"name":t,"token":hex(k)} for a,n,t,k in TARGETS]})
        if args.live:
            if args.pid is None or args.pid<=0:
                raise base.Refusal("Live mode requires explicit positive --pid")
            result.update(live(args.pid,pe,info))
        else:
            if args.pid is not None:
                raise base.Refusal("--pid without --live not accepted")
            result.update({"status":"PASS","coverage":"offline native proof matching only; no process APIs called"})
    except (OSError,ValueError,KeyError,struct.error,base.Refusal) as exc:
        result.update({"status":"REFUSED","error":str(exc)})
    text=json.dumps(result,indent=2)
    if output:
        output.write_text(text+"\n",encoding="utf-8")
    print(text)
    return 0 if result["status"]=="PASS" else 2


if __name__=="__main__":
    raise SystemExit(main())
