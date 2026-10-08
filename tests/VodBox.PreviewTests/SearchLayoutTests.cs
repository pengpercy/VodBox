using Avalonia.Headless.XUnit;
using VodBox.Desktop.Design;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class SearchLayoutTests
{
    [AvaloniaFact]
    public void ResizeRechunksWithoutLosingOrReorderingResults()
    {
        var search = DesignData.Main.Search;
        var selected = search.SelectedSite;
        search.SelectedSite = null;
        var expected = search.AllResults.ToArray();
        Assert.NotEmpty(expected);
        try
        {
            foreach (var (width, columns) in new[] { (400d, 2), (800d, 4), (1200d, 6), (180d, 1) })
            {
                search.SetResultWidth(width);
                Assert.All(search.ResultRows, row => Assert.InRange(row.Items.Count, 1, columns));
                Assert.Equal(Math.Min(columns, expected.Length), search.ResultRows[0].Items.Count);
                Assert.Equal(expected, search.ResultRows.SelectMany(row => row.Items));
            }
        }
        finally { search.SelectedSite = selected; search.SetResultWidth(956); }
    }
}
