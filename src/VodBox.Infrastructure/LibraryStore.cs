using Microsoft.Data.Sqlite;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed partial class LibraryStore : ILibraryStore
{
    private readonly string _connectionString;
    public LibraryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        int version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > 3) throw new InvalidDataException("数据库版本高于当前应用支持的版本。");
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS history(config TEXT, source TEXT, media TEXT, episode TEXT,
                title TEXT NOT NULL, uri TEXT NOT NULL, position INTEGER NOT NULL, updated TEXT NOT NULL,
                PRIMARY KEY(config,source,media,episode));
            CREATE TABLE IF NOT EXISTS favorites(config TEXT, source TEXT, media TEXT, title TEXT NOT NULL,
                PRIMARY KEY(config,source,media));
            """;
        cmd.ExecuteNonQuery();
        if (version < 2)
        {
            using var transaction = connection.BeginTransaction(); cmd.Transaction = transaction;
            cmd.CommandText = "ALTER TABLE history ADD COLUMN resolution INTEGER NOT NULL DEFAULT 0; ALTER TABLE history ADD COLUMN resolver TEXT; PRAGMA user_version=2;";
            cmd.ExecuteNonQuery(); transaction.Commit();
        }
        if (version < 3)
        {
            using var transaction = connection.BeginTransaction(); cmd.Transaction = transaction;
            cmd.CommandText = "ALTER TABLE history ADD COLUMN poster TEXT; ALTER TABLE history ADD COLUMN source_name TEXT; ALTER TABLE favorites ADD COLUMN poster TEXT; ALTER TABLE favorites ADD COLUMN source_name TEXT; PRAGMA user_version=3;";
            cmd.ExecuteNonQuery(); transaction.Commit();
        }
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }

    public Task SaveHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO history(config,source,media,episode,title,uri,position,updated,resolution,resolver,poster,source_name) VALUES($c,$s,$m,$e,$t,$u,$p,$d,$r,$v,$a,$n) ON CONFLICT(config,source,media,episode) DO UPDATE SET title=$t,uri=$u,position=$p,updated=$d,resolution=$r,resolver=$v,poster=$a,source_name=$n";
        Bind(cmd, entry.ConfigId, entry.SourceId, entry.MediaId, entry.Title);
        cmd.Parameters.AddWithValue("$e", entry.EpisodeId); cmd.Parameters.AddWithValue("$u", entry.Uri);
        cmd.Parameters.AddWithValue("$p", entry.PositionMs); cmd.Parameters.AddWithValue("$d", entry.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$a", (object?)entry.Poster ?? DBNull.Value); cmd.Parameters.AddWithValue("$n", (object?)entry.SourceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (int)entry.ResolutionKind); cmd.Parameters.AddWithValue("$v", (object?)entry.ResolverId ?? DBNull.Value);
        cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT config,source,media,episode,title,uri,position,updated,resolution,resolver,poster,source_name FROM history ORDER BY updated DESC LIMIT 200";
        using var reader = cmd.ExecuteReader(); var entries = new List<HistoryEntry>();
        while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), DateTimeOffset.Parse(reader.GetString(7)), (ResolutionKind)reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        return Task.FromResult<IReadOnlyList<HistoryEntry>>(entries);
    }
    public Task DeleteHistoryAsync(HistoryEntry? entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = entry is null ? "DELETE FROM history" : "DELETE FROM history WHERE config=$c AND source=$s AND media=$m AND episode=$e";
        if (entry is not null)
        {
            command.Parameters.AddWithValue("$c", entry.ConfigId); command.Parameters.AddWithValue("$s", entry.SourceId);
            command.Parameters.AddWithValue("$m", entry.MediaId); command.Parameters.AddWithValue("$e", entry.EpisodeId);
        }
        command.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = favorite ? "INSERT INTO favorites(config,source,media,title,poster,source_name) VALUES($c,$s,$m,$t,$a,$n) ON CONFLICT(config,source,media) DO UPDATE SET title=$t,poster=$a,source_name=$n"
            : "DELETE FROM favorites WHERE config=$c AND source=$s AND media=$m";
        Bind(cmd, entry.ConfigId, entry.SourceId, entry.MediaId, entry.Title);
        cmd.Parameters.AddWithValue("$a", (object?)entry.Poster ?? DBNull.Value); cmd.Parameters.AddWithValue("$n", (object?)entry.SourceName ?? DBNull.Value); cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT * FROM favorites ORDER BY title";
        using var reader = cmd.ExecuteReader(); var entries = new List<FavoriteEntry>();
        while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return Task.FromResult<IReadOnlyList<FavoriteEntry>>(entries);
    }
    private static void Bind(SqliteCommand cmd, string config, string source, string media, string title)
    {
        cmd.Parameters.AddWithValue("$c", config); cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$m", media); cmd.Parameters.AddWithValue("$t", title);
    }
}
