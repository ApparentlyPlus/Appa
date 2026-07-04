// EXPECT G040
// `static` only means anything on a class/module method; a free function is
// already never an instance member, so the modifier is a category error here.
static int func helper() { return 1; }
kernel { entry func Main() { let int x = helper(); } }
