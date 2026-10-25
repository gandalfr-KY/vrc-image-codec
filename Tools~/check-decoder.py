"""Check production CPU decoding against recorded GPU coefficients.

Only the GPU submission methods are replaced in this native reference harness.
The separate Editor trial verifies real Udon/GPU pixels against FFmpeg.
"""

from pathlib import Path
import os
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
UNITY = Path(
    "C:/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Data/MonoBleedingEdge"
)
CSC = os.environ.get(
    "CSC", str(UNITY / "lib/mono/4.5/csc.exe") if UNITY.exists() else "csc"
)
MONO = os.environ.get("MONO", str(UNITY / "bin/mono.exe") if UNITY.exists() else "mono")


def compare(actual, expected, width, height):
    for block in range(width // 8 * (height // 8)):
        for first, count in [(0, 64), (65, 16), (82, 16)]:
            header = (block * 99 + first) * 4
            assert actual[header] == expected[header], (block, first, "EOB")
            for local in range(count):
                at = header + 4 + local * 4
                decoded = int.from_bytes(actual[at : at + 2], "little", signed=True)
                original = int.from_bytes(expected[at : at + 2], "little") - 32768
                assert decoded == original, (block, first, local)


with tempfile.TemporaryDirectory(prefix="avif-decoder-") as directory:
    temp = Path(directory)
    source = (ROOT / "Runtime/TernaryAvifDecoder.cs").read_text(encoding="utf-8")
    start = source.index("    private RenderTexture Surface(")
    end = source.index("    private int[] intraYDefault", start)
    source = (
        source[:start]
        + "    private void BeginReconstruction(){busy=false;complete=true;}\n    private void Reconstruct(){}\n"
        + source[end:]
    )
    (temp / "DecoderCpu.cs").write_text(source, encoding="utf-8")
    compiler = [MONO, CSC] if CSC.endswith(".exe") else [CSC]
    exe = temp / "decoder.exe"
    subprocess.run(
        compiler
        + [
            "/nologo",
            "/main:DecoderTest",
            "/out:" + str(exe),
            str(temp / "DecoderCpu.cs"),
            str(ROOT / "Tests~/DecoderStandalone.cs"),
            str(ROOT / "Tests~/Standalone.cs"),
            str(ROOT / "Runtime/TernaryAvifEncoder.cs"),
            str(ROOT / "Runtime/AvifTables.cs"),
        ],
        check=True,
    )
    cases = 0
    for name, width, height in [("orange", 360, 640), ("fhd", 1920, 1080)]:
        folder = temp / name
        folder.mkdir()
        with zipfile.ZipFile(ROOT / f"Tests~/{name}-fixtures.zip") as fixture:
            fixture.extractall(folder)
        for scene in ["library", "bar", "lounge"]:
            image = folder / (scene + ".avif")
            output = temp / "coefficients.rgba"
            subprocess.run(
                [MONO, str(exe), str(image), str(output), "cancel"], check=True
            )
            actual = output.read_bytes()
            expected = (folder / (scene + ".packed.rgba")).read_bytes()
            compare(actual, expected, width, height)
            cases += 1
    encoder = temp / "encoder.exe"
    subprocess.run(
        compiler
        + [
            "/nologo",
            "/out:" + str(encoder),
            str(ROOT / "Tests~/Standalone.cs"),
            str(ROOT / "Runtime/TernaryAvifEncoder.cs"),
            str(ROOT / "Runtime/AvifTables.cs"),
        ],
        check=True,
    )
    for quality in [0, 90, 100]:
        packet = temp / "orange/library.packed.rgba"
        image = temp / f"quality-{quality}.avif"
        output = temp / "coefficients.rgba"
        subprocess.run(
            [
                MONO,
                str(encoder),
                str(packet),
                "360",
                "640",
                str(image),
                str(quality),
                "2",
            ],
            check=True,
            stdout=subprocess.DEVNULL,
        )
        subprocess.run([MONO, str(exe), str(image), str(output)], check=True)
        compare(output.read_bytes(), packet.read_bytes(), 360, 640)
        cases += 1
    valid = (temp / "orange/library.avif").read_bytes()
    invalid = [
        b"",
        valid[:299],
        valid[:-1],
        valid + b"\0",
        valid + bytes(100001),
        b"GIF8" + valid[4:],
        valid[:8] + b"jpeg" + valid[12:],
    ]
    for index, content in enumerate(invalid):
        image = temp / f"invalid-{index}.avif"
        image.write_bytes(content)
        result = subprocess.run(
            [MONO, str(exe), str(image), str(temp / "invalid.rgba")],
            capture_output=True,
        )
        assert result.returncode == 1 and b"Exception" not in result.stderr, index
    print(
        f"PASS {cases} reference images: every coefficient exact, cancellation/reuse, {len(invalid)} malformed/unsupported inputs rejected without exceptions"
    )
