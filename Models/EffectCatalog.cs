using System.Globalization;

namespace VideoEditor.Models;

/// <summary>Lightweight, CPU-rendered color looks available in the editor.</summary>
public sealed record VideoEffectPreset(string Name, string Family, int Strength);

public static class EffectCatalog
{
    static readonly string[] Families =
    {
        "Cinematic Teal", "Warm Film", "Cool Steel", "Vivid Pop", "Soft Pastel",
        "Noir", "Golden Hour", "Matte Fade", "Arctic", "Retro", "Dreamy Glow",
        "Night Mood", "Sepia Story", "Faded Summer", "Rose Bloom", "Forest Green",
        "Ocean Blue", "Bleach Bypass", "Classic Chrome", "Sunset Punch", "Soft Mono"
    };

    public static IReadOnlyList<VideoEffectPreset> All { get; } = Families
        .SelectMany(f => Enumerable.Range(1, 5).Select(s => new VideoEffectPreset($"{f} · {s}", f, s)))
        .ToArray();

    public static string FilterExpression(string name)
    {
        var preset = All.FirstOrDefault(p => p.Name == name);
        if (preset == null) return "";
        double k = preset.Strength / 5.0;
        (double contrast, double sat, double bright, double red, double green, double blue) = preset.Family switch
        {
            "Cinematic Teal" => (1 + .12*k, 1 + .12*k, -.025*k, -.08*k, .02*k, .12*k),
            "Warm Film" => (1 + .04*k, 1 - .08*k, .02*k, .14*k, .035*k, -.12*k),
            "Cool Steel" => (1 + .10*k, 1 - .08*k, -.01*k, -.10*k, .015*k, .13*k),
            "Vivid Pop" => (1 + .14*k, 1 + .42*k, 0, 0, 0, 0),
            "Soft Pastel" => (1 - .10*k, 1 - .16*k, .045*k, .045*k, .015*k, .055*k),
            "Noir" => (1 + .35*k, 0, -.035*k, 0, 0, 0),
            "Golden Hour" => (1 + .04*k, 1 + .08*k, .035*k, .16*k, .06*k, -.15*k),
            "Matte Fade" => (1 - .20*k, 1 - .10*k, .035*k, .025*k, .015*k, .01*k),
            "Arctic" => (1 + .08*k, 1 - .04*k, .015*k, -.12*k, .01*k, .16*k),
            "Retro" => (1 + .05*k, 1 - .12*k, .015*k, .12*k, .015*k, -.10*k),
            "Dreamy Glow" => (1 - .12*k, 1 + .08*k, .06*k, .06*k, .02*k, .08*k),
            "Night Mood" => (1 + .18*k, 1 - .18*k, -.055*k, -.035*k, -.025*k, .09*k),
            "Sepia Story" => (1 + .06*k, 1 - .28*k, .025*k, .18*k, .08*k, -.16*k),
            "Faded Summer" => (1 - .15*k, 1 - .12*k, .055*k, .09*k, .04*k, -.035*k),
            "Rose Bloom" => (1 + .02*k, 1 + .08*k, .025*k, .12*k, -.025*k, .055*k),
            "Forest Green" => (1 + .08*k, 1 + .06*k, -.015*k, -.08*k, .10*k, -.07*k),
            "Ocean Blue" => (1 + .10*k, 1 + .10*k, .005*k, -.08*k, .025*k, .14*k),
            "Bleach Bypass" => (1 + .28*k, 1 - .45*k, -.015*k, .035*k, .015*k, -.025*k),
            "Classic Chrome" => (1 + .22*k, 1 - .08*k, -.015*k, -.035*k, .015*k, .025*k),
            "Sunset Punch" => (1 + .16*k, 1 + .22*k, .025*k, .16*k, .025*k, -.12*k),
            "Soft Mono" => (1 - .06*k, 0, .025*k, 0, 0, 0),
            _ => (1.0, 1.0, 0.0, 0.0, 0.0, 0.0)
        };
        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        string eq = $",eq=contrast={F(contrast)}:saturation={F(sat)}:brightness={F(bright)}";
        if (preset.Family == "Noir") return eq + ",hue=s=0";
        return eq + $",colorbalance=rs={F(red)}:gs={F(green)}:bs={F(blue)}";
    }
}
