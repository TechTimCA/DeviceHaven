using System.Text.Json.Serialization;

namespace DeviceHaven.Shared;

/// <summary>
/// Messages exchanged between the agent and the server. The agent always
/// initiates the connection; the server never connects to an endpoint.
/// </summary>
public static class ProtocolVersion
{
    public const int Current = 1;
}

/// <summary>Sent once, by an agent that has a one-time enrolment code.</summary>
public sealed record EnrollRequest(
    string EnrollmentCode,
    string Hostname,
    string OsName,
    string OsVersion,
    string AgentVersion,
    int ProtocolVersion = Shared.ProtocolVersion.Current);

/// <summary>
/// The server's answer to a successful enrolment. <see cref="DeviceToken"/> is
/// shown exactly once; the server keeps only a hash of it.
/// </summary>
public sealed record EnrollResponse(
    Guid DeviceId,
    string DeviceToken,
    AgentSettings Settings);

/// <summary>
/// The regular "I'm alive" message. Inventory is only attached when it has
/// changed since the last one the server accepted.
/// </summary>
public sealed record CheckInRequest(
    string AgentVersion,
    DateTimeOffset AgentTime,
    string? InventoryHash,
    InventorySnapshot? Inventory,
    IReadOnlyList<CommandResult>? Results = null,
    string? UpdatesHash = null,
    UpdateReport? Updates = null);

public sealed record CheckInResponse(
    AgentSettings Settings,
    bool InventoryRequested,
    IReadOnlyList<AgentCommand> Commands,
    bool UpdatesRequested = false);

/// <summary>
/// A job the server asks the agent to do. Only the kinds in
/// <see cref="CommandKinds"/> exist; the agent ignores anything else.
/// </summary>
public sealed record AgentCommand(Guid Id, string Kind, string? PayloadJson);

public static class CommandKinds
{
    public const string ScanInventory = "inventory.scan";
    public const string ScanUpdates = "updates.scan";
    public const string InstallAppUpdates = "apps.upgrade";
    public const string InstallWindowsUpdates = "windows.install";
    public const string Notify = "notify";
    public const string RequestRestart = "restart.request";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ScanInventory, ScanUpdates, InstallAppUpdates, InstallWindowsUpdates, Notify, RequestRestart,
    };
}

/// <summary>Payloads, one per command kind.</summary>
public sealed record InstallUpdatesPayload(IReadOnlyList<string> Keys);
public sealed record NotifyPayload(string Title, string Message);
public sealed record RestartPayload(int DeadlineMinutes, string? Reason);

/// <summary>What happened when the agent ran a command.</summary>
public sealed record CommandResult(
    Guid Id,
    bool Success,
    string? Message,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ItemResult>? Items = null);

/// <summary>The outcome for one update inside an install command.</summary>
public sealed record ItemResult(string Key, bool Success, string? Message);

/// <summary>Updates waiting on a PC: apps through winget, and Windows Update.</summary>
public sealed record UpdateReport(
    IReadOnlyList<AppUpdate> Apps,
    IReadOnlyList<WindowsUpdate> Windows,
    DateTimeOffset ScannedAt,
    bool RebootPending,
    string? AppScanError = null,
    string? WindowsScanError = null);

/// <summary>An app with a newer version available. <see cref="Id"/> is the winget package id.</summary>
public sealed record AppUpdate(string Id, string Name, string CurrentVersion, string AvailableVersion, string? Source);

public sealed record WindowsUpdate(
    string UpdateId,
    string Title,
    string? Kb,
    string? Severity,
    IReadOnlyList<string> Categories,
    long? SizeBytes,
    bool MayRequireRestart);

public static class UpdateKinds
{
    public const string App = "app";
    public const string Windows = "windows";
}

/// <summary>
/// Settings an admin controls from the dashboard, for all devices, a group or
/// one device. Values are always passed through <see cref="Normalize"/>, so
/// an agent can never be told to check in more often than the safe minimum.
/// </summary>
public sealed record AgentSettings(
    int CheckInSeconds,
    int InventoryMinutes,
    int JitterPercent,
    bool LiveConnection,
    bool ShowTrayIcon,
    int UpdateScanHours = AgentSettings.DefaultUpdateScanHours,
    int RestartDeadlineHours = AgentSettings.DefaultRestartDeadlineHours)
{
    public const int MinCheckInSeconds = 30;
    public const int MaxCheckInSeconds = 86_400;      // once a day
    public const int MinInventoryMinutes = 15;
    public const int MaxInventoryMinutes = 10_080;    // weekly
    public const int MaxJitterPercent = 50;
    public const int DefaultUpdateScanHours = 4;
    public const int DefaultRestartDeadlineHours = 24;

    public static AgentSettings Default { get; } = new(
        CheckInSeconds: 60,
        InventoryMinutes: 60,
        JitterPercent: 10,
        LiveConnection: false,
        ShowTrayIcon: true);

    public AgentSettings Normalize() => this with
    {
        CheckInSeconds = Math.Clamp(CheckInSeconds, MinCheckInSeconds, MaxCheckInSeconds),
        InventoryMinutes = Math.Clamp(InventoryMinutes, MinInventoryMinutes, MaxInventoryMinutes),
        JitterPercent = Math.Clamp(JitterPercent, 0, MaxJitterPercent),
        UpdateScanHours = Math.Clamp(UpdateScanHours <= 0 ? DefaultUpdateScanHours : UpdateScanHours, 1, 72),
        RestartDeadlineHours = Math.Clamp(RestartDeadlineHours <= 0 ? DefaultRestartDeadlineHours : RestartDeadlineHours, 1, 168),
    };

    /// <summary>
    /// The next wait, with random jitter so that many devices do not all
    /// check in at the same moment.
    /// </summary>
    public TimeSpan NextCheckInDelay(Random random)
    {
        var s = Normalize();
        var spread = s.CheckInSeconds * s.JitterPercent / 100.0;
        var seconds = s.CheckInSeconds + ((random.NextDouble() * 2) - 1) * spread;
        return TimeSpan.FromSeconds(Math.Max(MinCheckInSeconds, seconds));
    }
}

public sealed record InventorySnapshot(
    string Hostname,
    string OsName,
    string OsVersion,
    string? OsBuild,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? CpuName,
    int CpuCores,
    long TotalMemoryBytes,
    string? LoggedOnUser,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<InstalledApp> Apps,
    DateTimeOffset CollectedAt,
    IReadOnlyList<NetworkAdapter>? Networks = null)
{
    /// <summary>
    /// The address people think of as "the PC's IP": the IPv4 address on the
    /// adapter that has a default gateway, else the first IPv4 address found.
    /// </summary>
    public string? PrimaryIpv4 =>
        Networks?.FirstOrDefault(n => n.Gateway is not null && n.Ipv4.Count > 0)?.Ipv4[0]
        ?? Networks?.FirstOrDefault(n => n.Ipv4.Count > 0)?.Ipv4[0];
}

public sealed record DiskInfo(string Name, long SizeBytes, long FreeBytes, bool? BitLockerOn);

public sealed record InstalledApp(string Name, string? Version, string? Publisher);

public sealed record NetworkAdapter(
    string Name,
    string? MacAddress,
    IReadOnlyList<string> Ipv4,
    IReadOnlyList<string> Ipv6,
    string? Gateway,
    string Type,
    long? SpeedMbps);

/// <summary>Problem response body used by every API error.</summary>
public sealed record ApiError(string Code, string Message);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(CheckInRequest))]
[JsonSerializable(typeof(CheckInResponse))]
[JsonSerializable(typeof(AgentSettings))]
[JsonSerializable(typeof(InventorySnapshot))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(UpdateReport))]
[JsonSerializable(typeof(InstallUpdatesPayload))]
[JsonSerializable(typeof(NotifyPayload))]
[JsonSerializable(typeof(RestartPayload))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(List<CommandResult>))]
public sealed partial class ProtocolJson : JsonSerializerContext;
