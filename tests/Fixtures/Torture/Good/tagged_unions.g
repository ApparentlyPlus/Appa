// EXPECT OK
// Tagged unions + exhaustive pattern matching: a no-payload variant and two
// payload-carrying variants, constructed via Union.Variant(...) and matched
// without a `default` (the resolver checks exhaustiveness statically).
import LibGata;

union Shape { Circle(float radius), Square(float side), Point }

float func Area(Shape s) {
    match (s) {
        case Circle(r) { return r * r * 3.0f; }
        case Square(side) { return side * side; }
        case Point { return 0.0f; }
    }
}

kernel { entry func Main() {
    let Shape a = Shape.Circle(2.0);
    let Shape b = Shape.Square(3.0);
    let Shape c = Shape.Point();
    let float total = Area(a) + Area(b) + Area(c);
    if (total > 20.0 && total < 22.0) { Console.PrintLine("ok"); } else { Console.PrintLine("bad"); }
} }
