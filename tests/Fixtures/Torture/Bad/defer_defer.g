// EXPECT G004
// `defer defer X;` — nesting a defer inside a defer's own action — used to crash
// the compiler (InvalidOperationException: "Collection was modified; enumeration
// operation may not execute") rather than producing a diagnostic: Ownership.cs's
// ReleaseFrame splices a frame's deferred actions by iterating `f.Defers` and
// re-lowering each one, and lowering a nested defer inserts into that same list
// mid-iteration. Found by tests/stress/fuzz_grammar.py (seed 200, case 202). Same
// family as defer_return.g/defer_break.g — a defer body cannot itself defer.
kernel { entry func Main() { defer defer { } } }
