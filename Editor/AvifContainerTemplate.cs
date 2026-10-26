#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEditor;
using VrcImageCodec;
namespace VrcAvif.Encoder.Editor
{
// Fixed ISO-BMFF/AV1 header bytes belong to configuration, not per-photo encoding.
public class AvifContainerTemplate
{
    private int width, height;
    private bool subsampleChroma;
    private const int quantizerIndex = 0;
    private Vector4 patch;
    public static void Configure(AvifGpuEntropy entropy, int width, int height, string directory)
    {
        var builder = new AvifContainerTemplate { width = width, height = height,
                                                  subsampleChroma = entropy.subsampleChroma };
        var bytes = new byte[1024 * 3 * 4];
        var sizes = new Vector4();
        for (int i = 0; i < 3; i++)
        {
            var header = builder.Build(i == 0 ? 1 : i == 1 ? 128 : 16384);
            Buffer.BlockCopy(header, 0, bytes, i * 4096, header.Length);
            sizes[i] = header.Length;
        }
        if (!entropy.containerHeaders)
        {
            entropy.containerHeaders = new Texture2D(1024, 3, TextureFormat.RGBA32, false, true);
            entropy.containerHeaders.name = "AVIF container headers";
            entropy.containerHeaders.filterMode = FilterMode.Point;
            AssetDatabase.CreateAsset(entropy.containerHeaders, AssetDatabase.GenerateUniqueAssetPath(
                                                                    directory + "/Container.asset"));
        }
        entropy.containerHeaders.LoadRawTextureData(bytes);
        entropy.containerHeaders.Apply(false, false);
        entropy.headerSizes = sizes;
        entropy.headerPatch = builder.patch;
        entropy.quantizerBit = 4 + (width > 64 ? 1 : 0) + (height > 64 ? 1 : 0);
        EditorUtility.SetDirty(entropy.containerHeaders);
        EditorUtility.SetDirty(entropy.rangeMaterial);
    }
    private byte[] file, header;
    private int fileCursor, headerBits;
    private void HeaderBits(int value, int count)
    {
        for (int k = count - 1; k >= 0; k--)
        {
            if (((value >> k) & 1) != 0) header[headerBits >> 3] |= (byte)(1 << (7 - (headerBits & 7)));
            headerBits++;
        }
    }
    private byte[] SequenceHeader()
    {
        header = new byte[64];
        headerBits = 0;
        HeaderBits(subsampleChroma ? 0 : 1, 3);
        HeaderBits(1, 1);
        HeaderBits(1, 1);
        HeaderBits(width * height > 665856 ? 8 : 4, 5);
        HeaderBits(15, 4);
        HeaderBits(15, 4);
        HeaderBits(width - 1, 16);
        HeaderBits(height - 1, 16);
        HeaderBits(2, 6);
        HeaderBits(0, 1);
        if (subsampleChroma) HeaderBits(0, 1);
        HeaderBits(1, 1);
        HeaderBits(1, 8);
        HeaderBits(13, 8);
        HeaderBits(6, 8);
        HeaderBits(1, 1);
        if (subsampleChroma) HeaderBits(0, 2);
        HeaderBits(0, 1);
        HeaderBits(0, 1);
        HeaderBits(1, 1);
        var result = new byte[(headerBits + 7) / 8];
        Array.Copy(header, result, result.Length);
        return result;
    }
    private byte[] FrameHeader()
    {
        header = new byte[64];
        headerBits = 0;
        HeaderBits(0, 1);
        HeaderBits(0, 1);
        HeaderBits(0, 1);
        HeaderBits(1, 1);
        if (width > 64) HeaderBits(0, 1);
        if (height > 64) HeaderBits(0, 1);
        HeaderBits(quantizerIndex, 8);
        HeaderBits(0, 1);
        for (int component = 0; component < 2; component++)
        {
            HeaderBits(1, 1);
            HeaderBits((-16) & 127, 7);
        }
        HeaderBits(0, 1); // Quantization matrices disabled in the selected profile.
        HeaderBits(0, 2);
        HeaderBits(0, 6);
        HeaderBits(0, 6);
        HeaderBits(0, 3);
        HeaderBits(0, 1);
        HeaderBits(1, 2);
        HeaderBits(0, 2);
        HeaderBits(26, 6);
        HeaderBits(0, 6);
        HeaderBits(0, 1);
        HeaderBits(1, 1);
        var result = new byte[(headerBits + 7) / 8];
        Array.Copy(header, result, result.Length);
        return result;
    }
    private void FByte(int value)
    {
        file[fileCursor++] = (byte)(value & 255);
    }
    private void UInt(int value, int bytes)
    {
        for (int i = bytes - 1; i >= 0; i--)
            FByte(value >> (8 * i));
    }
    private void Text(string value)
    {
        for (int i = 0; i < value.Length; i++)
            FByte(value[i]);
    }
    private void Patch(int at, int value)
    {
        for (int i = 0; i < 4; i++)
            file[at + i] = (byte)((value >> (24 - i * 8)) & 255);
    }
    private int Box(string type)
    {
        int at = fileCursor;
        UInt(0, 4);
        Text(type);
        return at;
    }
    private int Full(string type, int version)
    {
        int at = Box(type);
        UInt(version << 24, 4);
        return at;
    }
    private void End(int at)
    {
        Patch(at, fileCursor - at);
    }
    private void Leb(int value)
    {
        do
        {
            int part = value & 127;
            value >>= 7;
            FByte(part | (value != 0 ? 128 : 0));
        } while (value != 0);
    }
    private void Append(byte[] bytes)
    {
        Array.Copy(bytes, 0, file, fileCursor, bytes.Length);
        fileCursor += bytes.Length;
    }
    private byte[] Build(int outputSize)
    {
        byte[] sequence = SequenceHeader(), frame = FrameHeader();
        file = new byte[600];
        fileCursor = 0;
        int ftyp = Box("ftyp");
        Text("avif");
        UInt(0, 4);
        Text(subsampleChroma ? "avifmif1miafMA1B" : "avifmif1miafMA1A");
        End(ftyp);
        int meta = Full("meta", 0), handler = Full("hdlr", 0);
        UInt(0, 4);
        Text("pict");
        for (int i = 0; i < 13; i++)
            FByte(0);
        End(handler);
        int primary = Full("pitm", 0);
        UInt(1, 2);
        End(primary);
        int location = Full("iloc", 0);
        FByte(0x44);
        FByte(0);
        UInt(1, 2);
        UInt(1, 2);
        UInt(0, 2);
        UInt(1, 2);
        int offsetMarker = fileCursor;
        UInt(0, 4);
        int sizeMarker = fileCursor;
        UInt(0, 4);
        End(location);
        int info = Full("iinf", 0);
        UInt(1, 2);
        int entry = Full("infe", 2);
        UInt(1, 2);
        UInt(0, 2);
        Text("av01Color");
        FByte(0);
        End(entry);
        End(info);
        int properties = Box("iprp"), container = Box("ipco"), size = Full("ispe", 0);
        UInt(width, 4);
        UInt(height, 4);
        End(size);
        int pixels = Full("pixi", 0);
        FByte(3);
        FByte(8);
        FByte(8);
        FByte(8);
        End(pixels);
        int config = Box("av1C");
        FByte(0x81);
        FByte((subsampleChroma ? 0 : 32) | (width * height > 665856 ? 8 : 4));
        FByte(subsampleChroma ? 12 : 0);
        FByte(0);
        End(config);
        int color = Box("colr");
        Text("nclx");
        UInt(1, 2);
        UInt(13, 2);
        UInt(6, 2);
        FByte(128);
        End(color);
        End(container);
        int mapping = Full("ipma", 0);
        UInt(1, 4);
        UInt(1, 2);
        FByte(4);
        FByte(1);
        FByte(2);
        FByte(0x83);
        FByte(4);
        End(mapping);
        End(properties);
        End(meta);
        int data = Box("mdat"), contentStart = fileCursor;
        FByte(0x12);
        FByte(0);
        FByte(0x0a);
        Leb(sequence.Length);
        Append(sequence);
        FByte(0x32);
        Leb(frame.Length + outputSize);
        Append(frame);
        Patch(offsetMarker, contentStart);
        Patch(sizeMarker, fileCursor - contentStart + outputSize);
        Patch(data, fileCursor - data + outputSize);
        patch = new Vector4(sizeMarker, data, contentStart, frame.Length);
        byte[] result = new byte[fileCursor];
        Array.Copy(file, result, fileCursor);
        return result;
    }
}
}
#endif
