// EXPECT OK
kernel { entry func Main() {
  let int64 a = 5;
  let int b = a as int;   // explicit narrowing OK
} }
