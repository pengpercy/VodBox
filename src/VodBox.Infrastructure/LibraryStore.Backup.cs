using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed partial class LibraryStore
{
    public LibrarySnapshot ExportSnapshot(CancellationToken token = default)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT config,source,media,episode,title,uri,position,updated,resolution,resolver FROM history";
        var result = new LibrarySnapshot();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (result.History.Count >= 100000) throw new InvalidDataException("备份历史条目过多。");
                result.History.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), DateTimeOffset.Parse(reader.GetString(7)), (ResolutionKind)reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        command.CommandText = "SELECT config,source,media,title FROM favorites";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) { token.ThrowIfCancellationRequested(); if (result.Favorites.Count >= 100000) throw new InvalidDataException("备份收藏条目过多。"); result.Favorites.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))); }
        transaction.Commit(); return result;
    }
    public void MergeSnapshot(LibrarySnapshot snapshot, CancellationToken token = default)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var entry in snapshot.History)
        {
            token.ThrowIfCancellationRequested(); command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO history(config,source,media,episode,title,uri,position,updated,resolution,resolver)
                VALUES($c,$s,$m,$e,$t,$u,$p,$d,$r,$v)
                ON CONFLICT(config,source,media,episode) DO UPDATE SET title=$t,uri=$u,position=$p,updated=$d,resolution=$r,resolver=$v
                WHERE julianday($d) > julianday(history.updated)
                """;
            Bind(command, entry.ConfigId, entry.SourceId, entry.MediaId, entry.Title);
            command.Parameters.AddWithValue("$e", entry.EpisodeId); command.Parameters.AddWithValue("$u", entry.Uri);
            command.Parameters.AddWithValue("$p", entry.PositionMs); command.Parameters.AddWithValue("$d", entry.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$r", (int)entry.ResolutionKind); command.Parameters.AddWithValue("$v", (object?)entry.ResolverId ?? DBNull.Value); command.ExecuteNonQuery();
        }
        foreach (var entry in snapshot.Favorites)
        {
            token.ThrowIfCancellationRequested(); command.Parameters.Clear(); Bind(command, entry.ConfigId, entry.SourceId, entry.MediaId, entry.Title);
            command.CommandText = "INSERT INTO favorites VALUES($c,$s,$m,$t) ON CONFLICT(config,source,media) DO NOTHING"; command.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }
}
