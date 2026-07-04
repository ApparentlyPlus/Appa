// EXPECT G018
@intrinsic(alloc)
void* func A(usize n) { return null; }
@intrinsic(alloc)
void* func B(usize n) { return null; }
kernel { entry func Main() { } }
