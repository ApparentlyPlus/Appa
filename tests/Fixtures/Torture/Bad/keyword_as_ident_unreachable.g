// EXPECT G000
// `kernel`/`user`/`thread`/`process` used to ALSO be accepted in Primary
// expression position alongside IDENT — syntactically you could write `thread()`
// or bare `process`. But every declaration site (let/func/param/field/generic-
// param/enum-member) requires a strict IDENT token, so no symbol could ever be
// declared with one of these names — the carve-out could parse a reference, but
// that reference could never resolve to anything (always G005). Removed entirely
// rather than left as harmless dead code: now a parse error, same as any other
// unexpected keyword in expression position.
kernel { entry func Main() {
  let int x = thread + process;
} }
