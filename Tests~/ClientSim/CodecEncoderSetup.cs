#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UdonSharpEditor;
using TernaryStars;
using VrcAvif.Encoder.Editor;
using VRC.SDK3.ClientSim;
using VRC.SDK3.Components;

[InitializeOnLoad]
public static class CodecEncoderSetup
{
    const string Root = "Logs/CodecEncoder";
    static CodecEncoderSetup()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Root + "/version.txt", "production-gpu-v1");
        EditorApplication.update += Tick;
    }
    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
        if (EditorApplication.isPlaying)
        {
            if (SessionState.GetBool("CodecEncoder.running", false) &&
                (File.Exists(Root + "/done.txt") || File.Exists(Root + "/error.txt")))
                EditorApplication.isPlaying = false;
            return;
        }
        if (SessionState.GetBool("CodecEncoder.running", false))
        {
            SessionState.SetBool("CodecEncoder.running", false);
            ClientSimSettings.SaveSettings(
                JsonUtility.FromJson<ClientSimSettings>(SessionState.GetString("CodecEncoder.settings", "")));
            EditorSceneManager.OpenScene(SessionState.GetString("CodecEncoder.scene", ""));
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
                SessionState.SetString("CodecEncoder.scene", SceneManager.GetActiveScene().path);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var descriptor = new GameObject("ClientSim world").AddComponent<VRCSceneDescriptor>();
            descriptor.spawns = new[] { new GameObject("Spawn").transform };
            new GameObject("View").AddComponent<Camera>().tag = "MainCamera";
            AvifEncoderSetup.EnsurePrograms(typeof(CodecFixtureDriver));
            var drivers = new VRC.Udon.UdonBehaviour[3];
            var backends = new VRC.Udon.UdonBehaviour[3];
            TernaryAvifEncoder capture = null;
            for (int i = 0; i < 3; i++)
            {
                AvifEncoderSetup.Create();
                var encoder = Selection.activeGameObject.GetComponent<TernaryAvifEncoder>();
                AvifEncoderSetup.Configure(encoder,
                                           i == 0   ? 1920
                                           : i == 1 ? 360
                                                    : 16,
                                           i == 0   ? 1080
                                           : i == 1 ? 640
                                                    : 16,
                                           60, AvifProcessingLoad.Fast);
                if (i == 0) capture = encoder;
                var driver = encoder.gameObject.AddUdonSharpComponent<CodecFixtureDriver>();
                driver.backend = encoder.gpuEntropy;
                driver.packet = encoder.gpuTransform.packedTexture;
                UdonSharpEditorUtility.CopyProxyToUdon(driver);
                drivers[i] = UdonSharpEditorUtility.GetBackingUdonBehaviour(driver);
                backends[i] = UdonSharpEditorUtility.GetBackingUdonBehaviour(encoder.gpuEntropy);
            }
            var observer =
                new GameObject("Production codec test observer").AddComponent<CodecEncoderObserver>();
            observer.drivers = drivers;
            observer.backends = backends;
            observer.capture = UdonSharpEditorUtility.GetBackingUdonBehaviour(capture);
            observer.input = capture.gpuTransform.inputTexture;
            observer.source = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/CodecProduction/Source.png");
            EditorSceneManager.SaveScene(scene, "Assets/CodecProduction/Tests.unity");
            SessionState.SetString("CodecEncoder.settings", JsonUtility.ToJson(ClientSimSettings.Instance));
            var settings =
                JsonUtility.FromJson<ClientSimSettings>(JsonUtility.ToJson(ClientSimSettings.Instance));
            settings.enableClientSim = true;
            settings.spawnPlayer = false;
            settings.hideMenuOnLaunch = true;
            settings.targetFrameRate = 60;
            ClientSimSettings.SaveSettings(settings);
            SessionState.SetBool("CodecEncoder.running", true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception e)
        {
            File.WriteAllText(Root + "/error.txt", e.ToString());
            Debug.LogException(e);
            EditorSceneManager.OpenScene(
                SessionState.GetString("CodecEncoder.scene", "Assets/ManualDemo/Installation.unity"));
        }
    }
}
#endif
