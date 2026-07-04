// EXPECT G015
int func F(int a, float b) { return a; }
int func F(float a, int b) { return b; }
kernel { entry func Main() { let int x = F(1, 1); } }
