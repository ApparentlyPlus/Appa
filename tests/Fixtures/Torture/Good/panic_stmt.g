// EXPECT OK
// `panic "msg";` is kernel-only (see torture/bad/panic_user.g for the rejection
// case); this is the positive case confirming it actually transpiles (it never
// had any committed coverage before — and the env floor bind `_env_panic` was
// missing entirely until this sweep).
import LibGata;

kernel { entry func Main() {
  let int x = 1;
  if (x != 1) { panic "unreachable"; }
  Console.PrintLine("ok");
} }
