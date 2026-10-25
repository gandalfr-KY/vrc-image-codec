"""Compare Editor trial YUV output with independent FFmpeg decoding.

Usage: python check-decoder-gpu.py /path/to/UnityProject/Logs/AvifDecoder
The optional Tests~/DecoderTrial.cs creates these files in an isolated Editor project.
"""

from pathlib import Path
import json
import subprocess
import sys

folder = Path(sys.argv[1])
results = json.loads((folder / "results.json").read_text(encoding="utf-8"))
jobs = json.loads((folder / "jobs.json").read_text(encoding="utf-8"))
images = {job["id"]: job["file"] for job in jobs["jobs"]}
for row in results["rows"]:
    width, height = row["width"], row["height"]
    expected = subprocess.run(
        ["ffmpeg", "-v", "error", "-threads", "1", "-i", images[row["id"]], "-pix_fmt", "yuv420p", "-f", "rawvideo", "-"],
        capture_output=True,
        check=True,
    ).stdout
    atlas = (folder / (row["id"] + ".atlas.rgba")).read_bytes()
    actual = bytearray()
    for offset, size in [(0, 8), (64, 4), (80, 4)]:
        for by in range(height // 8):
            for y in range(size):
                for bx in range(width // 8):
                    for x in range(size):
                        slot = offset + y * size + x
                        at = ((by * 12 + slot // 8) * width + bx * 8 + slot % 8) * 4
                        actual.append(atlas[at])
    assert actual == expected, (row["id"], "GPU reconstructed YUV differs from FFmpeg")
    print("PASS", row["id"], "every YUV sample matches FFmpeg")
