// EXPECT G000
// `static` has no meaning on a field either — Gata has no class-level/shared field
// storage model, so a "static field" would just be a regular per-instance field
// wearing a misleading label. Was silently accepted (never read by any check); now
// a hard error, same as `static` on a free function (see static_free_func.g).
class C { static int x; }
kernel { entry func Main() { } }
