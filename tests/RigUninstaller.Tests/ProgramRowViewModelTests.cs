using System.Windows.Media;
using System.Windows.Media.Imaging;
using RigUninstaller.Models;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class ProgramRowViewModelTests
{
    private static InstalledProgram P(string name, string? publisher = null, long? sizeKb = null, DateTime? date = null) =>
        new()
        {
            Name = name,
            Publisher = publisher,
            EstimatedSizeKb = sizeKb,
            InstallDate = date,
            RegistryKeyPath = @"HKLM\SOFTWARE\x\" + name,
            KeyName = name,
        };

    [Theory]
    [InlineData("Java(TM) SE", "J")]
    [InlineData("7-Zip", "7")]
    [InlineData("  lowercase app", "L")]
    [InlineData("(weird) start", "W")]
    [InlineData("...", "?")]
    public void Initial_is_first_letter_or_digit_in_upper_case(string name, string expected)
    {
        Assert.Equal(expected, new ProgramRowViewModel(P(name)).Initial);
    }

    [Fact]
    public void Tile_color_is_stable_for_the_same_name()
    {
        var a = new ProgramRowViewModel(P("Acme Editor"));
        var b = new ProgramRowViewModel(P("Acme Editor"));

        Assert.Equal(a.TileColor, b.TileColor);
        Assert.Matches("^#[0-9A-F]{6}$", a.TileColor);
    }

    [Fact]
    public void Unknown_values_show_blank_text_or_dash()
    {
        var row = new ProgramRowViewModel(P("Plain"));

        Assert.Equal("", row.Publisher);
        Assert.Equal("", row.Version);
        Assert.Equal("—", row.SizeText);
        Assert.Equal("—", row.InstallDateText);
    }

    [Fact]
    public void Known_values_are_formatted()
    {
        var row = new ProgramRowViewModel(P("Plain", publisher: "Acme", sizeKb: 2048, date: new DateTime(2024, 3, 9)));

        Assert.Equal("Acme", row.Publisher);
        Assert.Equal("2024-03-09", row.InstallDateText);
        Assert.Contains("MB", row.SizeText);
    }

    [Fact]
    public void Setting_Icon_raises_change_for_Icon_and_HasIcon()
    {
        var row = new ProgramRowViewModel(P("Plain"));
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        ImageSource icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new byte[1], 1);

        row.Icon = icon;

        Assert.True(row.HasIcon);
        Assert.Contains("Icon", changed);
        Assert.Contains("HasIcon", changed);
    }
}
