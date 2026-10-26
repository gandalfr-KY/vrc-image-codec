#if UNITY_EDITOR
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UdonSharpEditor;
using VrcImageCodec;

namespace VrcAvif.Encoder.Editor
{
public static class ImageEncoderSetup
{
    [MenuItem("Tools/Image Codec/Create encoder")]
    public static void Create()
    {
        AvifEncoderSetup.EnsurePrograms(typeof(ImageEncoder));
        var root = new GameObject("Image encoder");
        Undo.RegisterCreatedObjectUndo(root, "Create image encoder");
        root.AddUdonSharpComponent<ImageEncoder>();
        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(root.scene);
    }

    public static Vector2Int[] Resolutions(RenderTexture input, ImageEncodingFormat format)
    {
        if (!input) return new Vector2Int[0];
        int a = input.width, b = input.height;
        while (b != 0)
        {
            int remainder = a % b;
            a = b;
            b = remainder;
        }
        // The reduced ratio is integral; multiplying both sides by 8 satisfies the AVIF profile.
        int unitWidth = input.width / a * (format == ImageEncodingFormat.AVIF ? 8 : 1);
        int unitHeight = input.height / a * (format == ImageEncodingFormat.AVIF ? 8 : 1);
        int limit = Mathf.Min(
            Mathf.Min(input.width / unitWidth, input.height / unitHeight),
            Mathf.Min(ImageEncoder.MaximumDimension / unitWidth, ImageEncoder.MaximumDimension / unitHeight));
        if (limit < 1) return new Vector2Int[0];
        limit = Mathf.Min(limit, Mathf.FloorToInt(Mathf.Sqrt((float)ImageEncoder.MaximumPixels /
                                                             (unitWidth * unitHeight))));
        var sizes = new List<Vector2Int>();
        int unitLongEdge = Mathf.Max(unitWidth, unitHeight);
        foreach (int longEdge in new[] { 256, 512, 640, 1280, 1920 })
        {
            int scale = Mathf.Min(longEdge / unitLongEdge, limit);
            var size = new Vector2Int(unitWidth * scale, unitHeight * scale);
            if (scale > 0 && (format != ImageEncodingFormat.AVIF || Mathf.Min(size.x, size.y) >= 16) &&
                !sizes.Contains(size))
                sizes.Add(size);
        }
        var maximum = new Vector2Int(unitWidth * limit, unitHeight * limit);
        if ((format != ImageEncodingFormat.AVIF || Mathf.Min(maximum.x, maximum.y) >= 16) &&
            !sizes.Contains(maximum))
            sizes.Add(maximum);
        return sizes.ToArray();
    }

    public static void Configure(ImageEncoder encoder)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || encoder.ConfigurationError() != "") return;
        Undo.RecordObject(encoder, "Configure image encoder");
        if (encoder.format == ImageEncodingFormat.AVIF)
        {
            if (!encoder.avifEncoder)
            {
                AvifEncoderSetup.Create();
                encoder.avifEncoder = Selection.activeGameObject.GetComponent<AvifEncoder>();
                encoder.avifEncoder.transform.SetParent(encoder.transform, false);
            }
            var avif = encoder.avifEncoder;
            if (avif.width != encoder.width || avif.height != encoder.height ||
                avif.subsampleChroma != encoder.subsampleChroma)
                AvifEncoderSetup.Configure(avif, encoder.width, encoder.height, encoder.quality,
                                           AvifProcessingLoad.Normal, encoder.subsampleChroma);
            avif.quality = encoder.quality;
            avif.gameObject.hideFlags = HideFlags.HideInHierarchy;
            avif.gameObject.SetActive(true);
            UdonSharpEditorUtility.CopyProxyToUdon(avif);
        }
        else
        {
            if (!encoder.jpegEncoder)
            {
                AvifEncoderSetup.EnsurePrograms(typeof(JpegEncoder));
                var implementation = new GameObject("JPEG encoder");
                Undo.RegisterCreatedObjectUndo(implementation, "Create JPEG encoder");
                implementation.transform.SetParent(encoder.transform, false);
                encoder.jpegEncoder = implementation.AddUdonSharpComponent<JpegEncoder>();
                JpegCodecSetup.Configure(encoder.jpegEncoder);
            }
            var jpeg = encoder.jpegEncoder;
            var texture = jpeg.inputTexture as RenderTexture;
            if (!texture)
            {
                const string directory = "Assets/ImageCodecGenerated";
                Directory.CreateDirectory(directory);
                texture = new RenderTexture(encoder.width, encoder.height, 0, RenderTextureFormat.ARGBHalf,
                                            RenderTextureReadWrite.Linear);
                texture.name = "Encoding input";
                AssetDatabase.CreateAsset(
                    texture, AssetDatabase.GenerateUniqueAssetPath(directory + "/Input.renderTexture"));
            }
            if (texture.width != encoder.width || texture.height != encoder.height)
            {
                texture.Release();
                texture.width = encoder.width;
                texture.height = encoder.height;
            }
            texture.filterMode = FilterMode.Bilinear;
            jpeg.inputTexture = texture;
            jpeg.width = encoder.width;
            jpeg.height = encoder.height;
            jpeg.quality = encoder.quality;
            jpeg.subsampleChroma = encoder.subsampleChroma;
            jpeg.gameObject.hideFlags = HideFlags.HideInHierarchy;
            jpeg.gameObject.SetActive(true);
            EditorUtility.SetDirty(texture);
            UdonSharpEditorUtility.CopyProxyToUdon(jpeg);
        }
        if (encoder.avifEncoder)
            encoder.avifEncoder.gameObject.SetActive(encoder.format == ImageEncodingFormat.AVIF);
        if (encoder.jpegEncoder)
            encoder.jpegEncoder.gameObject.SetActive(encoder.format == ImageEncodingFormat.JPEG);
        UdonSharpEditorUtility.CopyProxyToUdon(encoder);
        EditorUtility.SetDirty(encoder);
        EditorSceneManager.MarkSceneDirty(encoder.gameObject.scene);
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = encoder.gameObject;
    }
}
}
#endif
