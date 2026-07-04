// EXPECT G021
// A throwing call as a ternary arm is nested inside an expression — rejected.
throws int func risky(int x) { if (x < 0) { throw; } return x; }
int func sink(int a) { return a; }
kernel { entry func Main() {
  let int x = true ? risky(1) : 0;
} }
