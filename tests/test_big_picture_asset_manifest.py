# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later

import hashlib
import json
from pathlib import Path
import unittest


class BigPictureAssetManifestTests(unittest.TestCase):
    def test_recovered_300_assets_match_their_manifest(self):
        repository = Path(__file__).parents[1]
        manifest_path = repository / "assets" / "big-picture" / "3.00" / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

        self.assertEqual("3.00", manifest["firmware_version"])
        self.assertEqual("21.01", manifest["update_identity"]["prefix_version"])
        self.assertEqual(24, len(manifest["assets"]))
        self.assertEqual(
            10,
            sum("/control-center-icons/" in entry["path"] for entry in manifest["assets"]),
        )

        for entry in manifest["assets"]:
            path = repository / entry["path"]
            self.assertTrue(path.is_file(), entry["path"])
            data = path.read_bytes()
            self.assertEqual(entry["size"], len(data), entry["path"])
            self.assertEqual(
                entry["sha256"], hashlib.sha256(data).hexdigest(), entry["path"])

    def test_large_reference_only_containers_were_not_packaged(self):
        assets = Path(__file__).parents[1] / "assets" / "big-picture" / "3.00"
        self.assertFalse(any(assets.rglob("Sce.PlayStation.PUI_UI3.rco")))
        self.assertFalse(any(assets.rglob("initial_boot_movie.mp4")))


if __name__ == "__main__":
    unittest.main()
