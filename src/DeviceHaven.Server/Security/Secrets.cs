using System.Security.Cryptography;
using System.Text;

namespace DeviceHaven.Server.Security;

/// <summary>
/// Creates and checks the secrets the server hands out. Only hashes are ever
/// stored, so a copy of the database can't be used to impersonate a device
/// or reuse an enrolment code.
/// </summary>
public static class Secrets
{
    // Crockford base32: no I, L, O or U, so codes are easy to read out loud.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A one-time enrolment code like 7K3Q-M9TX-R2VB-H4NC (80 bits).</summary>
    public static string NewEnrollmentCode()
    {
        Span<char> chars = stackalloc char[16];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }

    /// <summary>Uppercases, drops dashes and spaces, and fixes look-alike letters.</summary>
    public static string NormalizeEnrollmentCode(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var c in code.ToUpperInvariant())
        {
            var ch = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (CodeAlphabet.Contains(ch, StringComparison.Ordinal)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>A device token of the form {deviceId}.{256-bit secret}.</summary>
    public static (string Token, string SecretHash) NewDeviceToken(Guid deviceId)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        return ($"{deviceId:N}.{secret}", Hash(secret));
    }

    public static bool TryParseDeviceToken(string? token, out Guid deviceId, out string secret)
    {
        deviceId = Guid.Empty;
        secret = "";
        if (string.IsNullOrEmpty(token) || token.Length > 200) return false;
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0) return false;
        if (!Guid.TryParseExact(token[..dot], "N", out deviceId)) return false;
        secret = token[(dot + 1)..];
        return secret.Length >= 40;
    }

    /// <summary>SHA-256 is enough here: every secret is long and random, never a password.</summary>
    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool HashMatches(string value, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(value)),
            Encoding.ASCII.GetBytes(expectedHash));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
