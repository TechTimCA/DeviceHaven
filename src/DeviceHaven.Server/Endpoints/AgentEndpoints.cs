using DeviceHaven.Server.Data;
using DeviceHaven.Shared;
using Microsoft.AspNetCore.Mvc;

namespace DeviceHaven.Server.Endpoints;

/// <summary>The only endpoints agents can reach.</summary>
public static class AgentEndpoints
{
    public const string EnrollRateLimit = "enroll";
    private const long MaxCheckInBytes = 4 * 1024 * 1024;

    public static void MapAgentEndpoints(this WebApplication app)
    {
        var agent = app.MapGroup("/api/agent");

        agent.MapPost("/enroll", (EnrollRequest request, HttpContext http, Store store, ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(request.EnrollmentCode) || string.IsNullOrWhiteSpace(request.Hostname))
                return Error(StatusCodes.Status400BadRequest, "invalid_request", "Enrolment code and hostname are required.");

            if (request.ProtocolVersion != ProtocolVersion.Current)
                return Error(StatusCodes.Status400BadRequest, "protocol_mismatch",
                    $"This server speaks protocol {ProtocolVersion.Current}; the agent sent {request.ProtocolVersion}.");

            var ip = http.Connection.RemoteIpAddress?.ToString();
            var result = store.Enroll(request, ip);
            if (result is null)
            {
                store.Audit($"agent@{ip}", "enroll.rejected", request.Hostname, "Unknown, used or expired code");
                log.LogWarning("Rejected enrolment from {Ip} for {Host}", ip, request.Hostname);
                // Same answer for every kind of bad code, so it can't be probed.
                return Error(StatusCodes.Status403Forbidden, "enrollment_refused", "That enrolment code is not valid.");
            }

            store.Audit($"agent@{ip}", "device.enrolled", result.DeviceId.ToString(), request.Hostname);
            log.LogInformation("Enrolled {Host} as {DeviceId}", request.Hostname, result.DeviceId);
            return Results.Ok(result);
        }).RequireRateLimiting(EnrollRateLimit);

        agent.MapPost("/checkin", (CheckInRequest request, HttpContext http, Store store) =>
        {
            var device = store.Authenticate(BearerToken(http));
            if (device is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthorized", "Device token not recognised.");

            string? storedHash = device.InventoryHash;
            if (request.Inventory is not null)
            {
                storedHash = InventoryHash.Compute(request.Inventory);
            }

            store.RecordCheckIn(device.Id, request.AgentVersion, http.Connection.RemoteIpAddress?.ToString(),
                request.Inventory, storedHash);

            // Ask for a full inventory if the server doesn't hold the version the agent has.
            var inventoryRequested = request.InventoryHash is not null
                && !string.Equals(request.InventoryHash, storedHash, StringComparison.OrdinalIgnoreCase);

            return Results.Ok(new CheckInResponse(store.GetAgentSettings(), inventoryRequested, []));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxCheckInBytes));
    }

    internal static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    internal static IResult Error(int status, string code, string message) =>
        Results.Json(new ApiError(code, message), ProtocolJson.Default.ApiError, statusCode: status);
}
