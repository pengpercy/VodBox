using VodBox.Core;

namespace VodBox.Application;

/// <summary>Retains the playing source/playlist independently of whatever the user is browsing.</summary>
public sealed class PlaylistSession(PlaybackCoordinator coordinator, IProviderFactory factory)
{
    private sealed record Selection(SourceDefinition Source, MediaDetail Detail, PlaybackLine Line, int Index, string ConfigId, long SessionId);
    private Selection? _selection;
    private long _generation;
    public bool HasNext => _selection is { } value && value.Index + 1 < value.Line.Episodes.Count;
    public bool HasPrevious => _selection is { Index: > 0 };
    public string? CurrentEpisodeId => _selection is { } value ? value.Line.Episodes[value.Index].Id : null;
    public void Clear() { Interlocked.Increment(ref _generation); _selection = null; }
    public async Task PlayAsync(SourceDefinition source, MediaDetail detail, PlaybackLine line, Episode episode, string configId,
        long startPositionMs = 0, CancellationToken token = default)
    {
        int index = line.Episodes.ToList().FindIndex(x => x.Id == episode.Id);
        if (index < 0) throw new InvalidDataException("所选集数不在播放线路中。");
        long generation = Interlocked.Increment(ref _generation);
        await using var provider = factory.Create(source);
        await coordinator.PlayAsync(async cancellation =>
        {
            var request = await provider.ResolvePlaybackAsync(detail.Item.Id, episode.Id, cancellation);
            return request with { SourceId = source.Id, MediaId = detail.Item.Id, EpisodeId = episode.Id,
                StartPositionMs = startPositionMs > 0 ? startPositionMs : request.StartPositionMs };
        }, configId, token);
        if (generation == _generation)
            _selection = new(source, detail, line, index, configId, coordinator.SessionId);
    }
    public Task<bool> MoveAsync(int offset, long startPositionMs = 0, CancellationToken token = default)
    {
        var value = _selection;
        if (value is null || value.SessionId != coordinator.SessionId || value.Index + offset < 0 || value.Index + offset >= value.Line.Episodes.Count)
            return Task.FromResult(false);
        return MoveCoreAsync(value, offset, startPositionMs, token);
    }
    private async Task<bool> MoveCoreAsync(Selection selection, int offset, long startPositionMs, CancellationToken token)
    {
        await PlayAsync(selection.Source, selection.Detail, selection.Line, selection.Line.Episodes[selection.Index + offset], selection.ConfigId, startPositionMs, token);
        return true;
    }
}
