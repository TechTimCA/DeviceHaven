using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DeviceHaven.Shared;

namespace DeviceHaven.Agent.Updates;

/// <summary>
/// Finds and installs third-party app updates with winget, Microsoft's
/// package manager that ships with Windows 10 and 11.
/// </summary>
public sealed partial class WingetClient(ILogger log)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,127}$")]
    private static partial Regex PackageIdPattern();

    public static bool IsValidId(string id) => PackageIdPattern().IsMatch(id);

    /// <summary>
    /// winget isn't on the PATH for the SYSTEM account the agent runs as, so
    /// look for it where Windows installs it.
    /// </summary>
    public static string? FindWinget()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            var candidates = Directory.GetDirectories(apps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .Select(dir => (dir, version: ParseVersion(Path.GetFileName(dir))))
                .OrderByDescending(c => c.version)
                .Select(c => Path.Combine(c.dir, "winget.exe"));
            var found = candidates.FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(local) ? local : null;

        static Version ParseVersion(string folder)
        {
            var parts = folder.Split('_');
            return parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : new Version(0, 0);
        }
    }

    public async Task<IReadOnlyList<AppUpdate>> ListUpgradesAsync(CancellationToken ct)
    {
        var winget = FindWinget() ?? throw new InvalidOperationException("winget (App Installer) is not installed on this PC.");
        var (exit, output) = await RunAsync(winget,
            ["upgrade", "--accept-source-agreements", "--disable-interactivity"], ListTimeout, ct).ConfigureAwait(false);
        var updates = ParseUpgradeTable(output);
        if (updates.Count == 0 && exit != 0 && !output.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("No applicable", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("winget upgrade exited with {Exit}: {Output}", exit, Tail(output));
        }
        return updates;
    }

    public async Task<ItemResult> UpgradeAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id)) return new ItemResult(id, false, "Not a valid winget package id.");
        var winget = FindWinget();
        if (winget is null) return new ItemResult(id, false, "winget (App Installer) is not installed on this PC.");

        var (exit, output) = await RunAsync(winget,
        [
            "upgrade", "--id", id, "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        ], InstallTimeout, ct).ConfigureAwait(false);

        return exit == 0
            ? new ItemResult(id, true, "Installed")
            : new ItemResult(id, false, $"winget exit code 0x{exit:X8}: {Tail(output)}");
    }

    /// <summary>
    /// Reads winget's upgrade table. Columns are found from the header line,
    /// so it works whatever the column widths are.
    /// </summary>
    public static List<AppUpdate> ParseUpgradeTable(string output)
    {
        // winget redraws its progress spinner with carriage returns: keep what each line ends up showing.
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .ToList();

        var headerIndex = lines.FindIndex(l =>
            l.Contains("Name", StringComparison.Ordinal) && l.Contains(" Id ", StringComparison.Ordinal)
            && l.Contains("Version", StringComparison.Ordinal) && l.Contains("Available", StringComparison.Ordinal));
        if (headerIndex < 0) return [];

        var header = lines[headerIndex];
        var idCol = header.IndexOf(" Id ", StringComparison.Ordinal) + 1;
        var versionCol = header.IndexOf("Version", StringComparison.Ordinal);
        var availableCol = header.IndexOf("Available", StringComparison.Ordinal);
        var sourceCol = header.IndexOf("Source", StringComparison.Ordinal);

        var results = new List<AppUpdate>();
        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (line.TrimStart().StartsWith('-')) continue;                  // the ----- rule under the header
            if (string.IsNullOrWhiteSpace(line)) break;                        // end of the table
            if (line.Length <= availableCol) break;                            // "3 upgrades available." etc.

            string Cut(int start, int end) =>
                start >= line.Length ? "" : line[start..Math.Min(end < 0 ? line.Length : end, line.Length)].Trim();

            var name = Cut(0, idCol);
            var id = Cut(idCol, versionCol);
            var version = Cut(versionCol, availableCol);
            var available = Cut(availableCol, sourceCol);
            var source = sourceCol > 0 ? Cut(sourceCol, -1) : null;

            // A long name or id can run into the next column; then read the row word by word from the right.
            bool Overlaps(int col) => col > 0 && col <= line.Length && line[col - 1] != ' ';
            if (!IsValidId(id) || id.Contains(' ', StringComparison.Ordinal)
                || Overlaps(idCol) || Overlaps(versionCol) || Overlaps(availableCol) || Overlaps(sourceCol))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var hasSource = sourceCol > 0 && tokens.Length >= 5;
                var offset = hasSource ? 1 : 0;
                if (tokens.Length < 4 + offset) continue;
                source = hasSource ? tokens[^1] : null;
                available = tokens[^(1 + offset)];
                version = tokens[^(2 + offset)];
                id = tokens[^(3 + offset)];
                name = string.Join(' ', tokens[..^(3 + offset)]);
            }

            if (!IsValidId(id) || string.IsNullOrWhiteSpace(available)) continue;
            results.Add(new AppUpdate(id, name.TrimEnd('…').Trim(), version, available, string.IsNullOrEmpty(source) ? null : source));
        }
        return results;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start winget.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"winget did not finish within {timeout.TotalMinutes:N0} minutes.");
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 2 && !l.All(ch => "-\\|/ ".Contains(ch, StringComparison.Ordinal))).ToList();
        return string.Join(" ", lines.TakeLast(2));
    }
}
