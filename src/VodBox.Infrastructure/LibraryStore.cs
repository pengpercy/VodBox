using System.Globalization;
using Microsoft.Data.Sqlite;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>SQLite 库存储：历史、收藏、配置订阅、键值偏好（7 表结构中桌面首版需要的 4 张）。</summary>
public sealed class LibraryStore : ILibraryStore, IConfigStore, IPreferences, IDisposable
{
    private const int SchemaVersion = 4;
    private readonly SqliteConnection _db;

    public LibraryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            _db.Open();
            Migrate(); // 完成升级和验证后才允许服务/视图模型读取库存储。
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    private void Migrate()
    {
        using var transaction = _db.BeginTransaction();
        using (var version = MigrationQuery(transaction, "PRAGMA user_version"))
        {
            if (Convert.ToInt32(version.ExecuteScalar()) > SchemaVersion)
                throw new InvalidDataException("库存储版本高于当前应用支持的版本，未修改数据库。");
        }

        var historyColumns = Columns(transaction, "history");
        var favoriteColumns = Columns(transaction, "favorites");
        var legacyHistory = historyColumns.Count > 0 && !historyColumns.Contains("source_key");
        if (legacyHistory)
        {
            RequireColumns(historyColumns, "history", "config", "source", "media", "episode", "title", "uri", "position", "updated");
            using var rename = MigrationQuery(transaction, "ALTER TABLE history RENAME TO history_legacy");
            rename.ExecuteNonQuery();
        }
        if (favoriteColumns.Count > 0)
        {
            RequireColumns(favoriteColumns, "favorites", "config", "source", "media", "title");
            using var rename = MigrationQuery(transaction, "ALTER TABLE favorites RENAME TO favorites_legacy");
            rename.ExecuteNonQuery();
        }

        using (var create = MigrationQuery(transaction, """
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
            """)) create.ExecuteNonQuery();

        ValidateSchema(transaction);
        if (legacyHistory) ImportHistory(transaction, historyColumns);
        if (favoriteColumns.Count > 0) ImportFavorites(transaction, favoriteColumns);
        using (var version = MigrationQuery(transaction, $"PRAGMA user_version={SchemaVersion}"))
            version.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteCommand MigrationQuery(SqliteTransaction transaction, string sql, params (string, object?)[] args)
    {
        var command = Query(sql, args);
        command.Transaction = transaction;
        return command;
    }

    private HashSet<string> Columns(SqliteTransaction transaction, string table)
    {
        using var command = MigrationQuery(transaction, $"PRAGMA table_info({table})");
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static void RequireColumns(HashSet<string> columns, string table, params string[] required)
    {
        if (required.Any(column => !columns.Contains(column)))
            throw new InvalidDataException($"库存储表 {table} 的结构不受支持，未修改数据库。");
    }

    private void ValidateSchema(SqliteTransaction transaction)
    {
        RequireColumns(Columns(transaction, "history"), "history", "source_key", "source_name", "media_id", "title",
            "poster", "remarks", "line_id", "episode_id", "position_ms", "duration_ms", "rate", "opening_skip", "ending_skip", "updated_at");
        RequireColumns(Columns(transaction, "favorite"), "favorite", "kind", "source_key", "media_id", "source_name",
            "title", "poster", "remarks", "created_at");
        RequireColumns(Columns(transaction, "config"), "config", "id", "url", "name", "kind", "created_at", "active");
        RequireColumns(Columns(transaction, "prefs"), "prefs", "key", "value");
        RequirePrimaryKey(transaction, "history", "source_key", "media_id");
        RequirePrimaryKey(transaction, "favorite", "kind", "source_key", "media_id");
        RequirePrimaryKey(transaction, "config", "id");
        RequirePrimaryKey(transaction, "prefs", "key");
    }

    private void RequirePrimaryKey(SqliteTransaction transaction, string table, params string[] required)
    {
        using var command = MigrationQuery(transaction, $"PRAGMA table_info({table})");
        using var reader = command.ExecuteReader();
        var key = new SortedDictionary<int, string>();
        while (reader.Read())
        {
            var position = reader.GetInt32(5);
            if (position > 0) key.Add(position, reader.GetString(1));
        }
        if (!key.Values.SequenceEqual(required, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"库存储表 {table} 的主键不受支持，未修改数据库。");
    }

    private static string? OptionalString(SqliteDataReader reader, HashSet<string> columns, string column) =>
        !columns.Contains(column) || reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

    private void ImportHistory(SqliteTransaction transaction, HashSet<string> columns)
    {
        // 保留旧表所有配置/集数及解析字段；当前主键只容纳每部媒体的最新观看记录。
        var entries = new List<HistoryEntry>();
        using (var command = MigrationQuery(transaction, "SELECT * FROM history_legacy ORDER BY rowid DESC"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var timestamp = reader.GetString(reader.GetOrdinal("updated"));
                if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated))
                    throw new InvalidDataException("旧观看历史时间格式不受支持，迁移已回滚。");
                var source = OptionalString(reader, columns, "source") ?? "";
                entries.Add(new HistoryEntry
                {
                    SourceKey = source,
                    SourceName = OptionalString(reader, columns, "source_name") ?? source,
                    MediaId = OptionalString(reader, columns, "media") ?? "",
                    Title = reader.GetString(reader.GetOrdinal("title")),
                    Poster = OptionalString(reader, columns, "poster"),
                    // 旧选集 ID 与当前源未必兼容，原始 URI/配置/集数仍在归档中完整保存。
                    EpisodeId = OptionalString(reader, columns, "episode") ?? "",
                    PositionMs = reader.GetInt64(reader.GetOrdinal("position")),
                    UpdatedAt = updated,
                });
            }
        }
        foreach (var entry in entries.OrderByDescending(entry => entry.UpdatedAt))
        {
            using var insert = MigrationQuery(transaction, """
                INSERT INTO history(source_key, source_name, media_id, title, poster, episode_id, updated_at, position_ms)
                VALUES($k,$sn,$m,$t,$p,$e,$time,$pos)
                ON CONFLICT(source_key, media_id) DO NOTHING
                """, ("$k", entry.SourceKey), ("$sn", entry.SourceName), ("$m", entry.MediaId), ("$t", entry.Title),
                ("$p", entry.Poster), ("$e", entry.EpisodeId), ("$time", entry.UpdatedAt.ToUnixTimeMilliseconds()), ("$pos", entry.PositionMs));
            insert.ExecuteNonQuery();
        }
    }

    private void ImportFavorites(SqliteTransaction transaction, HashSet<string> columns)
    {
        var entries = new List<FavoriteEntry>();
        using (var command = MigrationQuery(transaction, "SELECT * FROM favorites_legacy ORDER BY rowid DESC"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var source = OptionalString(reader, columns, "source") ?? "";
                entries.Add(new FavoriteEntry
                {
                    SourceKey = source,
                    SourceName = OptionalString(reader, columns, "source_name") ?? source,
                    MediaId = OptionalString(reader, columns, "media") ?? "",
                    Title = reader.GetString(reader.GetOrdinal("title")),
                    Poster = OptionalString(reader, columns, "poster"),
                    CreatedAt = DateTimeOffset.UnixEpoch, // 旧收藏未保存创建时间。
                });
            }
        }
        foreach (var entry in entries)
        {
            using var insert = MigrationQuery(transaction, """
                INSERT INTO favorite(kind, source_key, media_id, source_name, title, poster, created_at)
                VALUES($kind,$k,$m,$sn,$t,$p,$time)
                ON CONFLICT(kind, source_key, media_id) DO NOTHING
                """, ("$kind", (int)FavoriteKind.Vod), ("$k", entry.SourceKey), ("$m", entry.MediaId),
                ("$sn", entry.SourceName), ("$t", entry.Title), ("$p", entry.Poster), ("$time", entry.CreatedAt.ToUnixTimeMilliseconds()));
            insert.ExecuteNonQuery();
        }
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
