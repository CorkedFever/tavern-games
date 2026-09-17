using Microsoft.Data.Sqlite;

namespace TavernGames.Server.Data;

/// <summary>
/// The server's one SQLite file: profiles, venues and game results. Rooms and live
/// games stay in memory; only what should survive a restart lands here.
///
/// The schema is a list of numbered scripts. On startup every script newer than the
/// file's <c>PRAGMA user_version</c> runs in order, so deploying a new build upgrades
/// an existing database in place. To change the schema, append a script; never edit
/// one that has shipped.
/// </summary>
public sealed class TavernDb
{
    private static readonly string[] Migrations =
    [
        // v1: profiles, venues, results
        """
        CREATE TABLE profiles (
            id            TEXT PRIMARY KEY,
            token_hash    TEXT NOT NULL UNIQUE,
            display_name  TEXT NOT NULL,
            tagline       TEXT NOT NULL DEFAULT '',
            created_at    TEXT NOT NULL,
            last_seen_at  TEXT NOT NULL
        );

        CREATE TABLE venues (
            id           TEXT PRIMARY KEY,
            name         TEXT NOT NULL,
            description  TEXT NOT NULL DEFAULT '',
            join_code    TEXT NOT NULL UNIQUE,
            created_at   TEXT NOT NULL
        );

        CREATE TABLE venue_members (
            venue_id    TEXT NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
            profile_id  TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
            role        INTEGER NOT NULL,
            joined_at   TEXT NOT NULL,
            PRIMARY KEY (venue_id, profile_id)
        );
        CREATE INDEX ix_venue_members_profile ON venue_members(profile_id);

        CREATE TABLE game_results (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            venue_id     TEXT REFERENCES venues(id) ON DELETE SET NULL,
            game_type    TEXT NOT NULL,
            human_count  INTEGER NOT NULL,
            ended_at     TEXT NOT NULL
        );
        CREATE INDEX ix_game_results_venue ON game_results(venue_id, game_type);

        CREATE TABLE game_result_players (
            result_id   INTEGER NOT NULL REFERENCES game_results(id) ON DELETE CASCADE,
            profile_id  TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
            won         INTEGER NOT NULL,
            PRIMARY KEY (result_id, profile_id)
        );
        CREATE INDEX ix_game_result_players_profile ON game_result_players(profile_id);
        """,
    ];

    private readonly string _connectionString;

    public TavernDb(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
        }.ToString();

        Migrate();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();

        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        long version;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            version = (long)read.ExecuteScalar()!;
        }

        for (var next = (int)version; next < Migrations.Length; next++)
        {
            using var transaction = connection.BeginTransaction();
            using var apply = connection.CreateCommand();
            apply.Transaction = transaction;
            apply.CommandText = Migrations[next] + $"\nPRAGMA user_version = {next + 1};";
            apply.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}

/// <summary>Small helpers so the stores read as SQL plus parameters, not ADO.NET ceremony.</summary>
internal static class SqliteExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static int Execute(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static T? Scalar<T>(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public static List<T> Query<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }
}
