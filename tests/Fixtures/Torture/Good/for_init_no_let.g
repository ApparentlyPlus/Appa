// EXPECT OK
// ForInit's second form: a plain expression/assignment reusing an existing variable
// instead of declaring a new one with `let`. Also confirms the real grammar
// restriction this is paired with: the third for-clause is `[ Expr ]` only, not a
// full ForInit — `for (i = 0; i < 5; i = i + 1)` is a syntax error (assignment is
// not an Expr in Gata; `i++`/`i--` are, since they're postfix Expr operators).
kernel { entry func Main() {
  let int i = 0;
  let int sum = 0;
  for (i = 0; i < 5; i++) { sum = sum + i; }
} }
