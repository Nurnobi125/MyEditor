using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VideoEditor.Models;

namespace VideoEditor.Services;

public record ProbeResult(double Duration, bool HasAudio, bool HasVideo, int Width, int Height);

public static class FFmpegService
{
    const string MissingMsg = "MyEditor could not start the FFmpeg media engine. Retry media setup from the toolbar, then restart the app if setup asks you to.";
    const string EssentialsZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    const string Essentials7zUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z";
    static readonly string ToolsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "ffmpeg");
    // Packaged desktop apps can receive package-specific LocalAppData. Also check the standard per-user
    // location so an FFmpeg setup from the unpackaged build can be reused by an MSIX installation.
    static readonly string SharedToolsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "MyEditor", "ffmpeg");
    static readonly string ThumbnailDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "thumbnails");
    static readonly string ProxyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "proxies");
    static readonly string WaveformDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "waveforms");
    static string ReadyMarker => Path.Combine(ToolsDirectory, ".verified-v2");

    public static bool ToolsInstalled => IsBundledToolPair() ||
        (File.Exists(ReadyMarker) && IsUsableBinary(Path.Combine(ToolsDirectory, "ffmpeg.exe")) && IsUsableBinary(Path.Combine(ToolsDirectory, "ffprobe.exe")));

    static bool IsUsableBinary(string? path)
    {
        try { return path != null && File.Exists(path) && new FileInfo(path).Length > 1_000_000; }
        catch { return false; }
    }

    public static async Task EnsureToolsAsync(IProgress<double>? progress = null)
    {
        if (IsBundledToolPair()) return;
        Directory.CreateDirectory(ToolsDirectory);
        if (IsUsableBinary(Path.Combine(ToolsDirectory, "ffmpeg.exe")) && IsUsableBinary(Path.Combine(ToolsDirectory, "ffprobe.exe")))
        {
            try
            {
                await ValidateToolAsync(Path.Combine(ToolsDirectory, "ffmpeg.exe"), "ffmpeg version");
                await ValidateToolAsync(Path.Combine(ToolsDirectory, "ffprobe.exe"), "ffprobe version");
                // An old or stripped-down copy (for example from an earlier setup) must be replaced, not trusted.
                await MediaEngine.EnsureCapableAsync(Path.Combine(ToolsDirectory, "ffmpeg.exe"));
                File.WriteAllText(ReadyMarker, "verified");
                _ = MediaEngine.WriteReportAsync(Path.Combine(ToolsDirectory, "ffmpeg.exe"), Path.GetDirectoryName(ToolsDirectory)!);
                return;
            }
            catch
            {
                try { File.Delete(ReadyMarker); } catch { }
            }
        }

        // MSIX can isolate LocalAppData and may not be able to launch an executable from the
        // unpackaged app's data folder. Copy and verify the pair in this package's writable data area.
        var sharedFfmpeg = Path.Combine(SharedToolsDirectory, "ffmpeg.exe");
        var sharedFfprobe = Path.Combine(SharedToolsDirectory, "ffprobe.exe");
        if (!string.Equals(Path.GetFullPath(ToolsDirectory), Path.GetFullPath(SharedToolsDirectory), StringComparison.OrdinalIgnoreCase) &&
            IsUsableBinary(sharedFfmpeg) && IsUsableBinary(sharedFfprobe))
        {
            try
            {
                await InstallPairAsync(sharedFfmpeg, sharedFfprobe);
                return;
            }
            catch
            {
                // If the shared copy is blocked or damaged, install a fresh copy in package-local data below.
            }
        }

        var pathFfmpeg = FindOnPath("ffmpeg.exe");
        var pathFfprobe = FindOnPath("ffprobe.exe");
        if (pathFfmpeg != null && pathFfprobe != null)
        {
            try { await InstallPairAsync(pathFfmpeg, pathFfprobe); return; }
            catch { /* Fall through to downloading a clean private copy. */ }
        }

        MediaEngine.EnsureDiskSpace(ToolsDirectory, 900_000_000);
        var archivePath = Path.Combine(Path.GetTempPath(), "myeditor-ffmpeg-download.tmp");
        var stageDirectory = Path.Combine(ToolsDirectory, "setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageDirectory);
        var errors = new List<string>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyEditor/1.0");
            var sevenZip = Find7Zip();
            var candidates = new List<(string url, bool is7z)> { (sevenZip == null ? EssentialsZipUrl : Essentials7zUrl, sevenZip != null) };
            try
            {
                using var mirrorResponse = await http.GetAsync("https://api.github.com/repos/GyanD/codexffmpeg/releases/latest");
                mirrorResponse.EnsureSuccessStatusCode();
                using var mirror = JsonDocument.Parse(await mirrorResponse.Content.ReadAsStringAsync());
                var assets = mirror.RootElement.GetProperty("assets").EnumerateArray();
                string extension = sevenZip == null ? ".zip" : ".7z";
                var mirrorAsset = assets.FirstOrDefault(a =>
                    a.GetProperty("name").GetString()?.Contains("essentials_build" + extension, StringComparison.OrdinalIgnoreCase) == true);
                if (mirrorAsset.ValueKind != JsonValueKind.Undefined)
                    candidates.Add((mirrorAsset.GetProperty("browser_download_url").GetString()!, sevenZip != null));
            }
            catch { /* The primary download remains available when GitHub metadata cannot be reached. */ }
            if (sevenZip != null) candidates.Add((EssentialsZipUrl, false));

            Exception? lastError = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    if (File.Exists(archivePath)) File.Delete(archivePath);
                    await DownloadVerifiedAsync(http, candidates[i].url, archivePath, progress, i, candidates.Count);
                    if (candidates[i].is7z)
                        await ExtractWith7ZipAsync(sevenZip!, archivePath, stageDirectory);
                    else
                        ExtractExecutablesFromZip(archivePath, stageDirectory);
                    var stagedFfmpeg = Path.Combine(stageDirectory, "ffmpeg.exe");
                    var stagedFfprobe = Path.Combine(stageDirectory, "ffprobe.exe");
                    if (!IsUsableBinary(stagedFfmpeg) || !IsUsableBinary(stagedFfprobe))
                        throw new InvalidDataException("The archive did not contain complete ffmpeg.exe and ffprobe.exe files.");
                    await ValidateToolAsync(stagedFfmpeg, "ffmpeg version");
                    await ValidateToolAsync(stagedFfprobe, "ffprobe version");

                    await InstallPairAsync(stagedFfmpeg, stagedFfprobe);
                    if (ToolsInstalled) { progress?.Report(1); return; }
                    throw new InvalidDataException("The archive did not contain ffmpeg.exe and ffprobe.exe.");
                }
                catch (Exception ex) { lastError = ex; errors.Add(ex.Message); }
            }
            throw new InvalidOperationException("Automatic setup failed. " + string.Join(" | ", errors), lastError);
        }
        finally
        {
            try { File.Delete(archivePath); } catch { }
            try { if (Directory.Exists(stageDirectory)) Directory.Delete(stageDirectory, true); } catch { }
            foreach (var name in new[] { "ffmpeg.exe.new", "ffprobe.exe.new" })
                try { File.Delete(Path.Combine(ToolsDirectory, name)); } catch { }
        }
    }

    static bool IsBundledToolPair()
    {
        string[] roots = { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "tools") };
        return roots.Any(root => IsUsableBinary(Path.Combine(root, "ffmpeg.exe")) && IsUsableBinary(Path.Combine(root, "ffprobe.exe")));
    }

    static async Task InstallPairAsync(string ffmpeg, string ffprobe)
    {
        Directory.CreateDirectory(ToolsDirectory);
        string incomingFfmpeg = Path.Combine(ToolsDirectory, "ffmpeg.exe.new");
        string incomingFfprobe = Path.Combine(ToolsDirectory, "ffprobe.exe.new");
        try
        {
            File.Copy(ffmpeg, incomingFfmpeg, true);
            File.Copy(ffprobe, incomingFfprobe, true);
            await ValidateToolAsync(incomingFfmpeg, "ffmpeg version");
            await ValidateToolAsync(incomingFfprobe, "ffprobe version");
            await MediaEngine.EnsureCapableAsync(incomingFfmpeg);
            File.Move(incomingFfmpeg, Path.Combine(ToolsDirectory, "ffmpeg.exe"), true);
            File.Move(incomingFfprobe, Path.Combine(ToolsDirectory, "ffprobe.exe"), true);
            File.WriteAllText(ReadyMarker, "verified");
            _ = MediaEngine.WriteReportAsync(Path.Combine(ToolsDirectory, "ffmpeg.exe"), Path.GetDirectoryName(ToolsDirectory)!);
        }
        finally
        {
            try { if (File.Exists(incomingFfmpeg)) File.Delete(incomingFfmpeg); } catch { }
            try { if (File.Exists(incomingFfprobe)) File.Delete(incomingFfprobe); } catch { }
        }
    }

    static async Task ValidateToolAsync(string path, string expectedText)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-version");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Windows could not start {Path.GetFileName(path)}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(path)} did not respond during setup.");
        }
        string output = await stdout + await stderr;
        if (process.ExitCode != 0 || !output.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The downloaded {Path.GetFileName(path)} failed its startup check.");
    }

    /// <summary>Downloads with up to 3 attempts (short back-off) and checks the published SHA-256 when there is one.</summary>
    static async Task DownloadVerifiedAsync(HttpClient http, string url, string path,
        IProgress<double>? progress, int candidateIndex, int candidateCount)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                await DownloadArchiveAsync(http, url, path, progress, candidateIndex, candidateCount);
                await MediaEngine.VerifySha256Async(http, url, path);
                return;
            }
            catch (InvalidDataException) { try { File.Delete(path); } catch { } throw; }   // corrupt/tampered: do not retry this source
            catch (Exception ex) when (attempt < 3)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }
        throw last ?? new IOException("Download failed.");
    }

    static async Task DownloadArchiveAsync(HttpClient http, string url, string path,
        IProgress<double>? progress, int candidateIndex, int candidateCount)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);
        var buffer = new byte[256 * 1024];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            double candidateProgress = total is > 0 ? (double)received / total.Value : 0;
            progress?.Report(Math.Min(.88, (candidateIndex + candidateProgress) / candidateCount * .88));
        }
    }

    static void ExtractExecutablesFromZip(string archivePath, string destinationDirectory)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (name is not ("ffmpeg.exe" or "ffprobe.exe")) continue;
            using var input = entry.Open();
            using var output = File.Create(Path.Combine(destinationDirectory, name));
            input.CopyTo(output);
        }
    }

    static string? Find7Zip()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        };
        foreach (var path in candidates) if (File.Exists(path)) return path;
        return FindOnPath("7z.exe");
    }

    static async Task ExtractWith7ZipAsync(string sevenZip, string archivePath, string destinationDirectory)
    {
        var psi = new ProcessStartInfo(sevenZip)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "e", archivePath, "-o" + destinationDirectory, "-y", "-r", "ffmpeg.exe", "ffprobe.exe" })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the 7-Zip extractor.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var message = await stderr;
        await stdout;
        if (process.ExitCode != 0) throw new InvalidOperationException("7-Zip could not extract FFmpeg: " + message);
    }

    static string? FindOnPath(string fileName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static string? FindToolPath(string name)
    {
        string fileName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "tools", fileName),
            Path.Combine(ToolsDirectory, fileName),
            Path.Combine(SharedToolsDirectory, fileName)
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return FindOnPath(fileName);
    }

    static string Tool(string name)
    {
        string fileName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        string local = Path.Combine(ToolsDirectory, fileName);
        if (IsUsableBinary(local) && (File.Exists(ReadyMarker) || IsBundledToolPair())) return local;
        return FindToolPath(name) ?? name;
    }

    static Exception ToolLaunchException(string tool, Win32Exception exception)
    {
        string path = Tool(tool);
        string fileState = File.Exists(path) ? $"Windows found '{path}' but could not start it" : $"'{path}' was not found";
        return new InvalidOperationException($"{MissingMsg}\n\n{fileState}. Windows error {exception.NativeErrorCode}: {exception.Message}\n\nUse Settings → Retry media setup. If this is an MSIX install, rebuild and reinstall the latest package.", exception);
    }

    public static string ResolveTool(string name) => Tool(name);

    /// <summary>Render one CPU-only, low-resolution, graded preview frame for responsive inspector feedback.</summary>
    public static async Task RenderColorPreviewFrameAsync(Clip clip, double sourceTime, string output, int width, int height, CancellationToken cancel)
    {
        var filters = new List<string>
        {
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black",
            "setsar=1"
        };
        double exposureGamma = Math.Clamp(Math.Pow(2, -clip.Exposure), 0.25, 4);
        if (Math.Abs(clip.Brightness) > 0.001 || Math.Abs(clip.Contrast - 1) > 0.001 || Math.Abs(clip.Saturation - 1) > 0.001 || Math.Abs(clip.Exposure) > 0.001)
            filters.Add($"eq=brightness={F(clip.Brightness)}:contrast={F(clip.Contrast)}:saturation={F(clip.Saturation)}:gamma={F(exposureGamma)}:gamma_r={F(exposureGamma)}:gamma_g={F(exposureGamma)}:gamma_b={F(exposureGamma)}");
        if (Math.Abs(clip.Temperature) > 0.001 || Math.Abs(clip.Tint) > 0.001 || Math.Abs(clip.Highlights) > 0.001 || Math.Abs(clip.Shadows) > 0.001)
            filters.Add($"colorbalance=rs={F(clip.Temperature)}:rm={F(clip.Temperature)}:rh={F(clip.Temperature)}:gs={F(clip.Tint)}:gm={F(clip.Tint)}:gh={F(clip.Tint)}:bs={F(-clip.Temperature)}:bm={F(-clip.Temperature)}:bh={F(-clip.Temperature)}");
        if (Math.Abs(clip.Highlights) > 0.001 || Math.Abs(clip.Shadows) > 0.001)
            filters.Add($"eq=brightness={F(Math.Clamp(clip.Shadows * 0.12, -0.15, 0.15))}:contrast={F(Math.Clamp(1 + clip.Highlights * 0.18, 0.7, 1.3))}");
        if (Math.Abs(clip.Hue) > 0.001) filters.Add($"hue=h={F(clip.Hue)}");
        if (Math.Abs(clip.Vibrance) > 0.001) filters.Add($"vibrance=intensity={F(Math.Clamp(clip.Vibrance, -1, 2))}");
        if (Math.Abs(clip.Sharpness) > 0.001) filters.Add($"unsharp=5:5:{F(Math.Clamp(clip.Sharpness, 0, 2))}:5:5:0");
        if (clip.Vignette > 0.001) filters.Add($"vignette=angle=PI/{F(2 + (1 - Math.Clamp(clip.Vignette, 0, 1)) * 14)}:mode=forward");
        string look = EffectCatalog.FilterExpression(clip.Filter).TrimStart(',');
        if (look.Length > 0) filters.Add(look);
        else filters.Add(clip.Filter switch
        {
            "Black & White" => "hue=s=0", "Vintage" => "curves=preset=vintage", "Warm" => "colorbalance=rm=0.12:bm=-0.12",
            "Cool" => "colorbalance=rm=-0.1:bm=0.12", "Vivid" => "eq=saturation=1.4:contrast=1.1", _ => "null"
        });

        var psi = new ProcessStartInfo(Tool("ffmpeg"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-threads", "1", "-ss", F(Math.Max(0, sourceTime)), "-i", clip.Path, "-frames:v", "1", "-vf", string.Join(',', filters), "-q:v", "3", "-y", output })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg for preview.");
        var stderr = process.StandardError.ReadToEndAsync(cancel);
        _ = process.StandardOutput.ReadToEndAsync(cancel);
        try { await process.WaitForExitAsync(cancel); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
        string error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }

    public static async Task<string?> CreateThumbnailAsync(string input)
    {
        Directory.CreateDirectory(ThumbnailDirectory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(input))))[..24];
        string output = Path.Combine(ThumbnailDirectory, key + ".jpg");
        if (File.Exists(output)) return output;

        var psi = new ProcessStartInfo(Tool("ffmpeg"))
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-y", "-v", "error", "-ss", "0.1", "-i", input, "-frames:v", "1", "-vf", "scale=164:92:force_original_aspect_ratio=decrease", "-q:v", "6", output })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await errors;
        return process.ExitCode == 0 && File.Exists(output) ? output : null;
    }

    /// <summary>Creates a cached, transparent waveform image for timeline display.</summary>
    public static async Task<string?> CreateWaveformAsync(string input)
    {
        if (!File.Exists(input)) return null;
        Directory.CreateDirectory(WaveformDirectory);
        var info = new FileInfo(input);
        string identity = $"{Path.GetFullPath(input)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        string output = Path.Combine(WaveformDirectory, key + ".png");
        if (File.Exists(output) && new FileInfo(output).Length > 256) return output;
        string partial = Path.Combine(WaveformDirectory, key + ".tmp.png");
        var psi = new ProcessStartInfo(Tool("ffmpeg"))
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", input,
            "-filter_complex", "[0:a]aformat=channel_layouts=mono,showwavespic=s=1024x40:colors=0x6BE5B2:scale=sqrt,format=rgba,colorkey=0x000000:0.08:0.04[w]",
            "-map", "[w]", "-frames:v", "1", partial
        }) psi.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg to create the audio waveform.");
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length <= 256)
                throw new InvalidOperationException("Could not create an audio waveform. " + error);
            File.Move(partial, output, true);
            return output;
        }
        finally { try { if (File.Exists(partial)) File.Delete(partial); } catch { } }
    }

    static string ProxyPath(string source, int height)
    {
        var info = new FileInfo(source);
        string identity = $"{Path.GetFullPath(source)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|proxy-{height}";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        return Path.Combine(ProxyDirectory, key + ".mp4");
    }

    public static string? FindProxy(string source, int height = 540)
    {
        if (!File.Exists(source)) return null;
        var proxy = ProxyPath(source, height);
        return File.Exists(proxy) && new FileInfo(proxy).Length > 4096 ? proxy : null;
    }

    /// <summary>Builds a small, CPU-friendly preview copy. The original source is never modified.</summary>
    public static async Task<string> CreateProxyAsync(string source, int height = 540)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("The source video could not be found.", source);
        height = Math.Clamp(height, 240, 1080);
        Directory.CreateDirectory(ProxyDirectory);
        string output = ProxyPath(source, height);
        if (File.Exists(output) && new FileInfo(output).Length > 4096) return output;
        string partial = Path.Combine(ProxyDirectory, Guid.NewGuid().ToString("N") + ".tmp.mp4");
        var psi = new ProcessStartInfo(Tool("ffmpeg"))
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", source,
            "-map", "0:v:0", "-map", "0:a?",
            "-vf", $"scale=-2:{height}:force_divisible_by=2,pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "32", "-threads", "1",
            "-c:a", "aac", "-b:a", "64k", "-ac", "2", "-movflags", "+faststart", partial
        }) psi.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg to build a preview proxy.");
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length < 4096)
                throw new InvalidOperationException("Could not create a low-memory preview copy. " + error);
            File.Move(partial, output, true);
            return output;
        }
        finally { try { if (File.Exists(partial)) File.Delete(partial); } catch { } }
    }

    public static async Task ExtractMono16kWavAsync(string input, string output, double start, double duration)
    {
        var psi = new ProcessStartInfo(Tool("ffmpeg"))
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-y", "-v", "error", "-ss", F(start), "-i", input, "-t", F(duration), "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", output })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var message = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? MissingMsg : message);
    }

    static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    static string ZoomEaseExpression(string u, string curve) => curve switch
    {
        "Linear" => u,
        "Cinematic" => $"({u})*({u})*({u})*(({u})*(({u})*6-15)+10)",
        "Snappy" => $"1-(1-({u}))*(1-({u}))*(1-({u}))",
        _ => $"({u})*({u})*(3-2*({u}))"
    };

    static List<T> SampleForExpression<T>(List<T> items, int maximum)
    {
        if (items.Count <= maximum) return items;
        if (maximum <= 1) return new List<T> { items[0] };
        return Enumerable.Range(0, maximum)
            .Select(i => items[(int)Math.Round((double)i * (items.Count - 1) / (maximum - 1))])
            .Distinct().ToList();
    }

    static string ZoomPanTime(string expression, int fps) =>
        Regex.Replace(expression, @"(?<![A-Za-z0-9_])t(?![A-Za-z0-9_])", $"(on/{Math.Max(1, fps)})");

    static void AppendZoomPan(StringBuilder sb, string zoom, string focusX, string focusY, ExportSettings s)
    {
        // Fixed-size zoompan avoids dynamic scale dimensions that can make FFmpeg reconfigure
        // a downstream crop filter and fail partway through long screen recordings.
        string z = ZoomPanTime(zoom, s.Fps);
        string x = ZoomPanTime(focusX, s.Fps);
        string y = ZoomPanTime(focusY, s.Fps);
        sb.Append($",zoompan=z='{z}':x='clip(iw*({x})-iw/zoom/2,0,iw-iw/zoom)':y='clip(ih*({y})-ih/zoom/2,0,ih-ih/zoom)':d=1:s={s.Width}x{s.Height}:fps={s.Fps}");
    }

    static void AppendClickEffects(StringBuilder sb, Clip c, ExportSettings s)
    {
        if (!c.ShowClickEffects) return;
        string color = SafeHexColor(c.ClickEffectColor).TrimStart('#');
        double effectDuration = Math.Clamp(c.ClickEffectDuration, 0.15, 1.5);
        var phases = c.ClickEffectStyle switch
        {
            "Halo ring" => new[] { (0.00, 0.38, 0.78, 0.62), (0.38, 0.72, 1.00, 0.42), (0.72, 1.00, 1.28, 0.20) },
            "Bold ripple" => new[] { (0.00, 0.34, 0.62, 0.90), (0.34, 0.70, 1.05, 0.62), (0.70, 1.00, 1.55, 0.30) },
            _ => new[] { (0.00, 0.34, 0.68, 0.38), (0.34, 0.70, 0.96, 0.22), (0.70, 1.00, 1.20, 0.10) }
        };
        int baseSize = Math.Clamp((int)(Math.Clamp(c.ClickEffectSize, 0.03, 0.2) * Math.Min(s.Width, s.Height) * Math.Clamp(c.FrameScale, 0.2, 2)), 24, Math.Min(s.Width, s.Height));
        int thickness = c.ClickEffectStyle == "Bold ripple" ? 5 : c.ClickEffectStyle == "Halo ring" ? 3 : 2;
        foreach (var click in c.PointerEvents.Where(e => e.IsClick).Take(1000))
        {
            double at = (click.TimeSeconds - c.In) / c.Speed;
            if (at < 0 || at > c.Duration) continue;
            foreach (var phase in phases)
            {
                int size = Math.Clamp((int)(baseSize * phase.Item3), 24, Math.Min(s.Width, s.Height));
                double eventX = Math.Clamp(c.FrameX + (Math.Clamp(click.X, 0, 1) - 0.5) * c.FrameScale, 0, 1);
                double eventY = Math.Clamp(c.FrameY + (Math.Clamp(click.Y, 0, 1) - 0.5) * c.FrameScale, 0, 1);
                string x = F(eventX * s.Width - size / 2.0);
                string y = F(eventY * s.Height - size / 2.0);
                double phaseStart = at + effectDuration * phase.Item1;
                double phaseEnd = at + effectDuration * phase.Item2;
                sb.Append($",drawbox=x={x}:y={y}:w={size}:h={size}:color=0x{color}@{F(phase.Item4)}:t={thickness}:enable='between(t,{F(phaseStart)},{F(phaseEnd)})'");
            }
        }
    }

    static (string family, int variation, string p, string smooth) Animation(OverlayClip o)
    {
        if (string.IsNullOrWhiteSpace(o.AnimationPreset) || o.AnimationPreset == "None") return ("None", 1, "1", "1");
        var parts = o.AnimationPreset.Split(" · ", StringSplitOptions.TrimEntries);
        string family = parts[0];
        int.TryParse(parts.Length > 1 ? parts[1] : "1", out int variation);
        double span = Math.Clamp(o.Duration * (0.14 + Math.Clamp(variation, 1, 10) * 0.035), 0.18, 1.2);
        string p = $"min(1,max(0,(t-{F(o.Start)})/{F(span)}))";
        return (family, variation, p, $"({p})*({p})*(3-2*({p}))");
    }

    static string AnimationScale(string family, string p) => family switch
    {
        "Zoom In" => $"(0.12+0.88*({p}))",
        "Zoom Out" => $"(1.45-0.45*({p}))",
        "Spin In" => $"(0.2+0.8*({p}))",
        "Spin Out" => $"(1.4-0.4*({p}))",
        "Grow" => $"(0.18+0.82*({p}))",
        "Shrink" => $"(1.32-0.32*({p}))",
        "Twist" => $"(0.35+0.65*({p}))",
        "Pop" => $"(0.2+0.8*({p})+0.4*sin(PI*({p}))*(1-({p})))",
        "Bubble Pop" => $"(0.5+0.5*({p})+0.32*sin(PI*({p}))*(1-({p})))",
        "Bounce" => $"(0.7+0.3*({p})+0.22*abs(sin(3*PI*({p})))*(1-({p})))",
        "Pulse" => $"(1+0.08*sin(4*PI*({p}))*(1-({p})))",
        "Flip" => $"max(0.08,abs(cos(PI*(1-({p})))) )",
        "Elastic" => $"(1-exp(-5*({p}))*cos(10*PI*({p}))*(1-({p})))",
        _ => "1"
    };

    static string AnimationX(string family, string p, string x) => family switch
    {
        "Slide Left" => $"({x})*main_w-overlay_w/2-(1-({p}))*main_w*0.35",
        "Slide Right" => $"({x})*main_w-overlay_w/2+(1-({p}))*main_w*0.35",
        "Shake" => $"({x})*main_w-overlay_w/2+sin((t)*60)*(1-({p}))*main_w*0.025",
        "Wipe" => $"({x})*main_w-overlay_w/2-(1-({p}))*main_w*0.12",
        _ => $"({x})*main_w-overlay_w/2"
    };

    static string AnimationY(string family, string p, string y) => family switch
    {
        "Slide Up" => $"({y})*main_h-overlay_h/2-(1-({p}))*main_h*0.35",
        "Slide Down" => $"({y})*main_h-overlay_h/2+(1-({p}))*main_h*0.35",
        "Rise" => $"({y})*main_h-overlay_h/2+(1-({p}))*main_h*0.28",
        "Drop" => $"({y})*main_h-overlay_h/2-(1-({p}))*main_h*0.28",
        "Float" => $"({y})*main_h-overlay_h/2+sin(2*PI*({p}))*main_h*0.06*(1-({p}))",
        "Bounce" => $"({y})*main_h-overlay_h/2-abs(sin(3*PI*({p})))*(1-({p}))*main_h*0.07",
        "Drift" => $"({y})*main_h-overlay_h/2-(1-({p}))*main_h*0.12",
        _ => $"({y})*main_h-overlay_h/2"
    };

    static string AnimationAlpha(string family, string smooth) => family == "None" ? "1" : smooth;

    static (string zoom, string x, string y) PointerZoomExpressions(Clip clip)
    {
        var actions = clip.PointerEvents
            .Where(e => e.TimeSeconds >= clip.In && e.TimeSeconds <= clip.Out)
            .Select(e => new { Time = (e.TimeSeconds - clip.In) / clip.Speed, e.X, e.Y, e.IsClick, e.IsTyping, e.ZoomStrength, e.ZoomDurationSeconds, e.ZoomSpeedSeconds, e.ZoomEasing })
            .OrderBy(e => e.Time).ToList();
        if (actions.Count > 48)
        {
            // Keep the zoom graph compact enough for FFmpeg's expression parser on long recordings.
            // Preserve evenly spaced click/typing focus points and cursor path samples.
            var focus = SampleForExpression(actions.Where(e => e.IsClick || e.IsTyping).ToList(), 24);
            var moves = SampleForExpression(actions.Where(e => !e.IsClick && !e.IsTyping).ToList(), 24);
            actions = focus.Concat(moves).OrderBy(e => e.Time).ToList();
        }
        if (actions.Count == 0) return ("1", "0.5", "0.5");

        string zoom = "0";
        foreach (var action in actions)
        {
            if (!action.IsClick && !action.IsTyping) continue; // Clicks and typing start focus-zoom pulses; mouse moves steer the camera.
            double amount = Math.Max(0, Math.Clamp(action.ZoomStrength, 1, 2) - 1);
            double duration = Math.Clamp(action.ZoomDurationSeconds, 0.2, 4);
            double ramp = Math.Clamp(action.ZoomSpeedSeconds, 0.05, duration / 2);
            string dt = $"max(0,t-{F(action.Time)})";
            string riseU = $"min(1,max(0,({dt})/{F(ramp)}))";
            string rise = ZoomEaseExpression(riseU, action.ZoomEasing);
            string releaseU = $"min(1,max(0,(t-{F(action.Time + duration - ramp)})/{F(ramp)}))";
            string release = $"1-({ZoomEaseExpression(releaseU, action.ZoomEasing)})";
            string pulse = $"if(between(t,{F(action.Time)},{F(action.Time + duration)}),{F(amount)}*({rise})*({release}),0)";
            zoom = $"max({zoom},{pulse})";
        }
        zoom = $"min(2,1+{zoom})";

        string x = F(actions[^1].X), y = F(actions[^1].Y);
        for (int i = actions.Count - 2; i >= 0; i--)
        {
            var current = actions[i];
            var next = actions[i + 1];
            double span = Math.Max(0.08, next.Time - current.Time);
            string u = $"min(1,max(0,(t-{F(current.Time)})/{F(span)}))";
            string eased = $"({u})*({u})*(3-2*({u}))";
            string blendX = $"{F(current.X)}+({F(next.X)}-{F(current.X)})*{eased}";
            string blendY = $"{F(current.Y)}+({F(next.Y)}-{F(current.Y)})*{eased}";
            x = $"if(lt(t,{F(next.Time)}),{blendX},{x})";
            y = $"if(lt(t,{F(next.Time)}),{blendY},{y})";
        }
        x = $"if(lt(t,{F(actions[0].Time)}),{F(actions[0].X)},{x})";
        y = $"if(lt(t,{F(actions[0].Time)}),{F(actions[0].Y)},{y})";
        return (zoom, x, y);
    }

    static string DrawTextX(string family, string p, double x, string alignment = "Center", string? xValue = null)
    {
        string anchor = xValue ?? F(x);
        string offset = alignment switch { "Left" => "", "Right" => "-text_w", _ => "-text_w/2" };
        string motion = family switch
        {
            "Slide Left" => $"-(1-({p}))*w*0.35", "Slide Right" => $"+(1-({p}))*w*0.35",
            "Shake" => $"+sin(t*60)*(1-({p}))*w*0.025", "Wipe" => $"-(1-({p}))*w*0.12",
            "Spin In" or "Spin Out" => $"+cos((1-({p}))*2*PI)*(1-({p}))*w*0.06", _ => ""
        };
        return $"w*({anchor}){offset}{motion}";
    }

    static string DrawTextY(string family, string p, double y, string? yValue = null)
    {
        string anchor = yValue ?? F(y);
        string motion = family switch
        {
            "Slide Up" => $"-(1-({p}))*h*0.35", "Slide Down" => $"+(1-({p}))*h*0.35",
            "Rise" => $"+(1-({p}))*h*0.28", "Drop" => $"-(1-({p}))*h*0.28",
            "Float" => $"+sin(2*PI*({p}))*h*0.06*(1-({p}))", "Bounce" => $"-abs(sin(3*PI*({p})))*(1-({p}))*h*0.07",
            "Drift" => $"-(1-({p}))*h*0.12",
            "Zoom In" or "Zoom Out" or "Bubble Pop" or "Spin In" or "Spin Out" or "Elastic" => $"-(1-({p}))*h*0.045", _ => ""
        };
        return $"h*({anchor})-text_h/2{motion}";
    }

    static ProcessStartInfo Psi(string tool, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Tool(tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    static async Task<string> RunAsync(string tool, IEnumerable<string> args)
    {
        try
        {
            using var p = Process.Start(Psi(tool, args))!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new Exception(await e);
            return await o;
        }
        catch (Win32Exception ex) { throw ToolLaunchException(tool, ex); }
    }

    public static async Task<ProbeResult> ProbeAsync(string path)
    {
        var json = await RunAsync("ffprobe", new[]
        {
            "-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "json", path
        });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double dur = 0;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var dEl)
            && double.TryParse(dEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
            dur = dv;

        bool hasAudio = false, hasVideo = false;
        int w = 0, h = 0;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var st in streams.EnumerateArray())
            {
                string? type = st.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                if (type == "audio") hasAudio = true;
                else if (type == "video")
                {
                    hasVideo = true;
                    if (st.TryGetProperty("width", out var wEl)) w = wEl.GetInt32();
                    if (st.TryGetProperty("height", out var hEl)) h = hEl.GetInt32();
                }
            }
        }
        return new ProbeResult(dur, hasAudio, hasVideo, w, h);
    }

    // ---------- filter helpers ----------
    static string FilterPath(string p) => p.Replace("\\", "/").Replace(":", "\\:");

    static string TextColor(string color) => color switch
    {
        "Yellow" => "yellow", "Red" => "red", "Black" => "black", "Cyan" => "cyan",
        "Gold" => "gold", "Pink" => "hotpink", "Violet" => "violet", "Orange" => "orange",
        "Lime" => "lime", _ => "white"
    };

    static string FontFile()
    {
        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        string bangla = System.IO.Path.Combine(root, "Nirmala.ttf");
        return File.Exists(bangla) ? bangla : FontFile("Arial", false, false);
    }

    static string FontFile(string family, bool bold, bool italic)
    {
        string[] variants = family switch
        {
            "Segoe UI" => new[] { "segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf" },
            "Calibri" => new[] { "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf" },
            "Georgia" => new[] { "georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf" },
            "Consolas" => new[] { "consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf" },
            "Times New Roman" => new[] { "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf" },
            "Verdana" => new[] { "verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf" },
            "Nirmala UI" => new[] { "Nirmala.ttf", "Nirmala.ttf", "Nirmala.ttf", "Nirmala.ttf" },
            "Impact" => new[] { "impact.ttf", "impact.ttf", "impact.ttf", "impact.ttf" },
            _ => new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" }
        };
        int variant = bold ? italic ? 3 : 1 : italic ? 2 : 0;
        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        string candidate = System.IO.Path.Combine(root, variants[variant]);
        if (File.Exists(candidate)) return candidate;
        foreach (var fallback in new[] { "arial.ttf", "Nirmala.ttf" })
        {
            candidate = System.IO.Path.Combine(root, fallback);
            if (File.Exists(candidate)) return candidate;
        }
        return "C:/Windows/Fonts/arial.ttf";
    }

    static string Atempo(double s)
    {
        if (Math.Abs(s - 1) < 0.001) return "";
        var sb = new StringBuilder();
        double r = s;
        while (r > 2.0) { sb.Append(",atempo=2.0"); r /= 2.0; }
        while (r < 0.5) { sb.Append(",atempo=0.5"); r /= 0.5; }
        sb.Append($",atempo={F(r)}");
        return sb.ToString();
    }

    static string Xfade(string name) => name switch
    {
        "Fade" => "fade",
        "Fade to black" => "fadeblack",
        "Fade to white" => "fadewhite",
        "Dissolve" => "dissolve",
        "Wipe left" => "wipeleft",
        "Wipe right" => "wiperight",
        "Wipe up" => "wipeup",
        "Wipe down" => "wipedown",
        "Slide left" => "slideleft",
        "Slide right" => "slideright",
        "Slide up" => "slideup",
        "Slide down" => "slidedown",
        "Circle open" => "circleopen",
        "Circle close" => "circleclose",
        "Circle crop" => "circlecrop",
        "Rectangle crop" => "rectcrop",
        "Distance" => "distance",
        "Smooth left" => "smoothleft",
        "Smooth right" => "smoothright",
        "Smooth up" => "smoothup",
        "Smooth down" => "smoothdown",
        "Zoom in" => "zoomin",
        "Pixelize" => "pixelize",
        "Radial" => "radial",
        "Diagonal top-left" => "diagtl",
        "Diagonal top-right" => "diagtr",
        "Diagonal bottom-left" => "diagbl",
        "Diagonal bottom-right" => "diagbr",
        "Vertical open" => "vertopen",
        "Vertical close" => "vertclose",
        "Horizontal open" => "horzopen",
        "Horizontal close" => "horzclose",
        "Horizontal slice left" => "hlslice",
        "Horizontal slice right" => "hrslice",
        "Vertical slice up" => "vuslice",
        "Vertical slice down" => "vdslice",
        "Blur" => "hblur",
        "Fade gray" => "fadegrays",
        "Squeeze vertical" => "squeezev",
        "Squeeze horizontal" => "squeezeh",
        "Horizontal wind left" => "hlwind",
        "Horizontal wind right" => "hrwind",
        "Vertical wind up" => "vuwind",
        "Vertical wind down" => "vdwind",
        "Fast fade" => "fadefast",
        "Slow fade" => "fadeslow",
        "Wipe diagonal top-left" => "wipetl",
        "Wipe diagonal top-right" => "wipetr",
        "Wipe diagonal bottom-left" => "wipebl",
        "Wipe diagonal bottom-right" => "wipebr",
        "Cover left" => "coverleft",
        "Cover right" => "coverright",
        "Cover up" => "coverup",
        "Cover down" => "coverdown",
        "Reveal left" => "revealleft",
        "Reveal right" => "revealright",
        "Reveal up" => "revealup",
        "Reveal down" => "revealdown",
        _ => "fade"
    };

    /// <summary>Piecewise ffmpeg expression in absolute time t, built from keyframes (with per-key easing).</summary>
    static string Expr(List<Keyframe> keys, double start, Func<Keyframe, double> sel)
        => ClipMotion.OverlayExpr(keys, start, sel);

    static string VideoChain(Clip c, int i, ExportSettings s, string? textFile, int? backgroundInput)
    {
        var sb = new StringBuilder();
        bool reframe = c.AutoReframe && c.ReframePoints.Count >= 2 && !c.IsGap && !c.IsImage;
        bool fillBackground = (s.BackgroundMode is "Soft blur fill" or "Custom image") && !reframe;
        bool adjustFrame = !reframe && (Math.Abs(c.FrameScale - 1) > 0.001 || Math.Abs(c.FrameX - 0.5) > 0.001 || Math.Abs(c.FrameY - 0.5) > 0.001);
        string bgColor = SafeHexColor(s.BackgroundColor);
        string prepared = $"base{i}";
        if (c.IsGap) sb.Append($"[{i}:v]trim=start=0:end={F(c.Duration)},setpts=PTS-STARTPTS");
        else sb.Append($"[{i}:v]trim=start={F(c.In)}:end={F(c.Out)},setpts=(PTS-STARTPTS)/{F(c.Speed)}");
        if (c.Stabilize) sb.Append(",deshake");
        if (adjustFrame)
        {
            string bg = $"framebg{i}";
            sb.Append($",split=2[fgraw{i}][bgraw{i}];");
            if (s.BackgroundMode == "Soft blur fill")
                sb.Append($"[bgraw{i}]scale={s.Width}:{s.Height}:force_original_aspect_ratio=increase,crop={s.Width}:{s.Height},gblur=sigma=24,setsar=1[{bg}];");
            else if (s.BackgroundMode == "Custom image" && backgroundInput is int bgInput)
                sb.Append($"[{bgInput}:v]scale={s.Width}:{s.Height}:force_original_aspect_ratio=increase,crop={s.Width}:{s.Height},setsar=1[{bg}];");
            else
                sb.Append($"color=c={bgColor}:s={s.Width}x{s.Height}:r={s.Fps}:d={F(c.Duration)}[{bg}];");

            sb.Append($"[fgraw{i}]scale={s.Width}:{s.Height}:force_original_aspect_ratio={(s.CropToFill ? "increase" : "decrease")}");
            if (s.CropToFill) sb.Append($",crop={s.Width}:{s.Height}");
            sb.Append($",setsar=1,scale=w='max(2,trunc(iw*{F(Math.Clamp(c.FrameScale, 0.2, 2))}/2)*2)':h='max(2,trunc(ih*{F(Math.Clamp(c.FrameScale, 0.2, 2))}/2)*2)':eval=init,format=rgba[fg{i}];");
            sb.Append($"[{bg}][fg{i}]overlay=x='(W-w)*{F(Math.Clamp(c.FrameX, 0, 1))}':y='(H-h)*{F(Math.Clamp(c.FrameY, 0, 1))}':eof_action=pass[{prepared}];[{prepared}]");
        }
        else if (reframe)
        {
            // Shorts auto-reframe: fill the output frame and let the crop window follow the analysed subject path.
            string rx = ReframeExpr(c, true), ry = ReframeExpr(c, false);
            sb.Append($",scale={s.Width}:{s.Height}:force_original_aspect_ratio=increase,crop=w={s.Width}:h={s.Height}:x='clip(iw*({rx})-ow/2,0,iw-ow)':y='clip(ih*({ry})-oh/2,0,ih-oh)',setsar=1");
        }
        else if (fillBackground)
        {
            sb.Append($",split=2[fgraw{i}][bgraw{i}];");
            if (backgroundInput is int bg)
                sb.Append($"[{bg}:v]scale={s.Width}:{s.Height}:force_original_aspect_ratio=increase,crop={s.Width}:{s.Height},setsar=1[bg{i}];");
            else
                sb.Append($"[bgraw{i}]scale={s.Width}:{s.Height}:force_original_aspect_ratio=increase,crop={s.Width}:{s.Height},gblur=sigma=24,setsar=1[bg{i}];");
            sb.Append($"[fgraw{i}]scale={s.Width}:{s.Height}:force_original_aspect_ratio=decrease,pad={s.Width}:{s.Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1[fg{i}];");
            sb.Append($"[bg{i}][fg{i}]overlay=(W-w)/2:(H-h)/2[{prepared}];");
            sb.Append($"[{prepared}]");
        }
        else if (!s.CropToFill)
            sb.Append($",scale={s.Width}:{s.Height}:force_original_aspect_ratio=decrease,pad={s.Width}:{s.Height}:(ow-iw)/2:(oh-ih)/2:color={bgColor},setsar=1");
        else
            sb.Append($",scale={s.Width}:{s.Height}:force_original_aspect_ratio=increase,crop={s.Width}:{s.Height},setsar=1");

        // Place the ring in source-frame coordinates so the same zoom/pan transform carries it with the cursor.
        AppendClickEffects(sb, c, s);
        if (c.AutoMouseZoom && c.PointerEvents.Count > 0)
        {
            var (zoom, mouseX, mouseY) = PointerZoomExpressions(c);
            mouseX = $"({F(c.FrameX)}+(({mouseX})-0.5)*{F(Math.Clamp(c.FrameScale, 0.2, 2))})";
            mouseY = $"({F(c.FrameY)}+(({mouseY})-0.5)*{F(Math.Clamp(c.FrameScale, 0.2, 2))})";
            AppendZoomPan(sb, zoom, mouseX, mouseY, s);
        }

        // Keyframed zoom / pan / rotation (clip-local time t, after speed).
        if (c.HasKeys(ClipMotion.Zoom) || c.HasKeys(ClipMotion.PanX) || c.HasKeys(ClipMotion.PanY))
        {
            string kz = c.Expr(ClipMotion.Zoom), kx = c.Expr(ClipMotion.PanX), ky = c.Expr(ClipMotion.PanY);
            AppendZoomPan(sb, kz, kx, ky, s);
        }
        if (c.HasKeys(ClipMotion.Rotation))
            sb.Append($",rotate='({c.Expr(ClipMotion.Rotation)})*PI/180':ow=iw:oh=ih:c=black");

        bool colorKeys = c.HasKeys(ClipMotion.Brightness) || c.HasKeys(ClipMotion.Contrast) || c.HasKeys(ClipMotion.Saturation);
        if (colorKeys)
            sb.Append($",eq=brightness='{c.Expr(ClipMotion.Brightness)}':contrast='{c.Expr(ClipMotion.Contrast)}':saturation='{c.Expr(ClipMotion.Saturation)}':eval=frame");
        else if (Math.Abs(c.Brightness) > 0.001 || Math.Abs(c.Contrast - 1) > 0.001 || Math.Abs(c.Saturation - 1) > 0.001)
            sb.Append($",eq=brightness={F(c.Brightness)}:contrast={F(c.Contrast)}:saturation={F(c.Saturation)}");
        if (Math.Abs(c.Temperature) > 0.001 || Math.Abs(c.Tint) > 0.001)
            sb.Append($",colorbalance=rs={F(c.Temperature)}:rm={F(c.Temperature)}:rh={F(c.Temperature)}:gs={F(c.Tint)}:gm={F(c.Tint)}:gh={F(c.Tint)}:bs={F(-c.Temperature)}:bm={F(-c.Temperature)}:bh={F(-c.Temperature)}");
        if (Math.Abs(c.Exposure) > 0.001 || Math.Abs(c.Highlights) > 0.001 || Math.Abs(c.Shadows) > 0.001)
            sb.Append($",eq=gamma={F(Math.Clamp(Math.Pow(2, -c.Exposure), 0.25, 4))}:gamma_r={F(Math.Clamp(Math.Pow(2, -c.Exposure), 0.25, 4))}:gamma_g={F(Math.Clamp(Math.Pow(2, -c.Exposure), 0.25, 4))}:gamma_b={F(Math.Clamp(Math.Pow(2, -c.Exposure), 0.25, 4))}:brightness={F(Math.Clamp(c.Shadows * 0.12, -0.15, 0.15))}:contrast={F(Math.Clamp(1 + c.Highlights * 0.18, 0.7, 1.3))}");
        if (Math.Abs(c.Hue) > 0.001) sb.Append($",hue=h={F(c.Hue)}");
        if (Math.Abs(c.Vibrance) > 0.001) sb.Append($",vibrance=intensity={F(Math.Clamp(c.Vibrance, -1, 2))}");
        if (Math.Abs(c.Sharpness) > 0.001) sb.Append($",unsharp=5:5:{F(Math.Clamp(c.Sharpness, 0, 2))}:5:5:0");
        if (Math.Abs(c.Vignette) > 0.001)
            sb.Append($",vignette=angle=PI/{F(2 + (1 - Math.Clamp(c.Vignette, 0, 1)) * 14)}:mode=forward");

        string presetFilter = EffectCatalog.FilterExpression(c.Filter);
        sb.Append(presetFilter.Length > 0 ? presetFilter : c.Filter switch
        {
            "Black & White" => ",hue=s=0",
            "Vintage" => ",curves=preset=vintage",
            "Warm" => ",colorbalance=rm=0.12:bm=-0.12",
            "Cool" => ",colorbalance=rm=-0.1:bm=0.12",
            "Vivid" => ",eq=saturation=1.4:contrast=1.1",
            _ => ""
        });

        if (textFile != null)
            sb.Append($",drawtext=fontfile='{FilterPath(FontFile())}':textfile='{FilterPath(textFile)}':expansion=none")
              .Append($":fontsize={Math.Max(24, s.Height / 18)}:fontcolor=white:borderw=3:bordercolor=black")
              .Append(":x=(w-text_w)/2:y=h-text_h-h/12");

        // A composed background/frame ends its first graph segment with a named output
        // ([baseN]). The following filters must start directly after that label; a
        // leading comma means “empty filter” to FFmpeg and aborts exports with
        // “No such filter: ''”. Keep commas for all subsequent filters.
        sb.Append($",fps={s.Fps},settb=expr=1/{s.Fps},format=yuv420p[v{i}];");
        if (adjustFrame || fillBackground)
        {
            string labelWithComma = $"[{prepared}],";
            int labelIndex = sb.ToString().LastIndexOf(labelWithComma, StringComparison.Ordinal);
            if (labelIndex >= 0) sb.Remove(labelIndex + labelWithComma.Length - 1, 1);
        }
        return sb.ToString();
    }

    /// <summary>Crop-window centre (0..1) over clip-local time, from the analysed reframe points.</summary>
    static string ReframeExpr(Clip c, bool horizontal)
    {
        var keys = c.ReframePoints
            .Select(p => new PropKey { Time = (p.SourceTime - c.In) / c.Speed, Value = Math.Clamp(horizontal ? p.X : p.Y, 0, 1), Easing = "Linear" })
            .Where(k => k.Time >= -1.5 && k.Time <= c.Duration + 1.5)
            .OrderBy(k => k.Time).ToList();
        if (keys.Count == 0) return "0.5";
        return ClipMotion.ExprFromKeys(keys);
    }

    /// <summary>FFmpeg blend mode and the canvas colour that leaves the picture unchanged for that mode.</summary>
    static (string? Mode, string Neutral) BlendInfo(string? name) => name switch
    {
        "Screen" => ("screen", "black"),
        "Add" => ("addition", "black"),
        "Lighten" => ("lighten", "black"),
        "Difference" => ("difference", "black"),
        "Multiply" => ("multiply", "white"),
        "Darken" => ("darken", "white"),
        "Overlay" => ("overlay", "0x808080"),
        "Soft light" => ("softlight", "0x808080"),
        _ => (null, "black")
    };

    static string SafeHexColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToString().All(Uri.IsHexDigit) ? value : "#151820";

    static string AudioChain(Clip c, int i)
    {
        var sb = new StringBuilder();
        if (!c.HasAudio)
            return sb.Append($"anullsrc=r=44100:cl=stereo,atrim=duration={F(c.Duration)},asetpts=PTS-STARTPTS[a{i}];").ToString();

        sb.Append($"[{i}:a]atrim=start={F(c.In)}:end={F(c.Out)},asetpts=PTS-STARTPTS,aresample=44100");
        sb.Append(Atempo(c.Speed));
        if (c.DenoiseAudio) sb.Append(",afftdn=nf=-25");
        if (c.NormalizeLoudness) sb.Append(",loudnorm=I=-16:TP=-1.5:LRA=11,aresample=44100");
        sb.Append(c.Voice switch
        {
            "Deep" => ",asetrate=35280,aresample=44100,atempo=1.25",
            "Chipmunk" => ",asetrate=57330,aresample=44100,atempo=0.769",
            "Echo" => ",aecho=0.8:0.88:60:0.4",
            _ => ""
        });
        if (c.HasKeys(ClipMotion.Volume)) sb.Append($",volume='{c.Expr(ClipMotion.Volume)}':eval=frame");
        else if (Math.Abs(c.Volume - 1) > 0.001) sb.Append($",volume={F(c.Volume)}");
        sb.Append($",aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
        return sb.ToString();
    }

    // ---------- encoders ----------
    static readonly (string Id, string FfmpegName)[] EncoderCatalog =
    {
        ("h264", "libx264"), ("h265", "libx265"),
        ("h264_nvenc", "h264_nvenc"), ("h264_qsv", "h264_qsv"), ("h264_amf", "h264_amf"),
        ("h264_mf", "h264_mf")
    };

    public static bool IsHardwareEncoder(string id) => id is "h264_nvenc" or "h264_qsv" or "h264_amf";
    public static bool CanRetryWithCpu(string id) => IsHardwareEncoder(id) || id == "h264_mf";

    /// <summary>Encoders that are compiled into FFmpeg and, for GPU codecs, can initialize on this PC.</summary>
    public static async Task<HashSet<string>> GetAvailableEncodersAsync()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264" };
        try
        {
            string text = await RunAsync("ffmpeg", new[] { "-hide_banner", "-encoders" });
            foreach (var (id, name) in EncoderCatalog)
            {
                if (!text.Contains(" " + name + " ", StringComparison.Ordinal)) continue;
                if ((!IsHardwareEncoder(id) && id != "h264_mf") || await ProbeEncoderAsync(name)) found.Add(id);
            }
        }
        catch { /* keep the always-available CPU H.264 option */ }
        return found;
    }

    static async Task<bool> ProbeEncoderAsync(string encoder)
    {
        try
        {
            using var process = Process.Start(Psi("ffmpeg", new[]
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=128x72:r=1:d=1",
                "-frames:v", "1", "-an", "-pix_fmt", "nv12", "-c:v", encoder, "-f", "null", "-"
            }))!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                try { await Task.WhenAll(stdout, stderr); } catch { }
                return false;
            }
            await stdout;
            await stderr;
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    static string[] VideoEncoderArgs(ExportSettings s)
    {
        // Filters and source decoders also need CPU. Keep x264/x265 from claiming every core.
        string threads = s.LowEndCpu ? "1" : Math.Clamp(Environment.ProcessorCount / 3, 1, 4).ToString(CultureInfo.InvariantCulture);
        bool fast = s.LowEndCpu || s.Width <= 1280;
        int h264Crf = s.VideoQuality switch { "High quality" => s.LowEndCpu ? 23 : 18, "Smaller file" => s.LowEndCpu ? 31 : 27, _ => s.LowEndCpu ? 27 : 21 };
        int h265Crf = s.VideoQuality switch { "High quality" => s.LowEndCpu ? 25 : 21, "Smaller file" => s.LowEndCpu ? 32 : 29, _ => s.LowEndCpu ? 30 : 24 };
        int gpuQuality = s.VideoQuality switch { "High quality" => 19, "Smaller file" => 28, _ => 23 };
        return s.Encoder switch
        {
            "h265" => new[] { "-c:v", "libx265", "-preset", fast ? "ultrafast" : "veryfast", "-crf", h265Crf.ToString(CultureInfo.InvariantCulture),
                              "-tag:v", "hvc1", "-x265-params", s.LowEndCpu ? "log-level=error:pools=1:frame-threads=1:wpp=0" : "log-level=error", "-threads", threads },
            "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", s.LowEndCpu ? "p1" : "p4", "-rc", "vbr", "-cq", gpuQuality.ToString(CultureInfo.InvariantCulture), "-b:v", "0" },
            "h264_qsv" => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", gpuQuality.ToString(CultureInfo.InvariantCulture) },
            "h264_amf" => new[] { "-c:v", "h264_amf", "-quality", s.LowEndCpu ? "speed" : "balanced", "-rc", "cqp", "-qp_i", gpuQuality.ToString(CultureInfo.InvariantCulture), "-qp_p", (gpuQuality + 2).ToString(CultureInfo.InvariantCulture) },
            "h264_mf" => new[] { "-c:v", "h264_mf", "-b:v", (s.Width >= 3840 ? "24M" : s.Width >= 1920 ? "10M" : "5M") },
            _ => s.LowEndCpu
                ? new[] { "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", h264Crf.ToString(CultureInfo.InvariantCulture), "-threads", threads }
                : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", h264Crf.ToString(CultureInfo.InvariantCulture), "-threads", threads }
        };
    }

    static void AddVideoInput(List<string> args, string path, bool image, double imageLoopSeconds, bool lowEndCpu)
    {
        if (image)
        {
            args.AddRange(new[] { "-loop", "1", "-t", F(imageLoopSeconds) });
        }
        else if (lowEndCpu)
        {
            // A single software decoder thread keeps peak RAM and CPU contention predictable on 2–4 GB PCs.
            args.AddRange(new[] { "-threads:v", "1" });
        }
        else
        {
            // Each cut is a separate FFmpeg input. Several decoder threads per input can
            // multiply into dozens of workers and starve Windows input on edited recordings.
            // Bound decoding per source while retaining hardware decode when available.
            args.AddRange(new[] { "-threads:v", "1", "-hwaccel", "auto" });
        }
        args.Add("-i");
        args.Add(path);
    }

    /// <summary>Renders main track + overlays (PiP / keyframes / text) + extra audio tracks into one MP4.</summary>
    public static async Task ExportAsync(IReadOnlyList<Clip> clips, IReadOnlyList<OverlayClip> overlays,
        IReadOnlyList<AudioClip> audios, string output, ExportSettings s, IProgress<double>? progress,
        CancellationToken cancel = default, bool allowEngineRecovery = true)
    {
        var args = new List<string> { "-y" };
        var fc = new StringBuilder();
        var temps = new List<string>();
        double total = 0;
        int input = 0;

        try
        {
            // Add primary sources first so clip audio/video stream indices stay predictable.
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                total += c.Duration;
                if (c.IsGap)
                    args.AddRange(new[] { "-f", "lavfi", "-i", $"color=c=black:s={s.Width}x{s.Height}:r={s.Fps}:d={F(c.Duration)}" });
                else AddVideoInput(args, c.Path, c.IsImage, c.SourceDuration + 0.1, s.LowEndCpu);
                input++;
            }

            var textFiles = new string?[clips.Count];
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                string? textFile = null;

                if (!string.IsNullOrWhiteSpace(c.OverlayText))
                {
                    textFile = System.IO.Path.GetTempFileName();
                    File.WriteAllText(textFile, c.OverlayText, new UTF8Encoding(false));
                    temps.Add(textFile);
                }
                textFiles[i] = textFile;
            }

            var backgroundInputs = new int?[clips.Count];
            if (s.BackgroundMode == "Custom image" && !string.IsNullOrWhiteSpace(s.BackgroundImagePath) && File.Exists(s.BackgroundImagePath))
            {
                for (int i = 0; i < clips.Count; i++)
                {
                    backgroundInputs[i] = input++;
                    AddVideoInput(args, s.BackgroundImagePath, true, clips[i].Duration + 0.1, s.LowEndCpu);
                }
            }

            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                fc.Append(VideoChain(c, i, s, textFiles[i], backgroundInputs[i]));
                fc.Append(AudioChain(c, i));
            }

            string vOut, aOut;
            bool anyTransition = clips.Skip(1).Any(c => c.TransitionIn != "None");

            if (!anyTransition)
            {
                for (int i = 0; i < clips.Count; i++) fc.Append($"[v{i}][a{i}]");
                fc.Append($"concat=n={clips.Count}:v=1:a=1[v][a];");
                vOut = "v"; aOut = "a";
            }
            else
            {
                string vc = "v0", ac = "a0";
                double t = clips[0].Duration;
                string norm = $"fps={s.Fps},settb=expr=1/{s.Fps},setsar=1,format=yuv420p";

                for (int i = 1; i < clips.Count; i++)
                {
                    var c = clips[i];
                    string vn = $"vx{i}", an = $"ax{i}";
                    double td = 0;
                    if (c.TransitionIn != "None")
                    {
                        td = Math.Min(0.5, Math.Min(t, c.Duration) / 2);
                        fc.Append($"[{vc}][v{i}]xfade=transition={Xfade(c.TransitionIn)}:duration={F(td)}:offset={F(t - td)}[{vn}0];");
                        fc.Append($"[{vn}0]{norm}[{vn}];");
                        fc.Append($"[{ac}][a{i}]acrossfade=d={F(td)}[{an}];");
                    }
                    else
                    {
                        fc.Append($"[{vc}][v{i}]concat=n=2:v=1:a=0[{vn}0];");
                        fc.Append($"[{vn}0]{norm}[{vn}];");
                        fc.Append($"[{ac}][a{i}]concat=n=2:v=0:a=1[{an}];");
                    }
                    t += c.Duration - td;
                    vc = vn; ac = an;
                }
                vOut = vc; aOut = ac;
                total = t;
            }

            double audioEnd = audios.Count == 0 ? 0 : audios.Max(a => a.Start + a.Duration);
            double overlayEnd = overlays.Count == 0 ? 0 : overlays.Max(o => o.Start + o.Duration);
            double outputDuration = Math.Max(total, Math.Max(audioEnd, overlayEnd));
            double timelineTail = Math.Max(0, outputDuration - total);
            if (timelineTail > 0.01)
            {
                // Extend the base picture before compositing overlays/captions so assets that extend past
                // V1 can still appear during the held final frame.
                fc.Append($"[{vOut}]tpad=stop_mode=clone:stop_duration={F(timelineTail)},trim=duration={F(outputDuration)}[vpad];");
                vOut = "vpad";
                total = outputDuration;
            }

            // ----- video / image overlays (picture-in-picture, with keyframe animation) -----
            string vcur = vOut;
            foreach (var o in overlays.Where(q => !q.IsText).OrderBy(q => q.VideoTrack))
            {
                int idx = input++;
                AddVideoInput(args, o.Path, o.IsImage, o.Duration + 0.5, s.LowEndCpu);

                bool anim = o.Keyframes.Count >= 2;
                var (family, variation, animP, animSmooth) = Animation(o);
                bool hasAnimation = family != "None";
                string scaleAnim = AnimationScale(family, animP);
                fc.Append($"[{idx}:v]");
                if (!o.IsImage) fc.Append($"trim=start={F(o.In)}:end={F(o.In + o.Duration)},");
                // Still-image sources (including generated design shapes) can arrive as a single timestamp.
                // Normalize locally, create a real CFR frame sequence, then place that sequence on the timeline.
                // Without this, scale/rotate expressions see a frozen timestamp and the export looks static even
                // though the WPF preview animates the same clip.
                fc.Append($"setpts=PTS-STARTPTS,fps={s.Fps},setpts=PTS+{F(o.Start)}/TB,");
                // Rotation keyframes: rotate the source first (fixed-size canvas = its diagonal), then scale with a
                // compensation factor, because rotate cannot follow a frame size that changes every frame.
                bool rotKeys = anim && o.Keyframes.Any(k => Math.Abs(k.Rotation) > 0.01);
                double comp = rotKeys ? Math.Sqrt(1 + 1 / Math.Max(0.0001, o.AspectRatio * o.AspectRatio)) : 1;
                if (rotKeys)
                    fc.Append($"format=rgba,rotate='({ClipMotion.OverlayExpr(o.Keyframes, o.Start, k => k.Rotation)})*PI/180':ow='hypot(iw,ih)':oh='hypot(iw,ih)':c=black@0,");
                if (anim || hasAnimation)
                {
                    string keyScale = anim ? Expr(o.Keyframes, o.Start, k => k.Scale) : F(o.Scale);
                    fc.Append($"scale=w='trunc(({keyScale})*({scaleAnim})*{s.Width}*{F(comp)}/2)*2':h=-2:eval=frame,");
                }
                else
                    fc.Append($"scale=w={Math.Max(2, (int)(o.Scale * s.Width * comp / 2) * 2)}:h=-2,");
                fc.Append("format=rgba");
                if (o.ChromaColor is "Green" or "Blue")
                    fc.Append($",colorkey=color={(o.ChromaColor == "Green" ? "0x00FF00" : "0x0000FF")}:similarity={F(Math.Clamp(o.ChromaSimilarity, 0.01, 1))}:blend={F(Math.Clamp(o.ChromaBlend, 0, 1))}");
                if (o.Opacity < 0.999) fc.Append($",colorchannelmixer=aa={F(o.Opacity)}");
                // Opacity keyframes (only when a key actually differs from the default of 1).
                if (anim && o.Keyframes.Any(k => Math.Abs(k.Opacity - 1) > 0.001))
                    fc.Append($",geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*clip({ClipMotion.OverlayExpr(o.Keyframes, o.Start, k => k.Opacity, "T")},0,1)'");
                if (family is "Fade In" or "Wipe" or "Drift")
                    fc.Append($",fade=t=in:st={F(o.Start)}:d={F(Math.Clamp(o.Duration * (0.14 + Math.Clamp(variation, 1, 10) * 0.035), 0.18, 1.2))}:alpha=1");
                if (family is "Spin In" or "Spin Out" or "Twist")
                {
                    string rotation = family switch
                    {
                        "Spin Out" => $"-(1-({animP}))*2*PI",
                        "Twist" => $"(1-({animP}))*3*PI",
                        _ => $"(1-({animP}))*2*PI"
                    };
                    // Expand the rotated canvas so spin motion is visible instead of being clipped to the source bounds.
                    fc.Append($",rotate='{rotation}':ow='rotw(iw)':oh='roth(ih)':c=none");
                }
                fc.Append($"[ov{idx}];");

                string baseX = anim ? $"({Expr(o.Keyframes, o.Start, k => k.X)})" : F(o.X);
                string baseY = anim ? $"({Expr(o.Keyframes, o.Start, k => k.Y)})" : F(o.Y);
                string xe = hasAnimation ? AnimationX(family, animP, baseX) : $"({baseX})*main_w-overlay_w/2";
                string ye = hasAnimation ? AnimationY(family, animP, baseY) : $"({baseY})*main_h-overlay_h/2";
                string next = $"vo{idx}";
                var (blendName, neutral) = BlendInfo(o.BlendMode);
                if (blendName == null)
                    fc.Append($"[{vcur}][ov{idx}]overlay=x='{xe}':y='{ye}':eof_action=pass:enable='between(t,{F(o.Start)},{F(o.End)})'[{next}];");
                else
                {
                    // Blend modes: draw the overlay on a canvas of the mode's neutral colour (so empty areas change nothing),
                    // then blend that canvas with the picture only while the overlay is on screen.
                    fc.Append($"color=c={neutral}:s={s.Width}x{s.Height}:r={s.Fps}:d={F(Math.Max(total, o.End + 0.5))}[bl{idx}];");
                    fc.Append($"[bl{idx}][ov{idx}]overlay=x='{xe}':y='{ye}':eof_action=pass:enable='between(t,{F(o.Start)},{F(o.End)})',format=gbrp[ly{idx}];");
                    // blend works plane by plane, so do it in RGB (gbrp); in YUV the colours come out wrong.
                    fc.Append($"[{vcur}]format=gbrp[bs{idx}];[bs{idx}][ly{idx}]blend=all_mode={blendName}:enable='between(t,{F(o.Start)},{F(o.End)})',format=yuv420p[{next}];");
                }
                vcur = next;
            }

            // ----- text clips -----
            int tcount = 0;
            foreach (var o in overlays.Where(q => q.IsText && !string.IsNullOrWhiteSpace(q.Text)))
            {
                var tf = System.IO.Path.GetTempFileName();
                File.WriteAllText(tf, o.Text, new UTF8Encoding(false));
                temps.Add(tf);

                string col = TextColor(o.Color);
                string border = TextColor(o.TextOutlineColor);
                int fs = Math.Max(10, (int)(o.FontSize * s.Height / 1080.0));
                var (family, _, animP, animSmooth) = Animation(o);
                bool textKeys = o.Keyframes.Count > 0;
                string keyX = textKeys ? Expr(o.Keyframes, o.Start, k => k.X) : F(o.X);
                string keyY = textKeys ? Expr(o.Keyframes, o.Start, k => k.Y) : F(o.Y);
                string keyScale = textKeys ? Expr(o.Keyframes, o.Start, k => k.Scale) : "1";
                string keyOpacity = textKeys ? Expr(o.Keyframes, o.Start, k => k.Opacity) : "1";
                string ax = DrawTextX(family, animP, o.X, o.TextAlignment, keyX);
                string ay = DrawTextY(family, animP, o.Y, keyY);
                string next = $"vt{tcount++}";
                string shadowColor = TextColor(o.TextShadowColor);
                double shadowBlur = Math.Clamp(o.TextShadowSoftness * s.Height / 1080.0, 0, 20);
                double shadowOpacity = Math.Clamp(0.82 - shadowBlur * 0.018, 0.35, 0.82);
                double shadowOffset = Math.Clamp(1 + shadowBlur * 0.25, 1, 6);
                fc.Append($"[{vcur}]drawtext=fontfile='{FilterPath(FontFile(o.FontFamily, o.Bold, o.Italic))}':textfile='{FilterPath(tf)}':expansion=none")
                  .Append($":fontsize='{fs}*({keyScale})':fontcolor={col}:borderw={F(Math.Clamp(o.TextOutlineWidth * s.Height / 1080.0, 0, 12))}:bordercolor={border}:line_spacing={F(Math.Clamp(o.LineSpacing * s.Height / 1080.0, -20, 60))}:text_align=M{(o.TextAlignment == "Left" ? "L" : o.TextAlignment == "Right" ? "R" : "C")}")
                  .Append(o.TextShadow ? $":shadowcolor={shadowColor}@{F(shadowOpacity)}:shadowx={F(shadowOffset)}:shadowy={F(shadowOffset)}" : "")
                  .Append(o.TextShape == "None" ? "" : o.TextShape == "Highlight box"
                      ? ":box=1:boxcolor=0x147896@0.88:boxborderw=12" : ":box=1:boxcolor=0x0C0E12@0.78:boxborderw=12")
                  .Append($":alpha='({AnimationAlpha(family, animSmooth)})*{F(Math.Clamp(o.TextOpacity, 0, 1))}*({keyOpacity})':x='{ax}':y='{ay}'")
                  .Append($":enable='between(t,{F(o.Start)},{F(o.End)})'[{next}];");
                vcur = next;
            }
            fc.Append($"[{vcur}]format=yuv420p[vfinal];");

            // ----- extra audio (music, voice-over, text-to-speech) -----
            var amLabels = new List<string>();
            foreach (var a in audios)
            {
                int idx = input++;
                args.Add("-i"); args.Add(a.Path);
                int ms = (int)(a.Start * 1000);
                fc.Append($"[{idx}:a]atrim=start={F(a.In)}:end={F(a.In + a.Duration)},asetpts=PTS-STARTPTS,aresample=44100,")
                  .Append($"aformat=sample_fmts=fltp:channel_layouts=stereo,volume={F(a.Volume)}");
                double fadeIn = Math.Clamp(a.FadeInSeconds, 0, a.Duration / 2);
                double fadeOut = Math.Clamp(a.FadeOutSeconds, 0, a.Duration / 2);
                if (fadeIn > 0.001) fc.Append($",afade=t=in:st=0:d={F(fadeIn)}");
                if (fadeOut > 0.001) fc.Append($",afade=t=out:st={F(Math.Max(0, a.Duration - fadeOut))}:d={F(fadeOut)}");
                fc.Append($",adelay={ms}|{ms}[am{idx}];");
                amLabels.Add($"am{idx}");
            }
            string aFinal = aOut;
            if (amLabels.Count > 0)
            {
                fc.Append($"[{aOut}]").Append(string.Concat(amLabels.Select(l => $"[{l}]")))
                  .Append($"amix=inputs={1 + amLabels.Count}:duration=longest:dropout_transition=0:normalize=0[afinal];");
                aFinal = "afinal";
            }

            // Keep the generated filter graph out of the Windows command line. Screen recordings
            // with many zoom markers can produce a graph large enough for CreateProcess to fail
            // with Win32 error 206 (command line too long), even though ffmpeg.exe is present.
            string filterScript = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"myeditor-filter-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(filterScript, fc.ToString().TrimEnd(';'), new UTF8Encoding(false), cancel);
            temps.Add(filterScript);
            args.AddRange(new[]
            {
                // FFmpeg 9 removed -filter_complex_script. The slash form reads the option value
                // from a file and is supported by current FFmpeg releases.
                "-/filter_complex", filterScript, "-map", "[vfinal]", "-map", $"[{aFinal}]",
                "-filter_threads", s.LowEndCpu ? "1" : "2",
                "-filter_complex_threads", s.LowEndCpu ? "1" : "2"
            });
            args.AddRange(VideoEncoderArgs(s));
            args.AddRange(new[]
            {
                "-pix_fmt", s.Encoder == "h264_mf" ? "nv12" : "yuv420p",
                // Fragmented MP4 writes its index up front and avoids the final full-file
                // rewrite required by +faststart, which can appear to hang on long renders.
                "-c:a", "aac", "-b:a", s.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
                "-t", F(outputDuration), "-progress", "pipe:1", "-nostats", output
            });

            using var p = Process.Start(Psi("ffmpeg", args))!;
            // Export is background work. BelowNormal helps Windows keep the editor,
            // keyboard and mouse responsive when a complex render is CPU-heavy.
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* Priority changes may be denied by Windows policy. */ }
            using var cancelRegistration = cancel.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already finished */ }
            });
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (err) { err.AppendLine(e.Data); if (err.Length > 6000) err.Remove(0, err.Length - 4000); }
            };
            p.BeginErrorReadLine();

            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                {
                    if (long.TryParse(line[(line.IndexOf('=') + 1)..], out var us) && total > 0)
                        // Don't call the last fraction "finalizing" just because timestamps
                        // reached the expected duration: filters can still be producing frames.
                        // FFmpeg's explicit progress=end marks the actual end of frame processing.
                        progress?.Report(Math.Clamp(us / 1e6 / total, 0, 0.98));
                }
                else if (line == "progress=end")
                    progress?.Report(0.99);
            }
            await p.WaitForExitAsync();
            if (cancel.IsCancellationRequested)
            {
                try { File.Delete(output); } catch { }   // never leave a half-written file behind
                throw new OperationCanceledException(cancel);
            }
            if (p.ExitCode != 0)
            {
                try { File.Delete(output); } catch { }
                throw new Exception("FFmpeg failed:\n" + err);
            }
            progress?.Report(1);
        }
        catch (Win32Exception ex)
        {
            // A tool can be damaged or blocked after initial setup. Repair it once and retry this
            // export; the flag prevents a repair/retry loop if Windows still refuses to launch it.
            if (allowEngineRecovery && !cancel.IsCancellationRequested)
            {
                try { await EnsureToolsAsync(); }
                catch (Exception setupError)
                {
                    throw new InvalidOperationException(
                        $"{MissingMsg}\n\nAutomatic repair also failed: {setupError.Message}", setupError);
                }
                await ExportAsync(clips, overlays, audios, output, s, progress, cancel, allowEngineRecovery: false);
                return;
            }
            throw ToolLaunchException("ffmpeg", ex);
        }
        finally
        {
            foreach (var f in temps) { try { File.Delete(f); } catch { } }
        }
    }
}
