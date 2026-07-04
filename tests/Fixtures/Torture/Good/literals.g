// EXPECT OK
// Numeric literal forms: hex, u/l suffixes, magnitude widening, and float exponents
// / f suffix. (Float is legal in any realm now; the split here is just for variety.)
kernel { entry func Main() {
  let h1   = 0xFF;                       // hex
  let h2   = 0xFFULL;                    // hex + unsigned-int64 suffix
  let wide = 0x8000000000000000;         // wide hex, no suffix -> uint64
  let mask = 0x7FFFFFFFFFFFFFFFULL;      // fdlibm sign mask
  let lng  = 100L;                       // int64 suffix
  let usn  = 4096u;                      // unsigned suffix
  let big  = 5000000000;                 // > int32 -> widens to int64
  let umax = 18446744073709551615;       // > int64 -> uint64
  let dec  = 42;                         // plain int
} }
user { foreground process App { thread T { entry func Run() {
  let huge  = 1.0e+300;                  // double, positive exponent
  let small = 5.96046447753906250000e-08;// double, negative exponent
  let neg   = 1.5e-10;
  let flt   = 2.5f;                       // float suffix
  let fexp  = 2e10f;                      // exponent + float suffix
} } } }
