using VodBox.Core;

namespace VodBox.Desktop;

/// <summary>Preview-only data; no file, native player, or worker is initialized.</summary>
public sealed class DesignMainViewModel : MainViewModel
{
    public DesignMainViewModel() : base(designMode: true)
    {
        Sources.Add(new() { Id = "preview", Name = "设计预览 · 媒体库" });
        Categories.Add(new("all", "全部")); Categories.Add(new("film", "电影"));
        Items.Add(new("1", "探索自然", Remarks: "纪录片 · 4K"));
        Items.Add(new("2", "城市与远方", Remarks: "电影 · 1080p"));
        Items.Add(new("3", "旅途中的声音", Remarks: "音乐 · 无损"));
        Description = "在这里浏览内容源，选择播放线路和集数。此数据仅用于设计器预览。";
        NowPlaying = "探索自然 · 第 1 集"; Status = "设计预览，不启动任何运行时服务。";
        Duration = 3600000; Position = 720000; TimeText = "00:12:00 / 01:00:00";
        Episodes.Add(new("e1", "第 1 集 · 山野之间")); Episodes.Add(new("e2", "第 2 集 · 海岸之上"));
        Lines.Add(new("main", "高清线路", Episodes.ToList()));
        Channels.Add(new("c1", "自然频道", "纪录片", ["https://example.com/live.m3u8"]));
    }
}
