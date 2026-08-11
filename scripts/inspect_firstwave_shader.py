#!/usr/bin/env python3
"""Decode a raw FirstWave shader range with Sony's SDK 10 ISA oracle.

This is an evidence tool.  It never writes extracted firmware bytes; decoded
instructions and aggregate resource/constant contracts are emitted as JSON on
stdout.  The SDK DLL and firmware eboot are user-supplied local inputs.
"""

from __future__ import annotations

import argparse
from collections import Counter
import ctypes
import hashlib
import json
import os
from pathlib import Path
import sys


class _Option(ctypes.Structure):
    _fields_ = [("id", ctypes.c_uint64), ("value", ctypes.c_uint64)]


def _parse_int(value: str) -> int:
    return int(value, 0)


def _strings(value: object) -> list[str]:
    if not isinstance(value, list):
        return []
    result: list[str] = []
    for item in value:
        if isinstance(item, str):
            result.append(item)
        elif isinstance(item, dict) and isinstance(item.get("v"), str):
            result.append(item["v"])
        else:
            result.append(json.dumps(item, sort_keys=True, separators=(",", ":")))
    return result


class ShaderIsaOracle:
    def __init__(self, dll_path: Path, generation: int) -> None:
        if os.name != "nt":
            raise RuntimeError("libSceShaderIsaP.dll is a Windows host tool")
        self._dll_directory = os.add_dll_directory(str(dll_path.parent))
        self._dll = ctypes.CDLL(str(dll_path))
        self._generation = generation
        self._disassemble = self._dll.sceShaderIsaDisassembleRaw
        self._disassemble.argtypes = [
            ctypes.POINTER(_Option),
            ctypes.POINTER(ctypes.c_uint8),
            ctypes.c_uint32,
        ]
        self._disassemble.restype = ctypes.c_void_p
        self._instruction_size = self._dll.sceShaderIsaGetInstructionSize
        self._instruction_size.argtypes = [ctypes.c_uint32, ctypes.c_uint64]
        self._instruction_size.restype = ctypes.c_uint32
        self._free = self._dll.sceShaderIsaFreeResult
        self._free.argtypes = [ctypes.c_void_p]
        self._free.restype = None

    def decode_one(self, data: bytes) -> dict[str, object]:
        probe = data[:20]
        padded = probe[:8].ljust(8, b"\0")
        api_size = int(
            self._instruction_size(
                self._generation, int.from_bytes(padded, "little")
            )
        )
        options = (_Option * 2)(_Option(1, self._generation), _Option(0, 0))
        raw = (ctypes.c_uint8 * len(probe)).from_buffer_copy(probe)
        result = self._disassemble(options, raw, len(probe))
        if not result:
            raise RuntimeError("Sony shader ISA oracle returned no result")
        try:
            document = json.loads(ctypes.string_at(result).decode("utf-8"))
        finally:
            self._free(result)
        instructions = document.get("insts")
        if not isinstance(instructions, list) or not instructions:
            raise RuntimeError(f"Sony shader ISA oracle returned no instruction: {document}")
        instruction = instructions[0]
        if not isinstance(instruction, dict):
            raise RuntimeError(f"unexpected instruction record: {instruction}")
        json_size = instruction.get("size")
        if not isinstance(json_size, int) or json_size <= 0:
            raise RuntimeError(f"invalid JSON instruction size: {json_size}")
        if api_size != json_size:
            raise RuntimeError(
                f"instruction-size disagreement: API={api_size}, JSON={json_size}"
            )
        return {
            "size": json_size,
            "opcode": str(instruction.get("opcode", "")),
            "operands": _strings(instruction.get("operands")),
            "options": _strings(instruction.get("options")),
        }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--eboot", type=Path, required=True)
    parser.add_argument("--shader-isa-dll", type=Path, required=True)
    parser.add_argument("--offset", type=_parse_int)
    parser.add_argument("--length", type=_parse_int)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--generation", type=int, default=2)
    parser.add_argument("--instructions", action="store_true")
    args = parser.parse_args()

    oracle = ShaderIsaOracle(args.shader_isa_dll.resolve(), args.generation)

    if args.manifest is not None:
        if args.offset is not None or args.length is not None:
            parser.error("--manifest cannot be combined with --offset/--length")
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        eboot_bytes = args.eboot.read_bytes()
        identity = manifest["eboot"]
        failures: list[str] = []
        if len(eboot_bytes) != int(identity["size"]):
            failures.append(
                f"eboot size: expected {identity['size']}, got {len(eboot_bytes)}"
            )
        actual_eboot_hash = hashlib.sha256(eboot_bytes).hexdigest()
        if actual_eboot_hash != identity["sha256"]:
            failures.append(
                f"eboot sha256: expected {identity['sha256']}, got {actual_eboot_hash}"
            )
        verified: list[str] = []
        for stage in manifest["stages"]:
            offset = _parse_int(stage["file_offset"])
            length = _parse_int(stage["code_length"])
            result = inspect_range(eboot_bytes[offset : offset + length], offset, oracle)
            checks = {
                "sha256": result["sha256"],
                "instruction_count": result["instruction_count"],
                "first_opcode": result["first_opcode"],
                "last_opcode": result["last_opcode"],
            }
            for key, actual in checks.items():
                if actual != stage[key]:
                    failures.append(
                        f"{stage['name']} {key}: expected {stage[key]}, got {actual}"
                    )
            actual_counts = dict(
                sorted(Counter(item["opcode"] for item in result["resources"]).items())
            )
            if actual_counts != stage["resource_opcode_counts"]:
                failures.append(
                    f"{stage['name']} resource opcode counts differ: {actual_counts}"
                )
            actual_scalar = [
                {
                    "relative_offset": f"0x{item['relative_offset']:x}",
                    "opcode": item["opcode"],
                    "operands": item["operands"],
                    "options": item["options"],
                }
                for item in result["scalar_loads"]
            ]
            if actual_scalar != stage["scalar_loads"]:
                failures.append(f"{stage['name']} scalar-load contract differs")
            actual_exports = [
                {
                    "relative_offset": f"0x{item['relative_offset']:x}",
                    "operands": item["operands"],
                    "options": item["options"],
                }
                for item in result["exports"]
            ]
            if actual_exports != stage["exports"]:
                failures.append(f"{stage['name']} export contract differs")
            if "terminator_operands" in stage:
                actual_terminator = result["instructions"][-1]["operands"]
                if actual_terminator != stage["terminator_operands"]:
                    failures.append(
                        f"{stage['name']} terminal operands differ: "
                        f"{actual_terminator}"
                    )
            verified.append(stage["name"])
        json.dump(
            {"verified": verified, "failures": failures},
            sys.stdout,
            indent=2,
        )
        print()
        return 1 if failures else 0

    if args.offset is None or args.length is None:
        parser.error("inspection requires both --offset and --length")
    with args.eboot.open("rb") as source:
        source.seek(args.offset)
        program = source.read(args.length)
    if len(program) != args.length:
        raise RuntimeError(
            f"range exceeds eboot: requested {args.length}, read {len(program)}"
        )
    result = inspect_range(program, args.offset, oracle)
    if not args.instructions:
        result.pop("instructions")
    json.dump(result, sys.stdout, indent=2)
    print()
    return 0


def inspect_range(
    program: bytes, offset: int, oracle: ShaderIsaOracle
) -> dict[str, object]:
    decoded: list[dict[str, object]] = []
    cursor = 0
    while cursor < len(program):
        instruction = oracle.decode_one(program[cursor:])
        size = int(instruction["size"])
        if cursor + size > len(program):
            raise RuntimeError(
                f"instruction at +0x{cursor:x} crosses declared range"
            )
        instruction["relative_offset"] = cursor
        instruction["file_offset"] = offset + cursor
        instruction["bytes"] = program[cursor : cursor + size].hex()
        decoded.append(instruction)
        cursor += size

    resource_prefixes = (
        "image_",
        "buffer_",
        "tbuffer_",
        "global_",
        "flat_",
        "scratch_",
        "ds_",
    )
    scalar_loads = [
        item
        for item in decoded
        if str(item["opcode"]).startswith(("s_load", "s_buffer_load"))
    ]
    resources = [
        item
        for item in decoded
        if str(item["opcode"]).startswith(resource_prefixes)
    ]
    exports = [item for item in decoded if item["opcode"] == "exp"]
    result: dict[str, object] = {
        "offset": offset,
        "length": len(program),
        "sha256": hashlib.sha256(program).hexdigest(),
        "instruction_count": len(decoded),
        "first_opcode": decoded[0]["opcode"] if decoded else None,
        "last_opcode": decoded[-1]["opcode"] if decoded else None,
        "scalar_loads": scalar_loads,
        "resources": resources,
        "exports": exports,
        "instructions": decoded,
    }
    return result


if __name__ == "__main__":
    raise SystemExit(main())
