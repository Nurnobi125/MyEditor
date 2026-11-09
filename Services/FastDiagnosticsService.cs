using System.IO;
using System.Diagnostics;

namespace VideoEditor.Services;

/// <summary>Fast offline troubleshooting and crash logging; intentionally has no model download or background worker.</summary>
public static class FastDiagnosticsService
{
    static readonly object Gate = new();
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "logs", "errors.log");

    public static string LogFilePath => LogPath;

    public static string CheckPerformance()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long appMemory = Process.GetCurrentProcess().WorkingSet64;
        string capacity = available switch
        {
            > 0 and < 4L * 1024 * 1024 * 1024 => "Low-memory profile recommended (540p, 12–15 fps, CPU ultrafast).",
            > 0 and < 10L * 1024 * 1024 * 1024 => "Lite profile recommended for capture, editing and export on this memory budget.",
            _ => "Recommended profile can be used; choose a lower preview quality if playback stutters."
        };
        if (appMemory > 1_500_000_000) capacity += " Close and reopen MyEditor if memory use keeps growing.";
        return $"Offline Lite Assist · quick check\n\nLogical CPU cores: {Environment.ProcessorCount}\nMyEditor memory: {appMemory / 1024d / 1024d:0} MB\nWindows memory budget: {(available > 0 ? $"{available / 1024d / 1024d:0} MB" : "not reported")}\n\n{capacity}\n\nThis fast helper uses local rules only; it does not download a model or send project data anywhere.";
    }

    public static string Explain(Exception exception)
    {
        string message = exception.GetBaseException().Message;
        if (exception is OutOfMemoryException) return "Available memory is low. Close other apps and use Lite recording mode.";
        if (message.Contains("WASAPI", StringComparison.OrdinalIgnoreCase) || message.Contains("playback device", StringComparison.OrdinalIgnoreCase))
            return "Check the Windows output device. Avoid changing the output device while recording.";
        if (message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) || message.Contains("encoder", StringComparison.OrdinalIgnoreCase))
            return "Check the Media Tools/FFmpeg setup. If the GPU encoder fails, select the CPU encoder.";
        if (message.Contains("access", StringComparison.OrdinalIgnoreCase) || message.Contains("permission", StringComparison.OrdinalIgnoreCase))
            return "Check Windows permissions for files, microphone, and screen capture, plus folder access.";
        if (message.Contains("file", StringComparison.OrdinalIgnoreCase) || message.Contains("path", StringComparison.OrdinalIgnoreCase))
            return "Check whether the original media file was moved or renamed, then reopen the project.";
        return "Try again. More details were saved to the local error log.";
    }

    public static void Log(string source, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                    File.WriteAllText(LogPath, "Older diagnostics log rotated." + Environment.NewLine);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 72)}{Environment.NewLine}");
            }
        }
        catch { /* Diagnostics must never cause or worsen an app failure. */ }
    }
}
