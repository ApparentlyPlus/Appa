namespace Appa;

// Stub - full implementation in feat/backend, coming in at later commits
static class Mangler
{
    public static string Class(string name) => throw new NotImplementedException();
    public static string Allocator(string cls) => throw new NotImplementedException();
    public static string Dtor(string cls) => throw new NotImplementedException();
    public static string Enum(string name) => throw new NotImplementedException();
    public static string EnumMember(string enumName, string member) => throw new NotImplementedException();
    public static string Union(string name) => throw new NotImplementedException();
    public static string UnionTag(string unionName, string variant) => throw new NotImplementedException();
    public static string Method(string owner, string name, IReadOnlyList<Param> ps, bool overloaded) => throw new NotImplementedException();
    public static string FreeFunc(string name, IReadOnlyList<Param> ps, bool overloaded, bool isEntry, bool isExtern) => throw new NotImplementedException();
    public static string Operator(string owner, string op) => throw new NotImplementedException();
    public static string PrivateFreeFunc(string fileToken, string name, IReadOnlyList<Param> ps, bool overloaded) => throw new NotImplementedException();
    public static string FileToken(string file) => throw new NotImplementedException();
    public static string DisplayName(string name) => throw new NotImplementedException();
    public static string OverloadSuffix(IReadOnlyList<Param> ps) => throw new NotImplementedException();
}
