// EXPECT G004
// A `ref`-taking function used to decay into a function-pointer value with the
// `ref` silently erased — `func(...) -> R` types have no slot to express which
// parameters are `ref` at all, so the generated C function-pointer type and the
// real function's C signature disagreed (`int` vs `int*`): assigning/calling
// through the pointer compiled, but was a real type mismatch (undefined behavior
// at runtime), not just an unchecked feature. Now rejected at the point the value
// would be created.
func Inc(ref int x) { x = x + 1; }
kernel { entry func Main() {
  let f = Inc;
} }
