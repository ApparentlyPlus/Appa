import LibGata;
import Collections;

native {
    #user: long g_checksum = 0;
}
@extern func record_sum(int64 v);

class Box {
    int v;
    func _init(int x) { self.v = x; }
    public int func Get() { return self.v; }
}

// Generic free functions, monomorphized per inferred instantiation. `echo[String]`
// returns a managed parameter — ARC must retain it on the way out (LSan guards this).
T func pickMax[T](T a, T b) { if (a > b) { return a; } return b; }
T func echo[T](T x) { return x; }

enum Shape { Circle, Square, Triangle }

// A throws function whose error is data-driven (errors when x < 0). Native, because
// a Result is the only way to signal an error today. Exercises ARC on both the
// success and the error (catch) paths, including release of a managed local that is
// live when control jumps to the catch.
throws int func risky(int x) native {
    #user:
    if (x < 0) { Result_int err = { .value = 0, .has_error = true }; return err; }
    Result_int res = { .value = x, .has_error = false }; return res;
}

kernel { entry func Main() { } }

user {
    foreground process App {
        thread T {
            entry func Run() {
                let int64 sum = (0 as int64);
                // ARC churn over boxes
                for (let int i = 0; i < 200; i++) {
                    let Box b = new Box(i);
                    sum = sum + (b.Get() as int64);
                }
                // nested generics
                let List[List[int]] grid = new List[List[int]]();
                for (let int r = 0; r < 10; r++) {
                    let List[int] row = new List[int]();
                    for (let int c = 0; c < 10; c++) { row.Add(r * c); }
                    grid.Add(row);
                }
                for row in grid { for v in row { sum = sum + (v as int64); } }
                // collection initializer (ARC over the elements)
                let List[int] lst = new List[int] { 10, 20, 30 };
                for v in lst { sum = sum + (v as int64); }
                // throws + try/catch with a managed local live across the jump to catch
                for (let int q = 0; q < 4; q++) {
                    try {
                        let String tag = Int.ToString(q);
                        let int rr = risky(q - 2);
                        sum = sum + (rr as int64) + (tag.Length() as int64);
                    } catch {
                        sum = sum + 100;
                    }
                }
                // strings: build + concat + interpolation
                let String s = "";
                for (let int k = 0; k < 20; k++) { s = s + Int.ToString(k); }
                sum = sum + (s.Length() as int64);
                // String methods that build buffers (guards the pure-Gata rewrite)
                let String hello = "Hello, World";
                let String up = hello.ToUpper();
                let String lo = hello.ToLower();
                let String sub = hello.Substring(7, 5);
                let String ch = String.FromChar('Z');
                sum = sum + (up.Length() as int64) + (lo.Length() as int64)
                          + (sub.Length() as int64) + (ch.Length() as int64);
                sum = sum + (up.CharAt(0) as int64) + (lo.CharAt(0) as int64)
                          + (sub.CharAt(0) as int64) + (ch.CharAt(0) as int64);
                if (sub.Equals("World")) { sum = sum + 7; }

                // ── Collections over reference elements (ARC stress) ──────────
                // List[String]: add / set / insert / removeAt / reverse / get / search.
                let List[String] words = new List[String]();
                for (let int w = 0; w < 12; w++) { words.Add(Int.ToString(w)); }
                words.Insert(0, "head");
                words.Set(1, "ONE");
                words.RemoveAt(3);
                words.Reverse();
                sum = sum + (words.Length() as int64);
                for word in words { sum = sum + (word.Length() as int64); }
                let String mid = words.Get(2);
                sum = sum + (mid.Length() as int64);
                if (words.Contains("head")) { sum = sum + 5; }
                sum = sum + (words.IndexOf("ONE") as int64);

                // Stack[String]: push / peek / pop (Pop transfers ownership out).
                let Stack[String] st = new Stack[String]();
                for (let int p = 0; p < 8; p++) { st.Push(Int.ToString(p * 3)); }
                let String top = st.Peek();
                sum = sum + (top.Length() as int64) + (st.Length() as int64);
                while (!st.IsEmpty()) { let String popped = st.Pop(); sum = sum + (popped.Length() as int64); }

                // Queue[String]: enqueue / peek / dequeue (Dequeue transfers ownership out).
                let Queue[String] q = new Queue[String]();
                for (let int e = 0; e < 10; e++) { q.Enqueue(Int.ToString(e + 100)); }
                let String fr = q.Peek();
                sum = sum + (fr.Length() as int64);
                while (!q.IsEmpty()) { let String dq = q.Dequeue(); sum = sum + (dq.Length() as int64); }

                // Map[int,int]: put / get / has / remove + growth + backward-shift delete.
                let Map[int, int] mi = new Map[int, int]();
                for (let int m = 0; m < 50; m++) { mi.Put(m, m * m); }
                for (let int m2 = 0; m2 < 50; m2++) { if (m2 % 3 == 0) { mi.Remove(m2); } }
                sum = sum + (mi.Length() as int64);
                for (let int m3 = 0; m3 < 50; m3++) { if (mi.Has(m3)) { sum = sum + (mi.Get(m3) as int64); } }

                // Map[int,String]: managed values (release on overwrite/remove/deinit).
                let Map[int, String] ms = new Map[int, String]();
                for (let int n = 0; n < 20; n++) { ms.Put(n, Int.ToHex(n)); }
                ms.Remove(5);
                sum = sum + (ms.Length() as int64);
                let String hx = ms.Get(15);
                sum = sum + (hx.Length() as int64);

                // StringMap[int]: content-hashed String keys (FNV-1a + bitwise).
                let StringMap[int] smap = new StringMap[int]();
                for (let int z = 0; z < 30; z++) { smap.Put(Int.ToString(z), z * 2); }
                sum = sum + (smap.Length() as int64);
                if (smap.Has("17")) { sum = sum + (smap.Get("17") as int64); }
                sum = sum + (Int.ToHex(255).Length() as int64);

                // ── Ternary: numeric selection, nesting, widening ────────────
                for (let int tc = 0; tc < 50; tc++) {
                    let int sign = tc % 2 == 0 ? 1 : -1;            // bool cond, int arms
                    let int64 w   = tc > 10 ? sum : tc;             // widening: int64 arm + int arm
                    let int band = tc < 10 ? 0 : (tc < 20 ? 1 : 2);// nested / right-assoc
                    sum = sum + ((sign * tc) as int64) + (w & (3 as int64)) + (band as int64);
                }
                // ── Ternary: managed (String) arms — borrow, owning, producer, null ──
                let String a0 = "alpha";
                let String b0 = "beta";
                for (let int ti = 0; ti < 20; ti++) {
                    // borrow position: select a string and read it without taking ownership
                    sum = sum + ((ti % 2 == 0 ? a0 : b0).Length() as int64);
                    // owning + a producer arm: forces the if/else lowering and proves the
                    // untaken arm is never evaluated (no stray allocation / leak)
                    let String pick = ti % 3 == 0 ? Int.ToString(ti) : a0;
                    sum = sum + (pick.Length() as int64);
                    // null arm adopts the class type; guard before use
                    let String maybe = ti % 4 == 0 ? b0 : null;
                    if (maybe != null) { sum = sum + (maybe.Length() as int64); }
                }

                // ── Generic free functions (inferred + monomorphized) ──
                let int gmi = pickMax(3, 9);                          // T=int
                let int64 gml = pickMax((100 as int64), (50 as int64)); // T=int64 (distinct instance)
                let int gmi2 = pickMax(7, 2);                         // reuses the T=int instance
                sum = sum + (gmi as int64) + gml + (gmi2 as int64);
                for (let int ge = 0; ge < 25; ge++) {
                    let String es = echo(Int.ToString(ge));          // T=String — ARC over a managed arg/return
                    sum = sum + (es.Length() as int64);
                }

                // ── Compound bitwise + continue/break + enum switch ──
                let int bits = 0;
                bits |= 8; bits |= 2; bits &= 10; bits ^= 2; bits <<= 1;  // → 16
                sum = sum + (bits as int64);
                for (let int k = 0; k < 10; k++) {
                    if (k == 7) { break; }           // break targets the loop
                    if (k % 2 == 0) { continue; }    // continue targets the loop
                    sum = sum + (k as int64);        // odd k < 7: 1 + 3 + 5 = 9
                }
                let int classify = 0;
                for (let int sh = 0; sh < 3; sh++) {
                    let Shape shape = (sh as Shape);
                    switch (shape) {
                        case Shape.Circle { classify = classify + 1; }
                        case Shape.Square, Shape.Triangle { classify = classify + 10; }
                        default { classify = classify + 100; }
                    }
                }
                sum = sum + (classify as int64);     // 1 + 10 + 10 = 21

                // ── Format: every kind through the ONE general env delegate ──
                // Deterministic on any standard libc; each correct result adds a fixed 1.
                let String g1 = Format.Double(3.5);                 // stringify_float ("%g")
                if (g1.Equals("3.5")) { sum = sum + 1; }
                let String g2 = Format.Double(0.0);
                if (g2.Equals("0")) { sum = sum + 1; }
                let String f1 = Format.Double(3.14159, "%.2f");
                if (f1.Equals("3.14")) { sum = sum + 1; }
                let String f2 = Format.Double(255.0, "%.0f");
                if (f2.Equals("255")) { sum = sum + 1; }
                let String f3 = Format.Double(1.0, null);           // null spec → "%g"
                if (f3.Equals("1")) { sum = sum + 1; }
                let String d1 = Format.Int((255 as int64), "%d");   // signed
                if (d1.Equals("255")) { sum = sum + 1; }
                let String x1 = Format.Int((255 as int64), "%x");   // hex via ll-widen
                if (x1.Equals("ff")) { sum = sum + 1; }
                let String u1 = Format.UInt((255 as uint64), "%04X");
                if (u1.Equals("00FF")) { sum = sum + 1; }
                let String s1 = Format.Str("hi", "%4s");            // width-padded string
                if (s1.Equals("  hi")) { sum = sum + 1; }
                let String pf = Format.Double(3.14159, "%.3f");
                let String pi = $"pi~={pf}";                        // composes with interpolation
                if (pi.Equals("pi~=3.142")) { sum = sum + 1; }

                record_sum(sum);
            }
        }
    }
}
