using System.Globalization;
using Microsoft.Data.Sqlite;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>SQLite 库存储：历史、收藏、配置订阅、键值偏好（7 表结构中桌面首版需要的 4 张）。</summary>
public sealed class LibraryStore : ILibraryStore, IConfigStore, IPreferences, IDisposable
{
    private const int SchemaVersion = 5;
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
            CREATE TABLE IF NOT EXISTS watched(source_key TEXT NOT NULL,media_id TEXT NOT NULL,line_id TEXT NOT NULL,episode_id TEXT NOT NULL,
                watched_at INTEGER NOT NULL,PRIMARY KEY(source_key,media_id,line_id,episode_id));
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
        RequireColumns(Columns(transaction,"watched"),"watched","source_key","media_id","line_id","episode_id","watched_at");
        RequirePrimaryKey(transaction,"watched","source_key","media_id","line_id","episode_id");
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

    public Task MarkEpisodeWatchedAsync(string sourceKey,string mediaId,string lineId,string episodeId,CancellationToken ct=default) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(episodeId)) return;
        using var command=Query("INSERT INTO watched(source_key,media_id,line_id,episode_id,watched_at) VALUES($s,$m,$l,$e,$t) ON CONFLICT(source_key,media_id,line_id,episode_id) DO UPDATE SET watched_at=excluded.watched_at",
            ("$s",sourceKey),("$m",mediaId),("$l",lineId),("$e",episodeId),("$t",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        command.ExecuteNonQuery();
    },ct);

    public Task<IReadOnlyList<WatchedEpisode>> ExportWatchedAsync(CancellationToken ct=default) => Task.Run<IReadOnlyList<WatchedEpisode>>(()=>
    {
        using var command=Query("SELECT source_key,media_id,line_id,episode_id,watched_at FROM watched");
        using var reader=command.ExecuteReader();var result=new List<WatchedEpisode>();
        while(reader.Read())result.Add(new WatchedEpisode(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        return result;
    },ct);

    public Task<IReadOnlySet<string>> GetWatchedEpisodesAsync(string sourceKey,string mediaId,string lineId,CancellationToken ct=default) => Task.Run<IReadOnlySet<string>>(() =>
    {
        using var command=Query("SELECT episode_id FROM watched WHERE source_key=$s AND media_id=$m AND line_id=$l",("$s",sourceKey),("$m",mediaId),("$l",lineId));
        using var reader=command.ExecuteReader();var result=new HashSet<string>();
        while(reader.Read()) result.Add(reader.GetString(0));return result;
    },ct);

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

    public Task DeleteHistoryAsync(string sourceKey,string mediaId,CancellationToken ct=default)=>Task.Run(()=>
    {
        using var connection=new SqliteConnection(_db.ConnectionString);connection.Open();using var transaction=connection.BeginTransaction();
        foreach(var table in new[]{"history","watched"})
        {
            ct.ThrowIfCancellationRequested();using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText=$"DELETE FROM {table} WHERE source_key=$k AND media_id=$m";
            command.Parameters.AddWithValue("$k",sourceKey);command.Parameters.AddWithValue("$m",mediaId);command.ExecuteNonQuery();
        }
        transaction.Commit();
    },ct);

    public Task ClearHistoryAsync(CancellationToken ct = default) => Task.Run(() => Exec("DELETE FROM history; DELETE FROM watched"), ct);

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
        using var check = Query("SELECT COUNT(*) FROM config WHERE id=$id AND kind=$kind", ("$id", id), ("$kind", (int)kind));
        if (Convert.ToInt64(check.ExecuteScalar()) != 1) throw new InvalidOperationException("订阅不存在或类型不匹配。");
        using var command = Query("UPDATE config SET active=CASE WHEN id=$id THEN 1 ELSE 0 END WHERE kind=$kind", ("$id", id), ("$kind", (int)kind));
        command.ExecuteNonQuery();
    }, ct);

    /// <summary>独立连接单事务合并；不把后台播放写入混进恢复事务。</summary>
    public Task ImportBackupAtomicAsync(LibraryBackup backup, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection(_db.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        void Execute(string sql, params (string Key, object? Value)[] values)
        {
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        foreach (var entry in backup.History)
            Execute("""
                INSERT INTO history(source_key,source_name,media_id,title,poster,remarks,line_id,episode_id,position_ms,duration_ms,rate,opening_skip,ending_skip,updated_at)
                VALUES($k,$sn,$m,$t,$p,$r,$l,$e,$pos,$dur,$rate,$op,$ed,$time)
                ON CONFLICT(source_key,media_id) DO UPDATE SET source_name=excluded.source_name,title=excluded.title,poster=excluded.poster,
                remarks=excluded.remarks,line_id=excluded.line_id,episode_id=excluded.episode_id,position_ms=excluded.position_ms,
                duration_ms=excluded.duration_ms,rate=excluded.rate,opening_skip=excluded.opening_skip,ending_skip=excluded.ending_skip,updated_at=excluded.updated_at
                WHERE excluded.updated_at>=history.updated_at
                """, ("$k",entry.SourceKey),("$sn",entry.SourceName),("$m",entry.MediaId),("$t",entry.Title),("$p",entry.Poster),("$r",entry.Remarks),
                ("$l",entry.LineId),("$e",entry.EpisodeId),("$pos",entry.PositionMs),("$dur",entry.DurationMs),("$rate",entry.Rate),
                ("$op",entry.OpeningSkipSec),("$ed",entry.EndingSkipSec),("$time",entry.UpdatedAt.ToUnixTimeMilliseconds()));
        foreach(var entry in backup.Watched)
            Execute("INSERT INTO watched(source_key,media_id,line_id,episode_id,watched_at) VALUES($s,$m,$l,$e,$t) ON CONFLICT(source_key,media_id,line_id,episode_id) DO UPDATE SET watched_at=MAX(watched.watched_at,excluded.watched_at)",
                ("$s",entry.SourceKey),("$m",entry.MediaId),("$l",entry.LineId),("$e",entry.EpisodeId),("$t",entry.WatchedAt.ToUnixTimeMilliseconds()));
        foreach (var entry in backup.Favorites)
            Execute("""
                INSERT INTO favorite(kind,source_key,media_id,source_name,title,poster,remarks,created_at) VALUES($kind,$k,$m,$sn,$t,$p,$r,$time)
                ON CONFLICT(kind,source_key,media_id) DO NOTHING
                """, ("$kind",(int)entry.Kind),("$k",entry.SourceKey),("$m",entry.MediaId),("$sn",entry.SourceName),("$t",entry.Title),
                ("$p",entry.Poster),("$r",entry.Remarks),("$time",entry.CreatedAt.ToUnixTimeMilliseconds()));
        foreach (var entry in backup.Subscriptions.DistinctBy(entry => (entry.Kind,entry.Url)))
            Execute("""
                INSERT INTO config(url,name,kind,created_at,active)
                SELECT $url,$name,$kind,$time,0 WHERE NOT EXISTS(SELECT 1 FROM config WHERE url=$url AND kind=$kind)
                """, ("$url",entry.Url),("$name",entry.Name),("$kind",(int)entry.Kind),("$time",entry.CreatedAt.ToUnixTimeMilliseconds()));
        foreach (var (key,value) in backup.Preferences)
            Execute("INSERT INTO prefs(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$key",key),("$value",value));
        foreach(var kind in new[]{ConfigKind.Vod,ConfigKind.Live})
        {
            var preference=kind==ConfigKind.Vod?"config_vod":"config_live";
            var requested=backup.Preferences.GetValueOrDefault(preference);
            if(string.IsNullOrWhiteSpace(requested))requested=backup.Subscriptions.FirstOrDefault(entry=>entry.Kind==kind&&entry.Active)?.Url;
            if(string.IsNullOrWhiteSpace(requested))continue;
            using var check=connection.CreateCommand();check.Transaction=transaction;
            check.CommandText="SELECT id FROM config WHERE kind=$kind AND url=$url ORDER BY id LIMIT 1";
            check.Parameters.AddWithValue("$kind",(int)kind);check.Parameters.AddWithValue("$url",requested);
            if(check.ExecuteScalar() is not long selected)continue;
            Execute("UPDATE config SET active=CASE WHEN id=$id THEN 1 ELSE 0 END WHERE kind=$kind",("$id",selected),("$kind",(int)kind));
            Execute("INSERT INTO prefs(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$key",preference),("$value",requested));
        }
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
    }, ct);

    public Task<Dictionary<string, string>> ExportPreferencesAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var command = Query("SELECT key,value FROM prefs");
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, string>();
        while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }, ct);

    // ---------- 键值偏好 ----------

    private string? ReadPref(string key)
    {
        using var command = Query("SELECT value FROM prefs WHERE key=$k", ("$k", key));
        return command.ExecuteScalar() as string;
    }

    public string GetString(string key, string fallback = "") => ReadPref(key) ?? fallback;
    public int GetInt(string key, int fallback = 0) => int.TryParse(ReadPref(key),NumberStyles.Integer,CultureInfo.InvariantCulture,out var v) ? v : fallback;
    public bool GetBool(string key, bool fallback = false) => ReadPref(key) switch
    {
        "1" or "true" or "True" => true,
        "0" or "false" or "False" => false,
        _ => fallback,
    };
    public double GetDouble(string key,double fallback=0)
    {
        var text=ReadPref(key);
        if(double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value))return value;
        // 兼容早期随系统语言写入的小数，禁止千分位解释以免1,5误读成15。
        if(double.TryParse(text,NumberStyles.Float,CultureInfo.CurrentCulture,out value)&&double.IsFinite(value))return value;
        return fallback;
    }

    public void Set(string key, string value) => WritePref(key, value);
    public void Set(string key, int value) => WritePref(key, value.ToString(CultureInfo.InvariantCulture));
    public void Set(string key, bool value) => WritePref(key, value ? "1" : "0");
    public void Set(string key,double value)
    {
        if(!double.IsFinite(value))throw new ArgumentOutOfRangeException(nameof(value));
        WritePref(key,value.ToString("R",CultureInfo.InvariantCulture));
    }

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
