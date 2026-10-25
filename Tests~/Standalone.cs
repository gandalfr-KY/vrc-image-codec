// Replays recorded GPU packets through the production entropy/container code. GPU dispatch is mocked.
using System;
using System.IO;
using System.Diagnostics;
namespace UdonSharp
{
public class UdonSharpBehaviour
{
    public static implicit operator bool(UdonSharpBehaviour v)
    {
        return v != null;
    }
}
public enum BehaviourSyncMode
{
    None
}
public class UdonBehaviourSyncModeAttribute : Attribute
{
    public UdonBehaviourSyncModeAttribute(BehaviourSyncMode v)
    {
    }
}
}
namespace UnityEngine
{
public class HideInInspector : Attribute
{
}
public class RangeAttribute : Attribute
{
    public RangeAttribute(int a, int b)
    {
    }
}
public static class Time
{
    static readonly Stopwatch s = Stopwatch.StartNew();
    public static float realtimeSinceStartup
    {
        get {
            return (float)s.Elapsed.TotalSeconds;
        }
    }
}
public static class Mathf
{
    public static int Min(int a, int b)
    {
        return Math.Min(a, b);
    }
    public static int Abs(int a)
    {
        return Math.Abs(a);
    }
    public static float Max(float a, float b)
    {
        return Math.Max(a, b);
    }
    public static int Clamp(int v, int a, int b)
    {
        return Math.Min(b, Math.Max(a, v));
    }
}
public class Material
{
    public static implicit operator bool(Material m)
    {
        return m != null;
    }
    public void SetInt(string name, int value)
    {
    }
}
public static class Debug
{
    public static void Log(object v)
    {
        Console.WriteLine(v);
    }
    public static void LogError(object v)
    {
        Console.Error.WriteLine(v);
    }
}
}
namespace TernaryStars
{
public class TernaryAvifGpuTransform : UdonSharp.UdonSharpBehaviour
{
    public UnityEngine.Material material = new UnityEngine.Material();
    public bool busy, complete, readbackPending;
    public int width, height, quantDc, quantAc;
    public byte[] result;
    public string error;
    public void BeginTransform()
    {
        busy = true;
        complete = false;
    }
    public void CancelTransform()
    {
        busy = false;
        complete = false;
    }
}
}
class Program
{
    static int Main(string[] args)
    {
        var e = new TernaryStars.TernaryAvifEncoder();
        e.width = int.Parse(args[1]);
        e.height = int.Parse(args[2]);
        e.quality = int.Parse(args[4]);
        e.frameBudgetMs = float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
        e.gpuTransform = new TernaryStars.TernaryAvifGpuTransform();
        if (args.Length > 6 && args[6] == "missing")
            e.gpuTransform = null;
        e.BeginEncoding();
        if (!e.busy)
        {
            Console.Error.WriteLine(e.error);
            return 1;
        }
        e.gpuTransform.result = File.ReadAllBytes(args[0]);
        e.gpuTransform.busy = false;
        e.gpuTransform.complete = true;
        while (e.busy)
            e.StepEncoding();
        if (!e.complete)
        {
            Console.Error.WriteLine(e.error);
            return 1;
        }
        File.WriteAllBytes(args[3], e.resultAvif);
        return 0;
    }
}

// Native reference harness only; the actual Udon trial uses Unity's vector API.
namespace UnityEngine
{
public struct Vector3
{
    public float x, y, z;
    public Vector3(float a, float b, float c)
    {
        x = a;
        y = b;
        z = c;
    }
    public static Vector3 operator *(Vector3 a, float f)
    {
        return new Vector3(a.x * f, a.y * f, a.z * f);
    }
    public static Vector3 operator +(Vector3 a, Vector3 b)
    {
        return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
    }
}
public struct Vector3Int
{
    public int x, y, z;
    public Vector3Int(int a, int b, int c)
    {
        x = a;
        y = b;
        z = c;
    }
    public int this[int n]
    {
        get
        {
            return n == 0 ? x : n == 1 ? y : z;
        }
    }
    public static explicit operator Vector3(Vector3Int a)
    {
        return new Vector3(a.x, a.y, a.z);
    }
    public static Vector3Int FloorToInt(Vector3 a)
    {
        return new Vector3Int((int)System.Math.Floor(a.x), (int)System.Math.Floor(a.y), (int)System.Math.Floor(a.z));
    }
}
}
