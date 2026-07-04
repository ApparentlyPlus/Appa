// EXPECT G008
int func add(int a, int b) { return a + b; }
kernel { entry func Main() { let int x = add(1); } }
