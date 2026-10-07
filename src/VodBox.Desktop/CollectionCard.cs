using VodBox.Core;

namespace VodBox.Desktop;

public sealed class CollectionCard
{
    public MediaCard Card { get; }
    public HistoryEntry? History { get; }
    public FavoriteEntry? Favorite { get; }
    public SearchHit? SearchHit { get; }
    public string Title => Card.Title;
    public string SourceName { get; }
    public string Caption { get; }
    public CollectionCard(MediaItem item, string sourceName, string caption = "", HistoryEntry? history = null, FavoriteEntry? favorite = null, SearchHit? searchHit = null)
    { Card = new(item); SourceName = sourceName; Caption = caption; History = history; Favorite = favorite; SearchHit = searchHit; }
}
