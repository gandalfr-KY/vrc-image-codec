// GPU submission is replaced only in the native harness. CPU decoding is production source.
using System;
using System.IO;
using System.Reflection;

namespace UnityEngine
{
public class RenderTexture
{
}
public class Texture2D
{
}
}
namespace VRC.SDK3.Rendering
{
}
namespace VRC.SDKBase
{
}

class DecoderTest
{
    static int Main(string[] args)
    {
        var decoder = new TernaryStars.TernaryAvifDecoder();
        decoder.inputAvif = File.ReadAllBytes(args[0]);
        decoder.material = new UnityEngine.Material();
        decoder.BeginDecoding();
        if (args.Length > 2 && args[2] == "cancel")
        {
            decoder.StepDecoding();
            decoder.CancelDecoding();
            if (decoder.busy || decoder.complete || decoder.resultTexture != null)
                return 2;
            decoder.BeginDecoding();
        }
        while (decoder.busy)
            decoder.StepDecoding();
        if (!decoder.complete)
        {
            Console.Error.WriteLine(decoder.error);
            return 1;
        }
        var bytes = (byte[])decoder.GetType()
                        .GetField("coefficients", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(decoder);
        File.WriteAllBytes(args[1], bytes);
        return 0;
    }
}
