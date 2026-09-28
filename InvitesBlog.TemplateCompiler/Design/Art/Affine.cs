using System.Globalization;
using System.Text.RegularExpressions;

namespace InvitesBlog.TemplateCompiler.Design.Art;

/// <summary>
/// A 2D affine transform in SVG's column order: <c>[a c e; b d f; 0 0 1]</c>, so a point maps to
/// <c>(a·x + c·y + e, b·x + d·y + f)</c>. <c>A * B</c> applies B first, as SVG's transform lists do.
/// </summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public static Affine Rotate(double degrees, double cx = 0, double cy = 0)
    {
        var r = degrees * Math.PI / 180;
        var (s, c) = (Math.Sin(r), Math.Cos(r));
        var rot = new Affine(c, s, -s, c, 0, 0);
        return cx == 0 && cy == 0 ? rot : Translate(cx, cy) * rot * Translate(-cx, -cy);
    }

    public static Affine SkewX(double degrees) => new(1, 0, Math.Tan(degrees * Math.PI / 180), 1, 0, 0);
    public static Affine SkewY(double degrees) => new(1, Math.Tan(degrees * Math.PI / 180), 0, 1, 0, 0);

    public static Affine operator *(Affine m, Affine n) => new(
        m.A * n.A + m.C * n.B,
        m.B * n.A + m.D * n.B,
        m.A * n.C + m.C * n.D,
        m.B * n.C + m.D * n.D,
        m.A * n.E + m.C * n.F + m.E,
        m.B * n.E + m.D * n.F + m.F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    public double Determinant => A * D - B * C;

    public bool IsIdentity(double tolerance = 1e-9) =>
        Math.Abs(A - 1) < tolerance && Math.Abs(B) < tolerance && Math.Abs(C) < tolerance
        && Math.Abs(D - 1) < tolerance && Math.Abs(E) < tolerance && Math.Abs(F) < tolerance;

    public Affine Invert()
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-12) return Identity;
        return new Affine(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }

    /// <summary>
    /// This transform as the designer's element motion: a translation, then a rotation and a uniform
    /// scale about <paramref name="cx"/>,<paramref name="cy"/> — <c>X → c + d + s·R(θ)·(X − c)</c>.
    /// Exact for similarities; a skew or uneven scale comes out as its nearest similarity.
    /// </summary>
    public (double Dx, double Dy, double Degrees, double Scale) AboutCenter(double cx, double cy)
    {
        var scale = Math.Sqrt(Math.Abs(Determinant));
        var degrees = Math.Atan2(B, A) * 180 / Math.PI;
        // The centre goes exactly where this sends it; only the shape about it is approximated.
        var (mx, my) = Apply(cx, cy);
        return (mx - cx, my - cy, degrees, scale);
    }

    public string ToSvg() =>
        string.Create(CultureInfo.InvariantCulture, $"matrix({N(A)} {N(B)} {N(C)} {N(D)} {N(E)} {N(F)})");

    private static string N(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);

    // ----- Parsing -----------------------------------------------------------------------------------

    private static readonly Regex FunctionRegex = new(@"([a-zA-Z]+)\s*\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    /// <summary>The numbers in a string, in order (SVG's lenient list syntax: commas or spaces, or neither).</summary>
    public static List<double> Numbers(string? text)
    {
        var list = new List<double>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (Match m in NumberRegex.Matches(text))
            if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v))
                list.Add(v);
        return list;
    }

    /// <summary>An SVG <c>transform</c> attribute. Anything unreadable is the identity.</summary>
    public static Affine Parse(string? transform)
    {
        var m = Identity;
        if (string.IsNullOrWhiteSpace(transform)) return m;
        foreach (Match f in FunctionRegex.Matches(transform))
            m *= Function(f.Groups[1].Value, Numbers(f.Groups[2].Value));
        return m;
    }

    /// <summary>One SVG transform function (also what <c>animateTransform</c>'s <c>type</c> names).</summary>
    public static Affine Function(string name, IReadOnlyList<double> n)
    {
        double At(int i, double fallback = 0) => i < n.Count ? n[i] : fallback;
        return name switch
        {
            "matrix" when n.Count >= 6 => new Affine(n[0], n[1], n[2], n[3], n[4], n[5]),
            "translate" => Translate(At(0), At(1)),
            "scale" => Scale(At(0, 1), n.Count > 1 ? n[1] : At(0, 1)),
            "rotate" => Rotate(At(0), At(1), At(2)),
            "skewX" => SkewX(At(0)),
            "skewY" => SkewY(At(0)),
            _ => Identity,
        };
    }

    public override string ToString() => ToSvg();
}
