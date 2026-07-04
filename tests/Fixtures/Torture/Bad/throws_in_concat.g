// EXPECT G021
// A throwing call inside a string concatenation is still nested — rejected.
throws int func risky(int x) { if (x < 0) { throw; } return x; }
int func sink(int a) { return a; }
kernel { entry func Main() {
  let String s = "n=" + risky(1);
} }
