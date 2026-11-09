namespace VideoEditor.Services;

/// <summary>Small offline capture-profile advisor. It uses local hardware and memory facts; no model or network is needed.</summary>
public sealed record LocalCaptureAdvice(int Fps, int TargetHeight, RecorderQuality Quality, string Summary);

public static class LocalRecordingAssistant
{
    public static LocalCaptureAdvice Recommend(int requestedFps, int requestedHeight, RecorderQuality quality, double ramGiB, int logicalCores)
    {
        int fps = Math.Clamp(requestedFps, 10, 60);
        int height = Math.Max(0, requestedHeight);
        string summary = "Local Smart Assist · profile fits this PC";
        if (ramGiB > 0 && ramGiB < 4.5 || logicalCores <= 2)
        {
            fps = Math.Min(fps, 12);
            height = height == 0 ? 540 : Math.Min(height, 540);
            quality = RecorderQuality.Small;
            summary = "Local Smart Assist · 540p/12fps low-memory profile";
        }
        else if (ramGiB > 0 && ramGiB < 8.5 || logicalCores <= 4)
        {
            fps = Math.Min(fps, 24);
            height = height == 0 ? 720 : Math.Min(height, 720);
            summary = "Local Smart Assist · 720p/24fps balanced profile";
        }
        return new LocalCaptureAdvice(fps, height, quality, summary);
    }
}
