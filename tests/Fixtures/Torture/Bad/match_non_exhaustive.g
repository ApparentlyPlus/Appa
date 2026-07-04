// EXPECT G039
import LibGata;
union Shape { Circle(float radius), Square(float side), Point }
float func Area(Shape s) {
    match (s) {
        case Circle(r) { return r * r * 3.0; }
        case Square(side) { return side * side; }
    }
}
kernel { entry func Main() { let Shape s = Shape.Point(); let float a = Area(s); } }
