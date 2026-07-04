// EXPECT G000
// A class's generic parameter list used to reuse the loose type-reference grammar,
// so `class Foo[Bar[Baz]]` parsed as a single mangled "parameter" nothing could
// ever refer to — syntactically legal nonsense with no real meaning. Now requires
// each parameter to be a plain name.
class Foo[Bar[Baz]] { int v; }
kernel { entry func Main() { } }
