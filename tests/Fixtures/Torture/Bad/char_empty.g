// EXPECT G000
// `''` (empty char literal) used to be silently accepted and become NUL with no
// diagnostic. Now rejected.
kernel { entry func Main() { let c = ''; } }
