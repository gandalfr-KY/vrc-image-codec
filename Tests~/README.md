# Codec verification

Run `python Tools~/check-decoder.py` and, on Windows, `python Tools~/check-shaders.py` from the package root. Configure `CSC` and `MONO` if Unity's C# tools are installed elsewhere. Decoder tests use frozen binary reference inputs; no old CPU encoder is retained to generate them.

Encoder tests execute the production GPU implementation in an isolated Unity project with this package and the Worlds SDK installed:

1. Run `python Tools~/check-codec.py <UnityProject> --prepare`.
2. Refresh Unity and wait for `Logs/CodecEncoder/version.txt` to contain `production-gpu-v1`.
3. Run the same command with `--run`. The test enters ClientSim, checks ten exact bitstreams (portrait, Full HD, minimum dimensions/short header and all probability-context families), cancels/reuses the backend, rejects invalid quality, and performs a full image capture. It stops Play and restores the previous scene/settings automatically.
4. Run the command without flags to verify all fixture hashes and independently decode the results with FFmpeg.

The capture uses `Assets/CodecProduction/Source.png`; preparation derives one from a reference photo if absent. An optional `Logs/CodecEncoder/capture.sha256` adds an exact full-capture reference check. Otherwise only fixture rows claim reference equality. The observer saves elapsed time and actual frame intervals in `results.json`; a 60fps cap is not a guarantee of 60fps. Ordinary hosted CI validates decoder code and shader compilation; full encoding requires the Unity/GPU suite above.

## GPU AVIF decoder and sampling

Run `python Tools~/check-avif-decoder.py <isolated-unity-project> --prepare`, refresh Unity, then
`--run`. Run without flags after both `done.txt` and `stopped.txt` appear. Eighteen inputs cover
portrait/Full HD, adaptive/frozen CDFs, all quantizer-context families and 4:2:0/4:4:4. The suite
compares every reconstructed YUV sample with FFmpeg, rejects eight malformed inputs, and cancels
and reuses a component with an outstanding readback. Verification image readbacks are outside timing.

`python Tools~/check-sampling.py <isolated-unity-project> --prepare`, then `--run` and verification,
checks actual encode/decode round trips at small and Full-HD dimensions for both sampling modes.

Tests~/Reference contains a native C# entropy oracle for coefficient-level regression, excluded from
Unity packages. Production decoding uses Shaders/Av1Decode.shader and has no CPU coefficient decoder.

For graphics-enabled batch Editor runs, the test-only CodecBatchPacing helper caps the loop at a
nominal 60fps and exits Play when finished. Actual frame counts and timings are recorded; the cap
is not a guarantee of 60fps. Do not compare batch and graphical Editor timings as identical conditions.

# GPU JPEG pair

`python Tools~/check-jpeg.py <isolated-unity-project> --prepare` installs the test sources and creates
three Full-HD Q60 fixtures plus odd-size Q1/Q60/Q100 and linear-color fixtures. Refresh Unity, request
the compiled-Udon ClientSim run with `--run`, and run without flags after `done.txt` and `stopped.txt`
appear. The runner verifies normal JPEG decoding through FFmpeg, original dimensions/orientation,
GPU-decoded RGB tolerance, boundary rejection, malformed headers/entropy, cancellation and reuse of
both components. Begin/upload/command submission and completion readbacks are timed; native PNG
observation is outside codec time. The setup restores the previous scene and ClientSim settings.
No VRChat client is launched. The suite is opt-in because it requires Unity and a D3D11 GPU.
