# VRC AVIF Encoder

Independent single-image AVIF encoder for Unity 2022.3 / VRChat Worlds SDK 3.10.5 or later. BSD-2-Clause; upstream attribution and the AOM patent grant are in ThirdParty/AV1 and PATENTS.

## Install

Import the generated unitypackage, or put this package in `Packages/com.gandalfr.vrc-avif`. Use **Tools → AVIF Encoder → Create encoder**. The tool creates the component, compiled Udon programs and working RenderTextures. The VPM archive contains the same source; no native plug-in is required. VCC listing publication is a separate release step.

## API

Render into `encoder.gpuTransform.inputTexture`, call `encoder.BeginEncoding()`, then call `encoder.StepEncoding()` each Update while `encoder.busy`. On completion read `encoder.resultAvif`; check `encoder.error` on failure. `CancelEncoding()` cancels work. Do not reuse buffers while `gpuTransform.readbackPending` is true.

Editor code can call `VrcAvif.Encoder.Editor.AvifEncoderSetup.Configure(encoder, width, height, quality, load)`. Settings are validated there. Width and height must be multiples of eight, 16–1920, with at most 2,073,600 pixels. Default: 640×360, quality 60, 4:2:0. Quality changes coding quality; load changes scheduling only. AVIF output is capped at 100,000 bytes.

The GPU performs prediction, transform, quantization and mode selection. One coefficient readback feeds Udon entropy coding and AVIF packaging. There is no JPEG fallback, network call, URL handling, encryption, synchronization or storage dependency. Class names and asset GUIDs are retained so existing scenes can resolve moved scripts.

## Minimal camera example

Copy Samples~/MinimalCamera into Assets. Add MinimalAvifCapture alongside the encoder and assign its Camera and encoder fields. Render a frame using Capture(), then wait for the encoder result. The example has no pickup or UI; custom camera models and capture controllers stay outside this library.

## Verification and releases

`python Tools~/check-codec.py` replays recorded GPU coefficient fixtures against exact AVIF reference bytes and decodes them independently with FFmpeg. It requires Mono C# and FFmpeg. On Windows, `python Tools~/check-shaders.py` compiles the production fragment shaders with D3DCompiler. These checks do not replace Unity/Udon GPU integration tests.

`python Tools~/pack.py` creates a VPM package zip and GUID-preserving unitypackage under release/. Tests, build tools and optional samples are excluded from the installed package; the sample is included separately in the zip under Samples~.
