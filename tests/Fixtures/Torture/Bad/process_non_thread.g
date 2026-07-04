// EXPECT G000
// A process body may contain only 'thread' declarations, not loose functions.
user { process App { int func oops() { return 1; } } }
