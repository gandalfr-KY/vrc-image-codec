#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UdonSharp;
using UdonSharpEditor;
using TernaryStars;

namespace VrcAvif.Encoder.Editor
{
public enum AvifProcessingLoad
{
    Gentle,
    Normal,
    Fast
}
public static class AvifEncoderSetup
{
    [MenuItem("Tools/AVIF Encoder/Create decoder")]
    public static void CreateDecoder()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Create the decoder outside Play mode.");
        EnsurePrograms(typeof(TernaryAvifDecoder));
        var root = new GameObject("AVIF decoder");
        Undo.RegisterCreatedObjectUndo(root, "Create AVIF decoder");
        var decoder = root.AddUdonSharpComponent<TernaryAvifDecoder>();
        const string directory = "Assets/AvifEncoderGenerated";
        Directory.CreateDirectory(directory);
        AssetDatabase.Refresh();
        decoder.material = new Material(Shader.Find("TernaryStars/Communication/AV1 Intra"));
        AssetDatabase.CreateAsset(decoder.material,
                                  AssetDatabase.GenerateUniqueAssetPath(directory + "/Decoder.mat"));
        UdonSharpEditorUtility.CopyProxyToUdon(decoder);
        EditorUtility.SetDirty(decoder);
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(root.scene);
    }
    [MenuItem("Tools/AVIF Encoder/Create encoder")]
    public static void Create()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Create the encoder outside Play mode.");
        EnsurePrograms();
        var root = new GameObject("AVIF encoder");
        Undo.RegisterCreatedObjectUndo(root, "Create AVIF encoder");
        var encoder = root.AddUdonSharpComponent<TernaryAvifEncoder>();
        encoder.gpuTransform = root.AddUdonSharpComponent<TernaryAvifGpuTransform>();
        Configure(encoder, 640, 360, TernaryAvifEncoder.DefaultQuality);
        Selection.activeGameObject = root;
    }
    public static bool ValidDimensions(int w, int h)
    {
        return w >= 16 && h >= 16 && w <= 1920 && h <= 1920 && w % 8 == 0 && h % 8 == 0 && w * h <= 2073600;
    }
    public static void Configure(TernaryAvifDecoder decoder,
                                 AvifProcessingLoad load = AvifProcessingLoad.Normal)
    {
        if (!decoder) throw new ArgumentException("Select an AVIF decoder.");
        if (load < AvifProcessingLoad.Gentle || load > AvifProcessingLoad.Fast)
            throw new ArgumentException("Unsupported processing load.");
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Configure the decoder outside Play mode.");
        Undo.RecordObject(decoder, "Configure AVIF decoding");
        decoder.frameBudgetMs = load == AvifProcessingLoad.Gentle ? 1
                                : load == AvifProcessingLoad.Fast ? 4
                                                                  : 2;
        decoder.diagonalsPerFrame = load == AvifProcessingLoad.Gentle ? 4
                                    : load == AvifProcessingLoad.Fast ? 16
                                                                      : 8;
        UdonSharpEditorUtility.CopyProxyToUdon(decoder);
        EditorUtility.SetDirty(decoder);
        EditorSceneManager.MarkSceneDirty(decoder.gameObject.scene);
    }
    public static void Configure(TernaryAvifEncoder encoder, int w, int h, int q,
                                 AvifProcessingLoad load = AvifProcessingLoad.Normal)
    {
        if (!encoder) throw new ArgumentException("Select an AVIF encoder.");
        if (!ValidDimensions(w, h) || q < 0 || q > 100)
            throw new ArgumentException("Unsupported dimensions or quality.");
        if (load < AvifProcessingLoad.Gentle || load > AvifProcessingLoad.Fast)
            throw new ArgumentException("Unsupported processing load.");
        if (EditorApplication.isPlaying)
            throw new InvalidOperationException("Configure the encoder outside Play mode.");
        var gpu = encoder.gpuTransform;
        if (!gpu) throw new InvalidOperationException("A GPU transform is required.");
        Undo.RecordObject(encoder, "Configure AVIF");
        Undo.RecordObject(gpu, "Configure AVIF");
        encoder.width = w;
        encoder.height = h;
        encoder.quality = q;
        gpu.diagonalsPerFrame = load == AvifProcessingLoad.Gentle ? 2
                                : load == AvifProcessingLoad.Fast ? 8
                                                                  : 4;
        gpu.width = w;
        gpu.height = h;
        gpu.linearInput = PlayerSettings.colorSpace == ColorSpace.Linear;
        string directory =
            "Assets/AvifEncoderGenerated/" + encoder.GetInstanceID().ToString().Replace("-", "n");
        Directory.CreateDirectory(directory);
        AssetDatabase.Refresh();
        var shader = Shader.Find("TernaryStars/Communication/AV1 Intra");
        if (!shader) throw new InvalidOperationException("AV1 Intra shader is missing.");
        if (!gpu.material)
        {
            gpu.material = new Material(shader);
            AssetDatabase.CreateAsset(gpu.material,
                                      AssetDatabase.GenerateUniqueAssetPath(directory + "/AV1.mat"));
        }
        gpu.material.shader = shader;
        ConfigureRateCosts(gpu.material, directory);
        int columns = w / 8, rows = h / 8;
        gpu.optimizedTexture = Texture(directory, "Optimized coefficients", columns * 16, rows, false,
                                       gpu.optimizedTexture, true);
        gpu.candidateTexture = Texture(directory, "Transform candidates", columns * 40, rows, false,
                                       gpu.candidateTexture, true, RenderTextureFormat.RFloat);
        gpu.uvCandidates = Texture(directory, "UV candidates", columns * 8, rows, false, gpu.uvCandidates,
                                   true, RenderTextureFormat.RFloat);
        gpu.uvModeTexture = Texture(directory, "UV modes", columns, rows, false, gpu.uvModeTexture);
        gpu.modeCosts = Texture(directory, "Prediction costs", columns * 51, rows, false, gpu.modeCosts, true,
                                RenderTextureFormat.RGFloat);
        gpu.shortlist =
            Texture(directory, "Prediction shortlist", columns * 2, rows, false, gpu.shortlist, true);
        gpu.modeTexture = Texture(directory, "Prediction modes", w / 8, h / 8, false, gpu.modeTexture);
        gpu.cflTexture = Texture(directory, "Chroma prediction", w / 8, h / 8, false, gpu.cflTexture);
        gpu.quantA = Texture(directory, "Quant A", w, h / 8 * 12, false, gpu.quantA);
        gpu.reconA = Texture(directory, "Reconstruction A", w, h / 8 * 12, false, gpu.reconA);
        gpu.reconB = Texture(directory, "Reconstruction B", w, h / 8 * 12, false, gpu.reconB);
        gpu.packedTexture = Texture(directory, "Coefficients", 512, (w / 8 * (h / 8) * 99 + 511) / 512, false,
                                    gpu.packedTexture);
        gpu.inputTexture = Texture(directory, "Capture", w, h, true, gpu.inputTexture);
        gpu.sourceYuvTexture =
            Texture(directory, "Cached source YUV", w, h * 3 / 2, false, gpu.sourceYuvTexture);
        if (!encoder.gpuEntropy)
        {
            encoder.gpuEntropy = encoder.GetComponent<TernaryAvifGpuEntropy>();
            if (!encoder.gpuEntropy)
                encoder.gpuEntropy = encoder.gameObject.AddUdonSharpComponent<TernaryAvifGpuEntropy>();
        }
        ConfigureEntropy(encoder.gpuEntropy, w, h, directory);
        UdonSharpEditorUtility.CopyProxyToUdon(encoder.gpuEntropy);
        UdonSharpEditorUtility.CopyProxyToUdon(encoder);
        UdonSharpEditorUtility.CopyProxyToUdon(gpu);
        EditorUtility.SetDirty(encoder);
        EditorUtility.SetDirty(gpu);
        EditorSceneManager.MarkSceneDirty(encoder.gameObject.scene);
        AssetDatabase.SaveAssets();
    }
    [Serializable]
    class RateCosts
    {
        public int[] costs, eobCosts;
    }
    static void ConfigureEntropy(TernaryAvifGpuEntropy entropy, int width, int height, string directory)
    {
        entropy.width = width;
        entropy.height = height;
        entropy.syntaxMaterial =
            EntropyMaterial(entropy.syntaxMaterial, "VrcAvif/Syntax", directory + "/Syntax.mat");
        entropy.cdfMaterial =
            EntropyMaterial(entropy.cdfMaterial, "VrcAvif/Adaptive CDF", directory + "/CDF.mat");
        entropy.rangeMaterial =
            EntropyMaterial(entropy.rangeMaterial, "VrcAvif/Range coding", directory + "/Range.mat");
        if (!entropy.initialCdf)
        {
            var pixels = new Color[16384];
            pixels[0] = new Color(19132, 32767, 32767, 0);
            CdfFamily(pixels, AvifTables.CreatePartition16Default(), 3, 9);
            CdfFamily(pixels, AvifTables.CreatePartition32Default(), 39, 9);
            CdfFamily(pixels, AvifTables.CreatePartition64Default(), 75, 9);
            CdfFamily(pixels, AvifTables.CreateTxTypeDefault(), 112, 4);
            CdfFamily(pixels, AvifTables.CreateEob64Default(), 268, 6);
            CdfFamily(pixels, AvifTables.CreateEob16Default(), 316, 4);
            CdfFamily(pixels, AvifTables.CreateIntraYDefault(), 3300, 12);
            CdfFamily(pixels, AvifTables.CreateIntraUvDefault(), 2928, 13);
            CdfFamily(pixels, AvifTables.CreateAngleDeltaDefault(), 3210, 6);
            CdfFamily(pixels, AvifTables.CreateCflSignDefault(), 3109, 7);
            CdfFamily(pixels, AvifTables.CreateCflAlphaDefault(), 3116, 15);
            SmallCdfFamily(pixels, AvifTables.CreateSkipDefault(), 3900);
            SmallCdfFamily(pixels, AvifTables.CreateAllZeroDefault(), 164);
            SmallCdfFamily(pixels, AvifTables.CreateEobExtra8Default(), 348);
            SmallCdfFamily(pixels, AvifTables.CreateEobExtra4Default(), 388);
            SmallCdfFamily(pixels, AvifTables.CreateCoeffBaseEobDefault(), 412);
            SmallCdfFamily(pixels, AvifTables.CreateCoeffBaseDefault(), 540);
            SmallCdfFamily(pixels, AvifTables.CreateCoeffBrDefault(), 1788);
            SmallCdfFamily(pixels, AvifTables.CreateDcSignDefault(), 2796);
            entropy.initialCdf = new Texture2D(1024, 16, TextureFormat.RGBAFloat, false, true);
            entropy.initialCdf.name = "AV1 initial probabilities";
            entropy.initialCdf.filterMode = FilterMode.Point;
            entropy.initialCdf.SetPixels(pixels);
            entropy.initialCdf.Apply(false, false);
            AssetDatabase.CreateAsset(entropy.initialCdf,
                                      AssetDatabase.GenerateUniqueAssetPath(directory + "/CDF.asset"));
        }
        AvifContainerTemplate.Configure(entropy, width, height, directory);
        EditorUtility.SetDirty(entropy);
    }
    static Material EntropyMaterial(Material material, string name, string path)
    {
        var shader = Shader.Find(name);
        if (!shader) throw new InvalidOperationException("Missing shader: " + name);
        if (!material)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(path));
        }
        material.shader = shader;
        EditorUtility.SetDirty(material);
        return material;
    }
    static void CdfFamily(Color[] pixels, int[] values, int start, int length)
    {
        for (int at = 0; at < values.Length; at += length)
            for (int k = 0; k < length; k++)
            {
                int index = (start + at) * 4 + k / 4;
                Color color = pixels[index];
                color[k % 4] = values[at + k];
                pixels[index] = color;
            }
    }
    static void SmallCdfFamily(Color[] pixels, Vector3Int[] values, int start)
    {
        for (int i = 0; i < values.Length; i++)
            pixels[(start + i) * 4] = new Color(values[i].x, values[i].y, values[i].z, 0);
    }
    static void ConfigureRateCosts(Material material, string directory)
    {
        var texture = material.GetTexture("_AomCosts") as Texture2D;
        if (!texture)
        {
            texture = new Texture2D(1024, 2, TextureFormat.RGBAFloat, false, true);
            texture.name = "AV1 rate costs";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            AssetDatabase.CreateAsset(
                texture, AssetDatabase.GenerateUniqueAssetPath(directory + ("/Rate " + "costs.asset")));
        }
        string source = AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("Av1RateCosts")[0]);
        var costs = JsonUtility.FromJson<RateCosts>(File.ReadAllText(source));
        var pixels = new Color[2048];
        for (int i = 0; i < costs.costs.Length; i++)
            pixels[i] = new Color(costs.costs[i], 0, 0, 0);
        for (int i = 0; i < costs.eobCosts.Length; i++)
            pixels[1024 + i] = new Color(costs.eobCosts[i], 0, 0, 0);
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        material.SetTexture("_AomCosts", texture);
        EditorUtility.SetDirty(texture);
        EditorUtility.SetDirty(material);
    }
    static RenderTexture Texture(string dir, string name, int w, int h, bool srgb = false,
                                 RenderTexture existing = null, bool floating = false,
                                 RenderTextureFormat preferred = RenderTextureFormat.Default)
    {
        var format =
            preferred != RenderTextureFormat.Default && SystemInfo.SupportsRenderTextureFormat(preferred)
                ? preferred
            : floating ? RenderTextureFormat.ARGBFloat
                       : RenderTextureFormat.ARGB32;
        if (existing)
        {
            existing.Release();
            existing.width = w;
            existing.height = h;
            existing.format = format;
            EditorUtility.SetDirty(existing);
            return existing;
        }
        var rt = new RenderTexture(w, h, srgb ? 24 : 0, format,
                                   srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.name = name;
        rt.filterMode = FilterMode.Point;
        rt.wrapMode = TextureWrapMode.Clamp;
        rt.useMipMap = false;
        rt.antiAliasing = 1;
        AssetDatabase.CreateAsset(rt,
                                  AssetDatabase.GenerateUniqueAssetPath(dir + "/" + name + ".renderTexture"));
        return rt;
    }
    public static void EnsurePrograms(params Type[] types)
    {
        const string dir = "Assets/AvifEncoderGenerated/Programs";
        Directory.CreateDirectory(dir);
        AssetDatabase.Refresh();
        foreach (string guid in AssetDatabase.FindAssets("t:UdonSharpProgramAsset", new[] { dir }))
        {
            var orphan =
                AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(AssetDatabase.GUIDToAssetPath(guid));
            if (orphan && !orphan.sourceCsScript) UdonSharpEditorUtility.DeleteProgramAsset(orphan);
        }
        if (types.Length == 0)
            types = new[] { typeof(TernaryAvifEncoder), typeof(TernaryAvifGpuTransform),
                            typeof(TernaryAvifGpuEntropy) };
        foreach (var type in types)
        {
            var asset = UdonSharpEditorUtility.GetUdonSharpProgramAsset(type);
            bool created = !asset;
            if (created) asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            asset.ScriptVersion = UdonSharpProgramVersion.CurrentVersion;
            asset.sourceCsScript = System.Linq.Enumerable.FirstOrDefault(
                System.Linq.Enumerable.Select(
                    AssetDatabase.FindAssets(type.Name + " t:MonoScript"),
                    guid => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid))),
                script => script && script.GetClass() == type);
            if (!asset.sourceCsScript)
                throw new InvalidOperationException("Missing Udon source: " + type.Name);
            if (created)
                AssetDatabase.CreateAsset(asset, dir + "/" + type.Name + ".asset");
            else
                EditorUtility.SetDirty(asset);
        }
        AssetDatabase.SaveAssets();
        UdonSharp.Compiler.UdonSharpCompilerV1.CompileSync();
        if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
            throw new InvalidOperationException(
                "Resolve UdonSharp compile errors before creating the encoder.");
    }
}
}
#endif
