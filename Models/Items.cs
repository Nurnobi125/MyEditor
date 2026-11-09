using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VideoEditor.Models;

public enum MediaKind { Video, Image, Audio }

/// <summary>A file in the media bin.</summary>
public class MediaItem : INotifyPropertyChanged
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
    public string Name => $"[{(Kind == MediaKind.Video ? "VID" : Kind == MediaKind.Image ? "IMG" : "AUD")}] {System.IO.Path.GetFileName(Path)}";
    public string ThumbnailGlyph => Kind == MediaKind.Audio ? "♫" : Kind == MediaKind.Video ? "▶" : "▧";
    ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Animation point. Time is seconds from the start of the overlay.</summary>
public class Keyframe
{
    public double Time { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; }
    public double Opacity { get; set; } = 1;
    /// <summary>Degrees, clockwise.</summary>
    public double Rotation { get; set; }
    /// <summary>Curve of the segment that starts at this key: Linear, Ease in, Ease out, Ease in-out, Hold.</summary>
    public string Easing { get; set; } = "Linear";
}

/// <summary>A picture-in-picture video/image, or a text clip, placed at an absolute time on the timeline.
/// X, Y = center position (0..1 of the frame). Scale = width as a fraction of frame width.</summary>
public class OverlayClip
{
    public bool IsText { get; set; }
    /// <summary>Independent video overlay lane (2–4); text clips use the dedicated text lane.</summary>
    public int VideoTrack { get; set; } = 2;
    /// <summary>Text lane number (1–3), used to separate captions and titles that overlap in time.</summary>
    public int TextTrack { get; set; } = 1;
    public string Path { get; set; } = "";
    public bool IsImage { get; set; }
    public string Name => IsText ? "T: " + Text : System.IO.Path.GetFileName(Path);

    public double Start { get; set; }
    public double Duration { get; set; } = 3;
    public double In { get; set; }
    public double SourceDuration { get; set; }
    public double AspectRatio { get; set; } = 16.0 / 9.0;

    public double X { get; set; } = 0.75;
    public double Y { get; set; } = 0.25;
    public double Scale { get; set; } = 0.3;
    public double Opacity { get; set; } = 1;
    /// <summary>"None", "Green" or "Blue": that colour is made transparent (green screen).</summary>
    public string ChromaColor { get; set; } = "None";
    public double ChromaSimilarity { get; set; } = 0.30;
    public double ChromaBlend { get; set; } = 0.10;
    /// <summary>Normal, Screen, Multiply, Overlay, Add, Lighten, Darken, Soft light, Difference.</summary>
    public string BlendMode { get; set; } = "Normal";
    /// <summary>Named animation preset; numbered variations control timing.</summary>
    public string AnimationPreset { get; set; } = "None";
    public List<Keyframe> Keyframes { get; set; } = new();

    public string Text { get; set; } = "Text";
    public int FontSize { get; set; } = 60;
    public string FontFamily { get; set; } = "Nirmala UI";
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public string TextAlignment { get; set; } = "Center";
    public double LineSpacing { get; set; }
    public string Color { get; set; } = "White";
    public string TextShape { get; set; } = "None";
    public double TextOpacity { get; set; } = 1;
    public double TextOutlineWidth { get; set; } = 3;
    public string TextOutlineColor { get; set; } = "Black";
    public bool TextShadow { get; set; }
    public string TextShadowColor { get; set; } = "Black";
    public double TextShadowSoftness { get; set; } = 7;

    public double End => Start + Duration;

    public OverlayClip Clone()
    {
        var c = (OverlayClip)MemberwiseClone();
        c.Keyframes = Keyframes.Select(k => new Keyframe { Time = k.Time, X = k.X, Y = k.Y, Scale = k.Scale, Opacity = k.Opacity, Rotation = k.Rotation, Easing = k.Easing }).ToList();
        return c;
    }
}

/// <summary>Music / voice-over / text-to-speech audio placed at an absolute time.</summary>
public class AudioClip
{
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public double Start { get; set; }
    public double In { get; set; }
    public double Duration { get; set; }
    public double SourceDuration { get; set; }
    public string WaveformPath { get; set; } = "";
    /// <summary>Audio lane number (1–3). Clips on every lane are mixed in preview/export.</summary>
    public int Track { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public List<AudioVolumeKeyframe> VolumeKeyframes { get; set; } = new();

    public AudioClip Clone()
    {
        var clone = (AudioClip)MemberwiseClone();
        clone.VolumeKeyframes = VolumeKeyframes.Select(k => new AudioVolumeKeyframe { Time = k.Time, Volume = k.Volume }).ToList();
        return clone;
    }

    public double VolumeAt(double time)
    {
        var keys = VolumeKeyframes.OrderBy(k => k.Time).ToList();
        if (keys.Count == 0 || time <= keys[0].Time) return keys.Count == 0 ? Volume : keys[0].Volume;
        for (int i = 1; i < keys.Count; i++)
        {
            if (time <= keys[i].Time)
            {
                var a = keys[i - 1]; var b = keys[i];
                double t = Math.Clamp((time - a.Time) / Math.Max(0.001, b.Time - a.Time), 0, 1);
                return a.Volume + (b.Volume - a.Volume) * t;
            }
        }
        return keys[^1].Volume;
    }
}

public class AudioVolumeKeyframe
{
    public double Time { get; set; }
    public double Volume { get; set; } = 1;
}

public static class OverlayMath
{
    /// <summary>Position/scale at a time (seconds from overlay start). Needs 2+ keyframes to animate.</summary>
    public static (double x, double y, double s) At(this OverlayClip o, double rel)
    {
        var k = o.Keyframes.OrderBy(q => q.Time).ToList();
        if (k.Count < 2) return (o.X, o.Y, o.Scale);
        if (rel <= k[0].Time) return (k[0].X, k[0].Y, k[0].Scale);
        for (int i = 1; i < k.Count; i++)
        {
            if (rel < k[i].Time)
            {
                var a = k[i - 1]; var b = k[i];
                double f = EasingCurves.Apply((rel - a.Time) / Math.Max(0.001, b.Time - a.Time), a.Easing);
                return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Scale + (b.Scale - a.Scale) * f);
            }
        }
        var l = k[^1];
        return (l.X, l.Y, l.Scale);
    }

    public static (double x, double y, double s, double opacity) AtText(this OverlayClip o, double rel)
    {
        var k = o.Keyframes.OrderBy(q => q.Time).ToList();
        if (k.Count == 0) return (o.X, o.Y, 1, o.TextOpacity);
        if (k.Count == 1 || rel <= k[0].Time) return (k[0].X, k[0].Y, k[0].Scale, k[0].Opacity);
        for (int i = 1; i < k.Count; i++)
        {
            if (rel < k[i].Time)
            {
                var a = k[i - 1]; var b = k[i];
                double f = EasingCurves.Apply((rel - a.Time) / Math.Max(0.001, b.Time - a.Time), a.Easing);
                return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f,
                    a.Scale + (b.Scale - a.Scale) * f, a.Opacity + (b.Opacity - a.Opacity) * f);
            }
        }
        var last = k[^1];
        return (last.X, last.Y, last.Scale, last.Opacity);
    }

    /// <summary>Rotation (degrees) and opacity from the keyframes at a time measured from overlay start. Needs 2+ keys.</summary>
    public static (double rotation, double opacity) RotOpAt(this OverlayClip o, double rel)
    {
        var k = o.Keyframes.OrderBy(q => q.Time).ToList();
        if (k.Count < 2) return (0, o.IsText ? o.TextOpacity : o.Opacity);
        if (rel <= k[0].Time) return (k[0].Rotation, k[0].Opacity);
        for (int i = 1; i < k.Count; i++)
        {
            if (rel < k[i].Time)
            {
                var a = k[i - 1]; var b = k[i];
                double f = EasingCurves.Apply((rel - a.Time) / Math.Max(0.001, b.Time - a.Time), a.Easing);
                return (a.Rotation + (b.Rotation - a.Rotation) * f, a.Opacity + (b.Opacity - a.Opacity) * f);
            }
        }
        return (k[^1].Rotation, k[^1].Opacity);
    }
}
