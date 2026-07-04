// EXPECT OK
// A class referenced ONLY from raw text inside a `native {}` block (never `new`'d,
// never named in any Gata expression) is invisible to Dce's ordinary IR walk and
// would otherwise be silently dropped as a dead typedef while the raw native text
// referencing it (`gata_Ghost* p`) survives verbatim — broken C with no escape
// valve. `@keep` is the explicit fix: it exempts the class from Dce AND from
// Densifier's dense renaming (native text is never rewritten, so the class must
// keep its literal `gata_Ghost` C name). An earlier version of this fix tried to
// *infer* the need for this by regex-scanning native text for name mentions —
// real false-negative risk (a macro, a cast, anything not spelled exactly
// "gata_Ghost" would silently miss it) and no way to express "keep this" for any
// other reason — replaced with this explicit, zero-guesswork annotation. See
// native_no_keep_dropped.g for the negative case: without `@keep`, the class is
// dropped again, by design.
import LibGata;
@keep
class Ghost { int x; }
native {
    void touch_ghost(void) { gata_Ghost* p = 0; }
}
kernel { entry func Main() { } }
