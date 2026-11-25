"""Compare Release CLI builds on a local RVT. Requires Python 3.8+ and dotnet.

Example: python tools/benchmark-export.py model.rvt before/RvtIO.Cli.dll after/RvtIO.Cli.dll --runs 3
Each process exports IFC, compact JSON, full JSON and Scene together. The temporary
directory must have room for one set of outputs. Times exclude hashing and cleanup.
Add --compare-separate to time four independent processes of the candidate build too.
"""
import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import tempfile
import time


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


FILES = {"ifc": "model.ifc", "compact-json": "compact.json", "full-json": "full.json", "scene": "scene.json"}


def run(cli, model, directory, formats):
    outputs = {flag: FILES[flag] for flag in formats}
    outputs["report"] = "report.json"
    command = ["dotnet", str(cli), str(model)]
    if "ifc" in formats:
        command.extend(["--ifc-timestamp", "2000-01-01T00:00:00Z"])
    for flag, name in outputs.items():
        command.extend(["--" + flag, str(directory / name)])
    log = directory / "stderr.log"
    with log.open("wb") as stderr:
        start = time.perf_counter()
        with subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=stderr) as process:
            code = process.wait()
            elapsed = time.perf_counter() - start
            peak = None
            if os.name == "nt":
                class MemoryCounters(ctypes.Structure):
                    _fields_ = [("size", ctypes.c_uint32), ("faults", ctypes.c_uint32),
                                ("peak", ctypes.c_size_t), ("rest", ctypes.c_size_t * 7)]

                memory = MemoryCounters()
                memory.size = ctypes.sizeof(memory)
                if not ctypes.windll.psapi.GetProcessMemoryInfo(
                        ctypes.c_void_p(int(process._handle)), ctypes.byref(memory), memory.size):
                    raise ctypes.WinError()
                peak = memory.peak
    if code:
        with log.open("rb") as stderr:
            stderr.seek(max(0, log.stat().st_size - 2000))
            raise RuntimeError(f"{cli} exited with {code}: {stderr.read().decode(errors='replace')}")
    return {"seconds": round(elapsed, 3), "peakWorkingSetBytes": peak,
            "outputs": {k: sha256(directory / name) for k, name in outputs.items()}}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("model", "baseline", "candidate"):
        ap.add_argument(name, type=Path)
    ap.add_argument("--runs", type=int, default=3, help="runs per build (default: 3)")
    ap.add_argument("--min-speedup", type=float, help="require this median improvement over the baseline batch")
    ap.add_argument("--temp-dir", type=Path, help="parent directory for temporary exports")
    ap.add_argument("--compare-separate", action="store_true", help="also compare the four separate candidate exports with its batch")
    args = ap.parse_args()
    if args.runs < 1:
        ap.error("--runs must be positive")
    if args.min_speedup is not None and not (math.isfinite(args.min_speedup) and args.min_speedup > 0):
        ap.error("--min-speedup must be finite and positive")
    for path in (args.model, args.baseline, args.candidate):
        if not path.is_file():
            ap.error(f"file not found: {path}")
    builds = {"baseline": args.baseline.resolve(), "candidate": args.candidate.resolve()}
    times = {key: [] for key in builds}
    separate = []
    expected = None
    for i in range(args.runs):
        for name in (list(builds) if i % 2 == 0 else list(reversed(builds))):
            with tempfile.TemporaryDirectory(dir=args.temp_dir) as tmp:
                result = run(builds[name], args.model.resolve(), Path(tmp).resolve(), FILES)
            print(json.dumps({"build": name, "run": i + 1, **result}), flush=True)
            if expected is None:
                expected = result["outputs"]
            for output, digest in result["outputs"].items():
                if expected[output] != digest:
                    raise SystemExit(f"Byte mismatch: {name}, run {i + 1}, {output}")
            times[name].append(result["seconds"])
        if args.compare_separate:
            seconds = 0
            for fmt in FILES:
                with tempfile.TemporaryDirectory(dir=args.temp_dir) as tmp:
                    result = run(builds["candidate"], args.model.resolve(), Path(tmp).resolve(), [fmt])
                print(json.dumps({"build": "candidate", "run": i + 1, "format": fmt, **result}), flush=True)
                if result["outputs"][fmt] != expected[fmt]:
                    raise SystemExit(f"Single export differs from batch: run {i + 1}, {fmt}")
                seconds += result["seconds"]
            separate.append(seconds)
    medians = {name: statistics.median(values) for name, values in times.items()}
    summary = {"inputBytes": args.model.stat().st_size, "medianSeconds": medians,
               "speedup": medians["baseline"] / medians["candidate"]}
    if separate:
        summary.update(separateSeconds=statistics.median(separate),
                       batchVsSeparate=statistics.median(separate) / medians["candidate"])
    print(json.dumps(summary))
    if args.min_speedup is not None and summary["speedup"] < args.min_speedup:
        raise SystemExit(f"Batch speedup {summary['speedup']:.3f}x is below the target {args.min_speedup:g}x")


if __name__ == "__main__":
    main()
