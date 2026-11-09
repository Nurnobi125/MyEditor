using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoEditor.Models;

public sealed record StickerPreset(string Name, string Category, string Glyph, string Color, string AnimationPreset);

/// <summary>Locally generated sticker artwork and motion combinations; no downloads or GPU required.</summary>
public static class StickerCatalog
{
    static readonly (string Category, string[] Glyphs)[] Art =
    {
        ("Reactions", new[] { "♥", "★", "☺", "!", "?", "✓", "✕", "☻" }),
        ("Creator", new[] { "▶", "♫", "✎", "⌕", "▣", "◉", "✉", "☏" }),
        ("Celebration", new[] { "✦", "✧", "✺", "✿", "♛", "♕", "☀", "❋" }),
        ("Pointers", new[] { "➜", "➤", "⬆", "⬇", "⬅", "➡", "✚", "⌖" }),
        ("Shapes", new[] { "◆", "⬟", "⬢", "⬡", "●", "◈", "✪", "✹" }),
        ("Emoji reactions", new[] { "😀", "😁", "😂", "🤣", "😍", "🥰", "😎", "🤔", "😭", "😡", "🙌", "🙏" }),
        ("Emoji creator", new[] { "👍", "👎", "🔥", "🎉", "💯", "✨", "🚀", "💡", "✅", "❌", "👀", "⭐" }),
        ("Emoji studio", new[] { "🎬", "🎥", "🎙️", "🎧", "🎨", "✂️", "🖥️", "📱", "📌", "📣", "💬", "🌈" }),
        // Premium pack: metallic-ring badges with glow, gloss and sparkles.
        ("Premium Love", new[] { "💖", "💝", "💘", "💞", "💕", "😍", "🥰", "😘" }),
        ("Premium Hype", new[] { "🔥", "⚡", "💥", "🚀", "🏆", "👑", "💎", "🎯" }),
        ("Premium Glam", new[] { "✨", "💫", "🌟", "💎", "👑", "🪩", "💄", "🕶️" }),
        ("Premium Money", new[] { "💰", "💸", "🤑", "📈", "💵", "🏦", "💳", "🪙" }),
        ("Premium Social", new[] { "❤️", "👍", "🔔", "📲", "📣", "🎁", "🔗", "✅" })
    };
    static readonly (string Name, string Hex)[] PremiumPalettes =
    {
        ("Gold", "#F5C542"), ("Rose Gold", "#E6A5A1"), ("Platinum", "#C9D3E0"), ("Royal", "#7B5CFF"), ("Emerald", "#2EC98B"), ("Ruby", "#E5334F")
    };
    static readonly (string Name, string Hex)[] Palettes =
    {
        ("Aqua", "#28C8E8"), ("Violet", "#9C72F5"), ("Coral", "#FF6F79"), ("Gold", "#FFC857"), ("Mint", "#4DD6A2"), ("Rose", "#F47AC1")
    };
    static readonly string[] Motions = { "Pop", "Bounce", "Spin In", "Float", "Zoom In", "Pulse", "Slide Up", "Elastic", "Drift", "Twist" };

    public static bool IsPremium(string category) => category.StartsWith("Premium", StringComparison.Ordinal);
    public static IReadOnlyList<string> Categories { get; } = Art.Select(a => a.Category).ToArray();

    public static IReadOnlyList<StickerPreset> All { get; } = Art
        .SelectMany(category => category.Glyphs.SelectMany((glyph, n) => (IsPremium(category.Category) ? PremiumPalettes : Palettes).SelectMany((palette, p) => Motions.Select((motion, m) =>
            new StickerPreset($"{category.Category} {n + 1:00} · {palette.Name} · {motion}", category.Category, glyph, palette.Hex, $"{motion} · {m + 1:00}")))))
        .ToArray();

    public static string CreateAsset(StickerPreset sticker)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "stickers");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sticker.Name)))[..20] + ".png");
        if (File.Exists(file)) return file;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var color = (Color)ColorConverter.ConvertFromString(sticker.Color)!;
            if (IsPremium(sticker.Category)) DrawPremium(dc, color, sticker.Glyph);
            else {
            var fill = new LinearGradientBrush(color, Color.FromArgb(225, (byte)Math.Min(255, color.R + 45), (byte)Math.Min(255, color.G + 35), (byte)Math.Min(255, color.B + 50)), 40);
            fill.Freeze();
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(52, color.R, color.G, color.B)), null, new Point(128, 128), 116, 116);
            dc.DrawRoundedRectangle(fill, new Pen(new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)), 8), new Rect(42, 42, 172, 172), 55, 55);
            var typeface = new Typeface(new FontFamily(sticker.Category.StartsWith("Emoji", StringComparison.Ordinal) || IsPremium(sticker.Category) ? "Segoe UI Emoji" : "Segoe UI Symbol"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var text = new FormattedText(sticker.Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 102,
                new SolidColorBrush(Color.FromRgb(255, 255, 255)), 1.0);
            dc.DrawText(text, new Point((256 - text.Width) / 2, (256 - text.Height) / 2 - 2));
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 2), new Point(128, 128), 105, 105);
            }
        }
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
        return file;
    }

    static Color Lift(Color c, int d) => Color.FromRgb((byte)Math.Min(255, c.R + d), (byte)Math.Min(255, c.G + d), (byte)Math.Min(255, c.B + d));
    static Color Dim(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    static Color Alpha(byte a, Color c) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Premium badge: soft glow, brushed-metal ring, dark glossy disc, big glyph, highlight and sparkles.</summary>
    static void DrawPremium(DrawingContext dc, Color color, string glyph)
    {
        var center = new Point(128, 128);
        dc.DrawEllipse(new RadialGradientBrush(Alpha(150, color), Alpha(0, color)), null, center, 127, 127);

        var ring = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Lift(color, 95), 0.0), new GradientStop(color, 0.35),
            new GradientStop(Dim(color, 0.55), 0.70), new GradientStop(Lift(color, 70), 1.0)
        }, 45);
        dc.DrawEllipse(ring, null, center, 104, 104);

        var disc = new RadialGradientBrush(Color.FromRgb(0x30, 0x33, 0x44), Color.FromRgb(0x0D, 0x0E, 0x15)) { GradientOrigin = new Point(0.35, 0.28) };
        dc.DrawEllipse(disc, null, center, 87, 87);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Alpha(170, Lift(color, 110))), 2.5), center, 91, 91);

        var typeface = new Typeface(new FontFamily("Segoe UI Emoji"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 98, Brushes.White, 1.0);
        dc.DrawText(text, new Point((256 - text.Width) / 2, (256 - text.Height) / 2 - 2));

        dc.DrawEllipse(new LinearGradientBrush(Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90), null, new Point(128, 86), 64, 38);

        var sparkle = new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        foreach (var (x, y, size) in new[] { (208.0, 46.0, 34.0), (46.0, 206.0, 22.0), (216.0, 198.0, 15.0) })
        {
            var star = new FormattedText("✦", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, sparkle, size, new SolidColorBrush(Lift(color, 120)), 1.0);
            dc.DrawText(star, new Point(x - star.Width / 2, y - star.Height / 2));
        }
    }
}
