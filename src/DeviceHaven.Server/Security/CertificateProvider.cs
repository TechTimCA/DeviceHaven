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

    public X509Certificate2 Certificate => _certificate ??= TrustLocally(LoadOrCreate());

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

    /// <summary>
    /// Adds the certificate (public part only, never the private key) to this
    /// server's own trusted list, so the console opens at https://localhost
    /// without a browser warning. Only affects this one machine, and the
    /// certificate can't sign anything else (it isn't a certificate authority).
    /// </summary>
    private X509Certificate2 TrustLocally(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows()) return certificate;
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var old in store.Certificates.Find(X509FindType.FindBySubjectName, "DeviceHaven", false))
            {
                if (old.Thumbprint != certificate.Thumbprint && old.Subject.Contains("O=DeviceHaven", StringComparison.Ordinal))
                    store.Remove(old);
            }
            if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0)
            {
                using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
                store.Add(publicOnly);
                _log.LogInformation("Trusted the server certificate on this machine so the console opens without a warning.");
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not add the server certificate to this machine's trusted list; the browser will show a warning.");
        }
        return certificate;
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
