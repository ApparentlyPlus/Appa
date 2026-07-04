// EXPECT G004
// A hex literal that does not fit in 64 bits is rejected, like its decimal sibling.
kernel { entry func Main() { let x = 0x1FFFFFFFFFFFFFFFFF; } }
