#!/usr/bin/env python3
"""Verify the checked-in FirstWave 12.40 host tables against NPXS40087."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct


ROOT = Path(__file__).resolve().parents[1]
TABLE = ROOT / "docs/sony-shell/evidence/firstwave-host-constants-12.40.json"
HEADER = (
    ROOT
    / "frontend/ProsperismoLauncher/windows/Prosperismo/FirstWaveFirmware1240Constants.h"
)


def parse_int(value: str | int) -> int:
    return int(value, 0) if isinstance(value, str) else value


def source_vectors(document: dict[str, object]) -> list[tuple[int, tuple[int, ...]]]:
    result: list[tuple[int, tuple[int, ...]]] = []
    for record in document["palettes"]:
        for field in record["fields"]:
            result.append(
                (parse_int(field["sourceVa"]), tuple(map(parse_int, field["bits"])))
            )
    for name in ("lattice", "boundaryRing"):
        table = document["controlSeeds"][name]
        result.extend(
            (parse_int(va), tuple(map(parse_int, bits)))
            for va, bits in zip(table["sourceVas"], table["bits"], strict=True)
        )
    return result


def header_vectors(text: str) -> list[tuple[int, tuple[int, ...]]]:
    pattern = re.compile(
        r"\{(0x[0-9a-f]+)u, \{\{"
        r"(0x[0-9a-f]+)u, (0x[0-9a-f]+)u, "
        r"(0x[0-9a-f]+)u, (0x[0-9a-f]+)u\}\}\}"
    )
    return [
        (int(match[0], 16), tuple(int(value, 16) for value in match[1:]))
        for match in pattern.findall(text)
    ]


def verify(eboot: Path) -> None:
    document = json.loads(TABLE.read_text(encoding="utf-8"))
    data = eboot.read_bytes()
    source = document["source"]
    assert len(data) == source["executableSize"]
    assert hashlib.sha256(data).hexdigest() == source["executableSha256"]

    delta = source["virtualAddressToFileOffsetDelta"]
    for va, expected in source_vectors(document):
        actual = struct.unpack_from("<4I", data, va + delta)
        assert actual == expected, f"vector mismatch at VA 0x{va:x}"

    for evidence in source["evidenceRanges"]:
        start = parse_int(evidence["startVa"])
        end = parse_int(evidence["endVaExclusive"])
        blob = data[start + delta : end + delta]
        assert len(blob) == evidence["length"]
        assert hashlib.sha256(blob).hexdigest() == evidence["sha256"]

    # Constructor immediates: transition step at object+0x50 and reset palette 4.
    assert data[0xC4D0E + delta : 0xC4D16 + delta].hex() == "41c746500f745a3b"
    assert data[0xC4DBE + delta : 0xC4DC6 + delta].hex() == "41c7464c04000000"

    lattice = document["controlSeeds"]["lattice"]
    boundary = document["controlSeeds"]["boundaryRing"]
    assert lattice["shape"] == {"rows": 11, "columns": 15}
    assert len(lattice["sourceVas"]) == 165
    assert boundary["pairCount"] == 13
    assert len(boundary["sourceVas"]) == 26

    checked = source_vectors(document)
    generated = header_vectors(HEADER.read_text(encoding="utf-8"))
    assert generated == checked, "C++ table is out of sync with JSON evidence"

    projection = document["resetHostUpload"]["worldProjectionMatrix"]["bits"]
    assert projection[0][0] == "0x3fdbbf35"
    assert projection[1][1] == "0x3fdbbf35"
    assert projection[2][2:] == ["0xbf83759f", "0xbf800000"]
    assert projection[3][2] == "0xc3cab3e5"

    print(
        "verified FirstWave 12.40: "
        f"{len(checked)} source vectors, 3 code ranges, reset host upload"
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--eboot", type=Path, required=True)
    args = parser.parse_args()
    verify(args.eboot)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
