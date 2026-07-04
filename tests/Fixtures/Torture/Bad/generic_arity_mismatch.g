// EXPECT G008
// A generic instantiation with the wrong number of type arguments (`Map[int]`
// instead of `Map[K, V]`) used to report with an empty file (no source line
// shown at all — Monomorphizer's per-request file wasn't threaded through the
// breadth-first queue) and a bare argument-count message. Now reports against
// the real source line and names the actual parameter/argument types
// ("expects 2 type argument(s) (K, V), got 1 (int)"). The failed instantiation
// also used to cascade into a SECOND error showing the raw mangled name
// ("unknown type 'Map_int'"); now registers the display mapping even on
// failure, so the cascade reads "Map[int]" instead.
import LibGata;
import Collections;
kernel { entry func Main() {
  let Map[int] m;
} }
