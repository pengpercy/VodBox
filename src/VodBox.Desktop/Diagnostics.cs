using VodBox.Infrastructure;
using VodBox.Playback.LibVlc;

namespace VodBox.Desktop;

internal static class Diagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Console.WriteLine($"VodBox 0.1.0 | {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
            using var http = new HttpClient();
            string location = Path.Combine(Core.AppLayout.AssetsDirectory, "examples", "vodbox.json");
            var config = await new ConfigLoader(http).LoadAsync(location);
            var source = config.Sources.First(x => x.Runtime == Core.ProviderRuntime.Csharp);
            await using var provider = new CatalogProvider(source, http);
            var categories = await provider.GetCategoriesAsync(default);
            var page = await provider.GetItemsAsync(null, null, default);
            Console.WriteLine($"Config/schema: OK | categories={categories.Count}, items={page.Items.Count}");
            string temp = Path.Combine(Path.GetTempPath(), "vodbox-diagnostics-" + Guid.NewGuid().ToString("N"));
            try { var store = new LibraryStore(Path.Combine(temp, "test.db")); await store.GetHistoryAsync(); Console.WriteLine("SQLite: OK"); }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(temp, true); }
            if (args.Contains("--native"))
            {
                await using var engine = new LibVlcEngine(headless: true, rebuildPluginCache: args.Contains("--rebuild-vlc-cache"));
                string wav = Path.Combine(Path.GetTempPath(), "vodbox-audio-" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    WriteTestWave(wav);
                    await engine.OpenAsync(new() { Uri = new Uri(wav).AbsoluteUri, Title = "诊断音频" }, 1, default);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (engine.Snapshot.Position <= TimeSpan.Zero || engine.Snapshot.Duration <= TimeSpan.Zero)
                    {
                        if (engine.Snapshot.State == Core.PlaybackState.Failed) throw new InvalidOperationException(engine.Snapshot.Error);
                        await Task.Delay(100, timeout.Token);
                    }
                    Console.WriteLine($"LibVLC decode: OK | duration={engine.Snapshot.Duration.TotalMilliseconds:F0}ms");
                    await engine.StopAsync(default);
                }
                finally { File.Delete(wav); }
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void WriteTestWave(string path)
    {
        const int sampleRate = 8000, count = sampleRate * 4;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(count * 2);
        for (int i = 0; i < count; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / sampleRate) * 1000));
    }
}
