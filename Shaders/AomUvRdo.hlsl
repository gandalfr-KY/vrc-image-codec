// AV1 default Q-context 2, 4x4 chroma. AOM BSD-2-Clause + PATENTS.
static const int UvSkip[6]={1532,99,528,496,181,1129};
static const int UvEob[5]={797,2019,1393,1342,938};
static const int UvExtra[6]={361,701,386,665,472,554};
static const int UvLast[12]={136,1584,2197,22,2725,4004,23,2775,3632,31,2529,3567};
static const int UvBase[104]={819,790,1413,1270,178,1187,3326,4340,472,688,2059,3023,783,654,1386,1798,1146,819,1127,1053,1502,1088,1221,592,119,1444,3828,4620,401,744,2324,3345,708,640,1479,2066,1017,762,1158,1255,1295,956,1138,800,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,1024,178,1186,3349,4301,381,767,2389,3433,662,683,1487,2061,869,782,1352,1248,938,845,1122,1261};
static const int UvBr[84]={468,1129,1675,1408,535,1086,1598,1307,770,1082,1429,963,1016,1192,1384,687,1195,1312,1465,520,1423,1418,1535,399,1631,1712,1692,274,306,1248,1902,1881,317,1185,1873,1976,471,1041,1619,1597,660,1045,1442,1169,867,1110,1402,846,990,1202,1448,675,1293,1281,1399,513,226,1297,2199,2374,235,1274,2105,2457,362,1086,1813,1950,561,954,1510,1519,754,1074,1340,1045,861,1006,1235,1041,1071,1154,1284,715};
static const int UvScan[16]={0,1,4,8,5,2,3,6,9,12,13,10,7,11,14,15};
static const int UvOffsets[16]={0,1,6,6,1,6,6,21,6,6,21,21,6,21,21,21};
int UvLevel(int q[16],int r,int c,int cap){return r<4&&c<4?min(abs(q[r*4+c]),cap):0;}
int UvRate(int q[16],int end){
 int rate=UvSkip[end==0?1:0];if(end==0)return rate;
 int token=firstbithigh((uint)max(1,end-1))+1;if(end==1)token=0;
 rate+=UvEob[token];
 if(token>1){int shift=token-2;rate+=UvExtra[shift*2+(((end-(1<<(token-1))-1)>>shift)&1)]+512*shift;}
 [loop]for(int k=end-1;k>=0;k--){
  int ci=UvScan[k],r=ci/4,c=ci%4,a=abs(q[ci]);
  if(k==end-1){int ctx=k==0?0:k<=2?1:k<=4?2:3;rate+=UvLast[ctx*3+min(a-1,2)];}
  else {
   int mag=UvLevel(q,r,c+1,3)+UvLevel(q,r+1,c,3)+UvLevel(q,r+1,c+1,3)+UvLevel(q,r,c+2,3)+UvLevel(q,r+2,c,3);
   int ctx=ci==0?0:min((mag+1)>>1,4)+UvOffsets[ci];rate+=UvBase[ctx*4+min(a,3)];
  }
  if(a>2){
   int mag=UvLevel(q,r,c+1,15)+UvLevel(q,r+1,c,15)+UvLevel(q,r+1,c+1,15);
   int ctx=min((mag+1)>>1,6)+(ci==0?0:r<2&&c<2?7:14),left=a-3;
   [unroll]for(int b=0;b<4;b++){int value=min(left,3);rate+=UvBr[ctx*4+value];left-=value;if(value<3)break;}
  }
  if(a!=0)rate+=512;
  if(a>=15)rate+=512*(2*firstbithigh((uint)(a-14))+1);
 }
 return rate;
}
float OptimizeUv(int t[16],int plane,out int q[16]){
 int end=0;float distortion=0,lambda=CoefficientRateWeight(plane);
 [unroll]for(int k=0;k<16;k++){q[k]=QuantAt(t[k],plane,k);float d=t[k]-q[k]*StepBase(plane,k);distortion+=d*d;}
 [unroll]for(int k=0;k<16;k++)if(q[UvScan[k]]!=0)end=k+1;
 int rate=UvRate(q,end);
 [loop]for(int k=end-1;k>=0;k--){
  int ci=UvScan[k],original=q[ci];if(original==0)continue;
  float d=t[ci]-original*StepBase(plane,ci);q[ci]+=original<0?1:-1;
  float dn=t[ci]-q[ci]*StepBase(plane,ci);int newEnd=end;
  [loop]while(newEnd>0&&q[UvScan[newEnd-1]]==0)newEnd--;
  int newRate=UvRate(q,newEnd);float change=dn*dn-d*d;
  if(change+lambda*(newRate-rate)<0){distortion+=change;rate=newRate;end=newEnd;}else q[ci]=original;
 }
 float zero=0;[unroll]for(int k=0;k<16;k++)zero+=(float)t[k]*t[k];
 if(zero+lambda*UvSkip[1]<distortion+lambda*rate){[unroll]for(int k=0;k<16;k++)q[k]=0;return zero+ModeRateWeight()*UvSkip[1];}
 return distortion+ModeRateWeight()*rate;
}
