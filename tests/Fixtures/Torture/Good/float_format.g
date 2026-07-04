// EXPECT OK
// Formatting is pure-Gata policy over the env's ONE general delegate (_env_format):
// Format.Double/Int/UInt/Str cover every printf conversion.
import LibGata;
kernel { entry func Main() { } }
user { foreground process App { thread T { entry func Run() {
  let String g = Format.Double(3.5);
  let String a = Format.Double(3.14159, "%.2f");
  let String b = Format.Double(1000.0, "%e");
  let String c = Format.Double(2.0, null);
  let String d = Format.Int((42 as int64), "%05d");
  let String h = Format.UInt((255 as uint64), "%x");
  let String s = Format.Str("hi", "%-5s");
  Console.PrintLine($"g={g} a={a} b={b} c={c} d={d} h={h} s={s}");
} } } }
