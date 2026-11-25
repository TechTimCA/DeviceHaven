using System.Text.Json;
using System.Threading.Channels;
using DeviceHaven.Agent.Updates;
using DeviceHaven.Shared;

namespace DeviceHaven.Agent;

/// <summary>
/// Runs commands from the server one at a time, away from the check-in loop,
/// so a long Windows Update install never holds up check-ins. Results are
/// saved until the server confirms it has them.
/// </summary>
public sealed class CommandRunner(AgentStore store, IUpdateService updates, ILogger<CommandRunner> log)
{
    private readonly Channel<AgentCommand> _queue = Channel.CreateUnbounded<AgentCommand>();
    private readonly HashSet<Guid> _seen = [];
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    /// <summary>Raised when there's something new to send to the server.</summary>
    public event Action? WorkFinished;

    public void Enqueue(AgentCommand command)
    {
        lock (_seen)
        {
            if (!_seen.Add(command.Id)) return;   // never run the same command twice
        }
        _queue.Writer.TryWrite(command);
    }

    /// <summary>Runs queued commands until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var command in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            await ExecuteAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>Runs whatever is queued right now (used by tests).</summary>
    public async Task RunPendingAsync(CancellationToken ct)
    {
        while (_queue.Reader.TryRead(out var command)) await ExecuteAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>Scans for app and Windows updates and saves the report. One scan at a time.</summary>
    public async Task<UpdateReport?> ScanUpdatesAsync(CancellationToken ct)
    {
        if (!await _scanLock.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            log.LogInformation("Scanning for updates");
            var report = await updates.ScanAsync(ct).ConfigureAwait(false);
            var hash = InventoryHash.Compute(report);
            store.Update(s =>
            {
                s.Updates = report;
                s.UpdatesHash = hash;
                s.LastUpdateScanAt = DateTimeOffset.UtcNow;
            });
            log.LogInformation("Update scan found {Apps} app and {Windows} Windows update(s)", report.Apps.Count, report.Windows.Count);
            WorkFinished?.Invoke();
            return report;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task ExecuteAsync(AgentCommand command, CancellationToken ct)
    {
        CommandResult result;
        try
        {
            result = await RunCommandAsync(command, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;   // shutting down; the server will see the command was never finished
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Command {Kind} failed", command.Kind);
            result = new CommandResult(command.Id, false, ex.Message, DateTimeOffset.UtcNow);
        }

        store.Update(s => s.PendingResults.Add(result));
        WorkFinished?.Invoke();
    }

    private async Task<CommandResult> RunCommandAsync(AgentCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        switch (command.Kind)
        {
            case CommandKinds.ScanInventory:
                store.Update(s => s.ForceInventory = true);
                return new CommandResult(command.Id, true, "Inventory will be sent with the next check-in", now);

            case CommandKinds.ScanUpdates:
            {
                var report = await ScanUpdatesAsync(ct).ConfigureAwait(false);
                return report is null
                    ? new CommandResult(command.Id, true, "A scan was already running", now)
                    : new CommandResult(command.Id, true, $"Found {report.Apps.Count} app and {report.Windows.Count} Windows update(s)", DateTimeOffset.UtcNow);
            }

            case CommandKinds.Notify:
            {
                var payload = Parse(command, ProtocolJson.Default.NotifyPayload);
                store.Update(s => s.Notify(NotificationKinds.Message, Clip(payload.Title, 80), Clip(payload.Message, 500)));
                return new CommandResult(command.Id, true, "Shown to the signed-in user", now);
            }

            case CommandKinds.RequestRestart:
            {
                var payload = Parse(command, ProtocolJson.Default.RestartPayload);
                var minutes = Math.Clamp(payload.DeadlineMinutes, 5, 10_080);
                RequestRestart(TimeSpan.FromMinutes(minutes), payload.Reason ?? "Your IT admin asked for a restart.");
                return new CommandResult(command.Id, true, $"Restart requested within {minutes} minutes", now);
            }

            case CommandKinds.InstallAppUpdates:
            {
                var keys = ValidKeys(Parse(command, ProtocolJson.Default.InstallUpdatesPayload));
                var known = store.ReadState().Updates?.Apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase) ?? [];
                var items = await updates.InstallAppsAsync(keys, ct).ConfigureAwait(false);
                var installed = items.Where(i => i.Success).Select(i => known.TryGetValue(i.Key, out var a) ? a.Name : i.Key).ToList();
                if (installed.Count > 0)
                    store.Update(s => s.Notify(NotificationKinds.UpdatesInstalled, "Apps updated", Summarise(installed)));
                await ScanUpdatesAsync(ct).ConfigureAwait(false);
                return new CommandResult(command.Id, items.All(i => i.Success), $"{installed.Count} of {items.Count} updated", DateTimeOffset.UtcNow, items);
            }

            case CommandKinds.InstallWindowsUpdates:
            {
                var keys = ValidKeys(Parse(command, ProtocolJson.Default.InstallUpdatesPayload));
                var known = store.ReadState().Updates?.Windows.ToDictionary(w => w.UpdateId, StringComparer.OrdinalIgnoreCase) ?? [];
                var (items, reboot) = await updates.InstallWindowsAsync(keys, ct).ConfigureAwait(false);
                var installed = items.Where(i => i.Success && i.Message is "Installed" or "Installed with errors")
                    .Select(i => known.TryGetValue(i.Key, out var w) ? (w.Kb ?? w.Title) : i.Key).ToList();
                if (installed.Count > 0)
                    store.Update(s => s.Notify(NotificationKinds.UpdatesInstalled, "Windows updated", Summarise(installed)));
                if (reboot)
                {
                    var hours = (store.ReadState().Settings ?? AgentSettings.Default).Normalize().RestartDeadlineHours;
                    RequestRestart(TimeSpan.FromHours(hours), "Windows updates were installed and need a restart to finish.");
                }
                await ScanUpdatesAsync(ct).ConfigureAwait(false);
                return new CommandResult(command.Id, items.All(i => i.Success),
                    $"{installed.Count} of {items.Count} installed{(reboot ? "; restart needed" : "")}", DateTimeOffset.UtcNow, items);
            }

            default:
                return new CommandResult(command.Id, false, $"This agent doesn't know how to run '{command.Kind}'", now);
        }
    }

    /// <summary>Sets (or brings forward) a restart deadline and tells the user.</summary>
    public void RequestRestart(TimeSpan within, string reason)
    {
        var deadline = DateTimeOffset.UtcNow + within;
        store.Update(s =>
        {
            if (s.Restart is { Initiated: false } existing && existing.Deadline <= deadline) return;
            s.Restart = new RestartState { RequestedAt = DateTimeOffset.UtcNow, Deadline = deadline, Reason = reason };
            s.Notify(NotificationKinds.Restart, "Restart needed", $"{reason} Please restart by {deadline.ToLocalTime():ddd h:mm tt}.");
        });
    }

    private static T Parse<T>(AgentCommand command, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(command.PayloadJson ?? "", type) ?? throw new InvalidOperationException("Missing payload");

    private static List<string> ValidKeys(InstallUpdatesPayload payload) =>
        payload.Keys.Where(k => !string.IsNullOrWhiteSpace(k) && k.Length <= 128).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();

    private static string Summarise(IReadOnlyList<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
