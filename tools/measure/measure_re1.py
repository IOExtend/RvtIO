#!/usr/bin/env python3
"""Reproducible RE1 IFC benchmark and regression comparison for two RvtIO revisions.

Fetches pinned RE1 models and Autodesk Revit reference IFCs by URL, verifies byte
sizes and SHA-256 checksums before export, exports with `--strict --report` for
both RvtIO commits, compares both outputs against the same Revit reference IFC,
and runs a controlled perturbation + failure-gate verification.

Examples:
  python tools/measure/measure_re1.py --base origin/main --head HEAD
  python tools/measure/measure_re1.py --base-cli path/base/RvtIO.Cli.dll --head-cli path/head/RvtIO.Cli.dll
"""

from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from typing import Any

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
sys.path.insert(0, str(SCRIPT_DIR))

import ifc_compare  # noqa: E402


class MeasureFailure(RuntimeError):
    """Raised when a required download, hash, export, or control gate fails."""


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def verify_file_integrity(path: Path, expected_sha256: str, expected_bytes: int, label: str) -> None:
    if not path.is_file():
        raise MeasureFailure(f"Missing artifact {label}: {path}")
    actual_bytes = path.stat().st_size
    if actual_bytes != expected_bytes:
        raise MeasureFailure(
            f"Size mismatch for {label} ({path.name}): got {actual_bytes} bytes, expected {expected_bytes}"
        )
    actual_sha = sha256_file(path)
    if actual_sha.lower() != expected_sha256.lower():
        raise MeasureFailure(
            f"SHA-256 mismatch for {label} ({path.name}): got {actual_sha}, expected {expected_sha256}"
        )


def download_and_verify(
    artifact: dict[str, Any],
    cache_dir: Path,
    attempts: int = 4,
    offline: bool = False,
) -> Path:
    cache_dir.mkdir(parents=True, exist_ok=True)
    name = artifact["fileName"]
    path = cache_dir / name
    expected_sha = artifact["sha256"]
    expected_bytes = int(artifact["bytes"])
    url = artifact["url"]

    if path.exists():
        try:
            verify_file_integrity(path, expected_sha, expected_bytes, name)
            print(f"  [verified] {name} ({expected_bytes} bytes, sha256 {expected_sha[:12]}...)", flush=True)
            return path
        except MeasureFailure as err:
            if offline:
                raise
            print(f"  [re-fetch] cached {name} failed verification: {err}", flush=True)
            path.unlink(missing_ok=True)

    if offline:
        raise MeasureFailure(f"Offline mode enabled and verified artifact missing: {path}")

    tmp = cache_dir / f".{name}.{os.getpid()}.tmp"
    last_error: str = "unknown error"
    for attempt in range(1, attempts + 1):
        tmp.unlink(missing_ok=True)
        print(f"  [fetch]    {name} <- {url} (attempt {attempt}/{attempts})", flush=True)
        req = urllib.request.Request(url, headers={"User-Agent": "RvtIO-Measure/1.0"})
        try:
            with urllib.request.urlopen(req, timeout=60) as resp, tmp.open("wb") as out:
                while True:
                    chunk = resp.read(1 << 20)
                    if not chunk:
                        break
                    out.write(chunk)
            verify_file_integrity(tmp, expected_sha, expected_bytes, name)
            shutil.move(str(tmp), str(path))
            print(f"  [verified] {name} ({expected_bytes} bytes, sha256 {expected_sha[:12]}...)", flush=True)
            return path
        except (OSError, http.client.HTTPException, urllib.error.URLError, MeasureFailure) as err:
            last_error = str(err)
            tmp.unlink(missing_ok=True)
            print(f"  [retry]    {name}: {last_error}", flush=True)
            if attempt < attempts:
                time.sleep(min(2 * attempt, 10))

    raise MeasureFailure(f"Failed to download and verify {name} from {url}: {last_error}")


def validate_export_report(exit_code: int, report_path: Path, model_id: str, side: str) -> dict[str, Any]:
    if not report_path.is_file():
        raise MeasureFailure(f"[{side}/{model_id}] Export did not produce report JSON: {report_path} (exit {exit_code})")
    report = json.loads(report_path.read_text(encoding="utf-8"))
    status = report.get("status")
    rep_exit = report.get("exitCode")
    issues = report.get("issues") or []
    limits_hit = (report.get("limits") or {}).get("hit") or []
    if exit_code != 0 or rep_exit != 0 or status != "complete" or issues or limits_hit:
        raise MeasureFailure(
            f"[{side}/{model_id}] Incomplete or failed export: exit={exit_code}, reportExit={rep_exit}, "
            f"status={status!r}, issues={len(issues)}, limitsHit={len(limits_hit)}"
        )
    return report


def export_model(
    cli_dll: Path,
    rvt_path: Path,
    out_ifc: Path,
    report_path: Path,
    export_flags: list[str],
    model_id: str,
    side: str,
) -> dict[str, Any]:
    out_ifc.parent.mkdir(parents=True, exist_ok=True)
    cmd = ["dotnet", str(cli_dll), str(rvt_path), str(out_ifc), *export_flags, "--report", str(report_path)]
    start = time.perf_counter()
    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    elapsed = round(time.perf_counter() - start, 3)
    if proc.returncode != 0:
        tail = "\n".join(proc.stderr.splitlines()[-15:])
        raise MeasureFailure(f"[{side}/{model_id}] CLI exited with {proc.returncode}:\n{tail}")
    if not out_ifc.is_file():
        raise MeasureFailure(f"[{side}/{model_id}] Missing exported IFC file: {out_ifc}")
    report = validate_export_report(proc.returncode, report_path, model_id, side)
    return {
        "elapsedSeconds": elapsed,
        "ifcBytes": out_ifc.stat().st_size,
        "ifcSha256": sha256_file(out_ifc),
        "report": {
            "status": report.get("status"),
            "exitCode": report.get("exitCode"),
            "considered": (report.get("export") or {}).get("considered"),
            "exported": (report.get("export") or {}).get("exported"),
            "skipped": (report.get("export") or {}).get("skipped"),
            "geometry": (report.get("export") or {}).get("geometry"),
        },
    }


def resolve_git_sha(repo: Path, ref: str) -> str:
    proc = subprocess.run(
        ["git", "-C", str(repo), "rev-parse", "--verify", "--end-of-options", f"{ref}^{{commit}}"],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    if proc.returncode != 0:
        raise MeasureFailure(f"Cannot resolve git ref {ref!r}: {proc.stderr.strip()}")
    return proc.stdout.strip()


def build_cli_for_ref(repo: Path, ref: str, build_root: Path, side: str) -> tuple[str, Path]:
    sha = resolve_git_sha(repo, ref)
    wt_dir = build_root / f"wt-{side}-{sha[:10]}"
    out_bin = build_root / f"bin-{side}-{sha[:10]}"
    if (out_bin / "RvtIO.Cli.dll").is_file():
        return sha, out_bin / "RvtIO.Cli.dll"

    print(f"  [build:{side}] checking out {ref} ({sha[:10]})...", flush=True)
    subprocess.run(
        ["git", "-C", str(repo), "worktree", "add", "--detach", str(wt_dir), sha],
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
    )
    try:
        cli_proj = wt_dir / "src" / "RvtIO.Cli" / "RvtIO.Cli.csproj"
        subprocess.run(
            ["dotnet", "restore", str(cli_proj), "-v", "minimal"],
            check=True,
            cwd=str(wt_dir),
        )
        subprocess.run(
            ["dotnet", "build", str(cli_proj), "-c", "Release", "-f", "net10.0", "--no-restore", "-o", str(out_bin)],
            check=True,
            cwd=str(wt_dir),
        )
    except subprocess.CalledProcessError as err:
        raise MeasureFailure(f"[{side}] Build failed for {ref} ({sha[:10]}): {err}") from err
    finally:
        subprocess.run(
            ["git", "-C", str(repo), "worktree", "remove", "--force", str(wt_dir)],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    dll = out_bin / "RvtIO.Cli.dll"
    if not dll.is_file():
        raise MeasureFailure(f"[{side}] Expected binary not found after build: {dll}")
    return sha, dll


def _mutate_remove_and_shift_ifc(source_ifc: Path, mutated_ifc: Path, shift_mm: float = 500.0) -> tuple[str, str]:
    """Create a controlled mutated copy of source_ifc with 1 element removed and 1 shifted."""
    model = ifc_compare.StepModel(source_ifc)
    struct = model.extract_structure()
    elems = [e for e in struct["elements"].values() if e["worldBboxMeters"] is not None]
    if len(elems) < 2:
        raise MeasureFailure("Control check requires at least 2 bounded tagged elements")

    removed, shifted = elems[:2]
    shift_eid = shifted["entityId"]
    shift_args = list(model.args(shift_eid))
    parent_tok = shift_args[5]

    shift_native = (shift_mm / 1000.0) / model.length_scale_to_metres
    max_id = max(model.ent.keys())
    pt_id = max_id + 1
    ax_id = max_id + 2
    pl_id = max_id + 3
    shift_args[5] = f"#{pl_id}"
    injected = (
        f"\n#{pt_id}=IFCCARTESIANPOINT(({shift_native:.6f},0.,0.));\n"
        f"#{ax_id}=IFCAXIS2PLACEMENT3D(#{pt_id},$,$);\n"
        f"#{pl_id}=IFCLOCALPLACEMENT({parent_tok},#{ax_id});\n"
    )
    edits = [
        (*model.record_spans[removed["entityId"]], ""),
        (*model.record_spans[shift_eid], f"\n#{shift_eid}={shifted['class']}({','.join(shift_args)});"),
        (model.data_end, model.data_end, injected),
    ]
    text = model.text
    for start, end, replacement in sorted(edits, reverse=True):
        text = text[:start] + replacement + text[end:]

    mutated_ifc.write_text(text, encoding="utf-8")
    return removed["tag"], shifted["tag"]


def run_control_check(valid_ifc: Path, bbox_tol_m: float = 0.01) -> dict[str, Any]:
    """Run controlled perturbation and negative gate checks, failing loudly on any miss."""
    with tempfile.TemporaryDirectory() as tmp_str:
        tmp = Path(tmp_str)
        mutated = tmp / "mutated-control.ifc"
        removed_tag, shifted_tag = _mutate_remove_and_shift_ifc(valid_ifc, mutated, shift_mm=500.0)
        cmp_res = ifc_compare.compare_ifc_against_reference(mutated, valid_ifc, bbox_tol_m=bbox_tol_m)

        missing_tags = {x["tag"] for x in cmp_res["identifiers"]["missingIds"]}
        shifted_map = {x["tag"]: x for x in cmp_res["worldBbox"]["shiftedElements"]}
        if removed_tag not in missing_tags:
            raise MeasureFailure(f"Control check failed: removed Tag {removed_tag} not reported in missingIds")
        if shifted_tag not in shifted_map:
            raise MeasureFailure(f"Control check failed: shifted Tag {shifted_tag} not reported in worldBbox.shiftedElements")
        shift_dist = shifted_map[shifted_tag].get("centerShiftMeters", 0.0)
        if shift_dist < 0.45:
            raise MeasureFailure(f"Control check failed: expected ~0.5m shift on Tag {shifted_tag}, got {shift_dist}")

        # Negative gate 1: corrupted hash must fail verification
        bad_file = tmp / "bad-hash.bin"
        bad_file.write_bytes(b"corrupt")
        hash_rejected = False
        try:
            verify_file_integrity(bad_file, "0" * 64, bad_file.stat().st_size, "control-bad-hash")
        except MeasureFailure:
            hash_rejected = True
        if not hash_rejected:
            raise MeasureFailure("Control check failed: hash mismatch was not rejected")

        # Negative gate 2: partial export report must fail validation
        partial_report = tmp / "partial.report.json"
        partial_report.write_text(
            json.dumps({"schema": "rvtio-report/1", "status": "partial", "exitCode": 0, "issues": [{"code": "failed-members"}], "limits": {"hit": []}}),
            encoding="utf-8",
        )
        partial_rejected = False
        try:
            validate_export_report(0, partial_report, "control", "control")
        except MeasureFailure:
            partial_rejected = True
        if not partial_rejected:
            raise MeasureFailure("Control check failed: partial export report was not rejected")

        return {
            "passed": True,
            "removedTagDetected": removed_tag,
            "shiftedTagDetected": shifted_tag,
            "observedShiftMeters": shift_dist,
            "corruptHashRejected": True,
            "partialExportRejected": True,
        }


def render_markdown_summary(summary: dict[str, Any]) -> str:
    lines = [
        "# RE1 Reproducible IFC Comparison",
        "",
        f"- **Base revision**: `{summary['base']['ref']}` (`{summary['base']['commit']}`)",
        f"- **Head revision**: `{summary['head']['ref']}` (`{summary['head']['commit']}`)",
        f"- **Export flags**: `{ ' '.join(summary['rules']['exportFlags']) }`",
        f"- **Coordinate normalization**: {summary['rules']['coordinateSystem']}",
        f"- **Tolerances**: world bbox `<= {summary['rules']['tolerances']['worldBboxMeters']} m`, storey elevation `<= {summary['rules']['tolerances']['storeyElevationMeters']} m`",
        "",
        "## Model Comparison Against Revit Reference",
        "",
        "| Model | Side | Tagged (Match/Ref/Added) | GlobalId == Revit | Exact IFC Class | Storeys / Assigned Same | World BBox <= Tol | Max Corner Delta (m) | IFC Size |",
        "| --- | --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    for m in summary["models"]:
        for side in ("base", "head"):
            comp = m[side]["comparison"]
            ids = comp["identifiers"]
            cls = comp["classes"]
            st = comp["storeys"]
            bb = comp["worldBbox"]
            assign = st["elementAssignments"]
            tag_str = f"{ids['matchedCount']}/{ids['referenceTaggedCount']} (+{ids['addedCount']}, -{ids['missingCount']})"
            storey_str = f"{len(st['matchedStoreys'])}/{st['referenceCount']} ({assign['sameStorey']}/{ids['matchedCount']} same)"
            box_str = f"{bb['withinToleranceCount']}/{bb['pairedWithBothBoxes']} ({bb['approximatePairCount']} estimated)"
            delta_str = str(bb.get("modelBoxMaxCornerDeltaMeters"))
            lines.append(
                f"| `{m['id']}` | `{side}` | {tag_str} | {ids['exactGlobalIdCount']}/{ids['matchedCount']} | "
                f"{cls['exactClassMatchCount']}/{ids['matchedCount']} | {storey_str} | {box_str} | {delta_str} | "
                f"{m[side]['export']['ifcBytes']} B |"
            )

    lines.extend(["", "## Commit-to-Commit Deltas (head vs base against same reference)", ""])
    for m in summary["models"]:
        d = m["baseVsHead"]
        same_bytes = m["base"]["export"]["ifcSha256"] == m["head"]["export"]["ifcSha256"]
        lines.append(
            f"- **`{m['id']}`**: identical IFC = `{same_bytes}`, matched delta = `{d['matchedCountDelta']:+d}`, "
            f"GlobalId delta = `{d['exactGlobalIdDelta']:+d}`, class delta = `{d['exactClassMatchDelta']:+d}`, "
            f"storey delta = `{d['sameStoreyDelta']:+d}`, world-bbox within tol delta = `{d['bboxWithinToleranceDelta']:+d}`"
        )

    if summary.get("regressions"):
        lines.extend(["", "## Regressions", ""])
        lines.extend(f"- {regression}" for regression in summary["regressions"])

    lines.extend(["", "## Pinned Sources & Verification", ""])
    for m in summary["models"]:
        src = m["sources"]
        lic = m.get("license", "MIT")
        lines.append(
            f"- **`{m['id']}`** ({m['name']}, Revit {src['rvt']['revitRelease']}, {lic}): "
            f"RVT sha256 `{src['rvt']['sha256']}`, Revit IFC `{src['referenceIfc']['exportSettingsNote']}`, "
            f"IFC sha256 `{src['referenceIfc']['sha256']}`"
        )

    if summary.get("controlCheck"):
        cc = summary["controlCheck"]
        lines.extend([
            "",
            "## Control Perturbation & Failure Gate Check",
            "",
            f"- Passed: `{cc['passed']}` — detected removed Tag `{cc['removedTagDetected']}`, "
            f"detected shifted Tag `{cc['shiftedTagDetected']}` (`{cc['observedShiftMeters']} m`), "
            f"rejected corrupt SHA-256 (`{cc['corruptHashRejected']}`), rejected partial export (`{cc['partialExportRejected']}`).",
        ])
    lines.append("")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--manifest", type=Path, default=SCRIPT_DIR / "re1-manifest.json")
    ap.add_argument("--base", default="origin/main", help="Base git ref/commit (ignored when --base-cli is passed)")
    ap.add_argument("--head", default="HEAD", help="Head git ref/commit (ignored when --head-cli is passed)")
    ap.add_argument("--base-cli", type=Path, help="Prebuilt RvtIO.Cli.dll for the base revision")
    ap.add_argument("--head-cli", type=Path, help="Prebuilt RvtIO.Cli.dll for the head revision")
    ap.add_argument("--models", help="Comma-separated model IDs from manifest (default: all)")
    ap.add_argument("--cache-dir", type=Path, default=REPO_ROOT / "_measure_cache" / "re1")
    ap.add_argument("--out-dir", type=Path, default=REPO_ROOT / "_measure_out")
    ap.add_argument("--bbox-tol", type=float, help="Override world bbox tolerance in metres")
    ap.add_argument("--elev-tol", type=float, help="Override storey elevation tolerance in metres")
    ap.add_argument("--offline", action="store_true", help="Require verified files in cache-dir without network download")
    ap.add_argument("--no-control-check", action="store_true", help="Skip synthetic removal/shift & negative gate control check")
    ap.add_argument("--fail-on-regression", action="store_true", help="Exit non-zero for worse matched/class/storey/bbox results or unverified geometry")
    args = ap.parse_args(argv)

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    rules = manifest["rules"]
    bbox_tol = args.bbox_tol if args.bbox_tol is not None else float(rules["tolerances"]["worldBboxMeters"])
    elev_tol = args.elev_tol if args.elev_tol is not None else float(rules["tolerances"]["storeyElevationMeters"])
    for option, tolerance in (("--bbox-tol", bbox_tol), ("--elev-tol", elev_tol)):
        if not math.isfinite(tolerance) or tolerance < 0:
            raise MeasureFailure(f"{option} must be finite and non-negative")
    export_flags = list(rules["exportFlags"])

    selected = {x.strip() for x in args.models.split(",") if x.strip()} if args.models else None
    models = [m for m in manifest["models"] if selected is None or m["id"] in selected]
    if not models:
        raise MeasureFailure(f"No models selected from manifest {args.manifest}")

    # 1. Fetch & verify SHA-256 of every RVT and Revit reference IFC BEFORE running any export
    print("Verifying pinned reference artifacts (SHA-256)...", flush=True)
    verified_paths: dict[str, tuple[Path, Path]] = {}
    for m in models:
        rvt_path = download_and_verify(m["rvt"], args.cache_dir, offline=args.offline)
        ref_path = download_and_verify(m["referenceIfc"], args.cache_dir, offline=args.offline)
        verified_paths[m["id"]] = (rvt_path, ref_path)

    args.out_dir.mkdir(parents=True, exist_ok=True)
    build_root = args.out_dir / "_builds"
    build_root.mkdir(parents=True, exist_ok=True)

    # 2. Resolve/build RvtIO.Cli binaries for base & head
    if args.base_cli:
        if not args.base_cli.is_file():
            raise MeasureFailure(f"--base-cli does not exist: {args.base_cli}")
        base_sha, base_cli = args.base, args.base_cli.resolve()
    else:
        base_sha, base_cli = build_cli_for_ref(REPO_ROOT, args.base, build_root, "base")

    if args.head_cli:
        if not args.head_cli.is_file():
            raise MeasureFailure(f"--head-cli does not exist: {args.head_cli}")
        head_sha, head_cli = args.head, args.head_cli.resolve()
    elif not args.base_cli and resolve_git_sha(REPO_ROOT, args.head) == base_sha:
        head_sha, head_cli = base_sha, base_cli
    else:
        head_sha, head_cli = build_cli_for_ref(REPO_ROOT, args.head, build_root, "head")

    # 3. Export both revisions with --strict --report and compare against Revit reference IFC
    model_results: list[dict[str, Any]] = []
    control_ifc: Path | None = None
    regressions: list[str] = []

    for m in models:
        mid = m["id"]
        rvt_path, ref_ifc = verified_paths[mid]
        model_out = args.out_dir / mid
        model_out.mkdir(parents=True, exist_ok=True)

        side_data: dict[str, Any] = {}
        for side, cli_path in (("base", base_cli), ("head", head_cli)):
            print(f"[{mid}:{side}] exporting and scoring...", flush=True)
            out_ifc = model_out / f"{side}.ifc"
            report_path = model_out / f"{side}.report.json"
            exp_info = export_model(cli_path, rvt_path, out_ifc, report_path, export_flags, mid, side)
            cmp_res = ifc_compare.compare_ifc_against_reference(out_ifc, ref_ifc, bbox_tol_m=bbox_tol, storey_elev_tol_m=elev_tol)
            (model_out / f"{side}.compare.json").write_text(json.dumps(cmp_res, indent=2), encoding="utf-8")
            side_data[side] = {"export": exp_info, "comparison": cmp_res}
            if control_ifc is None:
                control_ifc = out_ifc

        base_vs_head = ifc_compare.diff_commit_reports(side_data["base"]["comparison"], side_data["head"]["comparison"])
        if base_vs_head["newlyMissingTags"]:
            regressions.append(f"{mid}: newly missing tags {base_vs_head['newlyMissingTags']}")
        if base_vs_head["regressedClassTags"]:
            regressions.append(f"{mid}: lost exact IFC class matches for tags {base_vs_head['regressedClassTags']}")
        if base_vs_head["regressedStoreyTags"]:
            regressions.append(f"{mid}: lost same-storey matches for tags {base_vs_head['regressedStoreyTags']}")
        if base_vs_head["regressedBboxTags"]:
            regressions.append(f"{mid}: regressed bbox tags {base_vs_head['regressedBboxTags']}")
        unverified_box_tags = side_data["head"]["comparison"]["worldBbox"]["unverifiedBoxTags"]
        if unverified_box_tags:
            regressions.append(f"{mid}: unverified bbox tags {unverified_box_tags}")

        model_results.append({
            "id": mid,
            "name": m["name"],
            "license": m["license"],
            "sources": {"rvt": m["rvt"], "referenceIfc": m["referenceIfc"]},
            "base": side_data["base"],
            "head": side_data["head"],
            "baseVsHead": base_vs_head,
        })

    control_result = None
    if not args.no_control_check:
        assert control_ifc is not None
        print("[control] running element removal/shift & negative gate checks...", flush=True)
        control_result = run_control_check(control_ifc, bbox_tol_m=bbox_tol)

    summary = {
        "schema": "rvtio-re1-measure-summary/1",
        "base": {"ref": args.base, "commit": base_sha},
        "head": {"ref": args.head, "commit": head_sha},
        "rules": {
            **rules,
            "tolerances": {"worldBboxMeters": bbox_tol, "storeyElevationMeters": elev_tol},
        },
        "models": model_results,
        "controlCheck": control_result,
        "regressions": regressions,
    }

    (args.out_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    md = render_markdown_summary(summary)
    (args.out_dir / "summary.md").write_text(md, encoding="utf-8")
    print("\n" + md, flush=True)

    if args.fail_on_regression and regressions:
        raise MeasureFailure("Regressions detected: " + "; ".join(regressions))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except MeasureFailure as exc:
        print(f"ERROR: {exc}", file=sys.stderr, flush=True)
        sys.exit(1)
