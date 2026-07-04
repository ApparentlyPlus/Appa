// EXPECT G007
// `long` is no longer a Gata type name — widths are explicit. Use `int64`.
// (The C-flavoured spellings long/size_t/int32_t survive only inside native bodies.)
kernel { entry func Main() { let long a = 5; } }
