import LibGata;
import Collections;

// overloaded free functions (selective mangling)
int func combine(int a, int b) { return a + b; }
int64 func combine(int64 a, int64 b) { return a + b; }

class Counter {
    int64 n;
    func _init() { self.n = (0 as int64); }
    public void func Bump(int by) { self.n = self.n + (by as int64); }
    public int64 func Value() { return self.n; }
}

kernel {
    entry func Main() {
        // arithmetic + explicit narrowing
        let int64 total = (0 as int64);
        for (let int i = 0; i < 100; i++) {
            total = total + combine(i, i);     // int overload
        }
        let int shown = total as int;          // explicit narrowing

        // ARC churn: allocate/free Counters in a loop
        let Counter c = new Counter();
        for (let int k = 0; k < 50; k++) {
            let Counter tmp = new Counter();
            tmp.Bump(k);
            c.Bump(tmp.Value() as int);
        }

        // nested generics + collection iteration
        let List[List[int]] grid = new List[List[int]]();
        for (let int r = 0; r < 3; r++) {
            let List[int] row = new List[int]();
            row.Add(r); row.Add(r + 1);
            grid.Add(row);
        }
        let int acc = 0;
        for row in grid { for v in row { acc += v; } }

        // strings: concat + interpolation
        Console.PrintLine("shown=" + Int.ToString(shown));
        Console.PrintLine($"counter={c.Value() as int} acc={acc}");
        if (acc == 9 && shown == 9900) { Console.PrintLine("REGRESSION_OK"); }
    }
}

user {
    foreground process App {
        thread T {
            entry func Run() {
                let double p = Math.Pi();
                Console.PrintLine($"pi*2={Math.Round(p + p) as int}");
            }
        }
    }
}
