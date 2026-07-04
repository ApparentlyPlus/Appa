// Float now works in any realm — GatOS restricts SSE only in interrupt-context files,
// so the language no longer bans floating point in the kernel. (Was bad/float_kernel.g
// + bad/double_kernel.g, which expected the now-removed G030/G031.)
kernel { entry func Main() {
  let double d = 1.0;
  let float  f = 2.5f;
} }
