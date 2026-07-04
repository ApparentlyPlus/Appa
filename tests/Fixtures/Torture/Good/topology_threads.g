// EXPECT OK
// A foreground and a background process, each with a thread; thread modes vary.
int func work(int a) { return a + 1; }

kernel { entry func Main() { } }

user {
  foreground process App {
    thread Ui      { entry func Run() { let int a = work(1); } }
    thread Worker { entry func Run() { let int b = work(2); } }
  }
  background process Daemon {
    thread Loop { entry func Run() { let int c = work(3); } }
  }
}
