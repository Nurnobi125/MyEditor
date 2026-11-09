namespace VideoEditor.Models;

/// <summary>One-click caption looks for Reels / Shorts / TikTok style videos. All presets use Nirmala UI,
/// which contains Bangla (Bengali) glyphs, so Bangla captions render with the same look as English ones.</summary>
public static class CaptionStyles
{
    public static readonly string[] Names =
    {
        "Classic (white, black outline)", "Reels yellow", "Bold box", "Neon cyan", "Pop pink", "Gold luxe", "Soft shadow", "Big shorts text"
    };

    /// <summary>Look used for newly generated auto-captions.</summary>
    public static string Current { get; set; } = Names[0];

    public static void Apply(OverlayClip o, string style)
    {
        o.FontFamily = "Nirmala UI";
        o.Bold = true;
        o.TextOpacity = 1;
        o.TextShadow = false;
        o.TextShape = "None";
        o.TextOutlineColor = "Black";
        switch (style)
        {
            case "Reels yellow": o.FontSize = 64; o.Color = "Yellow"; o.TextOutlineWidth = 5; o.TextShadow = true; o.Y = 0.78; break;
            case "Bold box": o.FontSize = 54; o.Color = "White"; o.TextOutlineWidth = 0; o.TextShape = "Caption box"; o.Y = 0.84; break;
            case "Neon cyan": o.FontSize = 60; o.Color = "Cyan"; o.TextOutlineWidth = 4; o.TextShadow = true; o.TextShadowColor = "Cyan"; o.TextShadowSoftness = 12; o.Y = 0.80; break;
            case "Pop pink": o.FontSize = 62; o.Color = "Pink"; o.TextOutlineWidth = 5; o.TextOutlineColor = "White"; o.TextShadow = true; o.Y = 0.78; break;
            case "Gold luxe": o.FontSize = 58; o.Color = "Gold"; o.TextOutlineWidth = 3; o.TextShadow = true; o.TextShadowSoftness = 9; o.Y = 0.84; break;
            case "Soft shadow": o.FontSize = 54; o.Color = "White"; o.TextOutlineWidth = 0; o.TextShadow = true; o.TextShadowSoftness = 14; o.Y = 0.86; break;
            case "Big shorts text": o.FontSize = 76; o.Color = "White"; o.TextOutlineWidth = 7; o.Y = 0.72; break;
            default: o.FontSize = 52; o.Color = "White"; o.TextOutlineWidth = 3; o.Y = 0.86; break;
        }
        o.X = 0.5;
    }
}
