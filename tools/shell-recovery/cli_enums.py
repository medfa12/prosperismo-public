#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""
Recover enum members and their numeric values from an embedded .NET assembly.

BGLayer.dll.sprx ships decrypted and carries CLI metadata, so the background
layer's contract is readable directly. The #Strings heap gives every type and
field *name*, but an enum is only actionable once its members' *values* are
known - LightRenderModeIndex is useless as a list of names when the shell
selects a mode by number.

Values live in the #~ table stream: an enum is a TypeDef whose fields are
static literals, and each literal's value sits in the Constant table keyed by a
coded index back to the field. Reaching those two tables means computing the
byte width of every preceding table, which is why this parses the whole schema
prefix rather than seeking directly.
"""

import argparse
import struct
import sys

# Table indices used here. The row-size calculation needs every table up to the
# highest one read, so the schema below stops at Constant (0x0B).
MODULE, TYPEREF, TYPEDEF, FIELDPTR, FIELD = 0x00, 0x01, 0x02, 0x03, 0x04
METHODPTR, METHODDEF, PARAMPTR, PARAM = 0x05, 0x06, 0x07, 0x08
INTERFACEIMPL, MEMBERREF, CONSTANT = 0x09, 0x0A, 0x0B

# Coded-index definitions: (tag_bit_count, [tables it can point at]).
TYPEDEFORREF = (2, [TYPEDEF, TYPEREF, 0x1B])
RESOLUTIONSCOPE = (2, [MODULE, 0x1A, 0x23, TYPEREF])
MEMBERREFPARENT = (3, [TYPEDEF, TYPEREF, 0x1A, METHODDEF, 0x1B])
HASCONSTANT = (2, [FIELD, PARAM, 0x17])

# Element types for constant values.
ELEMENT_SIZES = {
    0x02: 1, 0x03: 2, 0x04: 1, 0x05: 1, 0x06: 2, 0x07: 2,
    0x08: 4, 0x09: 4, 0x0A: 8, 0x0B: 8, 0x0C: 4, 0x0D: 8,
}
ELEMENT_SIGNED = {0x04, 0x06, 0x08, 0x0A}


class Metadata:
    def __init__(self, data):
        start = data.find(b"BSJB")
        if start < 0:
            raise SystemExit("no CLI metadata (BSJB) in file")
        self.data = data
        self.start = start

        ver_len, = struct.unpack("<I", data[start + 12:start + 16])
        p = start + 16 + ((ver_len + 3) & ~3)
        _, stream_count = struct.unpack("<HH", data[p:p + 4])
        p += 4

        self.heaps = {}
        for _ in range(stream_count):
            off, size = struct.unpack("<II", data[p:p + 8])
            p += 8
            end = data.find(b"\x00", p)
            name = data[p:end].decode()
            p = (end + 1 + 3) & ~3
            self.heaps[name] = (start + off, size)

        self._parse_tables()

    def string(self, index):
        base, size = self.heaps["#Strings"]
        end = self.data.find(b"\x00", base + index)
        return self.data[base + index:end].decode("utf-8", "replace")

    def blob(self, index):
        base, _ = self.heaps["#Blob"]
        p = base + index
        first = self.data[p]
        if first & 0x80 == 0:
            length, p = first & 0x7F, p + 1
        elif first & 0xC0 == 0x80:
            length = ((first & 0x3F) << 8) | self.data[p + 1]
            p += 2
        else:
            length = (((first & 0x1F) << 24) | (self.data[p + 1] << 16)
                      | (self.data[p + 2] << 8) | self.data[p + 3])
            p += 4
        return self.data[p:p + length]

    def _parse_tables(self):
        base, _ = self.heaps["#~"]
        heap_sizes = self.data[base + 6]
        valid, = struct.unpack("<Q", self.data[base + 8:base + 16])
        p = base + 24

        self.rows = {}
        for t in range(64):
            if valid & (1 << t):
                self.rows[t], = struct.unpack("<I", self.data[p:p + 4])
                p += 4
        self.rows_start = p

        self.str_w = 4 if heap_sizes & 0x01 else 2
        self.guid_w = 4 if heap_sizes & 0x02 else 2
        self.blob_w = 4 if heap_sizes & 0x04 else 2

    def _table_w(self, table):
        return 4 if self.rows.get(table, 0) >= (1 << 16) else 2

    def _coded_w(self, coded):
        bits, tables = coded
        limit = 1 << (16 - bits)
        return 4 if any(self.rows.get(t, 0) >= limit for t in tables) else 2

    def _schema(self, table):
        S, B, G = self.str_w, self.blob_w, self.guid_w
        T = self._table_w
        C = self._coded_w
        return {
            MODULE:        [2, S, G, G, G],
            TYPEREF:       [C(RESOLUTIONSCOPE), S, S],
            TYPEDEF:       [4, S, S, C(TYPEDEFORREF), T(FIELD), T(METHODDEF)],
            FIELDPTR:      [T(FIELD)],
            FIELD:         [2, S, B],
            METHODPTR:     [T(METHODDEF)],
            METHODDEF:     [4, 2, 2, S, B, T(PARAM)],
            PARAMPTR:      [T(PARAM)],
            PARAM:         [2, 2, S],
            INTERFACEIMPL: [T(TYPEDEF), C(TYPEDEFORREF)],
            MEMBERREF:     [C(MEMBERREFPARENT), S, B],
            CONSTANT:      [1, 1, C(HASCONSTANT), B],
        }.get(table)

    def table_offset(self, table):
        """Byte offset of a table's first row, after all preceding tables."""
        p = self.rows_start
        for t in sorted(self.rows):
            if t == table:
                return p
            schema = self._schema(t)
            if schema is None:
                raise SystemExit(f"table 0x{t:02X} precedes the target and is not in the schema")
            p += sum(schema) * self.rows[t]
        return None

    def read_rows(self, table):
        schema = self._schema(table)
        offset = self.table_offset(table)
        if offset is None or schema is None:
            return []
        width = sum(schema)
        out = []
        for i in range(self.rows.get(table, 0)):
            p = offset + (i * width)
            row = []
            for w in schema:
                row.append(int.from_bytes(self.data[p:p + w], "little"))
                p += w
            out.append(row)
        return out


def decode_constant(elem_type, blob):
    size = ELEMENT_SIZES.get(elem_type)
    if not size or len(blob) < size:
        return None
    return int.from_bytes(blob[:size], "little",
                          signed=elem_type in ELEMENT_SIGNED)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("assembly")
    ap.add_argument("--filter", action="append", default=None,
                    help="only show enums whose name contains this (repeatable)")
    args = ap.parse_args()

    md = Metadata(open(args.assembly, "rb").read())
    typedefs = md.read_rows(TYPEDEF)
    fields = md.read_rows(FIELD)
    constants = md.read_rows(CONSTANT)

    # Constant.Parent is a HasConstant coded index; tag 0 means Field.
    bits, _ = HASCONSTANT
    field_value = {}
    for elem_type, _pad, parent, blob_ix in constants:
        if (parent & ((1 << bits) - 1)) != 0:
            continue
        field_row = parent >> bits          # 1-based Field index
        value = decode_constant(elem_type, md.blob(blob_ix))
        if value is not None:
            field_value[field_row] = value

    FIELD_STATIC, FIELD_LITERAL = 0x0010, 0x0040
    print(f"{args.assembly}")
    print(f"  {len(typedefs)} typedefs, {len(fields)} fields, {len(constants)} constants\n")

    shown = 0
    for i, td in enumerate(typedefs):
        name = md.string(td[1])
        if args.filter and not any(f.lower() in name.lower() for f in args.filter):
            continue
        first = td[4]
        last = typedefs[i + 1][4] if i + 1 < len(typedefs) else len(fields) + 1
        members = []
        for row in range(first, last):
            if row - 1 >= len(fields):
                break
            flags = fields[row - 1][0]
            if flags & FIELD_LITERAL and flags & FIELD_STATIC and row in field_value:
                members.append((md.string(fields[row - 1][1]), field_value[row]))
        if not members:
            continue
        ns = md.string(td[2])
        print(f"  {(ns + '.' if ns else '') + name}")
        for member, value in sorted(members, key=lambda m: m[1]):
            print(f"      {value:>6}  {member}")
        print()
        shown += 1

    if shown == 0:
        print("  (no enums matched)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
