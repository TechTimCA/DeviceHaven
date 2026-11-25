using System.Net;
using System.Net.Http.Json;
using DeviceHaven.Agent;
using DeviceHaven.Agent.Inventory;
using DeviceHaven.Agent.Updates;
using DeviceHaven.Server.Data;
using DeviceHaven.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceHaven.Server.Tests;

/// <summary>Commands, update reports, approvals, notifications and restarts, end to end.</summary>
public sealed class UpdatesAndCommandsTests : IDisposable
{
    private const string FakeFingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly TestServerFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "devicehaven-agent-tests", Guid.NewGuid().ToString("N"));

    public UpdatesAndCommandsTests() => _admin = _factory.CreateClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_agentDir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public List<AppUpdate> Apps { get; } =
        [
            new("Google.Chrome", "Google Chrome", "131.0.1", "132.0.2", "winget"),
            new("7zip.7zip", "7-Zip", "23.01", "24.09", "winget"),
        ];
        public List<WindowsUpdate> Windows { get; } =
        [
            new("11111111-2222-3333-4444-555555555555", "2026-11 Cumulative Update for Windows 11", "KB5050001", "Critical", ["Security Updates"], 600_000_000, true),
        ];
        public List<string> InstalledApps { get; } = [];
        public List<string> InstalledWindows { get; } = [];

        public Task<UpdateReport> ScanAsync(CancellationToken ct) =>
            Task.FromResult(new UpdateReport(Apps.ToList(), Windows.ToList(), DateTimeOffset.UtcNow, false));

        public Task<IReadOnlyList<ItemResult>> InstallAppsAsync(IReadOnlyList<string> ids, CancellationToken ct)
        {
            InstalledApps.AddRange(ids);
            Apps.RemoveAll(a => ids.Contains(a.Id));
            return Task.FromResult<IReadOnlyList<ItemResult>>(ids.Select(i => new ItemResult(i, true, "Installed")).ToList());
        }

        public Task<(IReadOnlyList<ItemResult> Results, bool RebootRequired)> InstallWindowsAsync(IReadOnlyList<string> updateIds, CancellationToken ct)
        {
            InstalledWindows.AddRange(updateIds);
            Windows.RemoveAll(w => updateIds.Contains(w.UpdateId));
            return Task.FromResult<(IReadOnlyList<ItemResult>, bool)>((updateIds.Select(i => new ItemResult(i, true, "Installed")).ToList(), true));
        }
    }

    private sealed class FakeSystem : ISystemActions
    {
        public List<TimeSpan> Restarts { get; } = [];
        public DateTimeOffset BootTime { get; set; } = DateTimeOffset.UtcNow.AddDays(-1);
        public void Restart(TimeSpan delay, string message) => Restarts.Add(delay);
    }

    private sealed class Collector : IInventoryCollector
    {
        public InventorySnapshot Collect() => ServerSecurityTests.SampleInventory();
    }

    private async Task<(AgentWorker Worker, AgentStore Store, FakeUpdates Updates, FakeSystem System, Guid DeviceId)> EnrolledAgentAsync()
    {
        var code = await _factory.CreateCodeAsync();
        var store = new AgentStore(_agentDir);
        store.SaveConfig(new AgentConfig { ServerUrl = "https://devicehaven.test:8443", Fingerprint = FakeFingerprint, EnrollmentCode = code });
        var updates = new FakeUpdates();
        var system = new FakeSystem();
        var runner = new CommandRunner(store, updates, NullLogger<CommandRunner>.Instance);
        var worker = new AgentWorker(store, new Collector(), NullLogger<AgentWorker>.Instance,
            c => new ServerClient(c.ServerUrl!, c.Fingerprint!, _factory.Server.CreateHandler()), null, runner, system);
        await worker.RunOnceAsync(CancellationToken.None);
        return (worker, store, updates, system, store.ReadState().DeviceId!.Value);
    }

    /// <summary>Check in, run any commands received, then check in again to report the results.</summary>
    private static async Task CycleAsync(AgentWorker worker)
    {
        await worker.RunOnceAsync(CancellationToken.None);
        await worker.Runner.RunPendingAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);
    }

    [Fact]
    public void Winget_upgrade_table_is_parsed_from_real_output()
    {
        const string output =
            "   - \r   \\ \r                                                                                                                        \r" +
            "Name                                   Id                         Version        Available      Source\n" +
            "--------------------------------------------------------------------------------------------------------\n" +
            "Google Chrome                          Google.Chrome              131.0.6778.86  131.0.6778.109 winget\n" +
            "Microsoft Visual C++ 2015-2022 Redist… Microsoft.VCRedist.2015+.x64 14.38.33135.0 14.42.34433.0 winget\n" +
            "7-Zip 23.01 (x64)                      7zip.7zip                  23.01          24.09          winget\n" +
            "3 upgrades available.\n";

        var updates = WingetClient.ParseUpgradeTable(output);

        Assert.Equal(3, updates.Count);
        Assert.Equal(("Google.Chrome", "131.0.6778.86", "131.0.6778.109"), (updates[0].Id, updates[0].CurrentVersion, updates[0].AvailableVersion));
        Assert.Equal("Microsoft.VCRedist.2015+.x64", updates[1].Id);
        Assert.Equal("14.42.34433.0", updates[1].AvailableVersion);
        Assert.Equal(("7zip.7zip", "7-Zip 23.01 (x64)"), (updates[2].Id, updates[2].Name));
    }

    [Fact]
    public void Winget_output_with_no_upgrades_gives_an_empty_list()
    {
        Assert.Empty(WingetClient.ParseUpgradeTable("No installed package found matching input criteria.\n"));
        Assert.False(WingetClient.IsValidId("Bad Id; rm -rf"));
        Assert.True(WingetClient.IsValidId("Microsoft.VCRedist.2015+.x64"));
    }

    [Fact]
    public async Task Message_from_the_console_reaches_the_tray_and_is_marked_done()
    {
        var (worker, store, _, _, deviceId) = await EnrolledAgentAsync();

        var send = await _admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/commands",
            new { kind = CommandKinds.Notify, title = "Heads up", message = "Server maintenance tonight at 9 pm." });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);

        await CycleAsync(worker);

        var note = store.ReadState().Notifications.Single(n => n.Kind == NotificationKinds.Message);
        Assert.Equal(("Heads up", "Server maintenance tonight at 9 pm."), (note.Title, note.Message));
        var commands = _factory.Services.GetRequiredService<Store>().ListCommands(deviceId);
        Assert.Equal("done", commands.Single(c => c.Kind == CommandKinds.Notify).Status);
    }

    [Fact]
    public async Task Bad_commands_are_refused()
    {
        var (_, _, _, _, deviceId) = await EnrolledAgentAsync();
        var tooLong = await _admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/commands",
            new { kind = CommandKinds.Notify, message = new string('x', 501) });
        var install = await _admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/commands",
            new { kind = CommandKinds.InstallAppUpdates, message = "Google.Chrome" });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, install.StatusCode);   // installs only through approvals
    }

    [Fact]
    public async Task Updates_are_reported_approved_installed_and_cleared()
    {
        var (worker, store, updates, system, deviceId) = await EnrolledAgentAsync();
        await worker.Runner.ScanUpdatesAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        // The console sees both apps and the Windows update.
        var groups = await _admin.GetFromJsonAsync<List<UpdateGroup>>("/api/admin/updates");
        Assert.Equal(3, groups!.Count);

        // Approve Chrome only.
        var approve = await _admin.PostAsJsonAsync("/api/admin/updates/approve",
            new { items = new[] { new { kind = UpdateKinds.App, key = "Google.Chrome" } } });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var pending = await _admin.GetFromJsonAsync<DeviceUpdateState>($"/api/admin/devices/{deviceId}/updates");
        Assert.Equal("approved", pending!.Updates.Single(u => u.Key == "Google.Chrome").ApprovalStatus);
        Assert.Null(pending.Updates.Single(u => u.Key == "7zip.7zip").ApprovalStatus);

        await CycleAsync(worker);

        Assert.Equal(["Google.Chrome"], updates.InstalledApps);          // only what was approved
        Assert.Contains(store.ReadState().Notifications, n => n.Kind == NotificationKinds.UpdatesInstalled && n.Message.Contains("Google Chrome", StringComparison.Ordinal));
        var after = await _admin.GetFromJsonAsync<DeviceUpdateState>($"/api/admin/devices/{deviceId}/updates");
        Assert.DoesNotContain(after!.Updates, u => u.Key == "Google.Chrome");   // gone after the re-scan
        Assert.Empty(system.Restarts);
    }

    [Fact]
    public async Task Windows_updates_that_need_a_restart_ask_the_user_then_restart_at_the_deadline()
    {
        var (worker, store, updates, system, _) = await EnrolledAgentAsync();
        await worker.Runner.ScanUpdatesAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        await _admin.PostAsJsonAsync("/api/admin/updates/approve",
            new { items = new[] { new { kind = UpdateKinds.Windows, key = "11111111-2222-3333-4444-555555555555" } } });
        await CycleAsync(worker);

        Assert.Single(updates.InstalledWindows);
        var state = store.ReadState();
        Assert.NotNull(state.Restart);
        Assert.Contains(state.Notifications, n => n.Kind == NotificationKinds.Restart);
        Assert.Empty(system.Restarts);                                    // not before the deadline

        store.Update(s => s.Restart!.Deadline = DateTimeOffset.UtcNow.AddMinutes(-1));
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal([TimeSpan.FromMinutes(10)], system.Restarts);       // 10-minute warning, once
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Single(system.Restarts);

        system.BootTime = DateTimeOffset.UtcNow;                          // the PC restarted
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Null(store.ReadState().Restart);
    }

    [Fact]
    public async Task Approving_an_update_a_pc_never_reported_does_nothing()
    {
        var (worker, _, _, _, _) = await EnrolledAgentAsync();
        await worker.Runner.ScanUpdatesAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        var response = await _admin.PostAsJsonAsync("/api/admin/updates/approve",
            new { items = new[] { new { kind = UpdateKinds.App, key = "Evil.Package" } } });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, int>>();
        Assert.Equal(0, body!["installs"]);
    }

    [Fact]
    public async Task Restart_request_from_the_console_sets_a_deadline()
    {
        var (worker, store, _, _, deviceId) = await EnrolledAgentAsync();
        await _admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/commands",
            new { kind = CommandKinds.RequestRestart, deadlineMinutes = 60, message = "New drivers installed." });
        await CycleAsync(worker);

        var restart = store.ReadState().Restart!;
        Assert.InRange(restart.Deadline, DateTimeOffset.UtcNow.AddMinutes(55), DateTimeOffset.UtcNow.AddMinutes(61));
        Assert.Equal("New drivers installed.", restart.Reason);
    }

    [Fact]
    public async Task Tray_can_read_status_and_ask_for_a_restart_only()
    {
        var (worker, store, _, system, _) = await EnrolledAgentAsync();
#pragma warning disable CA1416 // the pipe itself is Windows-only; the request handling isn't
        var tray = new TrayPipeServer(store, system, NullLogger<TrayPipeServer>.Instance);
        var status = tray.Handle(new TrayRequest(TrayActions.Status));
        var restart = tray.Handle(new TrayRequest(TrayActions.RestartNow));
        var other = tray.Handle(new TrayRequest("runCommand"));
#pragma warning restore CA1416

        Assert.True(status.Ok);
        Assert.True(status.Status!.Enrolled);
        Assert.True(status.Status.Connected);
        Assert.True(restart.Ok);
        Assert.Single(system.Restarts);
        Assert.False(other.Ok);
        await Task.CompletedTask;
        GC.KeepAlive(worker);
    }
}
