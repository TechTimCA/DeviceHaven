using System.Globalization;
using System.Text.Json;
using DeviceHaven.Shared;
using Microsoft.Data.Sqlite;

namespace DeviceHaven.Server.Data;

public sealed record CommandInfo(
    Guid Id,
    string Kind,
    string? Payload,
    string Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? CompletedAt,
    string? Result);

public sealed record DeviceUpdateRow(
    string Kind,
    string Key,
    string Title,
    string? CurrentVersion,
    string? AvailableVersion,
    string? Severity,
    string? Kb,
    long? SizeBytes,
    bool MayRestart,
    string? ApprovalStatus,
    string? ApprovalMessage);

public sealed record DeviceUpdateState(
    DateTimeOffset? ScannedAt,
    bool RebootPending,
    string? Error,
    IReadOnlyList<DeviceUpdateRow> Updates);

/// <summary>One update across every PC that needs it, for the Updates page.</summary>
public sealed record UpdateGroup(
    string Kind,
    string Key,
    string Title,
    string? AvailableVersion,
    string? Severity,
    string? Kb,
    IReadOnlyList<UpdateGroupDevice> Devices);

public sealed record UpdateGroupDevice(Guid Id, string Hostname, string? CurrentVersion, string? ApprovalStatus, string? ApprovalMessage);

public sealed record ApprovalItem(string Kind, string Key, IReadOnlyList<Guid>? DeviceIds);

/// <summary>Commands for agents, update reports from them, and approvals.</summary>
public sealed partial class Store
{
    // ---------- Commands ----------

    public Guid CreateCommand(Guid deviceId, string kind, string? payload, string createdBy, SqliteConnection? c = null, SqliteTransaction? tx = null)
    {
        if (!CommandKinds.All.Contains(kind)) throw new ArgumentException($"Unknown command kind {kind}", nameof(kind));
        var own = c is null;
        c ??= Db.Open();
        try
        {
            var id = Guid.NewGuid();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO commands (id, device_id, kind, payload, status, created_at, created_by)
                VALUES ($id, $d, $k, $p, 'pending', $now, $by)
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$d", deviceId.ToString());
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$p", (object?)payload ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Now);
            cmd.Parameters.AddWithValue("$by", Clip(createdBy, 128));
            cmd.ExecuteNonQuery();
            return id;
        }
        finally
        {
            if (own) c.Dispose();
        }
    }

    /// <summary>Hands a device its waiting commands and marks them sent.</summary>
    public IReadOnlyList<AgentCommand> TakePendingCommands(Guid deviceId)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var list = new List<AgentCommand>();
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT id, kind, payload FROM commands WHERE device_id = $d AND status = 'pending' ORDER BY created_at LIMIT 20";
            read.Parameters.AddWithValue("$d", deviceId.ToString());
            using var r = read.ExecuteReader();
            while (r.Read()) list.Add(new AgentCommand(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
        foreach (var command in list)
        {
            using var mark = c.CreateCommand();
            mark.Transaction = tx;
            mark.CommandText = "UPDATE commands SET status = 'sent', sent_at = $now WHERE id = $id";
            mark.Parameters.AddWithValue("$id", command.Id.ToString());
            mark.Parameters.AddWithValue("$now", Now);
            mark.ExecuteNonQuery();
        }
        tx.Commit();
        return list;
    }

    /// <summary>Records what the agent reported. Results for other devices' commands are ignored.</summary>
    public void RecordResults(Guid deviceId, IReadOnlyList<CommandResult> results)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        foreach (var result in results.Take(100))
        {
            string? kind;
            using (var update = c.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE commands SET status = $s, completed_at = $now, result = $m
                    WHERE id = $id AND device_id = $d AND status IN ('pending', 'sent')
                    RETURNING kind
                    """;
                update.Parameters.AddWithValue("$s", result.Success ? "done" : "failed");
                update.Parameters.AddWithValue("$now", Now);
                update.Parameters.AddWithValue("$m", (object?)Clip(result.Message, 2000) ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", result.Id.ToString());
                update.Parameters.AddWithValue("$d", deviceId.ToString());
                kind = update.ExecuteScalar() as string;
            }
            if (kind is not (CommandKinds.InstallAppUpdates or CommandKinds.InstallWindowsUpdates)) continue;

            foreach (var item in result.Items ?? [])
            {
                using var approval = c.CreateCommand();
                approval.Transaction = tx;
                approval.CommandText = """
                    UPDATE update_approvals SET status = $s, message = $m, completed_at = $now
                    WHERE device_id = $d AND key = $k AND command_id = $cid
                    """;
                approval.Parameters.AddWithValue("$s", item.Success ? "installed" : "failed");
                approval.Parameters.AddWithValue("$m", (object?)Clip(item.Message, 500) ?? DBNull.Value);
                approval.Parameters.AddWithValue("$now", Now);
                approval.Parameters.AddWithValue("$d", deviceId.ToString());
                approval.Parameters.AddWithValue("$k", item.Key);
                approval.Parameters.AddWithValue("$cid", result.Id.ToString());
                approval.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public IReadOnlyList<CommandInfo> ListCommands(Guid deviceId, int take = 30)
    {
        using var c = Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, payload, status, created_at, created_by, completed_at, result
            FROM commands WHERE device_id = $d ORDER BY created_at DESC LIMIT $t
            """;
        cmd.Parameters.AddWithValue("$d", deviceId.ToString());
        cmd.Parameters.AddWithValue("$t", Math.Clamp(take, 1, 200));
        using var r = cmd.ExecuteReader();
        var list = new List<CommandInfo>();
        while (r.Read())
        {
            list.Add(new CommandInfo(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3), ParseDate(r, 4)!.Value, r.GetString(5), ParseDate(r, 6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    // ---------- Update reports ----------

    public void SaveUpdateReport(Guid deviceId, UpdateReport report, string hash)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        using (var clear = c.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM device_updates WHERE device_id = $d";
            clear.Parameters.AddWithValue("$d", deviceId.ToString());
            clear.ExecuteNonQuery();
        }

        void Insert(string kind, string key, string title, string? current, string? available, string? severity, string? kb, long? size, bool restart)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO device_updates (device_id, kind, key, title, current_version, available_version, severity, kb, size_bytes, may_restart)
                VALUES ($d, $k, $key, $t, $cur, $av, $sev, $kb, $size, $r)
                """;
            cmd.Parameters.AddWithValue("$d", deviceId.ToString());
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$key", Clip(key, 200));
            cmd.Parameters.AddWithValue("$t", Clip(title, 300));
            cmd.Parameters.AddWithValue("$cur", (object?)Clip(current, 64) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$av", (object?)Clip(available, 64) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sev", (object?)Clip(severity, 32) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kb", (object?)Clip(kb, 32) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", restart ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        foreach (var app in report.Apps.Take(500))
            Insert(UpdateKinds.App, app.Id, app.Name, app.CurrentVersion, app.AvailableVersion, null, null, null, false);
        foreach (var wu in report.Windows.Take(500))
            Insert(UpdateKinds.Windows, wu.UpdateId, wu.Title, null, null, wu.Severity, wu.Kb, wu.SizeBytes, wu.MayRequireRestart);

        // Approvals for updates that are no longer waiting are finished: drop them.
        using (var tidy = c.CreateCommand())
        {
            tidy.Transaction = tx;
            tidy.CommandText = """
                DELETE FROM update_approvals WHERE device_id = $d AND NOT EXISTS (
                    SELECT 1 FROM device_updates u WHERE u.device_id = update_approvals.device_id
                    AND u.kind = update_approvals.kind AND u.key = update_approvals.key)
                """;
            tidy.Parameters.AddWithValue("$d", deviceId.ToString());
            tidy.ExecuteNonQuery();
        }

        using (var meta = c.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = """
                UPDATE devices SET updates_hash = $h, updates_at = $at, reboot_pending = $rb, updates_error = $err WHERE id = $d
                """;
            meta.Parameters.AddWithValue("$h", hash);
            meta.Parameters.AddWithValue("$at", report.ScannedAt.ToString("O", CultureInfo.InvariantCulture));
            meta.Parameters.AddWithValue("$rb", report.RebootPending ? 1 : 0);
            var error = string.Join(" ", new[] { report.AppScanError, report.WindowsScanError }.Where(e => !string.IsNullOrWhiteSpace(e)));
            meta.Parameters.AddWithValue("$err", error.Length == 0 ? DBNull.Value : Clip(error, 500));
            meta.Parameters.AddWithValue("$d", deviceId.ToString());
            meta.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public DeviceUpdateState GetDeviceUpdates(Guid deviceId)
    {
        using var c = Db.Open();
        DateTimeOffset? scanned = null;
        var reboot = false;
        string? error = null;
        using (var meta = c.CreateCommand())
        {
            meta.CommandText = "SELECT updates_at, reboot_pending, updates_error FROM devices WHERE id = $d";
            meta.Parameters.AddWithValue("$d", deviceId.ToString());
            using var r = meta.ExecuteReader();
            if (r.Read())
            {
                scanned = ParseDate(r, 0);
                reboot = !r.IsDBNull(1) && r.GetInt64(1) != 0;
                error = r.IsDBNull(2) ? null : r.GetString(2);
            }
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT u.kind, u.key, u.title, u.current_version, u.available_version, u.severity, u.kb, u.size_bytes, u.may_restart, a.status, a.message
            FROM device_updates u
            LEFT JOIN update_approvals a ON a.device_id = u.device_id AND a.kind = u.kind AND a.key = u.key
            WHERE u.device_id = $d ORDER BY u.kind, u.title COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$d", deviceId.ToString());
        using var rows = cmd.ExecuteReader();
        var list = new List<DeviceUpdateRow>();
        while (rows.Read())
        {
            list.Add(new DeviceUpdateRow(rows.GetString(0), rows.GetString(1), rows.GetString(2),
                rows.IsDBNull(3) ? null : rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetString(4),
                rows.IsDBNull(5) ? null : rows.GetString(5), rows.IsDBNull(6) ? null : rows.GetString(6),
                rows.IsDBNull(7) ? null : rows.GetInt64(7), rows.GetInt64(8) != 0,
                rows.IsDBNull(9) ? null : rows.GetString(9), rows.IsDBNull(10) ? null : rows.GetString(10)));
        }
        return new DeviceUpdateState(scanned, reboot, error, list);
    }

    /// <summary>Every waiting update, grouped across the PCs that need it.</summary>
    public IReadOnlyList<UpdateGroup> ListUpdateGroups()
    {
        using var c = Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT u.kind, u.key, u.title, u.available_version, u.severity, u.kb, d.id, d.hostname, u.current_version, a.status, a.message
            FROM device_updates u
            JOIN devices d ON d.id = u.device_id AND d.revoked = 0
            LEFT JOIN update_approvals a ON a.device_id = u.device_id AND a.kind = u.kind AND a.key = u.key
            ORDER BY u.kind, u.title COLLATE NOCASE, d.hostname COLLATE NOCASE
            """;
        using var r = cmd.ExecuteReader();
        var groups = new Dictionary<(string, string), (UpdateGroup Group, List<UpdateGroupDevice> Devices)>();
        var order = new List<(string, string)>();
        while (r.Read())
        {
            var key = (r.GetString(0), r.GetString(1));
            if (!groups.TryGetValue(key, out var entry))
            {
                var devices = new List<UpdateGroupDevice>();
                entry = (new UpdateGroup(key.Item1, key.Item2, r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), devices), devices);
                groups[key] = entry;
                order.Add(key);
            }
            entry.Devices.Add(new UpdateGroupDevice(Guid.Parse(r.GetString(6)), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return order.Select(k => groups[k].Group).ToList();
    }

    /// <summary>
    /// Approves updates: one install command per PC and kind, covering every
    /// approved update for that PC. Only updates the PC actually reported are
    /// approved, so nothing can be pushed that the PC didn't list.
    /// </summary>
    public int Approve(IReadOnlyList<ApprovalItem> items, string actor)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var perDevice = new Dictionary<(Guid Device, string Kind), List<string>>();

        foreach (var item in items.Take(500))
        {
            if (item.Kind is not (UpdateKinds.App or UpdateKinds.Windows)) continue;
            using var find = c.CreateCommand();
            find.Transaction = tx;
            find.CommandText = """
                SELECT u.device_id FROM device_updates u JOIN devices d ON d.id = u.device_id AND d.revoked = 0
                WHERE u.kind = $k AND u.key = $key
                AND NOT EXISTS (SELECT 1 FROM update_approvals a WHERE a.device_id = u.device_id AND a.kind = u.kind AND a.key = u.key AND a.status = 'approved')
                """;
            find.Parameters.AddWithValue("$k", item.Kind);
            find.Parameters.AddWithValue("$key", item.Key);
            using var r = find.ExecuteReader();
            while (r.Read())
            {
                var device = Guid.Parse(r.GetString(0));
                if (item.DeviceIds is { Count: > 0 } wanted && !wanted.Contains(device)) continue;
                if (!perDevice.TryGetValue((device, item.Kind), out var keys)) perDevice[(device, item.Kind)] = keys = [];
                keys.Add(item.Key);
            }
        }

        foreach (var ((device, kind), keys) in perDevice)
        {
            var payload = JsonSerializer.Serialize(new InstallUpdatesPayload(keys), ProtocolJson.Default.InstallUpdatesPayload);
            var commandKind = kind == UpdateKinds.App ? CommandKinds.InstallAppUpdates : CommandKinds.InstallWindowsUpdates;
            var commandId = CreateCommand(device, commandKind, payload, actor, c, tx);
            foreach (var key in keys)
            {
                using var approve = c.CreateCommand();
                approve.Transaction = tx;
                approve.CommandText = """
                    INSERT INTO update_approvals (device_id, kind, key, command_id, status, approved_at, approved_by)
                    VALUES ($d, $k, $key, $cid, 'approved', $now, $by)
                    ON CONFLICT(device_id, kind, key) DO UPDATE SET command_id = excluded.command_id, status = 'approved',
                        message = NULL, approved_at = excluded.approved_at, approved_by = excluded.approved_by, completed_at = NULL
                    """;
                approve.Parameters.AddWithValue("$d", device.ToString());
                approve.Parameters.AddWithValue("$k", kind);
                approve.Parameters.AddWithValue("$key", key);
                approve.Parameters.AddWithValue("$cid", commandId.ToString());
                approve.Parameters.AddWithValue("$now", Now);
                approve.Parameters.AddWithValue("$by", actor);
                approve.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return perDevice.Values.Sum(v => v.Count);
    }
}
