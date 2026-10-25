// Adaptive CDF rules: AV1 specification and libaom aom_dsp/prob.h.
// Copyright (c) 2016, Alliance for Open Media. BSD-2-Clause + AOM patent grant.
using System;
using UdonSharp;
using UnityEngine;

namespace TernaryStars
{
    // Single-image AV1: GPU prediction/transform/RDO; Udon entropy coding. Derived from tinyavif,
    // Copyright (c) 2024-2025, the tinyavif contributors. BSD-2-Clause and
    // AOMedia Patent License 1.0: see ThirdParty/AV1. No native library/threads.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class TernaryAvifEncoder : UdonSharpBehaviour
    {
        public const int DefaultQuality=60;
        public int[] modeCounts=new int[13];
        private int[] predictionModes;private bool[] skippedBlocks;
        private int[] adaptationCounts;
        public int width=640,height=360;
        [Range(0,100)] public int quality=DefaultQuality;
        public float frameBudgetMs=2;
        public TernaryAvifGpuTransform gpuTransform;
        private byte[] packedCoefficients;
        [HideInInspector] public byte[] resultAvif;
        [HideInInspector] public bool busy,complete;
        [HideInInspector] public string error="";
        [HideInInspector] public float progress,lastWorkTime,elapsedSeconds,entropySeconds,cpuSeconds,maxStepMs;
        [HideInInspector] public int encodedBlocks,quantizerIndex;
        private byte[] buffer;
        private int[] levels,signs;
        private int cols,rows,sbCols,sbRows,cursor,stage,outputSize,qctx,qdc,qac;
        private float startedAt;
        private ulong low;
        private uint range;
        private int rangeCount;

        public void BeginEncoding()
        {
            if (gpuTransform && gpuTransform.readbackPending) { error="Previous AV1 GPU readback is still pending."; return; }
            CancelEncoding();
            if (width<16 || height<16 || width%8!=0 || height%8!=0 || width>1920 || height>1920 || width*height>2073600 || quality<0 || quality>100 || !gpuTransform || !gpuTransform.material)
            { error="AVIF requires valid GPU resources, width/height 16..1920 in multiples of 8, at most 2073600 pixels and quality 0..100."; return; }
            cols=width/8; rows=height/8; sbCols=(cols+7)/8; sbRows=(rows+7)/8;
            ResetProbabilities();
            skippedBlocks=new bool[cols*rows]; predictionModes=new int[cols*rows]; modeCounts=new int[13];
            levels=new int[cols*rows*3]; signs=new int[levels.Length]; buffer=new byte[100000];
            // Preserve the selected orange Q70 profile (qindex 92). Endpoints remain lossy.
            quantizerIndex=Mathf.Clamp((100-quality)*255/100+16,1,255);
            qdc=dcQuant[quantizerIndex]; qac=acQuant[quantizerIndex];
            qctx=quantizerIndex<=20?0:quantizerIndex<=60?1:quantizerIndex<=120?2:3;
            outputSize=0; low=0; range=32768; rangeCount=-9; cursor=0; stage=-1;
            elapsedSeconds=entropySeconds=cpuSeconds=maxStepMs=0; encodedBlocks=0;
            startedAt=lastWorkTime=Time.realtimeSinceStartup;
            gpuTransform.width=width; gpuTransform.height=height;
            gpuTransform.quantDc=qdc; gpuTransform.quantAc=qac;
            gpuTransform.material.SetInt("_QuantUvDc",dcQuant[Mathf.Clamp(quantizerIndex-16,0,255)]);
            gpuTransform.material.SetInt("_QuantUvAc",acQuant[Mathf.Clamp(quantizerIndex-16,0,255)]);
            gpuTransform.BeginTransform();
            if (!gpuTransform.busy) { error=gpuTransform.error; return; }
            busy=true;
        }

        public void CancelEncoding()
        {
            if (gpuTransform) gpuTransform.CancelTransform();
            buffer=file=header=packedCoefficients=null; levels=signs=null;
            busy=false; complete=false; error=""; resultAvif=null; progress=0;
        }

        private void Update(){if(busy)StepEncoding();}
        public void StepEncoding()
        {
            if(!busy)return;
            lastWorkTime=Time.realtimeSinceStartup;
            if(stage==-1)
            {
                elapsedSeconds=lastWorkTime-startedAt;
                if(gpuTransform.busy)return;
                if(!gpuTransform.complete){Fail(gpuTransform.error);return;}
                packedCoefficients=gpuTransform.result;stage=1;cursor=0;progress=.15f;
            }
            float until=lastWorkTime+Mathf.Max(.25f,frameBudgetMs)*.001f;
            do
            {
                if(cursor<sbCols*sbRows*64)
                {
                    int index=cursor++,sb=index/64,leaf=index%64;
                    int bx=(sb%sbCols)*8+(leaf&1)+((leaf>>1)&2)+((leaf>>2)&4);
                    int by=(sb/sbCols)*8+((leaf>>1)&1)+((leaf>>2)&2)+((leaf>>3)&4);
                    if(bx>=cols||by>=rows)continue;
                    if(leaf==0)Partition(bx,by,64);
                    if(leaf%16==0)Partition(bx,by,32);
                    if(leaf%4==0)Partition(bx,by,16);
                    Partition8();
                    int at=by*cols+bx,p=at*99*4;
                    bool omitted=packedCoefficients[p]==0&&packedCoefficients[p+65*4]==0&&packedCoefficients[p+82*4]==0;
                    int skipContext=(by>0&&skippedBlocks[at-cols]?1:0)+(bx>0&&skippedBlocks[at-1]?1:0);
                    AdaptiveSmall(omitted?1:0,3900+(skipContext),1);skippedBlocks[at]=omitted;
                    EncodePredictionModes(bx,by);
                    if(!omitted)for(int plane=0;plane<3&&busy;plane++)EncodePlane(bx,by,plane);
                    encodedBlocks++;progress=.15f+.8f*encodedBlocks/(cols*rows);
                }
                else{FinalizeRange();if(busy)WrapAvif();}
            }
            while(busy&&Time.realtimeSinceStartup<until);
            float now=Time.realtimeSinceStartup,work=now-lastWorkTime;
            cpuSeconds+=work;entropySeconds=cpuSeconds;maxStepMs=Mathf.Max(maxStepMs,work*1000);elapsedSeconds=now-startedAt;
        }
        
        // Default mode probabilities: AOM entropymode.c, BSD-2-Clause + patent grant.
        private int[] intraYDefault={15588,17027,19338,20218,20682,21110,21825,23244,24189,28165,29093,30466,12016,18066,19516,20303,20719,21444,21888,23032,24434,28658,30172,31409,10052,10771,22296,22788,23055,23239,24133,25620,26160,29336,29929,31567,14091,15406,16442,18808,19136,19546,19998,22096,24746,29585,30958,32462,12122,13265,15603,16501,18609,20033,22391,25583,26437,30261,31073,32475,10023,19585,20848,21440,21832,22760,23089,24023,25381,29014,30482,31436,5983,24099,24560,24886,25066,25795,25913,26423,27610,29905,31276,31794,7444,12781,20177,20728,21077,21607,22170,23405,24469,27915,29090,30492,8537,14689,15432,17087,17408,18172,18408,19825,24649,29153,31096,32210,7543,14231,15496,16195,17905,20717,21984,24516,26001,29675,30981,31994,12613,13591,21383,22004,22312,22577,23401,25055,25729,29538,30305,32077,9687,13470,18506,19230,19604,20147,20695,22062,23219,27743,29211,30907,6183,6505,26024,26252,26366,26434,27082,28354,28555,30467,30794,32086,10718,11734,14954,17224,17565,17924,18561,21523,23878,28975,30287,32252,9194,9858,16501,17263,18424,19171,21563,25961,26561,30072,30737,32463,12602,14399,15488,18381,18778,19315,19724,21419,25060,29696,30917,32409,8203,13821,14524,17105,17439,18131,18404,19468,25225,29485,31158,32342,8451,9731,15004,17643,18012,18425,19070,21538,24605,29118,30078,32018,7714,9048,9516,16667,16817,16994,17153,18767,26743,30389,31536,32528,8843,10280,11496,15317,16652,17943,19108,22718,25769,29953,30983,32485,12578,13671,15979,16834,19075,20913,22989,25449,26219,30214,31150,32477,9563,13626,15080,15892,17756,20863,22207,24236,25380,29653,31143,32277,8356,8901,17616,18256,19350,20106,22598,25947,26466,29900,30523,32261,10835,11815,13124,16042,17018,18039,18947,22753,24615,29489,30883,32482,7618,8288,9859,10509,15386,18657,22903,28776,29180,31355,31802,32593};
        private int[] intraY;
        private int[] intraUvDefault=new int[]{10407,11208,12900,13181,13823,14175,14899,15656,15986,20086,20995,22455,24212,4532,19780,20057,20215,20428,21071,21199,21451,22099,24228,24693,27032,29472,5273,5379,20177,20270,20385,20439,20949,21695,21774,23138,24256,24703,26679,6740,7167,7662,14152,14536,14785,15034,16741,18371,21520,22206,23389,24182,4987,5368,5928,6068,19114,20315,21857,22253,22411,24911,25380,26027,26376,5370,6889,7247,7393,9498,21114,21402,21753,21981,24780,25386,26517,27176,4816,4961,7204,7326,8765,8930,20169,20682,20803,23188,23763,24455,24940,6608,6740,8529,9049,9257,9356,9735,18827,19059,22336,23204,23964,24793,5998,7419,7781,8933,9255,9549,9753,10417,18898,22494,23139,24764,25989,10660,11298,12550,12957,13322,13624,14040,15004,15534,20714,21789,23443,24861,10522,11530,12552,12963,13378,13779,14245,15235,15902,20102,22696,23774,25838,10099,10691,12639,13049,13386,13665,14125,15163,15636,19676,20474,23519,25208,3144,5087,7382,7504,7593,7690,7801,8064,8232,9248,9875,10521,29048};
        private int[] intraUv;
        private int[] angleDeltaDefault={2180,5032,7567,22776,26989,30217,2301,5608,8801,23487,26974,30330,3780,11018,13699,19354,23083,31286,4581,11226,15147,17138,21834,28397,1737,10927,14509,19588,22745,28823,2664,10176,12485,17650,21600,30495,2240,11096,15453,20341,22561,28917,3605,10428,12459,17676,21244,30655};
        private int[] angleDelta;

        private void EncodePredictionModes(int bx,int by)
        {
            int at=by*cols+bx,offset=at*99*4,mode=packedCoefficients[offset+2];
            int above=by>0?predictionModes[at-cols]:0,left=bx>0?predictionModes[at-1]:0;
            AdaptiveSymbol(mode,intraY,3300,(modeContexts[above]*5+modeContexts[left])*12,12);
            if(mode>=1&&mode<=8)AdaptiveSymbol(packedCoefficients[offset+3]>>3,angleDelta,3210,(mode-1)*6,6);
            predictionModes[at]=mode;
            int au=packedCoefficients[offset+65*4+2]-16,av=packedCoefficients[offset+82*4+2]-16;
            int uvMode=packedCoefficients[offset+65*4+3];
            AdaptiveSymbol(uvMode,intraUv,2928,mode*13,13);
            if(uvMode>=1&&uvMode<=8)AdaptiveSymbol(3,angleDelta,3210,(uvMode-1)*6,6);
            if(uvMode==13){
                int su=au==0?0:au<0?1:2,sv=av==0?0:av<0?1:2;
                AdaptiveSymbol(su*3+sv-1,cflSign,3109,0,7);
                if(su!=0)AdaptiveSymbol(Mathf.Abs(au)-1,cflAlpha,3116,((su-1)*3+sv)*15,15);
                if(sv!=0)AdaptiveSymbol(Mathf.Abs(av)-1,cflAlpha,3116,((sv-1)*3+su)*15,15);
            }
            modeCounts[mode]++;
        }

        private int[] cflSignDefault={1418, 2123, 13340, 18405, 26972, 28343, 32294};
        private int[] cflSign;
        private int[] cflAlphaDefault={7637, 20719, 31401, 32481, 32657, 32688, 32692, 32696, 32700,
                  32704, 32708, 32712, 32716, 32720, 32724,14365, 23603, 28135, 31168, 32167, 32395, 32487, 32573, 32620,
                  32647, 32668, 32672, 32676, 32680, 32684,11532, 22380, 28445, 31360, 32349, 32523, 32584, 32649, 32673,
                  32677, 32681, 32685, 32689, 32693, 32697,26990, 31402, 32282, 32571, 32692, 32696, 32700, 32704, 32708,
                  32712, 32716, 32720, 32724, 32728, 32732,17248, 26058, 28904, 30608, 31305, 31877, 32126, 32321, 32394,
                  32464, 32516, 32560, 32576, 32593, 32622,14738, 21678, 25779, 27901, 29024, 30302, 30980, 31843, 32144,
                  32413, 32520, 32594, 32622, 32656, 32660};
        private int[] cflAlpha;



        private int[] modeContexts={0,1,2,3,4,4,4,4,3,0,1,2,0};

        private int[] logByte=new int[]{0,0,1,1,2,2,2,2,3,3,3,3,3,3,3,3,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7};

        // Only called for syntax elements proven constant for this encoder subset.
        private void StableSymbol(int symbol,int[] cdf,int countBase,int offset,int length){
            int loCdf=symbol==0?0:cdf[offset+symbol-1],hiCdf=symbol==length?32768:cdf[offset+symbol],symbols=length+1;
            uint hi=(uint)(32768-hiCdf);
            if(symbol==0)range-=((range>>8)*(hi>>6)>>1)+(uint)(4*(symbols-1));
            else {
                uint lo=(uint)(32768-loCdf),a=((range>>8)*(lo>>6)>>1)+(uint)(4*(symbols-symbol)),b=((range>>8)*(hi>>6)>>1)+(uint)(4*(symbols-symbol-1));
                low+=range-a;range=a-b;
            }
            // A normalized interval needs no shift, log lookup or byte emission.
            if(range<32768){
                int d=range>=256?7-logByte[(int)(range>>8)]:15-logByte[(int)range];
                rangeCount+=d;if(rangeCount>=40)FlushRange(d);low<<=d;range<<=d;
            }
            int at=countBase+offset,count=adaptationCounts[at];if(count<0)return;
            int rate=4+(count>>4)+(length>2?1:0);bool changed=false;
            for(int i=0;i<length;i++){
                int p=cdf[offset+i],delta=i<symbol?-(p>>rate):(32768-p)>>rate;
                if(delta!=0)changed=true;cdf[offset+i]=p+delta;
            }
            adaptationCounts[at]=count<32?count+1:changed?32:-1;
        }

        private void ResetProbabilities(){
            adaptationCounts=new int[4000];smallCdf=new Vector3Int[4000];
            partition8Cdf=19132;partition8Count=0;partition8Stable=false;
            partition16=new int[partition16Default.Length];Array.Copy(partition16Default,partition16,partition16.Length);
            partition32=new int[partition32Default.Length];Array.Copy(partition32Default,partition32,partition32.Length);
            partition64=new int[partition64Default.Length];Array.Copy(partition64Default,partition64,partition64.Length);
            Array.Copy(skipDefault,0,smallCdf,3900,skipDefault.Length);
            txType=new int[txTypeDefault.Length];Array.Copy(txTypeDefault,txType,txType.Length);
            Array.Copy(allZeroDefault,0,smallCdf,164,allZeroDefault.Length);
            eob64=new int[eob64Default.Length];Array.Copy(eob64Default,eob64,eob64.Length);
            eob16=new int[eob16Default.Length];Array.Copy(eob16Default,eob16,eob16.Length);
            Array.Copy(eobExtra8Default,0,smallCdf,348,eobExtra8Default.Length);
            Array.Copy(eobExtra4Default,0,smallCdf,388,eobExtra4Default.Length);
            Array.Copy(coeffBaseEobDefault,0,smallCdf,412,coeffBaseEobDefault.Length);
            Array.Copy(coeffBaseDefault,0,smallCdf,540,coeffBaseDefault.Length);
            Array.Copy(coeffBrDefault,0,smallCdf,1788,coeffBrDefault.Length);
            Array.Copy(dcSignDefault,0,smallCdf,2796,dcSignDefault.Length);
            intraY=new int[intraYDefault.Length];Array.Copy(intraYDefault,intraY,intraY.Length);
            intraUv=new int[intraUvDefault.Length];Array.Copy(intraUvDefault,intraUv,intraUv.Length);
            angleDelta=new int[angleDeltaDefault.Length];Array.Copy(angleDeltaDefault,angleDelta,angleDelta.Length);
            cflSign=new int[cflSignDefault.Length];Array.Copy(cflSignDefault,cflSign,cflSign.Length);
            cflAlpha=new int[cflAlphaDefault.Length];Array.Copy(cflAlphaDefault,cflAlpha,cflAlpha.Length);
        }

        // The fixed 8x8 leaf always emits partition symbol zero. Only its first
        // threshold is ever read; when adaptation converges its Q6 tail is one.
        private void Partition8(){
            if(partition8Stable)range-=(range>>9)+12;
            else {
                range-=((range>>8)*(uint)((32768-partition8Cdf)>>6)>>1)+12;
                int delta=(32768-partition8Cdf)>>(5+(partition8Count>>4));partition8Cdf+=delta;
                if(partition8Count<32)partition8Count++;else if(delta==0)partition8Stable=true;
            }
            if(range<32768){
                int d=range>=256?7-logByte[(int)(range>>8)]:15-logByte[(int)range];
                rangeCount+=d;if(rangeCount>=40)FlushRange(d);low<<=d;range<<=d;
            }
        }
        private void Partition(int bx,int by,int size)
        {
            int[] cdf=size==16?partition16:size==32?partition32:partition64;
            int offset=((bx>0?2:0)+(by>0?1:0))*9;
            bool subRows=by*2+size/8<rows*2,subCols=bx*2+size/8<cols*2;
            if(subRows&&subCols)StableSymbol(3,cdf,size==16?3:size==32?39:75,offset,9);
            else if(subCols)
            {
                int p=Probability(cdf,offset,2)+Probability(cdf,offset,3)+Probability(cdf,offset,4)+Probability(cdf,offset,6)+Probability(cdf,offset,7)+Probability(cdf,offset,9);
                Bit(1,32768-p);
            }
            else if(subRows)
            {
                int p=Probability(cdf,offset,1)+Probability(cdf,offset,3)+Probability(cdf,offset,4)+Probability(cdf,offset,5)+Probability(cdf,offset,6)+Probability(cdf,offset,8);
                Bit(1,32768-p);
            }
        }
        private int Probability(int[] cdf,int at,int symbol){return (symbol==9?32768:cdf[at+symbol])-(symbol==0?0:cdf[at+symbol-1]);}

        private void EncodePlane(int bx,int by,int plane)
        {
            int block=by*cols+bx,planeOffset=plane==0?0:plane==1?65:82;
            int offset=(block*99+planeOffset)*4,eob=packedCoefficients[offset],level=packedCoefficients[offset+1];offset+=4;
            int tx=plane==0?1:0,pt=plane==0?0:1,total=plane==0?64:16,mi=block*3+plane;
            int[] scan=plane==0?scan8:scan4;
            int zeroCtx=0;
            if(plane>0)zeroCtx=7+(by>0&&levels[mi-cols*3]!=0?1:0)+(bx>0&&levels[mi-3]!=0?1:0);
            levels[mi]=Mathf.Min(level,63);
            AdaptiveSmall(eob==0?1:0,164+((qctx*2+tx)*13+zeroCtx),1);
            if(eob==0)return;
            if(plane==0){
                int type=packedCoefficients[block*396+3]&7;
                AdaptiveSymbol(type==4?0:type==0?1:type==1?3:type==2?4:2,txType,112,predictionModes[block]*4,4);
            }
            int eobClass=eob<=1?0:logByte[eob-1]+1;
            if(plane==0)AdaptiveSymbol(eobClass,eob64,268,(qctx*2+pt)*6,6);else AdaptiveSymbol(eobClass,eob16,316,(qctx*2+pt)*4,4);
            if(eobClass>1)
            {
                int bottom=(1<<(eobClass-1))+1,shift=eobClass-2,bit=((eob-bottom)>>shift)&1;
                if(plane==0)AdaptiveSmall(bit,348+((qctx*2+pt)*5+shift),1);else AdaptiveSmall(bit,388+((qctx*2+pt)*3+shift),1);
                Literal(eob-bottom-(bit<<shift),shift);
            }
            int contextFamily=(qctx*2+tx)*2+pt,baseEobOffset=contextFamily*4,baseOffset=contextFamily*26,brOffset=contextFamily*21;
            for(int k=eob-1;k>=0;k--)
            {
                int loc=scan[k],at=offset+loc*4,word=BitConverter.ToInt32(packedCoefficients,at),coefficient=(word&65535)-32768,abs=Mathf.Abs(coefficient);
                if(k==eob-1)
                {
                    int ctx=k==0?0:k<=total/8?1:k<=total/4?2:3;
                    AdaptiveSmall(Mathf.Min(abs-1,2),412+(baseEobOffset+ctx),2);
                }
                else
                {
                    int ctx=(word>>16)&255;
                    AdaptiveSmall(Mathf.Min(abs,3),540+(baseOffset+ctx),3);
                }
                if(abs>2)
                {
                    int ctx=(word>>24)&255;
                    int lev=3;
                    for(int j=0;j<4;j++){int value=Mathf.Min(abs-lev,3);AdaptiveSmall(value,1788+(brOffset+ctx),3);lev+=value;if(value<3)break;}
                }
            }
            int dc=(BitConverter.ToInt32(packedCoefficients,offset)&65535)-32768;
            if(dc!=0)
            {
                int net=(by>0?signs[mi-cols*3]:0)+(bx>0?signs[mi-3]:0),ctx=net==0?0:net<0?1:2;
                AdaptiveSmall(dc<0?1:0,2796+((qctx*2+pt)*3+ctx),1);
            }
            if(Mathf.Abs(dc)>=15)Golomb(Mathf.Abs(dc)-15);
            signs[mi]=dc==0?0:dc<0?-1:1;
            for(int k=1;k<eob;k++){int value=(BitConverter.ToInt32(packedCoefficients,offset+scan[k]*4)&65535)-32768;if(value!=0)UniformBit(value<0?1:0);if(Mathf.Abs(value)>=15)Golomb(Mathf.Abs(value)-15);}
        }
        private int FloorLog(int value){int result=0;if(value>=65536){value>>=16;result+=16;}if(value>=256){value>>=8;result+=8;}if(value>=16){value>>=4;result+=4;}if(value>=4){value>>=2;result+=2;}if(value>=2)result++;return result;}
        // Native vector operations update all small-CDF thresholds together.
        // For rate 4..7, p*(1-2^-rate)+bias is an exact binary fraction.
        // Floor matches p-(p>>rate) below the symbol and p+((32768-p)>>rate) above.
        private Vector3Int[] smallCdf;
        private float[] cdfFactors={.9375f,.96875f,.984375f,.9921875f};
        private Vector3[] cdfBias={new Vector3(2048.0f,2048.0f,2048.0f),new Vector3(0.9375f,2048.0f,2048.0f),new Vector3(0.9375f,0.9375f,2048.0f),new Vector3(0.9375f,0.9375f,0.9375f),new Vector3(1024.0f,1024.0f,1024.0f),new Vector3(0.96875f,1024.0f,1024.0f),new Vector3(0.96875f,0.96875f,1024.0f),new Vector3(0.96875f,0.96875f,0.96875f),new Vector3(512.0f,512.0f,512.0f),new Vector3(0.984375f,512.0f,512.0f),new Vector3(0.984375f,0.984375f,512.0f),new Vector3(0.984375f,0.984375f,0.984375f),new Vector3(256.0f,256.0f,256.0f),new Vector3(0.9921875f,256.0f,256.0f),new Vector3(0.9921875f,0.9921875f,256.0f),new Vector3(0.9921875f,0.9921875f,0.9921875f)};
        private void AdaptiveSmall(int symbol,int index,int length){
            Vector3Int cdf=smallCdf[index];
            int loCdf=symbol==0?0:cdf[symbol-1],hiCdf=symbol==length?32768:cdf[symbol],symbols=length+1;
            uint hi=(uint)(32768-hiCdf);
            if(symbol==0)range-=((range>>8)*(hi>>6)>>1)+(uint)(4*(symbols-1));
            else {
                uint lo=(uint)(32768-loCdf),a=((range>>8)*(lo>>6)>>1)+(uint)(4*(symbols-symbol)),b=((range>>8)*(hi>>6)>>1)+(uint)(4*(symbols-symbol-1));
                low+=range-a;range=a-b;
            }
            // A normalized interval needs no shift, log lookup or byte emission.
            if(range<32768){
                int d=range>=256?7-logByte[(int)(range>>8)]:15-logByte[(int)range];
                rangeCount+=d;if(rangeCount>=40)FlushRange(d);low<<=d;range<<=d;
            }
            int count=adaptationCounts[index],rate=(count>>4)+(length>2?1:0);
            smallCdf[index]=Vector3Int.FloorToInt((Vector3)cdf*cdfFactors[rate]+cdfBias[rate*4+symbol]);
            if(count<32)adaptationCounts[index]=count+1;
        }
        private void AdaptiveSymbol(int symbol,int[] cdf,int countBase,int offset,int length)
        {
            int loCdf=symbol==0?0:cdf[offset+symbol-1],hiCdf=symbol==length?32768:cdf[offset+symbol],symbols=length+1;
            uint hi=(uint)(32768-hiCdf);
            if(symbol==0)range-=((range>>8)*(hi>>6)>>1)+(uint)(4*(symbols-1));
            else {
                uint lo=(uint)(32768-loCdf),a=((range>>8)*(lo>>6)>>1)+(uint)(4*(symbols-symbol)),b=((range>>8)*(hi>>6)>>1)+(uint)(4*(symbols-symbol-1));
                low+=range-a;range=a-b;
            }
            // A normalized interval needs no shift, log lookup or byte emission.
            if(range<32768){
                int d=range>=256?7-logByte[(int)(range>>8)]:15-logByte[(int)range];
                rangeCount+=d;if(rangeCount>=40)FlushRange(d);low<<=d;range<<=d;
            }
            {
                int at=countBase+offset,count=adaptationCounts[at];
                int rate=4+(count>>4)+(length>2?1:0);
                if(length<=3){
                    int p=cdf[offset];cdf[offset]=symbol>0?p-(p>>rate):p+((32768-p)>>rate);
                    if(length>=2){p=cdf[offset+1];cdf[offset+1]=symbol>1?p-(p>>rate):p+((32768-p)>>rate);}
                    if(length==3){p=cdf[offset+2];cdf[offset+2]=symbol>2?p-(p>>rate):p+((32768-p)>>rate);}
                }else for(int i=0;i<length;i++){
                    int p=cdf[offset+i];
                    cdf[offset+i]=i<symbol?p-(p>>rate):p+((32768-p)>>rate);
                }
                if(count<32)adaptationCounts[at]=count+1;
            }
        }
        private void Bit(int value,int p){RangeSymbol(value,value==0?0:p,value==0?p:32768,2);}
        private void Literal(int value,int bits){for(int s=bits-1;s>=0;s--)UniformBit((value>>s)&1);}
        private void Golomb(int value){value++;int bits=FloorLog(value);Literal(0,bits);Literal(value,bits+1);}
        private void RangeSymbol(int symbol,int loCdf,int hiCdf,int symbols)
        {
            uint hi=(uint)(32768-hiCdf);
            if(symbol==0)range-=((range>>8)*(hi>>6)>>1)+(uint)(4*(symbols-1));
            else
            {
                uint lo=(uint)(32768-loCdf),a=((range>>8)*(lo>>6)>>1)+(uint)(4*(symbols-symbol)),b=((range>>8)*(hi>>6)>>1)+(uint)(4*(symbols-symbol-1));
                low+=range-a;range=a-b;
            }
            // A normalized interval needs no shift, log lookup or byte emission.
            if(range<32768){
                int d=range>=256?7-logByte[(int)(range>>8)]:15-logByte[(int)range];
                rangeCount+=d;if(rangeCount>=40)FlushRange(d);low<<=d;range<<=d;
            }
        }
        // Exact 1/2-probability specialization for signs and literals.
        // Keep normalization identical to RangeSymbol; golden bitstreams cover both.
        private void UniformBit(int value)
        {
            uint half=((range>>8)<<7)+4;
            if(value==0)range-=half;else{low+=range-half;range=half;}
            // A normalized interval needs no shift, log lookup or byte emission.
            if(range<32768){
                int d=range>=256?7-logByte[(int)(range>>8)]:15-logByte[(int)range];
                rangeCount+=d;if(rangeCount>=40)FlushRange(d);low<<=d;range<<=d;
            }
        }
        // Rare path shared by adaptive, fixed-probability and uniform symbols.
        private void FlushRange(int shift){
            int bytes=(rangeCount>>3)+1,c=rangeCount-shift+24-(bytes<<3);
            if(outputSize+bytes>buffer.Length){Fail("AVIF exceeds the server's 100KB image limit.");return;}
            ulong output=low>>c;low&=(1ul<<c)-1;
            if((output&(1ul<<(bytes<<3)))!=0)Carry();
            output&=(1ul<<(bytes<<3))-1;
            for(int i=bytes-1;i>=0;i--)buffer[outputSize++]=(byte)((output>>(8*i))&255ul);
            rangeCount=c+shift-24;
        }
        private void Carry(){for(int i=outputSize-1;i>=0;i--){if(buffer[i]==255)buffer[i]=0;else{buffer[i]++;return;}}Fail("AV1 entropy carry overflow");}
        private void Put(int value){if(outputSize>=buffer.Length){Fail("AVIF exceeds the server's 100KB image limit.");return;}buffer[outputSize++]=(byte)(value&255);}
        private void Fail(string value){busy=false;complete=false;error=value;Debug.LogError("[AV1CPU] "+value);}
        private void FinalizeRange()
        {
            int s=rangeCount+10;ulong m=16383,e=((low+m)&~m)|(m+1),mask=(1ul<<(rangeCount+16))-1;
            while(s>0){ulong value=e>>(rangeCount+16);if((value&256)!=0)Carry();Put((int)(value&255ul));e&=mask;s-=8;rangeCount-=8;mask>>=8;}
        }
        private byte[] file,header;
        private int fileCursor,headerBits;
        private void HeaderBits(int value,int count)
        {
            for(int k=count-1;k>=0;k--){if(((value>>k)&1)!=0)header[headerBits>>3]|=(byte)(1<<(7-(headerBits&7)));headerBits++;}
        }
        private byte[] SequenceHeader()
        {
            header=new byte[64];headerBits=0;
            HeaderBits(0,3);HeaderBits(1,1);HeaderBits(1,1);HeaderBits(width*height>665856?8:4,5);
            HeaderBits(15,4);HeaderBits(15,4);HeaderBits(width-1,16);HeaderBits(height-1,16);
            HeaderBits(2,6);HeaderBits(0,1);HeaderBits(0,1);HeaderBits(1,1);
            HeaderBits(1,8);HeaderBits(13,8);HeaderBits(6,8);HeaderBits(1,1);HeaderBits(0,2);HeaderBits(0,1);HeaderBits(0,1);HeaderBits(1,1);
            var result=new byte[(headerBits+7)/8];Array.Copy(header,result,result.Length);return result;
        }
        private byte[] FrameHeader()
        {
            header=new byte[64];headerBits=0;
            HeaderBits(0,1);HeaderBits(0,1);HeaderBits(0,1);HeaderBits(1,1);
            if(width>64)HeaderBits(0,1);if(height>64)HeaderBits(0,1);
            HeaderBits(quantizerIndex,8);HeaderBits(0,1);
            for(int component=0;component<2;component++){
                HeaderBits(1,1);HeaderBits((-16)&127,7);
            }
            HeaderBits(0,1); // Quantization matrices disabled in the selected profile.
            HeaderBits(0,2);
            HeaderBits(0,6);HeaderBits(0,6);HeaderBits(0,3);HeaderBits(0,1);HeaderBits(1,2);HeaderBits(0,2);HeaderBits(26,6);HeaderBits(0,6);HeaderBits(0,1);HeaderBits(1,1);
            var result=new byte[(headerBits+7)/8];Array.Copy(header,result,result.Length);return result;
        }
        private void FByte(int value){file[fileCursor++]=(byte)(value&255);}
        private void UInt(int value,int bytes){for(int i=bytes-1;i>=0;i--)FByte(value>>(8*i));}
        private void Text(string value){for(int i=0;i<value.Length;i++)FByte(value[i]);}
        private void Patch(int at,int value){for(int i=0;i<4;i++)file[at+i]=(byte)((value>>(24-i*8))&255);}
        private int Box(string type){int at=fileCursor;UInt(0,4);Text(type);return at;}
        private int Full(string type,int version){int at=Box(type);UInt(version<<24,4);return at;}
        private void End(int at){Patch(at,fileCursor-at);}
        private void Leb(int value){do{int part=value&127;value>>=7;FByte(part|(value!=0?128:0));}while(value!=0);}
        private void Append(byte[] bytes){Array.Copy(bytes,0,file,fileCursor,bytes.Length);fileCursor+=bytes.Length;}
        private void WrapAvif()
        {
            byte[] sequence=SequenceHeader(),frame=FrameHeader();
            file=new byte[outputSize+600];fileCursor=0;
            int ftyp=Box("ftyp");Text("avif");UInt(0,4);Text("avifmif1miafMA1B");End(ftyp);
            int meta=Full("meta",0),handler=Full("hdlr",0);UInt(0,4);Text("pict");for(int i=0;i<13;i++)FByte(0);End(handler);
            int primary=Full("pitm",0);UInt(1,2);End(primary);
            int location=Full("iloc",0);FByte(0x44);FByte(0);UInt(1,2);UInt(1,2);UInt(0,2);UInt(1,2);
            int offsetMarker=fileCursor;UInt(0,4);int sizeMarker=fileCursor;UInt(0,4);End(location);
            int info=Full("iinf",0);UInt(1,2);int entry=Full("infe",2);UInt(1,2);UInt(0,2);Text("av01Color");FByte(0);End(entry);End(info);
            int properties=Box("iprp"),container=Box("ipco"),size=Full("ispe",0);UInt(width,4);UInt(height,4);End(size);
            int pixels=Full("pixi",0);FByte(3);FByte(8);FByte(8);FByte(8);End(pixels);
            int config=Box("av1C");FByte(0x81);FByte(width*height>665856?8:4);FByte(12);FByte(0);End(config);
            int color=Box("colr");Text("nclx");UInt(1,2);UInt(13,2);UInt(6,2);FByte(128);End(color);End(container);
            int mapping=Full("ipma",0);UInt(1,4);UInt(1,2);FByte(4);FByte(1);FByte(2);FByte(0x83);FByte(4);End(mapping);End(properties);End(meta);
            int data=Box("mdat"),contentStart=fileCursor;
            FByte(0x12);FByte(0);FByte(0x0a);Leb(sequence.Length);Append(sequence);
            FByte(0x32);Leb(frame.Length+outputSize);Append(frame);Array.Copy(buffer,0,file,fileCursor,outputSize);fileCursor+=outputSize;
            Patch(offsetMarker,contentStart);Patch(sizeMarker,fileCursor-contentStart);End(data);
            if(fileCursor>100000){Fail("AVIF exceeds the server's 100KB image limit.");return;}
            resultAvif=new byte[fileCursor];Array.Copy(file,resultAvif,fileCursor);
            busy=false;complete=true;progress=1;elapsedSeconds=Time.realtimeSinceStartup-startedAt;
levels=signs=null;buffer=file=header=null;
            packedCoefficients=null;if(gpuTransform)gpuTransform.result=null;
            Debug.Log("[AV1CPU] Complete; seconds="+elapsedSeconds+" bytes="+resultAvif.Length+" entropy="+entropySeconds);
        }

        private int partition8Cdf,partition8Count;private bool partition8Stable;
        private int[] partition16Default = {15597,20929,24571,26706,27664,28821,29601,30571,31902,7925,11043,16785,22470,23971,25043,26651,28701,29834,5414,13269,15111,20488,22360,24500,25537,26336,32117,2662,6362,8614,20860,23053,24778,26436,27829,31171};
        private int[] partition16;
        private int[] partition32Default = {18462,20920,23124,27647,28227,29049,29519,30178,31544,7689,9060,12056,24992,25660,26182,26951,28041,29052,6015,9009,10062,24544,25409,26545,27071,27526,32047,1394,2208,2796,28614,29061,29466,29840,30185,31899};
        private int[] partition32;
        private int[] partition64Default = {20137,21547,23078,29566,29837,30261,30524,30892,31724,6732,7490,9497,27944,28250,28515,28969,29630,30104,5945,7663,8348,28683,29117,29749,30064,30298,32238,870,1212,1487,31198,31394,31574,31743,31881,32332};
        private int[] partition64;
        private Vector3Int[] skipDefault={new Vector3Int(31671,0,0),new Vector3Int(16515,0,0),new Vector3Int(4576,0,0)};
        private int[] uvMode = {10407,11208,12900,13181,13823,14175,14899,15656,15986,20086,20995,22455,24212};
        private int[] txTypeDefault = {6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214,6554,13107,19661,26214};
        private int[] txType;
        private Vector3Int[] allZeroDefault={new Vector3Int(31849,0,0),new Vector3Int(5892,0,0),new Vector3Int(12112,0,0),new Vector3Int(21935,0,0),new Vector3Int(20289,0,0),new Vector3Int(27473,0,0),new Vector3Int(32487,0,0),new Vector3Int(7654,0,0),new Vector3Int(19473,0,0),new Vector3Int(29984,0,0),new Vector3Int(9961,0,0),new Vector3Int(30242,0,0),new Vector3Int(32117,0,0),new Vector3Int(31548,0,0),new Vector3Int(1549,0,0),new Vector3Int(10130,0,0),new Vector3Int(16656,0,0),new Vector3Int(18591,0,0),new Vector3Int(26308,0,0),new Vector3Int(32537,0,0),new Vector3Int(5403,0,0),new Vector3Int(18096,0,0),new Vector3Int(30003,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(30371,0,0),new Vector3Int(7570,0,0),new Vector3Int(13155,0,0),new Vector3Int(20751,0,0),new Vector3Int(20969,0,0),new Vector3Int(27067,0,0),new Vector3Int(32013,0,0),new Vector3Int(5495,0,0),new Vector3Int(17942,0,0),new Vector3Int(28280,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(31782,0,0),new Vector3Int(1836,0,0),new Vector3Int(10689,0,0),new Vector3Int(17604,0,0),new Vector3Int(21622,0,0),new Vector3Int(27518,0,0),new Vector3Int(32399,0,0),new Vector3Int(4419,0,0),new Vector3Int(16294,0,0),new Vector3Int(28345,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(29614,0,0),new Vector3Int(9068,0,0),new Vector3Int(12924,0,0),new Vector3Int(19538,0,0),new Vector3Int(17737,0,0),new Vector3Int(24619,0,0),new Vector3Int(30642,0,0),new Vector3Int(4119,0,0),new Vector3Int(16026,0,0),new Vector3Int(25657,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(31957,0,0),new Vector3Int(3230,0,0),new Vector3Int(11153,0,0),new Vector3Int(18123,0,0),new Vector3Int(20143,0,0),new Vector3Int(26536,0,0),new Vector3Int(31986,0,0),new Vector3Int(3050,0,0),new Vector3Int(14603,0,0),new Vector3Int(25155,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(26887,0,0),new Vector3Int(6729,0,0),new Vector3Int(10361,0,0),new Vector3Int(17442,0,0),new Vector3Int(15045,0,0),new Vector3Int(22478,0,0),new Vector3Int(29072,0,0),new Vector3Int(2713,0,0),new Vector3Int(11861,0,0),new Vector3Int(20773,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(31903,0,0),new Vector3Int(2044,0,0),new Vector3Int(7528,0,0),new Vector3Int(14618,0,0),new Vector3Int(16182,0,0),new Vector3Int(24168,0,0),new Vector3Int(31037,0,0),new Vector3Int(2786,0,0),new Vector3Int(11194,0,0),new Vector3Int(20155,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0),new Vector3Int(16384,0,0)};
        private int[] eob64Default = {329,498,1101,1784,3265,7758,3505,5304,10086,13814,17684,23370,1260,1446,2253,3712,6652,13369,8609,10612,14624,18714,22614,29024,2374,2772,4583,7276,12288,19706,15050,17126,21410,24886,28156,30726,6307,7541,12060,16358,22553,27865,24212,25708,28268,30035,31307,32049};
        private int[] eob64;
        private int[] eob16Default = {840,1039,1980,4895,3247,4950,9688,14563,2125,2551,5165,8946,7637,9498,14259,19108,4016,4897,8881,14968,11139,13270,18241,23566,6708,8958,14746,22133,19575,21766,26044,29709};
        private int[] eob16;
        private Vector3Int[] eobExtra8Default={new Vector3Int(20401,0,0),new Vector3Int(17025,0,0),new Vector3Int(12845,0,0),new Vector3Int(12873,0,0),new Vector3Int(14094,0,0),new Vector3Int(20681,0,0),new Vector3Int(20701,0,0),new Vector3Int(15250,0,0),new Vector3Int(15017,0,0),new Vector3Int(14928,0,0),new Vector3Int(20430,0,0),new Vector3Int(20662,0,0),new Vector3Int(15367,0,0),new Vector3Int(16970,0,0),new Vector3Int(14657,0,0),new Vector3Int(22117,0,0),new Vector3Int(22028,0,0),new Vector3Int(18650,0,0),new Vector3Int(16042,0,0),new Vector3Int(15885,0,0),new Vector3Int(19139,0,0),new Vector3Int(21487,0,0),new Vector3Int(18959,0,0),new Vector3Int(20910,0,0),new Vector3Int(19089,0,0),new Vector3Int(20536,0,0),new Vector3Int(20664,0,0),new Vector3Int(20625,0,0),new Vector3Int(19123,0,0),new Vector3Int(14862,0,0),new Vector3Int(20238,0,0),new Vector3Int(21057,0,0),new Vector3Int(19159,0,0),new Vector3Int(22337,0,0),new Vector3Int(20159,0,0),new Vector3Int(20125,0,0),new Vector3Int(20559,0,0),new Vector3Int(21707,0,0),new Vector3Int(22296,0,0),new Vector3Int(17333,0,0)};
        private Vector3Int[] eobExtra4Default={new Vector3Int(16961,0,0),new Vector3Int(17223,0,0),new Vector3Int(7621,0,0),new Vector3Int(19069,0,0),new Vector3Int(22525,0,0),new Vector3Int(13377,0,0),new Vector3Int(17471,0,0),new Vector3Int(20223,0,0),new Vector3Int(11357,0,0),new Vector3Int(20335,0,0),new Vector3Int(21667,0,0),new Vector3Int(14818,0,0),new Vector3Int(18983,0,0),new Vector3Int(20512,0,0),new Vector3Int(14885,0,0),new Vector3Int(20090,0,0),new Vector3Int(19444,0,0),new Vector3Int(17286,0,0),new Vector3Int(20177,0,0),new Vector3Int(20789,0,0),new Vector3Int(20262,0,0),new Vector3Int(21416,0,0),new Vector3Int(20855,0,0),new Vector3Int(23410,0,0)};
        private Vector3Int[] coeffBaseEobDefault={new Vector3Int(17837,29055,0),new Vector3Int(29600,31446,0),new Vector3Int(30844,31878,0),new Vector3Int(24926,28948,0),new Vector3Int(21365,30026,0),new Vector3Int(30512,32423,0),new Vector3Int(31658,32621,0),new Vector3Int(29630,31881,0),new Vector3Int(5717,26477,0),new Vector3Int(30491,31703,0),new Vector3Int(31550,32158,0),new Vector3Int(29648,31491,0),new Vector3Int(12608,27820,0),new Vector3Int(30680,32225,0),new Vector3Int(30809,32335,0),new Vector3Int(31299,32423,0),new Vector3Int(17560,29888,0),new Vector3Int(29671,31549,0),new Vector3Int(31007,32056,0),new Vector3Int(27286,30006,0),new Vector3Int(26594,31212,0),new Vector3Int(31208,32582,0),new Vector3Int(31835,32637,0),new Vector3Int(30595,32206,0),new Vector3Int(15239,29932,0),new Vector3Int(31315,32095,0),new Vector3Int(32130,32434,0),new Vector3Int(30864,31996,0),new Vector3Int(26279,30968,0),new Vector3Int(31142,32495,0),new Vector3Int(31713,32540,0),new Vector3Int(31929,32594,0),new Vector3Int(20092,30774,0),new Vector3Int(30695,32020,0),new Vector3Int(31131,32103,0),new Vector3Int(28666,30870,0),new Vector3Int(27258,31095,0),new Vector3Int(31804,32623,0),new Vector3Int(31763,32528,0),new Vector3Int(31438,32506,0),new Vector3Int(18049,30489,0),new Vector3Int(31706,32286,0),new Vector3Int(32163,32473,0),new Vector3Int(31550,32184,0),new Vector3Int(27116,30842,0),new Vector3Int(31971,32598,0),new Vector3Int(32088,32576,0),new Vector3Int(32067,32664,0),new Vector3Int(22497,31198,0),new Vector3Int(31715,32495,0),new Vector3Int(31606,32337,0),new Vector3Int(30388,31990,0),new Vector3Int(27877,31584,0),new Vector3Int(32170,32728,0),new Vector3Int(32155,32688,0),new Vector3Int(32219,32702,0),new Vector3Int(21457,31043,0),new Vector3Int(31951,32483,0),new Vector3Int(32153,32562,0),new Vector3Int(31473,32215,0),new Vector3Int(27558,31151,0),new Vector3Int(32020,32640,0),new Vector3Int(32097,32575,0),new Vector3Int(32242,32719,0)};
        private Vector3Int[] coeffBaseDefault={new Vector3Int(4034,8930,12727),new Vector3Int(18082,29741,31877),new Vector3Int(12596,26124,30493),new Vector3Int(9446,21118,27005),new Vector3Int(6308,15141,21279),new Vector3Int(2463,6357,9783),new Vector3Int(20667,30546,31929),new Vector3Int(13043,26123,30134),new Vector3Int(8151,18757,24778),new Vector3Int(5255,12839,18632),new Vector3Int(2820,7206,11161),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(15736,27553,30604),new Vector3Int(11210,23794,28787),new Vector3Int(5947,13874,19701),new Vector3Int(4215,9323,13891),new Vector3Int(2833,6462,10059),new Vector3Int(6302,16444,21761),new Vector3Int(23040,31538,32475),new Vector3Int(15196,28452,31496),new Vector3Int(10020,22946,28514),new Vector3Int(6533,16862,23501),new Vector3Int(3538,9816,15076),new Vector3Int(24444,31875,32525),new Vector3Int(15881,28924,31635),new Vector3Int(9922,22873,28466),new Vector3Int(6527,16966,23691),new Vector3Int(4114,11303,17220),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(20201,30770,32209),new Vector3Int(14754,28071,31258),new Vector3Int(8378,20186,26517),new Vector3Int(5916,15299,21978),new Vector3Int(4268,11583,17901),new Vector3Int(4536,10072,14001),new Vector3Int(25459,31416,32206),new Vector3Int(16605,28048,30818),new Vector3Int(11008,22857,27719),new Vector3Int(6915,16268,22315),new Vector3Int(2625,6812,10537),new Vector3Int(24257,31788,32499),new Vector3Int(16880,29454,31879),new Vector3Int(11958,25054,29778),new Vector3Int(7916,18718,25084),new Vector3Int(3383,8777,13446),new Vector3Int(22720,31603,32393),new Vector3Int(14960,28125,31335),new Vector3Int(9731,22210,27928),new Vector3Int(6304,15832,22277),new Vector3Int(2910,7818,12166),new Vector3Int(20375,30627,32131),new Vector3Int(13904,27284,30887),new Vector3Int(9368,21558,27144),new Vector3Int(5937,14966,21119),new Vector3Int(2667,7225,11319),new Vector3Int(23970,31470,32378),new Vector3Int(17173,29734,32018),new Vector3Int(12795,25441,29965),new Vector3Int(8981,19680,25893),new Vector3Int(4728,11372,16902),new Vector3Int(6037,16771,21957),new Vector3Int(24774,31704,32426),new Vector3Int(16830,28589,31056),new Vector3Int(10602,22828,27760),new Vector3Int(6733,16829,23071),new Vector3Int(3250,8914,13556),new Vector3Int(25582,32220,32668),new Vector3Int(18659,30342,32223),new Vector3Int(12546,26149,30515),new Vector3Int(8420,20451,26801),new Vector3Int(4636,12420,18344),new Vector3Int(27581,32362,32639),new Vector3Int(18987,30083,31978),new Vector3Int(11327,24248,29084),new Vector3Int(7264,17719,24120),new Vector3Int(3995,10768,16169),new Vector3Int(25893,31831,32487),new Vector3Int(16577,28587,31379),new Vector3Int(10189,22748,28182),new Vector3Int(6832,17094,23556),new Vector3Int(3708,10110,15334),new Vector3Int(25904,32282,32656),new Vector3Int(19721,30792,32276),new Vector3Int(12819,26243,30411),new Vector3Int(8572,20614,26891),new Vector3Int(5364,14059,20467),new Vector3Int(6041,11854,15927),new Vector3Int(20326,30905,32251),new Vector3Int(14164,26831,30725),new Vector3Int(9760,20647,26585),new Vector3Int(6416,14953,21219),new Vector3Int(2966,7151,10891),new Vector3Int(23567,31374,32254),new Vector3Int(14978,27416,30946),new Vector3Int(9434,20225,26254),new Vector3Int(6658,14558,20535),new Vector3Int(3916,8677,12989),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(18088,29545,31587),new Vector3Int(13062,25843,30073),new Vector3Int(8940,16827,22251),new Vector3Int(7654,13220,17973),new Vector3Int(5733,10316,14456),new Vector3Int(8455,19003,24368),new Vector3Int(23563,32021,32604),new Vector3Int(16237,29446,31935),new Vector3Int(10724,23999,29358),new Vector3Int(6725,17528,24416),new Vector3Int(3927,10927,16825),new Vector3Int(26313,32288,32634),new Vector3Int(17430,30095,32095),new Vector3Int(11116,24606,29679),new Vector3Int(7195,18384,25269),new Vector3Int(4726,12852,19315),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(22822,31648,32483),new Vector3Int(16724,29633,31929),new Vector3Int(10261,23033,28725),new Vector3Int(7029,17840,24528),new Vector3Int(4867,13886,21502),new Vector3Int(6779,13743,17678),new Vector3Int(24806,31797,32457),new Vector3Int(17616,29047,31372),new Vector3Int(11063,23175,28003),new Vector3Int(6521,16110,22324),new Vector3Int(2764,7504,11654),new Vector3Int(25266,32367,32637),new Vector3Int(19054,30553,32175),new Vector3Int(12139,25212,29807),new Vector3Int(7311,18162,24704),new Vector3Int(3397,9164,14074),new Vector3Int(25988,32208,32522),new Vector3Int(16253,28912,31526),new Vector3Int(9151,21387,27372),new Vector3Int(5688,14915,21496),new Vector3Int(2717,7627,12004),new Vector3Int(23144,31855,32443),new Vector3Int(16070,28491,31325),new Vector3Int(8702,20467,26517),new Vector3Int(5243,13956,20367),new Vector3Int(2621,7335,11567),new Vector3Int(26636,32340,32630),new Vector3Int(19990,31050,32341),new Vector3Int(13243,26105,30315),new Vector3Int(8588,19521,25918),new Vector3Int(4717,11585,17304),new Vector3Int(8639,19339,24429),new Vector3Int(24404,31837,32525),new Vector3Int(16997,29425,31784),new Vector3Int(11253,24234,29149),new Vector3Int(6751,17394,24028),new Vector3Int(3490,9830,15191),new Vector3Int(26283,32471,32714),new Vector3Int(19599,31168,32442),new Vector3Int(13146,26954,30893),new Vector3Int(8214,20588,26890),new Vector3Int(4699,13081,19300),new Vector3Int(28212,32458,32669),new Vector3Int(18594,30316,32100),new Vector3Int(11219,24408,29234),new Vector3Int(6865,17656,24149),new Vector3Int(3678,10362,16006),new Vector3Int(25825,32136,32616),new Vector3Int(17313,29853,32021),new Vector3Int(11197,24471,29472),new Vector3Int(6947,17781,24405),new Vector3Int(3768,10660,16261),new Vector3Int(27352,32500,32706),new Vector3Int(20850,31468,32469),new Vector3Int(14021,27707,31133),new Vector3Int(8964,21748,27838),new Vector3Int(5437,14665,21187),new Vector3Int(8896,16227,20630),new Vector3Int(23629,31782,32527),new Vector3Int(15173,27755,31321),new Vector3Int(10158,21233,27382),new Vector3Int(6420,14857,21558),new Vector3Int(3269,8155,12646),new Vector3Int(24835,32009,32496),new Vector3Int(16509,28421,31579),new Vector3Int(10957,21514,27418),new Vector3Int(7881,15930,22096),new Vector3Int(5388,10960,15918),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(20745,30773,32093),new Vector3Int(15200,27221,30861),new Vector3Int(13032,20873,25667),new Vector3Int(12285,18663,23494),new Vector3Int(11563,17481,21489),new Vector3Int(10808,22056,26896),new Vector3Int(25739,32313,32676),new Vector3Int(17288,30203,32221),new Vector3Int(11359,24878,29896),new Vector3Int(6949,17767,24893),new Vector3Int(4287,11796,18071),new Vector3Int(27880,32521,32705),new Vector3Int(19038,31004,32414),new Vector3Int(12564,26345,30768),new Vector3Int(8269,19947,26779),new Vector3Int(5674,14657,21674),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(25742,32319,32671),new Vector3Int(19557,31164,32454),new Vector3Int(13381,26381,30755),new Vector3Int(10101,21466,26722),new Vector3Int(9209,19650,26825),new Vector3Int(9704,17294,21132),new Vector3Int(26762,32278,32633),new Vector3Int(18382,29620,31819),new Vector3Int(10891,23475,28723),new Vector3Int(6358,16583,23309),new Vector3Int(3248,9118,14141),new Vector3Int(27204,32573,32699),new Vector3Int(19818,30824,32329),new Vector3Int(11772,25120,30041),new Vector3Int(6995,18033,25039),new Vector3Int(3752,10442,16098),new Vector3Int(27222,32256,32559),new Vector3Int(15356,28399,31475),new Vector3Int(8821,20635,27057),new Vector3Int(5511,14404,21239),new Vector3Int(2935,8222,13051),new Vector3Int(24875,32120,32529),new Vector3Int(15233,28265,31445),new Vector3Int(8605,20570,26932),new Vector3Int(5431,14413,21196),new Vector3Int(2994,8341,13223),new Vector3Int(28201,32604,32700),new Vector3Int(21041,31446,32456),new Vector3Int(13221,26213,30475),new Vector3Int(8255,19385,26037),new Vector3Int(4930,12585,18830),new Vector3Int(11016,22111,26794),new Vector3Int(25946,32357,32677),new Vector3Int(17890,30452,32252),new Vector3Int(11678,25142,29816),new Vector3Int(6720,17534,24584),new Vector3Int(4230,11665,17820),new Vector3Int(28400,32623,32747),new Vector3Int(21164,31668,32575),new Vector3Int(13572,27388,31182),new Vector3Int(8234,20750,27358),new Vector3Int(5065,14055,20897),new Vector3Int(28981,32547,32705),new Vector3Int(18681,30543,32239),new Vector3Int(10919,24075,29286),new Vector3Int(6431,17199,24077),new Vector3Int(3819,10464,16618),new Vector3Int(26870,32467,32693),new Vector3Int(19041,30831,32347),new Vector3Int(11794,25211,30016),new Vector3Int(6888,18019,24970),new Vector3Int(4370,12363,18992),new Vector3Int(29578,32670,32744),new Vector3Int(23159,32007,32613),new Vector3Int(15315,28669,31676),new Vector3Int(9298,22607,28782),new Vector3Int(6144,15913,22968),new Vector3Int(7062,16472,22319),new Vector3Int(24538,32261,32674),new Vector3Int(13675,28041,31779),new Vector3Int(8590,20674,27631),new Vector3Int(5685,14675,22013),new Vector3Int(3655,9898,15731),new Vector3Int(26493,32418,32658),new Vector3Int(16376,29342,32090),new Vector3Int(10594,22649,28970),new Vector3Int(8176,17170,24303),new Vector3Int(5605,12694,19139),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(23888,31902,32542),new Vector3Int(18612,29687,31987),new Vector3Int(16245,24852,29249),new Vector3Int(15765,22608,27559),new Vector3Int(19895,24699,27510),new Vector3Int(9320,22102,27840),new Vector3Int(27057,32464,32724),new Vector3Int(16331,30268,32309),new Vector3Int(10319,23935,29720),new Vector3Int(6189,16448,24106),new Vector3Int(3589,10884,18808),new Vector3Int(29026,32624,32748),new Vector3Int(19226,31507,32587),new Vector3Int(12692,26921,31203),new Vector3Int(7049,19532,27635),new Vector3Int(7727,15669,23252),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(8192,16384,24576),new Vector3Int(28056,32625,32748),new Vector3Int(22383,32075,32669),new Vector3Int(15417,27098,31749),new Vector3Int(18127,26493,27190),new Vector3Int(5461,16384,21845),new Vector3Int(7754,16948,22142),new Vector3Int(25670,32330,32691),new Vector3Int(15663,29225,31994),new Vector3Int(9878,23288,29158),new Vector3Int(6419,17088,24336),new Vector3Int(3859,11003,17039),new Vector3Int(27562,32595,32725),new Vector3Int(17575,30588,32399),new Vector3Int(10819,24838,30309),new Vector3Int(7124,18686,25916),new Vector3Int(4479,12688,19340),new Vector3Int(28385,32476,32673),new Vector3Int(15306,29005,31938),new Vector3Int(8937,21615,28322),new Vector3Int(5982,15603,22786),new Vector3Int(3620,10267,16136),new Vector3Int(27280,32464,32667),new Vector3Int(15607,29160,32004),new Vector3Int(9091,22135,28740),new Vector3Int(6232,16632,24020),new Vector3Int(4047,11377,17672),new Vector3Int(29220,32630,32718),new Vector3Int(19650,31220,32462),new Vector3Int(13050,26312,30827),new Vector3Int(9228,20870,27468),new Vector3Int(6146,15149,21971),new Vector3Int(8626,20271,26216),new Vector3Int(26707,32406,32711),new Vector3Int(16999,30329,32286),new Vector3Int(11445,25123,30286),new Vector3Int(6411,18828,25601),new Vector3Int(6801,12458,20248),new Vector3Int(29918,32682,32748),new Vector3Int(20649,31739,32618),new Vector3Int(12879,27773,31581),new Vector3Int(7896,21751,28244),new Vector3Int(5260,14870,23698),new Vector3Int(29252,32593,32731),new Vector3Int(17072,30460,32294),new Vector3Int(10653,24143,29365),new Vector3Int(6536,17490,23983),new Vector3Int(4929,13170,20085),new Vector3Int(28137,32518,32715),new Vector3Int(18171,30784,32407),new Vector3Int(11437,25436,30459),new Vector3Int(7252,18534,26176),new Vector3Int(4126,13353,20978),new Vector3Int(31162,32726,32748),new Vector3Int(23017,32222,32701),new Vector3Int(15629,29233,32046),new Vector3Int(9387,22621,29480),new Vector3Int(6922,17616,25010)};
        private Vector3Int[] coeffBrDefault={new Vector3Int(14298,20718,24174),new Vector3Int(12536,19601,23789),new Vector3Int(8712,15051,19503),new Vector3Int(6170,11327,15434),new Vector3Int(4742,8926,12538),new Vector3Int(3803,7317,10546),new Vector3Int(1696,3317,4871),new Vector3Int(14392,19951,22756),new Vector3Int(15978,23218,26818),new Vector3Int(12187,19474,23889),new Vector3Int(9176,15640,20259),new Vector3Int(7068,12655,17028),new Vector3Int(5656,10442,14472),new Vector3Int(2580,4992,7244),new Vector3Int(12136,18049,21426),new Vector3Int(13784,20721,24481),new Vector3Int(10836,17621,21900),new Vector3Int(8372,14444,18847),new Vector3Int(6523,11779,16000),new Vector3Int(5337,9898,13760),new Vector3Int(3034,5860,8462),new Vector3Int(15967,22905,26286),new Vector3Int(13534,20654,24579),new Vector3Int(9504,16092,20535),new Vector3Int(6975,12568,16903),new Vector3Int(5364,10091,14020),new Vector3Int(4357,8370,11857),new Vector3Int(2506,4934,7218),new Vector3Int(23032,28815,30936),new Vector3Int(19540,26704,29719),new Vector3Int(15158,22969,27097),new Vector3Int(11408,18865,23650),new Vector3Int(8885,15448,20250),new Vector3Int(7108,12853,17416),new Vector3Int(4231,8041,11480),new Vector3Int(19823,26490,29156),new Vector3Int(18890,25929,28932),new Vector3Int(15660,23491,27433),new Vector3Int(12147,19776,24488),new Vector3Int(9728,16774,21649),new Vector3Int(7919,14277,19066),new Vector3Int(5440,10170,14185),new Vector3Int(14406,20862,24414),new Vector3Int(11824,18907,23109),new Vector3Int(8257,14393,18803),new Vector3Int(5860,10747,14778),new Vector3Int(4475,8486,11984),new Vector3Int(3606,6954,10043),new Vector3Int(1736,3410,5048),new Vector3Int(14430,20046,22882),new Vector3Int(15593,22899,26709),new Vector3Int(12102,19368,23811),new Vector3Int(9059,15584,20262),new Vector3Int(6999,12603,17048),new Vector3Int(5684,10497,14553),new Vector3Int(2822,5438,7862),new Vector3Int(15785,21585,24359),new Vector3Int(18347,25229,28266),new Vector3Int(14974,22487,26389),new Vector3Int(11423,18681,23271),new Vector3Int(8863,15350,20008),new Vector3Int(7153,12852,17278),new Vector3Int(3707,7036,9982),new Vector3Int(15460,21696,25469),new Vector3Int(12170,19249,23191),new Vector3Int(8723,15027,19332),new Vector3Int(6428,11704,15874),new Vector3Int(4922,9292,13052),new Vector3Int(4139,7695,11010),new Vector3Int(2291,4508,6598),new Vector3Int(19856,26920,29828),new Vector3Int(17923,25289,28792),new Vector3Int(14278,21968,26297),new Vector3Int(10910,18136,22950),new Vector3Int(8423,14815,19627),new Vector3Int(6771,12283,16774),new Vector3Int(4074,7750,11081),new Vector3Int(19852,26074,28672),new Vector3Int(19371,26110,28989),new Vector3Int(16265,23873,27663),new Vector3Int(12758,20378,24952),new Vector3Int(10095,17098,21961),new Vector3Int(8250,14628,19451),new Vector3Int(5205,9745,13622),new Vector3Int(14995,21341,24749),new Vector3Int(13158,20289,24601),new Vector3Int(8941,15326,19876),new Vector3Int(6297,11541,15807),new Vector3Int(4817,9029,12776),new Vector3Int(3731,7273,10627),new Vector3Int(1847,3617,5354),new Vector3Int(14472,19659,22343),new Vector3Int(16806,24162,27533),new Vector3Int(12900,20404,24713),new Vector3Int(9411,16112,20797),new Vector3Int(7056,12697,17148),new Vector3Int(5544,10339,14460),new Vector3Int(2954,5704,8319),new Vector3Int(12464,18071,21354),new Vector3Int(15482,22528,26034),new Vector3Int(12070,19269,23624),new Vector3Int(8953,15406,20106),new Vector3Int(7027,12730,17220),new Vector3Int(5887,10913,15140),new Vector3Int(3793,7278,10447),new Vector3Int(15571,22232,25749),new Vector3Int(14506,21575,25374),new Vector3Int(10189,17089,21569),new Vector3Int(7316,13301,17915),new Vector3Int(5783,10912,15190),new Vector3Int(4760,9155,13088),new Vector3Int(2993,5966,8774),new Vector3Int(23424,28903,30778),new Vector3Int(20775,27666,30290),new Vector3Int(16474,24410,28299),new Vector3Int(12471,20180,24987),new Vector3Int(9410,16487,21439),new Vector3Int(7536,13614,18529),new Vector3Int(5048,9586,13549),new Vector3Int(21090,27290,29756),new Vector3Int(20796,27402,30026),new Vector3Int(17819,25485,28969),new Vector3Int(13860,21909,26462),new Vector3Int(11002,18494,23529),new Vector3Int(8953,15929,20897),new Vector3Int(6448,11918,16454),new Vector3Int(15999,22208,25449),new Vector3Int(13050,19988,24122),new Vector3Int(8594,14864,19378),new Vector3Int(6033,11079,15238),new Vector3Int(4554,8683,12347),new Vector3Int(3672,7139,10337),new Vector3Int(1900,3771,5576),new Vector3Int(15788,21340,23949),new Vector3Int(16825,24235,27758),new Vector3Int(12873,20402,24810),new Vector3Int(9590,16363,21094),new Vector3Int(7352,13209,17733),new Vector3Int(5960,10989,15184),new Vector3Int(3232,6234,9007),new Vector3Int(15761,20716,23224),new Vector3Int(19318,25989,28759),new Vector3Int(15529,23094,26929),new Vector3Int(11662,18989,23641),new Vector3Int(8955,15568,20366),new Vector3Int(7281,13106,17708),new Vector3Int(4248,8059,11440),new Vector3Int(14899,21217,24503),new Vector3Int(13519,20283,24047),new Vector3Int(9429,15966,20365),new Vector3Int(6700,12355,16652),new Vector3Int(5088,9704,13716),new Vector3Int(4243,8154,11731),new Vector3Int(2702,5364,7861),new Vector3Int(22745,28388,30454),new Vector3Int(20235,27146,29922),new Vector3Int(15896,23715,27637),new Vector3Int(11840,19350,24131),new Vector3Int(9122,15932,20880),new Vector3Int(7488,13581,18362),new Vector3Int(5114,9568,13370),new Vector3Int(20845,26553,28932),new Vector3Int(20981,27372,29884),new Vector3Int(17781,25335,28785),new Vector3Int(13760,21708,26297),new Vector3Int(10975,18415,23365),new Vector3Int(9045,15789,20686),new Vector3Int(6130,11199,15423),new Vector3Int(16138,22223,25509),new Vector3Int(15347,22430,26332),new Vector3Int(9614,16736,21332),new Vector3Int(6600,12275,16907),new Vector3Int(4811,9424,13547),new Vector3Int(3748,7809,11420),new Vector3Int(2254,4587,6890),new Vector3Int(15196,20284,23177),new Vector3Int(18317,25469,28451),new Vector3Int(13918,21651,25842),new Vector3Int(10052,17150,21995),new Vector3Int(7499,13630,18587),new Vector3Int(6158,11417,16003),new Vector3Int(4014,7785,11252),new Vector3Int(15048,21067,24384),new Vector3Int(18202,25346,28553),new Vector3Int(14302,22019,26356),new Vector3Int(10839,18139,23166),new Vector3Int(8715,15744,20806),new Vector3Int(7536,13576,18544),new Vector3Int(5413,10335,14498),new Vector3Int(17394,24501,27895),new Vector3Int(15889,23420,27185),new Vector3Int(11561,19133,23870),new Vector3Int(8285,14812,19844),new Vector3Int(6496,12043,16550),new Vector3Int(4771,9574,13677),new Vector3Int(3603,6830,10144),new Vector3Int(21656,27704,30200),new Vector3Int(21324,27915,30511),new Vector3Int(17327,25336,28997),new Vector3Int(13417,21381,26033),new Vector3Int(10132,17425,22338),new Vector3Int(8580,15016,19633),new Vector3Int(5694,11477,16411),new Vector3Int(24116,29780,31450),new Vector3Int(23853,29695,31591),new Vector3Int(20085,27614,30428),new Vector3Int(15326,24335,28575),new Vector3Int(11814,19472,24810),new Vector3Int(10221,18611,24767),new Vector3Int(7689,14558,20321),new Vector3Int(16214,22380,25770),new Vector3Int(14213,21304,25295),new Vector3Int(9213,15823,20455),new Vector3Int(6395,11758,16139),new Vector3Int(4779,9187,13066),new Vector3Int(3821,7501,10953),new Vector3Int(2293,4567,6795),new Vector3Int(15859,21283,23820),new Vector3Int(18404,25602,28726),new Vector3Int(14325,21980,26206),new Vector3Int(10669,17937,22720),new Vector3Int(8297,14642,19447),new Vector3Int(6746,12389,16893),new Vector3Int(4324,8251,11770),new Vector3Int(16532,21631,24475),new Vector3Int(20667,27150,29668),new Vector3Int(16728,24510,28175),new Vector3Int(12861,20645,25332),new Vector3Int(10076,17361,22417),new Vector3Int(8395,14940,19963),new Vector3Int(5731,10683,14912),new Vector3Int(14433,21155,24938),new Vector3Int(14658,21716,25545),new Vector3Int(9923,16824,21557),new Vector3Int(6982,13052,17721),new Vector3Int(5419,10503,15050),new Vector3Int(4852,9162,13014),new Vector3Int(3271,6395,9630),new Vector3Int(22210,27833,30109),new Vector3Int(20750,27368,29821),new Vector3Int(16894,24828,28573),new Vector3Int(13247,21276,25757),new Vector3Int(10038,17265,22563),new Vector3Int(8587,14947,20327),new Vector3Int(5645,11371,15252),new Vector3Int(22027,27526,29714),new Vector3Int(23098,29146,31221),new Vector3Int(19886,27341,30272),new Vector3Int(15609,23747,28046),new Vector3Int(11993,20065,24939),new Vector3Int(9637,18267,23671),new Vector3Int(7625,13801,19144),new Vector3Int(18315,24289,27551),new Vector3Int(16854,24068,27835),new Vector3Int(10140,17927,23173),new Vector3Int(6722,12982,18267),new Vector3Int(4661,9826,14706),new Vector3Int(3832,8165,12294),new Vector3Int(2795,6098,9245),new Vector3Int(17145,23326,26672),new Vector3Int(20733,27680,30308),new Vector3Int(16032,24461,28546),new Vector3Int(11653,20093,25081),new Vector3Int(9290,16429,22086),new Vector3Int(7796,14598,19982),new Vector3Int(6502,12378,17441),new Vector3Int(21681,27732,30320),new Vector3Int(22389,29044,31261),new Vector3Int(19027,26731,30087),new Vector3Int(14739,23755,28624),new Vector3Int(11358,20778,25511),new Vector3Int(10995,18073,24190),new Vector3Int(9162,14990,20617),new Vector3Int(21425,27952,30388),new Vector3Int(18062,25838,29034),new Vector3Int(11956,19881,24808),new Vector3Int(7718,15000,20980),new Vector3Int(5702,11254,16143),new Vector3Int(4898,9088,16864),new Vector3Int(3679,6776,11907),new Vector3Int(23294,30160,31663),new Vector3Int(24397,29896,31836),new Vector3Int(19245,27128,30593),new Vector3Int(13202,19825,26404),new Vector3Int(11578,19297,23957),new Vector3Int(8073,13297,21370),new Vector3Int(5461,10923,19745),new Vector3Int(27367,30521,31934),new Vector3Int(24904,30671,31940),new Vector3Int(23075,28460,31299),new Vector3Int(14400,23658,30417),new Vector3Int(13885,23882,28325),new Vector3Int(14746,22938,27853),new Vector3Int(5461,16384,27307),new Vector3Int(18274,24813,27890),new Vector3Int(15537,23149,27003),new Vector3Int(9449,16740,21827),new Vector3Int(6700,12498,17261),new Vector3Int(4988,9866,14198),new Vector3Int(4236,8147,11902),new Vector3Int(2867,5860,8654),new Vector3Int(17124,23171,26101),new Vector3Int(20396,27477,30148),new Vector3Int(16573,24629,28492),new Vector3Int(12749,20846,25674),new Vector3Int(10233,17878,22818),new Vector3Int(8525,15332,20363),new Vector3Int(6283,11632,16255),new Vector3Int(20466,26511,29286),new Vector3Int(23059,29174,31191),new Vector3Int(19481,27263,30241),new Vector3Int(15458,23631,28137),new Vector3Int(12416,20608,25693),new Vector3Int(10261,18011,23261),new Vector3Int(8016,14655,19666),new Vector3Int(17616,24586,28112),new Vector3Int(15809,23299,27155),new Vector3Int(10767,18890,23793),new Vector3Int(7727,14255,18865),new Vector3Int(6129,11926,16882),new Vector3Int(4482,9704,14861),new Vector3Int(3277,7452,11522),new Vector3Int(22956,28551,30730),new Vector3Int(22724,28937,30961),new Vector3Int(18467,26324,29580),new Vector3Int(13234,20713,25649),new Vector3Int(11181,17592,22481),new Vector3Int(8291,18358,24576),new Vector3Int(7568,11881,14984),new Vector3Int(24948,29001,31147),new Vector3Int(25674,30619,32151),new Vector3Int(20841,26793,29603),new Vector3Int(14669,24356,28666),new Vector3Int(11334,23593,28219),new Vector3Int(8922,14762,22873),new Vector3Int(8301,13544,20535)};
        private Vector3Int[] dcSignDefault={new Vector3Int(16000,0,0),new Vector3Int(13056,0,0),new Vector3Int(18816,0,0),new Vector3Int(15232,0,0),new Vector3Int(12928,0,0),new Vector3Int(17280,0,0),new Vector3Int(16000,0,0),new Vector3Int(13056,0,0),new Vector3Int(18816,0,0),new Vector3Int(15232,0,0),new Vector3Int(12928,0,0),new Vector3Int(17280,0,0),new Vector3Int(16000,0,0),new Vector3Int(13056,0,0),new Vector3Int(18816,0,0),new Vector3Int(15232,0,0),new Vector3Int(12928,0,0),new Vector3Int(17280,0,0),new Vector3Int(16000,0,0),new Vector3Int(13056,0,0),new Vector3Int(18816,0,0),new Vector3Int(15232,0,0),new Vector3Int(12928,0,0),new Vector3Int(17280,0,0)};
        private int[] dcQuant = {4,8,8,9,10,11,12,12,13,14,15,16,17,18,19,19,20,21,22,23,24,25,26,26,27,28,29,30,31,32,32,33,34,35,36,37,38,38,39,40,41,42,43,43,44,45,46,47,48,48,49,50,51,52,53,53,54,55,56,57,57,58,59,60,61,62,62,63,64,65,66,66,67,68,69,70,70,71,72,73,74,74,75,76,77,78,78,79,80,81,81,82,83,84,85,85,87,88,90,92,93,95,96,98,99,101,102,104,105,107,108,110,111,113,114,116,117,118,120,121,123,125,127,129,131,134,136,138,140,142,144,146,148,150,152,154,156,158,161,164,166,169,172,174,177,180,182,185,187,190,192,195,199,202,205,208,211,214,217,220,223,226,230,233,237,240,243,247,250,253,257,261,265,269,272,276,280,284,288,292,296,300,304,309,313,317,322,326,330,335,340,344,349,354,359,364,369,374,379,384,389,395,400,406,411,417,423,429,435,441,447,454,461,467,475,482,489,497,505,513,522,530,539,549,559,569,579,590,602,614,626,640,654,668,684,700,717,736,755,775,796,819,843,869,896,925,955,988,1022,1058,1098,1139,1184,1232,1282,1336};
        private int[] acQuant = {4,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,74,75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,95,96,97,98,99,100,101,102,104,106,108,110,112,114,116,118,120,122,124,126,128,130,132,134,136,138,140,142,144,146,148,150,152,155,158,161,164,167,170,173,176,179,182,185,188,191,194,197,200,203,207,211,215,219,223,227,231,235,239,243,247,251,255,260,265,270,275,280,285,290,295,300,305,311,317,323,329,335,341,347,353,359,366,373,380,387,394,401,408,416,424,432,440,448,456,465,474,483,492,501,510,520,530,540,550,560,571,582,593,604,615,627,639,651,663,676,689,702,715,729,743,757,771,786,801,816,832,848,864,881,898,915,933,951,969,988,1007,1026,1046,1066,1087,1108,1129,1151,1173,1196,1219,1243,1267,1292,1317,1343,1369,1396,1423,1451,1479,1508,1537,1567,1597,1628,1660,1692,1725,1759,1793,1828};
        private int[] scan4 = {0,1,4,8,5,2,3,6,9,12,13,10,7,11,14,15};
        private int[] scan8 = {0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63};
    }
}

