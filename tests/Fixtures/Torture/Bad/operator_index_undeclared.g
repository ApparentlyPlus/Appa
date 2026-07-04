// EXPECT G012
// `[]` indexing is nominal — a class without a declared `operator []` cannot be indexed,
// even if it happens to have a structurally-iterable Length()/Get(int) shape.
class Plain {
    int v;
    int func Length() { return 1; }
    int func Get(int i) { return self.v; }
}
kernel { entry func Main() {
    let Plain p = new Plain();
    let int x = p[0];
} }
