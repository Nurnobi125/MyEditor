using System.IO;

namespace VideoEditor.Services;

public enum RuntimeModule
{
    MediaEngine,
    LocalCaptionEngine
}

public sealed record RuntimeModuleInfo(RuntimeModule Id, string Name, string Purpose, bool Required, bool Installed);

/// <summary>One place to detect and provision runtime components used by editor features.</summary>
public static class RuntimeModuleService
{
    public static IReadOnlyList<RuntimeModuleInfo> GetModules() => new[]
    {
        new RuntimeModuleInfo(RuntimeModule.MediaEngine, "FFmpeg media engine", "Import, preview, screen recording and export", true, FFmpegService.ToolsInstalled),
        new RuntimeModuleInfo(RuntimeModule.LocalCaptionEngine, "Local caption engine", "Offline speech-to-text captions", false, LocalWhisperService.IsInstalled)
    };

    public static bool IsInstalled(RuntimeModule module) => module switch
    {
        RuntimeModule.MediaEngine => FFmpegService.ToolsInstalled,
        RuntimeModule.LocalCaptionEngine => LocalWhisperService.IsInstalled,
        _ => false
    };

    public static async Task EnsureAsync(RuntimeModule module, IProgress<double>? progress = null)
    {
        switch (module)
        {
            case RuntimeModule.MediaEngine:
                await FFmpegService.EnsureToolsAsync(progress);
                break;
            case RuntimeModule.LocalCaptionEngine:
                await LocalWhisperService.EnsureInstalledAsync(progress);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(module), module, "Unknown runtime module.");
        }

        if (!IsInstalled(module))
            throw new InvalidDataException($"{GetModules().First(m => m.Id == module).Name} setup did not complete.");
    }
}
