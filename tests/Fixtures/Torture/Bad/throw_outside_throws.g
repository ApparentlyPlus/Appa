// EXPECT G021
// A bare 'throw;' in a function that is neither 'throws' nor inside a 'try'.
kernel { entry func Main() {
  throw;
} }
