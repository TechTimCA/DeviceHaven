using System.Runtime.Versioning;
using DeviceHaven.Shared;
using Microsoft.Win32;

namespace DeviceHaven.Agent;

/// <summary>Settings typed into the installer, or passed as MSI properties.</summary>
public sealed record PendingConfig(string? ServerUrl, string? Fingerprint, string? EnrollmentCode);

/// <summary>Where the installer leaves its settings for the agent to pick up.</summary>
public interface IPendingConfigSource
{
    PendingConfig? Read();
    void Clear();

    /// <summary>Tells future installers (upgrades) that this PC is already set up.</summary>
    void MarkConfigured();
}

/// <summary>
/// The MSI writes SERVERURL, FINGERPRINT and ENROLLCODE to
/// HKLM\SOFTWARE\DeviceHaven\Agent\Pending, a key only SYSTEM and
/// Administrators can read. The agent copies them into its own protected
/// config on start-up and then deletes the key, so the code doesn't linger.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryPendingConfigSource : IPendingConfigSource
{
    private const string KeyPath = @"SOFTWARE\DeviceHaven\Agent\Pending";

    public PendingConfig? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        if (key is null) return null;
        return new PendingConfig(
            key.GetValue("ServerUrl") as string,
            key.GetValue("Fingerprint") as string,
            key.GetValue("EnrollmentCode") as string);
    }

    public void Clear() => Registry.LocalMachine.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);

    public void MarkConfigured()
    {
        using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\DeviceHaven\Agent");
        key.SetValue("Configured", 1, RegistryValueKind.DWord);
    }
}

public static class InstallerSettings
{
    /// <summary>
    /// Validates and applies installer settings. Returns null on success,
    /// or the reason they were rejected (logged to the Windows Event Log).
    /// </summary>
    public static string? Apply(PendingConfig pending, AgentStore store)
    {
        if (string.IsNullOrWhiteSpace(pending.ServerUrl)
            || !Uri.TryCreate(pending.ServerUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "SERVERURL must be an https:// address, for example https://devicehaven:8443";
        }

        var fingerprint = Fingerprint.Normalize(pending.Fingerprint ?? "");
        if (fingerprint.Length != 64)
            return "FINGERPRINT must be the server's SHA-256 fingerprint, shown in the console with each code";

        var config = store.LoadConfig();
        config.ServerUrl = uri.GetLeftPart(UriPartial.Authority);
        config.Fingerprint = fingerprint;
        if (!string.IsNullOrWhiteSpace(pending.EnrollmentCode))
            config.EnrollmentCode = pending.EnrollmentCode.Trim();
        store.SaveConfig(config);
        return null;
    }
}
