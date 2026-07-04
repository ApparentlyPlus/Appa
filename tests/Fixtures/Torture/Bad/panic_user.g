// EXPECT G031
// panic is valid only in the kernel realm; using it in a userspace thread is an error.
kernel { entry func Main() {} }
user { foreground process A { thread T { entry func Run() { panic "nope"; } } } }
