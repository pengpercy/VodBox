using CommunityToolkit.Mvvm.ComponentModel;
using VodBox.Core;

namespace VodBox.Desktop;

public sealed record FilterChoice(string Value, string Name);
public partial class MainViewModel
{
    public IReadOnlyList<FilterChoice> YearChoices { get; } = new[] { new FilterChoice("", "全部年份") }
        .Concat(Enumerable.Range(0, Math.Max(1, DateTime.UtcNow.Year - 1899)).Select(offset =>
        { string year = (DateTime.UtcNow.Year - offset).ToString(System.Globalization.CultureInfo.InvariantCulture); return new FilterChoice(year, year + "年"); })).ToArray();
    public IReadOnlyList<FilterChoice> CompletionChoices { get; } = [new("", "全部状态"), new("1", "已完结"), new("0", "连载中")];
    [ObservableProperty] private FilterChoice _selectedYear = new("", "全部年份");
    [ObservableProperty] private FilterChoice _selectedCompletion = new("", "全部状态");
    [ObservableProperty] private bool _hasLibraryFilters;
    private bool _configuringFilters;
    private void ConfigureLibraryFilters(SourceDefinition? source)
    {
        _configuringFilters = true;
        try
        {
            HasLibraryFilters = source?.Runtime == ProviderRuntime.Csharp && source.Provider is "maccms-json" or "maccms-xml";
            SelectedYear = YearChoices[0]; SelectedCompletion = CompletionChoices[0];
        }
        finally { _configuringFilters = false; }
    }
    partial void OnSelectedYearChanged(FilterChoice value) { if (!_configuringFilters && HasLibraryFilters) _ = BrowseAsync(); }
    partial void OnSelectedCompletionChanged(FilterChoice value) { if (!_configuringFilters && HasLibraryFilters) _ = BrowseAsync(); }
    private IReadOnlyDictionary<string, string> CurrentBrowseFilters()
    {
        var filters = new Dictionary<string, string>();
        if (!HasLibraryFilters) return filters;
        if (SelectedYear?.Value is { Length: > 0 } year) filters["year"] = year;
        if (SelectedCompletion?.Value is { Length: > 0 } completion) filters["isend"] = completion;
        return filters;
    }
}
