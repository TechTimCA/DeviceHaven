using System.Globalization;
using System.Text.Json;
using DeviceHaven.Server.Security;
using DeviceHaven.Shared;
using Microsoft.Data.Sqlite;

namespace DeviceHaven.Server.Data;

public sealed record DeviceSummary(
    Guid Id,
    string Hostname,
    string OsName,
    string OsVersion,
    string AgentVersion,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastSeen,
    string? LastIp,
    bool Revoked);

public sealed record DeviceDetail(DeviceSummary Device, InventorySnapshot? Inventory, DateTimeOffset? InventoryAt);

public sealed record AuditEntry(long Id, DateTimeOffset At, string Actor, string Action, string? Target, string? Detail);

public sealed record DeviceAuth(Guid Id, string Hostname, string? InventoryHash);

/// <summary>All database reads and writes, kept in one place and parameterised throughout.</summary>
public sealed class Store
{
    private readonly Database _db;
    private readonly TimeProvider _time;

    public Store(Database db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    private string Now => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    // ---------- Enrolment codes ----------

    /// <summary>
    /// Creates a code. <paramref name="maxUses"/> above 1 makes a deployment
    /// code, for installing on many PCs with one silent install command.
    /// </summary>
    public (string Code, DateTimeOffset ExpiresAt) CreateEnrollmentCode(TimeSpan validFor, string? note, int maxUses = 1)
    {
        var code = Secrets.NewEnrollmentCode();
        var expires = _time.GetUtcNow().Add(validFor);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO enrollment_codes (code_hash, created_at, expires_at, note, max_uses) VALUES ($h, $c, $e, $n, $m)";
        cmd.Parameters.AddWithValue("$m", Math.Clamp(maxUses, 1, 1000));
        cmd.Parameters.AddWithValue("$h", Secrets.Hash(Secrets.NormalizeEnrollmentCode(code)));
        cmd.Parameters.AddWithValue("$c", Now);
        cmd.Parameters.AddWithValue("$e", expires.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return (code, expires);
    }

    /// <summary>
    /// Uses one go of a code in a single statement, so PCs racing with the same
    /// code can't exceed its limit. Returns false for unknown, used-up or expired codes.
    /// </summary>
    public bool TryConsumeEnrollmentCode(string code, string usedBy, SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE enrollment_codes SET uses = uses + 1, used_at = $now, used_by = $by
            WHERE code_hash = $h AND uses < max_uses AND expires_at > $now
            """;
        cmd.Parameters.AddWithValue("$h", Secrets.Hash(Secrets.NormalizeEnrollmentCode(code)));
        cmd.Parameters.AddWithValue("$now", Now);
        cmd.Parameters.AddWithValue("$by", usedBy);
        return cmd.ExecuteNonQuery() == 1;
    }

    // ---------- Devices ----------

    public EnrollResponse? Enroll(EnrollRequest request, string? ip)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var id = Guid.NewGuid();
        if (!TryConsumeEnrollmentCode(request.EnrollmentCode, id.ToString(), c, tx))
            return null;

        var (token, hash) = Secrets.NewDeviceToken(id);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO devices (id, hostname, os_name, os_version, agent_version, token_hash, enrolled_at, last_seen, last_ip)
            VALUES ($id, $host, $os, $ver, $agent, $hash, $now, $now, $ip)
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$host", Clip(request.Hostname, 64));
        cmd.Parameters.AddWithValue("$os", Clip(request.OsName, 128));
        cmd.Parameters.AddWithValue("$ver", Clip(request.OsVersion, 64));
        cmd.Parameters.AddWithValue("$agent", Clip(request.AgentVersion, 32));
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$now", Now);
        cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return new EnrollResponse(id, token, GetAgentSettings());
    }

    /// <summary>Checks a device token. Revoked devices are always refused.</summary>
    public DeviceAuth? Authenticate(string? bearerToken)
    {
        if (!Secrets.TryParseDeviceToken(bearerToken, out var id, out var secret)) return null;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT token_hash, hostname, inventory_hash FROM devices WHERE id = $id AND revoked = 0";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return Secrets.HashMatches(secret, r.GetString(0))
            ? new DeviceAuth(id, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2))
            : null;
    }

    public void RecordCheckIn(Guid id, string agentVersion, string? ip, InventorySnapshot? inventory, string? inventoryHash)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        if (inventory is null)
        {
            cmd.CommandText = "UPDATE devices SET last_seen = $now, last_ip = $ip, agent_version = $agent WHERE id = $id";
        }
        else
        {
            cmd.CommandText = """
                UPDATE devices SET last_seen = $now, last_ip = $ip, agent_version = $agent,
                    hostname = $host, os_name = $os, os_version = $ver,
                    inventory_json = $inv, inventory_hash = $ihash, inventory_at = $now
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$host", Clip(inventory.Hostname, 64));
            cmd.Parameters.AddWithValue("$os", Clip(inventory.OsName, 128));
            cmd.Parameters.AddWithValue("$ver", Clip(inventory.OsVersion, 64));
            cmd.Parameters.AddWithValue("$inv", JsonSerializer.Serialize(inventory, ProtocolJson.Default.InventorySnapshot));
            cmd.Parameters.AddWithValue("$ihash", inventoryHash ?? InventoryHash.Compute(inventory));
        }
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$now", Now);
        cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent", Clip(agentVersion, 32));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<DeviceSummary> ListDevices()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SummaryColumns} FROM devices ORDER BY hostname COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var list = new List<DeviceSummary>();
        while (r.Read()) list.Add(ReadSummary(r));
        return list;
    }

    public DeviceDetail? GetDevice(Guid id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SummaryColumns}, inventory_json, inventory_at FROM devices WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var summary = ReadSummary(r);
        var inventory = r.IsDBNull(9) ? null : JsonSerializer.Deserialize(r.GetString(9), ProtocolJson.Default.InventorySnapshot);
        return new DeviceDetail(summary, inventory, ParseDate(r, 10));
    }

    public bool RevokeDevice(Guid id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE devices SET revoked = 1 WHERE id = $id AND revoked = 0";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return cmd.ExecuteNonQuery() == 1;
    }

    private const string SummaryColumns =
        "id, hostname, os_name, os_version, agent_version, enrolled_at, last_seen, last_ip, revoked";

    private static DeviceSummary ReadSummary(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        ParseDate(r, 5)!.Value, ParseDate(r, 6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt64(8) != 0);

    // ---------- Settings ----------

    private const string AgentSettingsKey = "agent.global";

    public AgentSettings GetAgentSettings()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", AgentSettingsKey);
        var json = cmd.ExecuteScalar() as string;
        var settings = json is null ? null : JsonSerializer.Deserialize(json, ProtocolJson.Default.AgentSettings);
        return (settings ?? AgentSettings.Default).Normalize();
    }

    public AgentSettings SaveAgentSettings(AgentSettings settings)
    {
        var normalized = settings.Normalize();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", AgentSettingsKey);
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(normalized, ProtocolJson.Default.AgentSettings));
        cmd.ExecuteNonQuery();
        return normalized;
    }

    // ---------- Audit ----------

    public void Audit(string actor, string action, string? target = null, string? detail = null)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_log (at, actor, action, target, detail) VALUES ($at, $actor, $action, $target, $detail)";
        cmd.Parameters.AddWithValue("$at", Now);
        cmd.Parameters.AddWithValue("$actor", Clip(actor, 128));
        cmd.Parameters.AddWithValue("$action", Clip(action, 64));
        cmd.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> ReadAudit(int take)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, at, actor, action, target, detail FROM audit_log ORDER BY id DESC LIMIT $take";
        cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 1000));
        using var r = cmd.ExecuteReader();
        var list = new List<AuditEntry>();
        while (r.Read())
        {
            list.Add(new AuditEntry(r.GetInt64(0), ParseDate(r, 1)!.Value, r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return list;
    }

    // ---------- Helpers ----------

    private static DateTimeOffset? ParseDate(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Clip(string? value, int max)
    {
        var v = (value ?? "").Trim();
        return v.Length <= max ? v : v[..max];
    }
}
