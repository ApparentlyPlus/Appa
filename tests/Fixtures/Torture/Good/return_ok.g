// EXPECT OK
kernel { entry func Main() { let x = add(2,3); } }
int func add(int a, int b) { return a + b; }
