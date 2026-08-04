namespace Appa;

using System.Collections.Immutable;

/// <summary>
/// A declaration compared by the shape of its parameter list rather than by a rendering of it.
/// </summary>
internal readonly record struct SignatureKey(string Name, ImmutableArray<TypeSpec> Params)
{
    /// <summary>
    /// The key for a declaration written with a parameter list. Ref-ness is not part of it, matching
    /// the overload rule: two functions differing only in 'ref' are one signature.
    /// </summary>
    public static SignatureKey Of(string name, Param[] ps)
    {
        var types = ImmutableArray.CreateBuilder<TypeSpec>(ps.Length);
        foreach (var p in ps) types.Add(p.Type);
        return new SignatureKey(name, types.MoveToImmutable());
    }

    public bool Equals(SignatureKey other)
    {
        if (Name != other.Name || Params.Length != other.Params.Length) return false;
        for (int i = 0; i < Params.Length; i++)
            if (!SameShape(Params[i], other.Params[i])) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Name);
        foreach (var p in Params) h.Add(ShapeHash(p));
        return h.ToHashCode();
    }

    /// <summary>
    /// Compares two type specs by shape.
    /// </summary>
    public static bool SameShape(TypeSpec? a, TypeSpec? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        switch (a, b)
        {
            case (NamedSpec x, NamedSpec y):
                if (x.Name != y.Name || x.Args.Length != y.Args.Length) return false;
                for (int i = 0; i < x.Args.Length; i++)
                    if (!SameShape(x.Args[i], y.Args[i])) return false;
                return true;
            case (PtrSpec x, PtrSpec y):
                return SameShape(x.Inner, y.Inner);
            case (ArraySpec x, ArraySpec y):
                return x.SizeText == y.SizeText && SameShape(x.Elem, y.Elem);
            case (FuncSpec x, FuncSpec y):
                if (x.Params.Length != y.Params.Length || !SameShape(x.Ret, y.Ret)) return false;
                for (int i = 0; i < x.Params.Length; i++)
                    if (!SameShape(x.Params[i], y.Params[i])) return false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A hash agreeing with SameShape.
    /// </summary>
    public static int ShapeHash(TypeSpec? t)
    {
        var h = new HashCode();
        switch (t)
        {
            case null:
                break;
            case NamedSpec n:
                h.Add(n.Name);
                foreach (var a in n.Args) h.Add(ShapeHash(a));
                break;
            case PtrSpec p:
                h.Add('*');
                h.Add(ShapeHash(p.Inner));
                break;
            case ArraySpec a:
                h.Add(a.SizeText);
                h.Add(ShapeHash(a.Elem));
                break;
            case FuncSpec f:
                h.Add(ShapeHash(f.Ret));
                foreach (var p in f.Params) h.Add(ShapeHash(p));
                break;
        }
        return h.ToHashCode();
    }
}
