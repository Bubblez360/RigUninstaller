using RigUninstaller.Helpers;
using RigUninstaller.Models;
using RigUninstaller.Services;
using RigUninstaller.ViewModels;
using Xunit;

namespace RigUninstaller.Tests;

public class DriveColumnTests
{
    private static InstalledProgram P(string name, string? location, string? icon = null) => new()
    {
        Name = name,
        InstallLocation = location,
        DisplayIcon = icon,
        RegistryKeyPath = @"HKLM\x\" + name,
        KeyName = name,
    };

    private static readonly InstalledProgram[] Sample =
    {
        P("Dota 2", @"F:\Steam\steamapps\common\dota 2 beta"),
        P("Discord", @"C:\Users\PC\AppData\Local\Discord"),
        P("Chrome", @"C:\Program Files\Google\Chrome"),
        P("Hunter: World", @"E:\Games\MHW"),
        P("Mystery", null),
    };

    [Fact]
    public void A_program_knows_its_drive_from_its_install_folder_icon_or_uninstaller()
    {
        Assert.Equal("F:", Sample[0].Drive);
        Assert.Equal("G:", P("Icon only", null, @"G:\App\app.exe,0").Drive);
        Assert.Equal(DiskUsage.UnknownDrive, Sample[4].Drive);
    }

    [Fact]
    public void Reading_the_drive_does_not_change_the_programs_equality_or_hash()
    {
        // Rows are looked up by program (a record): the first read of Drive must not break that.
        var program = P("Notes", @"C:\Program Files\Notes");
        var twin = P("Notes", @"C:\Program Files\Notes");
        var lookup = new Dictionary<InstalledProgram, int> { [program] = 1 };

        _ = program.Drive;

        Assert.Equal(1, lookup[program]);
        Assert.Equal(program.GetHashCode(), twin.GetHashCode());
        Assert.Equal(program, twin);
    }

    [Fact]
    public void Typing_a_drive_shows_only_the_programs_on_it()
    {
        var onC = ProgramListQuery.Apply(Sample, "c:", SortColumn.Name, false).Select(p => p.Name);
        var onF = ProgramListQuery.Apply(Sample, "F:", SortColumn.Name, false).Select(p => p.Name);

        Assert.Equal(new[] { "Chrome", "Discord" }, onC);
        Assert.Equal(new[] { "Dota 2" }, onF);
    }

    [Fact]
    public void A_drive_word_can_be_combined_with_a_name_and_is_not_confused_with_text_that_ends_in_a_colon()
    {
        Assert.Equal(new[] { "Discord" }, ProgramListQuery.Apply(Sample, "c: disc", SortColumn.Name, false).Select(p => p.Name));

        // "r:" is not a drive here: it is only matched when a program is really on drive R.
        Assert.Empty(ProgramListQuery.Apply(Sample, "r:", SortColumn.Name, false));
        // Text that merely contains a colon still searches the name as before.
        Assert.Equal(new[] { "Hunter: World" }, ProgramListQuery.Apply(Sample, "hunter:", SortColumn.Name, false).Select(p => p.Name));
    }

    [Fact]
    public void Sorting_by_drive_groups_the_drives_and_puts_unknown_last_in_both_directions()
    {
        var up = ProgramListQuery.Apply(Sample, null, SortColumn.Drive, false).Select(p => p.Name);
        var down = ProgramListQuery.Apply(Sample, null, SortColumn.Drive, true).Select(p => p.Name);

        Assert.Equal(new[] { "Chrome", "Discord", "Hunter: World", "Dota 2", "Mystery" }, up);
        Assert.Equal(new[] { "Dota 2", "Hunter: World", "Chrome", "Discord", "Mystery" }, down);
    }

    [Fact]
    public void The_row_shows_the_drive_or_a_dash()
    {
        Assert.Equal("F:", new ProgramRowViewModel(Sample[0]).DriveText);
        Assert.Equal(SizeFormatter.Unknown, new ProgramRowViewModel(Sample[4]).DriveText);
    }
}
