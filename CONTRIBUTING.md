# Contributing

This package owns only RenderTexture-to-AVIF conversion and its authoring tools.
Camera pickup/UI, synchronization, cryptography, URLs and Web storage belong to consumers.
Keep runtime dependencies directed from the consumer to this package.

Validate unknown settings at their public boundary. Keep the coding hot path allocation and operation order stable; loops and explicit buffers are intentional in Udon/GPU code. Avoid single-use wrappers, defensive revalidation of internal data, import aliases, and speculative options. Keep supporting helpers beside their caller. Comments should explain constraints rather than restate code.

Run Tools~/check-codec.py after runtime edits and Tools~/check-shaders.py after shader edits. Compare exact AVIF bytes, not only successful decoding. Test package import and actual compiled-Udon processing in Unity; do not launch VRChat automatically. Never modify upstream attribution or replace this package with a native codec dependency.
