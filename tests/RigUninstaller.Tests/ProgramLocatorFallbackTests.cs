using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class ProgramLocatorFallbackTests
{
    private static readonly SafetyGuard Guard = new(VmTestBase.TestRoots);

    private static InstalledProgram P(string name, string? location = null) => new()
    {
        Name = name,
        InstallLocation = location,
        RegistryKeyPath = @"HKLM\x\" + name,
        KeyName = name,
    };

    [Fact]
    public void A_program_that_registers_another_folder_is_found_by_the_folder_it_runs_from()
    {
        // The NVIDIA App registers its installer folder, but runs from "...\NVIDIA App\...".
        var nvidia = P("NVIDIA App 11.0.9.251", @"C:\Program Files\NVIDIA Corporation\Installer2\Display.NvApp.{1}");
        var driver = P("NVIDIA Graphics Driver 610.74", @"C:\Program Files\NVIDIA Corporation\Installer2\Display.Driver.{2}");

        var found = ProgramLocator.Find(@"C:\Program Files\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA Overlay.exe", new[] { nvidia, driver }, Guard);

        Assert.Same(nvidia, found);
    }

    [Fact]
    public void A_folder_that_fits_two_programs_picks_none()
    {
        var one = P("Contoso Notes 1.0");
        var two = P("Contoso Notes Pro 2.0");

        Assert.Null(ProgramLocator.Find(@"C:\Tools\Contoso Notes\notes.exe", new[] { one, two }, Guard));
    }

    [Fact]
    public void Generic_folders_never_pick_a_program()
    {
        var windows = P("Windows Driver Package 1.0");
        var users = P("Programs Helper 2.0");

        Assert.Null(ProgramLocator.Find(@"C:\Users\PC\AppData\Local\Programs\thing.exe", new[] { windows, users }, Guard));
        Assert.Null(ProgramLocator.Find(@"C:\Windows\System32\thing.exe", new[] { windows }, Guard));
    }

    [Fact]
    public void A_one_word_vendor_folder_does_not_pick_a_program_by_prefix()
    {
        var vendorProgram = P("Microsoft Visual C++ 2015 Redistributable");

        Assert.Null(ProgramLocator.Find(@"C:\Users\PC\AppData\Local\Microsoft\OneDrive\OneDrive.exe", new[] { vendorProgram }, Guard));
    }

    [Fact]
    public void The_exact_install_folder_still_wins_over_the_name_fallback()
    {
        var exact = P("Other Tool 1.0", @"D:\Games\Stuff");
        var named = P("Stuff 2.0");

        Assert.Same(exact, ProgramLocator.Find(@"D:\Games\Stuff\run.exe", new[] { exact, named }, Guard));
    }
}
