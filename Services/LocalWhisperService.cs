using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace VideoEditor.Services;

/// <summary>Optional, offline speech-to-text support powered by whisper.cpp.</summary>
public static class LocalWhisperService
{
    static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "whisper");
    static readonly string SharedRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "MyEditor", "whisper");
    static readonly string ModelPath = Path.Combine(Root, "ggml-base-q5_1.bin");
    static readonly string SharedModelPath = Path.Combine(SharedRoot, "ggml-base-q5_1.bin");
    const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin";

    public static bool IsInstalled => IsCompleteModel(ModelPath) || IsCompleteModel(SharedModelPath)
        ? FindCli() != null
        : false;

    static bool IsCompleteModel(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 10_000_000; }
        catch { return false; }
    }

    static string EffectiveModelPath => IsCompleteModel(ModelPath) ? ModelPath : SharedModelPath;

    static string? FindCli()
    {
        foreach (var root in new[] { Root, SharedRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var cli = Directory.GetFiles(root, "whisper-cli.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(path => new FileInfo(path).Length > 1_000_000);
                if (cli != null) return cli;
            }
            catch { }
        }
        return null;
    }

    public static async Task EnsureInstalledAsync(IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(Root);
        if (FindCli() == null)
        {
            using var http = CreateHttpClient();
            using var response = await http.GetAsync(
                "https://api.github.com/repos/ggml-org/whisper.cpp/releases/latest");
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var assets = release.RootElement.GetProperty("assets").EnumerateArray();
            var asset = assets.FirstOrDefault(a =>
                a.GetProperty("name").GetString() is "whisper-bin-x64.zip" or "whisper-blas-bin-x64.zip");
            if (asset.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("A Windows x64 CPU build of whisper.cpp was not found in its latest release.");

            var url = asset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidDataException("The whisper.cpp download link is missing.");
            var archive = Path.Combine(Path.GetTempPath(), "myeditor-whisper-runtime.zip");
            try
            {
                await DownloadAsync(http, url, archive, progress, 0, 0.12);
                using var zip = ZipFile.OpenRead(archive);
                var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    var destination = Path.GetFullPath(Path.Combine(Root, entry.FullName));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var source = entry.Open();
                    using var target = File.Create(destination);
                    await source.CopyToAsync(target);
                }
            }
            finally { try { File.Delete(archive); } catch { } }
        }

        if (!IsCompleteModel(ModelPath) && !IsCompleteModel(SharedModelPath))
        {
            using var http = CreateHttpClient();
            string partialModel = ModelPath + ".download";
            try
            {
                if (File.Exists(partialModel)) File.Delete(partialModel);
                await DownloadAsync(http, ModelUrl, partialModel, progress, 0.12, 0.88);
                if (!IsCompleteModel(partialModel)) throw new InvalidDataException("The downloaded local caption model is incomplete.");
                File.Move(partialModel, ModelPath, true);
            }
            finally { try { if (File.Exists(partialModel)) File.Delete(partialModel); } catch { } }
        }
        if (!IsInstalled) throw new InvalidDataException("The local speech model setup did not finish correctly.");
        progress?.Report(1);
    }

    static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyEditor/1.0");
        return client;
    }

    static async Task DownloadAsync(HttpClient http, string url, string destination,
        IProgress<double>? progress, double offset, double range)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(destination);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            if (total is > 0) progress?.Report(Math.Min(offset + range, offset + range * received / total.Value));
        }
    }

    /// <summary>Spoken language passed to whisper.cpp: "auto", "bn" (Bangla), "en", "hi", ...</summary>
    public static string Language { get; set; } = "auto";

    public static async Task<string> TranscribeAsync(string wavPath, IProgress<string>? status = null)
    {
        if (!IsInstalled) throw new InvalidOperationException("Download the local caption model first.");
        string prefix = Path.Combine(Path.GetTempPath(), "myeditor-caption-" + Guid.NewGuid().ToString("N"));
        string srtPath = prefix + ".srt";
        var start = new ProcessStartInfo(FindCli()!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-m", EffectiveModelPath, "-f", wavPath, "-osrt", "-of", prefix, "-l", Language, "-t", Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(CultureInfo.InvariantCulture), "-np" })
            start.ArgumentList.Add(arg);
        try
        {
            status?.Report("Transcribing offline…");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start whisper.cpp.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var errorText = await stderr;
            await stdout;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText) ? "Local transcription failed." : errorText);
            if (!File.Exists(srtPath)) throw new InvalidDataException("The transcription did not produce subtitle text.");
            return await File.ReadAllTextAsync(srtPath);
        }
        finally { try { File.Delete(srtPath); } catch { } }
    }
}
