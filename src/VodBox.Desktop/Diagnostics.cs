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
            try
            {
                var store = new LibraryStore(Path.Combine(temp, "test.db")); await store.GetHistoryAsync(); Console.WriteLine("SQLite: OK");
                var preferences = new PreferencesStore(Path.Combine(temp, "preferences.json"));
                await preferences.SaveAsync(new Core.AppPreferences { Volume = 35, AutoNext = false });
                if ((await preferences.LoadAsync()).Volume != 35) throw new InvalidDataException("偏好设置校验失败。");
                var configurations = new ConfigurationRepository(Path.Combine(temp, "configurations"));
                await configurations.SaveAsync(config, location);
                if ((await configurations.LoadAsync(config.Id)).Id != config.Id) throw new InvalidDataException("配置快照校验失败。");
                Console.WriteLine("AOT preferences/configuration: OK");
                await store.SaveHistoryAsync(new(config.Id, source.Id, "backup-smoke", "main", "备份诊断", "https://example.com/sample.mp4", 1234, DateTimeOffset.UtcNow));
                await store.SetFavoriteAsync(new(config.Id, source.Id, "backup-smoke", "备份诊断"), true);
                var backups = new BackupService(store, configurations, preferences);
                string backupPath = Path.Combine(temp, "backup.json");
                await backups.ExportAsync(backupPath, new() { Volume = 35, Theme = "Light" });
                var backup = await backups.ReadAsync(backupPath);
                var restored = new LibraryStore(Path.Combine(temp, "restored.db"));
                var restoredPreferences = new PreferencesStore(Path.Combine(temp, "restored-preferences.json"));
                var restoredConfigurations = new ConfigurationRepository(Path.Combine(temp, "restored-configurations"));
                await new BackupService(restored, restoredConfigurations, restoredPreferences).ImportAsync(backup);
                if ((await restored.GetHistoryAsync()).Single().PositionMs != 1234 || (await restored.GetFavoritesAsync()).Count != 1 || (await restoredPreferences.LoadAsync()).Theme != "Light" || (await restoredConfigurations.LoadAsync(config.Id)).Id != config.Id) throw new InvalidDataException("备份还原校验失败。");
                Console.WriteLine("AOT portable backup/import: OK");
                var danmaku = new Core.DanmakuDocument { Comments = [new(0, "弹幕诊断", Core.DanmakuMode.Scroll, 0x60AEFF)] };
                var comments = DanmakuLoader.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(danmaku, Core.VodBoxJson.Default.DanmakuDocument));
                var timeline = new Core.DanmakuTimeline(); timeline.Configure(comments, 640, 360, 24);
                if (timeline.Update(1000, _ => 120).Single().Comment.Text != "弹幕诊断") throw new InvalidDataException("弹幕时间轴校验失败。");
                if (DanmakuLoader.Parse("<i><d p='0,1,25,16777215'>XML</d></i>"u8.ToArray()).Count != 1) throw new InvalidDataException("弹幕 XML 校验失败。");
                Console.WriteLine("AOT danmaku parse/timeline: OK");
            }
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
