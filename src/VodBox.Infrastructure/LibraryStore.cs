using Microsoft.Data.Sqlite;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed class LibraryStore : ILibraryStore
{
    private readonly string _connectionString;
    public LibraryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS history(config TEXT, source TEXT, media TEXT, episode TEXT,
                title TEXT NOT NULL, uri TEXT NOT NULL, position INTEGER NOT NULL, updated TEXT NOT NULL,
                PRIMARY KEY(config,source,media,episode));
            CREATE TABLE IF NOT EXISTS favorites(config TEXT, source TEXT, media TEXT, title TEXT NOT NULL,
                PRIMARY KEY(config,source,media));
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }

    public Task SaveHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO history VALUES($c,$s,$m,$e,$t,$u,$p,$d) ON CONFLICT(config,source,media,episode) DO UPDATE SET title=$t,uri=$u,position=$p,updated=$d";
        Bind(cmd, entry.ConfigId, entry.SourceId, entry.MediaId, entry.Title);
        cmd.Parameters.AddWithValue("$e", entry.EpisodeId); cmd.Parameters.AddWithValue("$u", entry.Uri);
        cmd.Parameters.AddWithValue("$p", entry.PositionMs); cmd.Parameters.AddWithValue("$d", entry.UpdatedAt.ToString("O"));
        cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM history ORDER BY updated DESC LIMIT 200";
        using var reader = cmd.ExecuteReader(); var entries = new List<HistoryEntry>();
        while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), DateTimeOffset.Parse(reader.GetString(7))));
        return Task.FromResult<IReadOnlyList<HistoryEntry>>(entries);
    }
    public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = favorite ? "INSERT INTO favorites VALUES($c,$s,$m,$t) ON CONFLICT(config,source,media) DO UPDATE SET title=$t"
            : "DELETE FROM favorites WHERE config=$c AND source=$s AND media=$m";
        Bind(cmd, entry.ConfigId, entry.SourceId, entry.MediaId, entry.Title); cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT * FROM favorites ORDER BY title";
        using var reader = cmd.ExecuteReader(); var entries = new List<FavoriteEntry>();
        while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return Task.FromResult<IReadOnlyList<FavoriteEntry>>(entries);
    }
    private static void Bind(SqliteCommand cmd, string config, string source, string media, string title)
    {
        cmd.Parameters.AddWithValue("$c", config); cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$m", media); cmd.Parameters.AddWithValue("$t", title);
    }
}
