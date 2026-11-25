using DeviceHaven.Shared;

namespace DeviceHaven.Agent.Updates;

/// <summary>Finds and installs app and Windows updates. Swapped for a fake in tests.</summary>
public interface IUpdateService
{
    Task<UpdateReport> ScanAsync(CancellationToken ct);
    Task<IReadOnlyList<ItemResult>> InstallAppsAsync(IReadOnlyList<string> ids, CancellationToken ct);
    Task<(IReadOnlyList<ItemResult> Results, bool RebootRequired)> InstallWindowsAsync(IReadOnlyList<string> updateIds, CancellationToken ct);
}

public sealed class UpdateService(ILogger<UpdateService> log) : IUpdateService
{
    private readonly WingetClient _winget = new(log);

    public async Task<UpdateReport> ScanAsync(CancellationToken ct)
    {
        IReadOnlyList<AppUpdate> apps = [];
        IReadOnlyList<WindowsUpdate> windows = [];
        string? appError = null, windowsError = null;
        var reboot = false;

        try { apps = await _winget.ListUpgradesAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            appError = $"App updates: {ex.Message}";
            log.LogWarning(ex, "App update scan failed");
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // The Windows Update Agent is slow and blocking; keep it off the check-in path.
                windows = await Task.Run(() => OperatingSystem.IsWindows() ? new WindowsUpdateClient(log).Search() : [], ct).ConfigureAwait(false);
                reboot = WindowsUpdateClient.IsRebootPending();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                windowsError = $"Windows Update: {ex.Message}";
                log.LogWarning(ex, "Windows Update scan failed");
            }
        }
        else
        {
            windowsError = "Windows Update: not available on this operating system.";
        }

        return new UpdateReport(apps, windows, DateTimeOffset.UtcNow, reboot, appError, windowsError);
    }

    public async Task<IReadOnlyList<ItemResult>> InstallAppsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var results = new List<ItemResult>();
        foreach (var id in ids)
        {
            try { results.Add(await _winget.UpgradeAsync(id, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new ItemResult(id, false, ex.Message)); }
        }
        return results;
    }

    public Task<(IReadOnlyList<ItemResult> Results, bool RebootRequired)> InstallWindowsAsync(IReadOnlyList<string> updateIds, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<(IReadOnlyList<ItemResult>, bool)>(
                (updateIds.Select(id => new ItemResult(id, false, "Windows Update is not available here.")).ToList(), false));
        return Task.Run(() => OperatingSystem.IsWindows()
            ? new WindowsUpdateClient(log).Install(updateIds)
            : ((IReadOnlyList<ItemResult>)[], false), ct);
    }
}
