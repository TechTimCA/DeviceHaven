using DeviceHaven.Shared;

namespace DeviceHaven.Agent;

/// <summary>Writes agent.json from installer arguments, checking them first.</summary>
public static class Setup
{
    public static int Configure(string[] args, AgentStore store)
    {
        string? server = null, fingerprint = null, code = null;
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            var value = args[i + 1];
            switch (args[i].ToLowerInvariant())
            {
                case "--server": server = value; break;
                case "--fingerprint": fingerprint = value; break;
                case "--code": code = value; break;
                default:
                    Console.Error.WriteLine($"Unknown option {args[i]}");
                    return 2;
            }
        }

        if (server is null || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Console.Error.WriteLine("--server must be an https:// address, e.g. https://devicehaven:8443");
            return 2;
        }
        if (fingerprint is null || Fingerprint.Normalize(fingerprint).Length != 64)
        {
            Console.Error.WriteLine("--fingerprint must be the server's SHA-256 fingerprint, shown with each enrolment code.");
            return 2;
        }

        var config = store.LoadConfig();
        config.ServerUrl = uri.GetLeftPart(UriPartial.Authority);
        config.Fingerprint = Fingerprint.Normalize(fingerprint);
        if (!string.IsNullOrWhiteSpace(code)) config.EnrollmentCode = code.Trim();
        store.SaveConfig(config);
        Console.WriteLine("DeviceHaven Agent configured.");
        return 0;
    }
}
