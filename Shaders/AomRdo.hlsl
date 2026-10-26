Texture2D<float4> _AomCosts;
int AomCost(int i){return (int)_AomCosts.Load(int3(i,0,0)).r;}
int AomEobCost(int i){return (int)_AomCosts.Load(int3(i,1,0)).r;}
static const int Aom_scan[64]={0,8,1,2,9,16,24,17,10,3,4,11,18,25,32,40,33,26,19,12,5,6,13,20,27,34,41,48,56,49,42,35,28,21,14,7,15,22,29,36,43,50,57,58,51,44,37,30,23,31,38,45,52,59,60,53,46,39,47,54,61,62,55,63};
static const int Aom_nzOffsets[64]={0,1,6,6,21,21,21,21,1,6,6,21,21,21,21,21,6,6,21,21,21,21,21,21,6,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21};
// Profile tuning for prediction screening and chroma RDO: rate weight is quantizer-step squared * 0.07.
// Table-based rate costs additionally use 1/512-bit units. This is not a quality percentage or AV1 requirement.
// The luma coefficient trellis below uses a separate libaom-derived multiplier.
static const float PredictionRateWeight = 0.07;
int _QuantUvDc,_QuantUvAc;
int StepBase(int plane,int local){return plane==0?(local==0?_QuantDc:_QuantAc):(local==0?_QuantUvDc:_QuantUvAc);}

int QuantAt(int value,int plane,int local){int q=StepBase(plane,local);if(abs(value)*32<q*16)return 0;int a=(int)floor((min(abs(value)+q/2,32767)*32.*floor(65536./q))/2097152.);return value<0?-a:a;}

// Fixed still-image coefficient RDO. AV1 frozen native costs; float decisions.
// Unlike the prior IQ-sharpness experiment, single coefficients may become zero.
int RdoContext(int ci,int levels[144]){
 if(ci==0)return 0;int p=ci+((ci>>3)<<2);
 int mag=min(levels[p+12],3)+min(levels[p+1],3)+min(levels[p+13],3)+min(levels[p+24],3)+min(levels[p+2],3);
 return min((mag+1)>>1,4)+Aom_nzOffsets[ci];
}
int RdoCoeffCost(int ci,int si,int end,int q,int levels[144]){
 int a=abs(q),p=ci+((ci>>3)<<2);bool last=si==end-1;
 int ctx=last?(si==0?0:si<=8?1:si<=16?2:3):RdoContext(ci,levels);
 int cost=last?AomCost(26+ctx*3+min(a,3)-1):AomCost(38+ctx*8+min(a,3));
 if(a==0)return cost;
 cost+=ci==0?AomCost(392+(q<0?1:0)):512;
 if(a>2){int mag=last?0:min((levels[p+1]+levels[p+12]+levels[p+13]+1)>>1,6);
  int br=mag+(ci==0?0:(ci>>3)<2&&(ci&7)<2?7:14);
  cost+=AomCost(398+br*26+min(a-3,12));
  if(a>=15)cost+=512*(2*firstbithigh((uint)(a-14))+1);
 }return cost;
}
int RdoEobCost(int eob){
 if(eob==0)return AomCost(1);
 int token=1,start=1,bits=0;
 if(eob>=2){token=2;start=2;}
 if(eob>=3){token=3;start=3;bits=1;int limit=4;[loop]while(eob>limit){token++;start=limit+1;bits++;limit<<=1;}}
 int cost=AomCost(0)+AomEobCost(token-1);
 if(bits>0)cost+=AomCost(374+(token-3)*2+(((eob-start)>>(bits-1))&1))+512*(bits-1);
 return cost;
}
int LumaQuant(int value,int local,int tx){if(tx!=4)return QuantAt(value,0,local);int q=StepBase(0,local);if(abs(value)<(q+1)/2)return 0;int a=(int)floor((min(abs(value)+q/2,32767)*floor(65536./q))/65536.);return value<0?-a:a;}
int PredictionRate(int code,int2 xy);
// Copyright (c) 2021, Alliance for Open Media. All rights reserved.
// BSD-2-Clause + AOM Patent License 1.0, see LICENSE and PATENTS.
// Port of txb_rdopt.c: av1_optimize_txb, update_coeff_general/eob/simple/skip.
// Fixed 8x8, 8-bit, intra, 2D transform class, sharpness=0, no QM.
// Float RD decisions are intentional. Costs use frozen native CDFs, not a
// claim of identical decisions to the frame-adaptive libaom encoder.
float OptimizeRdo(int transformed[64],int mode,int txType,int2 xy,out int optimized[64]){
 int q[64],t[64],levels[144],nz[3];int end=0;
 [loop]for(int k=0;k<144;k++)levels[k]=0;
 [loop]for(int k=0;k<64;k++){
  int rm=(k&7)*8+(k>>3);t[k]=transformed[rm];q[k]=LumaQuant(t[k],rm,txType);
  levels[k+((k>>3)<<2)]=min(abs(q[k]),127);
 }
 [loop]for(int k=0;k<64;k++)if(q[Aom_scan[k]]!=0)end=k+1;
 // rd.c, KF_UPDATE: q is DC quant, not the public quality slider.
 // trellis: round(x->rdmult * 8 * 17 / 32); RDCOST / 128 / 512.
 float rdBase=floor((float)_QuantDc*_QuantDc*(3.3+.0015*_QuantDc));
 float lambda=floor((rdBase*8*17+16)/32)/65536.;
 if(end>0){
  int accRate=RdoEobCost(end)-AomCost(0),si=end-1,ci=Aom_scan[si],value=q[ci];
  float accDist=0,step=StepBase(0,(ci&7)*8+(ci>>3));
  int rate=RdoCoeffCost(ci,si,end,value,levels);
  float d=t[ci]-value*step,dist=d*d-(float)t[ci]*t[ci];
  // Last coefficient never becomes zero in this initial general step.
  if(abs(value)>=2){
   int low=value+(value<0?1:-1),lowRate=RdoCoeffCost(ci,si,end,low,levels);
   float dl=t[ci]-low*step,lowDist=dl*dl-(float)t[ci]*t[ci];
   if(lowDist+lambda*lowRate<dist+lambda*rate){q[ci]=low;rate=lowRate;dist=lowDist;levels[ci+((ci>>3)<<2)]=min(abs(low),127);}
  }
  accRate+=rate;accDist+=dist;nz[0]=ci;int nzCount=1;si--;
  // Native keeps at most two nonzero suffix coefficients before switching
  // to the simple inner-coefficient path; no whole-block repeated scan.
  [loop]while(si>=0&&nzCount<=2){
   ci=Aom_scan[si];value=q[ci];
   if(value==0){accRate+=RdoCoeffCost(ci,si,end,0,levels);si--;continue;}
   step=StepBase(0,(ci&7)*8+(ci>>3));d=t[ci]-value*step;
   dist=d*d-(float)t[ci]*t[ci];rate=RdoCoeffCost(ci,si,end,value,levels);
   int low=value+(value<0?1:-1),lowRate=RdoCoeffCost(ci,si,end,low,levels);
   float dl=t[ci]-low*step,lowDist=dl*dl-(float)t[ci]*t[ci];
   bool lower=lowDist+lambda*lowRate<dist+lambda*rate;
   int keptRate=lower?lowRate:rate;float keptDist=lower?lowDist:dist;
   int newEnd=si+1,newRate=RdoEobCost(newEnd)-AomCost(0)+RdoCoeffCost(ci,si,newEnd,value,levels);
   float newDist=dist;bool lowerTail=false;
   if(abs(low)>0){
    int tailLowRate=RdoEobCost(newEnd)-AomCost(0)+RdoCoeffCost(ci,si,newEnd,low,levels);
    if(lowDist+lambda*tailLowRate<newDist+lambda*newRate){newRate=tailLowRate;newDist=lowDist;lowerTail=true;}
   }
   if(newDist+lambda*newRate<accDist+keptDist+lambda*(accRate+keptRate)){
    [unroll]for(int k=0;k<3;k++)if(k<nzCount){int old=nz[k];q[old]=0;levels[old+((old>>3)<<2)]=0;}
    end=newEnd;nzCount=0;accRate=newRate;accDist=newDist;lower=lowerTail;
   }else{accRate+=keptRate;accDist+=keptDist;}
   if(lower){q[ci]=low;levels[ci+((ci>>3)<<2)]=min(abs(low),127);}
   if(q[ci]!=0){nz[nzCount]=ci;nzCount++;}si--;
  }
  if(si<0&&nzCount<=2&&lambda*AomCost(1)<accDist+lambda*(accRate+AomCost(0))){
   [loop]for(int k=0;k<64;k++)q[k]=0;end=0;
  }
  [loop]for(;si>=0;si--){
   ci=Aom_scan[si];value=q[ci];if(value==0)continue;
   step=StepBase(0,(ci&7)*8+(ci>>3));
   // Native simple path avoids reducing coefficients already below source.
   if(si>0&&abs(value*step)<abs(t[ci]))continue;
   int low=value+(value<0?1:-1);d=t[ci]-value*step;float dl=t[ci]-low*step;
   if(dl*dl+lambda*RdoCoeffCost(ci,si,end,low,levels)<d*d+lambda*RdoCoeffCost(ci,si,end,value,levels)){
    q[ci]=low;levels[ci+((ci>>3)<<2)]=min(abs(low),127);
   }
  }
 }
 float distortion=0;int rate=RdoEobCost(end);
 [loop]for(int k=0;k<64;k++){
  float d=t[k]-q[k]*StepBase(0,(k&7)*8+(k>>3));distortion+=d*d;
  optimized[(k&7)*8+(k>>3)]=q[k];
 }
 [loop]for(int k=0;k<end;k++){int ci=Aom_scan[k];rate+=RdoCoeffCost(ci,k,end,q[ci],levels);}
 return distortion+lambda*(rate+PredictionRate(mode,xy)+(end>0?(txType==0?512:1536):0));
}
