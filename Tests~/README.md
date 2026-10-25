# Codec verification

Run `python Tools~/check-codec.py`, `python Tools~/check-decoder.py` and, on Windows, `python Tools~/check-shaders.py` from the package root. Configure `CSC` and `MONO` if Unity's C# tools are installed elsewhere.

For real Udon/GPU validation, copy `DecoderTrial.cs` to an isolated Unity project's `Assets/Editor`, with the codec and Worlds SDK installed. It remains inactive until explicitly requested and does not launch VRChat or enter Play Mode.

Create `Logs/AvifDecoder/jobs.json` with `{"jobs":[{"id":"sample","file":"absolute/path/to/sample.avif"}]}`. Use this encoder's AVIF output. Write `run` to `Logs/AvifDecoder/run.request`. The trial executes compiled Udon, saves a reconstructed YUV atlas and PNG, and records timings in `results.json`. It runs steps continuously, excluding normal frame scheduling waits; its final readback is for validation only. Check `done.txt` or `error.txt`.

Run `python Tools~/check-decoder-gpu.py /path/to/UnityProject/Logs/AvifDecoder` to compare all YUV samples with FFmpeg. RGB upsampling/color conversion is distinct from this YUV equality check. Driver timings include the test-only final GPU readback.
