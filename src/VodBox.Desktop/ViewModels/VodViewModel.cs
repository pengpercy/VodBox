using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>点播：分类 Tab + 海报网格 + 分页。</summary>
public sealed partial class VodViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<Category> Categories { get; } = [];
    public ObservableCollection<MediaItem> Items { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private bool _loading;

    partial void OnSelectedCategoryChanged(Category? value) => _ = ReloadAsync();

    public VodViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    public async Task LoadAsync()
    {
        var source = _services.Registry.Default();
        if (source is null) return;
        Categories.Clear();
        try
        {
            foreach (var category in await source.GetCategoriesAsync())
                Categories.Add(category);
            SelectedCategory = Categories.FirstOrDefault();
        }
        catch (Exception error)
        {
            _main.StatusMessage = $"分类加载失败：{error.Message}";
        }
    }

    private async Task ReloadAsync()
    {
        var source = _services.Registry.Default();
        if (source is null || SelectedCategory is null) return;
        Loading = true;
        try
        {
            var result = await source.GetItemsAsync(SelectedCategory.Id, Page, null);
            Items.Clear();
            foreach (var item in result.Items) Items.Add(item);
            PageCount = Math.Max(1, result.PageCount);
        }
        catch (Exception error)
        {
            _main.StatusMessage = $"加载失败：{error.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    [RelayCommand]
    public async Task NextPage()
    {
        if (Page < PageCount) { Page++; await ReloadAsync(); }
    }

    [RelayCommand]
    public async Task PrevPage()
    {
        if (Page > 1) { Page--; await ReloadAsync(); }
    }

    [RelayCommand]
    private void OpenItem(MediaItem item)
    {
        var key = _services.Registry.Sources.FirstOrDefault(s => s.Runtime == SourceRuntime.MacCms)?.Key ?? "";
        _main.Detail.Open(key, item);
    }
}
