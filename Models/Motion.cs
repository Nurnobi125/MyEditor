using System.Globalization;
using System.Text;

namespace VideoEditor.Models;

/// <summary>One keyframe of a single property. Time = seconds from the start of the clip (after speed).
/// Easing controls the curve of the segment that STARTS at this key.</summary>
public class PropKey
{
    public double Time { get; set; }
    public double Value { get; set; }
    public string Easing { get; set; } = "Ease in-out";
}

/// <summary>Where the 9:16 (or any) crop window should look. SourceTime is absolute seconds in the source file.</summary>
public class ReframePoint
{
    public double SourceTime { get; set; }
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
}

public static class EasingCurves
{
    public static readonly string[] Names = { "Linear", "Ease in", "Ease out", "Ease in-out", "Hold" };

    public static double Apply(double u, string? name)
    {
        u = Math.Clamp(u, 0, 1);
        return name switch
        {
            "Ease in" => u * u,
            "Ease out" => 1 - (1 - u) * (1 - u),
            "Ease in-out" => u * u * (3 - 2 * u),
            "Hold" => u < 1 ? 0 : 1,
            _ => u
        };
    }

    /// <summary>Same curve as <see cref="Apply"/>, as an ffmpeg expression over the already-clamped 0..1 expression <paramref name="u"/>.</summary>
    public static string Expr(string u, string? name) => name switch
    {
        "Ease in" => $"(({u})*({u}))",
        "Ease out" => $"(1-(1-({u}))*(1-({u})))",
        "Ease in-out" => $"(({u})*({u})*(3-2*({u})))",
        "Hold" => "0",
        _ => $"({u})"
    };
}

public sealed record ClipPropInfo(string Name, double Default, double Min, double Max, double Step, string Hint);

/// <summary>Per-property keyframes for main-track (V1) clips: zoom, pan, rotation, colour and volume.</summary>
public static class ClipMotion
{
    public const string Zoom = "Zoom", PanX = "Pan X", PanY = "Pan Y", Rotation = "Rotation",
        Brightness = "Brightness", Contrast = "Contrast", Saturation = "Saturation", Volume = "Volume";

    public static readonly ClipPropInfo[] Props =
    {
        new(Zoom, 1, 1, 3, 0.01, "1 = full frame, 2 = twice as close"),
        new(PanX, 0.5, 0, 1, 0.005, "Which part of the frame to centre on when zoomed (0 left · 1 right)"),
        new(PanY, 0.5, 0, 1, 0.005, "Which part of the frame to centre on when zoomed (0 top · 1 bottom)"),
        new(Rotation, 0, -180, 180, 0.5, "Degrees, clockwise. Empty corners show black"),
        new(Brightness, 0, -0.5, 0.5, 0.01, "Same range as the Brightness slider"),
        new(Contrast, 1, 0.5, 1.5, 0.01, "Same range as the Contrast slider"),
        new(Saturation, 1, 0, 2, 0.01, "Same range as the Saturation slider"),
        new(Volume, 1, 0, 2, 0.01, "Same range as the Volume slider"),
    };

    public static ClipPropInfo Info(string name) => Props.FirstOrDefault(p => p.Name == name) ?? Props[0];

    public static bool HasKeys(this Clip c) => c.Keys.Any(k => k.Value.Count > 0);
    public static bool HasKeys(this Clip c, string prop) => c.Keys.TryGetValue(prop, out var l) && l.Count > 0;
    public static bool HasVisualKeys(this Clip c) =>
        new[] { Zoom, PanX, PanY, Rotation, Brightness, Contrast, Saturation }.Any(c.HasKeys);

    /// <summary>Value of a property at a time measured from the start of the clip (after speed).</summary>
    public static double ValueAt(this Clip c, string prop, double localTime)
    {
        double def = c.BaseValue(prop);
        if (!c.Keys.TryGetValue(prop, out var list) || list.Count == 0) return def;
        var k = list.OrderBy(x => x.Time).ToList();
        if (k.Count == 1 || localTime <= k[0].Time) return k[0].Value;
        for (int i = 1; i < k.Count; i++)
        {
            if (localTime < k[i].Time)
            {
                var a = k[i - 1]; var b = k[i];
                double u = (localTime - a.Time) / Math.Max(0.05, b.Time - a.Time);
                return a.Value + (b.Value - a.Value) * EasingCurves.Apply(u, a.Easing);
            }
        }
        return k[^1].Value;
    }

    /// <summary>The un-animated value (the slider value) of a property.</summary>
    public static double BaseValue(this Clip c, string prop) => prop switch
    {
        Brightness => c.Brightness,
        Contrast => c.Contrast,
        Saturation => c.Saturation,
        Volume => c.Volume,
        _ => Info(prop).Default
    };

    public static void SetKey(this Clip c, string prop, double time, double value, string easing)
    {
        if (!c.Keys.TryGetValue(prop, out var list)) c.Keys[prop] = list = new List<PropKey>();
        var existing = list.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.05);
        if (existing == null) list.Add(existing = new PropKey { Time = time });
        existing.Value = value;
        existing.Easing = easing;
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    public static bool RemoveKeyNear(this Clip c, string prop, double time)
    {
        if (!c.Keys.TryGetValue(prop, out var list)) return false;
        int n = list.RemoveAll(k => Math.Abs(k.Time - time) < 0.05);
        if (list.Count == 0) c.Keys.Remove(prop);
        return n > 0;
    }

    static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>After a clip is cut at <paramref name="local"/> seconds (left and right are still identical clones),
    /// keep the early keys on the left half and re-time the later keys to the start of the right half.</summary>
    public static void SplitKeys(Clip left, Clip right, double local)
    {
        var all = left.Keys.ToDictionary(k => k.Key, k => k.Value.OrderBy(x => x.Time).ToList());
        left.Keys = new Dictionary<string, List<PropKey>>();
        right.Keys = new Dictionary<string, List<PropKey>>();
        foreach (var (prop, keys) in all)
        {
            if (keys.Count == 0) continue;
            double v = left.ValueAtList(keys, local);
            var l = keys.Where(k => k.Time < local - 0.01).Select(k => new PropKey { Time = k.Time, Value = k.Value, Easing = k.Easing }).ToList();
            l.Add(new PropKey { Time = local, Value = v, Easing = "Linear" });
            var r = new List<PropKey> { new PropKey { Time = 0, Value = v, Easing = keys.LastOrDefault(k => k.Time <= local)?.Easing ?? "Linear" } };
            r.AddRange(keys.Where(k => k.Time > local + 0.01).Select(k => new PropKey { Time = k.Time - local, Value = k.Value, Easing = k.Easing }));
            left.Keys[prop] = l;
            if (r.Count > 1) right.Keys[prop] = r;
        }
    }

    static double ValueAtList(this Clip c, List<PropKey> k, double t)
    {
        if (k.Count == 1 || t <= k[0].Time) return k[0].Value;
        for (int i = 1; i < k.Count; i++)
            if (t < k[i].Time)
            {
                var a = k[i - 1]; var b = k[i];
                return a.Value + (b.Value - a.Value) * EasingCurves.Apply((t - a.Time) / Math.Max(0.05, b.Time - a.Time), a.Easing);
            }
        return k[^1].Value;
    }

    /// <summary>Piecewise ffmpeg expression in clip-local time <paramref name="tv"/> (seconds from clip start).</summary>
    public static string Expr(this Clip c, string prop, string tv = "t")
    {
        double def = c.BaseValue(prop);
        if (!c.Keys.TryGetValue(prop, out var list) || list.Count == 0) return F(def);
        return ExprFromKeys(list, tv);
    }

    public static string ExprFromKeys(IEnumerable<PropKey> list, string tv = "t")
    {
        var k = list.OrderBy(x => x.Time).ToList();
        if (k.Count == 0) return "0";
        if (k.Count == 1) return F(k[0].Value);
        string e = F(k[^1].Value);
        for (int i = k.Count - 1; i >= 1; i--)
        {
            var a = k[i - 1]; var b = k[i];
            double span = Math.Max(0.05, b.Time - a.Time);
            string u = $"clip(({tv}-{F(a.Time)})/{F(span)},0,1)";
            e = $"if(lt({tv},{F(b.Time)}),{F(a.Value)}+({F(b.Value - a.Value)})*{EasingCurves.Expr(u, a.Easing)},{e})";
        }
        return $"if(lt({tv},{F(k[0].Time)}),{F(k[0].Value)},{e})";
    }

    /// <summary>Same as <see cref="Expr(Clip,string,string)"/> for generic overlay keyframes.</summary>
    public static string OverlayExpr(IEnumerable<Keyframe> keys, double start, Func<Keyframe, double> sel, string tv = "t")
    {
        var k = keys.OrderBy(q => q.Time).ToList();
        string e = F(sel(k[^1]));
        for (int i = k.Count - 1; i >= 1; i--)
        {
            var a = k[i - 1]; var b = k[i];
            double ta = start + a.Time, tb = start + b.Time;
            double span = Math.Max(0.05, tb - ta);
            string u = $"clip(({tv}-{F(ta)})/{F(span)},0,1)";
            e = $"if(lt({tv},{F(tb)}),{F(sel(a))}+({F(sel(b) - sel(a))})*{EasingCurves.Expr(u, a.Easing)},{e})";
        }
        return $"if(lt({tv},{F(start + k[0].Time)}),{F(sel(k[0]))},{e})";
    }
}

/// <summary>Speed-curve presets. A curve is applied by cutting the clip into a few contiguous pieces that each
/// have their own constant speed, so preview, trimming and export all keep working unchanged.</summary>
public static class SpeedCurves
{
    // (position along the trimmed source range 0..1, speed)
    static readonly Dictionary<string, (double Pos, double Speed)[]> Presets = new()
    {
        ["Montage"] = new[] { (0.0, 1.0), (0.20, 1.0), (0.38, 0.35), (0.55, 2.8), (0.72, 0.35), (0.88, 1.0), (1.0, 1.0) },
        ["Hero"] = new[] { (0.0, 1.0), (0.30, 1.0), (0.45, 0.30), (0.62, 0.30), (0.78, 2.0), (1.0, 1.0) },
        ["Bullet"] = new[] { (0.0, 1.0), (0.40, 1.0), (0.48, 0.20), (0.70, 0.20), (0.80, 1.0), (1.0, 1.0) },
        ["Jumper"] = new[] { (0.0, 2.2), (0.25, 0.5), (0.50, 2.2), (0.75, 0.5), (1.0, 2.2) },
        ["Ramp up"] = new[] { (0.0, 0.5), (1.0, 3.0) },
        ["Ramp down"] = new[] { (0.0, 3.0), (1.0, 0.5) },
    };

    public static readonly string[] Names = Presets.Keys.ToArray();

    public static double SpeedAt(string preset, double pos)
    {
        var pts = Presets[preset];
        if (pos <= pts[0].Pos) return pts[0].Speed;
        for (int i = 1; i < pts.Length; i++)
        {
            if (pos <= pts[i].Pos)
            {
                double u = (pos - pts[i - 1].Pos) / Math.Max(1e-6, pts[i].Pos - pts[i - 1].Pos);
                u = u * u * (3 - 2 * u); // smooth ramp between nodes
                return pts[i - 1].Speed + (pts[i].Speed - pts[i - 1].Speed) * u;
            }
        }
        return pts[^1].Speed;
    }

    /// <summary>Cuts <paramref name="source"/> into contiguous pieces with stepped speeds. Source must be a video clip.</summary>
    public static List<Clip> Apply(Clip source, string preset, int maxPieces = 14)
    {
        double length = source.Out - source.In;
        int n = Math.Clamp((int)Math.Floor(length / 0.15), 2, maxPieces);
        var pieces = new List<Clip>(n);
        for (int i = 0; i < n; i++)
        {
            var piece = source.Clone();
            piece.In = source.In + length * i / n;
            piece.Out = source.In + length * (i + 1) / n;
            piece.Speed = Math.Round(Math.Clamp(SpeedAt(preset, (i + 0.5) / n), 0.2, 4), 2);
            if (i > 0) { piece.TransitionIn = "None"; piece.OverlayText = ""; }
            // Keyframes are timed from the start of the clip; keep them on the piece that owns that moment.
            piece.Keys = new Dictionary<string, List<PropKey>>();
            pieces.Add(piece);
        }
        return pieces;
    }
}
