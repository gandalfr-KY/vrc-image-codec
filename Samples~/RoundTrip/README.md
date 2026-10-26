# Encode, decode and display

1. Import this sample alongside the codec package.
2. Select an image in Project (optional; otherwise a test pattern is generated).
3. Choose **Tools > Image Codec > Create round-trip sample**.
4. Enter Play mode and press **Encode & Decode**.

The left image is the input. The right image is restored from the compressed bytes. The status shows the actual byte count. Change format, sampling, resolution and quality on Image encoder before entering Play mode.

Select **Run codec** to inspect Image Codec Link. Its only connection fields are Encoder, Decoder and Display; drag components from Hierarchy to change them. The button event is wired automatically. Image Codec Link handles compression, byte-array transfer, decoding and display. `CodecRoundTrip.cs` only prepares the source image and displays status; it contains no transfer logic.

The sample creates a Canvas, button, view camera and a world descriptor if needed. It does not replace an existing scene. Its input texture is 640×360; use a 16:9 source or supply your own appropriate RenderTexture for another ratio. Results stay in memory and are not saved as files automatically.

ImageCodecLink is sample code in `VrcImageCodec.Samples`, not a runtime dependency of the codec. Its data flow uses the public read-only properties and `TryBeginDecoding(bytes)`. See the package README for the supported API, cancellation, and result ownership.
