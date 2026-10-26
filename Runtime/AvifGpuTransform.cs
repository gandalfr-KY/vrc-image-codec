using UdonSharp;
using UnityEngine;
using UnityEngine.Serialization;
using VRC.SDKBase;

namespace VrcImageCodec
{
// Fragment shaders only: no CUDA, native plug-in, ComputeShader or CPU thread.
// x+2y wavefronts preserve left, above and above-right reconstructed neighbors.
[AddComponentMenu("")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AvifGpuTransform : UdonSharpBehaviour
{
    // Keep these aligned with the pass order in Av1Intra.shader.
    private const int PredictionPass = 0, QuantizePass = 1, ReconstructPass = 2, PackPass = 3;
    private const int CflPass = 4, OptimizePass = 5, CandidatePass = 6, UvCandidatePass = 7, UvModePass = 8;
    private const int CdefDirectionPass = 9, CdefFilterPass = 10, PredictionCostPass = 11, ShortlistPass = 12;
    private const int PrepareSourcePass = 13, ChromaOptimizePass = 14, InverseRowsPass = 15,
                      CflCostsPass = 16;
    public Material material;
    public RenderTexture modeTexture, cflTexture, optimizedTexture, candidateTexture;
    [FormerlySerializedAs("uvCandidates")]
    public RenderTexture chromaCandidateTexture;
    [FormerlySerializedAs("uvModeTexture")]
    public RenderTexture chromaModeTexture;
    [FormerlySerializedAs("modeCosts")]
    public RenderTexture predictionCostTexture;
    [FormerlySerializedAs("shortlist")]
    public RenderTexture predictionShortlistTexture;
    [FormerlySerializedAs("quantA")]
    public RenderTexture quantizedTexture;
    [FormerlySerializedAs("reconA")]
    public RenderTexture reconstructionTexture;
    [FormerlySerializedAs("reconB")]
    public RenderTexture filteredTexture;
    public RenderTexture packedTexture;
    public RenderTexture inputTexture, sourceYuvTexture;
    public int width = 640, height = 360;
    public int diagonalsPerFrame = 4;
    public bool subsampleChroma = true;
    public RenderTexture chromaTexture;
    public bool linearInput = true, flipSourceY = true;
    [HideInInspector]
    public int quantDc, quantAc;
    [HideInInspector]
    public bool busy, complete;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public float elapsedSeconds, submitCpuSeconds, maxStepMs;
    private int diagonal, diagonals;
    private float startedAt;

    public void BeginTransform()
    {
        CancelTransform();
        // Setup owns intermediate texture layouts; runtime checks only the public input boundary.
        if (!material || !inputTexture || !sourceYuvTexture || !quantizedTexture || !reconstructionTexture ||
            !filteredTexture || !packedTexture || !modeTexture || !cflTexture || !optimizedTexture ||
            !candidateTexture || !chromaCandidateTexture || !chromaModeTexture || !predictionCostTexture ||
            !predictionShortlistTexture || (!subsampleChroma && !chromaTexture) || width < 16 ||
            height < 16 || width > 1920 || height > 1920 || width * height > 2073600 || width % 8 != 0 ||
            height % 8 != 0 || inputTexture.width != width || inputTexture.height != height || quantDc <= 0 ||
            quantAc <= 0 || diagonalsPerFrame < 1 || diagonalsPerFrame > 16)
        {
            error = "Configure GPU resources for the input dimensions before encoding.";
            return;
        }
        int columns = width / 8, rows = height / 8;
        startedAt = Time.realtimeSinceStartup;
        elapsedSeconds = submitCpuSeconds = maxStepMs = 0;
        if (!subsampleChroma) VRCGraphics.Blit(Texture2D.blackTexture, chromaModeTexture);
        diagonal = 0;
        diagonals = columns + 2 * rows - 2;
        material.SetInt("_Chroma444", subsampleChroma ? 0 : 1);
        material.SetVector("_PhotoSize", new Vector4(width, height, columns, rows));
        material.SetVector("_AtlasSize", new Vector4(quantizedTexture.width, quantizedTexture.height, 0, 0));
        material.SetVector("_PackedSize", new Vector4(packedTexture.width, packedTexture.height, 0, 0));
        material.SetInt("_QuantDc", quantDc);
        material.SetInt("_QuantAc", quantAc);
        material.SetInt("_LinearInput", linearInput ? 1 : 0);
        material.SetInt("_FlipSourceY", flipSourceY ? 1 : 0);
        material.SetTexture("_SourceTex", inputTexture);
        material.SetTexture("_ModeTex", modeTexture);
        material.SetTexture("_CflTex", cflTexture);
        material.SetTexture("_UvCandidates", chromaCandidateTexture);
        material.SetTexture("_UvModeTex", chromaModeTexture);
        material.SetTexture("_ModeCosts", predictionCostTexture);
        material.SetTexture("_Shortlist", predictionShortlistTexture);
        material.SetTexture("_QuantTex", quantizedTexture);
        material.SetTexture("_ReconTex", reconstructionTexture);
        material.SetTexture("_CandidateTex", candidateTexture);
        material.SetTexture("_OptimizedTex", optimizedTexture);
        // Immutable source conversion is shared by every prediction candidate.
        VRCGraphics.Blit(Texture2D.blackTexture, sourceYuvTexture, material, PrepareSourcePass);
        material.SetTexture("_SourceYuvTex", sourceYuvTexture);
        VRCGraphics.Blit(Texture2D.blackTexture, quantizedTexture);
        VRCGraphics.Blit(Texture2D.blackTexture, reconstructionTexture);
        busy = true;
    }
    public void CancelTransform()
    {
        busy = false;
        complete = false;
        error = "";
    }
    private void Update()
    {
        if (!busy) return;
        float before = Time.realtimeSinceStartup;
        for (int i = 0; i < diagonalsPerFrame && diagonal < diagonals; i++, diagonal++)
        {
            material.SetInt("_ChromaStage", 0);
            material.SetInt("_Diagonal", diagonal);
            VRCGraphics.Blit(Texture2D.blackTexture, predictionCostTexture, material, PredictionCostPass);
            VRCGraphics.Blit(Texture2D.blackTexture, predictionShortlistTexture, material, ShortlistPass);
            VRCGraphics.Blit(Texture2D.blackTexture, candidateTexture, material, CandidatePass);
            VRCGraphics.Blit(Texture2D.blackTexture, modeTexture, material, PredictionPass);
            VRCGraphics.Blit(Texture2D.blackTexture, optimizedTexture, material, OptimizePass);
            QuantizeAndReconstruct(optimizedTexture);

            if (subsampleChroma)
            {
                VRCGraphics.Blit(Texture2D.blackTexture, predictionCostTexture, material, CflCostsPass);
                VRCGraphics.Blit(Texture2D.blackTexture, cflTexture, material, CflPass);
                VRCGraphics.Blit(Texture2D.blackTexture, chromaCandidateTexture, material, UvCandidatePass);
                VRCGraphics.Blit(Texture2D.blackTexture, chromaModeTexture, material, UvModePass);
                VRCGraphics.Blit(Texture2D.blackTexture, optimizedTexture, material, ChromaOptimizePass);
            }
            else
            {
                material.SetTexture("_OptimizedTex", chromaTexture);
                VRCGraphics.Blit(Texture2D.blackTexture, chromaTexture, material, ChromaOptimizePass);
            }
            material.SetInt("_ChromaStage", 1);
            QuantizeAndReconstruct(subsampleChroma ? optimizedTexture : chromaTexture);
            material.SetTexture("_OptimizedTex", optimizedTexture);
        }
        if (diagonal == diagonals)
        {
            VRCGraphics.Blit(Texture2D.blackTexture, modeTexture, material, CdefDirectionPass);
            VRCGraphics.Blit(reconstructionTexture, filteredTexture, material, CdefFilterPass);
            VRCGraphics.Blit(quantizedTexture, packedTexture, material, PackPass);
            elapsedSeconds = Time.realtimeSinceStartup - startedAt;
            busy = false;
            complete = true;
        }
        float work = Time.realtimeSinceStartup - before;
        submitCpuSeconds += work;
        maxStepMs = Mathf.Max(maxStepMs, work * 1000);
    }
    private void QuantizeAndReconstruct(RenderTexture work)
    {
        // Each pass samples immutable inputs and writes only its active plane.
        // The scratch surface changes roles only after its previous values
        // have been consumed: optimized coefficients -> inverse rows.
        VRCGraphics.Blit(Texture2D.blackTexture, quantizedTexture, material, QuantizePass);
        VRCGraphics.Blit(Texture2D.blackTexture, work, material, InverseRowsPass);
        VRCGraphics.Blit(Texture2D.blackTexture, reconstructionTexture, material, ReconstructPass);
    }
}
}
