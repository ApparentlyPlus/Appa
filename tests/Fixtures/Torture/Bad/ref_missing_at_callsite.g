// EXPECT G037
// `ref` is required at the call site, not just the declaration.
func Swap[T](ref T a, ref T b) { let tmp = a; a = b; b = tmp; }
kernel { entry func Main() {
    let int x = 1;
    let int y = 2;
    Swap(x, y);
} }
