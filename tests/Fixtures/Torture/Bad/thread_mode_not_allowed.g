// EXPECT G043
// foreground or background modifiers are not allowed on thread declarations.
kernel { entry func Main() {} }
user {
  foreground process App {
    background thread Worker { entry func Run() {} }
  }
}
