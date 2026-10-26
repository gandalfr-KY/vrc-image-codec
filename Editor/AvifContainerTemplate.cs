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
        var bytes = new byte[1024 * 4 * 4];
        var sizes = new Vector4();
        for (int i = 0; i < 4; i++)
        {
            var header = builder.Build(1 << (i * 7));
            Buffer.BlockCopy(header, 0, bytes, i * 4096, header.Length);
            sizes[i] = header.Length;
        }
        if (entropy.containerHeaders && entropy.containerHeaders.height != 4)
        {
            entropy.containerHeaders.Reinitialize(1024, 4, TextureFormat.RGBA32, false);
        }
        if (!entropy.containerHeaders)
        {
            entropy.containerHeaders = new Texture2D(1024, 4, TextureFormat.RGBA32, false, true);
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
    private void WriteHeaderBits(int value, int count)
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
        WriteHeaderBits(subsampleChroma ? 0 : 1, 3);
        WriteHeaderBits(1, 1);
        WriteHeaderBits(1, 1);
        WriteHeaderBits(width * height > 665856 ? 8 : 4, 5);
        WriteHeaderBits(15, 4);
        WriteHeaderBits(15, 4);
        WriteHeaderBits(width - 1, 16);
        WriteHeaderBits(height - 1, 16);
        WriteHeaderBits(2, 6);
        WriteHeaderBits(0, 1);
        if (subsampleChroma) WriteHeaderBits(0, 1);
        WriteHeaderBits(1, 1);
        WriteHeaderBits(1, 8);
        WriteHeaderBits(13, 8);
        WriteHeaderBits(6, 8);
        WriteHeaderBits(1, 1);
        if (subsampleChroma) WriteHeaderBits(0, 2);
        WriteHeaderBits(0, 1);
        WriteHeaderBits(0, 1);
        WriteHeaderBits(1, 1);
        var result = new byte[(headerBits + 7) / 8];
        Array.Copy(header, result, result.Length);
        return result;
    }
    private byte[] FrameHeader()
    {
        header = new byte[64];
        headerBits = 0;
        WriteHeaderBits(0, 1);
        WriteHeaderBits(0, 1);
        WriteHeaderBits(0, 1);
        WriteHeaderBits(1, 1);
        if (width > 64) WriteHeaderBits(0, 1);
        if (height > 64) WriteHeaderBits(0, 1);
        WriteHeaderBits(quantizerIndex, 8);
        WriteHeaderBits(0, 1);
        for (int component = 0; component < 2; component++)
        {
            WriteHeaderBits(1, 1);
            WriteHeaderBits((-16) & 127, 7);
        }
        WriteHeaderBits(0, 1); // Quantization matrices disabled in the selected profile.
        WriteHeaderBits(0, 2);
        WriteHeaderBits(0, 6);
        WriteHeaderBits(0, 6);
        WriteHeaderBits(0, 3);
        WriteHeaderBits(0, 1);
        WriteHeaderBits(1, 2);
        WriteHeaderBits(0, 2);
        WriteHeaderBits(26, 6);
        WriteHeaderBits(0, 6);
        WriteHeaderBits(0, 1);
        WriteHeaderBits(1, 1);
        var result = new byte[(headerBits + 7) / 8];
        Array.Copy(header, result, result.Length);
        return result;
    }
    private void WriteByte(int value)
    {
        file[fileCursor++] = (byte)(value & 255);
    }
    private void WriteBigEndian(int value, int bytes)
    {
        for (int i = bytes - 1; i >= 0; i--)
            WriteByte(value >> (8 * i));
    }
    private void WriteAscii(string value)
    {
        for (int i = 0; i < value.Length; i++)
            WriteByte(value[i]);
    }
    private void PatchSize(int at, int value)
    {
        for (int i = 0; i < 4; i++)
            file[at + i] = (byte)((value >> (24 - i * 8)) & 255);
    }
    private int BeginBox(string type)
    {
        int at = fileCursor;
        WriteBigEndian(0, 4);
        WriteAscii(type);
        return at;
    }
    private int BeginFullBox(string type, int version)
    {
        int at = BeginBox(type);
        WriteBigEndian(version << 24, 4);
        return at;
    }
    private void EndBox(int at)
    {
        PatchSize(at, fileCursor - at);
    }
    private void WriteLeb128(int value)
    {
        do
        {
            int part = value & 127;
            value >>= 7;
            WriteByte(part | (value != 0 ? 128 : 0));
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
        int ftyp = BeginBox("ftyp");
        WriteAscii("avif");
        WriteBigEndian(0, 4);
        WriteAscii(subsampleChroma ? "avifmif1miafMA1B" : "avifmif1miafMA1A");
        EndBox(ftyp);
        int meta = BeginFullBox("meta", 0), handler = BeginFullBox("hdlr", 0);
        WriteBigEndian(0, 4);
        WriteAscii("pict");
        for (int i = 0; i < 13; i++)
            WriteByte(0);
        EndBox(handler);
        int primary = BeginFullBox("pitm", 0);
        WriteBigEndian(1, 2);
        EndBox(primary);
        int location = BeginFullBox("iloc", 0);
        WriteByte(0x44);
        WriteByte(0);
        WriteBigEndian(1, 2);
        WriteBigEndian(1, 2);
        WriteBigEndian(0, 2);
        WriteBigEndian(1, 2);
        int offsetMarker = fileCursor;
        WriteBigEndian(0, 4);
        int sizeMarker = fileCursor;
        WriteBigEndian(0, 4);
        EndBox(location);
        int info = BeginFullBox("iinf", 0);
        WriteBigEndian(1, 2);
        int entry = BeginFullBox("infe", 2);
        WriteBigEndian(1, 2);
        WriteBigEndian(0, 2);
        WriteAscii("av01Color");
        WriteByte(0);
        EndBox(entry);
        EndBox(info);
        int properties = BeginBox("iprp"), container = BeginBox("ipco"), size = BeginFullBox("ispe", 0);
        WriteBigEndian(width, 4);
        WriteBigEndian(height, 4);
        EndBox(size);
        int pixels = BeginFullBox("pixi", 0);
        WriteByte(3);
        WriteByte(8);
        WriteByte(8);
        WriteByte(8);
        EndBox(pixels);
        int config = BeginBox("av1C");
        WriteByte(0x81);
        WriteByte((subsampleChroma ? 0 : 32) | (width * height > 665856 ? 8 : 4));
        WriteByte(subsampleChroma ? 12 : 0);
        WriteByte(0);
        EndBox(config);
        int color = BeginBox("colr");
        WriteAscii("nclx");
        WriteBigEndian(1, 2);
        WriteBigEndian(13, 2);
        WriteBigEndian(6, 2);
        WriteByte(128);
        EndBox(color);
        EndBox(container);
        int mapping = BeginFullBox("ipma", 0);
        WriteBigEndian(1, 4);
        WriteBigEndian(1, 2);
        WriteByte(4);
        WriteByte(1);
        WriteByte(2);
        WriteByte(0x83);
        WriteByte(4);
        EndBox(mapping);
        EndBox(properties);
        EndBox(meta);
        int data = BeginBox("mdat"), contentStart = fileCursor;
        WriteByte(0x12);
        WriteByte(0);
        WriteByte(0x0a);
        WriteLeb128(sequence.Length);
        Append(sequence);
        WriteByte(0x32);
        WriteLeb128(frame.Length + outputSize);
        Append(frame);
        PatchSize(offsetMarker, contentStart);
        PatchSize(sizeMarker, fileCursor - contentStart + outputSize);
        PatchSize(data, fileCursor - data + outputSize);
        patch = new Vector4(sizeMarker, data, contentStart, frame.Length);
        byte[] result = new byte[fileCursor];
        Array.Copy(file, result, fileCursor);
        return result;
    }
}
}
#endif
