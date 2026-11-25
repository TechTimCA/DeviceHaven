using System.Text.Json.Serialization;

namespace DeviceHaven.Shared;

/// <summary>
/// The tray app talks to the agent service over a named pipe on the same PC.
/// It can only ask for status and ask for a restart; it can never run
/// commands or change settings.
/// </summary>
public static class TrayPipe
{
    public const string Name = "DeviceHaven.Agent.Tray";
}

public sealed record TrayRequest(string Action, Guid? Id = null);

public static class TrayActions
{
    public const string Status = "status";
    public const string RestartNow = "restartNow";
}

public sealed record TrayNotification(Guid Id, string Kind, string Title, string Message, DateTimeOffset CreatedAt);

public static class NotificationKinds
{
    public const string Message = "message";
    public const string UpdatesInstalled = "updates";
    public const string Restart = "restart";
}

public sealed record RestartInfo(DateTimeOffset Deadline, string? Reason, bool Initiated);

public sealed record TrayStatus(
    string DeviceName,
    string? ServerUrl,
    bool Enrolled,
    bool Connected,
    DateTimeOffset? LastCheckIn,
    bool ShowTrayIcon,
    int UpdatesWaiting,
    RestartInfo? Restart,
    IReadOnlyList<TrayNotification> Notifications);

public sealed record TrayReply(bool Ok, string? Message, TrayStatus? Status);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TrayRequest))]
[JsonSerializable(typeof(TrayReply))]
public sealed partial class TrayJson : JsonSerializerContext;
