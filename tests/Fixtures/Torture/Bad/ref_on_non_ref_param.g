// EXPECT G037
// Passing `ref` to a parameter that isn't declared `ref` is also rejected.
void func TakesInt(int n) { }
kernel { entry func Main() {
    let int x = 1;
    TakesInt(ref x);
} }
