namespace Appa.Tests;

using Appa;

/// <summary>
/// Unit coverage for the scope machinery itself, ahead of it being load-bearing. Every property
/// here is one a later pass silently depends on.
/// </summary>
public class ScopeTreeTests
{
    [Fact]
    public void RootQualifiesNothing()
    {
        var t = new ScopeTree();
        Assert.True(ScopeId.Root.IsRoot);
        Assert.Equal("Config", t.Qualify(ScopeId.Root, "Config"));
        Assert.Equal("", t.Suffix(ScopeId.Root));
        Assert.Equal(Realm.None, t.RealmOf(ScopeId.Root));
    }

    /// <summary>
    /// Realm scopes are project-global, so the same realm named twice - which happens whenever two
    /// files both open 'realm userspace' - must be one scope, not two.
    /// </summary>
    [Fact]
    public void InterningIsIdempotent()
    {
        var t = new ScopeTree();
        var a = t.Intern(ScopeId.Root, "userspace", Realm.User);
        var b = t.Intern(ScopeId.Root, "userspace", Realm.User);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SiblingRealmsAreDistinctScopes()
    {
        var t = new ScopeTree();
        var k = t.Intern(ScopeId.Root, "kernel", Realm.Kernel);
        var u = t.Intern(ScopeId.Root, "userspace", Realm.User);
        Assert.NotEqual(k, u);
        Assert.NotEqual(t.Qualify(k, "Config"), t.Qualify(u, "Config"));
    }

    /// <summary>
    /// The whole point of the process level: two processes may each declare a Config, and they are
    /// different types.
    /// </summary>
    [Fact]
    public void SameProcessNameAcrossRealmsIsDistinct()
    {
        var t = new ScopeTree();
        var kp = t.Intern(t.Intern(ScopeId.Root, "kernel", Realm.Kernel), "P", Realm.None);
        var up = t.Intern(t.Intern(ScopeId.Root, "userspace", Realm.User), "P", Realm.None);
        Assert.NotEqual(kp, up);
        Assert.NotEqual(t.Qualify(kp, "Config"), t.Qualify(up, "Config"));
    }

    /// <summary>
    /// A process contributes to the name path but never to visibility: it inherits the realm it
    /// sits in. This is the axis split, and getting it wrong would emit a process's declarations
    /// into the wrong translation unit.
    /// </summary>
    [Fact]
    public void ProcessInheritsItsRealm()
    {
        var t = new ScopeTree();
        var kernel = t.Intern(ScopeId.Root, "kernel", Realm.Kernel);
        var proc = t.Intern(kernel, "Worker", Realm.None);
        Assert.Equal(Realm.Kernel, t.RealmOf(proc));
        Assert.Equal("@kernel$Worker", t.Suffix(proc));
    }

    /// <summary>
    /// Qualified names must use characters no Gata identifier may contain, so a user cannot write
    /// one by hand and collide with a generated one.
    /// </summary>
    [Fact]
    public void QualifiedNamesCannotBeTypedAsIdentifiers()
    {
        var t = new ScopeTree();
        var proc = t.Intern(t.Intern(ScopeId.Root, "userspace", Realm.User), "App", Realm.None);
        string q = t.Qualify(proc, "Config");
        Assert.Contains('@', q);
        Assert.Contains('$', q);
        Assert.DoesNotContain(q, c => char.IsLetterOrDigit(c) || c == '_' ? false : c != '@' && c != '$');
    }

    [Fact]
    public void DisplayNameIsReadableAndFullyQualified()
    {
        var t = new ScopeTree();
        var proc = t.Intern(t.Intern(ScopeId.Root, "kernel", Realm.Kernel), "P1", Realm.None);
        Assert.Equal("kernel.P1.Config", t.Display(proc, "Config"));
        Assert.Equal("Config", t.Display(ScopeId.Root, "Config"));
    }

    /// <summary>
    /// Lookup walks outward and stops at the innermost hit. Shadowing is silent by design.
    /// </summary>
    [Fact]
    public void ResolveWalksOutwardAndInnermostWins()
    {
        var t = new ScopeTree();
        var index = new ScopeIndex(t);
        var kernel = t.Intern(ScopeId.Root, "kernel", Realm.Kernel);
        var proc = t.Intern(kernel, "P", Realm.None);

        index.Declare(ScopeId.Root, "Config", "Config");
        index.Declare(kernel, "Config", t.Qualify(kernel, "Config"));
        index.Declare(kernel, "Helper", t.Qualify(kernel, "Helper"));

        Assert.Equal("Config@kernel", index.Resolve(proc, "Config"));   // realm shadows root
        Assert.Equal("Helper@kernel", index.Resolve(proc, "Helper"));   // found by walking out
        Assert.Equal("Config", index.Resolve(ScopeId.Root, "Config"));  // root still sees its own
        Assert.Null(index.Resolve(proc, "Nothing"));                    // absent everywhere
    }

    /// <summary>
    /// A name nothing declares must resolve to null rather than to a guess, because that null is
    /// exactly what makes every pre-existing lookup path apply unchanged.
    /// </summary>
    [Fact]
    public void UndeclaredNamesResolveToNull()
    {
        var t = new ScopeTree();
        var index = new ScopeIndex(t);
        Assert.Null(index.Resolve(ScopeId.Root, "Anything"));
        Assert.False(index.HasScopedDeclarations);
        Assert.Equal(ScopeId.Root, index.ScopeOf("Anything"));
    }

    /// <summary>
    /// Binds a source through the real pass, so the walk is covered rather than just the tree.
    /// </summary>
    private static ScopeBindResult Bind(string src)
    {
        var sources = new SourceSet();
        sources.Add("<test>", src);
        var prog = SingleFileCompile.Parse(src);
        return new ScopeBinder(new DiagnosticBag(sources))
            .Bind([("<test>", prog)]);
    }

    [Fact]
    public void BinderRecordsRealmScopedDeclarations()
    {
        var r = Bind("""
            class Global { int n; }
            realm kernel { class Config { int n; } void func Helper() { } }
            realm userspace { class Config { int n; } }
            """);

        var kernel = r.Tree.Intern(ScopeId.Root, "kernel", Realm.Kernel);
        var user = r.Tree.Intern(ScopeId.Root, "userspace", Realm.User);

        // The collision the flat namespace cannot express today: one Config per realm.
        Assert.Equal("Config@kernel", r.Index.Resolve(kernel, "Config"));
        Assert.Equal("Config@userspace", r.Index.Resolve(user, "Config"));
        Assert.NotEqual(r.Index.Resolve(kernel, "Config"), r.Index.Resolve(user, "Config"));

        Assert.Equal("Helper@kernel", r.Index.Resolve(kernel, "Helper"));
        Assert.Null(r.Index.Resolve(user, "Helper"));   // sibling realms do not see each other
        Assert.True(r.Index.HasScopedDeclarations);
    }

    /// <summary>
    /// A program whose realms declare nothing but an entry func produces no scoped names at all,
    /// which is what makes qualification inert for every program written before scopes existed.
    /// An 'entry func' is deliberately excluded: it is named by the runtime, not by Gata code.
    /// </summary>
    [Fact]
    public void BinderIsInertWithoutScopedDeclarations()
    {
        var r = Bind("class Global { int n; } realm kernel { entry func Main() { } }");
        var kernel = r.Tree.Intern(ScopeId.Root, "kernel", Realm.Kernel);
        Assert.Null(r.Index.Resolve(kernel, "Main"));
        Assert.Null(r.Index.Resolve(ScopeId.Root, "Global"));
        Assert.False(r.Index.HasScopedDeclarations);
    }

    [Fact]
    public void ScopeOfNamesTheDeclaringScope()
    {
        var t = new ScopeTree();
        var index = new ScopeIndex(t);
        var kernel = t.Intern(ScopeId.Root, "kernel", Realm.Kernel);
        index.Declare(kernel, "Config", t.Qualify(kernel, "Config"));
        index.Declare(ScopeId.Root, "Global", "Global");

        Assert.Equal(kernel, index.ScopeOf("Config@kernel"));
        Assert.Equal(ScopeId.Root, index.ScopeOf("Global"));
        Assert.True(index.HasScopedDeclarations);
    }
}
