# Production encoder ClientSim trial

Use `Tools~/check-codec.py <UnityProject> --prepare`, refresh Unity, then use `--run`. See Tests~/README.md for the complete workflow. Save the scene first. The suite creates a separate scene and runs the actual production Udon/GPU implementation. Its native observer supplies fixtures and compares output only. It does not log in, build, upload or launch VRChat.

The first fixture is cancelled during readback and then reused. Subsequent tests compare all AVIF bytes with stored references. A final full capture includes the transform and AVIF assembly. `done.txt` indicates success; `error.txt` records a failed check. `stopped.txt` confirms that Play ended and settings/scene were restored.

The 60fps cap and Fast GPU scheduling are fixed for this test. Reported elapsed times are ClientSim measurements, not VRChat client measurements. Actual FPS is recorded through frame intervals. Avoid interacting with Unity during timing.

The decoder has a separate GPU reconstruction trial documented in Tests~/README.md. Tests~ is excluded from the installed runtime package.

The trial retains the encoder's camera-input `flipSourceY=true`. A PNG supplied directly is therefore vertically flipped in the encoded image. This still provides a consistent corpus for scheduling comparisons; set `encoder.gpuTransform.flipSourceY=false` in the setup before copying the proxy if testing the original PNG orientation instead. This setting does not change decoder orientation rules.

The observer stops ClientSim automatically and restores its settings and the previously open scene. A new run resets its result/status files; copy them elsewhere first if needed. Tests are one scene per setting, not a statistical benchmark.
