#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UdonSharpEditor;
using VrcImageCodec;
using VrcAvif.Encoder.Editor;
using VRC.SDK3.ClientSim;
using VRC.SDK3.Components;

[InitializeOnLoad]
public static class CodecBenchmarkSetup
{
    const string DirectoryPath = "Logs/CodecBenchmark";
    const string State = "CodecBenchmark.";
    static readonly int[] Widths = { 256, 512, 640, 1280, 1920 };
    static readonly int[] Heights = { 144, 288, 360, 720, 1080 };
    static CodecBenchmarkSetup()
    {
        EditorApplication.update += Update;
    }

    [MenuItem("Tools/Image Codec/Run benchmark (ClientSim)")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play mode before benchmarking.");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        System.IO.Directory.CreateDirectory(DirectoryPath);
        foreach (string name in new[] { "measurements.jsonl", "done.txt", "error.txt", "group.done" })
            File.Delete(Path.Combine(DirectoryPath, name));
        foreach (string path in System.IO.Directory.GetFiles(DirectoryPath, "*-source.rgba"))
            File.Delete(path);
        SessionState.SetString(State + "scene", SceneManager.GetActiveScene().path);
        SessionState.SetString(State + "settings", JsonUtility.ToJson(ClientSimSettings.Instance));
        SessionState.SetInt(State + "group", 0);
        SessionState.SetBool(State + "running", true);
        File.WriteAllText(
            Path.Combine(DirectoryPath, "environment.json"),
            JsonUtility.ToJson(new EnvironmentInfo { unity = Application.unityVersion,
                                                     gpu = SystemInfo.graphicsDeviceName,
                                                     cpu = SystemInfo.processorType,
                                                     graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                                                     colorSpace = PlayerSettings.colorSpace.ToString() },
                               true));
        try
        {
            PrepareGroup();
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(DirectoryPath, "error.txt"), exception.ToString());
            Restore(false);
        }
    }

    [Serializable]
    class EnvironmentInfo
    {
        public string unity, gpu, cpu, graphicsApi, colorSpace;
        public int targetFps = 60;
    }

    static void Update()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        if (!SessionState.GetBool(State + "running", false)) return;
        try
        {
            if (File.Exists(Path.Combine(DirectoryPath, "error.txt")))
            {
                Restore(false);
                return;
            }
            if (!File.Exists(Path.Combine(DirectoryPath, "group.done"))) return;
            File.Delete(Path.Combine(DirectoryPath, "group.done"));
            int group = SessionState.GetInt(State + "group", 0) + 1;
            SessionState.SetInt(State + "group", group);
            if (group == Widths.Length * 2)
            {
                EditorApplication.delayCall += () => Restore(true);
                return;
            }
            // UdonSharp queues work when returning to Edit mode; let it finish before replacing the scene.
            EditorApplication.delayCall += PrepareGroup;
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(DirectoryPath, "error.txt"), exception.ToString());
            Restore(false);
        }
    }

    static void PrepareGroup()
    {
        int group = SessionState.GetInt(State + "group", 0);
        int width = Widths[group / 2], height = Heights[group / 2];
        bool subsampled = group % 2 == 0;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var descriptor = new GameObject("Benchmark world").AddComponent<VRCSceneDescriptor>();
        descriptor.spawns = new[] { new GameObject("Spawn").transform };
        new GameObject("View").AddComponent<Camera>().tag = "MainCamera";
        var benchmark = new GameObject("Codec benchmark").AddComponent<CodecBenchmark>();
        benchmark.directory = Path.GetFullPath(DirectoryPath);
        benchmark.width = width;
        benchmark.height = height;
        benchmark.subsampleChroma = subsampled;
        benchmark.encoders = new VRC.Udon.UdonBehaviour[2];
        benchmark.decoders = new VRC.Udon.UdonBehaviour[2];
        AvifEncoderSetup.Create();
        var avif = Selection.activeGameObject.GetComponent<AvifEncoder>();
        AvifEncoderSetup.Configure(avif, width, height, 60, AvifProcessingLoad.Fast, subsampled);
        benchmark.encoders[0] = UdonSharpEditorUtility.GetBackingUdonBehaviour(avif);
        benchmark.input = avif.gpuTransform.inputTexture;
        AvifEncoderSetup.CreateDecoder();
        var decoder = Selection.activeGameObject.GetComponent<AvifDecoder>();
        AvifEncoderSetup.Configure(decoder, AvifProcessingLoad.Fast);
        benchmark.decoders[0] = UdonSharpEditorUtility.GetBackingUdonBehaviour(decoder);
        JpegCodecSetup.Create();
        var jpeg = Selection.activeGameObject.GetComponent<JpegEncoder>();
        jpeg.width = width;
        jpeg.height = height;
        jpeg.subsampleChroma = subsampled;
        jpeg.inputTexture = benchmark.input;
        UdonSharpEditorUtility.CopyProxyToUdon(jpeg);
        benchmark.encoders[1] = UdonSharpEditorUtility.GetBackingUdonBehaviour(jpeg);
        benchmark.decoders[1] =
            UdonSharpEditorUtility.GetBackingUdonBehaviour(jpeg.GetComponent<JpegDecoder>());
        System.IO.Directory.CreateDirectory("Assets/CodecBenchmark");
        EditorSceneManager.SaveScene(scene, "Assets/CodecBenchmark/Benchmark.unity");
        var settings =
            JsonUtility.FromJson<ClientSimSettings>(SessionState.GetString(State + "settings", ""));
        settings.enableClientSim = true;
        settings.spawnPlayer = false;
        settings.hideMenuOnLaunch = true;
        settings.targetFrameRate = 60;
        ClientSimSettings.SaveSettings(settings);
        // Let UdonSharp finish its deferred component setup before cloning the scene into Play mode.
        EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
    }

    static void Restore(bool success)
    {
        SessionState.SetBool(State + "running", false);
        ClientSimSettings.SaveSettings(
            JsonUtility.FromJson<ClientSimSettings>(SessionState.GetString(State + "settings", "")));
        string scene = SessionState.GetString(State + "scene", "");
        if (string.IsNullOrEmpty(scene)) EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);
        if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
        if (success)
            File.WriteAllText(Path.Combine(DirectoryPath, "done.txt"),
                              "ClientSim finished; scene and settings restored.");
        if (Application.isBatchMode) EditorApplication.Exit(success ? 0 : 1);
    }
}
#endif
