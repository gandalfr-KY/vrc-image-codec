"""Prepare or verify the actual compiled-Udon/GPU encoder in an isolated Unity project."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path, help="Isolated Unity project with Worlds SDK and this package")
parser.add_argument("--prepare", action="store_true", help="Copy test sources and extract fixtures; refresh Unity next")
parser.add_argument("--run", action="store_true", help="Request the prepared ClientSim suite")
args = parser.parse_args()
project = args.project.resolve()
log = project / "Logs/CodecEncoder"
log.mkdir(parents=True, exist_ok=True)

if args.prepare:
    runtime = project / "Assets/CodecProduction"
    runtime.mkdir(parents=True, exist_ok=True)
    editor = project / "Assets/Editor"
    editor.mkdir(exist_ok=True)
    for name in ["CodecEncoderSetup.cs", "CodecFixtureDriver.cs", "CodecEncoderObserver.cs"]:
        shutil.copy2(ROOT / "Tests~/ClientSim" / name, (editor if name.endswith("Setup.cs") else runtime) / name)
    jobs = []
    for family, width, height, quality in [("orange", 360, 640, 70), ("fhd", 1920, 1080, 60), ("edge", 16, 16, 70)]:
        fixtures = log / family
        fixtures.mkdir(exist_ok=True)
        with zipfile.ZipFile(ROOT / f"Tests~/{family}-fixtures.zip") as archive:
            archive.extractall(fixtures)
        for scene in ["minimum"] if family == "edge" else ["library", "bar", "lounge"]:
            jobs.append({"id": f"{family}-{scene}", "packet": str(fixtures / f"{scene}.packed.rgba"),
                         "width": width, "height": height, "quality": quality,
                         "sha256": hashlib.sha256((fixtures / f"{scene}.avif").read_bytes()).hexdigest()})
    for quality, digest in {
        0: "9971f632c04ee3202d9429edbe0e6c86a8240f9471f4f8e44e6a74e8c57b5a48",
        90: "e7c30ac52e1f95a0e6ec1725e1eea436a14537f4cdb95283ae8846be97c22e90",
        100: "7a3ba50cfaefe9466eedd199889a18f6506ada42620a5a82782c52b82a84efc5",
    }.items():
        jobs.append({"id": f"context-{quality}", "packet": str(log / "orange/library.packed.rgba"),
                     "width": 360, "height": 640, "quality": quality, "sha256": digest})
    (log / "jobs.json").write_text(json.dumps({"jobs": jobs}, indent=2), encoding="utf-8")
    if not (runtime / "Source.png").exists():
        subprocess.run(["ffmpeg", "-v", "error", "-i", str(log / "fhd/library.avif"), "-frames:v", "1",
                        "-update", "1", str(runtime / "Source.png")], check=True)
    print("Prepared. Refresh Unity, then run with --run after version.txt reads production-gpu-v1.")
    raise SystemExit(0)

if args.run:
    if (log / "version.txt").read_text() != "production-gpu-v1":
        raise SystemExit("Refresh the matching test sources in Unity first.")
    (log / "prepare.request").write_text("run", encoding="utf-8")
    print("Requested ClientSim test. Run this command without flags after done.txt/stopped.txt appear.")
    raise SystemExit(0)

if (log / "error.txt").exists():
    raise SystemExit((log / "error.txt").read_text())
if not (log / "done.txt").exists() or not (log / "stopped.txt").exists():
    raise SystemExit("The Unity test has not completed and restored its scene/settings.")
results = json.loads((log / "results.json").read_text())
jobs = json.loads((log / "jobs.json").read_text())["jobs"]
rows = {row["id"]: row for row in results["rows"]}
for job in jobs:
    actual = (log / (job["id"] + ".avif")).read_bytes()
    assert hashlib.sha256(actual).hexdigest() == job["sha256"], job["id"]
    assert rows[job["id"]]["exact"], job["id"]
    raw = subprocess.run(["ffmpeg", "-v", "error", "-threads", "1", "-i", str(log / (job["id"] + ".avif")),
                          "-pix_fmt", "yuv420p", "-f", "rawvideo", "-"], check=True, capture_output=True).stdout
    assert len(raw) == job["width"] * job["height"] * 3 // 2, job["id"]
assert results["cancelledAndReused"] and results["invalidSettingsRejected"]
assert "full-capture" in rows
capture = subprocess.run(["ffmpeg", "-v", "error", "-threads", "1", "-i", str(log / "full-capture.avif"),
                          "-pix_fmt", "yuv420p", "-f", "rawvideo", "-"], check=True, capture_output=True).stdout
assert len(capture) == 1920 * 1080 * 3 // 2
target = ROOT / "test-results/codec.json"
target.parent.mkdir(exist_ok=True)
target.write_text(json.dumps(results, indent=2), encoding="utf-8")
print(f"PASS: {len(jobs)} exact GPU bitstreams and independent FFmpeg decodes; cancellation/reuse, invalid quality, full capture.")
