// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
///
/// <para>The outline is kept as SVG path data rather than as a built
/// <see cref="Geometry"/> on purpose. <c>Geometry.Parse</c> needs Avalonia's
/// platform render interface, so building eagerly would make the parser
/// untestable without a window and would drag a rendering backend into every
/// icon inventory pass. Here the text is the parse result and
/// <see cref="Ps5IconShape.Build"/> turns it into geometry at draw time.</para>
/// </summary>
/// <param name="PathData">The outline as SVG path data, in viewBox coordinates.</param>
/// <param name="Transform">Accumulated group transform for this shape.</param>
/// <param name="Fill">Declared fill, or null when the shape inherits the caller's tint.</param>
/// <param name="Opacity">Declared opacity, 1 when absent.</param>
/// <param name="EvenOdd">True when the shape declared <c>fill-rule="evenodd"</c>.</param>
public sealed record Ps5IconShape(
    string PathData, Matrix Transform, Color? Fill, double Opacity, bool EvenOdd)
{
    private Geometry? _geometry;

    /// <summary>
    /// Builds (and caches) the geometry for this shape, or null when the path
    /// data will not parse. Requires Avalonia's render interface, so it is only
    /// ever called from a draw.
    /// </summary>
    public Geometry? Build()
    {
        if (_geometry is not null)
        {
            return _geometry;
        }

        try
        {
            var geometry = Geometry.Parse(PathData);
            if (geometry is PathGeometry path)
            {
                path.FillRule = EvenOdd ? FillRule.EvenOdd : FillRule.NonZero;
            }

            _geometry = geometry;
            return _geometry;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// A parsed <c>iconid_*</c> vector icon: its authoring grid and its shapes.
///
/// <para>The 671 vector icons in <c>Sce.PlayStation.PUI_UI3.rco</c> are plain
/// uncompressed SVG (<c>type="texture/svg"</c>), authored in Sketch on a 64x64
/// grid and exported at 2x. Because they are vectors they are exact at every
/// size, so this type deliberately has no pixel dimension: rasterising them to
/// a fixed size would throw away the only reason to prefer them over the
/// PNGs.</para>
/// </summary>
/// <param name="Id">The <c>iconid_*</c> id, without the prefix stripped.</param>
/// <param name="ViewBox">The SVG viewBox, i.e. the coordinate space the shapes live in.</param>
/// <param name="Shapes">Shapes in paint order.</param>
public sealed record Ps5VectorIcon(string Id, Rect ViewBox, IReadOnlyList<Ps5IconShape> Shapes)
{
    /// <summary>
    /// Draws the icon into <paramref name="destination"/>, fitting the viewBox
    /// uniformly and centring it. <paramref name="tint"/> paints shapes that
    /// declared no fill of their own, which is the <c>tintColor</c> path in
    /// <c>IconPS.ps.js</c> (default <c>#ffffff</c>).
    /// </summary>
    /// <param name="context">Target drawing context.</param>
    /// <param name="destination">Where to draw, in the caller's coordinates.</param>
    /// <param name="tint">Colour for untinted shapes; the shell's default is white.</param>
    public void Render(
        DrawingContext context,
        Rect destination,
        Color tint,
        bool overrideDeclaredFill = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ViewBox.Width <= 0 || ViewBox.Height <= 0 ||
            destination.Width <= 0 || destination.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(destination.Width / ViewBox.Width, destination.Height / ViewBox.Height);
        var offsetX = destination.X + ((destination.Width - (ViewBox.Width * scale)) / 2.0);
        var offsetY = destination.Y + ((destination.Height - (ViewBox.Height * scale)) / 2.0);

        var transform = Matrix.CreateTranslation(-ViewBox.X, -ViewBox.Y)
                        * Matrix.CreateScale(scale, scale)
                        * Matrix.CreateTranslation(offsetX, offsetY);

        using var _ = context.PushTransform(transform);
        foreach (var shape in Shapes)
        {
            var geometry = shape.Build();
            if (geometry is null)
            {
                continue;
            }

            using var __ = context.PushTransform(shape.Transform);
            var colour = overrideDeclaredFill ? tint : shape.Fill ?? tint;
            context.DrawGeometry(new SolidColorBrush(colour, shape.Opacity), pen: null, geometry);
        }
    }
}

/// <summary>
///
/// <para>Every one of the 671 icons is a Sketch export with the same shape: a
/// root <c>&lt;svg viewBox="0 0 64 64"&gt;</c>, nested <c>&lt;g&gt;</c> groups
/// carrying <c>fill</c>, <c>fill-rule</c> and an optional <c>transform</c>, and
/// <c>&lt;path d="..."&gt;</c> leaves. Rects, circles, ellipses and polygons
/// appear rarely enough to be worth handling and no more. Anything outside that
/// subset — strokes, gradients, clip paths, <c>&lt;use&gt;</c> — is skipped
/// rather than approximated, and <see cref="Ps5SvgIconParser.Parse"/> reports
/// what it skipped so a gap in the set is visible instead of silent.</para>
///
/// <para>Path data goes straight to <see cref="Geometry.Parse"/>: Avalonia's
/// points, not a re-fit.</para>
/// </summary>
public static class Ps5SvgIconParser
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>
    /// Parses an icon SVG. Returns null when the document is not an SVG or
    /// carries no drawable shape at all; never throws on malformed input, since
    /// this runs over blobs carved out of a user's dump.
    /// </summary>
    /// <param name="svg">SVG document text.</param>
    /// <param name="id">Icon id to record on the result.</param>
    /// <param name="skipped">Element names that were recognised but not drawn.</param>
    public static Ps5VectorIcon? Parse(string svg, string id, out IReadOnlyList<string> skipped)
    {
        var skippedNames = new List<string>();
        skipped = skippedNames;

        if (string.IsNullOrWhiteSpace(svg))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(svg, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg")
        {
            return null;
        }

        var viewBox = ParseViewBox(root);
        var shapes = new List<Ps5IconShape>();
        Walk(root, Matrix.Identity, inheritedFill: null, inheritedOpacity: 1.0, inheritedEvenOdd: false, shapes, skippedNames);

        return shapes.Count == 0 ? null : new Ps5VectorIcon(id, viewBox, shapes);
    }

    /// <summary>
    /// Reads the root <c>viewBox</c>, falling back to <c>width</c>/<c>height</c>
    /// </summary>
    /// <param name="root">The <c>&lt;svg&gt;</c> element.</param>
    public static Rect ParseViewBox(XElement root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var raw = (string?)root.Attribute("viewBox");
        if (raw is not null)
        {
            var parts = raw.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 &&
                TryNumber(parts[0], out var x) && TryNumber(parts[1], out var y) &&
                TryNumber(parts[2], out var w) && TryNumber(parts[3], out var h) &&
                w > 0 && h > 0)
            {
                return new Rect(x, y, w, h);
            }
        }

        var width = ParseLength((string?)root.Attribute("width"));
        var height = ParseLength((string?)root.Attribute("height"));
        if (width > 0 && height > 0)
        {
            return new Rect(0, 0, width, height);
        }

        // The authoring grid the whole set shares. Stated, not assumed: the
        // extracted blobs all declare viewBox="0 0 64 64" at width/height 128px.
        return new Rect(0, 0, 64, 64);
    }

    // Depth-first so shapes land in document (paint) order. Group state -
    // transform, fill and opacity - inherits down exactly as SVG says it does;
    // stroke does not, because nothing in this set strokes.
    private static void Walk(
        XElement element,
        Matrix transform,
        Color? inheritedFill,
        double inheritedOpacity,
        bool inheritedEvenOdd,
        List<Ps5IconShape> shapes,
        List<string> skipped)
    {
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (child.Name.Namespace != Svg && child.Name.Namespace != XNamespace.None)
            {
                continue;
            }

            var childTransform = ParseTransform((string?)child.Attribute("transform")) * transform;
            var fill = ParseFill((string?)child.Attribute("fill")) ?? inheritedFill;
            var opacity = inheritedOpacity * ParseOpacity((string?)child.Attribute("opacity"));

            // Sketch puts fill-rule on the outermost group and the paths several
            // levels down inherit it, so this has to descend like fill does
            // rather than be read off the immediate parent.
            var rule = (string?)child.Attribute("fill-rule");
            var evenOdd = rule is null
                ? inheritedEvenOdd
                : string.Equals(rule, "evenodd", StringComparison.OrdinalIgnoreCase);

            switch (local)
            {
                case "g":
                    Walk(child, childTransform, fill, opacity, evenOdd, shapes, skipped);
                    continue;

                case "title" or "desc" or "metadata" or "defs":
                    continue;

                case "path" or "rect" or "circle" or "ellipse" or "polygon" or "polyline":
                    var data = BuildPathData(child, local);
                    if (string.IsNullOrEmpty(data))
                    {
                        skipped.Add(local);
                        continue;
                    }

                    // "none" means the shape is a container for its children in
                    // this set, never an invisible shape worth emitting.
                    if (IsNoneFill((string?)child.Attribute("fill")))
                    {
                        continue;
                    }

                    shapes.Add(new Ps5IconShape(data, childTransform, fill, opacity, evenOdd));
                    continue;

                default:
                    skipped.Add(local);
                    continue;
            }
        }
    }

    /// <summary>
    /// Reduces one SVG shape element to path data. Everything becomes a path so
    /// there is a single code path to render and a single thing to cache; the
    /// primitives are rare enough in this set that the conversion is cheaper
    /// than a second shape type would be.
    /// </summary>
    /// <param name="element">The shape element.</param>
    /// <param name="local">Its local name.</param>
    public static string BuildPathData(XElement element, string local)
    {
        ArgumentNullException.ThrowIfNull(element);

        switch (local)
        {
            case "path":
                var d = (string?)element.Attribute("d");
                return string.IsNullOrWhiteSpace(d) ? string.Empty : d.Trim();

            case "rect":
                var x = ParseLength((string?)element.Attribute("x"));
                var y = ParseLength((string?)element.Attribute("y"));
                var w = ParseLength((string?)element.Attribute("width"));
                var h = ParseLength((string?)element.Attribute("height"));
                if (w <= 0 || h <= 0)
                {
                    return string.Empty;
                }

                // Corner radii are ignored rather than approximated: no icon in
                // the set uses them, and a wrong corner is worse than a square
                // one because it looks deliberate.
                return Invariant($"M{x},{y} H{x + w} V{y + h} H{x} Z");

            case "circle":
                var r = ParseLength((string?)element.Attribute("r"));
                return r <= 0
                    ? string.Empty
                    : EllipsePath(
                        ParseLength((string?)element.Attribute("cx")),
                        ParseLength((string?)element.Attribute("cy")), r, r);

            case "ellipse":
                var erx = ParseLength((string?)element.Attribute("rx"));
                var ery = ParseLength((string?)element.Attribute("ry"));
                return erx <= 0 || ery <= 0
                    ? string.Empty
                    : EllipsePath(
                        ParseLength((string?)element.Attribute("cx")),
                        ParseLength((string?)element.Attribute("cy")), erx, ery);

            case "polygon" or "polyline":
                var points = (string?)element.Attribute("points");
                return string.IsNullOrWhiteSpace(points)
                    ? string.Empty
                    : BuildPolygonPath(points, close: local == "polygon");

            default:
                return string.Empty;
        }
    }

    // Two half-arcs, because SVG's arc command cannot describe a full ellipse in
    // one sweep (start and end would coincide and the arc collapses).
    private static string EllipsePath(double cx, double cy, double rx, double ry) =>
        Invariant(
            $"M{cx - rx},{cy} A{rx},{ry} 0 1 0 {cx + rx},{cy} A{rx},{ry} 0 1 0 {cx - rx},{cy} Z");

    private static string Invariant(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);

    private static string BuildPolygonPath(string points, bool close)
    {
        var numbers = points.Split(
            [' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (numbers.Length < 4)
        {
            return string.Empty;
        }

        var path = new System.Text.StringBuilder();
        for (var i = 0; i + 1 < numbers.Length; i += 2)
        {
            path.Append(i == 0 ? 'M' : 'L')
                .Append(numbers[i]).Append(' ').Append(numbers[i + 1]).Append(' ');
        }

        if (close)
        {
            path.Append('Z');
        }

        return path.ToString();
    }

    /// <summary>
    /// Parses the SVG <c>transform</c> forms this set uses: <c>translate</c>,
    /// <c>scale</c>, <c>rotate</c> (degrees, optional centre) and <c>matrix</c>.
    /// Unknown functions are ignored rather than approximated.
    /// </summary>
    /// <param name="value">A <c>transform</c> attribute value, or null.</param>
    public static Matrix ParseTransform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Matrix.Identity;
        }

        var result = Matrix.Identity;
        var index = 0;
        while (index < value.Length)
        {
            var open = value.IndexOf('(', index);
            if (open < 0)
            {
                break;
            }

            var close = value.IndexOf(')', open);
            if (close < 0)
            {
                break;
            }

            var name = value[index..open].Trim(' ', ',', '\t', '\n', '\r');
            var args = value[(open + 1)..close]
                .Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            index = close + 1;

            var n = new double[args.Length];
            var ok = true;
            for (var i = 0; i < args.Length; i++)
            {
                ok &= TryNumber(args[i], out n[i]);
            }

            if (!ok)
            {
                continue;
            }

            // SVG applies transforms left to right, outermost first, so each new
            // one pre-multiplies.
            result = name switch
            {
                "translate" when n.Length >= 1 =>
                    Matrix.CreateTranslation(n[0], n.Length > 1 ? n[1] : 0) * result,
                "scale" when n.Length >= 1 =>
                    Matrix.CreateScale(n[0], n.Length > 1 ? n[1] : n[0]) * result,
                "rotate" when n.Length == 1 =>
                    Matrix.CreateRotation(n[0] * Math.PI / 180.0) * result,
                "rotate" when n.Length >= 3 =>
                    Matrix.CreateTranslation(-n[1], -n[2])
                    * Matrix.CreateRotation(n[0] * Math.PI / 180.0)
                    * Matrix.CreateTranslation(n[1], n[2]) * result,
                "matrix" when n.Length >= 6 =>
                    new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]) * result,
                _ => result,
            };
        }

        return result;
    }

    /// <summary>Parses a <c>#rgb</c> / <c>#rrggbb</c> fill; null for none, inherit or unparseable.</summary>
    /// <param name="value">A <c>fill</c> attribute value, or null.</param>
    public static Color? ParseFill(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsNoneFill(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Color.TryParse(text, out var colour) ? colour : null;
    }

    private static bool IsNoneFill(string? value) =>
        value is not null && value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

    private static double ParseOpacity(string? value) =>
        TryNumber(value, out var v) ? Math.Clamp(v, 0.0, 1.0) : 1.0;

    private static double ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var text = value.Trim();
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2];
        }

        return TryNumber(text, out var v) ? v : 0;
    }

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
