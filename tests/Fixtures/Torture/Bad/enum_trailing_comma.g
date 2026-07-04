// EXPECT G000
// Enums no longer allow a trailing comma before the closing brace — was an
// inconsistent exception versus union (see union_trailing_comma.g), now rejected
// the same way for both.
enum Color { Red, Green, Blue, }
kernel { entry func Main() { } }
