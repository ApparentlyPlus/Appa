// EXPECT G000
// `@intrinsic`/`@preamble` have no defined meaning on a `kernel{}`/`user{}` block —
// previously silently parsed and discarded; now a hard error.
@intrinsic(alloc)
kernel { entry func Main() { } }
