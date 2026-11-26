#!/usr/bin/env python3
"""Unit and failure-gate tests for RvtIO reproducible RE1 IFC comparison."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

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
    def setUp(self) -> None:
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)

    def write_ifc(self, name: str, content: str) -> Path:
        path = self.tmp / name
        path.write_text(content, encoding="utf-8")
        return path

    def sample_metres(self) -> str:
        return SAMPLE_IFC_TEMPLATE.format(
            unit_prefix="$", storey2_z="3.66", w_dim="4.", w_thick="0.2", w_height="3.",
            wall1_x="0.", wall2_x="5.",
        )

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

    def test_step_strings_keep_records_intact_during_parsing_and_control(self) -> None:
        content = (
            self.sample_metres()
            .replace("'test.ifc'", "'DATA; in filename'")
            .replace("'Wall 1'", "'Wall ); 1'")
            .replace("'Slab 1'", "'Slab ''B'' );\n /* name */ ; (#123)'")
            .replace("#32=IFCSLAB", "/* #32=IFCFAKE(); */ #32=IFCSLAB")
        )
        source = self.write_ifc("strings.ifc", content)
        elements = ifc_compare.StepModel(source).extract_structure()["elements"]
        self.assertEqual(list(elements), ["1001", "1002"])
        self.assertEqual(elements["1001"]["name"], "Wall ); 1")
        self.assertEqual(elements["1002"]["name"], "Slab 'B' );\n /* name */ ; (#123)")
        self.assertTrue(measure_re1.run_control_check(source)["passed"])

    def test_rotated_triangular_prism_matches_baked_vertices(self) -> None:
        def prism(p2: str, p3: str) -> str:
            return (
                self.sample_metres()
                .replace("#20=IFCRECTANGLEPROFILEDEF(.AREA.,$,$,4.,0.2);", (
                    "#20=IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,#40);\n"
                    "#40=IFCPOLYLINE((#41,#42,#43,#41));\n"
                    f"#41=IFCCARTESIANPOINT((0.,0.));\n"
                    f"#42=IFCCARTESIANPOINT(({p2}));\n"
                    f"#43=IFCCARTESIANPOINT(({p3}));\n"
                    "#44=IFCDIRECTION((1.,1.,0.));"
                ))
                .replace("#31,#24,'1002'", "#31,$,'1002'")
            )

        local = prism("1.,0.", "0.,1.")
        baked = self.write_ifc("baked.ifc", prism("0.70710678118655,0.70710678118655", "-0.70710678118655,0.70710678118655"))
        mapped = local.replace("#24=IFCPRODUCTDEFINITIONSHAPE($,$,(#23));", (
            "#24=IFCPRODUCTDEFINITIONSHAPE($,$,(#45));\n"
            "#45=IFCSHAPEREPRESENTATION($,'Body','MappedRepresentation',(#46));\n"
            "#46=IFCMAPPEDITEM(#47,#48);\n"
            "#47=IFCREPRESENTATIONMAP(#4,#23);\n"
            "#48=IFCCARTESIANTRANSFORMATIONOPERATOR3D(#44,#49,#3,1.,$);\n"
            "#49=IFCDIRECTION((-1.,1.,0.));"
        ))
        for encoding, model in (
            ("placement", local.replace("#26=IFCAXIS2PLACEMENT3D(#25,$,$)", "#26=IFCAXIS2PLACEMENT3D(#25,$,#44)")),
            ("mapped", mapped),
        ):
            with self.subTest(encoding=encoding):
                candidate = self.write_ifc(f"{encoding}.ifc", model)
                box, exact = ifc_compare.StepModel(candidate).product_world_aabb_metres(28)
                self.assertTrue(exact)
                self.assertEqual(box, [-0.707107, 0., 0., 0.707107, 0.707107, 3.])
                res = ifc_compare.compare_ifc_against_reference(candidate, baked)
                self.assertEqual(res["worldBbox"]["withinToleranceCount"], 1)
                self.assertEqual(res["worldBbox"]["exceedingToleranceCount"], 0)

    def test_rotated_circle_extrusion_bounds_radius(self) -> None:
        content = self.sample_metres().replace(
            "#20=IFCRECTANGLEPROFILEDEF(.AREA.,$,$,4.,0.2);",
            "#20=IFCCIRCLEPROFILEDEF(.AREA.,$,$,2.);\n#40=IFCDIRECTION((1.,1.,0.));",
        ).replace("#26=IFCAXIS2PLACEMENT3D(#25,$,$)", "#26=IFCAXIS2PLACEMENT3D(#25,$,#40)")
        source = self.write_ifc("circle.ifc", content)
        box, exact = ifc_compare.StepModel(source).product_world_aabb_metres(28)
        self.assertTrue(exact)
        self.assertEqual(box, [-2., -2., 0., 2., 2., 3.])

    def test_fail_on_regression_checks_class_storey_and_geometry_coverage(self) -> None:
        original = self.sample_metres()
        unsupported = original.replace(
            "#22=IFCEXTRUDEDAREASOLID(#20,#4,#21,3.);", "#22=IFCBLOCK(#4,4.,0.2,3.);",
        )
        no_geometry = original.replace(",#24,'100", ",$,'100")
        cli = self.tmp / "dummy.dll"
        cli.touch()
        for name, ref, base, head, expected in (
            ("class", original, original, original.replace("IFCWALLSTANDARDCASE(", "IFCWALL("), "exact IFC class"),
            ("storey", original, original, original.replace(
                "(#28,#32),#14);", "(#32),#14);\n#40=IFCRELCONTAINEDINSPATIALSTRUCTURE('Rel2',$,$,$,(#28),#15);",
            ), "same-storey"),
            ("offsetting-class-change", original, original.replace("IFCSLAB(", "IFCWALL("),
             original.replace("IFCWALLSTANDARDCASE(", "IFCWALL("), "exact IFC class matches for tags ['1001']"),
            ("lost-bounds", original, original, unsupported, "regressed bbox tags ['1001', '1002']"),
            ("unsupported", unsupported, unsupported, unsupported, "unverified bbox tags ['1001', '1002']"),
            ("geometryless", no_geometry, no_geometry, no_geometry, None),
        ):
            with self.subTest(case=name):
                reference = self.write_ifc("reference.ifc", ref)
                out = self.tmp / name

                def export(_cli, _rvt, out_ifc, _report, _flags, _model_id, side):
                    out_ifc.write_text(base if side == "base" else head, encoding="utf-8")
                    return {"ifcBytes": out_ifc.stat().st_size, "ifcSha256": measure_re1.sha256_file(out_ifc)}

                argv = [
                    "--base-cli", str(cli), "--head-cli", str(cli), "--models", "re1-architecture",
                    "--out-dir", str(out), "--no-control-check", "--fail-on-regression",
                ]
                with (
                    patch.object(measure_re1, "download_and_verify", return_value=reference),
                    patch.object(measure_re1, "export_model", side_effect=export),
                    patch("builtins.print"),
                ):
                    if expected:
                        with self.assertRaises(measure_re1.MeasureFailure):
                            measure_re1.main(argv)
                    else:
                        self.assertEqual(measure_re1.main(argv), 0)
                summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
                if expected:
                    self.assertEqual(len(summary["regressions"]), 1)
                    self.assertIn(expected, summary["regressions"][0])
                    self.assertIn(expected, (out / "summary.md").read_text(encoding="utf-8"))
                else:
                    self.assertEqual(summary["regressions"], [])
                if name in ("unsupported", "geometryless"):
                    bbox = summary["models"][0]["head"]["comparison"]["worldBbox"]
                    self.assertEqual(bbox["pairedWithBothBoxes"], 0)
                    self.assertEqual(bbox["missingReferenceBoxCount"], 2)
                    self.assertEqual(bbox["missingCandidateBoxCount"], 2)
                    self.assertEqual(bbox["missingBothBoxesCount"], 2)

    def test_rejects_invalid_bbox_tolerance(self) -> None:
        for tolerance in ("nan", "inf", "-0.01"):
            with self.subTest(tolerance=tolerance):
                with self.assertRaisesRegex(measure_re1.MeasureFailure, "--bbox-tol must be finite and non-negative"):
                    measure_re1.main([f"--bbox-tol={tolerance}"])

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
