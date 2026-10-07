using VodBox.Desktop.Services;

namespace VodBox.Desktop.Design;

/// <summary>设计时 AppServices 由 <see cref="AppServices.CreateDesignTime"/> 统一构造：空站源注册表 + 空 MpvEngine（Available=false 自然降级），不触网、不加载 libmpv。</summary>
internal static class DesignAppServices
{
    public static AppServices Create() => AppServices.CreateDesignTime();
}
