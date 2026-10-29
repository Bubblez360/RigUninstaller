using RigUninstaller.Services;
using Xunit;

namespace RigUninstaller.Tests;

/// <summary>
/// SafetyGuard is the single gate for every delete / registry write.
/// These tests use fake roots so they behave the same on any machine.
/// </summary>
public class SafetyGuardTests
{
    private static readonly SafetyGuardRoots Roots = new(
        SystemDrive: @"C:\",
        WindowsDir: @"C:\Windows",
        ProgramFiles: @"C:\Program Files",
        ProgramFilesX86: @"C:\Program Files (x86)",
        ProgramData: @"C:\ProgramData",
        UserProfile: @"C:\Users\Tester",
        AppDataRoaming: @"C:\Users\Tester\AppData\Roaming",
        AppDataLocal: @"C:\Users\Tester\AppData\Local");

    private readonly SafetyGuard _guard = new(Roots);

    // ---- file system: must be blocked ----

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\windows\syswow64\kernel32.dll")]
    [InlineData(@"C:\Windows\..\Windows\System32")]
    [InlineData(@"\\?\C:\Windows\System32")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users\Tester")]
    [InlineData(@"C:\Users\Tester\AppData\Roaming")]
    [InlineData(@"C:\Users\Tester\AppData\Local")]
    [InlineData(@"C:\Program Files\WindowsApps\Some.App_1.0")]
    [InlineData(@"C:\Program Files\Windows Defender\MpCmdRun.exe")]
    [InlineData(@"C:\Program Files (x86)\Windows Mail")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Caches")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup")]
    [InlineData(@"C:\Users\Tester\AppData\Local\Microsoft\Windows\INetCache")]
    [InlineData(@"C:\Users\Tester\AppData\Local\Packages\Foo")]
    [InlineData(@"C:\Users\Tester\AppData\Roaming\Microsoft\Windows\Recent")]
    [InlineData(@"C:\Somewhere\driver.sys")]
    public void CheckPath_blocks_protected_locations(string path)
    {
        var result = _guard.CheckPath(path);

        Assert.False(result.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\folder")]
    [InlineData(@"\\server\share\folder")]
    public void CheckPath_blocks_empty_relative_and_network_paths(string path)
    {
        Assert.False(_guard.CheckPath(path).IsAllowed);
    }

    // ---- file system: must be allowed ----

    [Theory]
    [InlineData(@"C:\Program Files\Acme App")]
    [InlineData(@"C:\Program Files (x86)\Acme App\bin\acme.exe")]
    [InlineData(@"C:\ProgramData\Acme")]
    [InlineData(@"C:\Users\Tester\AppData\Roaming\Acme")]
    [InlineData(@"C:\Users\Tester\AppData\Local\Acme\cache")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme App")]
    [InlineData(@"C:\Users\Tester\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Acme.lnk")]
    [InlineData(@"D:\Games\Acme")]
    public void CheckPath_allows_normal_program_locations(string path)
    {
        var result = _guard.CheckPath(path);

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void EnsurePathAllowed_throws_for_protected_path()
    {
        Assert.Throws<SafetyViolationException>(() => _guard.EnsurePathAllowed(@"C:\Windows\System32"));
    }

    [Fact]
    public void EnsurePathAllowed_does_not_throw_for_allowed_path()
    {
        _guard.EnsurePathAllowed(@"C:\Program Files\Acme App");
    }

    // ---- registry: must be blocked ----

    [Theory]
    [InlineData(@"HKLM")]
    [InlineData(@"HKLM\SOFTWARE")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows Defender")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Cryptography")]
    [InlineData(@"HKLM\SOFTWARE\Classes\.txt")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM")]
    [InlineData(@"HKCU\Software")]
    [InlineData(@"HKCU\Software\Microsoft")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKCU\Software\Classes\CLSID")]
    [InlineData(@"HKCU\Environment")]
    [InlineData(@"HKCR\.txt")]
    [InlineData(@"HKU\S-1-5-18\Software\Acme")]
    [InlineData(@"")]
    [InlineData(@"NOTAHIVE\Software\Acme")]
    public void CheckRegistryKey_blocks_core_keys(string keyPath)
    {
        var result = _guard.CheckRegistryKey(keyPath);

        Assert.False(result.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    // ---- registry: must be allowed ----

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Acme")]
    [InlineData(@"HKLM\SOFTWARE\Acme\Settings")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Acme")]
    [InlineData(@"HKCU\Software\Acme")]
    [InlineData(@"hkcu\software\acme")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Acme Corp")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Office\16.0")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID-1234}")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AcmeApp")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AcmeApp")]
    public void CheckRegistryKey_allows_program_keys(string keyPath)
    {
        var result = _guard.CheckRegistryKey(keyPath);

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void EnsureRegistryKeyAllowed_throws_for_core_key()
    {
        Assert.Throws<SafetyViolationException>(() => _guard.EnsureRegistryKeyAllowed(@"HKLM\SYSTEM"));
    }
}
