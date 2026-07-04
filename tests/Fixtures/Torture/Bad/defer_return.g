// EXPECT G004
import LibGata;
void func Foo() {
    defer { return; }
}
kernel { entry func Main() { Foo(); } }
