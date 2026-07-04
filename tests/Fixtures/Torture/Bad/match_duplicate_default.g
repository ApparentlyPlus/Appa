// EXPECT G000
// Same fix as switch_duplicate_default.g, for `match`.
union Shape { Circle(float radius), Square(float side), Point }

float func Area(Shape s) {
    match (s) {
        default { return -1.0; }
        case Circle(r) { return r * r * 3.0f; }
        default { return -2.0; }
    }
}

kernel { entry func Main() { } }
