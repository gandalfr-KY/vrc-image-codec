// AOM 3.14.1 scalar ADST8, BSD-2-Clause and PATENTS.
void fwd_adst8(inout int arr[8],int cos_bit,int range_bits){
int output[8],step[8];
output[0] = arr[0];
output[1] = -arr[7];
output[2] = -arr[3];
output[3] = arr[4];
output[4] = -arr[1];
output[5] = arr[6];
output[6] = arr[2];
output[7] = -arr[5];
step[0] = output[0];
step[1] = output[1];
step[2] = half_btf(cospi13[32], output[2], cospi13[32], output[3], cos_bit);
step[3] = half_btf(cospi13[32], output[2], -cospi13[32], output[3], cos_bit);
step[4] = output[4];
step[5] = output[5];
step[6] = half_btf(cospi13[32], output[6], cospi13[32], output[7], cos_bit);
step[7] = half_btf(cospi13[32], output[6], -cospi13[32], output[7], cos_bit);
output[0] = step[0] + step[2];
output[1] = step[1] + step[3];
output[2] = step[0] - step[2];
output[3] = step[1] - step[3];
output[4] = step[4] + step[6];
output[5] = step[5] + step[7];
output[6] = step[4] - step[6];
output[7] = step[5] - step[7];
step[0] = output[0];
step[1] = output[1];
step[2] = output[2];
step[3] = output[3];
step[4] = half_btf(cospi13[16], output[4], cospi13[48], output[5], cos_bit);
step[5] = half_btf(cospi13[48], output[4], -cospi13[16], output[5], cos_bit);
step[6] = half_btf(-cospi13[48], output[6], cospi13[16], output[7], cos_bit);
step[7] = half_btf(cospi13[16], output[6], cospi13[48], output[7], cos_bit);
output[0] = step[0] + step[4];
output[1] = step[1] + step[5];
output[2] = step[2] + step[6];
output[3] = step[3] + step[7];
output[4] = step[0] - step[4];
output[5] = step[1] - step[5];
output[6] = step[2] - step[6];
output[7] = step[3] - step[7];
step[0] = half_btf(cospi13[4], output[0], cospi13[60], output[1], cos_bit);
step[1] = half_btf(cospi13[60], output[0], -cospi13[4], output[1], cos_bit);
step[2] = half_btf(cospi13[20], output[2], cospi13[44], output[3], cos_bit);
step[3] = half_btf(cospi13[44], output[2], -cospi13[20], output[3], cos_bit);
step[4] = half_btf(cospi13[36], output[4], cospi13[28], output[5], cos_bit);
step[5] = half_btf(cospi13[28], output[4], -cospi13[36], output[5], cos_bit);
step[6] = half_btf(cospi13[52], output[6], cospi13[12], output[7], cos_bit);
step[7] = half_btf(cospi13[12], output[6], -cospi13[52], output[7], cos_bit);
output[0] = step[1];
output[1] = step[6];
output[2] = step[3];
output[3] = step[4];
output[4] = step[5];
output[5] = step[2];
output[6] = step[7];
output[7] = step[0];
[unroll]for(int k=0;k<8;k++)arr[k]=output[k];
}
void inv_adst8(inout int arr[8],int cos_bit,int range_bits){
int output[8],step[8];
output[0] = arr[7];
output[1] = arr[0];
output[2] = arr[5];
output[3] = arr[2];
output[4] = arr[3];
output[5] = arr[4];
output[6] = arr[1];
output[7] = arr[6];
step[0] = half_btf(cospi12[4], output[0], cospi12[60], output[1], cos_bit);
step[1] = half_btf(cospi12[60], output[0], -cospi12[4], output[1], cos_bit);
step[2] = half_btf(cospi12[20], output[2], cospi12[44], output[3], cos_bit);
step[3] = half_btf(cospi12[44], output[2], -cospi12[20], output[3], cos_bit);
step[4] = half_btf(cospi12[36], output[4], cospi12[28], output[5], cos_bit);
step[5] = half_btf(cospi12[28], output[4], -cospi12[36], output[5], cos_bit);
step[6] = half_btf(cospi12[52], output[6], cospi12[12], output[7], cos_bit);
step[7] = half_btf(cospi12[12], output[6], -cospi12[52], output[7], cos_bit);
output[0] = clamp_value(step[0] + step[4], range_bits);
output[1] = clamp_value(step[1] + step[5], range_bits);
output[2] = clamp_value(step[2] + step[6], range_bits);
output[3] = clamp_value(step[3] + step[7], range_bits);
output[4] = clamp_value(step[0] - step[4], range_bits);
output[5] = clamp_value(step[1] - step[5], range_bits);
output[6] = clamp_value(step[2] - step[6], range_bits);
output[7] = clamp_value(step[3] - step[7], range_bits);
step[0] = output[0];
step[1] = output[1];
step[2] = output[2];
step[3] = output[3];
step[4] = half_btf(cospi12[16], output[4], cospi12[48], output[5], cos_bit);
step[5] = half_btf(cospi12[48], output[4], -cospi12[16], output[5], cos_bit);
step[6] = half_btf(-cospi12[48], output[6], cospi12[16], output[7], cos_bit);
step[7] = half_btf(cospi12[16], output[6], cospi12[48], output[7], cos_bit);
output[0] = clamp_value(step[0] + step[2], range_bits);
output[1] = clamp_value(step[1] + step[3], range_bits);
output[2] = clamp_value(step[0] - step[2], range_bits);
output[3] = clamp_value(step[1] - step[3], range_bits);
output[4] = clamp_value(step[4] + step[6], range_bits);
output[5] = clamp_value(step[5] + step[7], range_bits);
output[6] = clamp_value(step[4] - step[6], range_bits);
output[7] = clamp_value(step[5] - step[7], range_bits);
step[0] = output[0];
step[1] = output[1];
step[2] = half_btf(cospi12[32], output[2], cospi12[32], output[3], cos_bit);
step[3] = half_btf(cospi12[32], output[2], -cospi12[32], output[3], cos_bit);
step[4] = output[4];
step[5] = output[5];
step[6] = half_btf(cospi12[32], output[6], cospi12[32], output[7], cos_bit);
step[7] = half_btf(cospi12[32], output[6], -cospi12[32], output[7], cos_bit);
output[0] = step[0];
output[1] = -step[4];
output[2] = step[6];
output[3] = -step[2];
output[4] = step[3];
output[5] = -step[7];
output[6] = step[5];
output[7] = -step[1];
[unroll]for(int k=0;k<8;k++)arr[k]=output[k];
}
int adst_round(int value,int bits){return (value+(1<<(bits-1)))>>bits;}
void fwd_adst4(inout int arr[8],int cos_bit,int range_bits){
  int bit = cos_bit;
  
  int x0, x1, x2, x3;
  int s0, s1, s2, s3, s4, s5, s6, s7;

  x0 = arr[0];
  x1 = arr[1];
  x2 = arr[2];
  x3 = arr[3];

  if ((x0 | x1 | x2 | x3)==0) {
    arr[0] = arr[1] = arr[2] = arr[3] = 0;
    return;
  }

  s0 = (2642 * x0);
  s1 = (7606 * x0);
  s2 = (4964 * x1);
  s3 = (2642 * x1);
  s4 = (6689 * x2);
  s5 = (7606 * x3);
  s6 = (4964 * x3);
  s7 = (x0 + x1);

  s7 = (s7 - x3);

  x0 = (s0 + s2);
  x1 = (6689 * s7);
  x2 = (s1 - s3);
  x3 = (s4);

  x0 = (x0 + s5);
  x2 = (x2 + s6);

  s0 = (x0 + x3);
  s1 = (x1);
  s2 = (x2 - x3);
  s3 = (x2 - x0);

  s3 = (s3 + x3);

  arr[0] = adst_round(s0, bit);
  arr[1] = adst_round(s1, bit);
  arr[2] = adst_round(s2, bit);
  arr[3] = adst_round(s3, bit);
  
}
void inv_adst4(inout int arr[8],int cos_bit,int range_bits){
  int bit = cos_bit;
  
  int s0, s1, s2, s3, s4, s5, s6, s7;

  int x0 = arr[0];
  int x1 = arr[1];
  int x2 = arr[2];
  int x3 = arr[3];

  if ((x0 | x1 | x2 | x3)==0) {
    arr[0] = arr[1] = arr[2] = arr[3] = 0;
    return;
  }

  s0 = (1321 * x0);
  s1 = (2482 * x0);
  s2 = (3344 * x1);
  s3 = (3803 * x2);
  s4 = (1321 * x2);
  s5 = (2482 * x3);
  s6 = (3803 * x3);

  s7 = ((x0 - x2) + x3);

  s0 = (s0 + s3);
  s1 = (s1 - s4);
  s3 = (s2);
  s2 = (3344 * s7);

  s0 = (s0 + s5);
  s1 = (s1 - s6);

  x0 = (s0 + s3);
  x1 = (s1 + s3);
  x2 = (s2);
  x3 = (s0 + s1);

  x3 = (x3 - s3);

  arr[0] = adst_round(x0, bit);
  arr[1] = adst_round(x1, bit);
  arr[2] = adst_round(x2, bit);
  arr[3] = adst_round(x3, bit);
}
