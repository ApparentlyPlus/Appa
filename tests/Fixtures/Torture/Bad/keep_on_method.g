// EXPECT G041
// `@keep` only matters on a free function or a class — a method rides with its
// owning class's reachability (Dce.MarkClass marks every method/operator
// unconditionally once the class itself is reachable), so `@keep` on a method
// specifically would be a no-op. Rejected.
class C { @keep int func F() { return 1; } }
kernel { entry func Main() { } }
