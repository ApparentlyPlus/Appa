// EXPECT OK
// Enums + switch (multi-label, default, definite-return) + break/continue in cases.
enum Dir { North, East, South, West }

int func turns(Dir d) {
  switch (d) {
    case Dir.North { return 0; }
    case Dir.East, Dir.West { return 1; }
    default { return 2; }
  }
}

kernel { entry func Main() {
  let int hits = 0;
  for (let int i = 0; i < 8; i++) {
    switch (i) {
      case 0, 1 { continue; }     // targets the loop, not the switch
      case 7 { break; }           // targets the loop
      default { hits = hits + 1; }
    }
  }
  let int t = turns(Dir.West);
  if (t + hits == 1 + 4) { } else { }
} }
