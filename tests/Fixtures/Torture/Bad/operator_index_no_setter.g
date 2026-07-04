// EXPECT G038
// A class with only an `operator []` getter cannot be assigned through `[]`.
class RO {
    int v;
    operator func [](int i) -> int { return self.v; }
}
kernel { entry func Main() {
    let RO r = new RO();
    r[0] = 5;
} }
