// EXPECT G034
// A `ref` argument must be an lvalue.
func Swap[T](ref T a, ref T b) { let tmp = a; a = b; b = tmp; }
kernel { entry func Main() {
    let int y = 2;
    Swap(ref 5, ref y);
} }
