#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UdonSharp;
using UdonSharpEditor;
using VRC.Udon.Editor;
using VRC.Udon.Common.Interfaces;
using TernaryStars;
using VrcAvif.Encoder.Editor;

[InitializeOnLoad]
public static class DecoderTrial
{
    const string Root="Logs/AvifDecoder";
    [Serializable] class Job {public string id,file;}
    [Serializable] class Jobs {public Job[] jobs;}
    [Serializable] class Row {public string id;public int width,height,bytes,steps;public double cpuAndSubmitMs,totalMs,maxStepMs;}
    [Serializable] class Results {public Row[] rows;public string gpu,cpu;}
    static Jobs jobs;static int index;static bool active;
    static List<Row> rows=new List<Row>();
    static IUdonProgram program;
    static IUdonVM vm;
    static Material material;
    static DecoderTrial(){EditorApplication.update+=Tick;}
    static void Tick()
    {
        if(EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode||active)return;
        try
        {
            if(jobs==null)
            {
                if(!File.Exists(Root+"/run.request"))return;
                File.Delete(Root+"/run.request");File.Delete(Root+"/error.txt");
                AvifEncoderSetup.EnsurePrograms(typeof(TernaryAvifDecoder));
                jobs=JsonUtility.FromJson<Jobs>(File.ReadAllText(Root+"/jobs.json"));index=0;rows.Clear();
            }
            active=true;Run(jobs.jobs[index]);
        }
        catch(Exception e){Fail(e);}
    }
    static void Fail(Exception e){File.WriteAllText(Root+"/error.txt",e.ToString());UnityEngine.Debug.LogException(e);active=false;jobs=null;}
    static void Set(string name,object value){program.Heap.SetHeapVariable(program.SymbolTable.GetAddressFromSymbol(name),value);}
    static T Get<T>(string name){return program.Heap.GetHeapVariable<T>(program.SymbolTable.GetAddressFromSymbol(name));}
    static void Event(string name){vm.SetProgramCounter(program.EntryPoints.GetAddressFromSymbol(name));uint code=vm.Interpret();if(code!=0)throw new Exception("Udon halted: "+name+" "+code);}
    static void Run(Job job)
    {
        File.WriteAllText(Root+"/progress.txt",index+"/"+jobs.jobs.Length+" "+job.id);
        var asset=UdonSharpEditorUtility.GetUdonSharpProgramAsset(typeof(TernaryAvifDecoder));asset.UpdateProgram();
        program=asset.GetRealProgram();vm=UdonEditorManager.Instance.ConstructUdonVM();vm.LoadProgram(program);
        material=new Material(Shader.Find("TernaryStars/Communication/AV1 Intra"));
        foreach(int pass in new[]{2,9,10,15,17,18,19})if(!material.SetPass(pass))throw new Exception("Decoder shader unsupported: "+pass);
        Set("inputAvif",File.ReadAllBytes(job.file));Set("material",material);Set("frameBudgetMs",2f);Set("diagonalsPerFrame",8);
        var row=new Row{id=job.id,bytes=Get<byte[]>("inputAvif").Length};var timer=Stopwatch.StartNew();Event("BeginDecoding");
        while(Get<bool>("busy"))
        {
            var step=Stopwatch.StartNew();Event("StepDecoding");row.maxStepMs=Math.Max(row.maxStepMs,step.Elapsed.TotalMilliseconds);row.steps++;
            if(timer.Elapsed.TotalSeconds>120)throw new Exception("Decoder test timeout.");
        }
        row.cpuAndSubmitMs=timer.Elapsed.TotalMilliseconds;
        if(!Get<bool>("complete"))throw new Exception(Get<string>("error"));
        row.width=Get<int>("width");row.height=Get<int>("height");
        var texture=Get<RenderTexture>("filtered");
        AsyncGPUReadback.Request(texture,0,TextureFormat.RGBA32,request=>
        {
            try
            {
                row.totalMs=timer.Elapsed.TotalMilliseconds;if(request.hasError)throw new Exception("Decoder GPU readback failed.");
                File.WriteAllBytes(Root+"/"+job.id+".atlas.rgba",request.GetData<byte>().ToArray());
                var output=Get<RenderTexture>("resultTexture");var previous=RenderTexture.active;RenderTexture.active=output;
                var pixels=new Texture2D(row.width,row.height,TextureFormat.RGBA32,false);pixels.ReadPixels(new Rect(0,0,row.width,row.height),0,0);pixels.Apply();
                File.WriteAllBytes(Root+"/"+job.id+".png",pixels.EncodeToPNG());UnityEngine.Object.DestroyImmediate(pixels);RenderTexture.active=previous;
                foreach(string field in new[]{"quant","recon","filtered","mode","uvMode","scratch","output","upload"}){var value=Get<UnityEngine.Object>(field);if(value)UnityEngine.Object.DestroyImmediate(value);}
                UnityEngine.Object.DestroyImmediate(material);rows.Add(row);
                File.WriteAllText(Root+"/results.json",JsonUtility.ToJson(new Results{rows=rows.ToArray(),gpu=SystemInfo.graphicsDeviceName,cpu=SystemInfo.processorType},true));
                index++;active=false;
                if(index==jobs.jobs.Length){jobs=null;File.WriteAllText(Root+"/done.txt","PASS "+rows.Count+" real Udon/GPU decodes");}
            }
            catch(Exception e){Fail(e);}
        });
    }
}
#endif
