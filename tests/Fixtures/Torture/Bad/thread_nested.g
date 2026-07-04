// EXPECT G000
// Threads are pure topology — they cannot nest.
user { process App { thread T { thread U { entry func R() { } } } } }
