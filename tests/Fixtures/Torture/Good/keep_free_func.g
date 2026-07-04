// EXPECT OK
// `@keep` on a free function never called from any Gata expression, only from raw
// native text (`gata_helper()`), keeps it alive through Dce AND keeps its readable
// `gata_helper` C name through Densifier (a non-`@keep`, non-entry free function
// would normally collapse to a dense token like "_g3").
native {
    void call_it(void) { gata_helper(); }
}
@keep
void func helper() { }
kernel { entry func Main() { } }
