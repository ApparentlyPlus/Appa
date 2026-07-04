// EXPECT OK
// ReadBalanced (the native{}/native type/fields{} brace-counter) used to count
// every literal `{`/`}` byte with zero awareness of C comments or string/char
// literals — a brace inside either desynced the depth counter and truncated the
// native block early, leaking the remaining raw C as bogus Gata tokens. Confirms
// both a `}` inside a C string and inside a `//` comment no longer desync it.
native {
    char* s = "only a closing brace }";
    // a comment with } an unbalanced brace
}
kernel { entry func Main() { } }
