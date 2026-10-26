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
        if (UdonSharpGUI.DrawProgramSource(target, false)) return;
        UdonSharpGUI.DrawCompileErrorTextArea();
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
            resolutionError = DrawResolution(input.objectReferenceValue as RenderTexture);
            var quality = serializedObject.FindProperty("quality");
            quality.intValue = EditorGUILayout.IntSlider("Quality", quality.intValue, 1, 100);
        }
        bool changed = serializedObject.ApplyModifiedProperties();
        string error = resolutionError != "" ? resolutionError : encoder.GetConfigurationError();
        if (error != "")
        {
            EditorGUILayout.HelpBox(error, encoder.InputTexture ? MessageType.Error : MessageType.Info);
            return;
        }
        ImageEncodingEstimates.Draw(encoder);
        if (!EditorApplication.isPlayingOrWillChangePlaymode &&
            (changed || ImageEncoderSetup.NeedsConfiguration(encoder)) && !configurationQueued)
        {
            // Configure after Inspector drawing; resource generation can import assets and compile Udon.
            configurationQueued = true;
            EditorApplication.delayCall += () =>
            {
                configurationQueued = false;
                if (encoder) ImageEncoderSetup.Configure(encoder);
            };
        }
        if (encoder.LastError != "") EditorGUILayout.HelpBox(encoder.LastError, MessageType.Error);
    }
    private string DrawResolution(RenderTexture input)
    {
        var width = serializedObject.FindProperty("width");
        var height = serializedObject.FindProperty("height");
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorGUILayout.LabelField("Resolution", width.intValue + " × " + height.intValue);
            return "";
        }
        var sizes = ImageEncoderSetup.Resolutions(input);
        if (sizes.Length == 0)
        {
            EditorGUILayout.LabelField("Resolution", input ? "No compatible size" : "Assign an input first");
            string inputError = ImageEncoderSetup.InputError(input);
            if (inputError != "") return inputError;
            return input ? "No supported resolution keeps this aspect ratio. Use an input RenderTexture " +
                               "with a standard aspect ratio, such as 16:9, 4:3 or 1:1."
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
        if (sizes.Length == 1)
            EditorGUILayout.HelpBox(
                "Only one resolution is available. For more choices, use a larger input RenderTexture " +
                    "with a standard aspect ratio, such as 16:9, 4:3 or 1:1.",
                MessageType.Warning);
        return "";
    }
}

// Implementation settings are generated; the public codec APIs remain available to scripts.
public abstract class CodecImplementationInspector : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawProgramSource(target, false)) return;
        UdonSharpGUI.DrawCompileErrorTextArea();
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
