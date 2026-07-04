// EXPECT G022
// 'break' inside a catch with no enclosing loop has nothing to break out of.
throws int func risky(int x) { if (x < 0) { throw; } return x; }
int func sink(int a) { return a; }
kernel { entry func Main() {
  try { let int x = risky(1); sink(x); } catch { break; }
} }
