#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""
Extract files from exFAT segments inside a decrypted PS5 .PUP.dec.

The firmware assets are not loose in the update - they sit in an exFAT
filesystem image, which is itself stored as a run of independently
zlib-compressed 512 KB chunks. Two consequences shaped this tool:

* Searching the update for a filename finds nothing. exFAT stores names as
  UTF-16LE, split across multiple 32-byte File Name records with a 0xC1 tag
  between fragments, so no contiguous ASCII or even UTF-16 copy of a long name
  exists anywhere on disk.
* Files are stored in cluster chains, so a file cannot be carved by locating a
  magic number and reading forward. The FAT has to be walked.

The PUP segment table and each segment's companion block table are authoritative.
Do not concatenate zlib streams found by scanning: an update contains several
unrelated payloads, compressed blocks are not densely packed, and incompressible
blocks are stored verbatim.  This tool reconstructs each selected segment in
isolation, then reads it as a filesystem.
"""

import argparse
import dataclasses
import os
import struct
import sys
import tempfile
import zlib

PUP_MAGIC = 0xEEF51454
ENTRY_SIZE = 0x20
ENTRY_TABLE_OFFSET = 0x20
FLAG_BLOCK_TABLE = 0x1
FLAG_COMPRESSED = 0x8
FLAG_BLOCKED = 0x800
BLOCK_SIZE_CANDIDATES = (0x10000, 0x20000, 0x40000, 0x80000, 0x100000)

ENTRY_FILE = 0x85
ENTRY_STREAM = 0xC0
ENTRY_NAME = 0xC1
ATTR_DIRECTORY = 0x10


@dataclasses.dataclass(frozen=True)
class PupSegment:
    index: int
    flags: int
    offset: int
    compressed_size: int
    uncompressed_size: int

    @property
    def is_block_table(self):
        return bool(self.flags & FLAG_BLOCK_TABLE)

    @property
    def is_blocked(self):
        return bool(self.flags & FLAG_BLOCKED)

    @property
    def is_compressed(self):
        return bool(self.flags & FLAG_COMPRESSED)

    @property
    def target_index(self):
        return (self.flags >> 20) & 0xFFF


class Pup:
    """Validated reader for the outer decrypted-PUP segment container."""

    def __init__(self, path):
        self.path = path
        self.size = os.path.getsize(path)
        with open(path, "rb") as fh:
            header = fh.read(ENTRY_TABLE_OFFSET)
            if len(header) != ENTRY_TABLE_OFFSET:
                raise ValueError("truncated PUP header")
            magic, = struct.unpack_from("<I", header, 0)
            if magic != PUP_MAGIC:
                raise ValueError(f"bad PUP magic 0x{magic:08X}")
            declared_size, = struct.unpack_from("<Q", header, 0x10)
            count, = struct.unpack_from("<H", header, 0x18)
            if declared_size != self.size:
                raise ValueError(
                    f"PUP size mismatch: header={declared_size:,}, file={self.size:,}")
            table_size = count * ENTRY_SIZE
            table = fh.read(table_size)
            if len(table) != table_size:
                raise ValueError("truncated PUP segment table")

        self.segments = []
        for index in range(count):
            flags, offset, compressed_size, uncompressed_size = struct.unpack_from(
                "<QQQQ", table, index * ENTRY_SIZE)
            if offset > self.size or compressed_size > self.size - offset:
                raise ValueError(f"segment {index} payload is outside the PUP")
            self.segments.append(PupSegment(
                index, flags, offset, compressed_size, uncompressed_size))

        self.block_tables = {}
        for segment in self.segments:
            if not segment.is_block_table:
                continue
            target = segment.target_index
            if target >= len(self.segments):
                raise ValueError(
                    f"block table {segment.index} targets missing segment {target}")
            if target in self.block_tables:
                raise ValueError(f"duplicate block table for segment {target}")
            self.block_tables[target] = segment

    def describe(self):
        lines = [f"{self.path}: {self.size:,} bytes, {len(self.segments)} segments"]
        for seg in self.segments:
            labels = []
            if seg.is_block_table:
                labels.append(f"table-for={seg.target_index}")
            if seg.is_blocked:
                labels.append("blocked")
            if seg.is_compressed:
                labels.append("zlib")
            label = ",".join(labels) or "stored"
            lines.append(
                f"  {seg.index:2d} flags=0x{seg.flags:X} off=0x{seg.offset:X} "
                f"disk={seg.compressed_size:>12,} out={seg.uncompressed_size:>12,} "
                f"{label}")
        return "\n".join(lines)

    def _read_payload(self, segment):
        with open(self.path, "rb") as fh:
            fh.seek(segment.offset)
            data = fh.read(segment.compressed_size)
        if len(data) != segment.compressed_size:
            raise ValueError(f"short read for segment {segment.index}")
        return data

    @staticmethod
    def _infer_block_size(uncompressed_size, count):
        if count == 1:
            return uncompressed_size
        matches = [size for size in BLOCK_SIZE_CANDIDATES
                   if (uncompressed_size + size - 1) // size == count]
        if len(matches) != 1:
            raise ValueError(
                f"cannot uniquely infer block size for {uncompressed_size:,} bytes "
                f"across {count} blocks (matches: {matches})")
        return matches[0]

    def _compressed_blocks(self, segment):
        table_segment = self.block_tables.get(segment.index)
        if table_segment is None:
            raise ValueError(f"blocked segment {segment.index} has no block table")
        table = self._read_payload(table_segment)
        if len(table) % 40:
            raise ValueError(
                f"compressed block table {table_segment.index} has invalid size "
                f"{len(table):,}")
        count = len(table) // 40
        if not count:
            raise ValueError(f"compressed block table {table_segment.index} is empty")
        block_size = self._infer_block_size(segment.uncompressed_size, count)
        index_offset = count * 32
        records = [struct.unpack_from("<II", table, index_offset + i * 8)
                   for i in range(count)]
        for i, (offset, size) in enumerate(records):
            if offset >= segment.compressed_size:
                raise ValueError(
                    f"segment {segment.index} block {i} is outside its payload")
            available = segment.compressed_size - offset
            if size > available:
                # The recovery PUP's final system_ex record includes trailing
                # padding beyond the segment's declared payload.  Clip only
                # that final record; zlib's checksum and the exact inflated
                # length still validate the actual stream.
                if i != count - 1:
                    raise ValueError(
                        f"segment {segment.index} block {i} crosses its payload")
                records[i] = (offset, available)
        return block_size, records

    @staticmethod
    def _inflate_block(raw, expected, segment_index, block_index):
        # An incompressible block is stored byte-for-byte even though the
        # containing segment carries the compression flag.
        if len(raw) == expected:
            return raw
        try:
            data = zlib.decompress(raw)
        except zlib.error as exc:
            raise ValueError(
                f"segment {segment_index} block {block_index} failed zlib: {exc}") from exc
        if len(data) != expected:
            raise ValueError(
                f"segment {segment_index} block {block_index} inflated to "
                f"{len(data):,}, expected {expected:,}")
        return data

    def first_block(self, index):
        segment = self.segments[index]
        if segment.is_block_table:
            raise ValueError(f"segment {index} is a block table")
        if segment.is_blocked and segment.is_compressed:
            block_size, records = self._compressed_blocks(segment)
            offset, size = records[0]
            with open(self.path, "rb") as fh:
                fh.seek(segment.offset + offset)
                raw = fh.read(size)
            return self._inflate_block(
                raw, min(block_size, segment.uncompressed_size), index, 0)
        raw = self._read_payload(segment)
        if segment.is_compressed:
            raw = zlib.decompress(raw)
        return raw[:min(len(raw), 1 << 20)]

    def extract_segment(self, index, out_path, progress_every=100):
        segment = self.segments[index]
        if segment.is_block_table:
            raise ValueError(f"segment {index} is a block table")

        written = 0
        with open(out_path, "wb") as out:
            if segment.is_blocked and segment.is_compressed:
                block_size, records = self._compressed_blocks(segment)
                with open(self.path, "rb") as source:
                    for block_index, (offset, size) in enumerate(records):
                        expected = min(block_size,
                                       segment.uncompressed_size - written)
                        source.seek(segment.offset + offset)
                        raw = source.read(size)
                        if len(raw) != size:
                            raise ValueError(
                                f"short read for segment {index} block {block_index}")
                        data = self._inflate_block(
                            raw, expected, index, block_index)
                        out.write(data)
                        written += len(data)
                        if progress_every and (block_index + 1) % progress_every == 0:
                            print(f"  ...{block_index + 1}/{len(records)} blocks, "
                                  f"{written / 1e6:.0f} MB", flush=True)
            else:
                raw = self._read_payload(segment)
                if segment.is_compressed:
                    raw = zlib.decompress(raw)
                if len(raw) < segment.uncompressed_size:
                    raise ValueError(
                        f"segment {index} produced {len(raw):,}, expected "
                        f"{segment.uncompressed_size:,}")
                out.write(raw[:segment.uncompressed_size])
                written = segment.uncompressed_size

        if written != segment.uncompressed_size:
            raise ValueError(
                f"segment {index} wrote {written:,}, expected "
                f"{segment.uncompressed_size:,}")
        return written

    def exfat_segments(self):
        found = []
        for segment in self.segments:
            if segment.is_block_table or segment.uncompressed_size < 512:
                continue
            first = self.first_block(segment.index)
            if first[3:11] == b"EXFAT   ":
                found.append(segment.index)
        return found


class ExFat:
    """A read-only exFAT reader over an in-file volume at a known offset."""

    def __init__(self, fh, volume_offset):
        self.fh = fh
        self.base = volume_offset
        self.size = fh.seek(0, 2)
        fh.seek(volume_offset)
        boot = fh.read(512)
        if boot[3:11] != b"EXFAT   ":
            raise ValueError("not an exFAT boot sector")
        self.fat_offset, = struct.unpack("<I", boot[80:84])
        self.fat_length, = struct.unpack("<I", boot[84:88])
        self.heap_offset, = struct.unpack("<I", boot[88:92])
        self.cluster_count, = struct.unpack("<I", boot[92:96])
        self.root_cluster, = struct.unpack("<I", boot[96:100])
        self.sector_shift = boot[108]
        self.cluster_shift = boot[109]
        self.sector = 1 << self.sector_shift
        self.cluster = self.sector << self.cluster_shift

    def describe(self):
        return (f"exFAT @0x{self.base:X}  sector={self.sector} cluster={self.cluster} "
                f"heap@{self.heap_offset} root={self.root_cluster} "
                f"clusters={self.cluster_count:,}")

    def cluster_pos(self, cluster):
        return (self.base
                + ((self.heap_offset + ((cluster - 2) << self.cluster_shift)) * self.sector))

    def fat_next(self, cluster):
        pos = self.base + (self.fat_offset * self.sector) + (cluster * 4)
        if pos < 0 or pos + 4 > self.size:
            return None
        self.fh.seek(pos)
        raw = self.fh.read(4)
        if len(raw) < 4:
            return None
        return struct.unpack("<I", raw)[0]

    def read_chain(self, first, length, contiguous):
        """Read `length` bytes starting at `first`, following the FAT unless
        the entry is flagged contiguous (NoFatChain).

        A volume recovered from an update image is frequently truncated - its
        boot sector describes more clusters than the image actually holds - so
        a read can land past EOF and return nothing. Without a short-read
        break the loop would follow the chain forever while making no progress.
        """
        out = bytearray()
        cluster = first
        guard = 0
        max_clusters = (length // self.cluster) + 2
        while len(out) < length and guard <= max_clusters:
            if cluster < 2 or cluster >= 0xFFFFFFF7:
                break
            pos = self.cluster_pos(cluster)
            if pos < 0 or pos >= self.size:
                break
            self.fh.seek(pos)
            piece = self.fh.read(min(self.cluster, length - len(out)))
            if not piece:
                break
            out += piece
            guard += 1
            if contiguous:
                cluster += 1
            else:
                nxt = self.fat_next(cluster)
                if nxt is None or nxt == cluster:
                    break
                cluster = nxt
        return bytes(out[:length])

    def walk(self, cluster, contiguous=False, path="", depth=0, seen=None):
        """Yield (path, name, length, first_cluster, contiguous) for every file."""
        if seen is None:
            seen = set()
        if depth > 12 or cluster in seen:
            return
        seen.add(cluster)

        # A directory is read a bounded number of clusters deep; the guard
        # keeps a corrupt chain from pulling in the whole volume.
        data = self.read_chain(cluster, min(self.cluster * 16, 4 << 20), contiguous)
        i = 0
        while i + 32 <= len(data):
            tag = data[i]
            if tag == 0x00:
                break
            if tag != ENTRY_FILE:
                i += 32
                continue
            secondary = data[i + 1]
            attrs, = struct.unpack("<H", data[i + 4:i + 6])
            name = ""
            first = length = None
            flags = 0
            j = i + 32
            for _ in range(secondary):
                if j + 32 > len(data):
                    break
                t = data[j]
                if t == ENTRY_STREAM:
                    flags = data[j + 1]
                    length, = struct.unpack("<Q", data[j + 8:j + 16])
                    first, = struct.unpack("<I", data[j + 20:j + 24])
                elif t == ENTRY_NAME:
                    name += data[j + 2:j + 32].decode("utf-16-le", "ignore")
                j += 32
            name = name.rstrip("\x00")
            i = j
            if not name or first is None or length is None:
                continue

            # Reject entries that cannot be real. Reassembled volumes contain
            # stretches that parse as directory entries but are not, and
            # following them costs enormous reads at random offsets.
            if first < 2 or first >= self.cluster_count + 2:
                continue
            if length > (1 << 32):
                continue
            if any(ord(c) < 0x20 for c in name):
                continue
            no_fat_chain = bool(flags & 0x02)
            full = f"{path}/{name}"
            if attrs & ATTR_DIRECTORY:
                yield from self.walk(first, no_fat_chain, full, depth + 1, seen)
            else:
                yield full, name, length, first, no_fat_chain


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pup")
    ap.add_argument("out_dir")
    ap.add_argument("--segment", type=int, action="append",
                    help="exFAT segment index to inspect (repeatable; auto-detected by default)")
    ap.add_argument("--image", help="reuse/emit one selected segment image here")
    ap.add_argument("--keep-images", action="store_true",
                    help="retain reconstructed segment images under out_dir")
    ap.add_argument("--list-segments", action="store_true",
                    help="print the validated outer PUP segment table")
    ap.add_argument("--list-only", action="store_true",
                    help="list filesystem entries without extracting them")
    ap.add_argument("--only", action="append",
                    help="extract only full paths containing this (repeatable)")
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    pup = Pup(args.pup)
    if args.list_segments:
        print(pup.describe())

    segments = args.segment or pup.exfat_segments()
    if not segments:
        raise SystemExit("no exFAT segment found")
    if args.image and len(segments) != 1:
        raise SystemExit("--image requires exactly one --segment")

    total = 0
    for segment_index in segments:
        temporary = False
        if args.image:
            image = args.image
        elif args.keep_images:
            image = os.path.join(args.out_dir, f"segment_{segment_index:03d}.img")
        else:
            fd, image = tempfile.mkstemp(
                prefix=f"pup_segment_{segment_index:03d}_", suffix=".img")
            os.close(fd)
            temporary = True

        try:
            expected = pup.segments[segment_index].uncompressed_size
            if not os.path.exists(image) or os.path.getsize(image) != expected:
                print(f"reconstructing PUP segment {segment_index} ...", flush=True)
                written = pup.extract_segment(segment_index, image)
                print(f"  {written:,} bytes -> {image}\n", flush=True)
            else:
                print(f"reusing {image} ({os.path.getsize(image):,} bytes)\n")

            with open(image, "rb") as fh:
                fs = ExFat(fh, 0)
                print(fs.describe())
                entries = list(fs.walk(fs.root_cluster))
                print(f"  {len(entries)} files")
                selected = []
                for entry in entries:
                    full = entry[0]
                    if args.only and not any(
                            key.lower() in full.lower() for key in args.only):
                        continue
                    selected.append(entry)
                for full, name, length, first, contiguous in selected:
                    print(f"  {full:76} {length:>12,} B")
                    if args.list_only or not length or length > (256 << 20):
                        continue
                    data = fs.read_chain(first, length, contiguous)
                    if len(data) != length:
                        print(f"  SHORT {full}: got {len(data):,}/{length:,}")
                        continue
                    relative = os.path.normpath(full.lstrip("/"))
                    if relative.startswith(".."):
                        raise ValueError(f"unsafe exFAT path: {full}")
                    extraction_root = (args.out_dir if len(segments) == 1 else
                                       os.path.join(args.out_dir,
                                                    f"segment_{segment_index:03d}"))
                    dest = os.path.join(extraction_root, relative)
                    os.makedirs(os.path.dirname(dest), exist_ok=True)
                    with open(dest, "wb") as out:
                        out.write(data)
                    total += 1
        finally:
            if temporary and os.path.exists(image):
                os.unlink(image)
    print(f"\nextracted {total} files to {args.out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
