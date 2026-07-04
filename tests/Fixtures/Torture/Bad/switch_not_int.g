// EXPECT G004
kernel { entry func Main() {
  let String s = "x";
  switch (s) { default { } }
} }
