"""Verify production GPU AVIF decoding against FFmpeg in compiled-Udon ClientSim."""

from pathlib import Path
import argparse, json, shutil, subprocess, zipfile

root = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument("project", type=Path)
p.add_argument("--prepare", action="store_true")
p.add_argument("--run", action="store_true")
args = p.parse_args()
project = args.project.resolve()
log = project / "Logs/AvifDecoder"
log.mkdir(parents=True, exist_ok=True)
if args.prepare:
    support = project / "Assets/CodecTestSupport"
    support.mkdir(parents=True, exist_ok=True)
    shutil.copy2(
        root / "Tests~/ClientSim/CodecBatchPacing.cs", support / "CodecBatchPacing.cs"
    )
    (project / "Assets/AvifDecoderTests").mkdir(parents=True, exist_ok=True)
    for name in ["AvifDecoderTestSetup.cs", "AvifDecoderObserver.cs"]:
        shutil.copy2(
            root / "Tests~/ClientSim" / name,
            project
            / (
                "Assets/Editor"
                if name.endswith("Setup.cs")
                else "Assets/AvifDecoderTests"
            )
            / name,
        )
    for group, archive in [
        ("orange", "orange-fixtures.zip"),
        ("fhd", "fhd-fixtures.zip"),
        ("context", "decoder-context-fixtures.zip"),
        ("444", "decoder-444-fixtures.zip"),
    ]:
        folder = log / group
        folder.mkdir(exist_ok=True)
        with zipfile.ZipFile(root / "Tests~" / archive) as z:
            for name in z.namelist():
                if name.endswith(".avif"):
                    (folder / Path(name).name).write_bytes(z.read(name))
    files = [
        (log / group / (scene + ".avif"))
        for group in ["orange", "fhd"]
        for scene in ["library", "bar", "lounge"]
    ]
    files += [
        log / "context" / f"context-{q}{suffix}.avif"
        for q in [0, 90, 100]
        for suffix in ["", "-frozen"]
    ]
    files += [
        log / "context" / f"fhd-{scene}-frozen.avif"
        for scene in ["library", "bar", "lounge"]
    ]
    files += [
        log / "444" / (name + ".avif") for name in ["444-small", "444-32", "444-fhd"]
    ]
    jobs = [dict(id=f"{f.parent.name}-{f.stem}", path=str(f)) for f in files]
    valid = files[0].read_bytes()
    bad = [
        b"",
        valid[:299],
        valid[:-1],
        valid + b"\0",
        valid + bytes(100001),
        b"GIF8" + valid[4:],
        valid[:8] + b"jpeg" + valid[12:],
    ]

    # Keep the restricted frame header intact and zero only its entropy payload.
    def leb(data, at):
        value = 0
        shift = 0
        while True:
            b = data[at]
            at += 1
            value |= (b & 127) << shift
            if b < 128:
                return value, at
            shift += 7

    at = 0
    while valid[at + 4 : at + 8] != b"mdat":
        at += int.from_bytes(valid[at : at + 4], "big")
    at += 11
    length, at = leb(valid, at)
    at += length + 1
    _, at = leb(valid, at)
    at += 9
    bad.append(valid[:at] + bytes(len(valid) - at))
    invalid = []
    for n, data in enumerate(bad):
        f = log / f"invalid-{n}.avif"
        f.write_bytes(data)
        invalid.append(str(f))
    (log / "jobs.json").write_text(
        json.dumps(dict(jobs=jobs, invalid=invalid), indent=2)
    )
    print(
        f"Prepared {len(jobs)} valid images and {len(invalid)} invalid inputs. Refresh Unity."
    )
    raise SystemExit(0)
if args.run:
    if (log / "version.txt").read_text() != "avif-decoder-v1":
        raise SystemExit("Refresh matching test sources first.")
    (log / "prepare.request").write_text("run")
    raise SystemExit(0)
if (log / "error.txt").exists():
    raise SystemExit((log / "error.txt").read_text())
if not (log / "done.txt").exists() or not (log / "stopped.txt").exists():
    raise SystemExit("Wait for ClientSim to finish and restore scene/settings.")
result = json.loads((log / "results.json").read_text())
suite = json.loads((log / "jobs.json").read_text())
if (
    len(result["rows"]) != len(suite["jobs"])
    or not result["cancelledAndReused"]
    or result["invalidRejected"] != len(suite["invalid"])
):
    raise SystemExit("Incomplete regression run")
for row, job in zip(result["rows"], suite["jobs"]):
    width, height = row["width"], row["height"]
    full = not row["subsample"]
    tile = 24 if full else 12
    chroma = 8 if full else 4
    offsets = [0, 64, 128 if full else 80]
    raw = (log / (row["id"] + ".rgba")).read_bytes()[::4]
    actual = bytearray()
    for component in range(3):
        n = 8 if component == 0 else chroma
        stride = width // 8 * n
        plane = bytearray(stride * (height // 8 * n))
        for by in range(height // 8):
            for bx in range(width // 8):
                for r in range(n):
                    local = offsets[component] + r * n
                    src = (by * tile + local // 8) * width + bx * 8 + local % 8
                    dst = (by * n + r) * stride + bx * n
                    plane[dst : dst + n] = raw[src : src + n]
        actual.extend(plane)
    expected = subprocess.check_output(
        [
            "ffmpeg",
            "-v",
            "error",
            "-threads",
            "1",
            "-i",
            job["path"],
            "-pix_fmt",
            "yuv444p" if full else "yuv420p",
            "-f",
            "rawvideo",
            "-",
        ]
    )
    if actual != expected:
        raise SystemExit(
            f'{row["id"]}: {sum(a!=b for a,b in zip(actual,expected))} YUV samples differ'
        )
    row["exactYuvSamples"] = len(expected)
    print(
        f'{row["id"]}: {row["seconds"]:.3f}s, CPU submission {row["cpuSeconds"]:.3f}s, max step {row["maxStepMs"]:.2f}ms; {len(expected)} YUV samples exact'
    )
(log / "verified.json").write_text(json.dumps(result, indent=2))
