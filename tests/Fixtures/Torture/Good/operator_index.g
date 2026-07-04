// EXPECT OK
// Real `operator []` / `operator []=` overload: nominal (a class opts in explicitly),
// supports get, set, and compound assignment (desugars to a getter read + setter write).
import LibGata;

class Grid {
    int v0; int v1; int v2;
    operator func [](int i) -> int {
        if (i == 0) { return self.v0; }
        if (i == 1) { return self.v1; }
        return self.v2;
    }
    operator func []=(int i, int val) {
        if (i == 0) { self.v0 = val; }
        else { if (i == 1) { self.v1 = val; } else { self.v2 = val; } }
    }
}

kernel { entry func Main() {
    let Grid g = new Grid();
    g[0] = 10;
    g[1] = 20;
    g[2] = 30;
    g[1] += 5;
    Console.PrintLine(Int.ToString(g[0] + g[1] + g[2]));
} }
