// EXPECT OK
// String redesign additions: ordering operators, Split/Join/Replace/Pad/Repeat, StringBuilder.
import LibGata;
import Collections;

kernel { entry func Main() {
    let sb = new StringBuilder();
    sb.Append("Hello");
    sb.AppendChar(',');
    sb.Append(" World!");
    Console.PrintLine(sb.ToString());

    let parts = "a,bb,ccc".Split(",");
    Console.PrintLine(String.Join(parts, "-"));
    Console.PrintLine("foo bar foo".Replace("foo", "baz"));
    Console.PrintLine("7".PadLeft(4, '0'));
    Console.PrintLine("7".PadRight(4, '.'));
    Console.PrintLine("ab".Repeat(3));
    if ("apple" < "banana") { Console.PrintLine("ok"); }
} }
