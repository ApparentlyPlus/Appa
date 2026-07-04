// EXPECT OK
// `import "path/to/file.g";` — the quoted form, resolved relative to the project
// root (here, this file's own directory in --pure-transpile mode), as opposed to
// the bare-name form (`import LibGata;`) resolved against --stdlib. Previously had
// zero coverage anywhere outside its own grammar/doc mention.
import "importpath/helper.g";
kernel { entry func Main() { let int x = addTwo(3); } }
