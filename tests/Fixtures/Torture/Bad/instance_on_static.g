// EXPECT G014
class C { public static int func F() { return 1; } }
kernel { entry func Main() { let C c = new C(); let int x = c.F(); } }
