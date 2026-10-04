namespace VodBox.Core;

/// <summary>Standard macOS bundle locations, with flat layouts for development and other OSes.</summary>
public static class AppLayout
{
    private static string MacContents => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static string AssetsDirectory => FindMac("Resources/vodbox", AppContext.BaseDirectory);
    public static string PluginHostDirectory => FindMac("Helpers/plugin-host", Path.Combine(AppContext.BaseDirectory, "plugin-host"));
    public static string VlcDirectory => Path.Combine(AssetsDirectory, "native/vlc");
    private static string FindMac(string relative, string fallback)
    {
        if (!OperatingSystem.IsMacOS()) return fallback;
        string directory = Path.Combine(MacContents, relative);
        return Directory.Exists(directory) ? directory : fallback;
    }
}
