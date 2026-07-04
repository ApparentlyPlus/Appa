// EXPECT G000
// Mirror of string_bad_escape.g for char literals: an unrecognized escape used to
// silently drop the backslash and become the literal char ('\q' silently became
// 'q', zero diagnostic). Now rejected.
kernel { entry func Main() { let c = '\q'; } }
