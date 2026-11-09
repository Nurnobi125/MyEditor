using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VideoEditor.Models;

namespace VideoEditor.Services;

/// <summary>Bakes reverse playback and freeze frames into cached media files so preview and export need no special handling.</summary>
public static class ClipTools
{
    static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "clips");

    const double ChunkSeconds = 3; // FFmpeg's reverse filter keeps every frame of its input in memory, so work in short chunks.

    static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    static async Task<string> RunFfmpegAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FFmpegService.ResolveTool("ffmpeg"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(ct);
        await stdout;
        string err = await stderr;
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new InvalidOperationException(err.Length > 800 ? err[^800..] : err);
        return err;
    }

    /// <summary>Writes a reversed copy of source[start, start+duration] and returns its path.</summary>
    public static async Task<string> ReverseRangeAsync(string path, double start, double duration, bool hasAudio,
        IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDirectory);
        string id = Guid.NewGuid().ToString("N");
        string output = Path.Combine(CacheDirectory, $"reverse-{id}.mp4");
        int chunks = Math.Max(1, (int)Math.Ceiling(duration / ChunkSeconds - 1e-6));
        var chunkFiles = new List<string>();
        string listFile = Path.Combine(CacheDirectory, $"reverse-{id}.txt");
        try
        {
            for (int i = 0; i < chunks; i++)
            {
                double s = start + i * ChunkSeconds;
                double len = Math.Min(ChunkSeconds, start + duration - s);
                if (len <= 0.01) continue;
                string chunk = Path.Combine(CacheDirectory, $"reverse-{id}-{i:D3}.mp4");
                chunkFiles.Add(chunk);
                var args = new List<string>
                {
                    "-y", "-nostdin", "-v", "error", "-ss", F(s), "-t", F(len), "-i", path,
                    "-vf", "reverse,scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "17"
                };
                if (hasAudio) args.AddRange(new[] { "-af", "areverse,aresample=44100", "-c:a", "aac", "-b:a", "192k", "-ac", "2" });
                else args.Add("-an");
                args.Add(chunk);
                await RunFfmpegAsync(args, ct);
                progress?.Report((i + 1.0) / (chunks + 1));
            }
            if (chunkFiles.Count == 0) throw new InvalidOperationException("Nothing to reverse in this range.");

            // Reversed clip = reversed chunks played last-to-first.
            var list = new StringBuilder();
            for (int i = chunkFiles.Count - 1; i >= 0; i--)
                list.Append("file '").Append(chunkFiles[i].Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
            await File.WriteAllTextAsync(listFile, list.ToString(), new UTF8Encoding(false), ct);
            await RunFfmpegAsync(new[] { "-y", "-nostdin", "-v", "error", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", output }, ct);
            progress?.Report(1);
            return output;
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            throw;
        }
        finally
        {
            foreach (var f in chunkFiles) try { File.Delete(f); } catch { }
            try { File.Delete(listFile); } catch { }
        }
    }

    /// <summary>Saves the frame at <paramref name="sourceTime"/> as a PNG and returns its path.</summary>
    public static async Task<string> ExtractFrameAsync(string path, double sourceTime, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDirectory);
        string output = Path.Combine(CacheDirectory, $"freeze-{Guid.NewGuid():N}.png");
        await RunFfmpegAsync(new[]
        {
            "-y", "-nostdin", "-v", "error", "-ss", F(Math.Max(0, sourceTime)), "-i", path,
            "-frames:v", "1", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", output
        }, ct);
        if (!File.Exists(output)) throw new InvalidOperationException("The frame could not be captured at that position.");
        return output;
    }

    /// <summary>Source-time positions of pointer events in a reversed copy: t' = (Out - t).</summary>
    public static List<PointerEvent> ReversePointerEvents(Clip clip)
    {
        double length = clip.Out - clip.In;
        return clip.PointerEvents
            .Where(p => p.TimeSeconds >= clip.In && p.TimeSeconds <= clip.Out)
            .Select(p => new PointerEvent(Math.Clamp(clip.Out - p.TimeSeconds, 0, length), p.X, p.Y, p.IsClick)
            {
                Button = p.Button, IsTyping = p.IsTyping, ZoomStrength = p.ZoomStrength,
                ZoomDurationSeconds = p.ZoomDurationSeconds, ZoomSpeedSeconds = p.ZoomSpeedSeconds, ZoomEasing = p.ZoomEasing
            })
            .OrderBy(p => p.TimeSeconds).ToList();
    }

    // ---------------------------------------------------------------- silence removal
    /// <summary>Silent stretches of source[start, start+length] as (begin, end) seconds relative to <paramref name="start"/>.</summary>
    public static async Task<List<(double Start, double End)>> DetectSilencesAsync(string path, double start, double length,
        double noiseDb, double minSilenceSeconds, CancellationToken ct)
    {
        string log = await RunFfmpegAsync(new[]
        {
            "-hide_banner", "-nostdin", "-nostats", "-ss", F(start), "-t", F(length), "-i", path, "-vn",
            "-af", $"silencedetect=noise={noiseDb.ToString("F1", CultureInfo.InvariantCulture)}dB:d={minSilenceSeconds.ToString("F2", CultureInfo.InvariantCulture)}",
            "-f", "null", "-"
        }, ct);
        var result = new List<(double, double)>();
        double? open = null;
        foreach (Match m in Regex.Matches(log, @"silence_(start|end):\s*(-?[0-9.]+)"))
        {
            double v = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "start") open = Math.Max(0, v);
            else if (open.HasValue) { result.Add((open.Value, Math.Min(length, v))); open = null; }
        }
        if (open.HasValue) result.Add((open.Value, length)); // silence runs to the end of the clip
        return result;
    }

    /// <summary>The parts to keep: everything except the silences, with a little breathing room around speech.</summary>
    public static List<(double Start, double End)> KeepRanges(List<(double Start, double End)> silences, double length,
        double padding = 0.08, double minKeep = 0.25)
    {
        var keep = new List<(double Start, double End)>();
        double cursor = 0;
        foreach (var (s0, e0) in silences.OrderBy(x => x.Start))
        {
            if (s0 > cursor) keep.Add((cursor, s0));
            cursor = Math.Max(cursor, e0);
        }
        if (cursor < length) keep.Add((cursor, length));

        var padded = keep.Select(k => (Start: Math.Max(0, k.Start - padding), End: Math.Min(length, k.End + padding))).ToList();
        var merged = new List<(double Start, double End)>();
        foreach (var r in padded)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End + 0.01) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else merged.Add(r);
        }
        return merged.Where(r => r.End - r.Start >= minKeep).ToList();
    }
}
