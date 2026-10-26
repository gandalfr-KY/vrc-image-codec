# Contributing

This package owns AVIF/JPEG single-image conversion and its authoring tools.
Camera pickup/UI, synchronization, cryptography, URLs and Web storage belong to consumers.
Keep runtime dependencies directed from the consumer to this package.

The application API consists of ImageEncoder and ImageDecoder. Expose operations as methods and
status/results as get-only properties. Keep serialized configuration and implementation references
private; Editor tools access their serialized fields. Quality may be changed for the next operation.
The public low-level component fields are an implementation boundary required by Udon and the fixed
benchmark, not a reason to expose those internals from the application API. Display/UI connections
belong in Samples~, which may depend on the codec; the codec must not depend on its samples or UGUI.
Document byte-array/RenderTexture ownership and lifetime without adding automatic defensive copies.

Validate unknown settings at their public boundary. Keep the coding hot path allocation and operation order stable; loops and explicit buffers are intentional in Udon/GPU code. Avoid single-use wrappers, defensive revalidation of internal data, import aliases, and speculative options. Keep supporting helpers beside their caller. Comments should explain constraints rather than restate code.

Run the portable ClientSim benchmark in Tests~/README.md after runtime or shader edits. Use the actual compiled Udon implementation and GPU; never substitute a native CPU codec for the measured path. Verify every produced image once against FFmpeg and retain timing, binary length and pixel-error results. Validation stays outside measured intervals. These single-run timings include first-use effects; they are not statistical performance claims.

Keep untrusted-input bounds and format validation. Do not repeat intermediate layout checks already owned by Editor configuration. Reuse working buffers, but retain copies needed to upload padded textures or return a precisely sized byte array. Preserve upstream attribution and PATENTS. Do not launch VRChat automatically.

The benchmark definition is fixed unless the user explicitly requests a change. Keep the existing resolution/sampling/quality matrix, timing and binary-length measurements, and one external FFmpeg reconstruction check per output. Do not add source-image PSNR, before/after regression comparisons, new acceptance thresholds or other test changes without an explicit request.
