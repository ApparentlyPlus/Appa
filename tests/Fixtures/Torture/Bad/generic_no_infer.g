// EXPECT G007
// A type parameter that never appears in a parameter type cannot be inferred.
T func make[T]() { return default(T); }
kernel { entry func Main() { let int z = make(); } }
