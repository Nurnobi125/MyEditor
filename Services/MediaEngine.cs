using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEditor.Services;

/// <summary>What a particular ffmpeg.exe can actually do, measured by running it (not guessed from a version number).</summary>
public sealed record EngineReport(
    string Version,
    bool FilterScriptSupported,
    IReadOnlyList<string> MissingCritical,
    IReadOnlyList<string> MissingOptional,
    IReadOnlyList<string> HardwareEncoders,
    IReadOnlyList<string> HardwareDecoders)
{
    public bool Acceptable => FilterScriptSupported && MissingCritical.Count == 0;

    public string Summary
    {
        get
        {
            string hw = HardwareEncoders.Count == 0 ? "CPU only" : string.Join(", ", HardwareEncoders);
            return $"FFmpeg {Version} · GPU encoders: {hw}";
        }
    }

    public string ProblemText
    {
        get
        {
            var parts = new List<string>();
            if (!FilterScriptSupported)
                parts.Add($"this FFmpeg ({Version}) is too old: MyEditor needs FFmpeg 7 or newer (it reads long filter graphs from a file)");
            if (MissingCritical.Count > 0)
                parts.Add("it was built without: " + string.Join(", ", MissingCritical));
            return string.Join("; ", parts);
        }
    }

    public string FullText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Version: {Version}");
        sb.AppendLine($"Long filter graph files (-/filter_complex): {(FilterScriptSupported ? "yes" : "NO")}");
        sb.AppendLine("Missing required filters/encoders: " + (MissingCritical.Count == 0 ? "none" : string.Join(", ", MissingCritical)));
        sb.AppendLine("Missing optional filters: " + (MissingOptional.Count == 0 ? "none" : string.Join(", ", MissingOptional)));
        sb.AppendLine("Working GPU encoders: " + (HardwareEncoders.Count == 0 ? "none" : string.Join(", ", HardwareEncoders)));
        sb.AppendLine("Hardware decode methods: " + (HardwareDecoders.Count == 0 ? "none" : string.Join(", ", HardwareDecoders)));
        return sb.ToString();
    }
}

public static class MediaEngine
{
    // Without these MyEditor's preview or export cannot work.
    static readonly string[] CriticalFilters =
    {
        "xfade", "overlay", "scale", "crop", "concat", "amix", "atempo", "eq", "drawtext", "geq", "rotate",
        "colorbalance", "trim", "atrim", "setpts", "asetpts", "fps", "tpad", "aformat", "aresample", "volume", "split"
    };
    // Nice to have: the matching feature is skipped or degraded when one is missing.
    static readonly string[] OptionalFilters =
    {
        "vibrance", "afftdn", "aecho", "unsharp", "vignette", "deshake", "curves", "gblur", "hue", "fade",
        "colorchannelmixer", "reverse", "areverse", "silencedetect", "loudnorm", "afade", "zoompan"
    };
    static readonly string[] CriticalEncoders = { "libx264", "aac" };
    static readonly (string Id, string Name)[] HardwareEncoderCandidates =
    {
        ("NVIDIA NVENC", "h264_nvenc"), ("Intel Quick Sync", "h264_qsv"), ("AMD AMF", "h264_amf")
    };

    static async Task<(int Exit, string Out, string Err)> RunAsync(string exe, IEnumerable<string> args, int timeoutSeconds = 20)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + Path.GetFileName(exe));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, "", "timeout");
        }
        return (p.ExitCode, await so, await se);
    }

    static HashSet<string> ParseNames(string listing)
    {
        // Lines look like " T.C xfade   VV->V  Cross fade" or " V....D libx264  H.264 ..." (flags, then the name).
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in listing.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length is >= 2 and <= 8 && parts[0].All(c => char.IsLetter(c) || c == '.' || c == '=' || c == '|'))
                names.Add(parts[1]);
        }
        return names;
    }

    /// <summary>Runs the given ffmpeg and measures what it supports. Takes a few seconds.</summary>
    public static async Task<EngineReport> InspectAsync(string ffmpegPath)
    {
        var (_, vOut, vErr) = await RunAsync(ffmpegPath, new[] { "-hide_banner", "-version" });
        string firstLine = (vOut + vErr).Split('\n').FirstOrDefault(l => l.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase)) ?? "";
        var m = Regex.Match(firstLine, @"ffmpeg version\s+(\S+)", RegexOptions.IgnoreCase);
        string version = m.Success ? m.Groups[1].Value : "unknown";

        var filters = ParseNames((await RunAsync(ffmpegPath, new[] { "-hide_banner", "-filters" })).Out);
        var encoders = ParseNames((await RunAsync(ffmpegPath, new[] { "-hide_banner", "-encoders" })).Out);
        var hwaccelText = (await RunAsync(ffmpegPath, new[] { "-hide_banner", "-hwaccels" })).Out;
        var hwDecoders = hwaccelText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.EndsWith(":") && !l.Contains(' ')).ToList();

        var missingCritical = CriticalFilters.Where(f => !filters.Contains(f)).Concat(CriticalEncoders.Where(e => !encoders.Contains(e))).ToList();
        var missingOptional = OptionalFilters.Where(f => !filters.Contains(f)).ToList();

        // GPU encoders are only listed if the driver really works: do a one-frame test encode.
        var hardware = new List<string>();
        foreach (var (label, name) in HardwareEncoderCandidates)
        {
            if (!encoders.Contains(name)) continue;
            var (exit, _, _) = await RunAsync(ffmpegPath, new[]
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=128x72:r=1:d=1",
                "-frames:v", "1", "-an", "-c:v", name, "-f", "null", "-"
            }, 10);
            if (exit == 0) hardware.Add(label);
        }

        return new EngineReport(version, await SupportsFilterScriptAsync(ffmpegPath), missingCritical, missingOptional, hardware, hwDecoders);
    }

    /// <summary>MyEditor passes its filter graph with "-/filter_complex file" (FFmpeg 7+). Test it for real.</summary>
    static async Task<bool> SupportsFilterScriptAsync(string ffmpegPath)
    {
        string script = Path.Combine(Path.GetTempPath(), $"myeditor-selftest-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(script, "color=c=black:s=64x64:r=5:d=0.4,format=yuv420p[o]", new UTF8Encoding(false));
            var (exit, _, _) = await RunAsync(ffmpegPath, new[]
            {
                "-hide_banner", "-loglevel", "error", "-/filter_complex", script, "-map", "[o]", "-frames:v", "1", "-f", "null", "-"
            }, 15);
            return exit == 0;
        }
        catch { return false; }
        finally { try { File.Delete(script); } catch { } }
    }

    /// <summary>Throws a clear, user-readable error when this ffmpeg cannot run MyEditor. Returns the report otherwise.</summary>
    public static async Task<EngineReport> EnsureCapableAsync(string ffmpegPath)
    {
        var report = await InspectAsync(ffmpegPath);
        if (!report.Acceptable) throw new InvalidDataException("Unsuitable FFmpeg: " + report.ProblemText + ".");
        return report;
    }

    // ---------------------------------------------------------------- integrity
    /// <summary>
    /// Compares the SHA-256 of a downloaded archive with the published "&lt;url&gt;.sha256" file.
    /// If the checksum file cannot be fetched (mirror without one, offline) the check is skipped;
    /// a checksum that IS published but does not match is an error.
    /// </summary>
    public static async Task VerifySha256Async(HttpClient http, string archiveUrl, string archivePath)
    {
        string? expected = null;
        try
        {
            using var response = await http.GetAsync(archiveUrl + ".sha256");
            if (!response.IsSuccessStatusCode) return;
            var m = Regex.Match(await response.Content.ReadAsStringAsync(), @"\b[0-9a-fA-F]{64}\b");
            if (m.Success) expected = m.Value;
        }
        catch { return; }
        if (expected == null) return;

        await using var stream = File.OpenRead(archivePath);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded FFmpeg archive failed its SHA-256 integrity check. It was discarded.");
    }

    public static void EnsureDiskSpace(string folder, long requiredBytes)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < requiredBytes)
                throw new IOException($"Not enough free disk space for the media engine setup. About {requiredBytes / 1_000_000} MB is needed on {root}.");
        }
        catch (IOException) { throw; }
        catch { /* unknown drive type: do not block setup */ }
    }

    // ---------------------------------------------------------------- report file
    public static async Task WriteReportAsync(string ffmpegPath, string folder)
    {
        try
        {
            var report = await InspectAsync(ffmpegPath);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "engine-report.txt"),
                $"MyEditor media engine report · {DateTime.Now:u}\r\nPath: {ffmpegPath}\r\n{report.FullText()}");
        }
        catch { /* diagnostics must never break the app */ }
    }
}
