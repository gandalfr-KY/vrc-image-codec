"""Portable compiled-Udon/GPU benchmark. Python standard library + Unity/ClientSim + FFmpeg."""

import argparse
import csv
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def prepare(project):
    if not (project / "ProjectSettings/ProjectVersion.txt").exists():
        raise SystemExit(
            "Select a Unity Worlds SDK project with ClientSim and this package installed."
        )
    target = project / "Assets/CodecBenchmark"
    shutil.copytree(ROOT / "Tests~/Unity", target, dirs_exist_ok=True)
    shutil.copytree(
        ROOT / "Tests~/Images",
        project / "Logs/CodecBenchmark/images",
        dirs_exist_ok=True,
    )
    print(
        "Open Unity, wait for compilation, then Tools > Image Codec > Run benchmark (ClientSim)."
    )
    print(
        "Or launch Unity with -batchmode -projectPath <project> -executeMethod CodecBenchmarkSetup.Run (on Windows add -force-d3d11)"
    )
    print(
        "Do not use -quit or -nographics. Close the same project before batch launch."
    )


def verify(project, ffmpeg):
    directory = project / "Logs/CodecBenchmark"
    if (directory / "error.txt").exists():
        raise SystemExit((directory / "error.txt").read_text())
    if not (directory / "done.txt").exists():
        raise SystemExit("Finish the Unity benchmark first.")
    rows = [
        json.loads(line)
        for line in (directory / "measurements.jsonl").read_text().splitlines()
    ]
    expected = {
        (image, width, height, codec, sampling, quality)
        for image in ("library", "bar", "lounge")
        for width, height in (
            (256, 144),
            (512, 288),
            (640, 360),
            (1280, 720),
            (1920, 1080),
        )
        for codec in ("AVIF", "JPEG")
        for sampling in ("420", "444")
        for quality in (60, 70, 80)
    }
    actual = {
        (r["image"], r["width"], r["height"], r["codec"], r["sampling"], r["quality"])
        for r in rows
    }
    if actual != expected or len(rows) != len(expected):
        raise SystemExit("Incomplete or duplicated benchmark matrix.")
    failed = []
    for row in rows:
        if row.get("error"):
            row["status"] = "error"
            failed.append(row["id"] + ": " + row["error"])
            continue
        # Exactly one independent decode per encoded image. RGB is reconstructed with the
        # documented full-range BT.601 + nearest chroma grid, avoiding swscale filter differences.
        subsampled = row["sampling"] == "420"
        suffix = ".avif" if row["codec"] == "AVIF" else ".jpg"
        encoded = directory / (row["id"] + suffix)
        reference = subprocess.check_output(
            [
                ffmpeg,
                "-v",
                "error",
                "-threads",
                "1",
                "-i",
                str(encoded),
                "-frames:v",
                "1",
                "-pix_fmt",
                ("yuvj" if row["codec"] == "JPEG" else "yuv")
                + ("420p" if subsampled else "444p"),
                "-f",
                "rawvideo",
                "-",
            ]
        )
        width, height = row["width"], row["height"]
        chroma_width, chroma_height = (
            (width // 2, height // 2) if subsampled else (width, height)
        )
        plane_size, chroma_size = width * height, chroma_width * chroma_height
        pixels = (directory / (row["id"] + ".rgba")).read_bytes()
        if (
            encoded.stat().st_size != row["bytes"]
            or len(reference) != plane_size + 2 * chroma_size
            or len(pixels) != plane_size * 4
        ):
            raise SystemExit("Unexpected image length: " + row["id"])
        maximum = squared = 0
        for y in range(height):
            for x in range(width):
                chroma = (
                    (y // 2) * chroma_width + x // 2 if subsampled else y * width + x
                )
                luminance = reference[y * width + x]
                u, v = (
                    reference[plane_size + chroma] - 128,
                    reference[plane_size + chroma_size + chroma] - 128,
                )
                rgb = (
                    luminance + 1.402 * v,
                    luminance - 0.344136 * u - 0.714136 * v,
                    luminance + 1.772 * u,
                )
                for channel in range(3):
                    difference = pixels[
                        ((height - 1 - y) * width + x) * 4 + channel
                    ] - max(0, min(255, math.floor(rgb[channel] + 0.5)))
                    maximum = max(maximum, abs(difference))
                    squared += difference * difference
        row["maxRgbDelta"] = maximum
        row["rmsRgbDelta"] = math.sqrt(squared / (plane_size * 3))
        row["verified"] = maximum <= 3 and row["rmsRgbDelta"] <= 1.5
        row["status"] = "verified" if row["verified"] else "mismatch"
        if not row["verified"]:
            failed.append(
                f'{row["id"]}: RGB delta max={maximum}, RMS={row["rmsRgbDelta"]:.3f}'
            )
        print(
            f'{row["id"]}: {row["bytes"]} B, encode={row["encodeSeconds"]:.3f}s decode={row["decodeSeconds"]:.3f}s, max delta={maximum}',
            flush=True,
        )
    result = json.loads((directory / "environment.json").read_text())
    result.update(
        rows=rows,
        failures=failed,
        externalDecoder=subprocess.check_output(
            [ffmpeg, "-version"], text=True
        ).splitlines()[0],
    )
    (directory / "results.json").write_text(json.dumps(result, indent=2) + "\n")
    with (directory / "results.csv").open("w", newline="") as stream:
        writer = csv.DictWriter(
            stream, fieldnames=list(dict.fromkeys(key for row in rows for key in row))
        )
        writer.writeheader()
        writer.writerows(rows)
    summary = [
        "# ClientSim benchmark",
        "",
        f"{len(rows)} cases; target 60 fps; one run per image, including first-use costs.",
        "",
        "Binary sizes exclude URL/encryption. Timings exclude validation. Each row is the range across the three photographs.",
        "",
        "| Codec | Sampling | Resolution | Q | Bytes | Encode (s) | Decode (s) |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    groups = {}
    for row in rows:
        key = (
            row["codec"],
            row["sampling"],
            row["width"],
            row["height"],
            row["quality"],
        )
        groups.setdefault(key, []).append(row)
    for (codec, sampling, width, height, quality), group in groups.items():
        if any(row.get("error") for row in group):
            summary.append(
                f"| {codec} | {sampling} | {width}x{height} | {quality} | Limit/error: see JSON | | |"
            )
            continue
        ranges = [
            f"{min(row[key] for row in group):.{precision}f}–{max(row[key] for row in group):.{precision}f}"
            for key, precision in (
                ("bytes", 0),
                ("encodeSeconds", 3),
                ("decodeSeconds", 3),
            )
        ]
        summary.append(
            f"| {codec} | {sampling} | {width}x{height} | {quality} | "
            + " | ".join(ranges)
            + " |"
        )
    (directory / "results.md").write_text("\n".join(summary) + "\n", encoding="utf-8")
    if failed:
        raise SystemExit("\n".join(failed))
    print(
        f"Verified {sum(row.get('verified', False) for row in rows)} images once each. Results: {directory / 'results.json'}"
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "run", "verify"))
    parser.add_argument("project", type=Path)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument(
        "--unity", type=Path, help="Unity executable for the unattended run action"
    )
    args = parser.parse_args()
    project = args.project.resolve()
    if args.action == "prepare":
        prepare(project)
        return
    if args.action == "run":
        if not args.unity:
            parser.error("run requires --unity <Unity executable>")
        prepare(project)
        subprocess.run(
            [
                str(args.unity.resolve()),
                "-batchmode",
                *(["-force-d3d11"] if sys.platform == "win32" else []),
                "-projectPath",
                str(project),
                "-executeMethod",
                "CodecBenchmarkSetup.Run",
                "-logFile",
                str(project / "Logs/CodecBenchmark/editor.log"),
            ],
            check=True,
        )
    verify(project, args.ffmpeg)


if __name__ == "__main__":
    main()
