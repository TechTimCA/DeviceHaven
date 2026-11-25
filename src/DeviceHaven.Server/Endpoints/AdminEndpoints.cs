using System.Net;
using DeviceHaven.Server.Data;
using DeviceHaven.Server.Security;
using DeviceHaven.Shared;
using Microsoft.Extensions.Options;

namespace DeviceHaven.Server.Endpoints;

public sealed record CreateEnrollmentCodeRequest(int? ValidHours, string? Note, int? MaxUses);

public sealed record EnrollmentCodeResponse(
    string Code,
    DateTimeOffset ExpiresAt,
    int MaxUses,
    string ServerUrl,
    string Fingerprint,
    string InstallCommand);

public sealed record DeviceCommandRequest(string Kind, string? Title, string? Message, int? DeadlineMinutes);

public sealed record BroadcastRequest(string Kind, string? Title, string? Message, int? DeadlineMinutes, IReadOnlyList<Guid>? DeviceIds);

public sealed record ApproveRequest(IReadOnlyList<ApprovalItem> Items);

public sealed record ServerInfo(string Version, string ServerUrl, string Fingerprint, int DeviceCount);

/// <summary>
/// Dashboard API. Until admin sign-in with two-step verification lands,
/// these endpoints only answer requests made on the server itself.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter(async (context, next) =>
        {
            var ip = context.HttpContext.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip))
            {
                return AgentEndpoints.Error(StatusCodes.Status403Forbidden, "local_only",
                    "The admin API is only available on the server itself until admin sign-in is enabled.");
            }
            return await next(context);
        });

        admin.MapGet("/server", (Store store, CertificateProvider certs, IOptions<ServerOptions> options) =>
            new ServerInfo(
                typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                options.Value.ResolvePublicUrl(),
                Fingerprint.Display(certs.Fingerprint),
                store.ListDevices().Count(d => !d.Revoked)));

        admin.MapPost("/enrollment-codes", (CreateEnrollmentCodeRequest request, Store store,
            CertificateProvider certs, IOptions<ServerOptions> options) =>
        {
            var hours = Math.Clamp(request.ValidHours ?? 24, 1, 168);
            var uses = Math.Clamp(request.MaxUses ?? 1, 1, 1000);
            var (code, expires) = store.CreateEnrollmentCode(TimeSpan.FromHours(hours), request.Note, uses);
            var url = options.Value.ResolvePublicUrl();
            var fingerprint = certs.Fingerprint;
            store.Audit(Actor, "enrollment_code.created", null, $"Valid {hours}h for {uses} PC(s){(request.Note is null ? "" : $": {request.Note}")}");
            return Results.Ok(new EnrollmentCodeResponse(
                code, expires, uses, url, Fingerprint.Display(fingerprint),
                $"msiexec /i DeviceHaven-Agent.msi /qn SERVERURL=\"{url}\" ENROLLCODE=\"{code}\" FINGERPRINT=\"{fingerprint}\""));
        });

        admin.MapGet("/devices", (Store store) => store.ListDevices());

        admin.MapGet("/devices/{id:guid}", (Guid id, Store store) =>
            store.GetDevice(id) is { } device ? Results.Ok(device) : Results.NotFound());

        admin.MapDelete("/devices/{id:guid}", (Guid id, Store store) =>
        {
            if (!store.RevokeDevice(id)) return Results.NotFound();
            store.Audit(Actor, "device.revoked", id.ToString());
            return Results.NoContent();
        });

        admin.MapGet("/devices/{id:guid}/updates", (Guid id, Store store) => store.GetDeviceUpdates(id));

        admin.MapGet("/devices/{id:guid}/commands", (Guid id, Store store) => store.ListCommands(id));

        // Scan now, send a message, ask for a restart: one PC.
        admin.MapPost("/devices/{id:guid}/commands", (Guid id, DeviceCommandRequest request, Store store) =>
        {
            if (store.GetDevice(id) is not { Device.Revoked: false }) return Results.NotFound();
            var (payload, error) = BuildPayload(request.Kind, request.Title, request.Message, request.DeadlineMinutes);
            if (error is not null) return AgentEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_command", error);
            var commandId = store.CreateCommand(id, request.Kind, payload, Actor);
            store.Audit(Actor, $"command.{request.Kind}", id.ToString(), Describe(request.Kind, request.Title, request.Message, request.DeadlineMinutes));
            return Results.Ok(new { id = commandId });
        });

        // The same, for several PCs or all of them.
        admin.MapPost("/commands/broadcast", (BroadcastRequest request, Store store) =>
        {
            var (payload, error) = BuildPayload(request.Kind, request.Title, request.Message, request.DeadlineMinutes);
            if (error is not null) return AgentEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_command", error);
            var targets = store.ListDevices().Where(d => !d.Revoked)
                .Where(d => request.DeviceIds is not { Count: > 0 } || request.DeviceIds.Contains(d.Id)).ToList();
            foreach (var device in targets) store.CreateCommand(device.Id, request.Kind, payload, Actor);
            store.Audit(Actor, $"command.{request.Kind}", $"{targets.Count} device(s)", Describe(request.Kind, request.Title, request.Message, request.DeadlineMinutes));
            return Results.Ok(new { devices = targets.Count });
        });

        admin.MapGet("/updates", (Store store) => store.ListUpdateGroups());

        admin.MapPost("/updates/approve", (ApproveRequest request, Store store) =>
        {
            var count = store.Approve(request.Items ?? [], Actor);
            store.Audit(Actor, "updates.approved", null,
                string.Join(", ", (request.Items ?? []).Take(20).Select(i => $"{i.Kind}:{i.Key}")) + $" ({count} install(s))");
            return Results.Ok(new { installs = count });
        });

        admin.MapGet("/settings/agent", (Store store) => store.GetAgentSettings());

        admin.MapPut("/settings/agent", (AgentSettings settings, Store store) =>
        {
            var saved = store.SaveAgentSettings(settings);
            store.Audit(Actor, "settings.agent.changed", null,
                $"Check-in {saved.CheckInSeconds}s, inventory {saved.InventoryMinutes}m, jitter {saved.JitterPercent}%");
            return saved;
        });

        admin.MapGet("/audit", (int? take, Store store) => store.ReadAudit(take ?? 100));
    }

    /// <summary>Only the safe kinds can be sent this way; installs go through approvals.</summary>
    internal static (string? Payload, string? Error) BuildPayload(string kind, string? title, string? message, int? deadlineMinutes)
    {
        switch (kind)
        {
            case CommandKinds.ScanInventory or CommandKinds.ScanUpdates:
                return (null, null);
            case CommandKinds.Notify:
                if (string.IsNullOrWhiteSpace(message)) return (null, "A message is required.");
                if (message.Length > 500) return (null, "Messages can be up to 500 characters.");
                var t = string.IsNullOrWhiteSpace(title) ? "Message from IT" : title.Trim();
                if (t.Length > 80) return (null, "Titles can be up to 80 characters.");
                return (System.Text.Json.JsonSerializer.Serialize(new NotifyPayload(t, message.Trim()), ProtocolJson.Default.NotifyPayload), null);
            case CommandKinds.RequestRestart:
                var minutes = deadlineMinutes ?? 240;
                if (minutes is < 5 or > 10_080) return (null, "The restart deadline must be between 5 minutes and 7 days.");
                var reason = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
                if (reason is { Length: > 200 }) return (null, "The reason can be up to 200 characters.");
                return (System.Text.Json.JsonSerializer.Serialize(new RestartPayload(minutes, reason), ProtocolJson.Default.RestartPayload), null);
            default:
                return (null, $"'{kind}' can't be sent this way.");
        }
    }

    private static string Describe(string kind, string? title, string? message, int? deadlineMinutes) => kind switch
    {
        CommandKinds.Notify => $"{title ?? "Message from IT"}: {message}",
        CommandKinds.RequestRestart => $"Restart within {deadlineMinutes ?? 240} minutes{(message is null ? "" : $": {message}")}",
        _ => "",
    };

    // Replaced by the signed-in admin's name once sign-in exists.
    private const string Actor = "local-admin";
}
