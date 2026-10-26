# VRC AVIF and JPEG Codec

Independent single-image AVIF/JPEG encoders and matching decoders for Unity 2022.3 / VRChat Worlds SDK 3.10.5 or later. BSD-2-Clause; upstream attribution and the AOM patent grant are in ThirdParty/AV1 and PATENTS.

## Install

Import the generated unitypackage, or put this package in `Packages/com.gandalfr.vrc-avif`. Use **Tools → Image Codec → Create encoder**. The tool creates the component, compiled Udon programs and working RenderTextures. The VPM archive contains the same source; no native plug-in is required. VCC listing publication is a separate release step.

## Encoder settings

The Image Encoder Inspector exposes only format (AVIF/JPEG), chroma sampling (4:2:0/4:4:4), input RenderTexture, resolution and quality (default 60). Implementation components and working resources are configured automatically. Configure outside Play mode. The SDK Utilities foldout is omitted; compilation errors remain visible. Read-only encode/decode time and output-size estimates update with the selected settings. The Inspector shows coarse, single approximate values rather than ranges; detailed measurement assumptions remain in this documentation. Estimates use the 2026-10-26 RTX 3050 client measurements and the three-photo ClientSim matrix; AVIF timing is anchored to Fast scheduling, so the default scheduling can take longer. Resolution and Q60–80 are interpolated; outside the measured range the nearest reference is shown. These are indicative values, not performance or size guarantees.

Resolution is a dropdown of width × height pairs that preserve the input aspect ratio exactly. Candidates never exceed the input dimensions or the supported 1920-pixel side / Full HD pixel-count limits. Common long-edge presets and the largest compatible size are offered; Both formats use eight-pixel alignment in the settings UI. No stretching or cropping is applied. For JPEG and AVIF authoring, both input dimensions must be at least 16 pixels and multiples of eight; the Inspector explains how to correct invalid inputs. If only one resolution is available, it recommends a larger input with a standard aspect ratio such as 16:9, 4:3 or 1:1. This is a warning and does not prevent encoding.

Call `ImageEncoder.BeginEncoding()` and observe `busy`, `complete`, `error` and `resultBytes`. Call `CancelEncoding()` to cancel. The supplied RenderTexture is resized into an internal snapshot before compression. After cancellation, wait for `readbackPending` to clear before restarting. The format-specific APIs below remain available for integrations that already configure their own resources.

## Connect components without writing code

Create an encoder and decoder with **Tools → Image Codec**, then add **Image Codec Link** to a UI Button (or create a standalone link through the same menu). Drag the Image Encoder into **Encoder**, the Image Decoder into **Decoder**, and your output RawImage into **Display**. A UI Button on the same object is connected automatically after those references are assigned. For a world object, add a collider and use it to invoke the link. In Play mode the Inspector also provides an **Encode & Decode** button and status.

The link starts encoding, passes each completed byte array to the decoder without copying, and assigns the decoded RenderTexture to Display. It also forwards results when another component starts the encoder. Use one link per encoder/decoder pair; the link owns that decoder while processing. The encoder input must already contain the image you want to convert. No binary-string object, URL or file is required.

## Unified decoder and data transfer

Use **Tools → Image Codec → Create decoder**. `ImageDecoder` detects JPEG/AVIF from the bytes; there is no format, sampling or resolution setting. It delegates to the existing restricted-profile codecs, so their supported-format limits still apply.

After `ImageEncoder.complete` becomes true:

```csharp
decoder.inputBytes = encoder.resultBytes;
decoder.BeginDecoding();
```

Wait for `decoder.complete`, then assign `decoder.resultTexture` to a RawImage or material. Observe `busy`, `error` and `readbackPending`; cancel with `CancelDecoding()`. The transfer shares the byte array without copying. Keep input bytes unchanged during decoding. The decoder owns and reuses the result texture; use a GPU blit if an earlier result must be retained. Neither operation writes a file automatically.

### Round-trip sample

For a unitypackage install, also import the matching `-sample.unitypackage`. For a package install, import **Encode, decode and display** from Package Manager Samples (or copy `Samples~/RoundTrip` into Assets).

Select a photograph in the Project window, then use **Tools → Image Codec → Create round-trip sample**. With no selected photograph, the tool creates a portable gradient/checker pattern. Enter Play mode and click **Encode & Decode**: the left image is the input; the right is the decoded result. The status displays Encoding, Decoding, then the actual byte count. The sample uses the same Image Codec Link component. Its small `CodecRoundTrip` script only prepares the source image and updates the status label. Its child Image encoder exposes the five codec settings. No server, URL, camera model or native plugin is needed.

## Format-specific API

Both components advance automatically in their own `Update`. Call Begin once and observe completion; do not also call Step every Update. Step is exposed for a caller that deliberately owns scheduling instead of the component's Update.

| Operation | Encoder | Decoder |
| --- | --- | --- |
| Component | `AvifEncoder` | `AvifDecoder` |
| Input | `gpuTransform.inputTexture` | `inputAvif` (`byte[]`) |
| Start | `BeginEncoding()` | `BeginDecoding()` |
| Explicit step | `StepEncoding()` | `StepDecoding()` |
| Cancel | `CancelEncoding()` | `CancelDecoding()` |
| Result | `resultAvif` (`byte[]`) | `resultTexture` (`RenderTexture`) |
| State | `busy`, `complete`, `error`, `progress` | `busy`, `complete`, `error`, `progress` |

Render into `encoder.gpuTransform.inputTexture`, then call `encoder.BeginEncoding()`. When `complete` is true, pass `encoder.resultAvif` to `decoder.inputAvif` and call `decoder.BeginDecoding()`. When decoding completes, assign `decoder.resultTexture` to your display material. Create a configured decoder using **Tools → Image Codec → Create decoder**. Check `error` when an operation ends without completion.

Keep input bytes unchanged while decoding. The decoder owns its output RenderTexture and reuses it on the next decode; copy it with a GPU blit if a previous image must be retained. Cancel clears the public result/state but keeps GPU allocations for reuse. After cancelling an encode, wait until `gpuEntropy.readbackPending` is false before starting again; for decoding, wait for `decoder.readbackPending == false`. All state is local.

Editor code can call `VrcAvif.Encoder.Editor.AvifEncoderSetup.Configure(encoder, width, height, quality, load)`. Settings are validated there. Width and height must be multiples of eight, 16–1920, with at most 2,073,600 pixels. Default: 640×360, quality 60, 4:2:0. The optional final `subsampleChroma` argument selects 4:4:4 when false. Quality changes coding quality; load changes scheduling only. There is no fixed byte-size cap; output buffers are allocated after reading the exact GPU-generated length.

The matching decoder scheduling API is `AvifEncoderSetup.Configure(decoder, load)`. Both accept `AvifProcessingLoad.Gentle`, `Normal` and `Fast`. Encoder GPU diagonals per frame are 2/4/8; decoder reconstruction diagonals are 4/8/16, with soft decoder CPU budgets of 1/2/4 ms. `Fast` reduces frame scheduling waits at a higher per-frame cost; it does not change quality or the AVIF bitstream. Normal remains the default. There is no CPU encoding budget because encoding no longer traverses coefficients on Udon.

Adaptive CDF updates are always enabled. The experimental frozen-CDF encoder option and CPU entropy-coder path are removed.

The encoder GPU performs prediction, transform, quantization, coefficient traversal, syntax generation, all CDF updates, range coding and AVIF assembly. `AvifEncoder` coordinates the transform and `AvifGpuEntropy`; the latter owns intermediate textures and asynchronous readbacks. There is no coefficient readback or CPU-to-GPU symbol upload. A 4-byte syntax count sizes intermediate textures, then a 4-byte encoded length sizes the output; the final readback returns the complete AVIF in a padded buffer. Udon allocates the result byte array and copies the completed bytes. No guessed initial byte capacity or overflow retry is used. Byte output textures use up to 1024 RGBA pixels per row; only the last row is padded, and the width is passed to the shader. Fixed probability tables and container templates are generated by the Editor during configuration, not per photograph. Quality changes are applied to the AVIF header on the GPU.

The decoder parses the restricted container/frame headers on Udon and uploads the AVIF once. GPU geometry/fragment passes decode range symbols, update adaptive CDFs and emit coefficients in 16-block checkpoints. Ping-pong floating-point textures retain range/CDF/neighbour state without CPU readback. Prediction, inverse transforms, CDEF and RGB conversion also run on GPU. A small status readback rejects malformed entropy before reconstruction; a final four-byte readback fences RGB completion before publishing the output. `frameBudgetMs` limits CPU command-submission time, not GPU execution time; `diagonalsPerFrame` schedules reconstruction. Both codecs require PC shader model 5.0 (validated on D3D11) and floating-point RenderTextures. The AVIF decoder additionally uses geometry-shader instancing; Quest/mobile is unsupported. Range checkpoints use a two-dimensional atlas rather than assuming a 16,384-pixel device width. Integers in byte streams are packed explicitly, without host-endian or native pointer casts. Other graphics APIs and CPU architectures have not been validated.

For an existing encoder, run `AvifEncoderSetup.Configure(...)` once after upgrading. It adds/configures `gpuEntropy`, its three ordinary shaders and initial/header textures. Existing script GUIDs and the documented Begin/Step/Cancel/result API are retained. Intermediate transform fields use descriptive texture names with Unity serialization migration attributes. Reconfigure after upgrading. Diagnostic encoder fields `frameBudgetMs`, `entropySeconds`, `modeCounts` and the transform's old coefficient `result`/`readbackPending` are removed; consumers waiting for encoding readbacks should use `gpuEntropy.readbackPending`.

AVIF sampling is configured by the last `subsampleChroma` argument of `AvifEncoderSetup.Configure` (default true, 4:2:0). Reconfigure generated textures when changing sampling. Current 4:4:4 encoding uses DC chroma prediction. `CodecBuffers` shares texture lifecycle handling and `CodecCommon.hlsl` shares byte packing/color helpers; format-specific transforms and entropy coding remain separate.

The decoder accepts this encoder's restricted still-image profile, not general AVIF from arbitrary software: 8-bit SDR, 4:2:0 or 4:4:4, fixed 8×8 luma / 4×4 (4:2:0) or 8×8 (4:4:4) chroma transforms, one image, one tile, known headers, at most Full HD. Unsupported headers fail through `error`. Alpha, HDR, animation, other block layouts and arbitrary libaom output are outside this API. RGB output uses standard Unity texture orientation, exact sRGB conversion and nearest chroma upsampling. The benchmark checks reconstructed RGB and orientation against FFmpeg.

There is no automatic codec fallback, network call, URL handling, encryption, synchronization or storage dependency. The package ID and script GUIDs are retained. Codec classes now use neutral names in `VrcImageCodec`; update source references when upgrading.

### Rate-distortion tuning and buffer layout

RDO weights are calculated from the pinned libaom 3.14.1 all-intra PSNR profile (8-bit, sharpness=0, no AQ/delta-Q), rather than a fixed coefficient times the AC quantizer squared. Let `q` be the luma DC dequantizer and `R = floor(q² × (3.3 + 0.0015 × q))`, from `rd.c`. Rate costs are in 1/512-bit units. With raw 4×4/8×8 coefficient SSE, mode selection uses `R × 4 / (128 × 512)`; the factor four compensates for the transform-domain SSE shift in `tx_search.c`. Coefficient optimization uses `round(R × 8 × P / 32) / (128 × 512)`, with `P=17` for Y and `P=13` for UV, matching all-intra `use_chroma_trellis_rd_mult=1` in `txb_rdopt.c`/`encodetxb.h`. Chroma shares the frame's luma-derived multiplier; its separate UV quantizer still controls dequantization.

`ModeRateWeight` and `CoefficientRateWeight` share this calculation across luma, chroma and CfL. Coefficient decisions use the trellis weight; final candidate scores use the mode weight. The luma prediction shortlist estimates coefficient bits with the same CDF-derived cost functions as the final candidates, without the former per-zero/EOB heuristic penalties. This ports the weighting/unit conventions, not the complete libaom mode search: candidate limits, frozen cost estimates, approximate UV/transform syntax costs and this library's restricted transform profile remain. Compression and quality can change; decisions are not claimed identical to libaom.

AVIF encoding sizes its event buffers from the GPU event count and its final output from the GPU's exact encoded length. Decoder upload/packet textures scale with input length and block count. Fixed state sizes describe the shader layout: 4096 CDF contexts with four pixels each, two range/cursor pixels and two neighbour-context pixels per block. A coefficient packet has three plane headers plus 64 Y and two 16/64 U,V coefficient words (99/195 words). The 1024-column atlas layout is packing, not a file-size limit. CDF/range replay checkpoints every 256/2048 symbols are GPU scheduling choices, not input limits.

Editor container/frame headers grow while being written; they have no 600/64-byte scratch reservations. Their four payload-length variants are packed into a texture sized from their actual lengths. Decoder container walking is bounded by input length and positive box sizes, without an arbitrary box-count or 300-byte minimum gate. Bounds, format, GPU failure and cancellation/readback ownership checks remain.

## GPU JPEG API

Use **Tools → Image Codec → Create encoder** and choose JPEG. Use **Tools → Image Codec → Create decoder** for the matching decoder. JPEG is an explicit alternative; it does not change an existing AVIF camera or its Web protocol.

| Operation | Encoder | Decoder |
| --- | --- | --- |
| Component | `JpegEncoder` | `JpegDecoder` |
| Input | `inputTexture` (`Texture`, matching width/height) | `inputJpeg` (`byte[]`) |
| Start | `BeginEncoding()` | `BeginDecoding()` |
| Cancel | `CancelEncoding()` | `CancelDecoding()` |
| Output | `resultJpeg` (`byte[]`) | `resultTexture` (`RenderTexture`) |
| State | `busy`, `complete`, `error`, `progress`, `readbackPending`, `elapsedSeconds` | Same |

Call Begin once; GPU commands and readback callbacks finish the job without a Step/Update loop. Poll `complete`/`error`. While a job is busy, leave its settings, input, and material unchanged. Materials belong to individual components; do not share mutable material instances between concurrent jobs. After Cancel, wait for `readbackPending == false` before restarting. The decoder owns/reuses its result texture; GPU-blit a copy to keep an earlier image.

Encoder settings: width/height 1–1920, at most 2,073,600 pixels, quality 1–100 (default 60), 8-bit SDR YCbCr **4:4:4** by default; set `subsampleChroma = true` for **4:2:0**. Odd dimensions are padded internally and cropped correctly on output. `linearInput` should match the input texture's sampled color space (normally true in a Linear project; false for an uploaded texture containing raw sRGB bytes). `linearOutput` normally matches the project's color space. Decoder output is RGBAHalf for linear output (to preserve dark detail) and RGBA32 for sRGB output. There is no fixed byte-size cap.

The output is a normal JFIF Baseline JPEG with Annex K Huffman tables, interleaved three-component scan and a restart interval of **one MCU (8×8 for 4:4:4, 16×16 for 4:2:0)**. This deliberately trades some compression for independent GPU entropy work. Standard JPEG readers can display it. The matching GPU decoder accepts exactly this header/profile (arbitrary dimensions, quantization tables and entropy payload within that profile), **not arbitrary JPEG**: progressive JPEG, 4:2:2, other marker layouts/tables and JPEGs without restart markers are rejected.

GPU encoder: color conversion/subsampling → separable DCT/quantization → segment byte counts → prefix sum → direct entropy coding into the assembled JPEG → one readback containing the length and JPEG. Working capacity is derived from the MCU count and the baseline JPEG bit bound: at most 16 Huffman bits plus 11 DC amplitude bits, and 16 plus 10 bits for each of 63 AC coefficients per block; segment padding, byte stuffing, restart markers and the container header are included. This avoids a separate length-only completion wait at the cost of transferring unused capacity. The buffer is reused, and only the actual JPEG bytes are copied into the public result array. There is no fixed output-byte cap, overflow retry or alternate API. Texture allocation and byte packing helpers remain shared with AVIF.


GPU decoder: byte upload → header validation → parallel restart-marker indexing → prefix sum → block-start states/Huffman decoding → dequantization/separable IDCT → RGB conversion and validation. CPU reads only width, height and sampling hints to allocate bounded textures; the complete header is still validated on GPU before a result is published. One final status readback completes decoding. Reconstructed pixels remain on GPU. CPU responsibilities are input/result buffer copies, allocations, command submission and completion state. No CPU JPEG image coding or native runtime plugin is used. This follows the restart-segment parallelism described by [GPUJPEG](https://github.com/CESNET/GPUJPEG); CUDA source was not transplanted into Udon.

JPEG needs PC shader model 5.0 and floating-point render targets. Encoding and decoding retain their working textures for reuse (large floating-point coefficient/IDCT buffers plus an encoded-output buffer sized from the actual result; memory depends on resolution and subsampling). First-use allocation/driver compilation can stall frames. A fast completion time does not guarantee a small maximum frame time. Call Cancel to cancel a result; it cannot interrupt GPU work already submitted.

## Minimal camera example

Copy Samples~/MinimalCamera into Assets. Add MinimalAvifCapture alongside the encoder and assign its Camera and encoder fields. Render a frame using Capture(), then wait for the encoder result. The example has no pickup or UI; custom camera models and capture controllers stay outside this library.

## Verification and releases

The single portable benchmark replaces the older fixture/oracle test suites. See [Tests~/README.md](Tests~/README.md). It measures the production compiled-Udon encoder/decoder pair in ClientSim for three bundled world photographs, five resolutions, both chroma samplings and Q60/70/80, then checks each output once against FFmpeg. It writes timing/size/pixel-error results as JSON and CSV. No machine-specific source paths or native C# codec substitutes are used.

`python Tools~/pack.py` creates a VPM package zip and GUID-preserving unitypackage under release/. Tests, build tools and optional samples are excluded from the installed package; the sample is included separately in the zip under Samples~.
