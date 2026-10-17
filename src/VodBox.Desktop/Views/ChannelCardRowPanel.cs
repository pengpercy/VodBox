using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using VodBox.Core;

namespace VodBox.Desktop.Views;

/// <summary>
/// 频道卡片行的占位宿主：外层 ListBox 回收行容器时会清空内容并按模板重建行，行内卡片若跟着重建，
/// 每滚进一行就要新建整行卡片（含两张台标），滚轮快速滚动时逐行卡顿。
/// 宿主本身极轻：入树时从池里借一个已建好卡片的 <see cref="ChannelCardRowPanel"/>，离树时归还，卡片只换 DataContext。
/// </summary>
public sealed class ChannelCardRowHost : Decorator
{
    public static readonly StyledProperty<IReadOnlyList<LiveChannel>?> ChannelsProperty =
        ChannelCardRowPanel.ChannelsProperty.AddOwner<ChannelCardRowHost>();

    public static readonly StyledProperty<IDataTemplate?> CardTemplateProperty =
        ChannelCardRowPanel.CardTemplateProperty.AddOwner<ChannelCardRowHost>();

    public static readonly StyledProperty<int> ColumnsProperty =
        UniformGrid.ColumnsProperty.AddOwner<ChannelCardRowHost>();

    // 池按卡片模板区分（模板属于具体的 LiveView，卡片事件也绑在它上面）；上限覆盖几屏的行即可。
    private const int PoolLimit = 32;
    private static readonly ConditionalWeakTable<IDataTemplate, Stack<ChannelCardRowPanel>> Pools = new();

    public IReadOnlyList<LiveChannel>? Channels
    {
        get => GetValue(ChannelsProperty);
        set => SetValue(ChannelsProperty, value);
    }

    public IDataTemplate? CardTemplate
    {
        get => GetValue(CardTemplateProperty);
        set => SetValue(CardTemplateProperty, value);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Child is null && CardTemplate is { } template) Child = Rent(template);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Child is not ChannelCardRowPanel panel || panel.CardTemplate is not { } template) return;
        Child = null;
        var pool = Pools.GetOrCreateValue(template);
        if (pool.Count < PoolLimit) pool.Push(panel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Child is not ChannelCardRowPanel panel) return;
        if (change.Property == CardTemplateProperty)
        {
            Child = CardTemplate is { } template ? Rent(template) : null;
            return;
        }
        if (change.Property == ChannelsProperty) panel.Channels = Channels;
        else if (change.Property == ColumnsProperty) panel.Columns = Columns;
    }

    /// <summary>借出的面板先在树外换好频道，再入树：台标只按新频道加载一次，不会先按旧频道触发。</summary>
    private ChannelCardRowPanel Rent(IDataTemplate template)
    {
        var pool = Pools.GetOrCreateValue(template);
        var panel = pool.Count > 0 ? pool.Pop() : new ChannelCardRowPanel { Rows = 1, CardTemplate = template };
        panel.Columns = Columns;
        panel.Channels = Channels;
        return panel;
    }
}

/// <summary>频道卡片行：保留已建好的卡片，换行内容时只换各卡片的 DataContext。由 <see cref="ChannelCardRowHost"/> 池化复用。</summary>
public sealed class ChannelCardRowPanel : UniformGrid
{
    public static readonly StyledProperty<IReadOnlyList<LiveChannel>?> ChannelsProperty =
        AvaloniaProperty.Register<ChannelCardRowPanel, IReadOnlyList<LiveChannel>?>(nameof(Channels));

    public static readonly StyledProperty<IDataTemplate?> CardTemplateProperty =
        AvaloniaProperty.Register<ChannelCardRowPanel, IDataTemplate?>(nameof(CardTemplate));

    public IReadOnlyList<LiveChannel>? Channels
    {
        get => GetValue(ChannelsProperty);
        set => SetValue(ChannelsProperty, value);
    }

    public IDataTemplate? CardTemplate
    {
        get => GetValue(CardTemplateProperty);
        set => SetValue(CardTemplateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CardTemplateProperty) Children.Clear();
        if (change.Property == ChannelsProperty || change.Property == CardTemplateProperty || change.Property == ColumnsProperty) Sync();
    }

    private void Sync()
    {
        if (CardTemplate is not { } template) return;
        var channels = Channels ?? [];
        while (Children.Count < channels.Count)
        {
            // 先设 DataContext 再入树：否则新卡片会先继承行的 ChannelCardRow，编译绑定按 LiveChannel 强转而抛异常。
            var card = template.Build(null)!;
            card.DataContext = channels[Children.Count];
            Children.Add(card);
        }
        // 尾行不足一整行时多余卡片只隐藏不销毁，回收到满行时直接复用；超过列数的才真正移除。
        var capacity = Math.Max(channels.Count, Columns);
        if (Children.Count > capacity) Children.RemoveRange(capacity, Children.Count - capacity);
        for (var index = 0; index < Children.Count; index++)
        {
            var card = Children[index];
            var visible = index < channels.Count;
            card.DataContext = visible ? channels[index] : null;
            card.IsVisible = visible;
        }
    }
}
