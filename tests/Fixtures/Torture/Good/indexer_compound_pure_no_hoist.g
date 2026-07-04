// EXPECT OK
// `HoistIfImpure`'s underlying purity check (`IsPure`) is recursive: a field read
// of a pure variable (`h.items`) and a literal index are both pure, so the
// compound-assign fix (indexer_compound_no_dup.g) must NOT hoist them into
// temps — confirmed by inspecting the emitted C directly (not by this harness,
// which only checks transpile success): `h.items[0] += 5` emits
// `_gp(h->items, 0, (_go(h->items, 0) + 5));` with no `_ixo`/`_ixi` temp
// declarations and no extra nested `{ }` block, unlike the side-effecting-index
// case in indexer_compound_no_dup.g, which still needs both.
import LibGata;
import Collections;
class Holder {
    public List[int] items;
    func _init() { self.items = new List[int](); self.items.Add(10); }
}
kernel { entry func Main() {
    let Holder h = new Holder();
    h.items[0] += 5;
} }
