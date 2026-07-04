// EXPECT G035
module M { private int func helper() { return 1; } }
kernel { entry func Main() { let int z = M.helper(); } }
