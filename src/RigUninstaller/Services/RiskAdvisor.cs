using System.Text.RegularExpressions;
using RigUninstaller.Models;

namespace RigUninstaller.Services;

public enum RiskLevel
{
    /// <summary>Other programs or hardware may stop working, but Windows itself keeps running.</summary>
    Caution,

    /// <summary>Removing it can make the screen, sound, network or disk misbehave, or make Windows lag or crash.</summary>
    Danger,
}

/// <param name="Level">How serious the warning is.</param>
/// <param name="Reason">Plain-words reason shown in the tooltip and in the confirmation.</param>
public sealed record RiskInfo(RiskLevel Level, string Reason)
{
    public string Label => Level == RiskLevel.Danger ? "Risky" : "Caution";
}

/// <summary>
/// Knows which installed programs are better left alone: drivers, runtimes that other programs need, security
/// software and hardware control tools. It only warns; the user decides. Matching is by program name.
/// </summary>
public static class RiskAdvisor
{
    private sealed record Rule(Regex Pattern, RiskLevel Level, string Reason);

    private static Rule Make(string pattern, RiskLevel level, string reason) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), level, reason);

    // The first matching rule wins, so the most serious rules come first.
    private static readonly Rule[] Rules =
    {
        Make(@"\b(NVIDIA|AMD|Intel)\b.*\b(Graphics|Display)\b.*\bDriver\b|NVIDIA GeForce (Game Ready|Studio)|NVIDIA Driver|AMD (Radeon )?Software|Radeon (Software|Adrenalin)|Intel\W*(R)?\W*(UHD|Iris|Arc)\b.*Graphics",
            RiskLevel.Danger, "This is your graphics driver. Removing it can leave a black or low-resolution screen and make games and apps lag or crash until a new driver is installed."),
        Make(@"Chipset|Management Engine|Serial IO|Rapid Storage|\bRST\b|\bAHCI\b|NVMe.*Driver|Thunderbolt.*Driver|\bUSB\b.*Driver|Platform Monitoring|Dynamic Tuning|Dynamic Platform",
            RiskLevel.Danger, "This is a chipset, storage or system driver. Removing it can cause crashes, slow disks, or USB devices that stop working."),
        Make(@"Realtek.*(Audio|HD|Ethernet|LAN|Wireless|Bluetooth|Card Reader)|HD Audio Driver|Audio Driver|Sound Driver|Ethernet.*Driver|Wi-?Fi.*Driver|Wireless.*Driver|Bluetooth.*Driver|Killer.*(Network|Suite)|Intel\W*(R)?\W*(Wi-?Fi|Wireless|Bluetooth|Ethernet|Network)",
            RiskLevel.Danger, "This is a sound or network driver. Removing it can silence your audio or cut your internet until the driver is installed again."),
        Make(@"Windows Driver Package|Driver Package|Virtual (Audio|Network|Display) (Adapter|Driver)|TAP-Windows|WireGuard|Npcap|WinPcap",
            RiskLevel.Danger, "This is a driver package. Removing it can disable the hardware or network adapter that uses it."),
        Make(@"Microsoft Update Health Tools|Windows (Software Development Kit|Subsystem)|Windows Defender|Microsoft Defender",
            RiskLevel.Danger, "This belongs to Windows itself (updates or security). Removing it can break Windows Update or leave your PC unprotected."),

        Make(@"Microsoft (Windows Desktop Runtime|\.NET|ASP\.NET)|\.NET (Core |Desktop )?(Runtime|SDK|Host)|Windows Desktop Runtime",
            RiskLevel.Caution, "Many programs need this runtime to start, including RigUninstaller itself. Removing it can stop them from opening."),
        Make(@"Visual C\+\+.*(Redistributable|Runtime)|Microsoft VC\+\+|C\+\+ \d{4} (x64|x86)",
            RiskLevel.Caution, "Many games and apps need this to start. Removing it can make them fail with a missing-DLL error."),
        Make(@"WebView2|Edge WebView|Microsoft Edge$",
            RiskLevel.Caution, "Other apps show web pages with this. Removing it can break their windows or sign-in screens."),
        Make(@"DirectX|XNA Framework|Vulkan Run ?Time|OpenAL|PhysX",
            RiskLevel.Caution, "Games use this to draw graphics, play sound or run physics. Removing it can stop them from launching."),
        Make(@"\bJava\b.*(Runtime|Update|SE|\d)|\bJRE\b|\bJDK\b|OpenJDK",
            RiskLevel.Caution, "Some programs (and games like Minecraft) need Java. Removing it can stop them from running."),
        Make(@"Kaspersky|Norton|McAfee|Bitdefender|\bAvast\b|\bAVG\b|Malwarebytes|\bESET\b|Sophos|Trend Micro|Windows Security",
            RiskLevel.Caution, "This is your security software. Removing it leaves the PC without protection until another one is on."),
        Make(@"Armoury Crate|ASUS (Framework|System Control|Link|Business|Optimization|Smart)|MyASUS|Lenovo (Vantage|System|Commercial)|Dell (Command|Update|SupportAssist|Optimizer)|HP (Support|System|Hotkey)|Razer (Synapse|Central)|Logitech (G HUB|Options)|SteelSeries (GG|Engine)|Corsair iCUE|iCUE|MSI (Center|Afterburner|Dragon)|Gigabyte Control|Predator ?Sense|Nahimic|Sonic Studio|Sonic Radar",
            RiskLevel.Caution, "This controls your hardware (fans, keyboard lighting, mouse, sound effects). Removing it can lose those features, and sometimes leaves a fan or power setting stuck."),
        Make(@"Intel.*(Software|Driver|Utility)|Realtek.*Software",
            RiskLevel.Caution, "A hardware helper from your PC's maker. Removing it can switch off features of that hardware."),
        Make(@"^Python \d|Python Launcher",
            RiskLevel.Caution, "Other tools and scripts may rely on this Python. Removing it can make them stop working."),
    };

    /// <summary>The warning for this program, or null when removing it is an ordinary choice.</summary>
    public static RiskInfo? Assess(InstalledProgram program)
    {
        var name = program.Name;
        foreach (var rule in Rules)
        {
            if (rule.Pattern.IsMatch(name))
            {
                return new RiskInfo(rule.Level, rule.Reason);
            }
        }

        return null;
    }
}
