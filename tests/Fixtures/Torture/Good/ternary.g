// EXPECT OK
kernel { entry func Main() {
  let int a = 3;
  let int b = 7;
  // basic selection
  let int m = a > b ? a : b;
  // nested / right-associative chain
  let int band = a < 2 ? 0 : (a < 5 ? 1 : 2);
  // numeric widening: int64 arm + int arm unify to int64
  let int64 w = a > 0 ? (m as int64) : b;
  // used inside a larger expression
  let int s = (a > b ? a : b) + (a < b ? a : b);
  if (m == 7) { } else { }
} }
