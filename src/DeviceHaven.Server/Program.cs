using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DeviceHaven.Server;
using DeviceHaven.Server.Data;
using DeviceHaven.Server.Endpoints;
using DeviceHaven.Server.Security;
using DeviceHaven.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Host.UseWindowsService(o => o.ServiceName = "DeviceHavenServer");
if (OperatingSystem.IsWindows() && !builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddInMemoryCollection(ServerInstallerSettings.ReadFromRegistry());

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
var options = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
var dataDirectory = options.ResolveDataDirectory();

// Keys that protect the certificate password. On Windows they are themselves
// encrypted with the machine's DPAPI key, so copying the folder isn't enough.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("DeviceHaven.Server")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new Database(dataDirectory));
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton(sp => new CertificateProvider(
    dataDirectory, sp.GetRequiredService<IDataProtectionProvider>(), sp.GetRequiredService<ILogger<CertificateProvider>>()));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJson.Default);
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AgentEndpoints.EnrollRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.EnrollPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

// HTTPS only, with the server's own certificate. (Tests use an in-memory server instead.)
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.WebHost.ConfigureKestrel((context, kestrel) =>
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
        kestrel.ListenAnyIP(options.Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = kestrel.ApplicationServices.GetRequiredService<CertificateProvider>().Certificate;
                https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
            });
        });
    });
}

var app = builder.Build();

app.Services.GetRequiredService<Database>().Migrate();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Strict-Transport-Security"] = "max-age=31536000";
    headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'";
    await next();
});

app.UseRateLimiter();

// The web console: static files only. All data comes from the admin API.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapAgentEndpoints();
app.MapAdminEndpoints();

if (!app.Environment.IsEnvironment("Testing"))
{
    var certs = app.Services.GetRequiredService<CertificateProvider>();
    var publicUrl = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.ResolvePublicUrl();
    app.Logger.LogInformation("DeviceHaven Server listening on {Url}. Data folder: {Data}", publicUrl, dataDirectory);
    app.Logger.LogInformation("Agents must be given this fingerprint: {Fingerprint}", Fingerprint.Display(certs.Fingerprint));
}

app.Run();

public partial class Program;
