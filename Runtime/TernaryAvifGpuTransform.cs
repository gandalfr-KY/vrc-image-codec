using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

namespace TernaryStars
{
    // Fragment shaders only: no CUDA, native plug-in, ComputeShader or CPU thread.
    // x+2y wavefronts preserve left, above and above-right reconstructed neighbors.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class TernaryAvifGpuTransform : UdonSharpBehaviour
    {
        // Keep these aligned with the pass order in Av1Intra.shader.
        private const int PredictionPass=0,QuantizePass=1,ReconstructPass=2,PackPass=3;
        private const int CflPass=4,OptimizePass=5,CandidatePass=6,UvCandidatePass=7,UvModePass=8;
        private const int CdefDirectionPass=9,CdefFilterPass=10,PredictionCostPass=11,ShortlistPass=12;
        private const int PrepareSourcePass=13,ChromaOptimizePass=14,InverseRowsPass=15,CflCostsPass=16;
        public Material material;
        public RenderTexture modeTexture,cflTexture,optimizedTexture,candidateTexture,uvCandidates,uvModeTexture,modeCosts,shortlist;
        public RenderTexture quantA,reconA,reconB,packedTexture;
        public RenderTexture inputTexture,sourceYuvTexture;
        public int width=640,height=360;
        public int diagonalsPerFrame=4;
        public bool linearInput=true,flipSourceY=true;
        [HideInInspector] public int quantDc,quantAc;
        [HideInInspector] public bool busy,complete,readbackPending;
        [HideInInspector] public string error="";
        [HideInInspector] public byte[] result;
        [HideInInspector] public float elapsedSeconds,submitCpuSeconds,maxStepMs,readbackSeconds;
        private int diagonal,diagonals;
        private float startedAt,readbackAt;

        public void BeginTransform()
        {
            if(readbackPending){error="Previous AV1 GPU readback is still pending.";return;}
            CancelTransform();
            int cols=width/8,rows=height/8;
            if(!material||!inputTexture||!sourceYuvTexture||!quantA||!reconA||!reconB||!packedTexture||!modeTexture||!cflTexture||!optimizedTexture||!candidateTexture||!uvCandidates||!uvModeTexture||!modeCosts||!shortlist||
                width<16||height<16||width>1920||height>1920||width*height>2073600||width%8!=0||height%8!=0||quantDc<=0||quantAc<=0||diagonalsPerFrame<1||diagonalsPerFrame>16||
                sourceYuvTexture.width!=width||sourceYuvTexture.height!=height*3/2||
                quantA.width!=cols*8||quantA.height!=rows*12||reconA.width!=quantA.width||reconA.height!=quantA.height||reconB.width!=quantA.width||reconB.height!=quantA.height||inputTexture.width!=width||inputTexture.height!=height||
                modeTexture.width!=cols||modeTexture.height!=rows||cflTexture.width!=cols||cflTexture.height!=rows||
                optimizedTexture.width!=cols*16||optimizedTexture.height!=rows||candidateTexture.width!=cols*40||candidateTexture.height!=rows||
                uvCandidates.width!=cols*8||uvCandidates.height!=rows||uvModeTexture.width!=cols||uvModeTexture.height!=rows||
                modeCosts.width!=cols*51||modeCosts.height!=rows||shortlist.width!=cols*2||shortlist.height!=rows||
                packedTexture.width!=512||packedTexture.height!=(cols*rows*99+511)/512)
            {error="AV1 GPU configuration: image="+width+"x"+height+" q="+quantDc+","+quantAc+" source="+(inputTexture!=null)+" material="+(material!=null)+" quant="+(quantA?quantA.width:0)+"x"+(quantA?quantA.height:0)+" packed="+(packedTexture?packedTexture.width:0)+"x"+(packedTexture?packedTexture.height:0);return;}
            startedAt=Time.realtimeSinceStartup;elapsedSeconds=submitCpuSeconds=maxStepMs=readbackSeconds=0;
            diagonal=0;diagonals=cols+2*rows-2;
            material.SetVector("_PhotoSize",new Vector4(width,height,cols,rows));
            material.SetVector("_AtlasSize",new Vector4(quantA.width,quantA.height,0,0));
            material.SetVector("_PackedSize",new Vector4(packedTexture.width,packedTexture.height,0,0));
            material.SetInt("_QuantDc",quantDc);material.SetInt("_QuantAc",quantAc);
            material.SetInt("_LinearInput",linearInput?1:0);material.SetInt("_FlipSourceY",flipSourceY?1:0);
            material.SetTexture("_SourceTex",inputTexture);material.SetTexture("_ModeTex",modeTexture);material.SetTexture("_CflTex",cflTexture);material.SetTexture("_UvCandidates",uvCandidates);material.SetTexture("_UvModeTex",uvModeTexture);material.SetTexture("_ModeCosts",modeCosts);material.SetTexture("_Shortlist",shortlist);
            material.SetTexture("_QuantTex",quantA);material.SetTexture("_ReconTex",reconA);
            material.SetTexture("_CandidateTex",candidateTexture);material.SetTexture("_OptimizedTex",optimizedTexture);
            // Immutable source conversion is shared by every prediction candidate.
            VRCGraphics.Blit(Texture2D.blackTexture,sourceYuvTexture,material,PrepareSourcePass);
            material.SetTexture("_SourceYuvTex",sourceYuvTexture);
            VRCGraphics.Blit(Texture2D.blackTexture,quantA);
            VRCGraphics.Blit(Texture2D.blackTexture,reconA);
            busy=true;
        }
        public void CancelTransform(){busy=false;complete=false;error="";result=null;}
        private void Update()
        {
            if(!busy)return;
            if(Time.realtimeSinceStartup-startedAt>30){CancelTransform();error="AV1 GPU processing timed out.";return;}
            if(readbackPending)return;
            float before=Time.realtimeSinceStartup;
            for(int i=0;i<diagonalsPerFrame&&diagonal<diagonals;i++,diagonal++)
            {
                material.SetInt("_ChromaStage",0);material.SetInt("_Diagonal",diagonal);
                VRCGraphics.Blit(Texture2D.blackTexture,modeCosts,material,PredictionCostPass);
                VRCGraphics.Blit(Texture2D.blackTexture,shortlist,material,ShortlistPass);
                VRCGraphics.Blit(Texture2D.blackTexture,candidateTexture,material,CandidatePass);
                VRCGraphics.Blit(Texture2D.blackTexture,modeTexture,material,PredictionPass);
                VRCGraphics.Blit(Texture2D.blackTexture,optimizedTexture,material,OptimizePass);
                QuantizeAndReconstruct();

                VRCGraphics.Blit(Texture2D.blackTexture,modeCosts,material,CflCostsPass);
                VRCGraphics.Blit(Texture2D.blackTexture,cflTexture,material,CflPass);
                VRCGraphics.Blit(Texture2D.blackTexture,uvCandidates,material,UvCandidatePass);
                VRCGraphics.Blit(Texture2D.blackTexture,uvModeTexture,material,UvModePass);
                VRCGraphics.Blit(Texture2D.blackTexture,optimizedTexture,material,ChromaOptimizePass);
                material.SetInt("_ChromaStage",1);
                QuantizeAndReconstruct();
            }
            if(diagonal==diagonals)
            {
                VRCGraphics.Blit(Texture2D.blackTexture,modeTexture,material,CdefDirectionPass);
                VRCGraphics.Blit(reconA,reconB,material,CdefFilterPass);
                VRCGraphics.Blit(quantA,packedTexture,material,PackPass);
                readbackPending=true;readbackAt=Time.realtimeSinceStartup;
                VRCAsyncGPUReadback.Request(packedTexture,0,TextureFormat.RGBA32,this);
            }
            float work=Time.realtimeSinceStartup-before;submitCpuSeconds+=work;maxStepMs=Mathf.Max(maxStepMs,work*1000);
        }
        private void QuantizeAndReconstruct()
        {
            // Each pass samples immutable inputs and writes only its active plane.
            // The scratch surface changes roles only after its previous values
            // have been consumed: optimized coefficients -> inverse rows.
            VRCGraphics.Blit(Texture2D.blackTexture,quantA,material,QuantizePass);
            VRCGraphics.Blit(Texture2D.blackTexture,optimizedTexture,material,InverseRowsPass);
            VRCGraphics.Blit(Texture2D.blackTexture,reconA,material,ReconstructPass);
        }
        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            if(!readbackPending)return;
            readbackPending=false;if(!busy)return;
            result=new byte[packedTexture.width*packedTexture.height*4];
            if(request.hasError||!request.TryGetData(result)){CancelTransform();error="AV1 coefficient readback failed.";return;}
            readbackSeconds=Time.realtimeSinceStartup-readbackAt;elapsedSeconds=Time.realtimeSinceStartup-startedAt;
            busy=false;complete=true;
            Debug.Log("[AV1GPU] transform + asynchronous readback "+elapsedSeconds+"s, "+result.Length+" bytes; submit CPU "+submitCpuSeconds+"s");
        }
    }
}
