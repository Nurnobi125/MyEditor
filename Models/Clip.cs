namespace VideoEditor.Models;

/// <summary>One piece of the main video track (a trimmed part of a video or an image) + its effects.</summary>
public class Clip
{
    public string Path { get; set; } = "";
    public string Name => IsGap ? $"Gap · {GapDuration:0.##}s" : System.IO.Path.GetFileName(Path);
    public bool IsGap { get; set; }
    public double GapDuration { get; set; } = 1;
    public double SourceDuration { get; set; }
    public double In { get; set; }
    public double Out { get; set; }
    public bool HasAudio { get; set; }
    public bool IsImage { get; set; }
    /// <summary>Scale and normalized placement of the V1 source inside the output frame.</summary>
    public double FrameScale { get; set; } = 1.0;
    public double FrameX { get; set; } = 0.5;
    public double FrameY { get; set; } = 0.5;
    /// <summary>Recorded mouse positions in normalized frame coordinates, relative to the clip source start.</summary>
    public List<PointerEvent> PointerEvents { get; set; } = new();
    public bool AutoMouseZoom { get; set; }
    public bool ShowClickEffects { get; set; }
    public string ClickEffectColor { get; set; } = "#61D9FF";
    public string ClickEffectStyle { get; set; } = "Soft click pulse";
    public double ClickEffectDuration { get; set; } = 0.48;
    public double ClickEffectSize { get; set; } = 0.085;

    // ----- effects -----
    public double Speed { get; set; } = 1.0;
    public double Brightness { get; set; } = 0;
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public double Temperature { get; set; } = 0;
    public double Tint { get; set; } = 0;
    public double Exposure { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Hue { get; set; }
    public double Vibrance { get; set; }
    public double Vignette { get; set; }
    public double Sharpness { get; set; }
    public string Filter { get; set; } = "None";
    public bool Stabilize { get; set; }
    public bool DenoiseAudio { get; set; }
    /// <summary>Evens out loudness to about -16 LUFS (streaming / social-media level) with FFmpeg loudnorm.</summary>
    public bool NormalizeLoudness { get; set; }
    public string Voice { get; set; } = "None";
    public double Volume { get; set; } = 1;
    public string TransitionIn { get; set; } = "None";
    public string OverlayText { get; set; } = "";

    // ----- keyframes & shorts reframe -----
    /// <summary>Per-property keyframes (Zoom, Pan X, Pan Y, Rotation, Brightness, Contrast, Saturation, Volume). Time = seconds from clip start.</summary>
    public Dictionary<string, List<PropKey>> Keys { get; set; } = new();
    /// <summary>When true and ReframePoints exist, export crops to the output aspect following these points instead of a centred crop.</summary>
    public bool AutoReframe { get; set; }
    public List<ReframePoint> ReframePoints { get; set; } = new();

    public double Duration => IsGap ? Math.Max(0.1, GapDuration) : (Out - In) / Speed;

    public Clip Clone()
    {
        var copy = (Clip)MemberwiseClone();
        copy.Keys = Keys.ToDictionary(k => k.Key, k => k.Value.Select(p => new PropKey { Time = p.Time, Value = p.Value, Easing = p.Easing }).ToList());
        copy.ReframePoints = ReframePoints.Select(p => new ReframePoint { SourceTime = p.SourceTime, X = p.X, Y = p.Y }).ToList();
        copy.PointerEvents = PointerEvents.Select(p => new PointerEvent(p.TimeSeconds, p.X, p.Y, p.IsClick) { Button = p.Button, IsTyping = p.IsTyping, ZoomStrength = p.ZoomStrength, ZoomDurationSeconds = p.ZoomDurationSeconds, ZoomSpeedSeconds = p.ZoomSpeedSeconds, ZoomEasing = p.ZoomEasing }).ToList();
        return copy;
    }
}

public sealed class PointerEvent
{
    public PointerEvent() { }
    public PointerEvent(double timeSeconds, double x, double y, bool isClick)
    { TimeSeconds = timeSeconds; X = x; Y = y; IsClick = isClick; }
    public double TimeSeconds { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool IsClick { get; set; }
    public bool IsTyping { get; set; }
    public string Button { get; set; } = "Left";
    public double ZoomStrength { get; set; } = 1.35;
    public double ZoomDurationSeconds { get; set; } = 1.07;
    public double ZoomSpeedSeconds { get; set; } = 0.22;
    public string ZoomEasing { get; set; } = "Smooth";
}

/// <summary>Encoder is one of: h264, h265, h264_nvenc, h264_qsv, h264_amf.</summary>
public record ExportSettings(string Label, int Width, int Height, int Fps, bool CropToFill = false, bool LowEndCpu = false, string Encoder = "h264")
{
    public string VideoQuality { get; init; } = "Standard";
    public int AudioBitrateKbps { get; init; } = 192;
    public string BackgroundMode { get; init; } = "Black bars";
    public string BackgroundColor { get; init; } = "#151820";
    public string? BackgroundImagePath { get; init; }
}
