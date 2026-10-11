using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>
/// 独立搜索结果窗口：承载现有的 <see cref="SearchView"/>。窗口的 DataContext 与主窗口一致（同一个
/// <see cref="MainViewModel"/>），因此 <see cref="SearchView"/> 无需改动即可复用；又因为同一时刻
/// 只允许一扇搜索窗口，共享的搜索状态不会在多窗口之间互串。
/// </summary>
public partial class SearchWindow : Window
{
    public SearchWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closed += (_, _) =>
        {
            // 关窗即取消在途搜索：代次 +1 并取消请求，任何已经发出的旧结果都会被判为过期而丢弃，
            // 从而不会写进之后的新请求。
            if (DataContext is MainViewModel main) main.Search.CancelPending();
        };
    }
}
