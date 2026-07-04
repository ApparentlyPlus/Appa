// EXPECT OK
// `debug "msg";` — allowed in debug mode (the manifest mode the test harness
// builds under), unlike `panic`, allowed in both kernel and user code.
import LibGata;

kernel { entry func Main() {
  debug "entering Main";
  let int x = 1 + 1;
  debug "x computed";
  if (x == 2) { debug "x is two"; } else { debug "x is not two"; }
  Console.PrintLine("done");
} }
