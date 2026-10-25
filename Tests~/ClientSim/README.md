# Frame-scheduled ClientSim trial

Use an isolated Unity 2022.3 project with this codec and the Worlds SDK installed. Save the current scene first. This trial creates a separate scene and runs compiled Udon through its normal Update callbacks; its C# observer only measures results. It does not log in, build, upload, or launch VRChat.

1. Copy `CodecRealtimeMonitor.cs` into `Assets/CodecRealtime/` and `CodecRealtimeSetup.cs` into `Assets/Editor/`.
2. Put a representative photo at `Assets/CodecRealtime/Source.png` and refresh Unity.
3. Create `Logs/CodecRealtime/prepare.request` containing `prepare`.

The trial measures Full HD, Q60 at requested 60/30/90fps, each with Normal (2ms) and Fast (4ms) scheduling. It records actual baseline FPS, stage wall time, frame mean/p95/max, codec CPU-step time and Unity Editor memory snapshots in `results.json`. Check `progress.txt`, `done.txt` or `error.txt`. Avoid using the Editor during a measurement.

All AVIF outputs must be byte-identical. Each case saves an AVIF, an RGB PNG and the reconstructed YUV atlas. Validation-only GPU readback happens after the decode timer stops. Input controls are intentionally disabled; a fixed camera is used, including in projects with the legacy input backend. This is a codec trial, not an interaction or real-client test. Unity Editor memory and frame-time outliers cannot be interpreted as standalone VRChat memory or FPS.

The trial retains the encoder's camera-input `flipSourceY=true`. A PNG supplied directly is therefore vertically flipped in the encoded image. This still provides a consistent corpus for scheduling comparisons; set `encoder.gpuTransform.flipSourceY=false` in the setup before copying the proxy if testing the original PNG orientation instead. This setting does not change decoder orientation rules.

The observer stops ClientSim automatically and restores its settings and the previously open scene. A new run resets its result/status files; copy them elsewhere first if needed. Tests are one scene per setting, not a statistical benchmark.
