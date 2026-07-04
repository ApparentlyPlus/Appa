// EXPECT OK
kernel { entry func Main() {
  let int a = 5;
  let int64 b = a;       // widening int->int64 is implicit
  let short c = 3;
  let int64 d = c;       // widening short->int64
} }
