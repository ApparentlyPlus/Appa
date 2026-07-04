// EXPECT G029
// The ternary condition must be 'bool', not an int.
kernel { entry func Main() {
  let int a = 1;
  let int b = a ? 2 : 3;
} }
