#!/usr/bin/env python3
"""Unit and failure-gate tests for RvtIO reproducible RE1 IFC comparison."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

MEASURE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(MEASURE_DIR))

import ifc_compare  # noqa: E402
import measure_re1  # noqa: E402


SAMPLE_IFC_TEMPLATE = """ISO-10303-21;
HEADER;
FILE_DESCRIPTION(('ViewDefinition [CoordinationView_V2.0]'),'2;1');
FILE_NAME('test.ifc','2026-01-01T00:00:00',(''),(''),'','Autodesk Revit 26.2.0.20','');
FILE_SCHEMA(('IFC2X3'));
ENDSEC;
DATA;
#1=IFCSIUNIT(*,.LENGTHUNIT.,{unit_prefix},.METRE.);
#2=IFCUNITASSIGNMENT((#1));
#3=IFCCARTESIANPOINT((0.,0.,0.));
#4=IFCAXIS2PLACEMENT3D(#3,$,$);
#5=IFCLOCALPLACEMENT($,#4);
#6=IFCPROJECT('0ProjGuid0000000000001',$,'Proj',$,$,$,$,$,#2);
#7=IFCSITE('0SiteGuid0000000000001',$,'Site',$,$,#5,$,$,.ELEMENT.,$,$,$,$,$);
#8=IFCLOCALPLACEMENT(#5,#4);
#9=IFCBUILDING('0BldgGuid0000000000001',$,'Bldg',$,$,#8,$,$,.ELEMENT.,$,$,$);
#10=IFCCARTESIANPOINT((0.,0.,{storey2_z}));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCLOCALPLACEMENT(#8,#4);
#13=IFCLOCALPLACEMENT(#8,#11);
#14=IFCBUILDINGSTOREY('0StorGuid0000000000001',$,'Level 1',$,$,#12,$,'Level 1',.ELEMENT.,0.);
#15=IFCBUILDINGSTOREY('0StorGuid0000000000002',$,'Level 2',$,$,#13,$,'Level 2',.ELEMENT.,{storey2_z});
#20=IFCRECTANGLEPROFILEDEF(.AREA.,$,$,{w_dim},{w_thick});
#21=IFCDIRECTION((0.,0.,1.));
#22=IFCEXTRUDEDAREASOLID(#20,#4,#21,{w_height});
#23=IFCSHAPEREPRESENTATION($,'Body','SweptSolid',(#22));
#24=IFCPRODUCTDEFINITIONSHAPE($,$,(#23));
#25=IFCCARTESIANPOINT(({wall1_x},0.,0.));
#26=IFCAXIS2PLACEMENT3D(#25,$,$);
#27=IFCLOCALPLACEMENT(#12,#26);
#28=IFCWALLSTANDARDCASE('0WallGuid0000000000001',$,'Wall 1',$,$,#27,#24,'1001');
#29=IFCCARTESIANPOINT(({wall2_x},0.,0.));
#30=IFCAXIS2PLACEMENT3D(#29,$,$);
#31=IFCLOCALPLACEMENT(#12,#30);
#32=IFCSLAB('0SlabGuid0000000000002',$,'Slab 1',$,$,#31,#24,'1002');
#33=IFCRELCONTAINEDINSPATIALSTRUCTURE('0RelGuid00000000000001',$,$,$,(#28,#32),#14);
ENDSEC;
END-ISO-10303-21;
"""


class MeasureRe1Tests(unittest.TestCase):
    def test_manifest_has_pinned_re1_arch_and_mech(self) -> None:
        manifest = json.loads((MEASURE_DIR / "re1-manifest.json").read_text(encoding="utf-8"))
        ids = [m["id"] for m in manifest["models"]]
        self.assertEqual(ids, ["re1-architecture", "re1-mechanical"])
        for m in manifest["models"]:
            self.assertEqual(len(m["rvt"]["sha256"]), 64)
            self.assertEqual(len(m["referenceIfc"]["sha256"]), 64)
            self.assertGreater(m["rvt"]["bytes"], 0)
            self.assertGreater(m["referenceIfc"]["bytes"], 0)
            self.assertIn("shared coordinates", m["referenceIfc"]["exportSettingsNote"])

    def test_unit_normalization_mm_vs_metres_matches_world_bbox(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_str:
            tmp = Path(tmp_str)
            ref_ifc = tmp / "ref_mm.ifc"
            cand_ifc = tmp / "cand_m.ifc"
            ref_ifc.write_text(
                SAMPLE_IFC_TEMPLATE.format(
                    unit_prefix=".MILLI.", storey2_z="3660.", w_dim="4000.", w_thick="200.", w_height="3000.",
                    wall1_x="0.", wall2_x="5000.",
                ),
                encoding="utf-8",
            )
            cand_ifc.write_text(
                SAMPLE_IFC_TEMPLATE.format(
                    unit_prefix="$", storey2_z="3.66", w_dim="4.0", w_thick="0.2", w_height="3.0",
                    wall1_x="0.", wall2_x="5.0",
                ),
                encoding="utf-8",
            )
            res = ifc_compare.compare_ifc_against_reference(cand_ifc, ref_ifc, bbox_tol_m=0.01)
            self.assertEqual(res["identifiers"]["matchedCount"], 2)
            self.assertEqual(res["identifiers"]["exactGlobalIdCount"], 2)
            self.assertEqual(res["classes"]["exactClassMatchCount"], 2)
            self.assertEqual(res["storeys"]["elementAssignments"]["sameStorey"], 2)
            self.assertTrue(res["storeys"]["allElevationsWithinTolerance"])
            self.assertEqual(res["worldBbox"]["withinToleranceCount"], 2)
            self.assertEqual(res["worldBbox"]["exceedingToleranceCount"], 0)
            self.assertAlmostEqual(res["worldBbox"]["modelBoxMaxCornerDeltaMeters"], 0.0, places=6)

    def test_control_check_detects_removed_and_shifted_element(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_str:
            tmp = Path(tmp_str)
            ref_ifc = tmp / "ref.ifc"
            ref_ifc.write_text(
                SAMPLE_IFC_TEMPLATE.format(
                    unit_prefix=".MILLI.", storey2_z="3660.", w_dim="4000.", w_thick="200.", w_height="3000.",
                    wall1_x="0.", wall2_x="5000.",
                ),
                encoding="utf-8",
            )
            ctrl = measure_re1.run_control_check(ref_ifc, bbox_tol_m=0.01)
            self.assertTrue(ctrl["passed"])
            self.assertTrue(ctrl["corruptHashRejected"])
            self.assertTrue(ctrl["partialExportRejected"])
            self.assertGreaterEqual(ctrl["observedShiftMeters"], 0.45)

    def test_hash_download_and_partial_export_failures_raise(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_str:
            tmp = Path(tmp_str)
            # Hash mismatch fails before export
            bad = tmp / "bad.rvt"
            bad.write_bytes(b"wrong-content")
            with self.assertRaises(measure_re1.MeasureFailure):
                measure_re1.verify_file_integrity(bad, "a" * 64, len(b"wrong-content"), "bad")

            # Missing offline artifact fails
            with self.assertRaises(measure_re1.MeasureFailure):
                measure_re1.download_and_verify(
                    {
                        "fileName": "missing.rvt",
                        "url": "https://127.0.0.1:1/unreachable.rvt",
                        "sha256": hashlib.sha256(b"x").hexdigest(),
                        "bytes": 1,
                    },
                    tmp,
                    attempts=1,
                    offline=True,
                )

            # Partial report fails validation even with exit code 0
            rep = tmp / "partial.report.json"
            rep.write_text(json.dumps({"status": "partial", "exitCode": 0, "issues": [{"code": "failed-members"}]}), encoding="utf-8")
            with self.assertRaises(measure_re1.MeasureFailure):
                measure_re1.validate_export_report(0, rep, "re1-architecture", "head")

    def test_diff_commit_reports_captures_deltas(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_str:
            tmp = Path(tmp_str)
            ref = tmp / "ref.ifc"
            base = tmp / "base.ifc"
            head = tmp / "head.ifc"
            content = SAMPLE_IFC_TEMPLATE.format(
                unit_prefix=".MILLI.", storey2_z="3660.", w_dim="4000.", w_thick="200.", w_height="3000.",
                wall1_x="0.", wall2_x="5000.",
            )
            ref.write_text(content, encoding="utf-8")
            head.write_text(content, encoding="utf-8")
            # Base had wall 1 shifted by 500 mm
            base.write_text(
                SAMPLE_IFC_TEMPLATE.format(
                    unit_prefix=".MILLI.", storey2_z="3660.", w_dim="4000.", w_thick="200.", w_height="3000.",
                    wall1_x="500.", wall2_x="5000.",
                ),
                encoding="utf-8",
            )
            base_cmp = ifc_compare.compare_ifc_against_reference(base, ref, bbox_tol_m=0.01)
            head_cmp = ifc_compare.compare_ifc_against_reference(head, ref, bbox_tol_m=0.01)
            delta = ifc_compare.diff_commit_reports(base_cmp, head_cmp)
            self.assertEqual(delta["bboxWithinToleranceDelta"], 1)
            self.assertEqual(delta["newlyAlignedBboxTags"], ["1001"])
            self.assertEqual(delta["regressedBboxTags"], [])

            summary = {
                "base": {"ref": "base", "commit": "0000"},
                "head": {"ref": "head", "commit": "1111"},
                "rules": {
                    "exportFlags": ["--strict"],
                    "coordinateSystem": "metres",
                    "tolerances": {"worldBboxMeters": 0.01, "storeyElevationMeters": 0.005},
                },
                "models": [
                    {
                        "id": "re1-architecture",
                        "name": "RE1 Architecture",
                        "license": "MIT",
                        "sources": {
                            "rvt": {"revitRelease": 2025, "sha256": "a" * 64},
                            "referenceIfc": {"exportSettingsNote": "IFC2X3", "sha256": "b" * 64},
                        },
                        "base": {"export": {"ifcBytes": 100, "ifcSha256": "c" * 64}, "comparison": base_cmp},
                        "head": {"export": {"ifcBytes": 100, "ifcSha256": "d" * 64}, "comparison": head_cmp},
                        "baseVsHead": delta,
                    }
                ],
                "controlCheck": {
                    "passed": True,
                    "removedTagDetected": "1001",
                    "shiftedTagDetected": "1002",
                    "observedShiftMeters": 0.5,
                    "corruptHashRejected": True,
                    "partialExportRejected": True,
                },
            }
            md = measure_re1.render_markdown_summary(summary)
            self.assertIn("RE1 Reproducible IFC Comparison", md)
            self.assertIn("re1-architecture", md)


if __name__ == "__main__":
    unittest.main()
