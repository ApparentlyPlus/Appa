// EXPECT G006
// Diagnostics involving a generic instantiation used to show the internal mangled
// name ("List_int") instead of what the user wrote ("List[int]") — every type name
// reaching the resolver is already mangled at parse time (ParseTypeNameStr mangles
// type references, not just declarations), and nothing translated it back for
// display. Fixed via Mangler.DisplayName, backed by a registry the Monomorphizer
// populates per instantiation. This test doesn't assert the exact message text
// (the harness only greps for the code), but running it manually confirms
// "'List[int]' has no method 'Bogus'", not "'List_int' has no method 'Bogus'".
import LibGata;
import Collections;
kernel { entry func Main() {
  let List[int] xs = new List[int]();
  xs.Bogus();
} }
