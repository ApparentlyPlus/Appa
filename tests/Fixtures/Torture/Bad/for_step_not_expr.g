// EXPECT G000
// The for-loop's third clause is `[ Expr ]` only (ForStmt grammar), not a full
// ForInit/statement — an assignment is not an Expr in Gata (it's a statement-level
// form), so `i = i + 1` in step position is a syntax error. `i++`/`i--` work
// because postfix `++`/`--` ARE Expr-level operators (see for_init_no_let.g).
kernel { entry func Main() {
  let int i = 0;
  for (i = 0; i < 5; i = i + 1) { }
} }
