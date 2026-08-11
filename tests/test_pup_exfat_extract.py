# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later

import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import zlib


MODULE_PATH = (Path(__file__).parents[1]
               / "tools" / "shell-recovery" / "pup_exfat_extract.py")
SPEC = importlib.util.spec_from_file_location("pup_exfat_extract", MODULE_PATH)
PUP_MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUP_MODULE)


class PupExtractionTests(unittest.TestCase):
    @staticmethod
    def _build_pup(path):
        block_size = 0x10000
        first = b"A" * block_size
        second = bytes(range(256)) * (block_size // 256)
        exfat = bytearray(block_size)
        exfat[3:11] = b"EXFAT   "

        compressed_first = zlib.compress(first)
        compressed_exfat = zlib.compress(bytes(exfat))
        payload_one = compressed_first + (b"padding-is-not-a-block" * 3) + second
        second_offset = len(payload_one) - len(second)
        payload_two = compressed_exfat

        table_one = (b"\0" * 64
                     + struct.pack("<II", 0, len(compressed_first))
                     + struct.pack("<II", second_offset, len(second)))
        # Model the real system_ex edge case: its final table record includes
        # trailing padding beyond the segment payload. The complete zlib
        # stream itself remains inside the segment and validates normally.
        table_two = (b"\0" * 32
                     + struct.pack("<II", 0, len(compressed_exfat) + 7))

        count = 4
        payload_offset = 0x20 + count * 0x20
        pieces = [table_one, payload_one, table_two, payload_two]
        offsets = []
        cursor = payload_offset
        for piece in pieces:
            offsets.append(cursor)
            cursor += len(piece)

        entries = [
            ((1 << 20) | PUP_MODULE.FLAG_BLOCK_TABLE,
             offsets[0], len(table_one), len(table_one)),
            (PUP_MODULE.FLAG_BLOCKED | PUP_MODULE.FLAG_COMPRESSED,
             offsets[1], len(payload_one), len(first) + len(second)),
            ((3 << 20) | PUP_MODULE.FLAG_BLOCK_TABLE,
             offsets[2], len(table_two), len(table_two)),
            (PUP_MODULE.FLAG_BLOCKED | PUP_MODULE.FLAG_COMPRESSED,
             offsets[3], len(payload_two), len(exfat)),
        ]

        header = bytearray(payload_offset)
        struct.pack_into("<I", header, 0, PUP_MODULE.PUP_MAGIC)
        struct.pack_into("<Q", header, 0x10, cursor)
        struct.pack_into("<H", header, 0x18, count)
        for index, entry in enumerate(entries):
            struct.pack_into("<QQQQ", header, 0x20 + index * 0x20, *entry)
        path.write_bytes(bytes(header) + b"".join(pieces))
        return first + second

    def test_extracts_distinct_blocked_segments_using_their_tables(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            pup_path = root / "fixture.PUP.dec"
            expected = self._build_pup(pup_path)
            pup = PUP_MODULE.Pup(pup_path)

            self.assertEqual([3], pup.exfat_segments())
            output = root / "segment-one.img"
            self.assertEqual(len(expected), pup.extract_segment(1, output, 0))
            self.assertEqual(expected, output.read_bytes())

    def test_rejects_header_size_mismatch(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.PUP.dec"
            data = bytearray(0x20)
            struct.pack_into("<I", data, 0, PUP_MODULE.PUP_MAGIC)
            struct.pack_into("<Q", data, 0x10, len(data) + 1)
            path.write_bytes(data)
            with self.assertRaisesRegex(ValueError, "size mismatch"):
                PUP_MODULE.Pup(path)


if __name__ == "__main__":
    unittest.main()
