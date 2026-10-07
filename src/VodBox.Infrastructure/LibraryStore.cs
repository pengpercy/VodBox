using System.Text;
using Microsoft.Data.Sqlite;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>SQLite 库存储：历史、收藏、配置订阅、键值偏好（7 表结构中桌面首版需要的 4 张）。</summary>
public sealed class LibraryStore : ILibraryStore, IConfigStore, IPreferences, IDisposable
{
    private readonly SqliteConnection _db;

    public LibraryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Migrate();
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS history(
                source_key TEXT NOT NULL, source_name TEXT NOT NULL, media_id TEXT NOT NULL,
                title TEXT NOT NULL, poster TEXT, remarks TEXT, line_id TEXT DEFAULT '',
                episode_id TEXT DEFAULT '', position_ms INTEGER DEFAULT 0, duration_ms INTEGER DEFAULT 0,
                rate REAL DEFAULT 1.0, opening_skip INTEGER DEFAULT 0, ending_skip INTEGER DEFAULT 0,
                updated_at INTEGER NOT NULL, PRIMARY KEY(source_key, media_id));
            CREATE TABLE IF NOT EXISTS favorite(
                kind INTEGER NOT NULL, source_key TEXT NOT NULL, media_id TEXT NOT NULL,
                source_name TEXT NOT NULL, title TEXT NOT NULL, poster TEXT, remarks TEXT,
                created_at INTEGER NOT NULL, PRIMARY KEY(kind, source_key, media_id));
            CREATE TABLE IF NOT EXISTS config(
                id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, name TEXT NOT NULL,
                kind INTEGER NOT NULL, created_at INTEGER NOT NULL, active INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS prefs(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    private int Exec(string sql)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Query(string sql, params (string, object?)[] args)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    // ---------- 历史 ----------

    public async Task SaveHistoryAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            using var command = Query("""
                INSERT INTO history(source_key, source_name, media_id, title, poster, remarks, line_id, episode_id,
                    position_ms, duration_ms, rate, opening_skip, ending_skip, updated_at)
                VALUES($k,$sn,$m,$t,$p,$r,$l,$e,$pos,$dur,$rate,$op,$ed,$time)
                ON CONFLICT(source_key, media_id) DO UPDATE SET source_name=$sn, title=$t, poster=$p, remarks=$r,
                    line_id=$l, episode_id=$e, position_ms=$pos, duration_ms=$dur, rate=$rate,
                    opening_skip=$op, ending_skip=$ed, updated_at=$time
                """,
                ("$k", entry.SourceKey), ("$sn", entry.SourceName), ("$m", entry.MediaId), ("$t", entry.Title),
                ("$p", entry.Poster), ("$r", entry.Remarks), ("$l", entry.LineId), ("$e", entry.EpisodeId),
                ("$pos", entry.PositionMs), ("$dur", entry.DurationMs), ("$rate", entry.Rate),
                ("$op", entry.OpeningSkipSec), ("$ed", entry.EndingSkipSec), ("$time", entry.UpdatedAt.ToUnixTimeMilliseconds()));
            command.ExecuteNonQuery();
        }, ct);
    }

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit = 200, CancellationToken ct = default) => Task.Run<IReadOnlyList<HistoryEntry>>(() =>
    {
        using var command = Query($"SELECT * FROM history ORDER BY updated_at DESC LIMIT {limit}");
        using var reader = command.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (reader.Read()) list.Add(ReadHistory(reader));
        return list;
    }, ct);

    public Task<HistoryEntry?> FindHistoryAsync(string sourceKey, string mediaId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var command = Query("SELECT * FROM history WHERE source_key=$k AND media_id=$m", ("$k", sourceKey), ("$m", mediaId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHistory(reader) : null;
    }, ct);

    private static HistoryEntry ReadHistory(SqliteDataReader reader) => new()
    {
        SourceKey = reader.GetString(reader.GetOrdinal("source_key")),
        SourceName = reader.GetString(reader.GetOrdinal("source_name")),
        MediaId = reader.GetString(reader.GetOrdinal("media_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Poster = reader.IsDBNull(reader.GetOrdinal("poster")) ? null : reader.GetString(reader.GetOrdinal("poster")),
        Remarks = reader.IsDBNull(reader.GetOrdinal("remarks")) ? null : reader.GetString(reader.GetOrdinal("remarks")),
        LineId = reader.GetString(reader.GetOrdinal("line_id")),
        EpisodeId = reader.GetString(reader.GetOrdinal("episode_id")),
        PositionMs = reader.GetInt64(reader.GetOrdinal("position_ms")),
        DurationMs = reader.GetInt64(reader.GetOrdinal("duration_ms")),
        Rate = reader.GetDouble(reader.GetOrdinal("rate")),
        OpeningSkipSec = reader.GetInt32(reader.GetOrdinal("opening_skip")),
        EndingSkipSec = reader.GetInt32(reader.GetOrdinal("ending_skip")),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("updated_at"))),
    };

    public Task DeleteHistoryAsync(string sourceKey, string mediaId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var command = Query("DELETE FROM history WHERE source_key=$k AND media_id=$m", ("$k", sourceKey), ("$m", mediaId));
        command.ExecuteNonQuery();
    }, ct);

    public Task ClearHistoryAsync(CancellationToken ct = default) => Task.Run(() => Exec("DELETE FROM history"), ct);

    // ---------- 收藏 ----------

    public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken ct = default) => Task.Run(() =>
    {
        if (favorite)
        {
            using var command = Query("""
                INSERT INTO favorite(kind, source_key, media_id, source_name, title, poster, remarks, created_at)
                VALUES($kind,$k,$m,$sn,$t,$p,$r,$time)
                ON CONFLICT(kind, source_key, media_id) DO UPDATE SET source_name=$sn, title=$t, poster=$p, remarks=$r
                """,
                ("$kind", (int)entry.Kind), ("$k", entry.SourceKey), ("$m", entry.MediaId), ("$sn", entry.SourceName),
                ("$t", entry.Title), ("$p", entry.Poster), ("$r", entry.Remarks), ("$time", entry.CreatedAt.ToUnixTimeMilliseconds()));
            command.ExecuteNonQuery();
        }
        else
        {
            using var command = Query("DELETE FROM favorite WHERE kind=$kind AND source_key=$k AND media_id=$m",
                ("$kind", (int)entry.Kind), ("$k", entry.SourceKey), ("$m", entry.MediaId));
            command.ExecuteNonQuery();
        }
    }, ct);

    public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(FavoriteKind kind, CancellationToken ct = default) => Task.Run<IReadOnlyList<FavoriteEntry>>(() =>
    {
        using var command = Query("SELECT * FROM favorite WHERE kind=$kind ORDER BY created_at DESC", ("$kind", (int)kind));
        using var reader = command.ExecuteReader();
        var list = new List<FavoriteEntry>();
        while (reader.Read()) list.Add(ReadFavorite(reader));
        return list;
    }, ct);

    public Task<bool> IsFavoriteAsync(string sourceKey, string mediaId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var command = Query("SELECT COUNT(1) FROM favorite WHERE kind=$kind AND source_key=$k AND media_id=$m",
            ("$kind", (int)FavoriteKind.Vod), ("$k", sourceKey), ("$m", mediaId));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }, ct);

    private static FavoriteEntry ReadFavorite(SqliteDataReader reader) => new()
    {
        Kind = (FavoriteKind)reader.GetInt32(reader.GetOrdinal("kind")),
        SourceKey = reader.GetString(reader.GetOrdinal("source_key")),
        MediaId = reader.GetString(reader.GetOrdinal("media_id")),
        SourceName = reader.GetString(reader.GetOrdinal("source_name")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Poster = reader.IsDBNull(reader.GetOrdinal("poster")) ? null : reader.GetString(reader.GetOrdinal("poster")),
        Remarks = reader.IsDBNull(reader.GetOrdinal("remarks")) ? null : reader.GetString(reader.GetOrdinal("remarks")),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at"))),
    };

    // ---------- 配置订阅 ----------

    public Task<IReadOnlyList<ConfigSubscription>> ListAsync(ConfigKind kind, CancellationToken ct = default) => Task.Run<IReadOnlyList<ConfigSubscription>>(() =>
    {
        using var command = Query("SELECT * FROM config WHERE kind=$kind ORDER BY created_at DESC", ("$kind", (int)kind));
        using var reader = command.ExecuteReader();
        var list = new List<ConfigSubscription>();
        while (reader.Read())
        {
            list.Add(new ConfigSubscription
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Url = reader.GetString(reader.GetOrdinal("url")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Kind = (ConfigKind)reader.GetInt32(reader.GetOrdinal("kind")),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at"))),
                Active = reader.GetInt32(reader.GetOrdinal("active")) != 0,
            });
        }
        return list;
    }, ct);

    public Task AddAsync(ConfigSubscription subscription, CancellationToken ct = default) => Task.Run(() =>
    {
        using var command = Query("INSERT INTO config(url, name, kind, created_at, active) VALUES($u,$n,$kind,$time,0)",
            ("$u", subscription.Url), ("$n", subscription.Name), ("$kind", (int)subscription.Kind),
            ("$time", subscription.CreatedAt.ToUnixTimeMilliseconds()));
        command.ExecuteNonQuery();
    }, ct);

    public Task RemoveAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var command = Query("DELETE FROM config WHERE id=$id", ("$id", id));
        command.ExecuteNonQuery();
    }, ct);

    public Task SetActiveAsync(ConfigKind kind, long id, CancellationToken ct = default) => Task.Run(() =>
    {
        Exec("UPDATE config SET active=0 WHERE kind=$kind".Replace("$kind", ((int)kind).ToString()));
        using var command = Query("UPDATE config SET active=1 WHERE id=$id", ("$id", id));
        command.ExecuteNonQuery();
    }, ct);

    // ---------- 键值偏好 ----------

    private string? ReadPref(string key)
    {
        using var command = Query("SELECT value FROM prefs WHERE key=$k", ("$k", key));
        return command.ExecuteScalar() as string;
    }

    public string GetString(string key, string fallback = "") => ReadPref(key) ?? fallback;
    public int GetInt(string key, int fallback = 0) => int.TryParse(ReadPref(key), out var v) ? v : fallback;
    public bool GetBool(string key, bool fallback = false) => ReadPref(key) switch
    {
        "1" or "true" or "True" => true,
        "0" or "false" or "False" => false,
        _ => fallback,
    };
    public double GetDouble(string key, double fallback = 0) => double.TryParse(ReadPref(key), out var v) ? v : fallback;

    public void Set(string key, string value) => WritePref(key, value);
    public void Set(string key, int value) => WritePref(key, value.ToString());
    public void Set(string key, bool value) => WritePref(key, value ? "1" : "0");
    public void Set(string key, double value) => WritePref(key, value.ToString());

    private void WritePref(string key, string value)
    {
        using var command = Query("""
            INSERT INTO prefs(key, value) VALUES($k,$v)
            ON CONFLICT(key) DO UPDATE SET value=$v
            """, ("$k", key), ("$v", value));
        command.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
