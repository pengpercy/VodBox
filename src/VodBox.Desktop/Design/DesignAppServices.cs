using VodBox.Desktop.Services;

namespace VodBox.Desktop.Design;

/// <summary>设计时 AppServices：临时目录 SQLite + 空站源注册表。libmpv 不加载（Available=false 自然降级）。</summary>
internal static class DesignAppServices
{
    public static AppServices Create() =>
        new(Path.Combine(Path.GetTempPath(), "vodbox-design-preview"));
}
