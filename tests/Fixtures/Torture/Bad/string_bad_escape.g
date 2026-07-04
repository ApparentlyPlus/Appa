// EXPECT G000
// An unrecognized escape sequence (anything but \n \t \r \0 \' \" \\) used to be
// silently passed through verbatim into the generated C, which gcc warns on and
// silently drops the backslash for at the C level — a behavior difference the
// Gata source gave zero indication of. Now rejected at the lexer.
kernel { entry func Main() { let s = "hi \q there"; } }
