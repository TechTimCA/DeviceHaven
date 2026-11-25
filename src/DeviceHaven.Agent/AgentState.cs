using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceHaven.Shared;

namespace DeviceHaven.Agent;

/// <summary>
/// Written by the installer: where the server is, its certificate
/// fingerprint, and (until enrolment succeeds) the one-time code.
/// </summary>
public sealed class AgentConfig
{
    public string? ServerUrl { get; set; }
    public string? Fingerprint { get; set; }
    public string? EnrollmentCode { get; set; }
}

/// <summary>What the agent remembers between restarts. The device token is kept separately, encrypted.</summary>
public sealed class AgentStateData
{
    public Guid? DeviceId { get; set; }
    public AgentSettings? Settings { get; set; }
    public string? LastInventoryHash { get; set; }
    public DateTimeOffset? LastInventoryAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(AgentStateData))]
internal sealed partial class AgentJson : JsonSerializerContext;

/// <summary>
/// Files under %ProgramData%\DeviceHaven\Agent. On Windows the device token
/// is encrypted with the machine's DPAPI key, so it can't be copied to
/// another PC and reused.
/// </summary>
public sealed class AgentStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeviceHaven.Agent.DeviceToken.v1");
    private readonly string _dir;

    public AgentStore(string? directory = null)
    {
        _dir = directory ?? DefaultDirectory();
        Directory.CreateDirectory(_dir);
    }

    public static string DefaultDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeviceHaven", "Agent")
        : Path.Combine(AppContext.BaseDirectory, "agent-data");

    private string ConfigPath => Path.Combine(_dir, "agent.json");
    private string StatePath => Path.Combine(_dir, "state.json");
    private string TokenPath => Path.Combine(_dir, "device.token");

    public AgentConfig LoadConfig() =>
        File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), AgentJson.Default.AgentConfig) ?? new AgentConfig()
            : new AgentConfig();

    public void SaveConfig(AgentConfig config) =>
        WriteAtomic(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, AgentJson.Default.AgentConfig));

    public AgentStateData LoadState() =>
        File.Exists(StatePath)
            ? JsonSerializer.Deserialize(File.ReadAllText(StatePath), AgentJson.Default.AgentStateData) ?? new AgentStateData()
            : new AgentStateData();

    public void SaveState(AgentStateData state) =>
        WriteAtomic(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, AgentJson.Default.AgentStateData));

    public string? LoadToken()
    {
        if (!File.Exists(TokenPath)) return null;
        var bytes = File.ReadAllBytes(TokenPath);
        return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? Unprotect(bytes) : bytes);
    }

    public void SaveToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        WriteAtomic(TokenPath, OperatingSystem.IsWindows() ? Protect(bytes) : bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine);

    private static void WriteAtomic(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
