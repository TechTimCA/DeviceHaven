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

    // Replaced by the signed-in admin's name once sign-in exists.
    private const string Actor = "local-admin";
}
