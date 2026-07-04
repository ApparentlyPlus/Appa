// EXPECT G032
// Has Get(int) but no Length() -> int — the structural for..in protocol requires both.
class Bag {
    int func Get(int i) { return i; }
}
kernel { entry func Main() {
    let Bag b = new Bag();
    for x in b { }
} }
