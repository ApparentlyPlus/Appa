// EXPECT G000
// A raw, unescaped newline inside a string literal used to be accepted and
// forwarded verbatim into the generated C string literal, splitting it across
// two physical lines — guaranteed invalid C ("missing terminating \" character").
// Now rejected at the lexer as an unterminated string.
kernel { entry func Main() { let s = "line1
line2"; } }
