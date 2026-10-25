# VRC AVIF Codec

Independent single-image AVIF encoder and matching decoder for Unity 2022.3 / VRChat Worlds SDK 3.10.5 or later. BSD-2-Clause; upstream attribution and the AOM patent grant are in ThirdParty/AV1 and PATENTS.

## Install

Import the generated unitypackage, or put this package in `Packages/com.gandalfr.vrc-avif`. Use **Tools → AVIF Encoder → Create encoder**. The tool creates the component, compiled Udon programs and working RenderTextures. The VPM archive contains the same source; no native plug-in is required. VCC listing publication is a separate release step.

## API

Both components advance automatically in their own `Update`. Call Begin once and observe completion; do not also call Step every Update. Step is exposed for a caller that deliberately owns scheduling instead of the component's Update.

| Operation | Encoder | Decoder |
| --- | --- | --- |
| Component | `TernaryAvifEncoder` | `TernaryAvifDecoder` |
| Input | `gpuTransform.inputTexture` | `inputAvif` (`byte[]`) |
| Start | `BeginEncoding()` | `BeginDecoding()` |
| Explicit step | `StepEncoding()` | `StepDecoding()` |
| Cancel | `CancelEncoding()` | `CancelDecoding()` |
| Result | `resultAvif` (`byte[]`) | `resultTexture` (`RenderTexture`) |
| State | `busy`, `complete`, `error`, `progress` | `busy`, `complete`, `error`, `progress` |

Render into `encoder.gpuTransform.inputTexture`, then call `encoder.BeginEncoding()`. When `complete` is true, pass `encoder.resultAvif` to `decoder.inputAvif` and call `decoder.BeginDecoding()`. When decoding completes, assign `decoder.resultTexture` to your display material. Create a configured decoder using **Tools → AVIF Encoder → Create decoder**. Check `error` when an operation ends without completion.

Keep input bytes unchanged while decoding. The decoder owns its output RenderTexture and reuses it on the next decode; copy it with a GPU blit if a previous image must be retained. Cancel clears the public result/state but keeps GPU allocations for reuse. Do not reuse encoder buffers while `gpuTransform.readbackPending` is true. All state is local.

Editor code can call `VrcAvif.Encoder.Editor.AvifEncoderSetup.Configure(encoder, width, height, quality, load)`. Settings are validated there. Width and height must be multiples of eight, 16–1920, with at most 2,073,600 pixels. Default: 640×360, quality 60, 4:2:0. Quality changes coding quality; load changes scheduling only. AVIF output is capped at 100,000 bytes.

The matching decoder scheduling API is `AvifEncoderSetup.Configure(decoder, load)`. Both accept `AvifProcessingLoad.Gentle`, `Normal` and `Fast`, with soft CPU budgets of 1, 2 and 4 ms per frame respectively. Encoder GPU diagonals per frame are 2/4/8; decoder reconstruction diagonals are 4/8/16. `Fast` reduces frame scheduling waits at a higher per-frame cost; it does not change quality or the AVIF bitstream. Normal remains the default. These are time budgets within a frame, not one block per frame.

The encoder GPU performs prediction, transform, quantization and mode selection. One coefficient readback feeds Udon entropy coding and AVIF packaging. The decoder parses headers and entropy-decodes on Udon, uploads packed coefficients once, and reconstructs/predicts/filters on the GPU without a production GPU readback. `frameBudgetMs` is a soft CPU budget; allocation and GPU submission may exceed it. `diagonalsPerFrame` controls decoder reconstruction scheduling.

The decoder accepts this encoder's restricted still-image profile, not general AVIF from arbitrary software: 8-bit SDR, 4:2:0, fixed 8×8 luma / 4×4 chroma transforms, one image, known headers, at most Full HD and 100,000 bytes. Unsupported headers fail through `error`. Alpha, HDR, animation, other block layouts and arbitrary libaom output are outside this API. RGB output is sRGB with nearest chroma upsampling; YUV reconstruction is independently checked against FFmpeg.

There is no JPEG fallback, network call, URL handling, encryption, synchronization or storage dependency. Class names, package ID and existing asset GUIDs are retained so scenes can resolve moved scripts.

## Minimal camera example

Copy Samples~/MinimalCamera into Assets. Add MinimalAvifCapture alongside the encoder and assign its Camera and encoder fields. Render a frame using Capture(), then wait for the encoder result. The example has no pickup or UI; custom camera models and capture controllers stay outside this library.

## Verification and releases

`python Tools~/check-codec.py` replays recorded GPU coefficient fixtures against exact AVIF reference bytes and decodes them independently with FFmpeg. `python Tools~/check-decoder.py` checks production entropy decoding against every recorded coefficient, cancellation/reuse and malformed inputs. They require Mono C#; encoder reference validation also needs FFmpeg. On Windows, `python Tools~/check-shaders.py` compiles all production shader entrypoints with D3DCompiler. These checks do not replace Unity/Udon GPU integration tests.

`python Tools~/pack.py` creates a VPM package zip and GUID-preserving unitypackage under release/. Tests, build tools and optional samples are excluded from the installed package; the sample is included separately in the zip under Samples~.
