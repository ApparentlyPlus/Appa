// EXPECT OK
// `Process`/`Thread` are opaque handle types (SimpleTypeName alternatives to a
// plain class name) that lower straight to `void*` — meant for whatever a real
// platform binding hands back from spawning a process/thread. Nothing in libgata
// exposes one yet, so this test supplies its own tiny native stand-in. Catching
// this gap surfaced a real parser bug: `let Process p = ...` previously failed to
// parse at all ("expected Ident, found 'Process'") — LooksLikeTypeAndIdent only
// ever recognized TK.Ident as a type-lookahead start, never TK.Process/TK.Thread,
// the same class of bug `let CustomType*` was before its own fix. Fixed in
// Parser.cs's LooksLikeTypeAndIdent.
native {
    void* make_handle(void) { return (void*)1; }
}
@extern func make_handle() -> Process;

void func storeHandle(Process p, Process* slot) {
    unsafe { *slot = p; }
}

kernel { entry func Main() {
    let Process p = make_handle();
    let Thread t = make_handle() as Thread;
    if (p != null && t != null) {
        unsafe {
            let Process saved = null;
            storeHandle(p, &saved);
            if (saved != null) { }
        }
    }
} }
