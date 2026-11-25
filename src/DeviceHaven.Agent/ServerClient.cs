using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using DeviceHaven.Shared;

namespace DeviceHaven.Agent;

public sealed class ServerRejectedException(HttpStatusCode status, string? code, string? message)
    : Exception($"Server refused the request ({(int)status} {code}): {message}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Talks to the DeviceHaven server over HTTPS, trusting only the
/// certificate whose fingerprint the agent was installed with.
/// </summary>
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;

    public ServerClient(string serverUrl, string fingerprint, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Server URL must be an https:// address.", nameof(serverUrl));
        if (Shared.Fingerprint.Normalize(fingerprint).Length != 64)
            throw new ArgumentException("Server fingerprint must be a SHA-256 fingerprint (64 hex characters).", nameof(fingerprint));

        handler ??= new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
            SslOptions = new SslClientAuthenticationOptions
            {
                // The server's certificate is self-made, so normal chain checks
                // would fail. Instead, accept it only if it is exactly the one we pinned.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    Shared.Fingerprint.Matches(certificate, fingerprint),
            },
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeviceHaven-Agent", AgentInfo.Version));
    }

    public Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct) =>
        PostAsync("api/agent/enroll", request, ProtocolJson.Default.EnrollRequest, ProtocolJson.Default.EnrollResponse, null, ct);

    public Task<CheckInResponse> CheckInAsync(string deviceToken, CheckInRequest request, CancellationToken ct) =>
        PostAsync("api/agent/checkin", request, ProtocolJson.Default.CheckInRequest, ProtocolJson.Default.CheckInResponse, deviceToken, ct);

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path, TRequest body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        string? bearer, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, requestType),
        };
        if (bearer is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ApiError? error = null;
            try { error = await response.Content.ReadFromJsonAsync(ProtocolJson.Default.ApiError, ct).ConfigureAwait(false); }
            catch (System.Text.Json.JsonException) { }
            catch (NotSupportedException) { }
            throw new ServerRejectedException(response.StatusCode, error?.Code, error?.Message);
        }
        return await response.Content.ReadFromJsonAsync(responseType, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty response from server.");
    }

    public void Dispose() => _http.Dispose();
}

public static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
