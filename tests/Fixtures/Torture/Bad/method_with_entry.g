// EXPECT G000
// entry modifier is not allowed on class methods.
class C {
    entry void func Run() {}
}
kernel { entry func Main() {} }
