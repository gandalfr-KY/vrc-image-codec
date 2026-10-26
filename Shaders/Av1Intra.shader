// AV1 fragment kernels with bounded, fixed-prior rate-distortion decisions. Integer DCT routines retain tinyavif/AOM licenses.
Shader "VrcImageCodec/AV1 Intra"
{
 Properties { [HideInInspector] _AomCosts("AV1 rate costs",2D)="black"{} _MainTex("Previous stage",2D)="black"{} }
 SubShader {
  Cull Off ZWrite Off ZTest Always
  CGINCLUDE
  #include "UnityCG.cginc"
  #include "CodecCommon.hlsl"
  #include "Av1Transforms.hlsl"
  Texture2D<float4> _MainTex,_ReconTex,_QuantTex,_SourceTex,_ModeTex,_CflTex,_OptimizedTex,_CandidateTex,_UvCandidates,_UvModeTex,_ModeCosts,_Shortlist,_SourceYuvTex;
  float4 _AtlasSize,_PhotoSize,_PackedSize;
  int _Chroma444;
  int SampleCount(){return _Chroma444!=0?192:96;}
  int TileRows(){return _Chroma444!=0?24:12;}
  int PacketCount(){return _Chroma444!=0?195:99;}
  int PlaneOffset(int plane){return plane==0?0:plane==1?64:(_Chroma444!=0?128:80);}
  int HeaderOffset(int plane){return plane==0?0:plane==1?65:(_Chroma444!=0?130:82);}
  int PlaneSize(int plane){return plane==0||_Chroma444!=0?8:4;}
  int _Diagonal,_QuantDc,_QuantAc,_LinearInput,_FlipSourceY,_ChromaStage;
  // Each logical 8x8 block occupies an adjacent 8x12 tile (Y:64, U:16, V:16).
  int indexAt(float2 uv){
   int2 p=min((int2)(uv*_AtlasSize.xy),(int2)_AtlasSize.xy-1);
   return ((p.y/TileRows())*(int)_PhotoSize.z+p.x/8)*SampleCount()+(p.y%TileRows())*8+p.x%8;
  }
  float4 atIndex(Texture2D<float4> tex,int index){
   int block=index/SampleCount(),local=index%SampleCount();
   return tex.Load(int3((block%(int)_PhotoSize.z)*8+local%8,(block/(int)_PhotoSize.z)*TileRows()+local/8,0));
  }
  int unpackInt(float4 value){int2 b=(int2)floor(value.rg*255+.5);return b.x+b.y*256-32768;}
  float4 packInt(int value,int pred,int mode){int p=clamp(value,-32768,32767)+32768;return float4(p&255,p>>8,pred,mode)/255.;}
  int rawAt(int block,int local){return (int)floor(atIndex(_ReconTex,block*SampleCount()+local).r*255+.5);}
  void identify(int index,out int block,out int plane,out int n,out int local,out int2 xy){
   block=index/SampleCount();int slot=index%SampleCount();plane=slot<64?0:slot<(_Chroma444!=0?128:80)?1:2;n=PlaneSize(plane);
   local=slot-(PlaneOffset(plane));xy=int2(block%(int)_PhotoSize.z,block/(int)_PhotoSize.z);
  }
  int dcPrediction(int block,int plane,int n,int2 xy){
   int offset=PlaneOffset(plane),sum=0;
   [loop]for(int k=0;k<n;k++){
    if(xy.y>0)sum+=rawAt(block-(int)_PhotoSize.z,offset+(n-1)*n+k);
    if(xy.x>0)sum+=rawAt(block-1,offset+k*n+n-1);
   }
   int count=((xy.x>0?1:0)+(xy.y>0?1:0))*n;return count==0?128:(sum+count/2)/count;
  }
  int3 sourcePixel(int2 p){
   p=clamp(p,0,(int2)_PhotoSize.xy-1);if(_FlipSourceY!=0)p.y=(int)_PhotoSize.y-1-p.y;
   float3 rgb=_SourceTex.Load(int3(p,0)).rgb;
   if(_LinearInput!=0)rgb=float3(LinearToGammaSpaceExact(rgb.r),LinearToGammaSpaceExact(rgb.g),LinearToGammaSpaceExact(rgb.b));
   return (int3)floor(saturate(rgb)*255+.5);
  }
  // Cache the exact integer RGB-to-YUV conversion once per photograph.
  // Full-resolution Y is followed by half-resolution interleaved U/V.
  float4 prepareSourceYuv(v2f_img i):SV_Target{
   int2 p=min((int2)(i.uv*float2(_PhotoSize.x,_PhotoSize.y*(_Chroma444!=0?2.:1.5))),int2(_PhotoSize.x-1,_PhotoSize.y*(_Chroma444!=0?2.:1.5)-1));
   if(p.y<(int)_PhotoSize.y){int3 rgb=sourcePixel(p);return float4(((19595*rgb.r+38470*rgb.g+7471*rgb.b+32768)>>16)/255.,0,0,1);}
   if(_Chroma444!=0){int3 rgb=sourcePixel(int2(p.x,p.y-(int)_PhotoSize.y));
    int u=clamp((-11059*rgb.r-21709*rgb.g+32768*rgb.b+8421376)>>16,0,255);
    int v=clamp((32768*rgb.r-27439*rgb.g-5329*rgb.b+8421376)>>16,0,255);
    return float4(u/255.,v/255.,0,1);}
   if(p.x>=(int)_PhotoSize.x/2)return 0;
   int2 pixel=int2(p.x,p.y-(int)_PhotoSize.y)*2;
   int3 sum=sourcePixel(pixel)+sourcePixel(pixel+int2(1,0))+sourcePixel(pixel+int2(0,1))+sourcePixel(pixel+int2(1,1));
   int u=clamp((-11059*sum.r-21709*sum.g+32768*sum.b+33685504)>>18,0,255);
   int v=clamp((32768*sum.r-27439*sum.g-5329*sum.b+33685504)>>18,0,255);
   return float4(u/255.,v/255.,0,1);
  }
  int sourceValue(int2 block,int plane,int row,int col){
   int2 p=block*PlaneSize(plane)+int2(col,row);
   if(plane!=0)p.y+=(int)_PhotoSize.y;
   float4 value=_SourceYuvTex.Load(int3(p,0));
   return (int)floor((plane==2?value.g:value.r)*255+.5);
  }
  // AOM intrapred.c / intrapred_common.h, BSD-2-Clause + AOM patent grant.
  static const int smoothWeights[8]={255,197,146,105,73,50,37,32};
  int blockOrder(int2 p){int rank=0;[unroll]for(int k=0;k<3;k++)rank|=((p.x>>k)&1)<<(2*k)|((p.y>>k)&1)<<(2*k+1);return ((p.y/8)*(((int)_PhotoSize.z+7)/8)+p.x/8)*64+rank;}
  int topSample(int block,int sample){int cols=(int)_PhotoSize.z;int2 xy=int2(block%cols,block/cols);if(sample<8)return rawAt(block-cols,56+sample);int2 tr=xy+int2(1,-1);if(tr.x>=cols||blockOrder(tr)>=blockOrder(xy))return rawAt(block-cols,63);return rawAt(block-cols+1,56+min(sample-8,7));}
  static const int slopes[90]={

  0,    0, 0,        
  1023, 0, 0,        
  547,  0, 0,        
  372,  0, 0, 0, 0,  
  273,  0, 0,        
  215,  0, 0,        
  178,  0, 0,        
  151,  0, 0,        
  132,  0, 0,        
  116,  0, 0,        
  102,  0, 0, 0,     
  90,   0, 0,        
  80,   0, 0,        
  71,   0, 0,        
  64,   0, 0,        
  57,   0, 0,        
  51,   0, 0,        
  45,   0, 0, 0,     
  40,   0, 0,        
  35,   0, 0,        
  31,   0, 0,        
  27,   0, 0,        
  23,   0, 0,        
  19,   0, 0,        
  15,   0, 0, 0, 0,  
  11,   0, 0,        
  7,    0, 0,        
  3,    0, 0,        
};
  static const int baseAngles[9]={0,90,180,45,135,113,157,203,67};
  static const int angleCodes[51]={48,1,17,33,49,65,81,97,2,18,34,50,3,19,35,51,67,83,99,4,20,36,52,68,84,100,5,21,37,53,69,85,101,6,22,38,54,70,86,102,8,24,40,56,72,88,104,57,58,59,60};
  bool availableCode(int code,int2 xy){int mode=code&15,delta=(code>>4)-3;if(mode==0)return true;if(mode>8)return xy.x>0&&xy.y>0;int angle=baseAngles[mode]+delta*3;if(angle<=90)return xy.y>0;if(angle==180)return xy.x>0;return xy.x>0&&xy.y>0;}
  int predictor(int block,int n,int offset,int mode,int row,int col,int dc){
   int delta=(mode>>4)-3;mode&=15;if(mode==0)return dc;int columns=(int)_PhotoSize.z;
   if(mode<=8){
    int angle=baseAngles[mode]+3*delta;
    if(angle==90)return rawAt(block-columns,offset+(n-1)*n+col);
    if(angle==180)return rawAt(block-1,offset+row*n+n-1);
    if(angle<90){int pos=(col<<6)+(row+1)*slopes[angle],base=pos>>6,shift=(pos&63)>>1;return (topSample(block,min(base,15))*(32-shift)+topSample(block,min(base+1,15))*shift+16)>>5;}
    int dx=slopes[180-angle],dy=slopes[angle-90];
    int pos=(col<<6)-(row+1)*dx,base=pos>>6,shift=(pos&63)>>1,tl=rawAt(block-columns-1,63),a,b;
    if(base>=-1){a=base<0?tl:rawAt(block-columns,56+base);b=rawAt(block-columns,56+base+1);}
    else{pos=(row<<6)-(col+1)*dy;base=pos>>6;shift=(pos&63)>>1;a=base<0?tl:rawAt(block-1,base*8+7);b=rawAt(block-1,(base+1)*8+7);}
    return (a*(32-shift)+b*shift+16)>>5;
   }
   int top=rawAt(block-columns,56+col),left=rawAt(block-1,row*8+7);
   if(mode==12){int tl=rawAt(block-columns-1,63),b=top+left-tl;
    int dl=abs(b-left),dt=abs(b-top),dcorn=abs(b-tl);return dl<=dt&&dl<=dcorn?left:dt<=dcorn?top:tl;}
   int below=rawAt(block-1,63),right=rawAt(block-columns,63);
   int vertical=smoothWeights[row]*top+(256-smoothWeights[row])*below;
   int horizontal=smoothWeights[col]*left+(256-smoothWeights[col])*right;
   return mode==10?(vertical+128)>>8:mode==11?(horizontal+128)>>8:(vertical+horizontal+256)>>9;
  }

  
  #include "AomAdst.hlsl"
  #include "AomRdo.hlsl"

  // Evaluate each prediction once per block in a small (width/8,height/8) pass.

  float4 selectPrediction(v2f_img i):SV_Target{
   int2 xy=min((int2)(i.uv*_PhotoSize.zw),(int2)_PhotoSize.zw-1);
   if(xy.x+2*xy.y!=_Diagonal)return 0;
   int best=0;float cost=3.4e38;
   [loop]for(int k=0;k<40;k++){float c=_CandidateTex.Load(int3(xy.x*40+k,xy.y,0)).r;if(c<cost){cost=c;best=k;}}
   int code=(int)round(_Shortlist.Load(int3(xy.x*2+best/20,xy.y,0))[(best/5)%4]);
   return float4(code&15,code>>4,best%5,255)/255.;
  }

  int lumaQ3(int block,int r,int c){int p=r*16+c*2;return (rawAt(block,p)+rawAt(block,p+1)+rawAt(block,p+8)+rawAt(block,p+9))*2;}
  int lumaAverage(int block){int sum=0;[loop]for(int r=0;r<4;r++)[loop]for(int c=0;c<4;c++)sum+=lumaQ3(block,r,c);return (sum+8)>>4;}
  int cflPrediction(int dc,int ac,int alpha){int scaled=ac*alpha;return clamp(dc+(scaled<0?-((-scaled+32)>>6):(scaled+32)>>6),0,255);}
float cflTrialCost(int block,int2 xy,int plane,int alpha,int average);
  float4 selectCfl(v2f_img i):SV_Target{
   int2 xy=min((int2)(i.uv*_PhotoSize.zw),(int2)_PhotoSize.zw-1);
   if(xy.x+2*xy.y!=_Diagonal)return 0;
   float2 bestCost=3.4e38;int2 best=0;
   [loop]for(int k=0;k<33;k++){
    float2 cost=_ModeCosts.Load(int3(xy.x*51+k,xy.y,0)).rg;
    if(cost.x<bestCost.x){bestCost.x=cost.x;best.x=k;}
    if(cost.y<bestCost.y){bestCost.y=cost.y;best.y=k;}
   }
   return float4(best,0,255)/255.;
  }


#include "AomUvRdo.hlsl"
// AOM basic UV prediction and inferred UV transform type. CfL remains a candidate.
static const int uvModes[8]={0,1,2,9,10,11,12,13};
static const int uvWeights[4]={255,149,85,64};
int uvType(int mode){return mode==1||mode==10?1:mode==2||mode==11?2:mode==9||mode==12?3:0;}
int uvPredict(int block,int2 xy,int plane,int mode,int r,int c,int dc,int alpha,int average){
 if(mode==0)return dc;
 if(mode==13)return cflPrediction(dc,lumaQ3(block,r,c)-average,alpha);
 int off=plane==1?64:80,columns=(int)_PhotoSize.z;
 int top=rawAt(block-columns,off+12+c),left=rawAt(block-1,off+r*4+3);
 if(mode==1)return top;if(mode==2)return left;
 if(mode==12){int tl=rawAt(block-columns-1,off+15),b=top+left-tl;
  int dl=abs(b-left),dt=abs(b-top),d=abs(b-tl);return dl<=dt&&dl<=d?left:dt<=d?top:tl;}
 int down=rawAt(block-1,off+15),right=rawAt(block-columns,off+15);
 int vertical=uvWeights[r]*top+(256-uvWeights[r])*down,horizontal=uvWeights[c]*left+(256-uvWeights[c])*right;
 return mode==10?(vertical+128)>>8:mode==11?(horizontal+128)>>8:(vertical+horizontal+256)>>9;
}
void uvTransform(int block,int2 xy,int plane,int mode,int alpha,int average,out int t[16]){
 int dc=dcPrediction(block,plane,4,xy),tx=uvType(mode),v[8];
 [loop]for(int c=0;c<4;c++){
  [loop]for(int r=0;r<4;r++)v[r]=(sourceValue(xy,plane,r,c)-uvPredict(block,xy,plane,mode,r,c,dc,alpha,average))*4;
  if(tx==1||tx==3)fwd_adst4(v,13,0);else fwd_dct4(v,13,0);
  [loop]for(int r=0;r<4;r++)t[r*4+c]=v[r];
 }
 [loop]for(int r=0;r<4;r++){
  [loop]for(int c=0;c<4;c++)v[c]=t[r*4+c];
  if(tx==2||tx==3)fwd_adst4(v,13,0);else fwd_dct4(v,13,0);
  [loop]for(int c=0;c<4;c++)t[r*4+c]=v[c];
 }
}
float cflTrialCost(int block,int2 xy,int plane,int alpha,int average){
 int t[16],q[16],end=0;float distortion=0;
 uvTransform(block,xy,plane,13,alpha,average,t);
 [unroll]for(int k=0;k<16;k++){q[k]=QuantAt(t[k],plane,k);float d=t[k]-q[k]*StepBase(plane,k);distortion+=d*d;}
 [unroll]for(int k=0;k<16;k++)if(q[UvScan[k]]!=0)end=k+1;
 float alphaBits=alpha==0?1:3+firstbithigh(abs(alpha));
 return distortion+(float)_QuantUvAc*_QuantUvAc*PredictionRateWeight*(UvRate(q,end)/512.+alphaBits);
}
float4 chromaCandidate(v2f_img i):SV_Target{
 int2 p=min((int2)(i.uv*float2(_PhotoSize.z*8,_PhotoSize.w)),int2(_PhotoSize.z*8-1,_PhotoSize.w-1));
 int2 xy=int2(p.x/8,p.y);if(xy.x+2*xy.y!=_Diagonal)return 0;
 int mode=uvModes[p.x%8];if(mode!=0&&mode!=13&&(xy.x==0||xy.y==0))return float4(1e30,0,0,0);
 int block=xy.y*(int)_PhotoSize.z+xy.x,average=lumaAverage(block);
 int2 alphas=(int2)round(_CflTex.Load(int3(xy,0)).rg*255)-16;
 if(mode==13&&alphas.x==0&&alphas.y==0)return float4(1e30,0,0,0);
 float distortion=0,rate=mode==0?1:mode==13?4+firstbithigh(abs(alphas.x)+1)+firstbithigh(abs(alphas.y)+1):4;
 [loop]for(int plane=1;plane<=2;plane++){
  int t[16],alpha=plane==1?alphas.x:alphas.y;
  uvTransform(block,xy,plane,mode,alpha,average,t);
  int optimized[16];distortion+=OptimizeUv(t,plane,optimized);

 }
 return float4(distortion+_QuantUvAc*_QuantUvAc*.07*rate,0,0,0);
}
float4 selectChroma(v2f_img i):SV_Target{
 int2 xy=min((int2)(i.uv*_PhotoSize.zw),(int2)_PhotoSize.zw-1);if(xy.x+2*xy.y!=_Diagonal)return 0;
 float cost=1e30;int best=0;
 [unroll]for(int k=0;k<8;k++){float c=_UvCandidates.Load(int3(xy.x*8+k,xy.y,0)).r;if(c<cost){best=k;cost=c;}}
 return float4(_CflTex.Load(int3(xy,0)).rg,uvModes[best]/255.,1);
}

  float4 forwardCoefficient(v2f_img i):SV_Target{
   int index=indexAt(i.uv),block,plane,n,local;int2 xy;identify(index,block,plane,n,local,xy);
   if(xy.y>=(int)_PhotoSize.w||xy.x+2*xy.y!=_Diagonal||(((_ChromaStage==0&&plane!=0)||(_ChromaStage==1&&plane==0))))discard;
   int mode=plane==0?(int)floor(_ModeTex.Load(int3(xy,0)).r*255+.5):0;
   if(plane==0)mode+=16*(int)round(_ModeTex.Load(int3(xy,0)).g*255);
   int dc=dcPrediction(block,plane,n,xy);
   int alpha=0,average=0;int uvMode=plane==0?0:(int)round(_UvModeTex.Load(int3(xy,0)).b*255);
   if(plane!=0){float4 alphas=_CflTex.Load(int3(xy,0));alpha=(int)floor((plane==1?alphas.r:alphas.g)*255+.5)-16;average=lumaAverage(block);}
   if(plane==0){float4 values=_OptimizedTex.Load(int3(xy.x*16+local/4,xy.y,0));return packInt((int)round(values[local%4]),predictor(block,8,0,mode,local/8,local%8,dc),local==1?(mode>>4):(mode&15)+16*(int)round(_ModeTex.Load(int3(xy,0)).b*255));}
   if(plane!=0&&_Chroma444!=0){
    float4 values=_OptimizedTex.Load(int3(xy.x*32+(plane-1)*16+local/4,xy.y,0));
    return packInt((int)round(values[local%4]),dc,64);}
   if(plane!=0){
    float4 values=_OptimizedTex.Load(int3(xy.x*16+(plane-1)*4+local/4,xy.y,0));
    return packInt((int)round(values[local%4]),uvPredict(block,xy,plane,uvMode,local/4,local%4,dc,alpha,average),uvMode==13?alpha+16:64+uvMode);
   }
   return 0; // Both luma and chroma return above.
  }
  float4 reconstructPixel(v2f_img i):SV_Target{
   int index=indexAt(i.uv),block,plane,n,local;int2 xy;identify(index,block,plane,n,local,xy);
   if(xy.y>=(int)_PhotoSize.w||xy.x+2*xy.y!=_Diagonal||(((_ChromaStage==0&&plane!=0)||(_ChromaStage==1&&plane==0))))discard;
   int txType=plane==0?(int)round(_ModeTex.Load(int3(xy,0)).b*255):uvType((int)round(_UvModeTex.Load(int3(xy,0)).b*255));
   int row=local/n,col=local%n,v[8];
   [loop]for(int r=0;r<n;r++){
    int k=r*n+col;int texel=plane==0?k/4:(plane-1)*(_Chroma444!=0?16:4)+k/4;
    v[r]=(int)round(_OptimizedTex.Load(int3(xy.x*(_Chroma444!=0&&plane!=0?32:16)+texel,xy.y,0))[k%4]);
   }
   if(n==8){if(txType==4){[unroll]for(int z=0;z<8;z++)v[z]*=2;}else if(txType==1||txType==3)inv_adst8(v,12,16);else inv_dct8(v,12,16);}else {if(txType==1||txType==3)inv_adst4(v,12,16);else inv_dct4(v,12,16);}
   int pred=(int)floor(atIndex(_QuantTex,index).b*255+.5);
   return float4(clamp(pred+((v[row]+8)>>4),0,255)/255.,0,0,1);
  }
  // Packing and coefficient contexts follow the original byte layout.
  static const int scan4[16]={0,1,4,8,5,2,3,6,9,12,13,10,7,11,14,15};
  static const int scan8[64]={0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63};
  int coefficient(int index){int2 b=(int2)floor(atIndex(_QuantTex,index).rg*255+.5);return b.x+256*b.y-32768;}

  // Second-stage option: contexts depend only on quantized neighbours, not on the range coder.
  static const int locations[25]={0,1,6,6,21,1,6,6,21,21,6,6,21,21,21,6,21,21,21,21,21,21,21,21,21};
  float4 packContexts(v2f_img i):SV_Target
  {
   int2 p=min((int2)(i.uv*_PackedSize.xy),(int2)_PackedSize.xy-1);
   int index=p.y*(int)_PackedSize.x+p.x,block=index/PacketCount(),slot=index%PacketCount();
   if(block>=(int)(_PhotoSize.z*_PhotoSize.w))return 0;
   int plane=slot<65?0:slot<(_Chroma444!=0?130:82)?1:2,local=slot-(HeaderOffset(plane));
   int n=PlaneSize(plane),count=n*n,offset=block*SampleCount()+(PlaneOffset(plane));
   if(local==0)
   {
    int eob=0,level=0;
    [loop]for(int k=0;k<count;k++){int c=coefficient(offset+(plane==0||_Chroma444!=0?scan8[k]:scan4[k]));level+=abs(c);if(c!=0)eob=k+1;}
    int meta=(int)round(atIndex(_QuantTex,offset).a*255);
    return float4(eob,min(level,63),plane==0?((int)floor(atIndex(_QuantTex,offset).a*255+.5)&15):(meta>=64?16:meta),plane==0?((int)floor(atIndex(_QuantTex,offset).a*255+.5)>>4)+8*(int)round(atIndex(_QuantTex,offset+1).a*255):(meta>=64?meta-64:13))/255.;
   }
   int loc=local-1,row=loc/n,col=loc%n,mag=0,brMag=0;
   const int rr[5]={0,1,1,0,2},cc[5]={1,0,1,2,0};
   [unroll]for(int k=0;k<5;k++)
   {
    int r=row+rr[k],c=col+cc[k];
    if(r<n&&c<n){int value=abs(coefficient(offset+r*n+c));mag+=min(value,3);if(k<3)brMag+=min(value,15);}
   }
   int baseCtx=loc==0?0:min((mag+1)/2,4)+locations[min(row,4)*5+min(col,4)];
   int brCtx=min((brMag+1)/2,6)+(loc==0?0:row<2&&col<2?7:14);
   return float4(atIndex(_QuantTex,offset+loc).rg,float2(baseCtx,brCtx)/255.);
  }

  void transformCandidate(int block,int2 xy,int mode,int txType,out int transformed[64]){
   int dc=dcPrediction(block,0,8,xy),v[8];
   [loop]for(int c=0;c<8;c++){
    [loop]for(int r=0;r<8;r++)v[r]=(sourceValue(xy,0,r,c)-predictor(block,8,0,mode,r,c,dc))*4;
    if(txType==4){[unroll]for(int z=0;z<8;z++)v[z]*=2;}else if(txType==1||txType==3)fwd_adst8(v,13,0);else fwd_dct8(v,13,0);
    [loop]for(int r=0;r<8;r++)transformed[r*8+c]=(v[r]+1)>>1;
   }
   [loop]for(int r=0;r<8;r++){
    [loop]for(int c=0;c<8;c++)v[c]=transformed[r*8+c];
    if(txType==4){[unroll]for(int z=0;z<8;z++)v[z]*=2;}else if(txType==2||txType==3)fwd_adst8(v,13,0);else fwd_dct8(v,13,0);
    [loop]for(int c=0;c<8;c++)transformed[r*8+c]=v[c];
   }
  }
// Native AV1 default intra syntax rates, Q9 bits. BSD-2-Clause + PATENTS.
static const int Rate_intraYDefault[325]={549,2309,1959,2672,3145,3204,2825,2319,2619,1558,2633,2343,1962,741,1248,2303,2754,3225,2815,3177,2478,2328,1513,2271,2420,2351,873,2821,772,3101,3553,3828,2660,2284,3033,1724,2964,2213,2442,623,2375,2551,1941,3401,3236,3164,2030,1858,1413,2343,2276,3452,735,2479,1950,2657,2027,2316,1944,1720,2694,1587,2731,2328,3484,875,910,2405,2965,3269,2633,3399,2628,2351,1625,2294,2612,2366,1256,438,3150,3405,3844,2811,4156,3075,2451,1964,2344,3063,2597,1095,1341,1100,3018,3355,3046,3002,2422,2532,1664,2458,2328,1970,994,1236,2797,2205,3417,2776,3644,2320,1415,1466,2087,2498,3008,1085,1174,2404,2842,2181,1814,2403,1891,2285,1616,2380,2568,2767,705,2594,1061,2929,3447,3558,2721,2206,2869,1590,2773,2155,2851,900,1595,1383,2816,3304,3029,3022,2347,2470,1463,2294,2187,2119,1232,3415,383,3670,4182,4563,2898,2400,3763,2099,3403,2388,2860,825,2566,1714,1972,3372,3334,2911,1775,1945,1374,2377,2079,3066,939,2880,1179,2778,2467,2793,1933,1483,2955,1650,2879,2174,3455,706,2145,2515,1793,3260,3037,3238,2188,1623,1445,2430,2282,3334,1023,1303,2838,1877,3388,2849,3537,2532,1285,1507,2197,2453,3208,1001,2395,1349,1861,3314,3231,2901,1910,1750,1464,2608,2088,2790,1068,2365,3138,1124,3979,3857,3936,2224,1044,1622,2476,2583,3632,968,2310,2433,1587,2364,2389,2465,1629,1754,1520,2556,2277,3510,707,2512,1960,2693,1981,2128,2038,1913,2771,1554,2626,2369,3489,910,1542,2301,2731,2118,1740,2359,2055,2478,1505,2283,2485,3103,1009,3026,978,2907,2511,2784,1903,1685,3062,1666,2927,2169,3079,817,2592,2379,1786,2595,2562,2649,1590,2118,1408,2332,2231,3502,1078,2873,2244,2896,1407,1702,1509,1270,3247,2004,3172,2751,3865};
static const int Rate_angleDeltaDefault[56]={2002,1803,1890,567,1515,1712,1886,1962,1694,1720,593,1655,1683,1919,1595,1115,1849,1298,1605,1023,2287,1453,1179,1568,2069,1435,1188,1488,2170,939,1635,1377,1728,1244,1564,1854,1088,1959,1365,1563,963,1971,1982,966,1490,1405,1988,1211,1582,1630,1159,2054,1357,1638,922,2025};
int ModeGroup(int mode){return mode==1||mode==10?1:mode==2||mode==11?2:mode==3||mode==8?3:mode>=4&&mode<=7?4:0;}
int PredictionRate(int code,int2 xy){
 int block=xy.y*(int)_PhotoSize.z+xy.x;
 int above=xy.y>0?((int)round(atIndex(_QuantTex,(block-(int)_PhotoSize.z)*SampleCount()).a*255)&15):0;
 int left=xy.x>0?((int)round(atIndex(_QuantTex,(block-1)*SampleCount()).a*255)&15):0;
 int mode=code&15,cost=Rate_intraYDefault[(ModeGroup(above)*5+ModeGroup(left))*13+mode];
 if(mode>=1&&mode<=8)cost+=Rate_angleDeltaDefault[(mode-1)*7+(code>>4)];
 return cost;
}
  float4 optimizedCoefficients(v2f_img i):SV_Target{
   int2 p=min((int2)(i.uv*float2(_PhotoSize.z*16,_PhotoSize.w)),int2(_PhotoSize.z*16-1,_PhotoSize.w-1));
   int2 xy=int2(p.x/16,p.y);if(xy.x+2*xy.y!=_Diagonal)return 0;
   int block=xy.y*(int)_PhotoSize.z+xy.x;int3 selected=(int3)round(_ModeTex.Load(int3(xy,0)).rgb*255);
   int transformed[64],optimized[64];transformCandidate(block,xy,selected.x+16*selected.y,selected.z,transformed);
   OptimizeRdo(transformed,selected.x+16*selected.y,selected.z,xy,optimized);int at=(p.x%16)*4;
   return float4(optimized[at],optimized[at+1],optimized[at+2],optimized[at+3]);
  }
  float4 candidateCost(v2f_img i):SV_Target{
   int2 p=min((int2)(i.uv*float2(_PhotoSize.z*40,_PhotoSize.w)),int2(_PhotoSize.z*40-1,_PhotoSize.w-1));
   int2 xy=int2(p.x/40,p.y);if(xy.x+2*xy.y!=_Diagonal)return 0;
   int candidate=p.x%40,mode=(int)round(_Shortlist.Load(int3(xy.x*2+candidate/20,xy.y,0))[(candidate/5)%4]),txType=candidate%5;
   if(!availableCode(mode,xy))return float4(1e30,0,0,0);
   int block=xy.y*(int)_PhotoSize.z+xy.x,transformed[64],optimized[64];
   transformCandidate(block,xy,mode,txType,transformed);
   return float4(OptimizeRdo(transformed,mode,txType,xy,optimized),0,0,0);
  }
// AV1 8-bit CDEF, derived from pinned AOM cdef_block.c. BSD-2-Clause + PATENTS.
// Fixed primary strength 6, secondary 2, damping 4; chroma filter disabled.
// Runs after prediction/encoding. Filtered pixels never become intra references.
int cdefPixel(int2 p){
 if(any(p<0)||p.x>=(int)_PhotoSize.x||p.y>=(int)_PhotoSize.y)return 30000;
 int block=(p.y/8)*(int)_PhotoSize.z+p.x/8;return rawAt(block,(p.y%8)*8+p.x%8);
}
bool omittedBlock(int block){
 [loop]for(int k=0;k<SampleCount();k++)if(coefficient(block*SampleCount()+k)!=0)return false;return true;
}
float4 cdefDirection(v2f_img input):SV_Target{
 int2 xy=min((int2)(input.uv*_PhotoSize.zw),(int2)_PhotoSize.zw-1);int block=xy.y*(int)_PhotoSize.z+xy.x;
 if(omittedBlock(block))return float4(0,0,0,1);
 int partial[120],cost[8];[loop]for(int k=0;k<120;k++)partial[k]=0;[unroll]for(int k=0;k<8;k++)cost[k]=0;
 static const int divisor[9]={0,840,420,280,210,168,140,120,105};
 [loop]for(int r=0;r<8;r++){[loop]for(int c=0;c<8;c++){
  int x=rawAt(block,r*8+c)-128;
  partial[r+c]+=x;partial[15+r+c/2]+=x;partial[30+r]+=x;partial[45+3+r-c/2]+=x;
  partial[60+7+r-c]+=x;partial[75+3-r/2+c]+=x;partial[90+c]+=x;partial[105+r/2+c]+=x;
 }}
 [unroll]for(int k=0;k<8;k++){cost[2]+=partial[30+k]*partial[30+k];cost[6]+=partial[90+k]*partial[90+k];}
 cost[2]*=105;cost[6]*=105;
 [unroll]for(int k=0;k<7;k++){
  cost[0]+=(partial[k]*partial[k]+partial[14-k]*partial[14-k])*divisor[k+1];
  cost[4]+=(partial[60+k]*partial[60+k]+partial[74-k]*partial[74-k])*divisor[k+1];
 }
 cost[0]+=partial[7]*partial[7]*105;cost[4]+=partial[67]*partial[67]*105;
 [unroll]for(int d=1;d<8;d+=2){
  [unroll]for(int k=0;k<5;k++)cost[d]+=partial[d*15+3+k]*partial[d*15+3+k];cost[d]*=105;
  [unroll]for(int k=0;k<3;k++)cost[d]+=(partial[d*15+k]*partial[d*15+k]+partial[d*15+10-k]*partial[d*15+10-k])*divisor[2*k+2];
 }
 int best=0;[unroll]for(int d=1;d<8;d++)if(cost[d]>cost[best])best=d;
 int variance=(cost[best]-cost[(best+4)&7])>>10;
 int strength=variance==0?0:(6*(4+(variance>>6?min(firstbithigh(variance>>6),12):0))+8)>>4;
 return float4(best,strength,1,255)/255.;
}
static const int2 cdefOffsets[16]={int2(1,-1),int2(2,-2),int2(1,0),int2(2,-1),int2(1,0),int2(2,0),int2(1,0),int2(2,1),int2(1,1),int2(2,2),int2(0,1),int2(1,2),int2(0,1),int2(0,2),int2(0,1),int2(-1,2)};
int cdefConstrain(int difference,int strength){
 if(strength==0)return 0;int v=abs(difference),shift=max(0,4-firstbithigh(strength));return (difference<0?-1:1)*min(v,max(0,strength-(v>>shift)));
}
float4 cdefFilter(v2f_img input):SV_Target{
 int index=indexAt(input.uv),block,plane,n,local;int2 xy;identify(index,block,plane,n,local,xy);
 if(plane!=0||xy.y>=(int)_PhotoSize.w||_ModeTex.Load(int3(xy,0)).b==0)return atIndex(_MainTex,index);
 int2 pixel=xy*8+int2(local%8,local/8),params=(int2)round(_ModeTex.Load(int3(xy,0)).rg*255);
 int dir=params.x,strength=params.y,x=cdefPixel(pixel),sum=0,lo=x,hi=x;
 [unroll]for(int k=0;k<2;k++){
  [unroll]for(int sign=-1;sign<=1;sign+=2){
   int p=cdefPixel(pixel+sign*cdefOffsets[dir*2+k]);sum+=(strength&1?3:(k==0?4:2))*cdefConstrain(p-x,strength);
   if(p!=30000)hi=max(hi,p);lo=min(lo,p);
   [unroll]for(int delta=-2;delta<=2;delta+=4){int s=cdefPixel(pixel+sign*cdefOffsets[((dir+delta)&7)*2+k]);sum+=(k==0?2:1)*cdefConstrain(s-x,2);if(s!=30000)hi=max(hi,s);lo=min(lo,s);}
  }
 }
 int value=x+((8+sum-(sum<0?1:0))>>4);if(strength!=0)value=clamp(value,lo,hi);
 return float4(value/255.,0,0,1);
}

  float4 predictionCost(v2f_img input):SV_Target{
   int2 p=min((int2)(input.uv*float2(_PhotoSize.z*51,_PhotoSize.w)),int2(_PhotoSize.z*51-1,_PhotoSize.w-1)),xy=int2(p.x/51,p.y);
   if(xy.x+2*xy.y!=_Diagonal)return 0;int code=angleCodes[p.x%51];if(!availableCode(code,xy))return float4(1e30,0,0,0);
   int t[64],block=xy.y*(int)_PhotoSize.z+xy.x;transformCandidate(block,xy,code,0,t);
   float distortion=0,rate=PredictionRate(code,xy)/512.;int end=0;
   [loop]for(int k=0;k<64;k++){int ci=Aom_scan[k],rowMajor=(ci&7)*8+(ci>>3),q=QuantAt(t[rowMajor],0,rowMajor);float error=t[rowMajor]-q*StepBase(0,rowMajor);distortion+=error*error;if(q!=0){end=k+1;rate+=3+2*firstbithigh(abs(q)+1);}else rate+=.25;}
   return float4(distortion+_QuantAc*_QuantAc*PredictionRateWeight*(rate+end*.2),0,0,0);
  }
  float4 shortlistModes(v2f_img input):SV_Target{
   int2 p=min((int2)(input.uv*float2(_PhotoSize.z*2,_PhotoSize.w)),int2(_PhotoSize.z*2-1,_PhotoSize.w-1)),xy=int2(p.x/2,p.y);if(xy.x+2*xy.y!=_Diagonal)return 0;
   float costs[8];int choices[8];[unroll]for(int k=0;k<8;k++){costs[k]=3.4e38;choices[k]=48;}
   [loop]for(int k=0;k<51;k++){
    float cost=_ModeCosts.Load(int3(xy.x*51+k,xy.y,0)).r;int slot=8;
    [unroll]for(int j=0;j<8;j++)if(cost<costs[j])slot=min(slot,j);
    if(slot<8){[unroll]for(int j=7;j>0;j--)if(j>slot){costs[j]=costs[j-1];choices[j]=choices[j-1];}costs[slot]=cost;choices[slot]=angleCodes[k];}
   }
   int at=(p.x%2)*4;return float4(choices[at],choices[at+1],choices[at+2],choices[at+3]);
  }


  // Rasterize only a three-block-wide wavefront. The fragment guard selects
  // x+2*y == diagonal; out-of-range scratch texels are never consumed.
  v2f_img vertWave(appdata_img v){
   v2f_img o=vert_img(v);
   float x=(_Diagonal+3*o.uv.x-2*o.uv.y*_PhotoSize.w)/_PhotoSize.z;
   o.uv.x=x;o.pos.x=(2*x-1)*o.pos.w;return o;
  }
  // Four optimized chroma coefficients per fragment; shared by quantization.
  void transformChroma444(int block,int2 xy,int plane,out int t[64]){
   int dc=dcPrediction(block,plane,8,xy),v[8];
   [loop]for(int c=0;c<8;c++){
    [loop]for(int r=0;r<8;r++)v[r]=(sourceValue(xy,plane,r,c)-dc)*4;
    fwd_dct8(v,13,0);[loop]for(int r=0;r<8;r++)t[r*8+c]=(v[r]+1)>>1;
   }
   [loop]for(int r=0;r<8;r++){
    [loop]for(int c=0;c<8;c++)v[c]=t[r*8+c];
    fwd_dct8(v,13,0);[loop]for(int c=0;c<8;c++)t[r*8+c]=QuantAt(v[c],plane,r*8+c);
   }
  }
  float4 optimizedChroma(v2f_img i):SV_Target{
   if(_Chroma444!=0){
    int2 p=min((int2)(i.uv*float2(_PhotoSize.z*32,_PhotoSize.w)),int2(_PhotoSize.z*32-1,_PhotoSize.w-1));
    int2 xy=int2(p.x/32,p.y);if(xy.x+2*xy.y!=_Diagonal)return 0;
    int plane=1+(p.x%32)/16,t[64];transformChroma444(xy.y*(int)_PhotoSize.z+xy.x,xy,plane,t);
    int at=(p.x%16)*4;return float4(t[at],t[at+1],t[at+2],t[at+3]);
   }
   int2 p=min((int2)(i.uv*float2(_PhotoSize.z*16,_PhotoSize.w)),int2(_PhotoSize.z*16-1,_PhotoSize.w-1));
   int2 xy=int2(p.x/16,p.y);int slot=p.x%16;
   if(xy.x+2*xy.y!=_Diagonal||slot>=8)return 0;
   int block=xy.y*(int)_PhotoSize.z+xy.x,plane=1+slot/4;
   int mode=(int)round(_UvModeTex.Load(int3(xy,0)).b*255);
   int alpha=(int)round(_CflTex.Load(int3(xy,0))[plane-1]*255)-16;
   int t[16],q[16];uvTransform(block,xy,plane,mode,alpha,lumaAverage(block),t);OptimizeUv(t,plane,q);
   int at=(slot%4)*4;return float4(q[at],q[at+1],q[at+2],q[at+3]);
  }
  // AV1 8-bit inverse transforms clip intermediate values to signed 16 bits.
  // Smaller stage bounds incorrectly saturate valid high-quantizer input.
  // Save the first inverse-transform dimension, including original rounding.
  // The existing optimized-coefficient buffer is no longer needed at this stage.
  float4 inverseRows(v2f_img i):SV_Target{
   int span=_Chroma444!=0&&_ChromaStage!=0?32:16,perPlane=_Chroma444!=0?16:4;
   int2 p=min((int2)(i.uv*float2(_PhotoSize.z*span,_PhotoSize.w)),int2(_PhotoSize.z*span-1,_PhotoSize.w-1));
   int2 xy=int2(p.x/span,p.y);if(xy.x+2*xy.y!=_Diagonal)return 0;
   int slot=p.x%span,plane=_ChromaStage==0?0:1+slot/perPlane;
   if(_ChromaStage!=0&&slot>=perPlane*2)return 0;
   int n=PlaneSize(plane),local=(plane==0?slot:slot%perPlane)*4,row=local/n;
   int tx=plane==0?(int)round(_ModeTex.Load(int3(xy,0)).b*255):uvType((int)round(_UvModeTex.Load(int3(xy,0)).b*255));
   int block=xy.y*(int)_PhotoSize.z+xy.x,offset=block*SampleCount()+(PlaneOffset(plane)),v[8];
   [loop]for(int c=0;c<n;c++){
    int k=row*n+c;v[c]=clamp(unpackInt(atIndex(_QuantTex,offset+k))*StepBase(plane,k),-32768,32767);
   }
   if(n==8){if(tx==4){[unroll]for(int z=0;z<8;z++)v[z]*=2;}else if(tx==2||tx==3)inv_adst8(v,12,16);else inv_dct8(v,12,16);}
   else {if(tx==2||tx==3)inv_adst4(v,12,16);else inv_dct4(v,12,16);}
   int at=local%n;float4 values=float4(v[at],v[at+1],v[at+2],v[at+3]);
   if(n==8)values=floor((values+1)/2);return clamp(values,-32768,32767);
  }
  float4 cflCosts(v2f_img i):SV_Target{
   int2 p=min((int2)(i.uv*float2(_PhotoSize.z*51,_PhotoSize.w)),int2(_PhotoSize.z*51-1,_PhotoSize.w-1));
   int2 xy=int2(p.x/51,p.y);int slot=p.x%51;
   if(xy.x+2*xy.y!=_Diagonal||slot>=33)return 0;
   int block=xy.y*(int)_PhotoSize.z+xy.x,average=lumaAverage(block),alpha=slot-16;
   return float4(cflTrialCost(block,xy,1,alpha,average),cflTrialCost(block,xy,2,alpha,average),0,0);
  }
  // Decoder input keeps signed coefficients in little-endian RG, with one upload per image.
  Texture2D<float4> _DecodedTex;
  int _DecodeChroma;
  float4 decodedAt(int index){return _DecodedTex.Load(int3(index%(int)_PackedSize.x,index/(int)_PackedSize.x,0));}
  float4 decodedModes(v2f_img i):SV_Target{
   int2 p=min((int2)(i.uv*_PhotoSize.zw),(int2)_PhotoSize.zw-1);
   int block=p.y*(int)_PhotoSize.z+p.x;
   int4 y=(int4)round(decodedAt(block*PacketCount())*255.);
   int4 u=(int4)round(decodedAt(block*PacketCount()+HeaderOffset(1))*255.);
   int4 v=(int4)round(decodedAt(block*PacketCount()+HeaderOffset(2))*255.);
   if(_DecodeChroma!=0)return float4(u.z,v.z,u.w,255)/255.;
   return float4(y.z,y.w>>3,y.w&7,255)/255.;
  }
  float4 decodedQuant(v2f_img i):SV_Target{
   int index=indexAt(i.uv),block,plane,n,local;int2 xy;identify(index,block,plane,n,local,xy);
   if(xy.y>=(int)_PhotoSize.w||xy.x+2*xy.y!=_Diagonal||(_ChromaStage==0?plane!=0:plane==0))discard;
   int planeOffset=HeaderOffset(plane);
   int2 bytes=(int2)round(decodedAt(block*PacketCount()+planeOffset+1+local).rg*255.);
   int coefficient=((bytes.x+(bytes.y<<8))<<16)>>16;
   int dc=dcPrediction(block,plane,n,xy),pred;
   if(plane==0){int2 code=(int2)round(_ModeTex.Load(int3(xy,0)).rg*255.);pred=predictor(block,8,0,code.x+(code.y<<4),local/8,local%8,dc);}
   else if(_Chroma444!=0)pred=dc;
   else{int uv=(int)round(_UvModeTex.Load(int3(xy,0)).b*255.);int alpha=(int)round(_CflTex.Load(int3(xy,0))[plane-1]*255.)-16;pred=uvPredict(block,xy,plane,uv,local/4,local%4,dc,alpha,lumaAverage(block));}
   return packInt(coefficient,pred,0);
  }
  float4 decodedRgb(v2f_img i):SV_Target{
   // AVIF rows are top-first; Unity display textures are bottom-first.
   // Blit UV orientation differs between Editor and VRChat, so convert raster Y explicitly.
   int2 pixel=min((int2)i.pos.xy,(int2)_PhotoSize.xy-1);
   pixel.y=(int)_PhotoSize.y-1-pixel.y;
   int block=(pixel.y/8)*(int)_PhotoSize.z+pixel.x/8;
   float y=rawAt(block,(pixel.y%8)*8+pixel.x%8)/255.;
   int cp=_Chroma444!=0?(pixel.y%8)*8+pixel.x%8:((pixel.y%8)/2)*4+(pixel.x%8)/2;
   float u=(rawAt(block,PlaneOffset(1)+cp)-128)/255.;
   float v=(rawAt(block,PlaneOffset(2)+cp)-128)/255.;
   float3 rgb=saturate(float3(y+1.402*v,y-.344136*u-.714136*v,y+1.772*u));
   #ifndef UNITY_COLORSPACE_GAMMA
    rgb=float3(CodecSrgbToLinear(rgb.r),CodecSrgbToLinear(rgb.g),CodecSrgbToLinear(rgb.b));
   #endif
   return float4(rgb,1);
  }
  ENDCG
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment selectPrediction
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment forwardCoefficient
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment reconstructPixel
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment packContexts
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment selectCfl
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment optimizedCoefficients
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment candidateCost
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment chromaCandidate
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment selectChroma
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment cdefDirection
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment cdefFilter
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment predictionCost
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment shortlistModes
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment prepareSourceYuv
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment optimizedChroma
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment inverseRows
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment cflCosts
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment decodedModes
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vertWave
   #pragma fragment decodedQuant
   ENDCG }
  Pass { CGPROGRAM
   #pragma target 5.0
   #pragma vertex vert_img
   #pragma fragment decodedRgb
   ENDCG }
 }
}
