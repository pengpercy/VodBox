using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace VodBox.Desktop.Views;

public sealed class CollectionCardRow(int columns)
{
    public int Columns { get; } = columns;
    public ObservableCollection<CollectionCard> Cards { get; } = [];
}
public sealed partial class CollectionGridView : UserControl
{
    public static readonly StyledProperty<IEnumerable<CollectionCard>?> ItemsProperty = AvaloniaProperty.Register<CollectionGridView, IEnumerable<CollectionCard>?>(nameof(Items));
    public static readonly StyledProperty<bool> HorizontalCardsProperty = AvaloniaProperty.Register<CollectionGridView, bool>(nameof(HorizontalCards));
    public IEnumerable<CollectionCard>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public bool HorizontalCards { get => GetValue(HorizontalCardsProperty); set => SetValue(HorizontalCardsProperty, value); }
    public ObservableCollection<CollectionCardRow> Rows { get; } = [];
    private INotifyCollectionChanged? _observed;
    private int _columns = 1;
    public CollectionGridView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateColumns();
        AttachedToVisualTree += (_, _) => ObserveItems();
        DetachedFromVisualTree += (_, _) => { if (_observed is not null) _observed.CollectionChanged -= ItemsChanged; _observed = null; };
        PropertyChanged += (_, args) => { if (args.Property == ItemsProperty) ObserveItems(); else if (args.Property == HorizontalCardsProperty) UpdateColumns(); };
    }
    private void ObserveItems()
    {
        if (_observed is not null) _observed.CollectionChanged -= ItemsChanged;
        _observed = Items as INotifyCollectionChanged;
        if (_observed is not null) _observed.CollectionChanged += ItemsChanged;
        UpdateColumns(); RebuildRows();
    }
    private void UpdateColumns()
    {
        int columns = HorizontalCards ? (Bounds.Width >= 900 ? 2 : 1) : Math.Clamp((int)(Bounds.Width / 200), 1, 12);
        if (columns == _columns) return; _columns = columns; RebuildRows();
    }
    private void AddCard(CollectionCard card)
    {
        if (Rows.Count == 0 || Rows[^1].Cards.Count == _columns) Rows.Add(new(_columns));
        Rows[^1].Cards.Add(card);
    }
    private void RebuildRows() { Rows.Clear(); foreach (var card in Items ?? []) AddCard(card); }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null && args.NewStartingIndex == (Items?.Count() ?? 0) - args.NewItems.Count)
        { foreach (CollectionCard card in args.NewItems) AddCard(card); }
        else RebuildRows();
    }
}
