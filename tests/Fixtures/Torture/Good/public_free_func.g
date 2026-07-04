// EXPECT OK
// `public` on a free function is accepted as a harmless, redundant spelling of the
// default — a free function is already maximally visible (any importer can call
// it), so there's no narrower default for `public` to opt out of the way class
// members have one. Unlike `static` (see tests/torture/bad/static_free_func.g),
// this isn't a category error, just a no-op.
import LibGata;
public int func helper() { return 1; }
kernel { entry func Main() { Console.PrintLine(Int.ToString(helper())); } }
