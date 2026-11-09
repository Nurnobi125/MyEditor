using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoEditor.Services;

public enum RecorderEncoder { Auto, Cpu, Nvidia, Intel, Amd, MediaFoundation }
public enum RecorderQuality { Small, Balanced, High }
public enum WebcamCorner { BottomRight, BottomLeft, TopRight, TopLeft }
public enum WebcamLayout { PictureInPicture, SideBySide, PresenterFocus }
public static class RecorderAudioSource
{
    public const string WasapiLoopback = "wasapi://default-loopback";
    public static bool IsWasapiLoopback(string? value) => string.Equals(value, WasapiLoopback, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Everything the screen recorder can be configured with. The first block is saved to
/// %LOCALAPPDATA%\MyEditor\recorder.json; the "per recording" block is filled in by the main window each time.
/// </summary>
public sealed class RecorderOptions
{
    // ---- saved preferences (Recorder Studio) ----
    /// <summary>0 = follow the toolbar recording mode.</summary>
    public int FpsOverride { get; set; }
    /// <summary>-1 = follow the toolbar recording mode, 0 = native size, otherwise maximum output height.</summary>
    public int HeightOverride { get; set; } = -1;
    public RecorderQuality Quality { get; set; } = RecorderQuality.Balanced;
    public RecorderEncoder Encoder { get; set; } = RecorderEncoder.Auto;
    /// <summary>Second audio input mixed with the microphone (Stereo Mix / loopback / virtual-audio-capturer).</summary>
    public string SystemAudioDevice { get; set; } = "";
    public string MicrophoneDevice { get; set; } = "";
    public string WebcamDevice { get; set; } = "";
    public WebcamCorner WebcamPosition { get; set; } = WebcamCorner.BottomRight;
    public WebcamLayout WebcamLayout { get; set; } = WebcamLayout.PictureInPicture;
    public int WebcamSizePercent { get; set; } = 18;
    public bool WebcamMirror { get; set; } = true;
    public bool ShowCursor { get; set; } = true;
    public bool AutoZoomOnTyping { get; set; } = true;
    public bool SmartAssistEnabled { get; set; } = true;
    public int CountdownSeconds { get; set; } = 3;
    /// <summary>0 = no limit.</summary>
    public int MaxMinutes { get; set; }

    // ---- per recording (not saved) ----
    [JsonIgnore] public bool TrackMouse { get; set; } = true;
    [JsonIgnore] public bool TrackTyping { get; set; } = true;
    [JsonIgnore] public double SystemRamGiB { get; set; }
    [JsonIgnore] public int Fps { get; set; } = 30;
    /// <summary>0 = native size.</summary>
    [JsonIgnore] public int TargetHeight { get; set; }
    [JsonIgnore] public string? MicDevice { get; set; }
    [JsonIgnore] public CaptureArea? Area { get; set; }
    [JsonIgnore] public CaptureWindowInfo? Window { get; set; }

    public RecorderOptions Clone() => (RecorderOptions)MemberwiseClone();

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "recorder.json");

    public static RecorderOptions Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<RecorderOptions>(File.ReadAllText(SettingsPath)) ?? new RecorderOptions();
        }
        catch { /* corrupt settings file: fall back to defaults */ }
        return new RecorderOptions();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience; never block recording */ }
    }
}
