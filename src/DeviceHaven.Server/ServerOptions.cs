namespace DeviceHaven.Server;

/// <summary>Bound from the "DeviceHaven" section of appsettings.json.</summary>
public sealed class ServerOptions
{
    public const string Section = "DeviceHaven";

    /// <summary>HTTPS port agents and the dashboard connect to.</summary>
    public int Port { get; set; } = 8443;

    /// <summary>
    /// Where the database, certificate and keys live. Defaults to
    /// %ProgramData%\DeviceHaven\Server on Windows.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// The address agents should use, put into the install command shown
    /// with each enrolment code. Defaults to https://{machine name}:{port}.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>Enrolment attempts allowed per IP address per minute.</summary>
    public int EnrollPerMinute { get; set; } = 10;

    public string ResolveDataDirectory()
    {
        var dir = DataDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeviceHaven", "Server")
                : Path.Combine(AppContext.BaseDirectory, "data");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ResolvePublicUrl() =>
        string.IsNullOrWhiteSpace(PublicUrl)
            ? $"https://{Environment.MachineName.ToLowerInvariant()}:{Port}"
            : PublicUrl.TrimEnd('/');
}
