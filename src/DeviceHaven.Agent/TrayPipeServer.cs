using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeviceHaven.Shared;

namespace DeviceHaven.Agent;

/// <summary>
/// Answers the tray app over a local named pipe. Nothing on the network can
/// reach it, and the tray can only read status or ask for a restart.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayPipeServer(AgentStore store, ISystemActions system, ILogger<TrayPipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Tray connection failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>SYSTEM owns the pipe; signed-in users may connect to it. Nobody can connect from another PC.</summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(TrayPipe.Name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 65536, security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null || line.Length > 4096) return;

            var request = JsonSerializer.Deserialize(line, TrayJson.Default.TrayRequest);
            var reply = Handle(request);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, TrayJson.Default.TrayReply) + "\n");
            await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
        {
            log.LogDebug(ex, "Tray request ignored");
        }
    }

    public TrayReply Handle(TrayRequest? request)
    {
        switch (request?.Action)
        {
            case TrayActions.Status:
                return new TrayReply(true, null, BuildStatus());

            case TrayActions.RestartNow:
                store.Update(s =>
                {
                    if (s.Restart is not null) s.Restart.Initiated = true;
                });
                system.Restart(TimeSpan.FromSeconds(30), "DeviceHaven is restarting this PC to finish updates, as you asked.");
                return new TrayReply(true, "Restarting in 30 seconds", BuildStatus());

            default:
                return new TrayReply(false, "Unknown request", null);
        }
    }

    private TrayStatus BuildStatus()
    {
        var state = store.ReadState();
        var config = store.LoadConfig();
        var settings = (state.Settings ?? AgentSettings.Default).Normalize();
        var connected = state.LastCheckInAt is { } last
            && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(settings.CheckInSeconds * 3 + 120);
        return new TrayStatus(
            Environment.MachineName,
            config.ServerUrl,
            state.DeviceId is not null,
            connected,
            state.LastCheckInAt,
            settings.ShowTrayIcon,
            (state.Updates?.Apps.Count ?? 0) + (state.Updates?.Windows.Count ?? 0),
            state.Restart is { } r ? new RestartInfo(r.Deadline, r.Reason, r.Initiated) : null,
            state.Notifications.TakeLast(20).ToList());
    }
}
