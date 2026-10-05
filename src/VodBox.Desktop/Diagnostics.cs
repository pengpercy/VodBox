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
            int biliArgument = Array.IndexOf(args, "--bilibili-smoke");
            if (biliArgument >= 0)
            {
                string mediaId = biliArgument + 1 < args.Length && !args[biliArgument + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[biliArgument + 1] : "BV1WSHL66EdZ";
                await BilibiliSmokeAsync(http, mediaId, args.Contains("--native"));
            }
            int appGetArgument = Array.IndexOf(args, "--appget-smoke");
            if (appGetArgument < 0) appGetArgument = Array.IndexOf(args, "--app99-smoke");
            if (appGetArgument >= 0)
            {
                if (appGetArgument + 1 >= args.Length || args[appGetArgument + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidDataException("Spider smoke 需要新版配置文件路径。");
                int lineArgument = Array.IndexOf(args, "--appget-line");
                if (lineArgument < 0) lineArgument = Array.IndexOf(args, "--app99-line");
                int line = 0;
                if (lineArgument >= 0 && (lineArgument + 1 >= args.Length || !int.TryParse(args[lineArgument + 1], out line) || line < 0))
                    throw new InvalidDataException("--appget-line 需要从 0 开始的线路编号。");
                int mediaArgument = Array.IndexOf(args, "--appget-media");
                if (mediaArgument < 0) mediaArgument = Array.IndexOf(args, "--app99-media");
                string? mediaId = null;
                if (mediaArgument >= 0)
                {
                    if (mediaArgument + 1 >= args.Length || args[mediaArgument + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new InvalidDataException("--appget-media 需要视频编号。");
                    mediaId = args[mediaArgument + 1];
                }
                int categoryArgument = Array.IndexOf(args, "--spider-category");
                string? categoryId = null;
                if (categoryArgument >= 0)
                {
                    if (categoryArgument + 1 >= args.Length || args[categoryArgument + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new InvalidDataException("--spider-category 需要分类编号。");
                    categoryId = args[categoryArgument + 1];
                }
                await AppGetSmokeAsync(http, args[appGetArgument + 1], args.Contains("--native"), line, mediaId, categoryId);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task AppGetSmokeAsync(HttpClient http, string location, bool decode, int lineIndex, string? mediaId, string? categoryId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var config = await new ConfigLoader(http).LoadAsync(location, deadline.Token);
        foreach (var source in config.Sources)
        {
            string label = source.Provider is "app99" or "csp_App99" ? "App99" : "AppGet";
            await using var provider = new ProviderFactory(http, "unused", "unused").Create(source);
            var categories = await provider.GetCategoriesAsync(deadline.Token);
            var category = (categoryId is null ? categories.FirstOrDefault() : categories.FirstOrDefault(x => x.Id == categoryId))
                ?? throw new InvalidDataException($"{label} 没有所选分类。");
            var page = await provider.GetItemsAsync(category.Id, null, deadline.Token);
            var item = page.Items.FirstOrDefault() ?? throw new InvalidDataException($"{label} 分类没有内容。");
            var search = await provider.SearchAsync(item.Title, deadline.Token);
            if (search.Items.Count == 0) throw new InvalidDataException($"{label} 搜索没有返回内容。");
            var detail = await provider.GetDetailAsync(mediaId ?? item.Id, deadline.Token);
            Console.WriteLine($"C# {label} catalog: OK | source={source.Id}, category={category.Id}, media={detail.Item.Id}, categories={categories.Count}, items={page.Items.Count}, search={search.Items.Count}, lines={detail.PlaybackLines.Count}, episodes={detail.PlaybackLines.Sum(x => x.Episodes.Count)}");
            if (lineIndex >= detail.PlaybackLines.Count) throw new InvalidDataException("AppGet 所选线路不存在。");
            var episode = detail.PlaybackLines[lineIndex].Episodes.FirstOrDefault() ?? throw new InvalidDataException("AppGet 视频没有可用分集。");
            var request = await provider.ResolvePlaybackAsync(detail.Item.Id, episode.Id, deadline.Token);
            Console.WriteLine($"C# {label} Spider: OK | source={source.Id}, categories={categories.Count}, items={page.Items.Count}, search={search.Items.Count}, lines={detail.PlaybackLines.Count}, episodes={detail.PlaybackLines.Sum(x => x.Episodes.Count)}");
            if (!decode) continue;
            await using var engine = new LibVlcEngine(headless: true);
            await engine.OpenAsync(request, 2, deadline.Token);
            using var media = engine.Player!.Media!;
            while (engine.Snapshot.Position.TotalSeconds < 3 || engine.Player.VideoTrack < 0 || media.Statistics.DecodedVideo == 0)
            {
                if (engine.Snapshot.State == Core.PlaybackState.Failed) throw new InvalidOperationException(engine.Snapshot.Error);
                await Task.Delay(100, deadline.Token);
            }
            Console.WriteLine($"{label} LibVLC decode: OK | source={source.Id}, position={engine.Snapshot.Position.TotalSeconds:F2}s, decodedVideo={media.Statistics.DecodedVideo}, decodedAudio={media.Statistics.DecodedAudio}");
            await engine.StopAsync(default);
        }
    }
    private static async Task BilibiliSmokeAsync(HttpClient http, string mediaId, bool decode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var config = await new ConfigLoader(http).LoadAsync(Path.Combine(Core.AppLayout.AssetsDirectory, "examples", "bilibili.json"), deadline.Token);
        await using var provider = new ProviderFactory(http, "unused", "unused").Create(config.Sources.Single());
        var categories = await provider.GetCategoriesAsync(deadline.Token);
        var popular = await provider.GetItemsAsync("popular", null, deadline.Token);
        var search = await provider.SearchAsync("Avalonia", deadline.Token);
        var playlist = await provider.GetItemsAsync("playlist-demo", null, deadline.Token);
        if (playlist.Items.Count != 1 || playlist.Items[0].Id != "BV1WSHL66EdZ" || playlist.NextCursor is not null)
            throw new InvalidDataException("哔哩哔哩片单未保持配置顺序和身份。");
        if (popular.Items.Count == 0 || search.Items.Count == 0) throw new InvalidDataException("哔哩哔哩列表或搜索没有返回内容。");
        var detail = await provider.GetDetailAsync(mediaId, deadline.Token);
        string episodeId = detail.PlaybackLines.First().Episodes.First().Id;
        var request = await provider.ResolvePlaybackAsync(mediaId, episodeId, deadline.Token);
        var comments = await new DanmakuLoader(http).LoadAsync(request.DanmakuUri!, deadline.Token);
        Console.WriteLine($"C# Bilibili Spider: OK | categories={categories.Count}, popular={popular.Items.Count}, search={search.Items.Count}, playlist={playlist.Items.Count}, episodes={detail.PlaybackLines.Sum(x => x.Episodes.Count)}, danmaku={comments.Count}");
        if (!decode) return;
        await using var engine = new LibVlcEngine(headless: true);
        await engine.OpenAsync(request, 2, deadline.Token);
        using var media = engine.Player!.Media!;
        while (engine.Snapshot.Position.TotalSeconds < 3 || engine.Player.VideoTrack < 0 || media.Statistics.DecodedVideo == 0)
        {
            if (engine.Snapshot.State == Core.PlaybackState.Failed) throw new InvalidOperationException(engine.Snapshot.Error);
            await Task.Delay(100, deadline.Token);
        }
        var statistics = media.Statistics;
        Console.WriteLine($"Bilibili LibVLC decode: OK | position={engine.Snapshot.Position.TotalSeconds:F2}s, duration={engine.Snapshot.Duration.TotalSeconds:F2}s, videoTrack={engine.Player.VideoTrack}, decodedVideo={statistics.DecodedVideo}, decodedAudio={statistics.DecodedAudio}");
        await engine.StopAsync(default);
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
