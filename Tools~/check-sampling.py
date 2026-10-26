"""Prepare and verify compiled-Udon AVIF 4:2:0/4:4:4 encode/decode round trips."""

import argparse
import json
from pathlib import Path
import shutil
import struct
import subprocess
import zipfile
import zlib

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path)
parser.add_argument("--prepare", action="store_true")
parser.add_argument("--run", action="store_true")
args = parser.parse_args()
project = args.project.resolve()
log = project / "Logs/AvifSampling"
log.mkdir(parents=True, exist_ok=True)
if args.prepare:
    support = project / "Assets/CodecTestSupport"
    support.mkdir(parents=True, exist_ok=True)
    shutil.copy2(
        root / "Tests~/ClientSim/CodecBatchPacing.cs", support / "CodecBatchPacing.cs"
    )
    (project / "Assets/AvifSampling").mkdir(parents=True, exist_ok=True)
    for name in ["AvifSamplingSetup.cs", "AvifSamplingObserver.cs"]:
        shutil.copy2(
            root / "Tests~/ClientSim" / name,
            project
            / ("Assets/Editor" if name.endswith("Setup.cs") else "Assets/AvifSampling")
            / name,
        )
    with zipfile.ZipFile(root / "Tests~/fhd-fixtures.zip") as archive:
        (log / "source.avif").write_bytes(archive.read("library.avif"))
    subprocess.run(
        [
            "ffmpeg",
            "-v",
            "error",
            "-y",
            "-i",
            str(log / "source.avif"),
            "-frames:v",
            "1",
            "-update",
            "1",
            str(log / "source.png"),
        ],
        check=True,
    )

    def chunk(kind, data):
        return (
            struct.pack(">I", len(data))
            + kind
            + data
            + struct.pack(">I", zlib.crc32(kind + data))
        )

    pixels = b"".join(
        b"\0" + bytes(v for x in range(16) for v in [x * 17, y * 17, (x + y) * 8])
        for y in range(16)
    )
    (log / "small.png").write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", 16, 16, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(pixels))
        + chunk(b"IEND", b"")
    )
    print(
        "Prepared. Refresh Unity, then --run after version.txt reads sampling-v2-gpu."
    )
    raise SystemExit(0)
if args.run:
    if (log / "version.txt").read_text() != "sampling-v2-gpu":
        raise SystemExit("Refresh matching test sources first.")
    (log / "prepare.request").write_text("run")
    raise SystemExit(0)
if (log / "error.txt").exists():
    raise SystemExit((log / "error.txt").read_text())
if not (log / "done.txt").exists() or not (log / "stopped.txt").exists():
    raise SystemExit("ClientSim must finish and restore its scene/settings.")
result = json.loads((log / "results.json").read_text())
if [row["id"] for row in result["rows"]] != [
    "420-small",
    "444-small",
    "420-fhd",
    "444-fhd",
]:
    raise SystemExit("Incomplete sampling jobs")
for row in result["rows"]:
    name = row["id"]
    width, height = (16, 16) if name.endswith("small") else (1920, 1080)
    full = name.startswith("444")
    tile = 24 if full else 12
    chroma = 8 if full else 4
    offsets = [0, 64, 128 if full else 80]
    expected = subprocess.check_output(
        [
            "ffmpeg",
            "-v",
            "error",
            "-threads",
            "1",
            "-i",
            str(log / (name + ".avif")),
            "-pix_fmt",
            "yuv444p" if full else "yuv420p",
            "-f",
            "rawvideo",
            "-",
        ]
    )
    for suffix in ["recon", "decoded"]:
        raw = (log / (name + "." + suffix + ".rgba")).read_bytes()[::4]
        actual = bytearray()
        for component in range(3):
            n = 8 if component == 0 else chroma
            plane = bytearray(width // 8 * n * (height // 8 * n))
            stride = width // 8 * n
            for by in range(height // 8):
                for bx in range(width // 8):
                    for r in range(n):
                        local = offsets[component] + r * n
                        source = (by * tile + local // 8) * width + bx * 8 + local % 8
                        target = (by * n + r) * stride + bx * n
                        plane[target : target + n] = raw[source : source + n]
            actual.extend(plane)
        if actual != expected:
            raise SystemExit(
                f"{name} {suffix}: {sum(a!=b for a,b in zip(actual,expected))} YUV samples differ"
            )
    row["exactYuvSamples"] = len(expected)
    print(
        f'{name}: {row["bytes"]} bytes, encode {row["encodeSeconds"]:.3f}s, decode {row["decodeSeconds"]:.3f}s; {len(expected)} YUV samples exact in both paths'
    )
(log / "verified.json").write_text(json.dumps(result, indent=2))
