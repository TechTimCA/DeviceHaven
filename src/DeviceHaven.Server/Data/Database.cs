using Microsoft.Data.Sqlite;

namespace DeviceHaven.Server.Data;

/// <summary>
/// One SQLite file holds everything. There is no database server to install
/// or expose on the network. (Encryption of the file itself is planned; the
/// secrets inside are already stored only as hashes.)
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string dataDirectory)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "devicehaven.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Migrate()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS enrollment_codes (
                code_hash   TEXT PRIMARY KEY,
                created_at  TEXT NOT NULL,
                expires_at  TEXT NOT NULL,
                used_at     TEXT,
                used_by     TEXT,
                note        TEXT,
                max_uses    INTEGER NOT NULL DEFAULT 1,
                uses        INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS devices (
                id              TEXT PRIMARY KEY,
                hostname        TEXT NOT NULL,
                os_name         TEXT NOT NULL,
                os_version      TEXT NOT NULL,
                agent_version   TEXT NOT NULL,
                token_hash      TEXT NOT NULL,
                enrolled_at     TEXT NOT NULL,
                last_seen       TEXT,
                last_ip         TEXT,
                inventory_json  TEXT,
                inventory_hash  TEXT,
                inventory_at    TEXT,
                revoked         INTEGER NOT NULL DEFAULT 0,
                primary_ip      TEXT
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                at      TEXT NOT NULL,
                actor   TEXT NOT NULL,
                action  TEXT NOT NULL,
                target  TEXT,
                detail  TEXT
            );

            -- The audit log can only be added to, never changed or deleted.
            CREATE TRIGGER IF NOT EXISTS audit_log_no_update BEFORE UPDATE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS audit_log_no_delete BEFORE DELETE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;

            CREATE TABLE IF NOT EXISTS commands (
                id            TEXT PRIMARY KEY,
                device_id     TEXT NOT NULL,
                kind          TEXT NOT NULL,
                payload       TEXT,
                status        TEXT NOT NULL,          -- pending, sent, done, failed
                created_at    TEXT NOT NULL,
                created_by    TEXT NOT NULL,
                sent_at       TEXT,
                completed_at  TEXT,
                result        TEXT
            );
            CREATE INDEX IF NOT EXISTS commands_device ON commands (device_id, status);

            -- What each PC reported as waiting, replaced on every new report.
            CREATE TABLE IF NOT EXISTS device_updates (
                device_id          TEXT NOT NULL,
                kind               TEXT NOT NULL,     -- app or windows
                key                TEXT NOT NULL,     -- winget id or Windows update id
                title              TEXT NOT NULL,
                current_version    TEXT,
                available_version  TEXT,
                severity           TEXT,
                kb                 TEXT,
                size_bytes         INTEGER,
                may_restart        INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (device_id, kind, key)
            );

            -- Your approvals, and how each install went.
            CREATE TABLE IF NOT EXISTS update_approvals (
                device_id     TEXT NOT NULL,
                kind          TEXT NOT NULL,
                key           TEXT NOT NULL,
                command_id    TEXT NOT NULL,
                status        TEXT NOT NULL,          -- approved, installed, failed
                message       TEXT,
                approved_at   TEXT NOT NULL,
                approved_by   TEXT NOT NULL,
                completed_at  TEXT,
                PRIMARY KEY (device_id, kind, key)
            );

            INSERT INTO schema_info (version)
            SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            """;
        command.ExecuteNonQuery();

        // Version 0.1.9: devices.primary_ip (the PC's own main IPv4 address).
        AddColumnIfMissing(connection, "devices", "primary_ip", "TEXT");

        // Version 0.2: update reports.
        AddColumnIfMissing(connection, "devices", "updates_hash", "TEXT");
        AddColumnIfMissing(connection, "devices", "updates_at", "TEXT");
        AddColumnIfMissing(connection, "devices", "reboot_pending", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "devices", "updates_error", "TEXT");
    }

    private static void AddColumnIfMissing(Microsoft.Data.Sqlite.SqliteConnection connection, string table, string column, string type)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
        check.Parameters.AddWithValue("$c", column);
        if (Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }
}
