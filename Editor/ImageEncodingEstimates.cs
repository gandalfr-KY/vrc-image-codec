#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using VrcImageCodec;

namespace VrcAvif.Encoder.Editor
{
internal static class ImageEncodingEstimates
{
    // RTX 3050 / Ryzen 5 5600 / D3D11, 2026-10-26 CodecClient report, Q60.
    // AVIF timings use its Fast-scheduling Full HD measurements, scaled by the
    // existing three-photo ClientSim matrix for resolution/quality differences.
    // JPEG uses the report's 512 and Full HD timings (completion waits dominate).
    // Byte ranges come from those three photos, not capacity or acceptance limits.
    // Rows: AVIF420, AVIF444, JPEG420, JPEG444; then ascending pixels and Q60/70/80.
    private static readonly int[] Pixels = { 256 * 144, 512 * 288, 640 * 360, 1280 * 720, 1920 * 1080 };
    private static readonly float[,] Samples = {
        { 0.284202f, 0.254381f, 3730f, 5661f },       { 0.246879f, 0.265919f, 4533f, 7366f },
        { 0.250441f, 0.277495f, 5234f, 8716f },       { 0.445225f, 0.465421f, 8822f, 12976f },
        { 0.406094f, 0.507768f, 11162f, 17351f },     { 0.408588f, 0.548964f, 13226f, 21306f },
        { 0.546191f, 0.617391f, 11495f, 16774f },     { 0.488145f, 0.67172f, 14833f, 22820f },
        { 0.514283f, 0.728704f, 17672f, 28326f },     { 0.985785f, 1.45527f, 27860f, 38049f },
        { 1.02543f, 1.65182f, 37029f, 53986f },       { 1.10373f, 1.8078f, 44933f, 69032f },
        { 1.4432f, 2.293f, 41714f, 56167f },          { 1.54085f, 2.61814f, 56934f, 79939f },
        { 1.71458f, 2.90406f, 70518f, 103254f },      { 0.303669f, 0.254212f, 5924f, 6964f },
        { 0.238487f, 0.287195f, 7466f, 9437f },       { 0.234499f, 0.301758f, 9183f, 12117f },
        { 0.476994f, 0.49815f, 13881f, 16686f },      { 0.400566f, 0.565427f, 18068f, 22911f },
        { 0.417587f, 0.643214f, 22762f, 29870f },     { 0.508429f, 0.666429f, 18061f, 21851f },
        { 0.482379f, 0.761495f, 23635f, 30191f },     { 0.499901f, 0.871072f, 30066f, 39601f },
        { 1.00234f, 1.61204f, 43792f, 52818f },       { 1.00471f, 1.86779f, 58816f, 72211f },
        { 1.08572f, 2.181f, 75661f, 96155f },         { 1.4178f, 2.5908f, 65760f, 79999f },
        { 1.4786f, 2.95678f, 88671f, 108338f },       { 1.69215f, 3.44611f, 113739f, 142477f },
        { 0.0336f, 0.0332f, 5081f, 6666f },           { 0.0336f, 0.0332f, 5839f, 7806f },
        { 0.0336f, 0.0332f, 7058f, 9643f },           { 0.0336f, 0.0332f, 13859f, 18895f },
        { 0.0336f, 0.0332f, 15987f, 22209f },         { 0.0336f, 0.0332f, 19506f, 27543f },
        { 0.0336301f, 0.0331742f, 19834f, 26662f },   { 0.0336301f, 0.0331742f, 22705f, 31347f },
        { 0.0336301f, 0.0331742f, 27596f, 38984f },   { 0.0338813f, 0.0329589f, 60516f, 78091f },
        { 0.0338813f, 0.0329589f, 68629f, 91471f },   { 0.0338813f, 0.0329589f, 82221f, 113217f },
        { 0.0343f, 0.0326f, 115902f, 145743f },       { 0.0343f, 0.0326f, 129646f, 169034f },
        { 0.0343f, 0.0326f, 151760f, 205664f },       { 0.0338f, 0.033f, 7352f, 8678f },
        { 0.0338f, 0.033f, 8438f, 10037f },           { 0.0338f, 0.033f, 10178f, 12279f },
        { 0.0338f, 0.033f, 22541f, 26844f },          { 0.0338f, 0.033f, 25432f, 30918f },
        { 0.0338f, 0.033f, 30472f, 37683f },          { 0.0338086f, 0.0329914f, 32824f, 38597f },
        { 0.0338086f, 0.0329914f, 36723f, 44353f },   { 0.0338086f, 0.0329914f, 43837f, 53852f },
        { 0.0338804f, 0.0329196f, 111044f, 126522f }, { 0.0338804f, 0.0329196f, 122501f, 143248f },
        { 0.0338804f, 0.0329196f, 144683f, 171438f }, { 0.034f, 0.0328f, 226970f, 252303f },
        { 0.034f, 0.0328f, 247664f, 281974f },        { 0.034f, 0.0328f, 287604f, 331363f },
    };

    public static void Draw(ImageEncoder encoder)
    {
        int pixels = encoder.width * encoder.height;
        int upper = 1;
        while (upper < Pixels.Length - 1 && pixels > Pixels[upper])
            upper++;
        float sizeBlend = Mathf.InverseLerp(Pixels[upper - 1], Pixels[upper], pixels);
        float quality = Mathf.Clamp(encoder.quality, 60, 80);
        int qualityIndex = quality < 70 ? 0 : 1;
        float qualityBlend = (quality - (60 + qualityIndex * 10)) / 10;
        int group = (encoder.format == ImageEncodingFormat.AVIF ? 0 : 2) + (encoder.subsampleChroma ? 0 : 1);
        int lowerRow = group * Pixels.Length * 3 + (upper - 1) * 3 + qualityIndex;
        int upperRow = lowerRow + 3;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField(
            new GUIContent(
                "Estimates",
                "Reference: RTX 3050 / Ryzen 5 5600, D3D11, VRChat client. AVIF reference uses Fast " +
                    "scheduling; the default scheduling can take longer. Other settings are interpolated " +
                    "using three-photo ClientSim measurements."),
            EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Encode time",
            "~" + Estimate(lowerRow, upperRow, 0, sizeBlend, qualityBlend).ToString("0.00") + " s");
        EditorGUILayout.LabelField(
            "Decode time",
            "~" + Estimate(lowerRow, upperRow, 1, sizeBlend, qualityBlend).ToString("0.00") + " s");
        EditorGUILayout.LabelField(
            "Output size",
            "~" + (Estimate(lowerRow, upperRow, 2, sizeBlend, qualityBlend) / 1024).ToString("0.#") + "–" +
                (Estimate(lowerRow, upperRow, 3, sizeBlend, qualityBlend) / 1024).ToString("0.#") + " KiB");
        EditorGUILayout.HelpBox(
            "Rough reference only. Image content, hardware and frame rate affect results." +
                (encoder.format == ImageEncodingFormat.AVIF ? " AVIF timing reference uses Fast scheduling."
                                                            : "") +
                (pixels < Pixels[0] || encoder.quality < 60 || encoder.quality > 80
                     ? " Outside the measured range, the nearest reference is shown (256×144–1920×1080, " +
                           "Q60–80)."
                     : ""),
            MessageType.Info);
    }

    private static float Estimate(int lowerRow, int upperRow, int column, float sizeBlend, float qualityBlend)
    {
        return Mathf.Lerp(Mathf.Lerp(Samples[lowerRow, column], Samples[lowerRow + 1, column], qualityBlend),
                          Mathf.Lerp(Samples[upperRow, column], Samples[upperRow + 1, column], qualityBlend),
                          sizeBlend);
    }
}
}
#endif
