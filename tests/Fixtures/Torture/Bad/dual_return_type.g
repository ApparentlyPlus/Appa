// EXPECT G000
// A free function used to accept a return type written BOTH before `func` and
// after the parameter list, with the trailing one silently winning and the leading
// one discarded with no warning. Now a hard error: write the return type once.
int func Foo() -> String { return "hi"; }
kernel { entry func Main() { } }
