// AV1 integer transforms adapted from tinyavif (BSD-2-Clause / AOMedia Patent License 1.0).
// Copyright (c) 2024-2025, The tinyavif contributors. All rights reserved.

static const int cospi12[64] = {4096,4095,4091,4085,4076,4065,4052,4036,4017,3996,3973,3948,3920,3889,3857,3822,3784,3745,3703,3659,3612,3564,3513,3461,3406,3349,3290,3229,3166,3102,3035,2967,2896,2824,2751,2675,2598,2520,2440,2359,2276,2191,2106,2019,1931,1842,1751,1660,1567,1474,1380,1285,1189,1092,995,897,799,700,601,501,401,301,201,101};

static const int cospi13[64] = {8192,8190,8182,8170,8153,8130,8103,8071,8035,7993,7946,7895,7839,7779,7713,7643,7568,7489,7405,7317,7225,7128,7027,6921,6811,6698,6580,6458,6333,6203,6070,5933,5793,5649,5501,5351,5197,5040,4880,4717,4551,4383,4212,4038,3862,3683,3503,3320,3135,2948,2760,2570,2378,2185,1990,1795,1598,1401,1202,1003,803,603,402,201};

int half_btf(int w0,int in0,int w1,int in1,int bits) {return (w0*in0+w1*in1+(1<<(bits-1)))>>bits;}
int clamp_value(int value,int bits) {return clamp(value,-(1<<(bits-1)),(1<<(bits-1))-1);}

void fwd_dct4(inout int arr[8],int cos_bit,int range_bits) {

  int stage1[4] = {
    arr[0] + arr[3],
    arr[1] + arr[2],
    -arr[2] + arr[1],
    -arr[3] + arr[0]
  };

  int stage2[4] = {
    half_btf(cospi13[32], stage1[0], cospi13[32], stage1[1], cos_bit),
    half_btf(-cospi13[32], stage1[1], cospi13[32], stage1[0], cos_bit),
    half_btf(cospi13[48], stage1[2], cospi13[16], stage1[3], cos_bit),
    half_btf(cospi13[48], stage1[3], -cospi13[16], stage1[2], cos_bit)
  };

  int stage3[4] = {
    stage2[0],
    stage2[2],
    stage2[1],
    stage2[3]
  };

  for(int k=0;k<4;k++) arr[k]=stage3[k];

}

void fwd_dct8(inout int arr[8],int cos_bit,int range_bits) {

  int stage1[8] = {
    arr[0] + arr[7],
    arr[1] + arr[6],
    arr[2] + arr[5],
    arr[3] + arr[4],
    -arr[4] + arr[3],
    -arr[5] + arr[2],
    -arr[6] + arr[1],
    -arr[7] + arr[0],
  };

  int stage2[8] = {
    stage1[0] + stage1[3],
    stage1[1] + stage1[2],
    -stage1[2] + stage1[1],
    -stage1[3] + stage1[0],
    stage1[4],
    half_btf(-cospi13[32], stage1[5], cospi13[32], stage1[6], cos_bit),
    half_btf(cospi13[32], stage1[6], cospi13[32], stage1[5], cos_bit),
    stage1[7],
  };

  int stage3[8] = {
    half_btf(cospi13[32], stage2[0], cospi13[32], stage2[1], cos_bit),
    half_btf(-cospi13[32], stage2[1], cospi13[32], stage2[0], cos_bit),
    half_btf(cospi13[48], stage2[2], cospi13[16], stage2[3], cos_bit),
    half_btf(cospi13[48], stage2[3], -cospi13[16], stage2[2], cos_bit),
    stage2[4] + stage2[5],
    -stage2[5] + stage2[4],
    -stage2[6] + stage2[7],
    stage2[7] + stage2[6],
  };

  int stage4[8] = {
    stage3[0],
    stage3[1],
    stage3[2],
    stage3[3],
    half_btf(cospi13[56], stage3[4], cospi13[8], stage3[7], cos_bit),
    half_btf(cospi13[24], stage3[5], cospi13[40], stage3[6], cos_bit),
    half_btf(cospi13[24], stage3[6], -cospi13[40], stage3[5], cos_bit),
    half_btf(cospi13[56], stage3[7], -cospi13[8], stage3[4], cos_bit),
  };

  int stage5[8] = {
    stage4[0],
    stage4[4],
    stage4[2],
    stage4[6],
    stage4[1],
    stage4[5],
    stage4[3],
    stage4[7],
  };

  for(int k=0;k<8;k++) arr[k]=stage5[k];

}

void inv_dct4(inout int arr[8],int cos_bit,int range_bits) {

  int stage1[4] = {
    arr[0],
    arr[2],
    arr[1],
    arr[3]
  };

  int stage2[4] = {
    half_btf(cospi12[32], stage1[0], cospi12[32], stage1[1], cos_bit),
    half_btf(cospi12[32], stage1[0], -cospi12[32], stage1[1], cos_bit),
    half_btf(cospi12[48], stage1[2], -cospi12[16], stage1[3], cos_bit),
    half_btf(cospi12[16], stage1[2], cospi12[48], stage1[3], cos_bit)
  };

  int stage3[4] = {
    clamp_value(stage2[0] + stage2[3], range_bits),
    clamp_value(stage2[1] + stage2[2], range_bits),
    clamp_value(stage2[1] - stage2[2], range_bits),
    clamp_value(stage2[0] - stage2[3], range_bits)
  };

  for(int k=0;k<4;k++) arr[k]=stage3[k];

}

void inv_dct8(inout int arr[8],int cos_bit,int range_bits) {

  int stage1[8] = {
    arr[0],
    arr[4],
    arr[2],
    arr[6],
    arr[1],
    arr[5],
    arr[3],
    arr[7],
  };

  int stage2[8] = {
    stage1[0],
    stage1[1],
    stage1[2],
    stage1[3],
    half_btf(cospi12[56], stage1[4], -cospi12[8], stage1[7], cos_bit),
    half_btf(cospi12[24], stage1[5], -cospi12[40], stage1[6], cos_bit),
    half_btf(cospi12[40], stage1[5], cospi12[24], stage1[6], cos_bit),
    half_btf(cospi12[8], stage1[4], cospi12[56], stage1[7], cos_bit)
  };

  int stage3[8] = {
    half_btf(cospi12[32], stage2[0], cospi12[32], stage2[1], cos_bit),
    half_btf(cospi12[32], stage2[0], -cospi12[32], stage2[1], cos_bit),
    half_btf(cospi12[48], stage2[2], -cospi12[16], stage2[3], cos_bit),
    half_btf(cospi12[16], stage2[2], cospi12[48], stage2[3], cos_bit),
    clamp_value(stage2[4] + stage2[5], range_bits),
    clamp_value(stage2[4] - stage2[5], range_bits),
    clamp_value(-stage2[6] + stage2[7], range_bits),
    clamp_value(stage2[6] + stage2[7], range_bits),
  };
  
  int stage4[8] = {
    clamp_value(stage3[0] + stage3[3], range_bits),
    clamp_value(stage3[1] + stage3[2], range_bits),
    clamp_value(stage3[1] - stage3[2], range_bits),
    clamp_value(stage3[0] - stage3[3], range_bits),
    stage3[4],
    half_btf(-cospi12[32], stage3[5], cospi12[32], stage3[6], cos_bit),
    half_btf(cospi12[32], stage3[5], cospi12[32], stage3[6], cos_bit),
    stage3[7],
  };

  int stage5[8] = {
    clamp_value(stage4[0] + stage4[7], range_bits),
    clamp_value(stage4[1] + stage4[6], range_bits),
    clamp_value(stage4[2] + stage4[5], range_bits),
    clamp_value(stage4[3] + stage4[4], range_bits),
    clamp_value(stage4[3] - stage4[4], range_bits),
    clamp_value(stage4[2] - stage4[5], range_bits),
    clamp_value(stage4[1] - stage4[6], range_bits),
    clamp_value(stage4[0] - stage4[7], range_bits),
  };

  for(int k=0;k<8;k++) arr[k]=stage5[k];

}