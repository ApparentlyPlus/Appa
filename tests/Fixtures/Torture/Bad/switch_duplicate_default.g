// EXPECT G000
// `switch` used to place no limit on how many `default` arms appear — each one
// silently OVERWROTE the previous, discarding its entire parsed body with no
// warning. Now a hard error: at most one `default`.
kernel { entry func Main() {
  let int x = 5;
  switch (x) {
    default { x = 1; }
    case 1 { x = 2; }
    default { x = 3; }
  }
} }
