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
