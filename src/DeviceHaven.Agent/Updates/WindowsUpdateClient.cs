using System.Runtime.Versioning;
using DeviceHaven.Shared;
using Microsoft.Win32;

namespace DeviceHaven.Agent.Updates;

/// <summary>
/// Talks to the Windows Update Agent built into Windows (the same engine
/// Settings → Windows Update uses), through its COM interface.
/// Only updates the admin approved are ever downloaded or installed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateClient(ILogger log)
{
    private const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";

    public IReadOnlyList<WindowsUpdate> Search()
    {
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search(Criteria);

        var list = new List<WindowsUpdate>();
        int count = result.Updates.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic u = result.Updates.Item(i);
            var kbs = new List<string>();
            int kbCount = u.KBArticleIDs.Count;
            for (var k = 0; k < kbCount; k++) kbs.Add("KB" + (string)u.KBArticleIDs.Item(k));

            var categories = new List<string>();
            int catCount = u.Categories.Count;
            for (var c = 0; c < catCount; c++) categories.Add((string)u.Categories.Item(c).Name);

            long? size = null;
            try { size = Convert.ToInt64(u.MaxDownloadSize, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { }

            int rebootBehavior = 0;
            try { rebootBehavior = u.InstallationBehavior.RebootBehavior; } catch (Exception) { }

            list.Add(new WindowsUpdate(
                UpdateId: (string)u.Identity.UpdateID,
                Title: (string)u.Title,
                Kb: kbs.FirstOrDefault(),
                Severity: string.IsNullOrWhiteSpace((string?)u.MsrcSeverity) ? null : (string)u.MsrcSeverity,
                Categories: categories,
                SizeBytes: size,
                MayRequireRestart: rebootBehavior != 0));
        }
        return list;
    }

    /// <summary>Downloads and installs the approved updates. Returns one result per update id asked for.</summary>
    public (IReadOnlyList<ItemResult> Results, bool RebootRequired) Install(IReadOnlyList<string> updateIds)
    {
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic found = searcher.Search(Criteria);

        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", throwOnError: true)!)!;
        var wanted = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int count = found.Updates.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic u = found.Updates.Item(i);
            string id = u.Identity.UpdateID;
            if (!wanted.Contains(id)) continue;
            if (!(bool)u.EulaAccepted) u.AcceptEula();
            collection.Add(u);
            titles[id] = u.Title;
        }

        var results = new List<ItemResult>();
        foreach (var id in wanted.Where(id => !titles.ContainsKey(id)))
            results.Add(new ItemResult(id, true, "Already installed or no longer needed"));
        if ((int)collection.Count == 0) return (results, false);

        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = collection;
        downloader.Download();

        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = collection;
        dynamic outcome = installer.Install();

        int n = collection.Count;
        for (var i = 0; i < n; i++)
        {
            dynamic u = collection.Item(i);
            string id = u.Identity.UpdateID;
            int code = outcome.GetUpdateResult(i).ResultCode;   // 2 = succeeded, 3 = succeeded with errors
            results.Add(new ItemResult(id, code is 2 or 3, code switch
            {
                2 => "Installed",
                3 => "Installed with errors",
                4 => "Failed",
                5 => "Cancelled",
                _ => $"Result code {code}",
            }));
        }
        bool reboot = outcome.RebootRequired;
        log.LogInformation("Installed {Count} Windows update(s); restart needed: {Reboot}", n, reboot);
        return (results, reboot);
    }

    /// <summary>Windows says a restart is waiting (from any update, not just ours).</summary>
    public static bool IsRebootPending()
    {
        using var lm = Registry.LocalMachine;
        return lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null
            || lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null;
    }

    private static dynamic CreateSession()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!;
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "DeviceHaven";
        return session;
    }
}
