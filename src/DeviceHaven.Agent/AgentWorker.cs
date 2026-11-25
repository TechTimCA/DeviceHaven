using System.Diagnostics;
using DeviceHaven.Agent.Inventory;
using DeviceHaven.Shared;

namespace DeviceHaven.Agent;

/// <summary>
/// The agent's main loop: enrol once, then check in on the schedule the
/// server sets. Mostly it sleeps, so it uses almost no CPU.
/// </summary>
public sealed class AgentWorker(
    AgentStore store,
    IInventoryCollector collector,
    ILogger<AgentWorker> log,
    Func<AgentConfig, ServerClient>? clientFactory = null) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private readonly Random _random = new();
    private readonly Func<AgentConfig, ServerClient> _clientFactory =
        clientFactory ?? (c => new ServerClient(c.ServerUrl!, c.Fingerprint!));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LowerPriority();
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
                log.LogError("Agent is not configured: {Reason}. Retrying in 5 minutes.", ex.Message);
                wait = TimeSpan.FromMinutes(5);
            }
            catch (Exception ex)
            {
                failures++;
                wait = Backoff(failures);
                log.LogWarning(ex, "Check-in failed ({Failures} in a row). Next try in {Wait}.", failures, wait);
            }

            try { await Task.Delay(wait, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One enrol-or-check-in cycle. Returns how long to wait before the next.</summary>
    public async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        var config = store.LoadConfig();
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.Fingerprint))
            throw new AgentNotConfiguredException("server address or fingerprint missing from agent.json");

        var state = store.LoadState();
        var token = store.LoadToken();
        using var client = _clientFactory(config);

        if (token is null || state.DeviceId is null)
        {
            if (string.IsNullOrWhiteSpace(config.EnrollmentCode))
                throw new AgentNotConfiguredException("not enrolled and no enrolment code was provided");

            var snapshot = collector.Collect();
            var enrolled = await client.EnrollAsync(new EnrollRequest(
                config.EnrollmentCode, snapshot.Hostname, snapshot.OsName, snapshot.OsVersion, AgentInfo.Version), ct).ConfigureAwait(false);

            store.SaveToken(enrolled.DeviceToken);
            state = new AgentStateData { DeviceId = enrolled.DeviceId, Settings = enrolled.Settings };
            store.SaveState(state);

            // The code is single-use; don't keep it lying around.
            config.EnrollmentCode = null;
            store.SaveConfig(config);
            token = enrolled.DeviceToken;
            log.LogInformation("Enrolled with the server as device {DeviceId}.", enrolled.DeviceId);
        }

        var settings = (state.Settings ?? AgentSettings.Default).Normalize();

        // Collect inventory when it's due; only send it if it changed.
        InventorySnapshot? inventory = null;
        var hash = state.LastInventoryHash;
        var due = state.LastInventoryAt is null
            || DateTimeOffset.UtcNow - state.LastInventoryAt > TimeSpan.FromMinutes(settings.InventoryMinutes);
        if (due)
        {
            var collected = collector.Collect();
            var newHash = InventoryHash.Compute(collected);
            state.LastInventoryAt = DateTimeOffset.UtcNow;
            if (!string.Equals(newHash, hash, StringComparison.Ordinal))
            {
                inventory = collected;
                hash = newHash;
            }
        }

        var response = await CheckInWithRetryForInventoryAsync(client, token, hash, inventory, ct).ConfigureAwait(false);

        state.LastInventoryHash = hash;
        state.Settings = response.Settings.Normalize();
        store.SaveState(state);

        return state.Settings.NextCheckInDelay(_random);
    }

    private async Task<CheckInResponse> CheckInWithRetryForInventoryAsync(
        ServerClient client, string token, string? hash, InventorySnapshot? inventory, CancellationToken ct)
    {
        try
        {
            var response = await client.CheckInAsync(token,
                new CheckInRequest(AgentInfo.Version, DateTimeOffset.UtcNow, hash, inventory), ct).ConfigureAwait(false);

            // The server lost or never got our inventory (e.g. restored from backup): send it now.
            if (response.InventoryRequested && inventory is null)
            {
                var full = collector.Collect();
                response = await client.CheckInAsync(token,
                    new CheckInRequest(AgentInfo.Version, DateTimeOffset.UtcNow, InventoryHash.Compute(full), full), ct).ConfigureAwait(false);
            }
            return response;
        }
        catch (ServerRejectedException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            log.LogError("The server no longer recognises this device (it may have been removed). Re-install the agent with a new enrolment code.");
            throw;
        }
    }

    private TimeSpan Backoff(int failures)
    {
        var seconds = Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, Math.Min(failures - 1, 10)));
        return TimeSpan.FromSeconds(seconds * (0.8 + _random.NextDouble() * 0.4));
    }

    private void LowerPriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) { log.LogDebug(ex, "Could not lower process priority"); }
    }
}

public sealed class AgentNotConfiguredException(string message) : Exception(message);
