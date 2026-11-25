using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeviceHaven.Agent;
using DeviceHaven.Agent.Inventory;
using DeviceHaven.Server.Data;
using DeviceHaven.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceHaven.Server.Tests;

/// <summary>Runs the real agent loop against the real server, in memory.</summary>
public sealed class AgentEndToEndTests : IDisposable
{
    private readonly TestServerFactory _factory = new();
    private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "devicehaven-agent-tests", Guid.NewGuid().ToString("N"));
    private const string FakeFingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_agentDir, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedCollector(InventorySnapshot snapshot) : IInventoryCollector
    {
        public int Calls { get; private set; }
        public InventorySnapshot Current { get; set; } = snapshot;
        public InventorySnapshot Collect() { Calls++; return Current; }
    }

    private (AgentWorker Worker, AgentStore Store, FixedCollector Collector) CreateAgent(string code)
    {
        var store = new AgentStore(_agentDir);
        store.SaveConfig(new AgentConfig { ServerUrl = "https://devicehaven.test:8443", Fingerprint = FakeFingerprint, EnrollmentCode = code });
        var collector = new FixedCollector(ServerSecurityTests.SampleInventory());
        var worker = new AgentWorker(store, collector, NullLogger<AgentWorker>.Instance,
            config => new ServerClient(config.ServerUrl!, config.Fingerprint!, _factory.Server.CreateHandler()));
        return (worker, store, collector);
    }

    [Fact]
    public async Task Agent_enrols_reports_inventory_and_forgets_the_code()
    {
        var code = await _factory.CreateCodeAsync();
        var (worker, store, _) = CreateAgent(code);

        var wait = await worker.RunOnceAsync(CancellationToken.None);

        var state = store.LoadState();
        Assert.NotNull(state.DeviceId);
        Assert.NotNull(store.LoadToken());
        Assert.Null(store.LoadConfig().EnrollmentCode);
        Assert.InRange(wait.TotalSeconds, AgentSettings.MinCheckInSeconds, AgentSettings.MaxCheckInSeconds);

        var device = _factory.Services.GetRequiredService<Store>().GetDevice(state.DeviceId!.Value);
        Assert.Equal("OptiPlex 7020", device!.Inventory!.Model);
    }

    [Fact]
    public async Task Unchanged_inventory_is_not_sent_again()
    {
        var code = await _factory.CreateCodeAsync();
        var (worker, store, collector) = CreateAgent(code);
        await worker.RunOnceAsync(CancellationToken.None);
        var firstReport = _factory.Services.GetRequiredService<Store>().GetDevice(store.LoadState().DeviceId!.Value)!.InventoryAt;

        // Not due yet: the agent checks in without collecting or sending inventory.
        var callsBefore = collector.Calls;
        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(callsBefore, collector.Calls);
        var after = _factory.Services.GetRequiredService<Store>().GetDevice(store.LoadState().DeviceId!.Value)!;
        Assert.Equal(firstReport, after.InventoryAt);
        Assert.True(after.Device.LastSeen > firstReport);
    }

    [Fact]
    public async Task Agent_settings_follow_the_server()
    {
        var code = await _factory.CreateCodeAsync();
        _factory.Services.GetRequiredService<Store>().SaveAgentSettings(AgentSettings.Default with { CheckInSeconds = 3600, JitterPercent = 0 });
        var (worker, store, _) = CreateAgent(code);

        var wait = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3600, store.LoadState().Settings!.CheckInSeconds);
        Assert.Equal(3600, wait.TotalSeconds, precision: 0);
    }

    [Fact]
    public void Agent_refuses_a_server_with_the_wrong_certificate()
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=imposter", key, HashAlgorithmName.SHA256);
        using var imposter = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        using var real = new CertificateRequest("CN=real", ECDsa.Create(), HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var pinned = Fingerprint.Of(real);
        Assert.True(Fingerprint.Matches(real, pinned));
        Assert.True(Fingerprint.Matches(real, Fingerprint.Display(pinned).ToLowerInvariant()));
        Assert.False(Fingerprint.Matches(imposter, pinned));
        Assert.False(Fingerprint.Matches(null, pinned));
        Assert.False(Fingerprint.Matches(real, "too-short"));
    }

    [Fact]
    public void Check_in_timing_spreads_out_but_stays_in_bounds()
    {
        var settings = AgentSettings.Default with { CheckInSeconds = 300, JitterPercent = 20 };
        var random = new Random(42);
        var waits = Enumerable.Range(0, 500).Select(_ => settings.NextCheckInDelay(random).TotalSeconds).ToList();

        Assert.All(waits, w => Assert.InRange(w, 240, 360));
        Assert.True(waits.Max() - waits.Min() > 60, "jitter should spread check-ins out");
    }

    private sealed class FakeInstallerSettings(PendingConfig? pending) : IPendingConfigSource
    {
        public PendingConfig? Pending { get; private set; } = pending;
        public bool Configured { get; private set; }
        public PendingConfig? Read() => Pending;
        public void Clear() => Pending = null;
        public void MarkConfigured() => Configured = true;
    }

    [Fact]
    public async Task Agent_enrols_from_settings_left_by_the_installer_and_deletes_them()
    {
        var code = await _factory.CreateCodeAsync();
        var store = new AgentStore(_agentDir);
        var installer = new FakeInstallerSettings(new PendingConfig("https://devicehaven.test:8443", Fingerprint.Display(FakeFingerprint).ToLowerInvariant(), code));
        var worker = new AgentWorker(store, new FixedCollector(ServerSecurityTests.SampleInventory()), NullLogger<AgentWorker>.Instance,
            config => new ServerClient(config.ServerUrl!, config.Fingerprint!, _factory.Server.CreateHandler()), installer);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Null(installer.Pending);                      // registry key removed
        Assert.True(installer.Configured);                   // upgrades won't ask again
        Assert.NotNull(store.LoadState().DeviceId);          // enrolled
        Assert.Null(store.LoadConfig().EnrollmentCode);      // code not kept
        Assert.Equal(FakeFingerprint, store.LoadConfig().Fingerprint);
    }

    [Fact]
    public void Installer_settings_are_validated()
    {
        var store = new AgentStore(_agentDir);
        Assert.NotNull(InstallerSettings.Apply(new PendingConfig("http://plain:8443", FakeFingerprint, "X"), store));
        Assert.NotNull(InstallerSettings.Apply(new PendingConfig("https://ok:8443", "12:34", "X"), store));
        Assert.NotNull(InstallerSettings.Apply(new PendingConfig(null, FakeFingerprint, "X"), store));
        Assert.Null(InstallerSettings.Apply(new PendingConfig("https://ok:8443", FakeFingerprint, null), store));
        Assert.Null(store.LoadConfig().EnrollmentCode);
    }

    [Fact]
    public void Setup_rejects_bad_install_arguments()
    {
        var store = new AgentStore(_agentDir);
        Assert.Equal(2, Setup.Configure(["--server", "http://insecure:8443", "--fingerprint", FakeFingerprint], store));
        Assert.Equal(2, Setup.Configure(["--server", "https://ok:8443", "--fingerprint", "1234"], store));
        Assert.Equal(0, Setup.Configure(["--server", "https://ok:8443/extra/path", "--fingerprint", FakeFingerprint, "--code", "AAAA-BBBB-CCCC-DDDD"], store));
        Assert.Equal("https://ok:8443", store.LoadConfig().ServerUrl);
    }
}
