// EXPECT G000
// An unrecognized `@word` used to silently lex as a plain Ident, usable as a
// variable/function name — it type-checked clean all the way through the
// resolver and only failed at the C toolchain stage ("stray '@' in program"),
// nowhere near the actual Gata source defect. Now rejected at the lexer.
kernel { entry func Main() { let int @foo = 5; } }
