// EXPECT G004
// Narrowing a 64-bit value into a 32-bit one needs an explicit `as`.
kernel { entry func Main() {
  let int64 a = 5;
  let int   b = a;
} }
