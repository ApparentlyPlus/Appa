// EXPECT G000
// A field used to silently accept `entry`/`throws`/annotations preceding it (the
// flags were just dropped, no error) since the parser consumes them unconditionally
// before deciding method-vs-field. Now a hard error.
class C { throws int x; }
kernel { entry func Main() { } }
