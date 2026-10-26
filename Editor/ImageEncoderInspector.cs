#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UdonSharpEditor;
using VrcImageCodec;

namespace VrcAvif.Encoder.Editor
{
[CustomEditor(typeof(ImageEncoder))]
public class ImageEncoderInspector : UnityEditor.Editor
{
    private bool configurationQueued;

    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target, false, false)) return;
        var encoder = (ImageEncoder)target;
        serializedObject.Update();
        string resolutionError;
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        {
            var format = serializedObject.FindProperty("format");
            EditorGUILayout.PropertyField(format, new GUIContent("Format"));
            var sampling = serializedObject.FindProperty("subsampleChroma");
            sampling.boolValue = EditorGUILayout.Popup("Chroma sampling", sampling.boolValue ? 0 : 1,
                                                       new[] { "4:2:0", "4:4:4" }) == 0;
            var input = serializedObject.FindProperty("inputTexture");
            EditorGUILayout.PropertyField(input, new GUIContent("Input RenderTexture"));
            resolutionError = DrawResolution(input.objectReferenceValue as RenderTexture,
                                             (ImageEncodingFormat)format.enumValueIndex);
            var quality = serializedObject.FindProperty("quality");
            quality.intValue = EditorGUILayout.IntSlider("Quality", quality.intValue, 1, 100);
        }
        bool changed = serializedObject.ApplyModifiedProperties();
        string error = resolutionError != "" ? resolutionError : encoder.ConfigurationError();
        if (error != "")
        {
            EditorGUILayout.HelpBox(error, encoder.inputTexture ? MessageType.Error : MessageType.Info);
            return;
        }
        bool needsConfiguration = encoder.format == ImageEncodingFormat.AVIF
                                      ? !encoder.avifEncoder || encoder.avifEncoder.width != encoder.width ||
                                            encoder.avifEncoder.height != encoder.height ||
                                            encoder.avifEncoder.subsampleChroma != encoder.subsampleChroma
                                      : !encoder.jpegEncoder || encoder.jpegEncoder.width != encoder.width ||
                                            encoder.jpegEncoder.height != encoder.height ||
                                            encoder.jpegEncoder.subsampleChroma != encoder.subsampleChroma;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && (changed || needsConfiguration) &&
            !configurationQueued)
        {
            // Configure after Inspector drawing; resource generation can import assets and compile Udon.
            configurationQueued = true;
            EditorApplication.delayCall += () =>
            {
                configurationQueued = false;
                if (encoder) ImageEncoderSetup.Configure(encoder);
            };
        }
        if (encoder.error != "") EditorGUILayout.HelpBox(encoder.error, MessageType.Error);
    }
    private string DrawResolution(RenderTexture input, ImageEncodingFormat format)
    {
        var width = serializedObject.FindProperty("width");
        var height = serializedObject.FindProperty("height");
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorGUILayout.LabelField("Resolution", width.intValue + " × " + height.intValue);
            return "";
        }
        var sizes = ImageEncoderSetup.Resolutions(input, format);
        if (sizes.Length == 0)
        {
            EditorGUILayout.LabelField("Resolution", input ? "No compatible size" : "Assign an input first");
            return input ? "No supported resolution keeps this aspect ratio. For AVIF, use JPEG or change " +
                               "the input RenderTexture."
                         : "";
        }
        int selected = Array.FindIndex(sizes, size => size.x == width.intValue && size.y == height.intValue);
        if (selected < 0)
        {
            int longEdge = Mathf.Max(width.intValue, height.intValue), distance = int.MaxValue;
            for (int i = 0; i < sizes.Length; i++)
            {
                int candidate = Mathf.Abs(Mathf.Max(sizes[i].x, sizes[i].y) - longEdge);
                if (candidate >= distance) continue;
                distance = candidate;
                selected = i;
            }
        }
        selected = EditorGUILayout.Popup("Resolution", selected,
                                         Array.ConvertAll(sizes, size => size.x + " × " + size.y));
        width.intValue = sizes[selected].x;
        height.intValue = sizes[selected].y;
        return "";
    }
}

// Implementation settings are generated; the public codec APIs remain available to scripts.
public abstract class CodecImplementationInspector : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target, false, false)) return;
        EditorGUILayout.HelpBox(
            target is AvifDecoder || target is JpegDecoder
                ? "Pass encoded bytes through the decoder API. Resolution and sampling are read from the " +
                  "image."
                : "Configured automatically. Use Tools > Image Codec > Create encoder for image settings.",
            MessageType.Info);
    }
}
[CustomEditor(typeof(AvifEncoder))]
public class AvifEncoderInspector : CodecImplementationInspector
{
}
[CustomEditor(typeof(JpegEncoder))]
public class JpegEncoderInspector : CodecImplementationInspector
{
}
[CustomEditor(typeof(AvifGpuTransform))]
public class AvifTransformInspector : CodecImplementationInspector
{
}
[CustomEditor(typeof(AvifGpuEntropy))]
public class AvifEntropyInspector : CodecImplementationInspector
{
}
[CustomEditor(typeof(AvifDecoder))]
public class AvifDecoderInspector : CodecImplementationInspector
{
}
[CustomEditor(typeof(JpegDecoder))]
public class JpegDecoderInspector : CodecImplementationInspector
{
}
}
#endif
