using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeviceHaven.Shared;
using Microsoft.AspNetCore.DataProtection;

namespace DeviceHaven.Server.Security;

/// <summary>
/// Gives the server its own TLS certificate, made on first start. No outside
/// certificate authority is involved: agents trust it because they are given
/// its fingerprint when they are installed.
/// </summary>
public sealed class CertificateProvider
{
    private const string CertFile = "server-cert.pfx";
    private const string PasswordFile = "server-cert.key";

    private readonly string _directory;
    private readonly IDataProtector _protector;
    private readonly ILogger<CertificateProvider> _log;
    private X509Certificate2? _certificate;

    public CertificateProvider(string dataDirectory, IDataProtectionProvider dataProtection, ILogger<CertificateProvider> log)
    {
        _directory = dataDirectory;
        _protector = dataProtection.CreateProtector("DeviceHaven.ServerCertificate.v1");
        _log = log;
    }

    public X509Certificate2 Certificate => _certificate ??= LoadOrCreate();

    public string Fingerprint => Shared.Fingerprint.Of(Certificate);

    private X509Certificate2 LoadOrCreate()
    {
        var certPath = Path.Combine(_directory, CertFile);
        var passwordPath = Path.Combine(_directory, PasswordFile);

        if (File.Exists(certPath) && File.Exists(passwordPath))
        {
            var password = _protector.Unprotect(File.ReadAllText(passwordPath));
            var existing = X509CertificateLoader.LoadPkcs12FromFile(certPath, password, X509KeyStorageFlags.Exportable);
            if (existing.NotAfter > DateTime.UtcNow.AddDays(30))
            {
                Log(existing, "Loaded");
                return existing;
            }
            _log.LogWarning("Server certificate expires {Expiry:u}; creating a new one. Agents must be given the new fingerprint.", existing.NotAfter);
        }

        var created = Create();
        var newPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(certPath, created.Export(X509ContentType.Pfx, newPassword));
        File.WriteAllText(passwordPath, _protector.Protect(newPassword));
        Log(created, "Created");
        return X509CertificateLoader.LoadPkcs12FromFile(certPath, newPassword, X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var host = Environment.MachineName.ToLowerInvariant();
        var request = new CertificateRequest($"CN={host}, O=DeviceHaven", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(5));
    }

    private void Log(X509Certificate2 cert, string verb) =>
        _log.LogInformation("{Verb} server certificate. Fingerprint (SHA-256): {Fingerprint}. Valid until {Expiry:u}.",
            verb, Shared.Fingerprint.Display(Shared.Fingerprint.Of(cert)), cert.NotAfter);
}
