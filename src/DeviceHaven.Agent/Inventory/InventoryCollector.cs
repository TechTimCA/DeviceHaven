using DeviceHaven.Shared;

namespace DeviceHaven.Agent.Inventory;

public interface IInventoryCollector
{
    InventorySnapshot Collect();
}

/// <summary>Picks the right collector for the operating system.</summary>
public static class InventoryCollectorFactory
{
    public static IInventoryCollector Create(ILoggerFactory logs) =>
        OperatingSystem.IsWindows()
            ? new WindowsInventoryCollector(logs.CreateLogger<WindowsInventoryCollector>())
            : new BasicInventoryCollector();
}

/// <summary>
/// Facts .NET can read on any OS. Used for development and as a fallback
/// when a Windows query fails.
/// </summary>
public sealed class BasicInventoryCollector : IInventoryCollector
{
    public InventorySnapshot Collect() => new(
        Hostname: Environment.MachineName,
        OsName: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        OsVersion: Environment.OSVersion.Version.ToString(),
        OsBuild: null,
        Manufacturer: null,
        Model: null,
        SerialNumber: null,
        CpuName: null,
        CpuCores: Environment.ProcessorCount,
        TotalMemoryBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        LoggedOnUser: null,
        Disks: ReadDisks(),
        Apps: [],
        CollectedAt: DateTimeOffset.UtcNow);

    internal static List<DiskInfo> ReadDisks() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DiskInfo(d.Name, d.TotalSize, d.AvailableFreeSpace, null))
            .ToList();
}
