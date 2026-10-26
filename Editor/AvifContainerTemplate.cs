#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
    // Placeholder: the GPU assembler patches the per-image quantizer into this template.
    private const int TemplateQuantizerIndex = 0;
    private Vector4 patch;
    public static void Configure(AvifGpuEntropy entropy, int width, int height, string directory)
    {
        var builder = new AvifContainerTemplate { width = width, height = height,
                                                  subsampleChroma = entropy.subsampleChroma };
        // Four LEB128 payload-length layouts, packed as one RGBA row per layout.
        var headers = new byte[4][];
        var sizes = new Vector4();
        int columns = 0;
        for (int i = 0; i < headers.Length; i++)
        {
            headers[i] = builder.Build(1 << (i * 7));
            sizes[i] = headers[i].Length;
            columns = Mathf.Max(columns, (headers[i].Length + 3) / 4);
        }
        var bytes = new byte[columns * 4 * headers.Length];
        for (int i = 0; i < headers.Length; i++)
            Buffer.BlockCopy(headers[i], 0, bytes, i * columns * 4, headers[i].Length);
        if (entropy.containerHeaders &&
            (entropy.containerHeaders.width != columns || entropy.containerHeaders.height != headers.Length))
            entropy.containerHeaders.Reinitialize(columns, headers.Length, TextureFormat.RGBA32, false);
        if (!entropy.containerHeaders)
        {
            entropy.containerHeaders =
                new Texture2D(columns, headers.Length, TextureFormat.RGBA32, false, true);
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
    private List<byte> file, header;
    private int headerBits;
    private void WriteHeaderBits(int value, int count)
    {
        for (int k = count - 1; k >= 0; k--)
        {
            if ((headerBits & 7) == 0) header.Add(0);
            header[headerBits >> 3] |= (byte)(((value >> k) & 1) << (7 - (headerBits & 7)));
            headerBits++;
        }
    }
    private byte[] SequenceHeader()
    {
        header = new List<byte>();
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
        return header.ToArray();
    }
    private byte[] FrameHeader()
    {
        header = new List<byte>();
        headerBits = 0;
        WriteHeaderBits(0, 1);
        WriteHeaderBits(0, 1);
        WriteHeaderBits(0, 1);
        WriteHeaderBits(1, 1);
        if (width > 64) WriteHeaderBits(0, 1);
        if (height > 64) WriteHeaderBits(0, 1);
        WriteHeaderBits(TemplateQuantizerIndex, 8);
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
        return header.ToArray();
    }
    private void WriteByte(int value)
    {
        file.Add((byte)(value & 255));
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
        int at = file.Count;
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
        PatchSize(at, file.Count - at);
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
        file.AddRange(bytes);
    }
    private byte[] Build(int outputSize)
    {
        byte[] sequence = SequenceHeader(), frame = FrameHeader();
        file = new List<byte>();
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
        int offsetMarker = file.Count;
        WriteBigEndian(0, 4);
        int sizeMarker = file.Count;
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
        int data = BeginBox("mdat"), contentStart = file.Count;
        WriteByte(0x12);
        WriteByte(0);
        WriteByte(0x0a);
        WriteLeb128(sequence.Length);
        Append(sequence);
        WriteByte(0x32);
        WriteLeb128(frame.Length + outputSize);
        Append(frame);
        PatchSize(offsetMarker, contentStart);
        PatchSize(sizeMarker, file.Count - contentStart + outputSize);
        PatchSize(data, file.Count - data + outputSize);
        patch = new Vector4(sizeMarker, data, contentStart, frame.Length);
        return file.ToArray();
    }
}
}
#endif
