namespace VideoEditor.Models;

/// <summary>Portable project document. Media remains at its original locations.</summary>
public sealed class EditorProject
{
    public int FormatVersion { get; set; } = 1;
    public string ApplicationVersion { get; set; } = "3.3.0";
    public string SourceProjectPath { get; set; } = "";
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public double Playhead { get; set; }
    public double TimelineZoom { get; set; } = 60;
    public int AspectPreset { get; set; }
    public int QualityPreset { get; set; }
    public string ExportVideoQuality { get; set; } = "Standard quality";
    public int ExportAudioKbps { get; set; } = 192;
    public string BackgroundMode { get; set; } = "Black bars";
    public string BackgroundColor { get; set; } = "#151820";
    public string BackgroundImagePath { get; set; } = "";
    public bool UseLowMemoryProxies { get; set; }
    public int PreviewProxyHeight { get; set; }
    public int TextLaneCount { get; set; } = 3;
    public int AudioLaneCount { get; set; } = 3;
    public Dictionary<string, bool> MutedTracks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> LockedTracks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ProjectMedia> Media { get; set; } = new();
    public List<Clip> MainTrack { get; set; } = new();
    public List<OverlayClip> Overlays { get; set; } = new();
    public List<AudioClip> Audio { get; set; } = new();
}

public sealed class ProjectMedia
{
    public string Path { get; set; } = "";
    public MediaKind Kind { get; set; }
    public double Duration { get; set; }
    public bool HasAudio { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<PointerEvent> PointerEvents { get; set; } = new();
    public bool AutoMouseZoom { get; set; }
    public bool ShowClickEffects { get; set; }
}
