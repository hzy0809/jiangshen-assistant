"""Read cached UnityWeb TextAsset and decode named protobuf table fields only.

Does not load or execute game assemblies, contact a server, or inspect a process.
Schema names/tags come from the adjacent offline ILSpy dbdata declarations.
"""
from pathlib import Path
import hashlib
import json
import lzma
import re
import struct

ROOT = Path(__file__).resolve().parent
SOURCE = Path(r'C:\Users\yu\AppData\LocalLow\GMarkSoft\______\assets\db_template1.unity3d')


def varint(data, pos):
    value = shift = 0
    while True:
        byte = data[pos]
        pos += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, pos
        shift += 7
        if shift > 63:
            raise ValueError('varint overflow')


def fields(data):
    pos = 0
    while pos < len(data):
        key, pos = varint(data, pos)
        tag, wire = key >> 3, key & 7
        if wire == 0:
            value, pos = varint(data, pos)
        elif wire == 2:
            size, pos = varint(data, pos)
            value = data[pos:pos + size]
            if len(value) != size:
                raise ValueError('truncated field')
            pos += size
        elif wire in (1, 5):
            size = 8 if wire == 1 else 4
            value, pos = data[pos:pos + size], pos + size
        else:
            raise ValueError(f'unsupported wire {wire}')
        yield tag, wire, value


def schema(type_name):
    source = (ROOT / f'game.net.data.StoC.{type_name}.cs.txt').read_text(encoding='utf-8-sig')
    pattern = r'\[ProtoMember\((\d+),[^\]]*Name = "([^"]+)"[^\]]*DataFormat = DataFormat\.(\w+)\)\][\s\S]*?public (\w+) \w+'
    return {int(tag): (name, fmt, kind) for tag, name, fmt, kind in re.findall(pattern, source)}


def decode(data, type_name):
    definitions = schema(type_name)
    result = {}
    for tag, wire, value in fields(data):
        name, fmt, kind = definitions[tag]
        if kind == 'string':
            value = value.decode('utf-8')
        elif fmt == 'ZigZag':
            value = (value >> 1) ^ -(value & 1)
        elif kind == 'bool':
            value = bool(value)
        result[name] = value
    return result


raw = SOURCE.read_bytes()
if raw[:9] != b'UnityWeb\0':
    raise ValueError('not the named UnityWeb cache')
# UnityWeb v3 header: the named cache's complete header size is 60 bytes.
header_size = struct.unpack_from('>I', raw, 31)[0]
bundle = lzma.decompress(raw[header_size:])
count = struct.unpack_from('>I', bundle, 0)[0]
if count != 1:
    raise ValueError('expected one bundle entry')
end = bundle.index(0, 4)
entry_offset, entry_size = struct.unpack_from('>II', bundle, end + 1)
serialized = bundle[entry_offset:entry_offset + entry_size]
metadata_size, file_size, version, data_offset = struct.unpack_from('>IIII', serialized)
if version != 9 or serialized[16] != 0:
    raise ValueError('expected little endian serialized v9')
name_size = struct.unpack_from('<I', serialized, data_offset)[0]
name = serialized[data_offset + 4:data_offset + 4 + name_size].decode('ascii')
size_pos = (data_offset + 4 + name_size + 3) & ~3
payload_size = struct.unpack_from('<I', serialized, size_pos)[0]
payload = serialized[size_pos + 4:size_pos + 4 + payload_size]
if name != 'db_template1' or len(payload) != payload_size:
    raise ValueError('unexpected TextAsset')
tables = {67: ('buildings', 'db_building'), 80: ('products', 'db_building_product'), 83: ('types', 'db_building_type')}
result = {
    'source': str(SOURCE), 'source_sha256': hashlib.sha256(raw).hexdigest(),
    'scope': 'cached client configuration; current server and account ownership unverified',
    'bundle_entry': bundle[4:end].decode('ascii'), 'serialized_version': version,
    'text_asset': name, 'payload_size': payload_size,
    'schema': 'adjacent installed dbdata.dll ILSpy declarations; db_templates1 tags 67,80,83',
    'buildings': [], 'products': [], 'types': [],
}
for tag, wire, value in fields(payload):
    if tag in tables:
        target, type_name = tables[tag]
        result[target].append(decode(value, type_name))
(ROOT / 'cached-building-tables.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'source_sha256': result['source_sha256'], 'counts': {key: len(result[key]) for key in ('buildings', 'products', 'types')}, 'types': result['types']}, ensure_ascii=False))
