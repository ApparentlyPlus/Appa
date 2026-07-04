// EXPECT G000
// A free function's return type only goes before `func`, period — not just "not
// both at once" (see dual_return_type.g): the trailing `-> Type` spelling on its
// own, with no leading type at all, is also rejected. `int func Foo()`, never
// `func Foo() -> int`.
func Foo() -> int { return 1; }
kernel { entry func Main() { } }
