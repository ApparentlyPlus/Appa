// EXPECT G000
// `@keep` only means anything on a class or free function — Dce never
// independently considers an enum's reachability the way it does a class/free
// function (enums aren't even in `m.Classes`/`m.FreeFunctions`), so `@keep` would
// be a no-op there. Rejected the same way every other annotation already is on an
// enum/union/kernel/user/process.
@keep
enum Color { Red, Green }
kernel { entry func Main() { } }
