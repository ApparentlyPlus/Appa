// EXPECT G004
// The two arms have incompatible types (int vs String) and cannot be unified.
kernel { entry func Main() {
  let int a = 1;
  let int x = a > 0 ? 5 : "no";
} }
