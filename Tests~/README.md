# Codec verification

Run `python Tools~/check-decoder.py` and, on Windows, `python Tools~/check-shaders.py` from the package root. Configure `CSC` and `MONO` if Unity's C# tools are installed elsewhere. Decoder tests use frozen binary reference inputs; no old CPU encoder is retained to generate them.

Encoder tests execute the production GPU implementation in an isolated Unity project with this package and the Worlds SDK installed:

1. Run `python Tools~/check-codec.py <UnityProject> --prepare`.
2. Refresh Unity and wait for `Logs/CodecEncoder/version.txt` to contain `production-gpu-v1`.
3. Run the same command with `--run`. The test enters ClientSim, checks ten exact bitstreams (portrait, Full HD, minimum dimensions/short header and all probability-context families), cancels/reuses the backend, rejects invalid quality, and performs a full image capture. It stops Play and restores the previous scene/settings automatically.
4. Run the command without flags to verify all fixture hashes and independently decode the results with FFmpeg.

The capture uses `Assets/CodecProduction/Source.png`; preparation derives one from a reference photo if absent. An optional `Logs/CodecEncoder/capture.sha256` adds an exact full-capture reference check. Otherwise only fixture rows claim reference equality. The observer saves elapsed time and actual frame intervals in `results.json`; a 60fps cap is not a guarantee of 60fps. Ordinary hosted CI validates decoder code and shader compilation; full encoding requires the Unity/GPU suite above.

For real Udon/GPU validation, copy `DecoderTrial.cs` to an isolated Unity project's `Assets/Editor`, with the codec and Worlds SDK installed. It remains inactive until explicitly requested and does not launch VRChat or enter Play Mode.

Create `Logs/AvifDecoder/jobs.json` with `{"jobs":[{"id":"sample","file":"absolute/path/to/sample.avif"}]}`. Use this encoder's AVIF output. Write `run` to `Logs/AvifDecoder/run.request`. The trial executes compiled Udon, saves a reconstructed YUV atlas and PNG, and records timings in `results.json`. It runs steps continuously, excluding normal frame scheduling waits; its final readback is for validation only. Check `done.txt` or `error.txt`.

Run `python Tools~/check-decoder-gpu.py /path/to/UnityProject/Logs/AvifDecoder` to compare all YUV samples with FFmpeg. RGB upsampling/color conversion is distinct from this YUV equality check. Driver timings include the test-only final GPU readback.
