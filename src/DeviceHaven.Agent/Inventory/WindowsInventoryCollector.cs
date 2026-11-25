using System.Management;
using System.Runtime.Versioning;
using DeviceHaven.Shared;
using Microsoft.Win32;

namespace DeviceHaven.Agent.Inventory;

/// <summary>
/// Reads hardware, Windows and installed-software details with WMI and the
/// registry. Every query is independent: if one fails, the rest still report.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInventoryCollector(ILogger<WindowsInventoryCollector> log) : IInventoryCollector
{
    public InventorySnapshot Collect()
    {
        var system = QueryFirst("SELECT Manufacturer, Model, UserName FROM Win32_ComputerSystem");
        var bios = QueryFirst("SELECT SerialNumber FROM Win32_BIOS");
        var cpu = QueryFirst("SELECT Name, NumberOfLogicalProcessors FROM Win32_Processor");
        var os = QueryFirst("SELECT Caption, Version, BuildNumber, TotalVisibleMemorySize FROM Win32_OperatingSystem");

        var memoryKb = AsLong(os?["TotalVisibleMemorySize"]);

        return new InventorySnapshot(
            Hostname: Environment.MachineName,
            OsName: AsString(os?["Caption"]) ?? "Windows",
            OsVersion: AsString(os?["Version"]) ?? Environment.OSVersion.Version.ToString(),
            OsBuild: ReadDisplayBuild() ?? AsString(os?["BuildNumber"]),
            Manufacturer: AsString(system?["Manufacturer"]),
            Model: AsString(system?["Model"]),
            SerialNumber: AsString(bios?["SerialNumber"]),
            CpuName: AsString(cpu?["Name"]),
            CpuCores: (int)(AsLong(cpu?["NumberOfLogicalProcessors"]) ?? Environment.ProcessorCount),
            TotalMemoryBytes: memoryKb is { } kb ? kb * 1024 : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            LoggedOnUser: AsString(system?["UserName"]),
            Disks: ReadDisks(),
            Apps: ReadInstalledApps(),
            CollectedAt: DateTimeOffset.UtcNow,
            Networks: BasicInventoryCollector.ReadNetworks());
    }

    private ManagementBaseObject? QueryFirst(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(20);
            foreach (var item in searcher.Get()) return item;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            log.LogWarning(ex, "WMI query failed: {Query}", wql);
        }
        return null;
    }

    private List<DiskInfo> ReadDisks()
    {
        var bitLocker = ReadBitLockerStatus();
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DiskInfo(d.Name, d.TotalSize, d.AvailableFreeSpace,
                bitLocker.TryGetValue(d.Name.TrimEnd('\\'), out var on) ? on : null))
            .ToList();
    }

    /// <summary>ProtectionStatus 1 = on. Needs the agent to run as SYSTEM, which the service does.</summary>
    private Dictionary<string, bool> ReadBitLockerStatus()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            foreach (var volume in searcher.Get())
            {
                if (AsString(volume["DriveLetter"]) is { } letter)
                    result[letter] = AsLong(volume["ProtectionStatus"]) == 1;
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            log.LogDebug(ex, "BitLocker status unavailable");
        }
        return result;
    }

    /// <summary>Installed programs from both the 64-bit and 32-bit uninstall lists.</summary>
    private List<InstalledApp> ReadInstalledApps()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;
                    var displayName = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (key.GetValue("ParentKeyName") is not null) continue; // updates and patches
                    var app = new InstalledApp(displayName.Trim(), key.GetValue("DisplayVersion") as string, key.GetValue("Publisher") as string);
                    apps.TryAdd($"{app.Name}|{app.Version}", app);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                log.LogWarning(ex, "Could not read installed programs ({View})", view);
            }
        }
        return apps.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"26H2"-style version name plus build, e.g. "26H2 (26300.1234)".</summary>
    private static string? ReadDisplayBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key is null) return null;
        var display = key.GetValue("DisplayVersion") as string;
        var build = key.GetValue("CurrentBuildNumber") as string;
        var ubr = key.GetValue("UBR") is int u ? $".{u}" : "";
        return display is null ? build : $"{display} ({build}{ubr})";
    }

    private static string? AsString(object? value) => value?.ToString()?.Trim() is { Length: > 0 } s ? s : null;

    private static long? AsLong(object? value) => value switch
    {
        null => null,
        IConvertible c => Convert.ToInt64(c, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };
}
