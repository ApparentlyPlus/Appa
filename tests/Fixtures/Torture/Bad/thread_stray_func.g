// EXPECT G000
// A thread body holds exactly one 'entry func' — no helper functions.
// (Define helpers at module/file scope and call them from the entry.)
user { process App { thread T {
  entry func R() { }
  int func helper() { return 1; }
} } }
