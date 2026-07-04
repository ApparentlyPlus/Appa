// EXPECT G006
class C { int x; func f() { let y = self.x(); } }
kernel { entry func Main() { } }
