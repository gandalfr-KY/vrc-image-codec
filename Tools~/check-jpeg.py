"""Run/verify the actual GPU JPEG pair as compiled Udon in an isolated ClientSim project."""
import argparse
import json
import math
import random
from pathlib import Path
import shutil
import struct
import subprocess
import zipfile
import zlib

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path)
parser.add_argument("--prepare", action="store_true")
parser.add_argument("--run", action="store_true")
args = parser.parse_args()
project = args.project.resolve()
log = project / "Logs/JpegCodec"
log.mkdir(parents=True, exist_ok=True)

if args.prepare:
    support=project/'Assets/CodecTestSupport';support.mkdir(parents=True,exist_ok=True)
    shutil.copy2(ROOT/'Tests~/ClientSim/CodecBatchPacing.cs',support/'CodecBatchPacing.cs')
    runtime = project / "Assets/JpegCodecTests"
    runtime.mkdir(parents=True, exist_ok=True)
    for name in ["JpegCodecTestSetup.cs", "JpegCodecObserver.cs"]:
        target = project / "Assets/Editor" if name.endswith("Setup.cs") else runtime
        target.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / "Tests~/ClientSim" / name, target / name)
    jobs = []
    with zipfile.ZipFile(ROOT / "Tests~/fhd-fixtures.zip") as archive:
        for scene in ["library", "bar", "lounge"]:
            (log / f"{scene}.avif").write_bytes(archive.read(f"{scene}.avif"))
            subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(log / f"{scene}.avif"), "-frames:v", "1",
                            "-update", "1", str(log / f"{scene}.png")], check=True)
            jobs.append(dict(id=f"fhd-{scene}", path=str(log / f"{scene}.png"), width=1920, height=1080, quality=60))
    def chunk(kind, content):
        return struct.pack(">I", len(content)) + kind + content + struct.pack(">I", zlib.crc32(kind+content))
    pixels = b"".join(b"\0" + bytes(v for x in range(17) for v in [(x*17)%256, (y*23)%256, ((x+y)*13)%256]) for y in range(13))
    (log / "edge.png").write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB",17,13,8,2,0,0,0)) +
                                   chunk(b"IDAT",zlib.compress(pixels)) + chunk(b"IEND",b""))
    jobs = [dict(id=f"edge-{q}", path=str(log / "edge.png"), width=17, height=13, quality=q) for q in [1,60,100]] + jobs
    jobs.insert(3,dict(id="edge-linear", path=str(log / "edge.png"), width=17, height=13, quality=60,linear=True))
    random_source = random.Random(20261026)
    noise = b"".join(b"\0" + random_source.randbytes(256*3) for _ in range(256))
    (log / "noise.png").write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB",256,256,8,2,0,0,0)) +
                                    chunk(b"IDAT",zlib.compress(noise)) + chunk(b"IEND",b""))
    jobs.insert(4,dict(id="noise-100",path=str(log / "noise.png"),width=256,height=256,quality=100))
    jobs += [dict(job,id=job['id']+'-420',subsampleChroma=True) for job in jobs]
    (log / "jobs.json").write_text(json.dumps(dict(jobs=jobs),indent=2))
    print("Prepared. Refresh Unity, then --run after version.txt reads jpeg-gpu-v2.")
    raise SystemExit(0)

if args.run:
    if (log / "version.txt").read_text() != "jpeg-gpu-v2":
        raise SystemExit("Refresh matching Unity tests first.")
    (log / "prepare.request").write_text("run")
    raise SystemExit(0)

if (log / "error.txt").exists():
    raise SystemExit((log / "error.txt").read_text())
if not (log / "done.txt").exists() or not (log / "stopped.txt").exists():
    raise SystemExit("ClientSim must finish and restore its scene/settings.")
results = json.loads((log / "results.json").read_text())
for check in ["cancelledAndReused", "malformedRejected", "invalidSettingsRejected"]:
    if not results[check]:
        raise SystemExit(f"Failed {check}")
jobs = json.loads((log / "jobs.json").read_text())["jobs"]
if [row["id"] for row in results["rows"]] != [job["id"] for job in jobs]:
    raise SystemExit("Incomplete jobs")

def rgb(path, width=0, height=0):
    if width:
        # Decode YUV independently, then use the API's explicit 2x nearest chroma grid.
        # swscale's odd-width resize maps ceil(width/2) to width instead of exact 2x replication.
        raw = subprocess.check_output(["ffmpeg","-v","error","-i",str(path),"-frames:v","1","-pix_fmt","yuvj420p","-f","rawvideo","-"])
        ysize=width*height;cw=(width+1)//2;ch=(height+1)//2;result=bytearray(ysize*3)
        for y in range(height):
            for x in range(width):
                at=y*width+x;c=y//2*cw+x//2;v=raw[at];u=raw[ysize+c]-128;w=raw[ysize+cw*ch+c]-128
                values=(v+1.402*w,v-.344136*u-.714136*w,v+1.772*u)
                for channel,value in enumerate(values):result[at*3+channel]=min(255,max(0,math.floor(value+.5)))
        return bytes(result)
    return subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(path), "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])

for row, job in zip(results["rows"], jobs):
    jpeg = log / (row["id"] + ".jpg")
    reference = rgb(jpeg,job['width'],job['height']) if job.get('subsampleChroma') else rgb(jpeg)
    actual = rgb(log / (row["id"] + ".png"))
    if len(reference) != job["width"] * job["height"] * 3 or len(actual) != len(reference):
        raise SystemExit("Wrong image dimensions")
    errors = [a-b for a,b in zip(actual,reference)]
    maximum = max(map(abs,errors))
    rms = math.sqrt(sum(x*x for x in errors)/len(errors))
    if maximum > 3 or rms > 1:
        raise SystemExit(f"JPEG inverse transform mismatch: {row['id']} max={maximum} rms={rms}")
    source = rgb(Path(job["path"]))
    mse = sum((a-b)**2 for a,b in zip(reference,source))/len(reference)
    row.update(maxRgbDelta=maximum, rmsRgbDelta=rms, psnr=10*math.log10(255**2/mse) if mse else 99)
    print(row["id"], row["bytes"], "bytes", f"encode={row['encodeSeconds']:.3f}s decode={row['decodeSeconds']:.3f}s",
          f"PSNR={row['psnr']:.2f}dB inverse max={maximum} rms={rms:.3f}")
(log / "verified.json").write_text(json.dumps(results,indent=2))
