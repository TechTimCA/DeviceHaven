using System.Net;
using System.Net.Http.Json;
using DeviceHaven.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace DeviceHaven.Server.Tests;

/// <summary>
/// Runs the real server in memory with its own empty data folder.
/// Requests appear to come from the server itself unless a test sets
/// the X-Test-Remote-IP header.
/// </summary>
public sealed class TestServerFactory : WebApplicationFactory<Program>
{
    public const string RemoteIpHeader = "X-Test-Remote-IP";

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "devicehaven-tests", Guid.NewGuid().ToString("N"));
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataDirectory);
        builder.UseEnvironment("Testing");
        builder.UseSetting("DeviceHaven:DataDirectory", DataDirectory);
        builder.UseSetting("DeviceHaven:PublicUrl", "https://devicehaven.test:8443");
        builder.UseSetting("DeviceHaven:EnrollPerMinute", "1000");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }

    public async Task<string> CreateCodeAsync(int validHours = 24)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/admin/enrollment-codes", new { validHours });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EnrollmentCodeResponse>();
        return body!.Code;
    }

    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress =
                    context.Request.Headers.TryGetValue(RemoteIpHeader, out var ip) ? IPAddress.Parse(ip!) : IPAddress.Loopback;
                await nextMiddleware();
            });
            next(app);
        };
    }
}
