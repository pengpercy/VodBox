using System.Collections.ObjectModel;

namespace VodBox.Desktop;

// Virtualize rows, keeping the small set of cards inside each realized row together.
public sealed class MediaCardRow(int columns)
{
    public int Columns { get; } = columns;
    public ObservableCollection<MediaCard> Cards { get; } = [];
}
