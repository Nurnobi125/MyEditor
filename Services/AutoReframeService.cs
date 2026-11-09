using System.Diagnostics;
using System.IO;
using System.Globalization;
using VideoEditor.Models;

namespace VideoEditor.Services;

/// <summary>
/// Finds where a vertical (9:16) crop window should look so a landscape clip still shows the action.
/// Screen recordings use the recorded mouse path. Other videos are analysed offline with FFmpeg:
/// a tiny grayscale copy is decoded and the area with the most motion/detail is followed by a smoothed,
/// "virtual camera" path that only moves when the subject really leaves the middle of the crop.
/// This is motion/detail tracking, not face recognition.
/// </summary>
public static class AutoReframeService
{
    const int GridW = 96, GridH = 54;
    const double SampleFps = 6;

    public static async Task<List<ReframePoint>> AnalyzeAsync(Clip clip, IProgress<double>? progress, CancellationToken ct)
    {
        if (clip.IsGap || clip.IsImage) return new List<ReframePoint>();

        var fromMouse = FromPointer(clip);
        if (fromMouse.Count >= 3) return Simplify(Smooth(fromMouse, 1 / 4.0), 40);

        var raw = await AnalyzeMotionAsync(clip, progress, ct);
        if (raw.Count == 0) return new List<ReframePoint>();
        return Simplify(Smooth(raw, 1 / SampleFps), 40);
    }

    // ---------- screen recordings ----------
    static List<(double T, double X, double Y)> FromPointer(Clip clip)
    {
        var events = clip.PointerEvents
            .Where(p => p.TimeSeconds >= clip.In - 0.01 && p.TimeSeconds <= clip.Out + 0.01)
            .OrderBy(p => p.TimeSeconds)
            .ToList();
        var result = new List<(double, double, double)>();
        double lastT = double.NegativeInfinity;
        foreach (var e in events)
        {
            if (e.TimeSeconds - lastT < 0.25) continue; // ~4 samples per second is plenty
            result.Add((e.TimeSeconds, Math.Clamp(e.X, 0, 1), Math.Clamp(e.Y, 0, 1)));
            lastT = e.TimeSeconds;
        }
        return result;
    }

    // ---------- general video ----------
    static async Task<List<(double T, double X, double Y)>> AnalyzeMotionAsync(Clip clip, IProgress<double>? progress, CancellationToken ct)
    {
        double length = Math.Max(0.2, clip.Out - clip.In);
        var psi = new ProcessStartInfo(FFmpegService.ResolveTool("ffmpeg"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[]
        {
            "-v", "error", "-nostdin",
            "-ss", clip.In.ToString("F3", CultureInfo.InvariantCulture),
            "-t", length.ToString("F3", CultureInfo.InvariantCulture),
            "-i", clip.Path, "-an", "-sn",
            "-vf", $"fps={SampleFps.ToString(CultureInfo.InvariantCulture)},scale={GridW}:{GridH}:flags=area,format=gray",
            "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"
        }) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg for reframe analysis.");
        var errorText = process.StandardError.ReadToEndAsync();
        using var cancelRegistration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

        int frameSize = GridW * GridH;
        var stream = process.StandardOutput.BaseStream;
        var current = new byte[frameSize];
        var previous = new byte[frameSize];
        bool havePrevious = false;
        int index = 0;
        double expectedFrames = Math.Max(1, length * SampleFps);
        var samples = new List<(double, double, double)>();
        double lastX = 0.5, lastY = 0.5;

        while (await ReadExactAsync(stream, current, frameSize, ct))
        {
            var (x, y, strength) = Locate(current, havePrevious ? previous : null);
            if (strength > 0.05) { lastX = x; lastY = y; }   // enough activity above the noise floor: trust the measurement
            samples.Add((clip.In + index / SampleFps, lastX, lastY));
            (current, previous) = (previous, current);
            havePrevious = true;
            index++;
            if (index % 12 == 0) progress?.Report(Math.Min(0.98, index / expectedFrames));
        }

        await process.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 && samples.Count == 0)
            throw new InvalidOperationException("FFmpeg could not read this clip: " + (await errorText).Trim());
        progress?.Report(1);
        return samples;
    }

    static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>Centre of the strongest motion/detail cluster in a frame (0..1 coordinates) and how strong it was.</summary>
    internal static (double X, double Y, double Strength) Locate(byte[] frame, byte[]? previous)
    {
        var col = new double[GridW];
        var row = new double[GridH];
        double total = 0;
        for (int y = 0; y < GridH - 1; y++)
        {
            for (int x = 0; x < GridW - 1; x++)
            {
                int i = y * GridW + x;
                // Noise floors: compression noise and sensor grain should not look like a subject.
                double motion = previous == null ? 0 : Math.Max(0, Math.Abs(frame[i] - previous[i]) - 8);
                double detail = Math.Max(0, Math.Abs(frame[i] - frame[i + 1]) + Math.Abs(frame[i] - frame[i + GridW]) - 24);
                double w = motion + detail * 0.15;
                col[x] += w; row[y] += w; total += w;
            }
        }
        double strength = total / (GridW * GridH);
        return (Cluster(col), Cluster(row), strength);
    }

    static double Cluster(double[] values)
    {
        int n = values.Length;
        var blur = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0; int cnt = 0;
            for (int k = -3; k <= 3; k++) { int j = i + k; if (j >= 0 && j < n) { sum += values[j]; cnt++; } }
            blur[i] = sum / cnt;
        }
        int peak = 0;
        for (int i = 1; i < n; i++) if (blur[i] > blur[peak]) peak = i;
        double weighted = 0, weights = 0;
        int radius = Math.Max(4, n / 8);
        for (int i = Math.Max(0, peak - radius); i <= Math.Min(n - 1, peak + radius); i++)
        {
            weighted += (i + 0.5) * blur[i];
            weights += blur[i];
        }
        return weights <= 0 ? 0.5 : Math.Clamp(weighted / weights / n, 0, 1);
    }

    // ---------- smoothing ----------
    /// <summary>Median filter, then a virtual camera with a dead zone and a speed limit, so the crop does not jitter.</summary>
    internal static List<ReframePoint> Smooth(List<(double T, double X, double Y)> raw, double dt)
    {
        int n = raw.Count;
        var xs = Median(raw.Select(r => r.X).ToArray(), 5);
        var ys = Median(raw.Select(r => r.Y).ToArray(), 5);
        const double dead = 0.07, maxSpeed = 0.30; // fraction of frame per second
        double camX = xs[0], camY = ys[0];
        var result = new List<ReframePoint>(n);
        for (int i = 0; i < n; i++)
        {
            camX = Follow(camX, xs[i], dead, maxSpeed * dt * Math.Max(1, (i > 0 ? raw[i].T - raw[i - 1].T : dt) / dt));
            camY = Follow(camY, ys[i], dead, maxSpeed * dt * Math.Max(1, (i > 0 ? raw[i].T - raw[i - 1].T : dt) / dt));
            result.Add(new ReframePoint { SourceTime = raw[i].T, X = Math.Round(camX, 4), Y = Math.Round(camY, 4) });
        }
        return result;
    }

    static double Follow(double cam, double target, double dead, double maxStep)
    {
        double diff = target - cam;
        if (Math.Abs(diff) <= dead) return cam;
        double want = diff - Math.Sign(diff) * dead * 0.5; // re-centre a little past the dead zone edge
        return cam + Math.Clamp(want, -maxStep, maxStep);
    }

    static double[] Median(double[] v, int window)
    {
        var o = new double[v.Length];
        int h = window / 2;
        for (int i = 0; i < v.Length; i++)
        {
            int a = Math.Max(0, i - h), b = Math.Min(v.Length - 1, i + h);
            var slice = v[a..(b + 1)].OrderBy(q => q).ToArray();
            o[i] = slice[slice.Length / 2];
        }
        return o;
    }

    /// <summary>Drops points that lie on the straight line between their neighbours, keeping at most about <paramref name="max"/> points.</summary>
    internal static List<ReframePoint> Simplify(List<ReframePoint> points, int max)
    {
        if (points.Count <= 2) return points;
        for (double tolerance = 0.006; tolerance <= 0.2; tolerance *= 1.5)
        {
            var keep = new bool[points.Count];
            keep[0] = keep[^1] = true;
            Reduce(points, 0, points.Count - 1, tolerance, keep);
            var result = points.Where((_, i) => keep[i]).ToList();
            if (result.Count <= max) return result;
        }
        // Still too many (very busy clip): sample evenly.
        var even = new List<ReframePoint>();
        for (int i = 0; i < max; i++) even.Add(points[(int)Math.Round((points.Count - 1) * (double)i / (max - 1))]);
        return even;
    }

    static void Reduce(List<ReframePoint> p, int first, int last, double tolerance, bool[] keep)
    {
        if (last <= first + 1) return;
        double worst = -1; int at = -1;
        double span = Math.Max(1e-6, p[last].SourceTime - p[first].SourceTime);
        for (int i = first + 1; i < last; i++)
        {
            double u = (p[i].SourceTime - p[first].SourceTime) / span;
            double ex = p[first].X + (p[last].X - p[first].X) * u;
            double ey = p[first].Y + (p[last].Y - p[first].Y) * u;
            double err = Math.Max(Math.Abs(p[i].X - ex), Math.Abs(p[i].Y - ey));
            if (err > worst) { worst = err; at = i; }
        }
        if (worst <= tolerance) return;
        keep[at] = true;
        Reduce(p, first, at, tolerance, keep);
        Reduce(p, at, last, tolerance, keep);
    }
}
