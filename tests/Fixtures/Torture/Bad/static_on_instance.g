// EXPECT G013
class C { public int func F() { return 1; } }
kernel { entry func Main() { let int x = C.F(); } }
