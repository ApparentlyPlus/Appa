// EXPECT G035
// Same as default_private_field.g but for a method, and via a module rather than a
// class — modules are subject to the same private-by-default rule.
module M { int func helper() { return 1; } }
kernel { entry func Main() { let int z = M.helper(); } }
