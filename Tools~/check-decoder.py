"""Check production CPU decoding; only GPU reconstruction is replaced in this native harness."""
from pathlib import Path
import os
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
UNITY = Path("C:/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Data/MonoBleedingEdge")
CSC = os.environ.get("CSC", str(UNITY / "lib/mono/4.5/csc.exe") if UNITY.exists() else "csc")
MONO = os.environ.get("MONO", str(UNITY / "bin/mono.exe") if UNITY.exists() else "mono")


def compare(actual, expected, width, height):
    for block in range(width // 8 * (height // 8)):
        for first, count in [(0, 64), (65, 16), (82, 16)]:
            header = (block * 99 + first) * 4
            assert actual[header] == expected[header], (block, first, "EOB")
            for local in range(count):
                at = header + 4 + local * 4
                assert int.from_bytes(actual[at:at + 2], "little", signed=True) == int.from_bytes(expected[at:at + 2], "little") - 32768, (block, first, local)


with tempfile.TemporaryDirectory(prefix="avif-decoder-") as directory:
    temp = Path(directory)
    source = (ROOT / "Runtime/TernaryAvifDecoder.cs").read_text(encoding="utf-8")
    start = source.index("    private RenderTexture Surface(")
    end = source.index("    private int[] intraYDefault", start)
    source = source[:start] + "    private void BeginReconstruction(){busy=false;complete=true;}\n    private void Reconstruct(){}\n" + source[end:]
    (temp / "DecoderCpu.cs").write_text(source, encoding="utf-8")
    exe = temp / "decoder.exe"
    subprocess.run(([MONO, CSC] if CSC.endswith(".exe") else [CSC]) + ["/nologo", "/main:DecoderTest", "/out:" + str(exe),
                   str(temp / "DecoderCpu.cs"), str(ROOT / "Tests~/DecoderStandalone.cs"),
                   str(ROOT / "Tests~/UnityStubs.cs"), str(ROOT / "Runtime/AvifTables.cs")], check=True)
    for name in ["orange", "fhd", "decoder-context"]:
        (temp / name).mkdir()
        with zipfile.ZipFile(ROOT / f"Tests~/{name}-fixtures.zip") as fixture:
            fixture.extractall(temp / name)
    cases = []
    for name, width, height in [("orange", 360, 640), ("fhd", 1920, 1080)]:
        for scene in ["library", "bar", "lounge"]:
            cases.append((temp / name / f"{scene}.avif", temp / name / f"{scene}.packed.rgba", width, height, None))
    context = temp / "decoder-context"
    for quality in [0, 90, 100]:
        cases.append((context / f"context-{quality}.avif", temp / "orange/library.packed.rgba", 360, 640, None))
        cases.append((context / f"context-{quality}-frozen.avif", temp / "orange/library.packed.rgba", 360, 640, context / f"context-{quality}.avif"))
    for scene in ["library", "bar", "lounge"]:
        cases.append((context / f"fhd-{scene}-frozen.avif", temp / "fhd" / f"{scene}.packed.rgba", 1920, 1080, temp / "fhd" / f"{scene}.avif"))
    output = temp / "coefficients.rgba"
    for image, packet, width, height, baseline in cases:
        command = [MONO, str(exe), str(image), str(output), "cancel"]
        if baseline:
            command.append(str(baseline))
        subprocess.run(command, check=True)
        compare(output.read_bytes(), packet.read_bytes(), width, height)
        if baseline:
            decoded = [subprocess.run(["ffmpeg", "-v", "error", "-threads", "1", "-i", str(source), "-pix_fmt", "yuv420p", "-f", "rawvideo", "-"],
                                      capture_output=True, check=True).stdout for source in [baseline, image]]
            assert decoded[0] == decoded[1], (image.name, "CDF mode changed pixels")
            subprocess.run([MONO, str(exe), str(baseline), str(output), "cancel", str(image)], check=True)
            compare(output.read_bytes(), packet.read_bytes(), width, height)
    valid = (temp / "orange/library.avif").read_bytes()
    invalid = [b"", valid[:299], valid[:-1], valid + b"\0", valid + bytes(100001), b"GIF8" + valid[4:], valid[:8] + b"jpeg" + valid[12:]]
    for index, content in enumerate(invalid):
        image = temp / f"invalid-{index}.avif"
        image.write_bytes(content)
        result = subprocess.run([MONO, str(exe), str(image), str(temp / "invalid.rgba")], capture_output=True)
        assert result.returncode == 1 and b"Exception" not in result.stderr, index
    print(f"PASS {len(cases)} reference images: every coefficient exact, cancellation/reuse, {len(invalid)} malformed/unsupported inputs rejected without exceptions")
