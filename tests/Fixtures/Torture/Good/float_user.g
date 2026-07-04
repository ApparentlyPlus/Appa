// EXPECT OK
import LibGata;
user { foreground process App { thread T { entry func Run() {
  let double d = Math.Pi();
} } } }
kernel { entry func Main() { } }
