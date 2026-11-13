"""Offline raw-DEFLATE corpus from Python's independent zlib implementation."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import random
import sys
import zlib


def number(value: int) -> bytes:
    out = bytearray()
    while value >= 128:
        out.append((value & 127) | 128)
        value >>= 7
    out.append(value)
    return bytes(out)


def n(tag: int, value: int) -> bytes:
    return number(tag << 3) + number(value)


def m(tag: int, value: bytes) -> bytes:
    return number((tag << 3) | 2) + number(len(value)) + value


def report(length: int, profile: str) -> bytes:
    rng = random.Random(0x402000 + length)
    if profile == "repeat":
        name = b"A" * length
    elif profile == "pattern":
        name = ("将神报告zlib-1234567890-".encode() * ((length // 28) + 1))[:length]
        # Keep truncation of this synthetic Unicode text on UTF8 boundaries.
        name = name.decode("utf-8", errors="ignore").encode()
    else:
        name = bytes(rng.randrange(32, 127) for _ in range(length))
    left = m(1, b"owner:" + name) + n(2, 174) + n(3, 0) + n(4, 0)
    right = m(1, b"target:" + name[::-1] if profile != "pattern" else b"target:" + name) + n(2, 174) + n(3, 0) + n(4, 0)
    return m(1, left) + m(2, right) + n(3, 1) + n(5, 7) + n(7, 3)


def compress(raw: bytes, level: int, strategy: int, mode: str, mem: int = 8) -> bytes:
    window = 15 if mode == "zlib" else 31 if mode == "gzip" else -15
    codec = zlib.compressobj(level, zlib.DEFLATED, window, mem, strategy)
    if mode in ("sync-finish", "full-finish", "block-finish"):
        split = len(raw) // 2
        return (codec.compress(raw[:split]) + codec.flush(zlib.Z_SYNC_FLUSH if mode == "sync-finish" else zlib.Z_FULL_FLUSH if mode == "full-finish" else zlib.Z_BLOCK)
                + codec.compress(raw[split:]) + codec.flush(zlib.Z_FINISH))
    output = codec.compress(raw)
    return output + codec.flush(zlib.Z_SYNC_FLUSH if mode == "sync-open" else zlib.Z_FULL_FLUSH if mode == "full-open" else zlib.Z_FINISH)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path)
    parser.add_argument("--validate-ionic", type=Path)
    args = parser.parse_args()
    if args.validate_ionic:
        count = 0
        failed = []
        with args.validate_ionic.open(encoding="ascii") as corpus:
            for line in corpus:
                name, _, _, raw_b64, compressed_b64 = line.rstrip("\n").split("\t")
                raw, compressed = base64.b64decode(raw_b64), base64.b64decode(compressed_b64)
                codec = zlib.decompressobj(-15)
                try:
                    valid = codec.decompress(compressed, len(raw) + 1) == raw and codec.eof and not codec.unused_data
                except zlib.error:
                    valid = False
                count += 1
                if not valid:
                    failed.append(name)
        print(json.dumps({"status": "PASS" if not failed else "FAIL", "native_cases": count,
                          "failures": failed, "zlib_runtime": zlib.ZLIB_RUNTIME_VERSION}))
        if failed:
            raise SystemExit(1)
        return
    if not args.output:
        parser.error("--output or --validate-ionic is required")
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = []
    rows = []

    def emit(name: str, raw: bytes, compressed: bytes, expected: bool, group: str) -> None:
        decoder = zlib.decompressobj(-15)
        try:
            inflated = decoder.decompress(compressed, len(raw) + 1)
            same = inflated == raw
            eof = decoder.eof
            unused = len(decoder.unused_data)
        except zlib.error:
            same, eof, unused = False, False, -1
        if expected and not (same and eof and unused == 0):
            raise AssertionError(f"independent zlib rejected supposedly complete raw frame: {name}")
        rows.append("\t".join((name, "accept" if expected else "reject", group, base64.b64encode(raw).decode(), base64.b64encode(compressed).decode())))
        manifest.append({"case": name, "expected": "accept" if expected else "reject", "group": group,
                         "raw_bytes": len(raw), "compressed_bytes": len(compressed), "raw_sha256": hashlib.sha256(raw).hexdigest(),
                         "compressed_sha256": hashlib.sha256(compressed).hexdigest(), "python_matches": same,
                         "python_eof": eof, "python_unused_bytes": unused})

    strategies = {"default": zlib.Z_DEFAULT_STRATEGY, "fixed": zlib.Z_FIXED, "huffman": zlib.Z_HUFFMAN_ONLY, "rle": zlib.Z_RLE}
    lengths = (0, 1, 7, 31, 127, 511, 2047, 8191, 8192, 8193, 32767, 32768, 65535)
    for length in lengths:
        for profile in ("repeat", "pattern", "random"):
            raw = report(length, profile)
            for level in (0, 1, 6, 9):
                for strategy_name, strategy in strategies.items():
                    name = f"python-{profile}-{length}-l{level}-{strategy_name}"
                    emit(name, raw, compress(raw, level, strategy, "finish"), True, "finished")
            for mode in ("sync-finish", "full-finish", "block-finish"):
                emit(f"python-{profile}-{length}-{mode}", raw, compress(raw, 6, zlib.Z_DEFAULT_STRATEGY, mode), True, mode)
            if profile != "repeat":
                for memory in (1, 4, 9):
                    for level in (1, 6, 9):
                        for strategy_name in ("default", "fixed"):
                            emit(f"python-{profile}-{length}-m{memory}-l{level}-{strategy_name}", raw,
                                 compress(raw, level, strategies[strategy_name], "finish", memory), True, "memory-block-boundary")

    raw = report(71, "random")
    complete = compress(raw, 6, zlib.Z_DEFAULT_STRATEGY, "finish")
    for mode in ("sync-open", "full-open", "zlib", "gzip"):
        emit("framing-" + mode, raw, compress(raw, 6, zlib.Z_DEFAULT_STRATEGY, mode), False, "framing-negative")
    for name, tail in (("null", b"\0"), ("newline", b"\r\n"), ("sync-marker", b"\0\0\xff\xff"), ("garbage", b"malicious"), ("concatenated", complete)):
        emit("trailing-" + name, raw, complete + tail, False, "trailing-negative")
    for length in range(len(complete)):
        emit(f"prefix-{length}-of-{len(complete)}", raw, complete[:length], False, "truncation-negative")
    # Bits after a completed EOB inside its final byte are not trailing bytes.
    # Retain only variants independently proved complete and byte-exact by zlib.
    padding = 0
    for value in range(256):
        if value == complete[-1]:
            continue
        changed = complete[:-1] + bytes((value,))
        check = zlib.decompressobj(-15)
        try:
            if check.decompress(changed, len(raw) + 1) == raw and check.eof and not check.unused_data:
                emit(f"legal-final-byte-{value:02x}", raw, changed, True, "legal-final-padding")
                padding += 1
        except zlib.error:
            pass
    (args.output / "corpus.tsv").write_text("\n".join(rows) + "\n", encoding="ascii")
    (args.output / "corpus.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"cases": len(rows), "python": sys.version, "zlib_build": zlib.ZLIB_VERSION,
                      "zlib_runtime": zlib.ZLIB_RUNTIME_VERSION, "legal_final_padding_cases": padding}))


if __name__ == "__main__":
    main()
