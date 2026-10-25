"""Replay orange GPU packets through production Udon C# entropy coding.

This checks exact output and independent decoding, not GPU execution or speed.
"""

from pathlib import Path
import os, subprocess, tempfile, json, zipfile, hashlib

ROOT = Path(__file__).resolve().parents[1]
UNITY = Path(
    "C:/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Data/MonoBleedingEdge"
)
CSC = os.environ.get(
    "CSC", str(UNITY / "lib/mono/4.5/csc.exe") if UNITY.exists() else "csc"
)
MONO = os.environ.get("MONO", str(UNITY / "bin/mono.exe") if UNITY.exists() else "mono")
with tempfile.TemporaryDirectory(prefix="avif-codec-") as temp:
    path = Path(temp)
    exe = path / "codec.exe"
    compiler = [MONO, CSC] if CSC.endswith(".exe") else [CSC]
    subprocess.run(
        compiler
        + [
            "/nologo",
            "/out:" + str(exe),
            str(ROOT / "Tests~/Standalone.cs"),
            str(ROOT / "Runtime/TernaryAvifEncoder.cs"),
            str(ROOT / "Runtime/AvifTables.cs"),
        ],
        check=True,
    )
    with zipfile.ZipFile(ROOT / "Tests~/orange-fixtures.zip") as z:
        z.extractall(path)
    rows = []
    for scene in ["library", "bar", "lounge"]:
        source = path / (scene + ".packed.rgba")
        target = path / "output.avif"
        golden = (path / (scene + ".avif")).read_bytes()
        for budget in [1, 2, 4]:
            subprocess.run(
                [
                    MONO,
                    str(exe),
                    str(source),
                    "360",
                    "640",
                    str(target),
                    "70",
                    str(budget),
                ],
                check=True,
                stdout=subprocess.DEVNULL,
            )
            actual = target.read_bytes()
            assert actual == golden, (
                scene,
                budget,
                "selected orange bitstream changed",
            )
        raw = subprocess.run(
            [
                "ffmpeg",
                "-v",
                "error",
                "-threads",
                "1",
                "-i",
                str(target),
                "-pix_fmt",
                "yuv420p",
                "-f",
                "rawvideo",
                "-",
            ],
            check=True,
            capture_output=True,
        ).stdout
        assert len(raw) == 360 * 640 * 3 // 2
        rows.append(
            {
                "scene": scene,
                "bytes": len(golden),
                "sha256": hashlib.sha256(golden).hexdigest(),
                "budgets": [1, 2, 4],
                "matchesOrange": True,
                "independentDecode": True,
            }
        )
    # Replay one recorded packet under all remaining AV1 probability contexts.
    # These test entropy adaptation, not the visual quality of those settings.
    context_goldens = {
        0: "9971f632c04ee3202d9429edbe0e6c86a8240f9471f4f8e44e6a74e8c57b5a48",
        90: "e7c30ac52e1f95a0e6ec1725e1eea436a14537f4cdb95283ae8846be97c22e90",
        100: "7a3ba50cfaefe9466eedd199889a18f6506ada42620a5a82782c52b82a84efc5",
    }
    for quality, expected in context_goldens.items():
        subprocess.run(
            [
                MONO,
                str(exe),
                str(path / "library.packed.rgba"),
                "360",
                "640",
                str(target),
                str(quality),
                "2",
            ],
            check=True,
            stdout=subprocess.DEVNULL,
        )
        actual = target.read_bytes()
        digest = hashlib.sha256(actual).hexdigest()
        assert digest == expected, (quality, "probability context bitstream changed")
        rows.append(
            {
                "scene": "library-context-replay",
                "quality": quality,
                "bytes": len(actual),
                "sha256": digest,
                "matchesPreOptimization": True,
            }
        )
    # FullHD level4.0 replay is byte-identical to the pre-optimization reference.
    fhd = path / "fhd"
    fhd.mkdir()
    with zipfile.ZipFile(ROOT / "Tests~/fhd-fixtures.zip") as z:
        z.extractall(fhd)
    for scene in ["library", "bar", "lounge"]:
        source = fhd / (scene + ".packed.rgba")
        golden = (fhd / (scene + ".avif")).read_bytes()
        subprocess.run(
            [MONO, str(exe), str(source), "1920", "1080", str(target), "60", "2"],
            check=True,
            stdout=subprocess.DEVNULL,
        )
        assert target.read_bytes() == golden, (scene, "FullHD bitstream changed")
        rows.append(
            {
                "scene": scene,
                "width": 1920,
                "height": 1080,
                "quality": 60,
                "bytes": len(golden),
                "sha256": hashlib.sha256(golden).hexdigest(),
                "matchesPreOptimization": True,
            }
        )
    for w, h, q in [
        (0, 360, 70),
        (640, 361, 70),
        (1920, 1920, 70),
        (640, 360, -1),
        (640, 360, 101),
    ]:
        p = subprocess.run(
            [MONO, str(exe), str(source), str(w), str(h), str(target), str(q), "2"],
            capture_output=True,
        )
        assert p.returncode != 0, (w, h, q)
    p = subprocess.run(
        [MONO, str(exe), str(source), "360", "640", str(target), "70", "2", "missing"],
        capture_output=True,
    )
    assert p.returncode != 0, "GPU resources must be required; no CPU fallback"
    report = ROOT / "test-results/codec.json"
    report.parent.mkdir(exist_ok=True)
    report.write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    print(
        "PASS: 3 orange bitstreams x 3 scheduling budgets + 3 FullHD references + all 4 probability contexts, independent FFmpeg decode, invalid settings and missing GPU rejection"
    )
