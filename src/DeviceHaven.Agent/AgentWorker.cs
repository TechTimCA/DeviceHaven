using System.Diagnostics;
using DeviceHaven.Agent.Inventory;
using DeviceHaven.Agent.Updates;
using DeviceHaven.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceHaven.Agent;

/// <summary>
/// The agent's main loop: enrol once, then check in on the schedule the
/// server sets. Jobs from the server run separately (see
/// <see cref="CommandRunner"/>), so a long install never delays a check-in.
/// Between check-ins the agent sleeps and uses almost no CPU.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private readonly Random _random = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly AgentStore _store;
    private readonly IInventoryCollector _collector;
    private readonly ILogger<AgentWorker> _log;
    private readonly Func<AgentConfig, ServerClient> _clientFactory;
    private readonly IPendingConfigSource? _installerSettings;
    private readonly CommandRunner _runner;
    private readonly ISystemActions _system;

    public AgentWorker(
        AgentStore store,
        IInventoryCollector collector,
        ILogger<AgentWorker> log,
        Func<AgentConfig, ServerClient>? clientFactory = null,
        IPendingConfigSource? installerSettings = null,
        CommandRunner? runner = null,
        ISystemActions? system = null)
    {
        _store = store;
        _collector = collector;
        _log = log;
        _clientFactory = clientFactory ?? (c => new ServerClient(c.ServerUrl!, c.Fingerprint!));
        _installerSettings = installerSettings;
        _runner = runner ?? new CommandRunner(store, new UpdateService(NullLogger<UpdateService>.Instance), NullLogger<CommandRunner>.Instance);
        _system = system ?? new SystemActions(NullLogger<SystemActions>.Instance);
        _runner.WorkFinished += Wake;
    }

    public CommandRunner Runner => _runner;

    /// <summary>Check in early: a job finished, so the server should hear about it soon.</summary>
    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LowerPriority();
        var jobs = Task.Run(() => _runner.RunAsync(stoppingToken), stoppingToken);
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentNotConfiguredException ex)
            {
                _log.LogError("Agent is not configured: {Reason}. Retrying in 5 minutes.", ex.Message);
                wait = TimeSpan.FromMinutes(5);
            }
            catch (Exception ex)
            {
                failures++;
                wait = Backoff(failures);
                _log.LogWarning(ex, "Check-in failed ({Failures} in a row). Next try in {Wait}.", failures, wait);
            }

            try
            {
                // Sleep until the next check-in, or until a job finishes (whichever is first),
                // but never more often than every 5 seconds.
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                await _wake.WaitAsync(wait - TimeSpan.FromSeconds(5) > TimeSpan.Zero ? wait - TimeSpan.FromSeconds(5) : TimeSpan.Zero, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        try { await jobs.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    /// <summary>One enrol-or-check-in cycle. Returns how long to wait before the next.</summary>
    public async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        ApplyInstallerSettings();
        var config = _store.LoadConfig();
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.Fingerprint))
            throw new AgentNotConfiguredException("server address or fingerprint missing from agent.json");

        var state = _store.ReadState();
        var token = _store.LoadToken();
        using var client = _clientFactory(config);

        if (token is null || state.DeviceId is null)
        {
            if (string.IsNullOrWhiteSpace(config.EnrollmentCode))
                throw new AgentNotConfiguredException("not enrolled and no enrolment code was provided");

            var snapshot = _collector.Collect();
            var enrolled = await client.EnrollAsync(new EnrollRequest(
                config.EnrollmentCode, snapshot.Hostname, snapshot.OsName, snapshot.OsVersion, AgentInfo.Version), ct).ConfigureAwait(false);

            _store.SaveToken(enrolled.DeviceToken);
            state = _store.Update(s =>
            {
                s.DeviceId = enrolled.DeviceId;
                s.Settings = enrolled.Settings;
                s.LastInventoryHash = null;
                s.LastInventoryAt = null;
                s.AcceptedUpdatesHash = null;
            });

            // The code is single-use; don't keep it lying around.
            config.EnrollmentCode = null;
            _store.SaveConfig(config);
            token = enrolled.DeviceToken;
            _log.LogInformation("Enrolled with the server as device {DeviceId}.", enrolled.DeviceId);
        }

        // Already enrolled: a code passed again (for example on an upgrade) isn't needed, so don't keep it.
        if (!string.IsNullOrEmpty(config.EnrollmentCode))
        {
            config.EnrollmentCode = null;
            _store.SaveConfig(config);
        }

        var settings = (state.Settings ?? AgentSettings.Default).Normalize();
        HandleRestart(state);

        // Collect inventory when it's due (or the admin asked); only send it if it changed.
        InventorySnapshot? inventory = null;
        var hash = state.LastInventoryHash;
        var collectedAt = state.LastInventoryAt;
        var due = state.ForceInventory || state.LastInventoryAt is null
            || DateTimeOffset.UtcNow - state.LastInventoryAt > TimeSpan.FromMinutes(settings.InventoryMinutes);
        if (due)
        {
            var collected = _collector.Collect();
            var newHash = InventoryHash.Compute(collected);
            collectedAt = DateTimeOffset.UtcNow;
            if (state.ForceInventory || !string.Equals(newHash, hash, StringComparison.Ordinal))
            {
                inventory = collected;
                hash = newHash;
            }
        }

        // Look for updates on the schedule the admin set, in the background.
        if (state.LastUpdateScanAt is null || DateTimeOffset.UtcNow - state.LastUpdateScanAt > TimeSpan.FromHours(settings.UpdateScanHours))
            _ = Task.Run(() => _runner.ScanUpdatesAsync(ct), ct);

        var results = state.PendingResults.ToList();
        var sendUpdates = state.Updates is not null && state.UpdatesHash != state.AcceptedUpdatesHash;
        var request = new CheckInRequest(AgentInfo.Version, DateTimeOffset.UtcNow, hash, inventory,
            results.Count > 0 ? results : null, state.UpdatesHash, sendUpdates ? state.Updates : null);

        var response = await CheckInAsync(client, token, request, ct).ConfigureAwait(false);

        var sentIds = results.Select(r => r.Id).ToHashSet();
        state = _store.Update(s =>
        {
            s.PendingResults.RemoveAll(r => sentIds.Contains(r.Id));
            s.LastInventoryHash = hash;
            s.LastInventoryAt = collectedAt;
            if (inventory is not null) s.ForceInventory = false;
            s.Settings = response.Settings.Normalize();
            s.LastCheckInAt = DateTimeOffset.UtcNow;
            if (sendUpdates) s.AcceptedUpdatesHash = request.UpdatesHash;
            if (response.UpdatesRequested) s.AcceptedUpdatesHash = null;   // send the full list next time
        });

        foreach (var command in response.Commands) _runner.Enqueue(command);

        return state.Settings!.NextCheckInDelay(_random);
    }

    private async Task<CheckInResponse> CheckInAsync(ServerClient client, string token, CheckInRequest request, CancellationToken ct)
    {
        try
        {
            var response = await client.CheckInAsync(token, request, ct).ConfigureAwait(false);

            // The server lost or never got our inventory (e.g. restored from backup): send it now.
            if (response.InventoryRequested && request.Inventory is null)
            {
                var full = _collector.Collect();
                response = await client.CheckInAsync(token,
                    request with { InventoryHash = InventoryHash.Compute(full), Inventory = full, Results = null, Updates = null }, ct).ConfigureAwait(false);
            }
            return response;
        }
        catch (ServerRejectedException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            _log.LogError("The server no longer recognises this device (it may have been removed). Re-install the agent with a new enrolment code.");
            throw;
        }
    }

    /// <summary>
    /// Clears a restart request once the PC has restarted; at the deadline,
    /// restarts with a 10-minute warning on screen.
    /// </summary>
    private void HandleRestart(AgentStateData state)
    {
        if (state.Restart is not { } restart) return;

        if (_system.BootTime > restart.RequestedAt)
        {
            _store.Update(s => s.Restart = null);
            return;
        }

        if (!restart.Initiated && DateTimeOffset.UtcNow >= restart.Deadline)
        {
            _log.LogInformation("Restart deadline reached; restarting in 10 minutes");
            _system.Restart(TimeSpan.FromMinutes(10),
                "Your PC will restart in 10 minutes to finish updates. Please save your work. (DeviceHaven)");
            _store.Update(s => { if (s.Restart is not null) s.Restart.Initiated = true; });
        }
    }

    /// <summary>Picks up settings left by the MSI (first install, or a reinstall with new values).</summary>
    private void ApplyInstallerSettings()
    {
        var pending = _installerSettings?.Read();
        if (pending is null) return;
        var error = InstallerSettings.Apply(pending, _store);
        if (error is null)
        {
            _installerSettings!.MarkConfigured();
            _log.LogInformation("Applied settings from the installer. Server: {Server}", pending.ServerUrl);
        }
        else
        {
            _log.LogError("Installer settings were rejected: {Reason}", error);
        }
        _installerSettings!.Clear();
    }

    private TimeSpan Backoff(int failures)
    {
        var seconds = Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, Math.Min(failures - 1, 10)));
        return TimeSpan.FromSeconds(seconds * (0.8 + _random.NextDouble() * 0.4));
    }

    private void LowerPriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) { _log.LogDebug(ex, "Could not lower process priority"); }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}

public sealed class AgentNotConfiguredException(string message) : Exception(message);
