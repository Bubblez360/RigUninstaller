using RigUninstaller.Models;
using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

public class UninstallCommandParserTests
{
    private const string Guid1 = "{12345678-ABCD-4EF0-9876-0123456789AB}";
    private static readonly string MsiExe = Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    private static InstalledProgram P(string? uninstall, string? quiet = null, string keyName = "AcmeKey") =>
        new()
        {
            Name = "Acme",
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            RegistryKeyPath = @"HKLM\SOFTWARE\x\" + keyName,
            KeyName = keyName,
        };

    // ---- normal (non-MSI) commands ----

    [Theory]
    [InlineData(@"""C:\Program Files\Acme\unins000.exe"" /SILENT", @"C:\Program Files\Acme\unins000.exe", "/SILENT")]
    [InlineData(@"""C:\Program Files\Acme\unins000.exe""", @"C:\Program Files\Acme\unins000.exe", "")]
    [InlineData(@"C:\Program Files\Acme\uninstall.exe /S", @"C:\Program Files\Acme\uninstall.exe", "/S")]
    [InlineData(@"C:\Acme\unins.exe", @"C:\Acme\unins.exe", "")]
    [InlineData(@"C:\Acme Tools\UNINST.EXE /x", @"C:\Acme Tools\UNINST.EXE", "/x")]
    [InlineData(@"""C:\A\u.exe"" /S _?=""C:\Program Files\A""", @"C:\A\u.exe", @"/S _?=""C:\Program Files\A""")]
    [InlineData(@"rundll32.exe shell32.dll,Control_RunDLL x", "rundll32.exe", "shell32.dll,Control_RunDLL x")]
    [InlineData(@"C:\Acme Tools\remove.bat /q", @"C:\Acme Tools\remove.bat", "/q")]
    [InlineData(@"   ""C:\A\u.exe""   /S   ", @"C:\A\u.exe", "/S")]
    public void Normal_command_splits_path_and_arguments(string raw, string expectedFile, string expectedArgs)
    {
        var plans = UninstallCommandParser.CreatePlans(P(raw));

        var plan = Assert.IsType<UninstallPlan>(plans.Normal);
        Assert.Equal(expectedFile, plan.FileName);
        Assert.Equal(expectedArgs, plan.Arguments);
        Assert.False(plan.IsMsi);
        Assert.False(plan.IsQuiet);
        Assert.Null(plans.Error);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var plans = UninstallCommandParser.CreatePlans(P(@"%SystemRoot%\system32\foo.exe /S"));

        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, @"system32\foo.exe"), plans.Normal?.FileName);
    }

    [Fact]
    public void Unterminated_quote_is_rejected()
    {
        var plans = UninstallCommandParser.CreatePlans(P(@"""C:\A\u.exe /S"));

        Assert.Null(plans.Normal);
        Assert.Null(plans.Quiet);
        Assert.Contains("understood", plans.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_commands_give_an_error(string? raw)
    {
        var plans = UninstallCommandParser.CreatePlans(P(raw));

        Assert.Null(plans.Normal);
        Assert.Null(plans.Quiet);
        Assert.Contains("no uninstall command", plans.Error);
    }

    [Fact]
    public void DisplayCommand_quotes_a_path_with_spaces()
    {
        var plan = UninstallCommandParser.CreatePlans(P(@"C:\Program Files\A\u.exe /S")).Normal!;

        Assert.Equal(@"""C:\Program Files\A\u.exe"" /S", plan.DisplayCommand);
    }

    [Fact]
    public void DisplayCommand_without_arguments_has_no_trailing_space()
    {
        var plan = UninstallCommandParser.CreatePlans(P(@"C:\A\u.exe")).Normal!;

        Assert.Equal(@"C:\A\u.exe", plan.DisplayCommand);
    }

    // ---- quiet commands ----

    [Fact]
    public void QuietUninstallString_becomes_the_quiet_plan()
    {
        var plans = UninstallCommandParser.CreatePlans(
            P(@"""C:\A\u.exe""", quiet: @"""C:\A\u.exe"" /VERYSILENT"));

        var quiet = Assert.IsType<UninstallPlan>(plans.Quiet);
        Assert.Equal(@"C:\A\u.exe", quiet.FileName);
        Assert.Equal("/VERYSILENT", quiet.Arguments);
        Assert.True(quiet.IsQuiet);
        Assert.NotNull(plans.Normal);
    }

    [Fact]
    public void No_quiet_string_means_no_quiet_plan_for_normal_programs()
    {
        var plans = UninstallCommandParser.CreatePlans(P(@"""C:\A\u.exe"""));

        Assert.Null(plans.Quiet);
    }

    [Fact]
    public void Only_a_quiet_string_still_gives_a_usable_plan()
    {
        var plans = UninstallCommandParser.CreatePlans(P(null, quiet: @"""C:\A\u.exe"" /S"));

        Assert.Null(plans.Normal);
        Assert.NotNull(plans.Quiet);
        Assert.Null(plans.Error);
    }

    // ---- MSI ----

    [Theory]
    [InlineData("MsiExec.exe /I" + Guid1)]
    [InlineData("MsiExec.exe /X" + Guid1)]
    [InlineData("msiexec /x " + Guid1)]
    [InlineData(@"C:\Windows\System32\msiexec.exe /x " + Guid1 + " /qn")]
    [InlineData(@"""C:\Windows\System32\msiexec.exe"" /i " + Guid1)]
    public void Msi_entries_become_msiexec_x_with_the_product_guid(string raw)
    {
        var plans = UninstallCommandParser.CreatePlans(P(raw));

        var plan = Assert.IsType<UninstallPlan>(plans.Normal);
        Assert.True(plan.IsMsi);
        Assert.Equal(MsiExe, plan.FileName);
        Assert.Equal("/x " + Guid1, plan.Arguments);
    }

    [Fact]
    public void Msi_programs_get_a_synthesized_quiet_plan()
    {
        var plans = UninstallCommandParser.CreatePlans(P("MsiExec.exe /X" + Guid1));

        var quiet = Assert.IsType<UninstallPlan>(plans.Quiet);
        Assert.True(quiet.IsMsi);
        Assert.True(quiet.IsQuiet);
        Assert.Equal($"/x {Guid1} /qn /norestart", quiet.Arguments);
    }

    [Fact]
    public void Msi_quiet_string_is_normalised_to_msiexec_x_qn()
    {
        var plans = UninstallCommandParser.CreatePlans(
            P("MsiExec.exe /X" + Guid1, quiet: "MsiExec.exe /X" + Guid1 + " /quiet"));

        Assert.Equal($"/x {Guid1} /qn /norestart", plans.Quiet?.Arguments);
    }

    [Fact]
    public void Msi_without_guid_in_string_falls_back_to_the_key_name()
    {
        var plans = UninstallCommandParser.CreatePlans(P("MsiExec.exe /X", keyName: Guid1));

        Assert.Equal("/x " + Guid1, plans.Normal?.Arguments);
    }

    [Fact]
    public void Msi_without_any_guid_is_an_error()
    {
        var plans = UninstallCommandParser.CreatePlans(P("MsiExec.exe /X", keyName: "NotAGuid"));

        Assert.Null(plans.Normal);
        Assert.Contains("product code", plans.Error);
    }

    [Fact]
    public void A_program_whose_path_only_contains_msiexec_in_a_folder_name_is_not_msi()
    {
        var plans = UninstallCommandParser.CreatePlans(P(@"C:\Tools\msiexec-helper\unins.exe /S"));

        Assert.False(plans.Normal!.IsMsi);
        Assert.Equal(@"C:\Tools\msiexec-helper\unins.exe", plans.Normal.FileName);
    }
}
