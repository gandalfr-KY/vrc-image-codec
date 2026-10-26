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
    // Representative bytes use the midpoint of the three-photo size range, not a limit.
    // Rows: AVIF420, AVIF444, JPEG420, JPEG444; then ascending pixels and Q60/70/80.
    private static readonly int[] Pixels = { 256 * 144, 512 * 288, 640 * 360, 1280 * 720, 1920 * 1080 };
    private static readonly float[,] Samples = {
        { 0.284202f, 0.254381f, 4695.5f },   { 0.246879f, 0.265919f, 5949.5f },
        { 0.250441f, 0.277495f, 6975f },     { 0.445225f, 0.465421f, 10899f },
        { 0.406094f, 0.507768f, 14256.5f },  { 0.408588f, 0.548964f, 17266f },
        { 0.546191f, 0.617391f, 14134.5f },  { 0.488145f, 0.67172f, 18826.5f },
        { 0.514283f, 0.728704f, 22999f },    { 0.985785f, 1.45527f, 32954.5f },
        { 1.02543f, 1.65182f, 45507.5f },    { 1.10373f, 1.8078f, 56982.5f },
        { 1.4432f, 2.293f, 48940.5f },       { 1.54085f, 2.61814f, 68436.5f },
        { 1.71458f, 2.90406f, 86886f },      { 0.303669f, 0.254212f, 6444f },
        { 0.238487f, 0.287195f, 8451.5f },   { 0.234499f, 0.301758f, 10650f },
        { 0.476994f, 0.49815f, 15283.5f },   { 0.400566f, 0.565427f, 20489.5f },
        { 0.417587f, 0.643214f, 26316f },    { 0.508429f, 0.666429f, 19956f },
        { 0.482379f, 0.761495f, 26913f },    { 0.499901f, 0.871072f, 34833.5f },
        { 1.00234f, 1.61204f, 48305f },      { 1.00471f, 1.86779f, 65513.5f },
        { 1.08572f, 2.181f, 85908f },        { 1.4178f, 2.5908f, 72879.5f },
        { 1.4786f, 2.95678f, 98504.5f },     { 1.69215f, 3.44611f, 128108f },
        { 0.0336f, 0.0332f, 5873.5f },       { 0.0336f, 0.0332f, 6822.5f },
        { 0.0336f, 0.0332f, 8350.5f },       { 0.0336f, 0.0332f, 16377f },
        { 0.0336f, 0.0332f, 19098f },        { 0.0336f, 0.0332f, 23524.5f },
        { 0.0336301f, 0.0331742f, 23248f },  { 0.0336301f, 0.0331742f, 27026f },
        { 0.0336301f, 0.0331742f, 33290f },  { 0.0338813f, 0.0329589f, 69303.5f },
        { 0.0338813f, 0.0329589f, 80050f },  { 0.0338813f, 0.0329589f, 97719f },
        { 0.0343f, 0.0326f, 130822f },       { 0.0343f, 0.0326f, 149340f },
        { 0.0343f, 0.0326f, 178712f },       { 0.0338f, 0.033f, 8015f },
        { 0.0338f, 0.033f, 9237.5f },        { 0.0338f, 0.033f, 11228.5f },
        { 0.0338f, 0.033f, 24692.5f },       { 0.0338f, 0.033f, 28175f },
        { 0.0338f, 0.033f, 34077.5f },       { 0.0338086f, 0.0329914f, 35710.5f },
        { 0.0338086f, 0.0329914f, 40538f },  { 0.0338086f, 0.0329914f, 48844.5f },
        { 0.0338804f, 0.0329196f, 118783f }, { 0.0338804f, 0.0329196f, 132874f },
        { 0.0338804f, 0.0329196f, 158060f }, { 0.034f, 0.0328f, 239636f },
        { 0.034f, 0.0328f, 264819f },        { 0.034f, 0.0328f, 309484f },
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
        EditorGUILayout.LabelField("Estimates", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Encode time",
                                   TimeEstimate(Estimate(lowerRow, upperRow, 0, sizeBlend, qualityBlend)));
        EditorGUILayout.LabelField("Decode time",
                                   TimeEstimate(Estimate(lowerRow, upperRow, 1, sizeBlend, qualityBlend)));
        float kibibytes = Estimate(lowerRow, upperRow, 2, sizeBlend, qualityBlend) / 1024;
        float sizeStep = kibibytes < 10 ? 1 : kibibytes < 100 ? 5 : 10;
        EditorGUILayout.LabelField(
            "Output size", "About " + (Mathf.Round(kibibytes / sizeStep) * sizeStep).ToString("0") + " KiB");
        EditorGUILayout.LabelField("Estimates vary with image content and hardware.",
                                   EditorStyles.wordWrappedMiniLabel);
    }

    private static string TimeEstimate(float seconds)
    {
        if (seconds >= 1) return "About " + seconds.ToString("0.#") + " s";
        float step = seconds < 0.1f ? 10 : 100;
        return "About " + (Mathf.Round(seconds * 1000 / step) * step).ToString("0") + " ms";
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
