// EXPECT G024
int func F() { return 1; let int x = 2; }
kernel { entry func Main() { let int y = F(); } }
