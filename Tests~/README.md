# ClientSim benchmark

This is the only codec test suite. It runs the production compiled Udon components on the GPU in Unity ClientSim, then uses FFmpeg to independently decode each produced file exactly once. No VRChat login or client launch is required.

## Requirements

- Windows, D3D11 / shader model 5 GPU; Unity 2022.3.22f1 or the SDK-supported 2022.3 version.
- A disposable Worlds SDK 3.10.5+ project with ClientSim and this codec installed. Do not use your authoring scene/project for unattended benchmarks.
- Python 3.10+ (standard library only) and FFmpeg with AV1 decoding available on PATH. `--ffmpeg <executable>` selects another installation.

From this repository:

```sh
python Tools~/benchmark.py run "<unity-project>" --unity "<Unity-executable>"
```

Close that project in Unity first. This prepares the fixtures, launches a graphics-enabled batch Editor, waits for ClientSim, and verifies the results. It does not launch VRChat. The following separate steps are also available for inspecting the run in the Editor:

```sh
python Tools~/benchmark.py prepare "<unity-project>"
```

Open that Unity project, wait for compilation, then choose **Tools → Image Codec → Run benchmark (ClientSim)**. The runner creates a temporary benchmark scene and restores the previous active scene and ClientSim settings when finished. Results are under `<unity-project>/Logs/CodecBenchmark`.

For unattended execution, close that project in Unity first:

```sh
"<Unity-executable>" -batchmode -force-d3d11 -projectPath "<unity-project>" -executeMethod CodecBenchmarkSetup.Run -logFile "<log-file>"
```

Do not pass `-quit` (execution is asynchronous) or `-nographics` (these are GPU codecs). The runner exits the batch Editor after restoring the scene/settings. Finally:

```sh
python Tools~/benchmark.py verify "<unity-project>"
```

The verifier writes `results.json`, `results.csv` and a readable `results.md` summary, including machine/Unity/graphics information, seconds, frame counts, exact binary bytes, and RGB maximum/RMS error. Any missing case, unexpected codec error, independent decode failure or pixel mismatch fails verification. No codec is executed in hosted Linux CI; its workflow only checks the Python tooling.

## Matrix and interpretation

180 outputs: **3 photographs × 5 resolutions × 2 codecs × 2 samplings × 3 quality settings**.

- Images: `library.png`, `bar.png`, `lounge.png`, bundled snapshots from the author's world. No network download or local source path is needed.
- Resolution: 256×144, 512×288, 640×360, 1280×720, 1920×1080.
- Codec: AVIF and JPEG; chroma: 4:2:0 and 4:4:4; quality: 60, 70, 80.
- AVIF scheduling: Fast; requested frame rate: 60. Actual elapsed time includes allocation, GPU work and completion readbacks. Each case runs once; first-use costs are included. Values are not medians or statistical confidence intervals.
- JPEG and AVIF quality numbers are their own codec scales, not equivalent visual quality.
- Binary length excludes URLs, encryption and padding. The benchmark does not exercise the sharing product.

Scene groups keep only one configured pair per codec at a time. Fixture loading and resize submission occur before timing. File writes, validation readbacks and external decoding are outside the measured encode/decode intervals; completion latency can include previously queued GPU work. Timing ends when the real production component reports completion after its final GPU fence.

Pixels are checked against one FFmpeg YUV decode using the API's full-range BT.601 conversion and nearest chroma sampling. Unity readback rows are bottom-first, so the comparison flips rows once. Tolerance: maximum 3/255 and RMS 1.5/255, accounting for GPU inverse-transform/color rounding. This verifies dimensions, orientation and reconstructed pixels, not bit-identical output from different encoders. There is no fixed byte-size rejection threshold; output buffers grow to the generated size.

The replaced suites' native CPU oracle, frozen-CDF/experimental variants, fixed output-byte goldens and host-specific compiler paths are removed. This matrix is a round-trip correctness/performance benchmark, not an exhaustive malformed-input/security test suite; production input validation remains.
