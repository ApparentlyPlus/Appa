// EXPECT G017
@intrinsic(totally_bogus_role)
int func Foo() { return 0; }
kernel { entry func Main() { } }
