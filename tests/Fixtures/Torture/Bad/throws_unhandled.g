// EXPECT G021
// A throwing call at statement root but outside any 'try' / 'throws' function.
// (Regression guard: this used to crash the backend instead of diagnosing.)
throws int func risky(int x) { if (x < 0) { throw; } return x; }
int func sink(int a) { return a; }
kernel { entry func Main() {
  let int x = risky(1);
} }
