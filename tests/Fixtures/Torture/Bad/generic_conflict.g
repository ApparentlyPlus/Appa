// EXPECT G009
// The same type parameter inferred to two different types from different arguments.
T func same[T](T a, T b) { return a; }
kernel { entry func Main() { let int64 z = same(3, (4 as int64)); } }
