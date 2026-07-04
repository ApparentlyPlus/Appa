// EXPECT OK
// `{{`/`}}` as literal-brace escapes inside an interpolated string (the C#-style
// design `$"..."` is visibly modeled on) — previously unsupported: `{{` was
// mis-parsed as the start of a real interpolation containing the malformed
// sub-expression `{`, producing a confusing unrelated downstream error. Runtime-
// verified externally (not just transpile-checked here): expected output is
// "literal {braces} and a value 5".
import LibGata;
kernel { entry func Main() {
  let int x = 5;
  Console.PrintLine($"literal {{braces}} and a value {x}");
} }
