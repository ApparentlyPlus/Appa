// EXPECT G016
class Widget { }
int func F(int a) { return a; }
int func F(String a) { return 0; }
kernel { entry func Main() { let Widget w = new Widget(); let int x = F(w); } }
