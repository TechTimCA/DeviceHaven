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
                note        TEXT
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
                revoked         INTEGER NOT NULL DEFAULT 0
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

            INSERT INTO schema_info (version)
            SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            """;
        command.ExecuteNonQuery();
    }
}
