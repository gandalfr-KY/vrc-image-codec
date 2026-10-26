#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UdonSharpEditor;
using VrcImageCodec;
using VrcAvif.Encoder.Editor;
using VRC.SDK3.ClientSim;
using VRC.SDK3.Components;

[InitializeOnLoad]
public static class AvifSamplingSetup
{
    const string Root = "Logs/AvifSampling";
    static AvifSamplingSetup()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Root + "/version.txt", "sampling-v2-gpu");
        EditorApplication.update += Tick;
    }
    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
        if (EditorApplication.isPlaying)
        {
            if (SessionState.GetBool("AvifSampling.running", false) &&
                (File.Exists(Root + "/done.txt") || File.Exists(Root + "/error.txt")))
                EditorApplication.isPlaying = false;
            return;
        }
        if (SessionState.GetBool("AvifSampling.running", false))
        {
            SessionState.SetBool("AvifSampling.running", false);
            ClientSimSettings.SaveSettings(
                JsonUtility.FromJson<ClientSimSettings>(SessionState.GetString("AvifSampling.settings", "")));
            EditorSceneManager.OpenScene(SessionState.GetString("AvifSampling.scene", ""));
            File.WriteAllText(Root + "/stopped.txt", "ClientSim stopped; scene and settings restored.");
            return;
        }
        if (!File.Exists(Root + "/prepare.request")) return;
        File.Delete(Root + "/prepare.request");
        foreach (string file in new[] { "done.txt", "error.txt", "stopped.txt", "results.json" })
            File.Delete(Root + "/" + file);
        try
        {
            if (!string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
                SessionState.SetString("AvifSampling.scene", SceneManager.GetActiveScene().path);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var descriptor = new GameObject("ClientSim world").AddComponent<VRCSceneDescriptor>();
            descriptor.spawns = new[] { new GameObject("Spawn").transform };
            new GameObject("View").AddComponent<Camera>().tag = "MainCamera";
            var observer = new GameObject("Sampling test observer").AddComponent<AvifSamplingObserver>();
            observer.encoders = new VRC.Udon.UdonBehaviour[4];
            observer.transforms = new VRC.Udon.UdonBehaviour[4];
            observer.decoders = new VRC.Udon.UdonBehaviour[4];
            observer.inputs = new RenderTexture[4];
            observer.packets = new RenderTexture[4];
            observer.reconstructed = new RenderTexture[4];
            for (int i = 0; i < 4; i++)
            {
                AvifEncoderSetup.Create();
                var encoder = Selection.activeGameObject.GetComponent<AvifEncoder>();
                AvifEncoderSetup.Configure(encoder, i < 2 ? 16 : 1920, i < 2 ? 16 : 1080, 60,
                                           AvifProcessingLoad.Fast, i % 2 == 0);
                observer.encoders[i] = UdonSharpEditorUtility.GetBackingUdonBehaviour(encoder);
                observer.transforms[i] = UdonSharpEditorUtility.GetBackingUdonBehaviour(encoder.gpuTransform);
                observer.inputs[i] = encoder.gpuTransform.inputTexture;
                observer.packets[i] = encoder.gpuTransform.packedTexture;
                observer.reconstructed[i] = encoder.gpuTransform.reconB;
                AvifEncoderSetup.CreateDecoder();
                var decoder = Selection.activeGameObject.GetComponent<AvifDecoder>();
                AvifEncoderSetup.Configure(decoder, AvifProcessingLoad.Fast);
                observer.decoders[i] = UdonSharpEditorUtility.GetBackingUdonBehaviour(decoder);
            }
            var variants = new ShaderVariantCollection();
            variants.Add(new ShaderVariantCollection.ShaderVariant(Shader.Find("VrcImageCodec/AV1 Decode"),
                                                                   UnityEngine.Rendering.PassType.Normal));
            variants.WarmUp();
            new GameObject("Codec batch pacing").AddComponent<CodecBatchPacing>().root = Root;
            EditorSceneManager.SaveScene(scene, "Assets/AvifSampling/Tests.unity");
            SessionState.SetString("AvifSampling.settings", JsonUtility.ToJson(ClientSimSettings.Instance));
            var settings =
                JsonUtility.FromJson<ClientSimSettings>(JsonUtility.ToJson(ClientSimSettings.Instance));
            settings.enableClientSim = true;
            settings.spawnPlayer = false;
            settings.hideMenuOnLaunch = true;
            settings.targetFrameRate = 60;
            ClientSimSettings.SaveSettings(settings);
            SessionState.SetBool("AvifSampling.running", true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception e)
        {
            File.WriteAllText(Root + "/error.txt", e.ToString());
            Debug.LogException(e);
            EditorSceneManager.OpenScene(
                SessionState.GetString("AvifSampling.scene", "Assets/ManualDemo/Installation.unity"));
        }
    }
}
#endif
