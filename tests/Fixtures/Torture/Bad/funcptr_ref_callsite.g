// EXPECT G037
// Mirror of funcptr_ref_decay.g: `ref` written at an indirect-call site (through a
// function-pointer-typed variable) used to be silently stripped to the plain
// argument with zero diagnostic — ResolveCall unwraps every `ref x` to `x`
// unconditionally before the callee is known, and unlike direct calls (which
// re-check the original `ref` against the callee's signature in CoerceArgs),
// nothing did that for an indirect call. Since a function-pointer type can never
// have a `ref` parameter (see funcptr_ref_decay.g), writing `ref` here is always
// wrong — now rejected unconditionally.
int func AddOne(int x) { return x + 1; }
kernel { entry func Main() {
  let f = AddOne;
  let int y = 5;
  let int r = f(ref y);
} }
