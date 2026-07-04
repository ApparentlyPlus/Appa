// EXPECT G041
// `@preamble` only means anything on a top-level native block (it sets which
// section of generated C the block's body lands in); a free function only ever
// reads `@intrinsic`. Previously silently discarded (BindIntrinsics' loop skipped
// any non-IntrinsicAnnotation with no error); now rejected.
@preamble(user)
int func Foo() { return 1; }
kernel { entry func Main() { } }
