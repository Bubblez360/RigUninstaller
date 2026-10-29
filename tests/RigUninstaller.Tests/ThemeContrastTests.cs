using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>Keeps both colour themes readable (WCAG 2.x contrast) and in step with each other.</summary>
public class ThemeContrastTests
{
    private const double TextRatio = 4.5; // normal-size text
    private const double ControlRatio = 3.0; // the outline of an input

    private static Dictionary<string, string> Load(string theme)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "RigUninstaller", "Themes", theme + ".xaml")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var text = File.ReadAllText(Path.Combine(directory!.FullName, "src", "RigUninstaller", "Themes", theme + ".xaml"));
        return Regex.Matches(text, "x:Key=\"(\\w+)\" Color=\"#([0-9A-Fa-f]{6,8})\"")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static double Channel(int value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(string hex)
    {
        hex = hex.Length == 8 ? hex[2..] : hex; // drop the alpha byte
        int Part(int index) => int.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return 0.2126 * Channel(Part(0)) + 0.7152 * Channel(Part(2)) + 0.0722 * Channel(Part(4));
    }

    private static double Contrast(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    public static IEnumerable<object[]> TextPairs()
    {
        var pairs = new (string Text, string Background)[]
        {
            ("TextBrush", "SurfaceBrush"), ("TextBrush", "WindowBgBrush"), ("TextBrush", "SelectedRowBrush"),
            ("TextBrush", "BloatRowBrush"), ("TextBrush", "HoverBrush"),
            ("TextMutedBrush", "SurfaceBrush"), ("TextMutedBrush", "WindowBgBrush"), ("TextMutedBrush", "SurfaceAltBrush"),
            ("TextMutedBrush", "SelectedRowBrush"), ("TextMutedBrush", "RowHoverBrush"),
            ("AccentTextBrush", "AccentBrush"), ("AccentBrush", "SurfaceBrush"), ("AccentBrush", "WindowBgBrush"),
            ("WarnBrush", "SurfaceBrush"), ("WarnBrush", "BloatRowBrush"), ("WarnBrush", "SelectedRowBrush"),
            ("DangerBrush", "SurfaceBrush"), ("DangerBrush", "SelectedRowBrush"),
            ("OkBrush", "SurfaceBrush"),
        };
        foreach (var theme in new[] { "Light", "Dark" })
        {
            foreach (var (text, background) in pairs)
            {
                yield return new object[] { theme, text, background };
            }
        }
    }

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Text_colours_are_readable_on_the_colours_they_sit_on(string theme, string text, string background)
    {
        var colours = Load(theme);

        var ratio = Contrast(colours[text], colours[background]);

        Assert.True(ratio >= TextRatio, $"{theme}: {text} on {background} is {ratio:0.00}:1, needs {TextRatio}:1");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_outline_of_an_input_stands_out_from_its_surface(string theme)
    {
        var colours = Load(theme);

        var ratio = Contrast(colours["ControlBorderBrush"], colours["SurfaceBrush"]);

        Assert.True(ratio >= ControlRatio, $"{theme}: ControlBorderBrush is {ratio:0.00}:1, needs {ControlRatio}:1");
    }

    [Fact]
    public void Both_themes_define_exactly_the_same_colours()
    {
        var light = Load("Light").Keys.OrderBy(k => k).ToList();
        var dark = Load("Dark").Keys.OrderBy(k => k).ToList();

        Assert.Equal(light, dark);
    }
}
