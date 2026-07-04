// EXPECT G000
// `catch` takes no binding (a throw carries no payload). The old `catch (e)` form
// is gone — anything but a block after `catch` is a syntax error.
throws int func risky(int x) { if (x < 0) { throw; } return x; }
kernel { entry func Main() {
  try { let int v = risky(1); } catch (e) { }
} }
