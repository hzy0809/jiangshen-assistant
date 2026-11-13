"""Bounded read-only Global.character_ level/stamina/gold snapshot.

Offline by default. Reads exact declared field ordinals and validates live names,
types, flags, declaring classes and initialized per-domain vtables before values.
No game code invocation, memory modification, or application input exists here.
"""
from __future__ import annotations
import sys
sys.dont_write_bytecode = True
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import probe_mono_readonly as base
import probe_mono_types as types

MANAGED_HASHES = {
    "Assembly-CSharp.dll": "c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911",
    "Assembly-CSharp-firstpass.dll": "29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87",
    "pbdata.dll": "905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab",
}
FIELD_SPECS = {
    "character_": {"ordinal":41,"metadata_token":0x04000957,"signature":"06-12-81-C4","mono_type":0x12,"flags":0x16},
    "level": {"ordinal":14,"metadata_token":0x04000204,"signature":"06-06","mono_type":0x06,"flags":0x06,"format":"<h"},
    "stamina": {"ordinal":18,"metadata_token":0x04000208,"signature":"06-06","mono_type":0x06,"flags":0x06,"format":"<h"},
    "gold": {"ordinal":20,"metadata_token":0x0400020a,"signature":"06-08","mono_type":0x08,"flags":0x06,"format":"<i"},
}
# Only relocation-free native instruction slices. Their offsets and semantics
# are recorded in runtime-native-disassembly.txt. The slots below are derived
# from exact native mono_domain_foreach absolute operands in offline_layout().
PROOFS = {
    "class_field_array": (0x1a26f,"807e2000597522837e6400741c8b4674eb138b4d088b5164c1e20403517483c0103bc273048907"),
    "class_runtime_info_domain_vtable": (0x61318,"8b86a40000008b550885c074198b4a60570fb7383bf95f7c0d8d44880483380074048b00eb0d"),
    "ordinary_static_field_address": (0x60fa1,"8b400c03c1"),
    "ordinary_instance_field_address": (0x5eef5,"8b400c034508"),
    "class_initialized_fast_path": (0x6157f,"f6471c02740733c0e96f030000"),
    "class_init_success_failure_bits": (0x618b8,"8b471ca804750683c80289471c"),
}


def offline_layout(pe: base.PE) -> dict:
    for name,(rva,expected) in PROOFS.items():
        code=bytes.fromhex(expected)
        off=pe.offset(rva)
        if pe.data[off:off+len(code)] != code:
            raise base.Refusal(f"Unsupported native scalar layout: {name}")
    accessors={}
    for name in ("mono_field_get_name","mono_field_get_parent","mono_field_get_offset","mono_domain_get_id"):
        code=pe.code(name,11)
        if code[:8] != bytes.fromhex("558bec8b45088b40") or code[9:] != b"\x5d\xc3":
            raise base.Refusal(f"Unsupported scalar accessor: {name}")
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
            raise base.Refusal(f"Unsupported scalar accessor: {name}")
        accessors[name]={"rva":pe.exports[name],"bytes":expected}
    domain_count_rva=pe.exports["mono_domain_foreach"]+0x12
    count_code=pe.data[pe.offset(domain_count_rva):pe.offset(domain_count_rva)+7]
    if count_code[:3]!=bytes.fromhex("0fb73d"):
        raise base.Refusal("Unsupported native domain-count instruction")
    domain_array_rva=pe.exports["mono_domain_foreach"]+0x33
    array_code=pe.data[pe.offset(domain_array_rva):pe.offset(domain_array_rva)+6]
    if array_code[:2]!=bytes.fromhex("ff35"):
        raise base.Refusal("Unsupported native domain-array instruction")
    slots={"count_slot_rva":base.u32(count_code,3)-pe.base,"array_slot_rva":base.u32(array_code,2)-pe.base}
    for rva in slots.values():
        if not 0<=rva<pe.image_size-4:
            raise base.Refusal("Domain registry slot outside image")
    hashes={}
    for name,expected in MANAGED_HASHES.items():
        observed=hashlib.sha256((base.INSTALL/"js_Data"/"Managed"/name).read_bytes()).hexdigest()
        if observed!=expected:
            raise base.Refusal(f"Managed assembly hash changed: {name}")
        hashes[name]=observed
    return {"accessors":accessors,"proofs":{n:{"rva":r,"bytes":c} for n,(r,c) in PROOFS.items()},
            "domain_registry":{**slots,"count_instruction_rva":domain_count_rva,"count_instruction":count_code.hex(),
                               "array_instruction_rva":domain_array_rva,"array_instruction":array_code.hex()},
            "managed_sha256":hashes,"field_specs":FIELD_SPECS,
            "class_failure_offset":0x20,"field_count_offset":0x64,"field_array_offset":0x74,"field_stride":16,
            "class_runtime_info_offset":0xa4,"vtable_class_offset":0,"vtable_domain_offset":8,"vtable_static_data_offset":12,
            "vtable_init_flags_offset":0x1c,"initialized_mask":2,"init_failed_mask":4}


def read_field(reader: base.Reader, info: dict, klass: int, name: str) -> dict:
    spec=FIELD_SPECS[name]
    if reader.read(klass+info["class_failure_offset"],1,"class failure metadata")!=b"\0":
        raise base.Refusal("Class load failure marker is set")
    count=reader.pointer(klass+info["field_count_offset"],"class declared field count")
    array=reader.pointer(klass+info["field_array_offset"],"already-populated class fields")
    if not spec["ordinal"]<count<=1024 or not array:
        raise base.Refusal(f"Whitelisted declared field unavailable: {name}")
    address=array+spec["ordinal"]*info["field_stride"]
    raw=reader.read(address,16,"exact whitelisted field descriptor")
    typeptr,nameptr,parent,offset=struct.unpack("<4I",raw)
    if parent!=klass:
        raise base.Refusal(f"Field declaring class mismatch: {name}")
    types.validate_expected_string(reader,nameptr,name,"whitelisted field "+name)
    type_raw=reader.read(typeptr,8,"whitelisted MonoType metadata")
    type_data,attrs=struct.unpack_from("<IH",type_raw)
    if attrs!=spec["flags"] or type_raw[6]!=spec["mono_type"] or base.u32(type_raw,4)&0xc0000000:
        raise base.Refusal(f"Field type/flags/byref mismatch: {name}")
    if offset>4096:
        raise base.Refusal(f"Field offset special or outside bounded object/static layout: {name}")
    if reader.read(address,16,"field descriptor consistency")!=raw:
        raise base.Refusal("Field descriptor changed")
    return {"name":name,"descriptor":hex(address),"type_pointer":hex(typeptr),"type_data":type_data,
            "offset":offset,"type_enum":type_raw[6],"flags":attrs,"declared_ordinal":spec["ordinal"],
            "offline_field_token":hex(spec["metadata_token"]),"offline_signature":spec["signature"]}


def domain_registry(reader: base.Reader,mono_base: int,info: dict) -> tuple[list[dict],dict]:
    registry=info["domain_registry"]
    count_slot=mono_base+registry["count_slot_rva"]
    array_slot=mono_base+registry["array_slot_rva"]
    count_raw=reader.read(count_slot,2,"registered Mono domain count")
    count=struct.unpack("<H",count_raw)[0]
    array=reader.pointer(array_slot,"registered Mono domain array")
    if not 1<=count<=32 or not array:
        raise base.Refusal("Mono domain registry outside fixed bound")
    raw=reader.read(array,count*4,"registered Mono domain pointers")
    domains=[]
    ids=set()
    for index in range(count):
        ptr=base.u32(raw,index*4)
        if not ptr:
            continue
        ident=reader.pointer(ptr+info["accessors"]["mono_domain_get_id"]["offset"],"registered Mono domain id")
        if ident in ids or ident>255:
            raise base.Refusal("Duplicate or unsupported Mono domain id")
        ids.add(ident)
        domains.append({"pointer":ptr,"id":ident})
    if not domains:
        raise base.Refusal("No registered Mono domain")
    return domains,{"count_slot":count_slot,"count_raw":count_raw,"array_slot":array_slot,"array":array,"raw":raw}


def validate_domains_unchanged(reader: base.Reader,seal: dict):
    if reader.read(seal["count_slot"],2,"domain count consistency")!=seal["count_raw"] or reader.pointer(seal["array_slot"],"domain array identity consistency")!=seal["array"] or reader.read(seal["array"],len(seal["raw"]),"domain array contents consistency")!=seal["raw"]:
        raise base.Refusal("Mono domain registry changed during observation")


def live(pid: int,pe: base.PE,info: dict) -> dict:
    class_stage=types.live(pid,pe,types.layout(pe))
    if class_stage["status"]!="PASS":
        raise base.Refusal("Required types not all cached; no scalar reads")
    klass={c["name"]:int(c["class"],16) for c in class_stage["classes"]}
    mono_base=int(class_stage["mono_base"],16)
    reader=base.Reader(pid)
    try:
        if reader.process_creation_filetime()!=class_stage["process_creation_filetime"]:
            raise base.Refusal("Process identity changed before scalar stage")
        modules=[m for m in reader.modules() if m["name"].lower()=="mono.dll"]
        if len(modules)!=1 or modules[0]["base"]!=mono_base or Path(modules[0]["path"]).resolve()!=base.MONO.resolve():
            raise base.Refusal("Mono module changed before scalar stage")
        for proof in [*info["proofs"].values(),*info["accessors"].values()]:
            code=bytes.fromhex(proof["bytes"])
            if reader.read(mono_base+proof["rva"],len(code),"pinned native scalar layout proof")!=code:
                raise base.Refusal("Loaded scalar layout instructions differ")
        registry=info["domain_registry"]
        for prefix,operand in (("count",3),("array",2)):
            code=bytearray.fromhex(registry[prefix+"_instruction"])
            struct.pack_into("<I",code,operand,mono_base+registry[prefix+"_slot_rva"])
            if reader.read(mono_base+registry[prefix+"_instruction_rva"],len(code),"pinned native domain registry proof")!=code:
                raise base.Refusal("Loaded native domain registry instructions differ")
        fields={"character_":read_field(reader,info,klass["Global"],"character_")}
        for name in ("level","stamina","gold"):
            fields[name]=read_field(reader,info,klass["_character"],name)
        if fields["character_"]["type_data"]!=klass["Character"]:
            raise base.Refusal("Global.character_ declared type is not validated Database.Character")
        domains,seal=domain_registry(reader,mono_base,info)
        runtime_info=reader.pointer(klass["Global"]+info["class_runtime_info_offset"],"Global existing runtime_info")
        if not runtime_info:
            raise base.Refusal("Global has no existing runtime_info; no initialization attempted")
        max_domain=struct.unpack("<H",reader.read(runtime_info,2,"runtime_info domain capacity"))[0]
        if max_domain>255:
            raise base.Refusal("Global runtime_info domain capacity outside fixed bound")
        candidates=[]
        for domain in domains:
            if domain["id"]>max_domain:
                continue
            vtable=reader.pointer(runtime_info+4+domain["id"]*4,"Global existing per-domain vtable")
            if not vtable:
                continue
            if reader.pointer(vtable,"Global vtable class")!=klass["Global"] or reader.pointer(vtable+8,"Global vtable domain")!=domain["pointer"]:
                raise base.Refusal("Existing Global vtable class/domain mismatch")
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
                raise base.Refusal("Character object class/domain differs from validated type")
            candidates.append({"domain":domain,"global_vtable":vtable,"static_data":static_data,
                               "reference_slot":reference_slot,"character":character,"object_vtable":object_vtable,
                               "global_init_flags":init_flags})
        if len(candidates)!=1:
            raise base.Refusal("Expected exactly one initialized nonnull Character reference across registered domains")
        selected=candidates[0]
        observed_at=datetime.now(timezone.utc).isoformat()
        values={}
        for name in ("level","stamina","gold"):
            fmt=FIELD_SPECS[name]["format"]
            values[name]=struct.unpack(fmt,reader.read(selected["character"]+fields[name]["offset"],struct.calcsize(fmt),"whitelisted numeric Character."+name))[0]
        # The external snapshot cannot freeze the GC or game thread. Refuse on
        # any observed reference/vtable/domain change and disclose non-atomicity.
        if reader.pointer(selected["reference_slot"],"Character reference consistency")!=selected["character"] or reader.pointer(selected["character"],"Character object vtable consistency")!=selected["object_vtable"] or reader.pointer(selected["object_vtable"],"Character class consistency")!=klass["Character"]:
            raise base.Refusal("Character reference/type changed during scalar observation")
        if reader.pointer(klass["Global"]+info["class_runtime_info_offset"],"Global runtime_info consistency")!=runtime_info:
            raise base.Refusal("Global runtime_info changed")
        if reader.pointer(selected["global_vtable"]+info["vtable_init_flags_offset"],"Global initialized state consistency") & 6 != 2:
            raise base.Refusal("Global class initialization state changed")
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


def main() -> int:
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live",action="store_true")
    parser.add_argument("--pid",type=int)
    parser.add_argument("--output",type=Path)
    args=parser.parse_args()
    output=args.output.resolve() if args.output else None
    if output and (output.parent!=Path(__file__).resolve().parents[1] or not output.name.startswith("live-") or output.suffix!=".json"):
        parser.error("Output must be live-*.json directly under unity-object-layer")
    result={"utc":datetime.now(timezone.utc).isoformat(),"mode":"live" if args.live else "offline"}
    try:
        pe=base.PE(base.MONO,base.MONO_SHA256)
        info=offline_layout(pe)
        result.update({"mono_sha256":pe.sha256,"layout":info})
        if args.live:
            if args.pid is None or args.pid<=0:
                raise base.Refusal("Live mode requires explicit positive --pid")
            result.update(live(args.pid,pe,info))
        else:
            if args.pid is not None:
                raise base.Refusal("--pid without --live not accepted")
            result.update({"status":"PASS","coverage":"offline pinned native layout and managed hashes only; no process APIs called"})
    except (OSError,ValueError,KeyError,struct.error,base.Refusal) as exc:
        result.update({"status":"REFUSED","error":str(exc)})
    text=json.dumps(result,indent=2)
    if output:
        output.write_text(text+"\n",encoding="utf-8")
    print(text)
    return 0 if result["status"]=="PASS" else 2


if __name__=="__main__":
    raise SystemExit(main())
