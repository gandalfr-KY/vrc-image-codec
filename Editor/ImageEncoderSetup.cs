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

    public static string InputError(RenderTexture input)
    {
        if (!input) return "";
        if (input.width < 16 || input.height < 16)
            return "Set both dimensions of the input RenderTexture to at least 16 pixels.";
        if (input.width % 8 != 0 || input.height % 8 != 0)
            return "Set both dimensions of the input RenderTexture to multiples of 8.";
        return "";
    }

    public static Vector2Int[] Resolutions(RenderTexture input)
    {
        if (!input || InputError(input) != "") return new Vector2Int[0];
        int a = input.width, b = input.height;
        while (b != 0)
        {
            int remainder = a % b;
            a = b;
            b = remainder;
        }
        // Both formats use the same authoring grid while preserving the input ratio exactly.
        int unitWidth = input.width / a * 8;
        int unitHeight = input.height / a * 8;
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
            if (scale > 0 && Mathf.Min(size.x, size.y) >= 16 && !sizes.Contains(size)) sizes.Add(size);
        }
        var maximum = new Vector2Int(unitWidth * limit, unitHeight * limit);
        if (Mathf.Min(maximum.x, maximum.y) >= 16 && !sizes.Contains(maximum)) sizes.Add(maximum);
        return sizes.ToArray();
    }

    public static void Configure(ImageEncoder encoder)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || encoder.GetConfigurationError() != "") return;
        Undo.RecordObject(encoder, "Configure image encoder");
        var settings = new SerializedObject(encoder);
        var avif = settings.FindProperty("avifEncoder").objectReferenceValue as AvifEncoder;
        var jpeg = settings.FindProperty("jpegEncoder").objectReferenceValue as JpegEncoder;
        if (encoder.Format == ImageEncodingFormat.AVIF)
        {
            if (!avif)
            {
                AvifEncoderSetup.Create();
                avif = Selection.activeGameObject.GetComponent<AvifEncoder>();
                avif.transform.SetParent(encoder.transform, false);
                settings.FindProperty("avifEncoder").objectReferenceValue = avif;
            }
            if (avif.width != encoder.Width || avif.height != encoder.Height ||
                avif.subsampleChroma != encoder.SubsampleChroma || !avif.gpuTransform.materialSource ||
                !avif.gpuEntropy.materialSource)
                AvifEncoderSetup.Configure(avif, encoder.Width, encoder.Height, encoder.Quality,
                                           AvifProcessingLoad.Normal, encoder.SubsampleChroma);
            avif.quality = encoder.Quality;
            avif.gameObject.hideFlags = HideFlags.HideInHierarchy;
            avif.gameObject.SetActive(true);
            UdonSharpEditorUtility.CopyProxyToUdon(avif);
        }
        else
        {
            if (!jpeg)
            {
                AvifEncoderSetup.EnsurePrograms(typeof(JpegEncoder));
                var implementation = new GameObject("JPEG encoder");
                Undo.RegisterCreatedObjectUndo(implementation, "Create JPEG encoder");
                implementation.transform.SetParent(encoder.transform, false);
                jpeg = implementation.AddUdonSharpComponent<JpegEncoder>();
                settings.FindProperty("jpegEncoder").objectReferenceValue = jpeg;
            }
            CodecAssetOwnership.DetachSharedAssets(jpeg, "Assets/ImageCodecGenerated");
            JpegCodecSetup.Configure(jpeg);
            var texture = jpeg.inputTexture as RenderTexture;
            if (!texture)
            {
                const string directory = "Assets/ImageCodecGenerated";
                Directory.CreateDirectory(directory);
                texture = new RenderTexture(encoder.Width, encoder.Height, 0, RenderTextureFormat.ARGBHalf,
                                            RenderTextureReadWrite.Linear);
                texture.name = "Encoding input";
                AssetDatabase.CreateAsset(
                    texture, AssetDatabase.GenerateUniqueAssetPath(directory + "/Input.renderTexture"));
            }
            if (texture.width != encoder.Width || texture.height != encoder.Height)
            {
                texture.Release();
                texture.width = encoder.Width;
                texture.height = encoder.Height;
            }
            texture.filterMode = FilterMode.Bilinear;
            jpeg.inputTexture = texture;
            jpeg.width = encoder.Width;
            jpeg.height = encoder.Height;
            jpeg.quality = encoder.Quality;
            jpeg.subsampleChroma = encoder.SubsampleChroma;
            jpeg.gameObject.hideFlags = HideFlags.HideInHierarchy;
            jpeg.gameObject.SetActive(true);
            EditorUtility.SetDirty(texture);
            UdonSharpEditorUtility.CopyProxyToUdon(jpeg);
        }
        if (avif) avif.gameObject.SetActive(encoder.Format == ImageEncodingFormat.AVIF);
        if (jpeg) jpeg.gameObject.SetActive(encoder.Format == ImageEncodingFormat.JPEG);
        settings.ApplyModifiedProperties();
        UdonSharpEditorUtility.CopyProxyToUdon(encoder);
        EditorUtility.SetDirty(encoder);
        EditorSceneManager.MarkSceneDirty(encoder.gameObject.scene);
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = encoder.gameObject;
    }

    // Editor integrations use the same serialized settings as the Inspector, then generate resources.
    public static void Configure(ImageEncoder encoder, RenderTexture input, ImageEncodingFormat format,
                                 int width, int height, int quality = 60, bool subsampleChroma = true)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var settings = new SerializedObject(encoder);
        settings.FindProperty("inputTexture").objectReferenceValue = input;
        settings.FindProperty("format").intValue = (int)format;
        settings.FindProperty("width").intValue = width;
        settings.FindProperty("height").intValue = height;
        settings.FindProperty("quality").intValue = quality;
        settings.FindProperty("subsampleChroma").boolValue = subsampleChroma;
        settings.ApplyModifiedProperties();
        Configure(encoder);
    }

    public static bool NeedsConfiguration(ImageEncoder encoder)
    {
        var settings = new SerializedObject(encoder);
        if (encoder.Format == ImageEncodingFormat.AVIF)
        {
            var avif = settings.FindProperty("avifEncoder").objectReferenceValue as AvifEncoder;
            return !avif || avif.width != encoder.Width || avif.height != encoder.Height ||
                   avif.subsampleChroma != encoder.SubsampleChroma || !avif.gpuTransform.materialSource ||
                   !avif.gpuEntropy.materialSource;
        }
        var jpeg = settings.FindProperty("jpegEncoder").objectReferenceValue as JpegEncoder;
        return !jpeg || jpeg.width != encoder.Width || jpeg.height != encoder.Height ||
               jpeg.subsampleChroma != encoder.SubsampleChroma || !jpeg.materialSource;
    }
}
}
#endif
