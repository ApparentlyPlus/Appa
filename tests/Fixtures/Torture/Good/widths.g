// EXPECT OK
// Width-explicit primitive names + alias folding (long ≡ int64, size_t ≡ usize) +
// a throws return that exercises Result_<canonical> naming + an unsafe pointer local.
throws int64 func parse(int x) {
  if (x < 0) { throw; }
  return (x as int64);
}

kernel { entry func Main() {
  let int    a = 1;
  let int64  b = 2;          // 64-bit signed
  let uint   d = 3u;
  let uint64 e = 4u;
  let short  f = (5 as short);
  let ushort g = (6 as ushort);
  let byte   h = (7 as byte);
  let sbyte  i = (8 as sbyte);
  let usize  n = (9 as usize);

  // widening across the width names is implicit; the wide arithmetic stays 64-bit
  let int64 sum = (a as int64) + b + (d as int64) + (e as int64)
                + (f as int64) + (g as int64) + (h as int64) + (i as int64)
                + (n as int64);

  try {
    let int64 parsed = parse(sum as int);   // Result_int64 round-trips
    sum = sum + parsed;
  } catch { }

  // pointer locals with a width keyword work like any primitive
  unsafe {
    let byte* p = &h;
    let byte first = *p;
    sum = sum + (first as int64);
  }
  if (sum > 0) { } else { }
} }
