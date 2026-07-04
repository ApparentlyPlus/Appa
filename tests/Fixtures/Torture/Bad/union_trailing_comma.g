// EXPECT G000
// Unions don't allow a trailing comma before the closing brace. Enums used to
// (inconsistently) — see enum_trailing_comma.g, now rejected the same way.
union U { A, B, }
kernel { entry func Main() { } }
