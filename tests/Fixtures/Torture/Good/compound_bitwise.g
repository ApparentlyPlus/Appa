// EXPECT OK
kernel { entry func Main() {
  let int f = 0;
  f |= 8; f &= 12; f ^= 4; f <<= 2; f >>= 1;
  let uint u = 1u;
  u |= 2u; u <<= 3;
} }
