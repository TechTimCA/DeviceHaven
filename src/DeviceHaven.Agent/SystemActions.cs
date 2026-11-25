using System.Diagnostics;

namespace DeviceHaven.Agent;

/// <summary>Things that act on the whole PC. Swapped for a fake in tests.</summary>
public interface ISystemActions
{
    /// <summary>Restarts Windows after <paramref name="delay"/>, with a message on screen.</summary>
    void Restart(TimeSpan delay, string message);

    /// <summary>When the PC last started.</summary>
    DateTimeOffset BootTime { get; }
}

public sealed class SystemActions(ILogger<SystemActions> log) : ISystemActions
{
    public DateTimeOffset BootTime => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public void Restart(TimeSpan delay, string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            log.LogWarning("Restart requested, but restarts are only supported on Windows for now.");
            return;
        }
        var seconds = (int)Math.Clamp(delay.TotalSeconds, 0, 3600);
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/r");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(message.Length > 500 ? message[..500] : message);
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("p:2:17");   // planned: operating system hot fix
        using var p = Process.Start(psi);
        log.LogInformation("Restart scheduled in {Seconds} seconds", seconds);
    }
}
