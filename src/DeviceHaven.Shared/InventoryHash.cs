using System.Security.Cryptography;
using System.Text.Json;

namespace DeviceHaven.Shared;

/// <summary>
/// A stable hash of an inventory snapshot, ignoring when it was collected.
/// The agent sends the hash on every check-in and the full inventory only
/// when the hash changes, which keeps check-ins tiny.
/// </summary>
public static class InventoryHash
{
    public static string Compute(InventorySnapshot snapshot)
    {
        var stable = snapshot with
        {
            CollectedAt = default,
            Disks = snapshot.Disks
                .Select(d => d with { FreeBytes = RoundToGigabyte(d.FreeBytes) })
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Apps = snapshot.Apps
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Version, StringComparer.OrdinalIgnoreCase).ToList(),
            // Link speed can wobble on Wi-Fi; an address or adapter change still counts.
            Networks = snapshot.Networks?
                .Select(n => n with { SpeedMbps = null })
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(stable, ProtocolJson.Default.InventorySnapshot);
        return Convert.ToHexString(SHA256.HashData(json));
    }

    // Free space changes constantly; only count whole-gigabyte changes.
    private static long RoundToGigabyte(long bytes) => bytes / 1_073_741_824L;
}
