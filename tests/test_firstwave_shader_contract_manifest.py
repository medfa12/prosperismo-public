import hashlib
import json
from pathlib import Path
import unittest


REPO = Path(__file__).resolve().parents[1]
MANIFEST = REPO / "docs" / "sony-shell" / "firstwave-12.40-shader-contracts.json"


class FirstWaveShaderContractManifestTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.raw = MANIFEST.read_bytes()
        cls.manifest = json.loads(cls.raw)
        cls.by_name = {stage["name"]: stage for stage in cls.manifest["stages"]}

    def test_schema_and_stage_set_are_stable(self) -> None:
        self.assertEqual(
            self.manifest["schema"], "prosperismo.firstwave.shader-contracts.v1"
        )
        self.assertEqual(
            set(self.by_name),
            {
                "fw_blurh_p",
                "fw_blurv_p",
                "fw_blur_vv",
                "fw_flow_dv",
                "fw_flow_h",
                "fw_flow_vl",
                "fw_oit_p",
                "fw_comp_oit_p",
                "fw_fxaa_p",
                "fw_background_p",
            },
        )

    def test_entry_ranges_are_aligned_and_do_not_overlap(self) -> None:
        ranges = sorted(
            (
                int(stage["file_offset"], 0),
                int(stage["file_offset"], 0) + int(stage["code_length"], 0),
                stage["name"],
            )
            for stage in self.manifest["stages"]
        )
        for start, end, name in ranges:
            self.assertEqual(start % 4, 0, name)
            self.assertEqual(end % 4, 0, name)
            self.assertGreater(end, start, name)
        for left, right in zip(ranges, ranges[1:]):
            self.assertLessEqual(left[1], right[0], (left[2], right[2]))

    def test_every_slice_has_a_pinned_sha256_and_decode_contract(self) -> None:
        for stage in self.manifest["stages"]:
            digest = stage["sha256"]
            self.assertEqual(len(digest), hashlib.sha256().digest_size * 2)
            int(digest, 16)
            self.assertGreater(stage["instruction_count"], 0)
            self.assertEqual(stage["first_opcode"], "s_inst_prefetch")
            self.assertIn("resource_opcode_counts", stage)
            self.assertIn("scalar_loads", stage)
            self.assertIn("exports", stage)

    def test_flow_local_return_is_not_misbounded_as_oit(self) -> None:
        flow = self.by_name["fw_flow_vl"]
        oit = self.by_name["fw_oit_p"]
        self.assertEqual(flow["code_length"], "0x72c")
        self.assertEqual(flow["last_opcode"], "s_swappc_b64")
        self.assertEqual(flow["terminator_operands"], ["null", "s[6:7]"])
        self.assertLess(
            int(flow["file_offset"], 0) + int(flow["code_length"], 0),
            int(oit["file_offset"], 0),
        )

    def test_pipeline_and_binding_discriminators(self) -> None:
        self.assertEqual(
            self.manifest["pipeline_order"],
            [
                "fw_flow_vl",
                "fw_flow_h",
                "fw_flow_dv",
                "fw_oit_p",
                "fw_comp_oit_p",
                "fw_blurh_p",
                "fw_blurv_p",
                "fw_fxaa_p",
            ],
        )
        self.assertEqual(
            self.by_name["fw_flow_dv"]["resource_opcode_counts"],
            {"buffer_load_dwordx4": 16},
        )
        self.assertEqual(
            self.by_name["fw_oit_p"]["resource_opcode_counts"],
            {
                "buffer_atomic_add": 1,
                "buffer_load_dword": 4,
                "buffer_store_dword": 4,
            },
        )
        self.assertEqual(
            self.by_name["fw_fxaa_p"]["resource_opcode_counts"]["image_sample_lz"],
            24,
        )
        self.assertEqual(
            self.by_name["fw_background_p"]["resource_opcode_counts"], {}
        )

    def test_shared_constant_offsets_are_exact(self) -> None:
        members = self.manifest["constant_buffer_layout"]["members"]
        self.assertEqual(members["0x180"], "opacity")
        self.assertEqual(members["0x184"], "time")
        self.assertEqual(members["0x188"], "waveOpacity")
        self.assertEqual(members["0x18c"], "oitSliceOffset")
        self.assertEqual(members["0x190"], "screenDim.x")
        self.assertEqual(members["0x194"], "screenDim.y")


if __name__ == "__main__":
    unittest.main()
