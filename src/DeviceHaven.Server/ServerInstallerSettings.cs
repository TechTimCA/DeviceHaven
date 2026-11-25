using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DeviceHaven.Server;

/// <summary>
/// Settings chosen in the MSI (PORT, PUBLICURL), stored under
/// HKLM\SOFTWARE\DeviceHaven\Server. They override appsettings.json, so an
/// upgrade that replaces appsettings.json never loses them.
/// </summary>
public static class ServerInstallerSettings
{
    [SupportedOSPlatform("windows")]
    public static IEnumerable<KeyValuePair<string, string?>> ReadFromRegistry()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\DeviceHaven\Server");
        if (key is null) yield break;

        if (key.GetValue("Port") is string port && int.TryParse(port, out var p) && p is > 0 and < 65536)
            yield return new($"{ServerOptions.Section}:Port", p.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (key.GetValue("PublicUrl") is string url && !string.IsNullOrWhiteSpace(url))
            yield return new($"{ServerOptions.Section}:PublicUrl", url.Trim());
    }
}
