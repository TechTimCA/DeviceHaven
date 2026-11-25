using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DeviceHaven.Server.Data;
using DeviceHaven.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DeviceHaven.Server.Tests;

public sealed class ServerSecurityTests : IDisposable
{
    private readonly TestServerFactory _factory = new();
    private readonly HttpClient _client;

    public ServerSecurityTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static EnrollRequest Request(string code, string host = "OFFICE-PC-01") =>
        new(code, host, "Windows 11 Pro", "10.0.26300", "0.1.0");

    private async Task<EnrollResponse> EnrollAsync(string? code = null)
    {
        code ??= await _factory.CreateCodeAsync();
        var response = await _client.PostAsJsonAsync("/api/agent/enroll", Request(code));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(ProtocolJson.Default.EnrollResponse))!;
    }

    private async Task<HttpResponseMessage> CheckInAsync(string? token, InventorySnapshot? inventory = null, string? hash = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/agent/checkin")
        {
            Content = JsonContent.Create(new CheckInRequest("0.1.0", DateTimeOffset.UtcNow, hash, inventory), ProtocolJson.Default.CheckInRequest),
        };
        if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(message);
    }

    [Fact]
    public async Task Enrolment_code_works_once_only()
    {
        var code = await _factory.CreateCodeAsync();
        var first = await _client.PostAsJsonAsync("/api/agent/enroll", Request(code));
        var second = await _client.PostAsJsonAsync("/api/agent/enroll", Request(code, "ANOTHER-PC"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Fact]
    public async Task Deployment_code_works_for_exactly_its_number_of_pcs()
    {
        var create = await _client.PostAsJsonAsync("/api/admin/enrollment-codes", new { validHours = 24, maxUses = 3 });
        var code = (await create.Content.ReadFromJsonAsync<Endpoints.EnrollmentCodeResponse>())!;
        Assert.Equal(3, code.MaxUses);
        Assert.Contains("/qn", code.InstallCommand, StringComparison.Ordinal);

        var results = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            results.Add((await _client.PostAsJsonAsync("/api/agent/enroll", Request(code.Code, $"PC-{i}"))).StatusCode);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Forbidden], results);
    }

    [Fact]
    public async Task Console_page_is_served_with_a_strict_content_policy()
    {
        var response = await _client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("DeviceHaven Console", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_enrolment_code_is_refused()
    {
        var code = await _factory.CreateCodeAsync(validHours: 1);
        _factory.Time.Advance(TimeSpan.FromMinutes(61));

        var response = await _client.PostAsJsonAsync("/api/agent/enroll", Request(code));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_code_is_refused_with_the_same_answer()
    {
        var response = await _client.PostAsJsonAsync("/api/agent/enroll", Request("AAAA-BBBB-CCCC-DDDD"));
        var error = await response.Content.ReadFromJsonAsync(ProtocolJson.Default.ApiError);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("enrollment_refused", error!.Code);
    }

    [Fact]
    public async Task Codes_are_accepted_without_dashes_and_in_lower_case()
    {
        var code = await _factory.CreateCodeAsync();
        var typed = code.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var enrolled = await EnrollAsync(typed);
        Assert.NotEqual(Guid.Empty, enrolled.DeviceId);
    }

    [Fact]
    public async Task Check_in_needs_a_valid_device_token()
    {
        var enrolled = await EnrollAsync();
        var forged = enrolled.DeviceToken[..^4] + "AAAA";

        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckInAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckInAsync("garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckInAsync(forged)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CheckInAsync(enrolled.DeviceToken)).StatusCode);
    }

    [Fact]
    public async Task Revoked_device_can_no_longer_check_in()
    {
        var enrolled = await EnrollAsync();
        var revoke = await _client.DeleteAsync($"/api/admin/devices/{enrolled.DeviceId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckInAsync(enrolled.DeviceToken)).StatusCode);
    }

    [Fact]
    public async Task Admin_api_refuses_requests_from_other_computers()
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, "/api/admin/devices");
        message.Headers.Add(TestServerFactory.RemoteIpHeader, "192.168.1.50");
        var remote = await _client.SendAsync(message);
        var local = await _client.GetAsync("/api/admin/devices");

        Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task Server_never_stores_raw_tokens_or_codes()
    {
        var code = await _factory.CreateCodeAsync();
        var enrolled = await EnrollAsync(code);
        var secret = enrolled.DeviceToken.Split('.')[1];

        var dbBytes = await ReadAllDatabaseFilesAsync();
        Assert.DoesNotContain(secret, dbBytes, StringComparison.Ordinal);
        Assert.DoesNotContain(code, dbBytes, StringComparison.Ordinal);
        Assert.DoesNotContain(code.Replace("-", "", StringComparison.Ordinal), dbBytes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_settings_are_kept_within_safe_limits()
    {
        var response = await _client.PutAsJsonAsync("/api/admin/settings/agent",
            new AgentSettings(CheckInSeconds: 1, InventoryMinutes: 1, JitterPercent: 99, LiveConnection: true, ShowTrayIcon: false),
            ProtocolJson.Default.AgentSettings);
        var saved = await response.Content.ReadFromJsonAsync(ProtocolJson.Default.AgentSettings);

        Assert.Equal(AgentSettings.MinCheckInSeconds, saved!.CheckInSeconds);
        Assert.Equal(AgentSettings.MinInventoryMinutes, saved.InventoryMinutes);
        Assert.Equal(AgentSettings.MaxJitterPercent, saved.JitterPercent);
    }

    [Fact]
    public async Task Check_in_returns_the_admins_settings()
    {
        await _client.PutAsJsonAsync("/api/admin/settings/agent",
            AgentSettings.Default with { CheckInSeconds = 900 }, ProtocolJson.Default.AgentSettings);
        var enrolled = await EnrollAsync();

        var response = await CheckInAsync(enrolled.DeviceToken);
        var body = await response.Content.ReadFromJsonAsync(ProtocolJson.Default.CheckInResponse);
        Assert.Equal(900, body!.Settings.CheckInSeconds);
    }

    [Fact]
    public async Task Server_asks_for_inventory_it_does_not_have()
    {
        var enrolled = await EnrollAsync();
        var inventory = SampleInventory();
        var hash = InventoryHash.Compute(inventory);

        var hashOnly = await (await CheckInAsync(enrolled.DeviceToken, null, hash)).Content.ReadFromJsonAsync(ProtocolJson.Default.CheckInResponse);
        Assert.True(hashOnly!.InventoryRequested);

        var full = await (await CheckInAsync(enrolled.DeviceToken, inventory, hash)).Content.ReadFromJsonAsync(ProtocolJson.Default.CheckInResponse);
        Assert.False(full!.InventoryRequested);

        var detail = _factory.Services.GetRequiredService<Store>().GetDevice(enrolled.DeviceId);
        Assert.Equal("Dell Inc.", detail!.Inventory!.Manufacturer);
        Assert.Contains(detail.Inventory.Apps, a => a.Name == "7-Zip");
    }

    [Fact]
    public async Task Device_list_shows_the_pcs_own_ipv4_address()
    {
        var enrolled = await EnrollAsync();
        var inventory = SampleInventory();
        await CheckInAsync(enrolled.DeviceToken, inventory, InventoryHash.Compute(inventory));

        var devices = await _client.GetFromJsonAsync<List<DeviceSummary>>("/api/admin/devices");
        Assert.Equal("192.168.10.25", devices!.Single().PrimaryIp);
    }

    [Fact]
    public void Upgrading_an_existing_database_adds_new_columns()
    {
        // A database from version 0.1.8, before primary_ip existed.
        var dir = Path.Combine(Path.GetTempPath(), "devicehaven-migrate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new Database(dir);
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE devices (id TEXT PRIMARY KEY, hostname TEXT NOT NULL, os_name TEXT NOT NULL, os_version TEXT NOT NULL,
                  agent_version TEXT NOT NULL, token_hash TEXT NOT NULL, enrolled_at TEXT NOT NULL, last_seen TEXT, last_ip TEXT,
                  inventory_json TEXT, inventory_hash TEXT, inventory_at TEXT, revoked INTEGER NOT NULL DEFAULT 0);
                INSERT INTO devices (id, hostname, os_name, os_version, agent_version, token_hash, enrolled_at)
                VALUES ('6f1c2d3e-0000-4000-8000-000000000001', 'OLD-PC', 'Windows', '10.0', '0.1.8', 'x', '2026-11-25T18:00:00.0000000+00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        db.Migrate();
        db.Migrate(); // running twice is safe

        var devices = new Store(db, TimeProvider.System).ListDevices();
        Assert.Equal("OLD-PC", devices.Single().Hostname);
        Assert.Null(devices.Single().PrimaryIp);
    }

    [Fact]
    public async Task Audit_log_records_actions_and_cannot_be_edited()
    {
        await EnrollAsync();
        var audit = _factory.Services.GetRequiredService<Store>().ReadAudit(50);
        Assert.Contains(audit, a => a.Action == "device.enrolled");
        Assert.Contains(audit, a => a.Action == "enrollment_code.created");

        using var connection = _factory.Services.GetRequiredService<Database>().Open();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE audit_log SET action = 'tampered'";
        Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM audit_log";
        Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    internal static InventorySnapshot SampleInventory() => new(
        "OFFICE-PC-01", "Microsoft Windows 11 Pro", "10.0.26300", "26H2 (26300.1000)",
        "Dell Inc.", "OptiPlex 7020", "ABC1234", "Intel Core i5-14500", 20, 16L * 1024 * 1024 * 1024, @"OFFICE\tim",
        [new DiskInfo(@"C:\", 512_000_000_000, 300_000_000_000, true)],
        [new InstalledApp("7-Zip", "24.09", "Igor Pavlov"), new InstalledApp("Google Chrome", "131.0", "Google LLC")],
        DateTimeOffset.UtcNow,
        [new NetworkAdapter("vEthernet (Default Switch)", "00:15:5D:01:02:03", ["172.20.0.1"], [], null, "Ethernet", 10000),
         new NetworkAdapter("Ethernet", "A4:BB:6D:12:34:56", ["192.168.10.25"], [], "192.168.10.1", "Ethernet", 1000)]);

    private async Task<string> ReadAllDatabaseFilesAsync()
    {
        var text = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(_factory.DataDirectory, "devicehaven.db*"))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
            text.Append(await reader.ReadToEndAsync());
        }
        return text.ToString();
    }
}
