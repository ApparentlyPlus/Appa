// EXPECT OK
// Generic free functions with argument-type inference, monomorphized per instance.
T func max[T](T a, T b) { if (a > b) { return a; } return b; }
T func identity[T](T x) { return x; }
T func firstOf[T](T a, T b) { return a; }

kernel { entry func Main() {
  let int    a = max(3, 7);                          // T=int
  let int64  b = max((10 as int64), (4 as int64));   // T=int64 (distinct instance)
  let int    c = max(5, 9);                          // reuses the T=int instance
  let int    d = identity(42);
  let int    e = firstOf(1, 2);
  if (a + c + d + e == 7 + 9 + 42 + 1) { } else { }
} }
