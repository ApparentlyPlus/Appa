// EXPECT G004
user { foreground process App { thread T { entry func Run() {
  let int x = 0; let double d = 2.0;
  x <<= d;
} } } }
