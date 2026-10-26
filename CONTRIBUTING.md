# Contributing

This package owns AVIF/JPEG single-image conversion and its authoring tools.
Camera pickup/UI, synchronization, cryptography, URLs and Web storage belong to consumers.
Keep runtime dependencies directed from the consumer to this package.

Validate unknown settings at their public boundary. Keep the coding hot path allocation and operation order stable; loops and explicit buffers are intentional in Udon/GPU code. Avoid single-use wrappers, defensive revalidation of internal data, import aliases, and speculative options. Keep supporting helpers beside their caller. Comments should explain constraints rather than restate code.

Run the portable ClientSim benchmark in Tests~/README.md after runtime or shader edits. Use the actual compiled Udon implementation and GPU; never substitute a native CPU codec for the measured path. Verify every produced image once against FFmpeg and retain timing, binary length and pixel-error results. Validation stays outside measured intervals. These single-run timings include first-use effects; they are not statistical performance claims.

Keep untrusted-input bounds and format validation. Do not repeat intermediate layout checks already owned by Editor configuration. Reuse working buffers, but retain copies needed to upload padded textures or return a precisely sized byte array. Preserve upstream attribution and PATENTS. Do not launch VRChat automatically.
