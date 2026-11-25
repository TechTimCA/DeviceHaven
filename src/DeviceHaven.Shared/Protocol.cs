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
    InventorySnapshot? Inventory);

public sealed record CheckInResponse(
    AgentSettings Settings,
    bool InventoryRequested,
    IReadOnlyList<AgentCommand> Commands);

/// <summary>Placeholder for stage 2+ jobs (install software, run script, ...).</summary>
public sealed record AgentCommand(Guid Id, string Kind, string? PayloadJson);

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
    bool ShowTrayIcon)
{
    public const int MinCheckInSeconds = 30;
    public const int MaxCheckInSeconds = 86_400;      // once a day
    public const int MinInventoryMinutes = 60;        // hourly
    public const int MaxInventoryMinutes = 10_080;    // weekly
    public const int MaxJitterPercent = 50;

    public static AgentSettings Default { get; } = new(
        CheckInSeconds: 300,
        InventoryMinutes: 1_440,
        JitterPercent: 10,
        LiveConnection: false,
        ShowTrayIcon: true);

    public AgentSettings Normalize() => this with
    {
        CheckInSeconds = Math.Clamp(CheckInSeconds, MinCheckInSeconds, MaxCheckInSeconds),
        InventoryMinutes = Math.Clamp(InventoryMinutes, MinInventoryMinutes, MaxInventoryMinutes),
        JitterPercent = Math.Clamp(JitterPercent, 0, MaxJitterPercent),
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
    DateTimeOffset CollectedAt);

public sealed record DiskInfo(string Name, long SizeBytes, long FreeBytes, bool? BitLockerOn);

public sealed record InstalledApp(string Name, string? Version, string? Publisher);

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
public sealed partial class ProtocolJson : JsonSerializerContext;
