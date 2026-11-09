using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.Services;

namespace VideoEditor.Models;

public enum DesignKind { Shape, Background, Animated }

/// <summary>A shape or background design. <c>Design</c> is the geometry / pattern name, <c>Style</c> only applies to shapes.</summary>
public sealed record DesignAsset(string Name, DesignKind Kind, string Design, string Palette, string Style, string ColorA, string ColorB);

/// <summary>What is carried while a design card is dragged onto the timeline or the preview.</summary>
public sealed record DesignDrag(DesignAsset Asset, string Motion, double Duration);

/// <summary>Premium vector shapes and background designs, generated locally as PNG files (no downloads, no GPU).</summary>
public static class DesignCatalog
{
    const string Version = "v1";

    static readonly (string Name, string A, string B)[] Palettes =
    {
        ("Gold", "#FFD86B", "#E39A1B"), ("Rose Gold", "#F6C1B8", "#C97B84"), ("Aqua", "#5CEBFF", "#1783D8"),
        ("Violet", "#B58CFF", "#5B3DF5"), ("Sunset", "#FF9A5C", "#F0407F"), ("Emerald", "#6CF0B8", "#0E9B6A"),
        ("Ruby", "#FF6B7F", "#B3122E"), ("Midnight", "#4B5BA8", "#141A3D"), ("Platinum", "#F2F5FA", "#9AA6B8")
    };

    public static readonly string[] ShapeNames =
    {
        "Circle", "Rounded square", "Pill bar", "Tag", "Diamond", "Triangle", "Hexagon", "Star", "Burst", "Sparkle",
        "Heart", "Drop", "Shield", "Blob", "Arrow", "Chevron", "Plus", "Ring", "Speech bubble"
    };

    public static readonly string[] BackgroundNames =
    {
        "Aurora", "Mesh gradient", "Spotlight", "Diagonal gradient", "Radial glow", "Stripes",
        "Dots", "Tech grid", "Waves", "Bokeh", "Sunburst", "Glass cards"
    };

    public static readonly string[] ShapeStyles = { "Gradient", "Glass", "Neon" };

    /// <summary>Looping motion styles for animated backgrounds (rendered to MP4 with FFmpeg filters).</summary>
    public static readonly string[] AnimatedStyles = { "Color flow", "Drift", "Swirl", "Breathe", "Shimmer" };

    /// <summary>"None" plus the motion families the timeline animation engine understands.</summary>
    public static readonly string[] MotionFamilies =
    {
        "None", "Pop", "Bounce", "Zoom In", "Spin In", "Bubble Pop", "Fade In", "Slide Left", "Slide Right", "Slide Up",
        "Slide Down", "Pulse", "Elastic", "Float", "Drift", "Rise", "Drop", "Grow", "Twist", "Flip", "Wipe", "Shake"
    };

    public static IReadOnlyList<string> PaletteNames { get; } = Palettes.Select(p => p.Name).ToArray();

    public static IReadOnlyList<DesignAsset> Shapes { get; } = ShapeNames
        .SelectMany(s => Palettes.SelectMany(p => ShapeStyles.Select(st =>
            new DesignAsset($"{s} · {p.Name} · {st}", DesignKind.Shape, s, p.Name, st, p.A, p.B)))).ToArray();

    public static IReadOnlyList<DesignAsset> Backgrounds { get; } = BackgroundNames
        .SelectMany(b => Palettes.Select(p => new DesignAsset($"{b} · {p.Name}", DesignKind.Background, b, p.Name, "", p.A, p.B))).ToArray();

    public static IReadOnlyList<DesignAsset> Animated { get; } = BackgroundNames
        .SelectMany(b => Palettes.SelectMany(p => AnimatedStyles.Select(st =>
            new DesignAsset($"{b} · {p.Name} · {st}", DesignKind.Animated, b, p.Name, st, p.A, p.B)))).ToArray();

    /// <summary>The still background an animated one is built from (keeps the look identical).</summary>
    static DesignAsset StaticTwin(DesignAsset a)
        => new($"{a.Design} · {a.Palette}", DesignKind.Background, a.Design, a.Palette, "", a.ColorA, a.ColorB);

    public static string MotionPreset(string family) => family == "None" ? "None" : $"{family} · 03";

    // ------------------------------------------------------------------ files / thumbnails
    static readonly ConcurrentDictionary<string, BitmapSource> Thumbs = new();

    /// <summary>Small preview used by the gallery cards.</summary>
    public static BitmapSource Thumbnail(DesignAsset a, int aspectIndex = 0)
        => Thumbs.GetOrAdd(a.Name + "|" + (a.Kind == DesignKind.Background ? aspectIndex : 0),
            _ => a.Kind == DesignKind.Shape ? RenderShape(a, 132)
               : RenderBackground(a.Kind == DesignKind.Animated ? StaticTwin(a) : a, aspectIndex, 200));

    /// <summary>Full-size PNG on disk (cached). Backgrounds follow the project aspect (0 = 16:9, 1 = 9:16, 2 = 1:1).</summary>
    public static string CreateAsset(DesignAsset a, int aspectIndex = 0)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "designs");
        Directory.CreateDirectory(folder);
        string key = $"{a.Name}|{Version}|{(a.Kind == DesignKind.Background ? aspectIndex : 0)}";
        string file = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20] + ".png");
        if (File.Exists(file)) return file;

        BitmapSource bitmap = a.Kind == DesignKind.Shape ? RenderShape(a, 512) : RenderBackground(a, aspectIndex, 1920);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
        return file;
    }

    /// <summary>
    /// Renders a seamless looping MP4 (first use only, cached). All motion is built from FFmpeg filters over the still
    /// background, so it needs no GPU and the file works as a normal clip or overlay on the timeline.
    /// </summary>
    public static async Task<string> CreateAnimatedAsync(DesignAsset a, int aspectIndex, double seconds)
    {
        seconds = Math.Clamp(Math.Round(seconds), 2, 20);
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "designs");
        Directory.CreateDirectory(folder);
        string key = $"{a.Name}|{Version}|anim|{aspectIndex}|{seconds}";
        string file = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20] + ".mp4");
        if (File.Exists(file) && new FileInfo(file).Length > 4096) return file;

        string still = CreateAsset(StaticTwin(a), aspectIndex);
        int w, h;
        if (aspectIndex == 1) { w = 1080; h = 1920; } else if (aspectIndex == 2) { w = h = 1080; } else { w = 1920; h = 1080; }
        string temp = file + ".tmp.mp4";
        try { if (File.Exists(temp)) File.Delete(temp); } catch { }

        var psi = new ProcessStartInfo(FFmpegService.ResolveTool("ffmpeg")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-loop", "1", "-framerate", "30", "-i", still,
            "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-vf", AnimationChain(a.Style, w, h, seconds) + ",format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-r", "30", "-an", "-movflags", "+faststart", temp
        }) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(temp))
            throw new InvalidOperationException("FFmpeg could not render the animated background. " + error);
        File.Move(temp, file, true);
        return file;
    }

    /// <summary>Every style loops seamlessly: all sine periods divide the clip length.</summary>
    static string AnimationChain(string style, int w, int h, double seconds)
    {
        string L = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        static int Even(double v) => (int)v / 2 * 2;
        string drift = $"scale={Even(w * 1.3)}:{Even(h * 1.3)}:flags=bicubic,crop={w}:{h}" +
                       $":x=(iw-ow)/2*(1+0.9*sin(2*PI*t/{L})):y=(ih-oh)/2*(1+0.9*sin(4*PI*t/{L}))";
        string hue = $"hue=h=45*sin(2*PI*t/{L})";
        return style switch
        {
            "Drift" => drift,
            "Swirl" => $"scale={Even(w * 1.5)}:{Even(h * 1.5)}:flags=bicubic,rotate=a=0.06*sin(2*PI*t/{L}):c=black,crop={w}:{h}",
            "Breathe" => $"scale={Even(w * 1.6)}:{Even(h * 1.6)}:flags=bicubic," +
                         $"zoompan=z=1.12+0.08*sin(2*PI*on/(30*{L})):x=iw/2-(iw/zoom/2):y=ih/2-(ih/zoom/2):d=1:s={w}x{h}:fps=30",
            "Shimmer" => drift + $",hue=h=30*sin(2*PI*t/{L}),eq=brightness=0.04*sin(2*PI*t/{L}):eval=frame",
            _ => hue   // "Color flow"
        };
    }

    /// <summary>Width / height of the generated image (used for the overlay aspect ratio).</summary>
    public static double AspectOf(DesignAsset a, int aspectIndex = 0)
    {
        if (a.Kind != DesignKind.Shape) return aspectIndex == 1 ? 9.0 / 16 : aspectIndex == 2 ? 1.0 : 16.0 / 9;
        var (w, h, _, _) = ShapeLayout(BuildShape(a.Design).Bounds, 512);
        return (double)w / h;
    }

    // ------------------------------------------------------------------ colour helpers
    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    static Color Alpha(int a, Color c) => Color.FromArgb((byte)Math.Clamp(a, 0, 255), c.R, c.G, c.B);
    static Color Lift(Color c, int d) => Color.FromRgb((byte)Math.Min(255, c.R + d), (byte)Math.Min(255, c.G + d), (byte)Math.Min(255, c.B + d));
    static Color Dim(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Pen MakePen(Brush b, double t) => new(b, t) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    static int Seed(string text) => BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);

    static BitmapSource ToBitmap(DrawingVisual visual, int w, int h)
    {
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ------------------------------------------------------------------ shapes
    static Geometry Poly(params (double X, double Y)[] p)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(p[0].X, p[0].Y), true, true);
            c.PolyLineTo(p.Skip(1).Select(q => new Point(q.X, q.Y)).ToList(), true, true);
        }
        return g;
    }

    static Geometry StarPoly(int points, double outer, double inner)
    {
        var pts = new (double, double)[points * 2];
        for (int i = 0; i < pts.Length; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / points;
            double r = i % 2 == 0 ? outer : inner;
            pts[i] = (128 + r * Math.Cos(angle), 128 + r * Math.Sin(angle));
        }
        return Poly(pts);
    }

    static Geometry RegularPoly(int sides, double radius)
    {
        var pts = new (double, double)[sides];
        for (int i = 0; i < sides; i++)
        {
            double angle = -Math.PI / 2 + i * 2 * Math.PI / sides;
            pts[i] = (128 + radius * Math.Cos(angle), 128 + radius * Math.Sin(angle));
        }
        return Poly(pts);
    }

    static Geometry Path_(Action<StreamGeometryContext> draw)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) draw(c);
        return g;
    }

    static Geometry BuildShape(string name) => name switch
    {
        "Circle" => new EllipseGeometry(new Point(128, 128), 104, 104),
        "Rounded square" => new RectangleGeometry(new Rect(28, 28, 200, 200), 46, 46),
        "Pill bar" => new RectangleGeometry(new Rect(0, 0, 360, 84), 42, 42),
        "Tag" => Poly((70, 0), (360, 0), (300, 96), (10, 96)),
        "Diamond" => Poly((128, 14), (242, 128), (128, 242), (14, 128)),
        "Triangle" => Poly((128, 22), (240, 222), (16, 222)),
        "Hexagon" => RegularPoly(6, 112),
        "Star" => StarPoly(5, 116, 48),
        "Burst" => StarPoly(14, 118, 84),
        "Sparkle" => Poly((128, 8), (154, 102), (248, 128), (154, 154), (128, 248), (102, 154), (8, 128), (102, 102)),
        "Heart" => Path_(c =>
        {
            c.BeginFigure(new Point(128, 228), true, true);
            c.BezierTo(new Point(14, 152), new Point(18, 56), new Point(78, 48), true, true);
            c.BezierTo(new Point(108, 44), new Point(124, 64), new Point(128, 84), true, true);
            c.BezierTo(new Point(132, 64), new Point(148, 44), new Point(178, 48), true, true);
            c.BezierTo(new Point(238, 56), new Point(242, 152), new Point(128, 228), true, true);
        }),
        "Drop" => Path_(c =>
        {
            c.BeginFigure(new Point(128, 14), true, true);
            c.BezierTo(new Point(128, 14), new Point(214, 112), new Point(214, 160), true, true);
            c.ArcTo(new Point(42, 160), new Size(86, 86), 0, true, SweepDirection.Clockwise, true, true);
            c.BezierTo(new Point(42, 112), new Point(128, 14), new Point(128, 14), true, true);
        }),
        "Shield" => Path_(c =>
        {
            c.BeginFigure(new Point(128, 12), true, true);
            c.LineTo(new Point(230, 50), true, true);
            c.LineTo(new Point(230, 128), true, true);
            c.BezierTo(new Point(230, 186), new Point(182, 224), new Point(128, 246), true, true);
            c.BezierTo(new Point(74, 224), new Point(26, 186), new Point(26, 128), true, true);
            c.LineTo(new Point(26, 50), true, true);
        }),
        "Blob" => Path_(c =>
        {
            c.BeginFigure(new Point(58, 72), true, true);
            c.BezierTo(new Point(100, 8), new Point(204, 18), new Point(224, 90), true, true);
            c.BezierTo(new Point(250, 164), new Point(192, 244), new Point(120, 234), true, true);
            c.BezierTo(new Point(38, 224), new Point(8, 124), new Point(58, 72), true, true);
        }),
        "Arrow" => Poly((14, 92), (150, 92), (150, 34), (244, 128), (150, 222), (150, 164), (14, 164)),
        "Chevron" => Poly((36, 24), (112, 24), (214, 128), (112, 232), (36, 232), (138, 128)),
        "Plus" => Poly((90, 22), (166, 22), (166, 90), (234, 90), (234, 166), (166, 166), (166, 234), (90, 234), (90, 166), (22, 166), (22, 90), (90, 90)),
        "Ring" => new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new EllipseGeometry(new Point(128, 128), 110, 110), new EllipseGeometry(new Point(128, 128), 74, 74) }
        },
        "Speech bubble" => new CombinedGeometry(GeometryCombineMode.Union,
            new RectangleGeometry(new Rect(14, 24, 228, 148), 38, 38),
            Poly((62, 160), (50, 238), (124, 168))),
        _ => new EllipseGeometry(new Point(128, 128), 104, 104)
    };

    static (int W, int H, double Scale, double Pad) ShapeLayout(Rect bounds, int maxPx)
    {
        double pad = maxPx * 0.07;
        double scale = (maxPx - 2 * pad) / Math.Max(bounds.Width, bounds.Height);
        return ((int)Math.Ceiling(bounds.Width * scale + 2 * pad), (int)Math.Ceiling(bounds.Height * scale + 2 * pad), scale, pad);
    }

    static BitmapSource RenderShape(DesignAsset a, int maxPx)
    {
        var geometry = BuildShape(a.Design);
        var b = geometry.Bounds;
        var (w, h, scale, pad) = ShapeLayout(b, maxPx);
        Color ca = Parse(a.ColorA), cb = Parse(a.ColorB);
        double px = maxPx / 512.0;               // 1 "design pixel" at 512 px output
        double T(double v) => v * px / scale;    // pen thickness in geometry units

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var group = new TransformGroup();
            group.Children.Add(new TranslateTransform(-b.X, -b.Y));
            group.Children.Add(new ScaleTransform(scale, scale));
            group.Children.Add(new TranslateTransform(pad, pad));
            dc.PushTransform(group);

            var highlightRect = new Rect(b.X, b.Y, b.Width, b.Height * 0.55);
            switch (a.Style)
            {
                case "Glass":
                {
                    var fill = new LinearGradientBrush(Alpha(150, Lift(ca, 20)), Alpha(48, cb), 70);
                    dc.DrawGeometry(fill, MakePen(Solid(Alpha(230, Color.FromRgb(255, 255, 255))), T(3.5)), geometry);
                    dc.PushClip(geometry);
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90), null, highlightRect);
                    dc.Pop();
                    break;
                }
                case "Neon":
                {
                    for (int i = 0; i < 5; i++)
                        dc.DrawGeometry(null, MakePen(Solid(Alpha(16 + i * 16, ca)), T(26 - i * 5)), geometry);
                    dc.DrawGeometry(Solid(Alpha(34, ca)), MakePen(Solid(Lift(ca, 110)), T(5)), geometry);
                    break;
                }
                default:
                {
                    // soft drop shadow, metallic body, light rim and top gloss
                    dc.PushTransform(new TranslateTransform(T(4), T(8)));
                    dc.DrawGeometry(Solid(Color.FromArgb(70, 0, 0, 0)), null, geometry);
                    dc.Pop();
                    var body = new LinearGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(Lift(ca, 25), 0.0), new GradientStop(ca, 0.35), new GradientStop(cb, 1.0)
                    }, 62);
                    dc.DrawGeometry(body, MakePen(Solid(Lift(ca, 90)), T(3.5)), geometry);
                    dc.PushClip(geometry);
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90), null, highlightRect);
                    dc.Pop();
                    break;
                }
            }
            dc.Pop();
        }
        return ToBitmap(visual, w, h);
    }

    // ------------------------------------------------------------------ backgrounds
    static BitmapSource RenderBackground(DesignAsset a, int aspectIndex, int longEdge)
    {
        double ar = aspectIndex == 1 ? 9.0 / 16 : aspectIndex == 2 ? 1.0 : 16.0 / 9;
        int w, h;
        if (aspectIndex == 2) { w = h = (int)Math.Round(longEdge * 9.0 / 16); }
        else if (ar >= 1) { w = longEdge; h = (int)Math.Round(longEdge / ar); }
        else { h = longEdge; w = (int)Math.Round(longEdge * ar); }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            DrawBackground(dc, a.Design, w, h, Parse(a.ColorA), Parse(a.ColorB), new Random(Seed(a.Name)));
        return ToBitmap(visual, w, h);
    }

    static RadialGradientBrush Glow(Color c, int centerAlpha, int edgeAlpha = 0) => new(Alpha(centerAlpha, c), Alpha(edgeAlpha, c));

    static void Blob(DrawingContext dc, double cx, double cy, double rx, double ry, Color c, int alpha)
        => dc.DrawEllipse(Glow(c, alpha), null, new Point(cx, cy), rx, ry);

    static Geometry Fig(Point start, IEnumerable<Point> points)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(start, true, true);
            c.PolyLineTo(points.ToList(), false, true);
        }
        return g;
    }

    static void DrawBackground(DrawingContext dc, string design, int W, int H, Color ca, Color cb, Random rnd)
    {
        var full = new Rect(0, 0, W, H);
        double S = Math.Max(W, H);
        Color dark = Dim(cb, 0.22), night = Color.FromRgb(8, 10, 20);

        switch (design)
        {
            case "Aurora":
                dc.DrawRectangle(new LinearGradientBrush(Dim(cb, 0.30), night, 90), null, full);
                Blob(dc, W * 0.18, H * 0.25, S * 0.55, S * 0.40, ca, 190);
                Blob(dc, W * 0.80, H * 0.18, S * 0.50, S * 0.36, cb, 200);
                Blob(dc, W * 0.52, H * 0.85, S * 0.60, S * 0.38, Mix(ca, cb, 0.5), 170);
                Blob(dc, W * 0.92, H * 0.78, S * 0.35, S * 0.30, Lift(ca, 40), 120);
                break;

            case "Mesh gradient":
            {
                dc.DrawRectangle(Solid(Dim(Mix(ca, cb, 0.5), 0.55)), null, full);
                var meshTones = new[] { ca, cb, Lift(ca, 45), Dim(cb, 0.7), Mix(ca, cb, 0.5) };
                for (int i = 0; i < 7; i++)
                    Blob(dc, rnd.NextDouble() * W, rnd.NextDouble() * H, S * (0.30 + rnd.NextDouble() * 0.35), S * (0.25 + rnd.NextDouble() * 0.30), meshTones[i % meshTones.Length], 210);
                break;
            }

            case "Spotlight":
                dc.DrawRectangle(Solid(Mix(night, dark, 0.7)), null, full);
                Blob(dc, W * 0.5, -H * 0.05, S * 0.75, S * 0.62, ca, 230);
                Blob(dc, W * 0.5, H * 0.05, S * 0.32, S * 0.28, Lift(ca, 90), 150);
                dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(190, 0, 0, 0), 90), null, new Rect(0, H * 0.55, W, H * 0.45));
                break;

            case "Diagonal gradient":
                dc.DrawRectangle(new LinearGradientBrush(ca, cb, 35), null, full);
                Blob(dc, W * 0.15, H * 0.1, S * 0.55, S * 0.4, Color.FromRgb(255, 255, 255), 55);
                break;

            case "Radial glow":
                dc.DrawRectangle(Solid(dark), null, full);
                dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(ca, 0.0), new GradientStop(Alpha(225, cb), 0.5), new GradientStop(Alpha(0, cb), 1.0)
                }), null, new Point(W / 2, H / 2), S * 0.72, S * 0.55);
                break;

            case "Stripes":
                dc.DrawRectangle(new LinearGradientBrush(ca, cb, 60), null, full);
                dc.PushTransform(new RotateTransform(-22, W / 2, H / 2));
                for (double x = -W; x < W * 2; x += S / 9)
                    dc.DrawRectangle(Solid(Color.FromArgb(34, 255, 255, 255)), null, new Rect(x, -H, S / 20, H * 3));
                dc.Pop();
                break;

            case "Dots":
            {
                dc.DrawRectangle(new LinearGradientBrush(Dim(cb, 0.45), night, 60), null, full);
                double step = S / 30; var dot = Solid(Alpha(120, ca));
                for (double y = step / 2; y < H; y += step)
                    for (double x = step / 2; x < W; x += step)
                    {
                        double dist = Math.Sqrt(Math.Pow((x - W / 2) / W, 2) + Math.Pow((y - H / 2) / H, 2));
                        double r = step * 0.34 * Math.Clamp(1.15 - dist * 1.6, 0.12, 1.0);
                        dc.DrawEllipse(dot, null, new Point(x, y), r, r);
                    }
                break;
            }

            case "Tech grid":
            {
                dc.DrawRectangle(new LinearGradientBrush(Dim(cb, 0.35), night, 90), null, full);
                Blob(dc, W * 0.85, H * 0.1, S * 0.5, S * 0.35, ca, 120);
                Blob(dc, W * 0.1, H * 0.95, S * 0.45, S * 0.30, cb, 140);
                var pen = new Pen(Solid(Alpha(46, Lift(ca, 40))), Math.Max(1, S / 1400));
                double step = S / 32;
                for (double x = 0; x <= W; x += step) dc.DrawLine(pen, new Point(x, 0), new Point(x, H));
                for (double y = 0; y <= H; y += step) dc.DrawLine(pen, new Point(0, y), new Point(W, y));
                break;
            }

            case "Waves":
            {
                dc.DrawRectangle(new LinearGradientBrush(ca, cb, 90), null, full);
                for (int layer = 0; layer < 4; layer++)
                {
                    double baseline = H * (0.50 + layer * 0.11), amp = H * (0.05 + layer * 0.008), freq = 1.2 + layer * 0.55, phase = rnd.NextDouble() * 6;
                    var pts = new List<Point>();
                    for (int i = 0; i <= 80; i++)
                    {
                        double x = W * i / 80.0;
                        pts.Add(new Point(x, baseline + Math.Sin(x / W * Math.PI * 2 * freq + phase) * amp));
                    }
                    pts.Add(new Point(W, H)); pts.Add(new Point(0, H));
                    dc.DrawGeometry(Solid(Alpha(48 + layer * 28, layer % 2 == 0 ? Color.FromRgb(255, 255, 255) : dark)), null, Fig(pts[0], pts.Skip(1)));
                }
                break;
            }

            case "Bokeh":
            {
                dc.DrawRectangle(new LinearGradientBrush(Dim(cb, 0.30), Dim(ca, 0.16), 60), null, full);
                var tones = new[] { ca, cb, Color.FromRgb(255, 255, 255) };
                for (int i = 0; i < 30; i++)
                {
                    double r = H * (0.03 + rnd.NextDouble() * 0.10);
                    var c = tones[rnd.Next(tones.Length)];
                    int al = 40 + rnd.Next(70);
                    var center = new Point(rnd.NextDouble() * W, rnd.NextDouble() * H);
                    dc.DrawEllipse(new RadialGradientBrush(Alpha(al / 3, c), Alpha(al, c)), new Pen(Solid(Alpha(70, c)), Math.Max(1, S / 1200)), center, r, r);
                }
                break;
            }

            case "Sunburst":
            {
                dc.DrawRectangle(Solid(dark), null, full);
                dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(ca, 0.0), new GradientStop(cb, 0.6), new GradientStop(Dim(cb, 0.4), 1.0)
                }), null, new Point(W / 2, H / 2), S * 0.8, S * 0.8);
                int rays = 28; double step = 2 * Math.PI / rays; var ray = Solid(Color.FromArgb(30, 255, 255, 255));
                for (int i = 0; i < rays; i += 2)
                {
                    double a0 = i * step, a1 = (i + 1) * step, far = S * 1.6;
                    dc.DrawGeometry(ray, null, Fig(new Point(W / 2, H / 2), new[]
                    {
                        new Point(W / 2 + far * Math.Cos(a0), H / 2 + far * Math.Sin(a0)),
                        new Point(W / 2 + far * Math.Cos(a1), H / 2 + far * Math.Sin(a1))
                    }));
                }
                break;
            }

            default: // "Glass cards"
            {
                dc.DrawRectangle(new LinearGradientBrush(ca, cb, 40), null, full);
                Blob(dc, W * 0.22, H * 0.30, S * 0.35, S * 0.30, Lift(ca, 60), 170);
                Blob(dc, W * 0.80, H * 0.78, S * 0.40, S * 0.32, Dim(cb, 0.6), 190);
                var glass = new LinearGradientBrush(Color.FromArgb(86, 255, 255, 255), Color.FromArgb(20, 255, 255, 255), 60);
                var rim = new Pen(Solid(Color.FromArgb(120, 255, 255, 255)), Math.Max(1.5, S / 900));
                double radius = S * 0.025;
                dc.DrawRoundedRectangle(glass, rim, new Rect(W * 0.07, H * 0.12, W * 0.40, H * 0.76), radius, radius);
                dc.DrawRoundedRectangle(glass, rim, new Rect(W * 0.53, H * 0.12, W * 0.40, H * 0.34), radius, radius);
                dc.DrawRoundedRectangle(glass, rim, new Rect(W * 0.53, H * 0.54, W * 0.40, H * 0.34), radius, radius);
                break;
            }
        }
    }
}
