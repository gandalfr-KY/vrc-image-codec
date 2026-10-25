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
    public enum AvifProcessingLoad { Gentle, Normal, Fast }
    public static class AvifEncoderSetup
    {
        [MenuItem("Tools/AVIF Encoder/Create encoder")]
        public static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Create the encoder outside Play mode.");
            EnsurePrograms();
            var root = new GameObject("AVIF encoder");
            Undo.RegisterCreatedObjectUndo(root, "Create AVIF encoder");
            var encoder = root.AddUdonSharpComponent<TernaryAvifEncoder>();
            encoder.gpuTransform = root.AddUdonSharpComponent<TernaryAvifGpuTransform>();
            Configure(encoder, 640, 360, TernaryAvifEncoder.DefaultQuality);
            Selection.activeGameObject = root;
        }
        public static bool ValidDimensions(int w, int h)
        { return w >= 16 && h >= 16 && w <= 1920 && h <= 1920 && w % 8 == 0 && h % 8 == 0 && w * h <= 2073600; }
        public static void Configure(TernaryAvifEncoder encoder, int w, int h, int q, AvifProcessingLoad load=AvifProcessingLoad.Normal)
        {
            if (!ValidDimensions(w, h) || q < 0 || q > 100) throw new ArgumentException("Unsupported dimensions or quality.");
            if(load<AvifProcessingLoad.Gentle||load>AvifProcessingLoad.Fast)throw new ArgumentException("Unsupported processing load.");
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Configure the camera outside Play mode.");
            var gpu = encoder.gpuTransform;
            if (!gpu) throw new InvalidOperationException("A GPU transform is required.");
            Undo.RecordObject(encoder, "Configure AVIF"); Undo.RecordObject(gpu, "Configure AVIF");
            encoder.width = w; encoder.height = h; encoder.quality = q;
            encoder.frameBudgetMs=load==AvifProcessingLoad.Gentle?1:load==AvifProcessingLoad.Fast?4:2;
            gpu.diagonalsPerFrame=load==AvifProcessingLoad.Gentle?2:load==AvifProcessingLoad.Fast?8:4;
            gpu.width = w; gpu.height = h;
            gpu.linearInput = PlayerSettings.colorSpace == ColorSpace.Linear;
            string directory = "Assets/AvifEncoderGenerated/" + encoder.GetInstanceID().ToString().Replace("-", "n");
            Directory.CreateDirectory(directory); AssetDatabase.Refresh();
            var shader = Shader.Find("TernaryStars/Communication/AV1 Intra");
            if (!shader) throw new InvalidOperationException("AV1 Intra shader is missing.");
            if (!gpu.material) {
                gpu.material = new Material(shader);
                AssetDatabase.CreateAsset(gpu.material, AssetDatabase.GenerateUniqueAssetPath(directory + "/AV1.mat"));
            }
            gpu.material.shader=shader;
            ConfigureRateCosts(gpu.material,directory);
            int columns=w/8,rows=h/8;
            gpu.optimizedTexture=Texture(directory,"Optimized coefficients",columns*16,rows,false,gpu.optimizedTexture,true);
            gpu.candidateTexture=Texture(directory,"Transform candidates",columns*40,rows,false,gpu.candidateTexture,true,RenderTextureFormat.RFloat);
            gpu.uvCandidates=Texture(directory,"UV candidates",columns*8,rows,false,gpu.uvCandidates,true,RenderTextureFormat.RFloat);
            gpu.uvModeTexture=Texture(directory,"UV modes",columns,rows,false,gpu.uvModeTexture);
            gpu.modeCosts=Texture(directory,"Prediction costs",columns*51,rows,false,gpu.modeCosts,true,RenderTextureFormat.RGFloat);
            gpu.shortlist=Texture(directory,"Prediction shortlist",columns*2,rows,false,gpu.shortlist,true);
            gpu.modeTexture=Texture(directory,"Prediction modes",w/8,h/8,false,gpu.modeTexture);
            gpu.cflTexture=Texture(directory,"Chroma prediction",w/8,h/8,false,gpu.cflTexture);
            gpu.quantA = Texture(directory, "Quant A", w, h / 8 * 12, false, gpu.quantA);
            gpu.reconA = Texture(directory, "Reconstruction A", w, h / 8 * 12, false, gpu.reconA);
            gpu.reconB = Texture(directory, "Reconstruction B", w, h / 8 * 12, false, gpu.reconB);
            gpu.packedTexture = Texture(directory, "Coefficients", 512, (w / 8 * (h / 8) * 99 + 511) / 512, false, gpu.packedTexture);
            gpu.inputTexture = Texture(directory, "Capture", w, h, true, gpu.inputTexture);
            gpu.sourceYuvTexture = Texture(directory, "Cached source YUV", w, h*3/2, false, gpu.sourceYuvTexture);
            UdonSharpEditorUtility.CopyProxyToUdon(encoder); UdonSharpEditorUtility.CopyProxyToUdon(gpu);
            EditorUtility.SetDirty(encoder); EditorUtility.SetDirty(gpu);
            EditorSceneManager.MarkSceneDirty(encoder.gameObject.scene); AssetDatabase.SaveAssets();
        }
        [Serializable] class RateCosts { public int[] costs,eobCosts; }
        static void ConfigureRateCosts(Material material,string directory)
        {
            var texture=material.GetTexture("_AomCosts") as Texture2D;
            if(!texture) {
                texture=new Texture2D(1024,2,TextureFormat.RGBAFloat,false,true);
                texture.name="AV1 rate costs";texture.filterMode=FilterMode.Point;texture.wrapMode=TextureWrapMode.Clamp;
                AssetDatabase.CreateAsset(texture,AssetDatabase.GenerateUniqueAssetPath(directory+"/Rate costs.asset"));
            }
            string source=AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("Av1RateCosts")[0]);
            var costs=JsonUtility.FromJson<RateCosts>(File.ReadAllText(source));
            var pixels=new Color[2048];
            for(int i=0;i<costs.costs.Length;i++)pixels[i]=new Color(costs.costs[i],0,0,0);
            for(int i=0;i<costs.eobCosts.Length;i++)pixels[1024+i]=new Color(costs.eobCosts[i],0,0,0);
            texture.SetPixels(pixels);texture.Apply(false,false);material.SetTexture("_AomCosts",texture);
            EditorUtility.SetDirty(texture);EditorUtility.SetDirty(material);
        }
        static RenderTexture Texture(string dir, string name, int w, int h, bool srgb = false, RenderTexture existing = null, bool floating = false, RenderTextureFormat preferred = RenderTextureFormat.Default)
        {
            var format=preferred!=RenderTextureFormat.Default&&SystemInfo.SupportsRenderTextureFormat(preferred)
                ? preferred : floating?RenderTextureFormat.ARGBFloat:RenderTextureFormat.ARGB32;
            if (existing) {
                existing.Release(); existing.width=w; existing.height=h; existing.format=format; EditorUtility.SetDirty(existing); return existing;
            }
            var rt = new RenderTexture(w, h, srgb ? 24 : 0, format, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            rt.name = name; rt.filterMode = FilterMode.Point; rt.wrapMode = TextureWrapMode.Clamp; rt.useMipMap = false; rt.antiAliasing = 1;
            AssetDatabase.CreateAsset(rt, AssetDatabase.GenerateUniqueAssetPath(dir + "/" + name + ".renderTexture")); return rt;
        }
        public static void EnsurePrograms(params Type[] types)
        {
            const string dir="Assets/AvifEncoderGenerated/Programs";Directory.CreateDirectory(dir);AssetDatabase.Refresh();
            foreach(string guid in AssetDatabase.FindAssets("t:UdonSharpProgramAsset",new[]{dir})) {
                var orphan=AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if(orphan&&!orphan.sourceCsScript)UdonSharpEditorUtility.DeleteProgramAsset(orphan);
            }
            if(types.Length==0)types=new[]{typeof(TernaryAvifEncoder),typeof(TernaryAvifGpuTransform)};
            foreach(var type in types)
            {
                var asset=UdonSharpEditorUtility.GetUdonSharpProgramAsset(type);
                bool created=!asset;
                if(created)asset=ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                asset.ScriptVersion=UdonSharpProgramVersion.CurrentVersion;
                asset.sourceCsScript=System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.Select(AssetDatabase.FindAssets(type.Name+" t:MonoScript"),guid=>AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid))),script=>script&&script.GetClass()==type);
                if(!asset.sourceCsScript)throw new InvalidOperationException("Missing Udon source: "+type.Name);
                if(created)AssetDatabase.CreateAsset(asset,dir+"/"+type.Name+".asset");
                else EditorUtility.SetDirty(asset);
            }
            AssetDatabase.SaveAssets();UdonSharp.Compiler.UdonSharpCompilerV1.CompileSync();
            if(UdonSharpProgramAsset.AnyUdonSharpScriptHasError())throw new InvalidOperationException("Resolve UdonSharp compile errors before creating the camera.");
        }
    }
}
#endif
