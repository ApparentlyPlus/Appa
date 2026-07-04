// EXPECT G026
void func F() { return; }
kernel { entry func Main() { F(); } }
