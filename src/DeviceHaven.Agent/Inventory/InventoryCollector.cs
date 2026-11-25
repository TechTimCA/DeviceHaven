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
        CollectedAt: DateTimeOffset.UtcNow,
        Networks: ReadNetworks());

    /// <summary>
    /// Active network adapters with their addresses. Uses .NET's own network
    /// API, so it works the same on Windows, macOS and Linux.
    /// </summary>
    internal static List<NetworkAdapter> ReadNetworks()
    {
        var list = new List<NetworkAdapter>();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;

            System.Net.NetworkInformation.IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (System.Net.NetworkInformation.NetworkInformationException) { continue; }

            var v4 = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).ToList();
            var v6 = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                .Select(a => a.Address.ToString()).ToList();
            if (v4.Count == 0 && v6.Count == 0) continue;

            var gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any))
                ?.ToString();

            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            long? speed = nic.Speed > 0 ? nic.Speed / 1_000_000 : null;

            list.Add(new NetworkAdapter(
                Name: nic.Name,
                MacAddress: mac.Length == 6 ? string.Join(':', mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))) : null,
                Ipv4: v4,
                Ipv6: v6,
                Gateway: gateway,
                Type: nic.NetworkInterfaceType switch
                {
                    System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet => "Ethernet",
                    _ => nic.NetworkInterfaceType.ToString(),
                },
                SpeedMbps: speed));
        }
        // Adapters with a gateway first: that's the one carrying normal traffic.
        return list.OrderBy(n => n.Gateway is null).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static List<DiskInfo> ReadDisks() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DiskInfo(d.Name, d.TotalSize, d.AvailableFreeSpace, null))
            .ToList();
}
