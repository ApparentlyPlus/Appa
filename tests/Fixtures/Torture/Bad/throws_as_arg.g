// EXPECT G021
// A throwing call cannot hide inside a larger expression (here, an argument):
// its Result has nowhere to be unpacked.
throws int func risky(int x) { if (x < 0) { throw; } return x; }
int func sink(int a) { return a; }
kernel { entry func Main() {
  let int x = sink(risky(1));
} }
