using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VodBox.Desktop.ViewModels;

/// <summary>设置窗口左栏的一个分区项：标题 + 选中态 + 点击命令（复用 NavItemView 的 Command 契约）。</summary>
public sealed partial class SettingsSectionItem : ObservableObject
{
    private readonly SettingsWindowViewModel _owner;

    [ObservableProperty] private bool _isSelected;

    public string Title { get; }

    /// <summary>对应 <see cref="SettingsViewModel.Section"/> 的取值（迁移阶段用它驱动右栏内容）。</summary>
    public int ContentSection { get; }

    public ICommand SelectCommand { get; }

    public SettingsSectionItem(SettingsWindowViewModel owner, string title, int contentSection)
    {
        _owner = owner;
        Title = title;
        ContentSection = contentSection;
        // NavItemView 点击时固定以 null 执行 Command，所以每个分区各持一个命令，无法用单命令 + 参数。
        SelectCommand = new RelayCommand(() => _owner.Select(this));
    }
}

/// <summary>
/// 独立设置窗口的视图模型：驱动左栏分区切换与头部标题。
/// 本轮右栏为占位骨架；迁移阶段把 <see cref="Content"/> 指向现有 SettingsViewModel 即可，
/// 分区切换会自动同步 <see cref="SettingsViewModel.Section"/>。
/// </summary>
public sealed partial class SettingsWindowViewModel : ObservableObject
{
    // 声明顺序即左栏显示顺序（对齐设计规格 §5）；ContentSection 是 SettingsViewModel 里的既有分区号。
    public SettingsSectionItem Sources { get; }
    public SettingsSectionItem Playback { get; }
    public SettingsSectionItem Danmaku { get; }
    public SettingsSectionItem Subtitles { get; }
    public SettingsSectionItem Interface { get; }
    public SettingsSectionItem Data { get; }
    public SettingsSectionItem Remote { get; }
    public SettingsSectionItem Diagnostics { get; }
    public SettingsSectionItem About { get; }

    private readonly SettingsSectionItem[] _items;

    [ObservableProperty] private SettingsSectionItem? _selected;

    /// <summary>接入点：迁移阶段由 MainWindow 传入现有 SettingsViewModel；骨架阶段为 null。</summary>
    [ObservableProperty] private SettingsViewModel? _content;

    public string CurrentTitle => Selected?.Title ?? "";

    public SettingsWindowViewModel()
    {
        Sources = new(this, "源与订阅", 0);
        Playback = new(this, "播放", 1);
        Danmaku = new(this, "弹幕", 2);
        Subtitles = new(this, "字幕", 3);
        Interface = new(this, "界面", 4);
        Data = new(this, "数据", 5);
        Remote = new(this, "推送与遥控", 6);
        // 规格把「诊断」排在「关于」之前，但 SettingsViewModel 里 Diagnostics=8 / About=7，这里做映射。
        Diagnostics = new(this, "诊断", 8);
        About = new(this, "关于", 7);
        _items = [Sources, Playback, Danmaku, Subtitles, Interface, Data, Remote, Diagnostics, About];
        Select(Sources);
    }

    /// <summary>按分区号选中对应左栏项（窗口已存在时由主窗口调用，保证打开就落在请求的分区）。</summary>
    internal void SelectSection(int section)
    {
        var target = _items.FirstOrDefault(item => item.ContentSection == section);
        if (target is not null) Select(target);
    }

    /// <summary>左栏点击：切换选中态，并在已接入时同步 SettingsViewModel 的分区。</summary>
    internal void Select(SettingsSectionItem item)
    {
        foreach (var candidate in _items) candidate.IsSelected = ReferenceEquals(candidate, item);
        Selected = item;
        if (Content is { } content) content.Section = item.ContentSection;
    }

    partial void OnSelectedChanged(SettingsSectionItem? value) => OnPropertyChanged(nameof(CurrentTitle));

    partial void OnContentChanged(SettingsViewModel? value) => OnPropertyChanged(nameof(AppVersion));

    /// <summary>版本号：接入后沿用 SettingsViewModel.AppVersion（= VERSION / Directory.Build.props 的版本）；
    /// 骨架阶段直接读程序集版本，避免在打包产物里做运行期文件读取。</summary>
    public string AppVersion => Content?.AppVersion ?? (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知");
}
