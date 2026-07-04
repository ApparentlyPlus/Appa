// EXPECT G041
// Mirror of preamble_on_func.g: `@intrinsic` only means anything on a native
// type/method/free-func/extern-func (something with a C name to bind a role to); a
// plain top-level native block has no such name and only ever reads `@preamble`.
// Previously silently discarded; now rejected.
@intrinsic(alloc)
native { #kernel: int x; #user: int x; }
kernel { entry func Main() { } }
