# VodBox

一款跨平台的桌面影音应用：订阅点播源后即可聚合搜索、追剧、看直播，支持 **macOS / Windows / Linux**。

点播源格式与 [LunaTV](https://github.com/MoonTechLab/LunaTV) 兼容，LunaTV 能用的配置可以直接导入。VodBox 不内置任何播放源。

## 功能

- **订阅点播源**：填入订阅链接或直接粘贴配置即可使用，支持自动更新；更新订阅时保留你调整过的启用状态和排序。
- **源管理**：启用、禁用、排序、添加、删除（支持批量操作和误删恢复）；一键检测各源是否可用、延迟多少，结果按颜色区分，无效源可一键禁用。
- **聚合搜索**：同时搜索所有启用的源，结果边搜边出；同一部影片在不同源的结果会自动合并。
- **首页与点播**：热门、推荐、Top250 等榜单来自豆瓣，可自定义分类；筛选项默认收起，需要时再展开。动漫提供每日放送。
- **详情与换源**：列出同一部影片在各源的线路，换源时保持当前集数；可以自动测速，挑选清晰度高、速度快的源。
- **去广告**：自动过滤视频中插播的广告片段（可在设置中关闭）。
- **直播**：支持 M3U 与 TXT 播放列表、多线路、分组、频道筛选和节目单。
- **播放**：倍速、音量、音轨和字幕切换、全屏，以及断点续播和历史记录。
- **收藏、历史与本地文件**：常看的影片加入收藏，可从历史记录续播，本地视频文件也能直接播放。
- **外观**：深色和浅色主题。

## 快速开始

1. 从 [Releases](https://github.com/pengpercy/VodBox/releases) 下载对应系统的安装包：
   - Windows：ZIP；
   - macOS：DMG 或 ZIP；
   - Linux：deb 或 rpm。

   安装包已包含播放所需的全部组件，无需另外安装播放器。
2. 打开 VodBox，进入 **设置 → 播放源**，填入订阅地址（如 `https://example.com/config.json`），或直接粘贴配置内容。
3. 回到首页开始搜索和观看。直播默认使用配置中的直播源，也可以手动填写 m3u 地址。

**订阅源怎么写**：配置格式、示例、订阅链接的制作方法和采集接口要求，见 **[订阅源格式说明](docs/SOURCE-CONFIG.md)**。

## 说明与限制

- 只支持苹果CMS V10 采集接口，不支持 TVBox / FongMi 配置中的爬虫（jar/js）、type 4 源和解析接口。
- 只提供网页播放页的线路（`.html` 分享页、云播放器页面）会被跳过，因为这类页面无法直接播放。
- 遇到播放问题时，可以在 **设置 → 诊断** 中打开日志并查看最近的记录；日志中的 `key`、`authid`、`token` 等敏感参数会自动打码。

## 开发

需要 .NET SDK（版本由 `global.json` 固定）。首次运行前先准备播放组件：

```sh
bash build/dev-natives.sh          # 自动识别平台，也接受 osx-x64 / linux-arm64 / win-x64 等
dotnet build VodBox.slnx -c Debug
dotnet test tests/VodBox.Tests/VodBox.Tests.csproj
dotnet run --project src/VodBox.Desktop
```

发版：推送与 `VERSION` 一致的 `vX.Y.Z` 标签，CI 会构建各平台安装包并发布到 Releases。

设计稿见 [design/DESIGN.md](design/DESIGN.md)，打包相关说明见 [媒体发行说明](docs/MEDIA-BUNDLING.md)。
