// EXPECT OK
kernel { entry func Main() {
  let int i = 0;
  while (i < 3) { i++; }
  if (i == 3) { } else { }
} }
