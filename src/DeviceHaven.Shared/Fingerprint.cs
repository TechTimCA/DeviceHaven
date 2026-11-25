using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeviceHaven.Shared;

/// <summary>
/// Certificate pinning helpers. The agent is told the server's certificate
/// fingerprint at install time and refuses any server that doesn't match,
/// so a fake server on the network can't take over enrolled PCs.
/// </summary>
public static class Fingerprint
{
    /// <summary>SHA-256 of the certificate, as upper-case hex with no separators.</summary>
    public static string Of(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>Accepts "AB:CD:..", "ab cd ..", or plain hex.</summary>
    public static string Normalize(string fingerprint) =>
        new string(fingerprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    public static bool Matches(X509Certificate? certificate, string expected)
    {
        if (certificate is null) return false;
        var want = Normalize(expected);
        if (want.Length != 64) return false;
        var got = Of(certificate);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(got),
            System.Text.Encoding.ASCII.GetBytes(want));
    }

    /// <summary>Formats as AB:CD:EF... for showing to people.</summary>
    public static string Display(string fingerprint)
    {
        var hex = Normalize(fingerprint);
        return string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }
}
