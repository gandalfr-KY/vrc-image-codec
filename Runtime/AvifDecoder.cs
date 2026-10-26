// Restricted still-image AV1 decoding; AOM range arithmetic, BSD-2-Clause + PATENTS.
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
namespace VrcImageCodec
{
[AddComponentMenu("")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AvifDecoder : UdonSharpBehaviour
{
    public byte[] inputAvif;
    public Material material;
    [HideInInspector]
    public Renderer materialSource;
    [HideInInspector]
    public Material entropyMaterial;
    [HideInInspector]
    public Texture2D initialCdf;
    public float frameBudgetMs = 2;
    public int diagonalsPerFrame = 8;
    [HideInInspector]
    public bool subsampleChroma = true;
    [HideInInspector]
    public RenderTexture resultTexture;
    [HideInInspector]
    public bool busy, complete, readbackPending;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public int width, height, decodedBlocks;
    [HideInInspector]
    public float progress, elapsedSeconds, cpuSeconds, maxStepMs;
    private byte[] data, uploadBuffer;
    private byte[] statusReadback = new byte[4];
    private bool disableCdfUpdate;
    private int blockColumns, blockRows, decodedBlockCursor, blockLimit, stage, byteOffset, obuEnd, bitOffset,
        headerEnd, quantizerIndex, quantizerContext, diagonal;
    private float startedAt;
    private bool ownsMaterials;
    private Texture2D upload;
    private RenderTexture stateA, stateB, packets, status, quant, recon, filtered, mode, uvMode, scratch,
        chromaScratch, output;
    private int[] dcQuant = AvifQuantization.CreateDcQuant(), acQuant = AvifQuantization.CreateAcQuant();

    public void BeginDecoding()
    {
        if (busy || readbackPending) return;
        CancelDecoding();
        data = inputAvif;
        if (data == null || !material || !entropyMaterial || !materialSource || !initialCdf)
        {
            Fail(
                "AVIF decoding requires configured GPU materials/tables and a complete still-image payload.");
            return;
        }
        busy = true;
        startedAt = Time.realtimeSinceStartup;
        if (!ReadProfile()) return;
        if (!ownsMaterials)
        {
            // Slot order matches ConfigureGpuDecoder: reconstruction, entropy.
            Material[] materials = materialSource.materials;
            material = materials[0];
            entropyMaterial = materials[1];
            ownsMaterials = true;
        }
        blockColumns = width / 8;
        blockRows = height / 8;
        quantizerContext = quantizerIndex <= 20    ? 0
                           : quantizerIndex <= 60  ? 1
                           : quantizerIndex <= 120 ? 2
                                                   : 3;
        int inputRows = (data.Length + 4095) / 4096;
        if (!upload || upload.height != inputRows)
        {
            if (upload) Destroy(upload);
            upload = new Texture2D(1024, inputRows, TextureFormat.RGBA32, false, true);
            upload.filterMode = FilterMode.Point;
            upload.wrapMode = TextureWrapMode.Clamp;
        }
        if (uploadBuffer == null || uploadBuffer.Length != inputRows * 4096)
            uploadBuffer = new byte[inputRows * 4096];
        // Shader reads are bounded by the AVIF length, so unused padding need not be cleared.
        Array.Copy(data, uploadBuffer, data.Length);
        upload.LoadRawTextureData(uploadBuffer);
        upload.Apply(false, false);
        // Fixed shader layout: 4096 CDF contexts * 4 pixels, two range/cursor pixels,
        // then two neighbour-context pixels per block. This is state size, not an input-byte cap.
        int stateRows = (4096 * 4 + 2 + blockColumns * blockRows * 2 + 1023) / 1024;
        stateA = CodecBuffers.Surface(stateA, 1024, stateRows, RenderTextureFormat.ARGBFloat);
        stateB = CodecBuffers.Surface(stateB, 1024, stateRows, RenderTextureFormat.ARGBFloat);
        // Each packet has three plane headers plus 64 Y and 16/64 U,V coefficient words.
        packets = CodecBuffers.Surface(
            packets, 1024, (blockColumns * blockRows * (subsampleChroma ? 99 : 195) + 1023) / 1024,
            RenderTextureFormat.ARGB32);
        status = CodecBuffers.Surface(status, 1, 1, RenderTextureFormat.ARGB32);
        entropyMaterial.SetTexture("_Bytes", upload);
        entropyMaterial.SetTexture("_Initial", initialCdf);
        entropyMaterial.SetInt("_Start", bitOffset / 8);
        entropyMaterial.SetInt("_End", obuEnd);
        entropyMaterial.SetInt("_Cols", blockColumns);
        entropyMaterial.SetInt("_Rows", blockRows);
        entropyMaterial.SetInt("_Qctx", quantizerContext);
        entropyMaterial.SetInt("_Subsampled", subsampleChroma ? 1 : 0);
        entropyMaterial.SetInt("_DisableCdf", disableCdfUpdate ? 1 : 0);
        VRCGraphics.Blit(Texture2D.blackTexture, stateA, entropyMaterial, 0);
        decodedBlockCursor = stage = decodedBlocks = 0;
        blockLimit = ((blockColumns + 7) / 8) * ((blockRows + 7) / 8) * 64;
    }
    public void CancelDecoding()
    {
        busy = complete = false;
        error = "";
        progress = elapsedSeconds = cpuSeconds = maxStepMs = 0;
        resultTexture = null;
        data = null;
    }
    private void Update()
    {
        if (busy) StepDecoding();
    }
    public void StepDecoding()
    {
        if (!busy || readbackPending) return;
        float before = Time.realtimeSinceStartup;
        if (stage == 0)
        {
            float until = before + Mathf.Max(.25f, frameBudgetMs) * .001f;
            do
            {
                entropyMaterial.SetInt("_ChunkStart", decodedBlockCursor);
                entropyMaterial.SetTexture("_State", stateA);
                VRCGraphics.Blit(Texture2D.blackTexture, stateB, entropyMaterial, 3);
                entropyMaterial.SetInt("_OutputHeight", stateB.height);
                VRCGraphics.Blit(Texture2D.blackTexture, stateB, entropyMaterial, 1);
                entropyMaterial.SetInt("_OutputHeight", packets.height);
                VRCGraphics.Blit(Texture2D.blackTexture, packets, entropyMaterial, 2);
                RenderTexture swap = stateA;
                stateA = stateB;
                stateB = swap;
                decodedBlockCursor += 16;
            } while (decodedBlockCursor < blockLimit && Time.realtimeSinceStartup < until);
            progress = .75f * decodedBlockCursor / blockLimit;
            if (decodedBlockCursor >= blockLimit)
            {
                entropyMaterial.SetTexture("_State", stateA);
                VRCGraphics.Blit(Texture2D.blackTexture, status, entropyMaterial, 4);
                readbackPending = true;
                VRCAsyncGPUReadback.Request(status, 0, TextureFormat.RGBA32, this);
            }
        }
        else
            Reconstruct();
        float work = Time.realtimeSinceStartup - before;
        cpuSeconds += work;
        maxStepMs = Mathf.Max(maxStepMs, work * 1000);
        elapsedSeconds = Time.realtimeSinceStartup - startedAt;
    }
    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!busy) return;
        byte[] bytes = statusReadback;
        if (request.hasError || !request.TryGetData(bytes))
        {
            Fail("AVIF GPU readback failed.");
            return;
        }
        if (stage == 2)
        {
            resultTexture = output;
            busy = false;
            complete = true;
            progress = 1;
            elapsedSeconds = Time.realtimeSinceStartup - startedAt;
            return;
        }
        if (bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0)
        {
            Fail("Invalid or unsupported AV1 entropy stream.");
            return;
        }
        decodedBlocks = blockColumns * blockRows;
        BeginReconstruction();
    }
    private void Fail(string message)
    {
        busy = false;
        complete = false;
        error = message;
    }
    private int UInt32(int pos)
    {
        if (pos < 0 || pos > data.Length - 4)
        {
            Fail("Truncated AVIF box.");
            return 0;
        }
        uint size = ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) |
                    data[pos + 3];
        if (size > int.MaxValue)
        {
            Fail("Oversized AVIF box.");
            return 0;
        }
        return (int)size;
    }
    private bool Tag(int pos, string tag)
    {
        if (pos < 0 || pos > data.Length - 4) return false;
        for (int i = 0; i < 4; i++)
            if (data[pos + i] != tag[i]) return false;
        return true;
    }
    private int Byte()
    {
        if (byteOffset >= obuEnd)
        {
            Fail("Truncated AVIF payload.");
            return 0;
        }
        return data[byteOffset++];
    }
    private int Leb()
    {
        int value = 0;
        for (int i = 0; i < 4; i++)
        {
            int b = Byte();
            value |= (b & 127) << (7 * i);
            if (b < 128) return value;
        }
        Fail("Invalid OBU length.");
        return 0;
    }
    private int Bits(int count)
    {
        if (!busy || bitOffset + count > headerEnd * 8)
        {
            Fail("Truncated AV1 header.");
            return 0;
        }
        int value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | ((data[bitOffset >> 3] >> (7 - (bitOffset++ & 7))) & 1);
        return value;
    }
    private void Expect(int value, int count)
    {
        if (busy && Bits(count) != value)
            Fail("Unsupported AVIF profile; use this library's single-image encoder.");
    }
    private bool ReadProfile()
    {
        if (!Tag(4, "ftyp") || !Tag(8, "avif"))
        {
            Fail("Not an AVIF image.");
            return false;
        }
        int payload = -1;
        for (int pos = 0; pos < data.Length && busy;)
        {
            int size = UInt32(pos);
            if (!busy || size < 8 || size > data.Length - pos)
            {
                Fail("Invalid AVIF box bounds.");
                break;
            }
            if (Tag(pos + 4, "mdat"))
            {
                if (payload != -1)
                {
                    Fail("Multiple AVIF payloads.");
                    break;
                }
                payload = pos + 8;
                obuEnd = pos + size;
            }
            pos += size;
        }
        if (!busy) return false;
        if (payload < 0)
        {
            Fail("Missing AVIF image payload.");
            return false;
        }
        byteOffset = payload;
        if (Byte() != 0x12 || Byte() != 0 || Byte() != 0x0a)
        {
            Fail("Unsupported AV1 sequence.");
            return false;
        }
        int sequenceLength = Leb();
        if (sequenceLength < 1 || sequenceLength > obuEnd - byteOffset)
        {
            Fail("Invalid sequence bounds.");
            return false;
        }
        bitOffset = byteOffset * 8;
        headerEnd = byteOffset + sequenceLength;
        int profile = Bits(3);
        if (profile != 0 && profile != 1)
        {
            Fail("Unsupported AV1 color profile.");
            return false;
        }
        subsampleChroma = profile == 0;
        Expect(1, 1);
        Expect(1, 1);
        int level = Bits(5);
        Expect(15, 4);
        Expect(15, 4);
        width = Bits(16) + 1;
        height = Bits(16) + 1;
        if (width < 16 || height < 16 || width > 1920 || height > 1920 || width % 8 != 0 || height % 8 != 0 ||
            width * height > 2073600 || level != (width * height > 665856 ? 8 : 4))
        {
            Fail("Unsupported AVIF dimensions or AV1 level.");
            return false;
        }
        Expect(2, 6);
        Expect(0, 1);
        if (subsampleChroma) Expect(0, 1);
        Expect(1, 1);
        Expect(1, 8);
        Expect(13, 8);
        Expect(6, 8);
        Expect(1, 1);
        if (subsampleChroma) Expect(0, 2);
        Expect(0, 1);
        Expect(0, 1);
        Expect(1, 1);
        while (busy && bitOffset < headerEnd * 8)
            Expect(0, 1);
        byteOffset = headerEnd;
        if (!busy || Byte() != 0x32)
        {
            if (busy) Fail("Missing AV1 frame.");
            return false;
        }
        int frameLength = Leb();
        if (frameLength != obuEnd - byteOffset)
        {
            Fail("Invalid AV1 frame length.");
            return false;
        }
        bitOffset = byteOffset * 8;
        headerEnd = obuEnd;
        // Probability adaptation is signaled per image; reconstruction is unchanged.
        disableCdfUpdate = Bits(1) != 0;
        Expect(0, 1);
        Expect(0, 1);
        Expect(1, 1);
        if (width > 64) Expect(0, 1);
        if (height > 64) Expect(0, 1);
        quantizerIndex = Bits(8);
        Expect(0, 1);
        for (int i = 0; i < 2; i++)
        {
            Expect(1, 1);
            Expect(112, 7);
        }
        Expect(0, 1);
        Expect(0, 2);
        Expect(0, 6);
        Expect(0, 6);
        Expect(0, 3);
        Expect(0, 1);
        Expect(1, 2);
        Expect(0, 2);
        Expect(26, 6);
        Expect(0, 6);
        Expect(0, 1);
        Expect(1, 1);
        while (busy && (bitOffset & 7) != 0)
            Expect(0, 1);
        byteOffset = bitOffset / 8;
        if (byteOffset >= obuEnd || quantizerIndex == 0)
            Fail("Missing AV1 tile or unsupported lossless mode.");
        return busy;
    }

    private void BeginReconstruction()
    {
        int atlasRows = blockRows * (subsampleChroma ? 12 : 24);
        quant = CodecBuffers.Surface(quant, width, atlasRows, RenderTextureFormat.ARGB32);
        recon = CodecBuffers.Surface(recon, width, atlasRows, RenderTextureFormat.ARGB32);
        filtered = CodecBuffers.Surface(filtered, width, atlasRows, RenderTextureFormat.ARGB32);
        mode = CodecBuffers.Surface(mode, blockColumns, blockRows, RenderTextureFormat.ARGB32);
        uvMode = CodecBuffers.Surface(uvMode, blockColumns, blockRows, RenderTextureFormat.ARGB32);
        scratch = CodecBuffers.Surface(scratch, blockColumns * 16, blockRows, RenderTextureFormat.ARGBFloat);
        if (!subsampleChroma)
            chromaScratch = CodecBuffers.Surface(chromaScratch, blockColumns * 32, blockRows,
                                                 RenderTextureFormat.ARGBFloat);
        output = CodecBuffers.Surface(output, width, height, RenderTextureFormat.ARGB32, true);
        material.SetInt("_Chroma444", subsampleChroma ? 0 : 1);
        material.SetVector("_PhotoSize", new Vector4(width, height, blockColumns, blockRows));
        material.SetVector("_AtlasSize", new Vector4(width, atlasRows, 0, 0));
        material.SetVector("_PackedSize", new Vector4(packets.width, packets.height, 0, 0));
        material.SetTexture("_DecodedTex", packets);
        material.SetInt("_QuantDc", dcQuant[quantizerIndex]);
        material.SetInt("_QuantAc", acQuant[quantizerIndex]);
        material.SetInt("_QuantUvDc", dcQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        material.SetInt("_QuantUvAc", acQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        material.SetTexture("_QuantTex", quant);
        material.SetTexture("_ReconTex", recon);
        material.SetTexture("_ModeTex", mode);
        material.SetTexture("_UvModeTex", uvMode);
        material.SetTexture("_CflTex", uvMode);
        material.SetTexture("_OptimizedTex", scratch);
        material.SetInt("_DecodeChroma", 0);
        VRCGraphics.Blit(Texture2D.blackTexture, mode, material, 17);
        material.SetInt("_DecodeChroma", 1);
        VRCGraphics.Blit(Texture2D.blackTexture, uvMode, material, 17);
        VRCGraphics.Blit(Texture2D.blackTexture, recon);
        data = null;
        stage = 1;
        diagonal = 0;
    }
    private void Reconstruct()
    {
        // Per-instance material properties are set before each submission so duplicated decoders cannot mix
        // inputs.
        material.SetInt("_Chroma444", subsampleChroma ? 0 : 1);
        material.SetVector("_PhotoSize", new Vector4(width, height, blockColumns, blockRows));
        material.SetVector("_AtlasSize", new Vector4(width, blockRows * (subsampleChroma ? 12 : 24), 0, 0));
        material.SetVector("_PackedSize", new Vector4(packets.width, packets.height, 0, 0));
        material.SetTexture("_DecodedTex", packets);
        material.SetTexture("_QuantTex", quant);
        material.SetTexture("_ReconTex", recon);
        material.SetTexture("_ModeTex", mode);
        material.SetTexture("_UvModeTex", uvMode);
        material.SetTexture("_CflTex", uvMode);
        material.SetTexture("_OptimizedTex", scratch);
        material.SetInt("_QuantDc", dcQuant[quantizerIndex]);
        material.SetInt("_QuantAc", acQuant[quantizerIndex]);
        material.SetInt("_QuantUvDc", dcQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        material.SetInt("_QuantUvAc", acQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        int diagonals = blockColumns + 2 * blockRows - 2;
        for (int i = 0; i < Mathf.Max(1, diagonalsPerFrame) && diagonal < diagonals; i++, diagonal++)
        {
            material.SetInt("_Diagonal", diagonal);
            for (int plane = 0; plane < 2; plane++)
            {
                material.SetInt("_ChromaStage", plane);
                RenderTexture work = !subsampleChroma && plane != 0 ? chromaScratch : scratch;
                material.SetTexture("_OptimizedTex", work);
                VRCGraphics.Blit(Texture2D.blackTexture, quant, material, 18);
                VRCGraphics.Blit(Texture2D.blackTexture, work, material, 15);
                VRCGraphics.Blit(Texture2D.blackTexture, recon, material, 2);
            }
        }
        progress = .75f + .25f * diagonal / diagonals;
        if (diagonal < diagonals) return;
        VRCGraphics.Blit(Texture2D.blackTexture, mode, material, 9);
        VRCGraphics.Blit(recon, filtered, material, 10);
        material.SetTexture("_ReconTex", filtered);
        VRCGraphics.Blit(Texture2D.blackTexture, output, material, 19);
        // Fence the final RGB pass before publishing completion; pixels stay on the GPU.
        VRCGraphics.Blit(output, status);
        stage = 2;
        readbackPending = true;
        VRCAsyncGPUReadback.Request(status, 0, TextureFormat.RGBA32, this);
    }

    private void OnDestroy()
    {
        if (ownsMaterials)
        {
            Destroy(material);
            Destroy(entropyMaterial);
        }
        if (upload) Destroy(upload);
        CodecBuffers.Release(new RenderTexture[] { stateA, stateB, packets, status, quant, recon, filtered,
                                                   mode, uvMode, scratch, chromaScratch, output });
    }
}
}
