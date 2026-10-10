# VodBox 设置项清单（迁移到独立设置窗口用）

> 作者：Researcher (Alma) · 类型：**只读盘点**，用于保证「内嵌设置页 → 独立设置窗口」迁移**零丢失**
> 盘点对象：
> - `src/VodBox.Desktop/Views/SettingsView.axaml`（内嵌长滚动页，当前线上形态）
> - `src/VodBox.Desktop/Views/SettingsView.axaml.cs`（4 个 `Click=` 事件处理器）
> - `src/VodBox.Desktop/ViewModels/SettingsViewModel.cs`（733 行，绑定面）
> - 交叉引用：`ViewModels/PlayerViewModel.cs`（`PlayerSettings.*` 的真身）、`ViewModels/MainViewModel.cs`、`Views/SettingsWindow.axaml(.cs)`、`ViewModels/SettingsWindowViewModel.cs`、`Views/Converters.cs`、`App.axaml.cs`
>
> 配套规格：`docs/SETTINGS-WINDOW-SPEC.md`（Designer，v1）。**注意**：`SettingsWindow.axaml` 目前是**占位骨架**，里面的文案/绑定大多是示意值（例如「记忆播放进度」「默认字幕语言」「弹幕来源」这些**在 VM 里根本不存在**），迁移要以本清单为准，不要把骨架当已完成内容。

**量级**：9 个分区、**61 个可交互设置项**（53 个分区内 + 8 个根级弹窗内；不含左栏 9 个分区导航按钮）、**10 段纯说明性文字**、**5 个数据列表类控件**、**6 个代码后置 `Click=` 事件处理器**（另有 1 个纯代码构建的运行时确认弹窗）。

---

## 0. 导航壳（不属于任何分区，但迁移时必须一并搬运）

### 0.1 左栏分区导航按钮（9 个）

父容器：`Border`（`Grid.Column=0`，`Padding="12,18"`，`DataContext="{Binding Settings}"`），内含 `StackPanel Spacing="4"`。
每个按钮统一形态：`Button Click="OnSelectSection"` + `HorizontalContentAlignment="Left"` + `Padding="13,9"`，内容为 `StackPanel Orientation="Horizontal" Spacing="8"`（`PathIcon` 18×18 + `TextBlock`），背景由转换器驱动：

```
Background="{Binding Section, Converter={x:Static views:SectionConverters.NavBackground}, ConverterParameter=N}"
```

| Tag | 文案 | 图标 | 语义 |
|---|---|---|---|
| 0 | 源与订阅 | `Icon.Broadcast` | 订阅源 + 站点列表 |
| 1 | 播放 | `Icon.Play` | 播放行为 |
| 2 | 弹幕 | `Icon.Information` ⚠️ 错误图标（规格要求改 `Icon.Danmaku`） | 弹幕 |
| 3 | 字幕 | `Icon.Information` ⚠️ 错误图标（规格要求改 `Icon.Subtitles`） | 字幕 |
| 4 | 界面 | `Icon.ViewGrid` | 主题/密度/壁纸 |
| 5 | 数据 | `Icon.ContentSave` | 备份/缓存 |
| 6 | 推送与遥控 | `Icon.Cellphone` | 局域网/推送 |
| 7 | 关于 | `Icon.Information` | 版本/更新 |
| 8 | 诊断 | `Icon.History` | 日志 |

**`Click="OnSelectSection"` 做了什么**（`SettingsView.axaml.cs` 末尾）：
```csharp
if (sender is Button { Tag: string value } && int.TryParse(value, out var section)) VM.Section = section;
```
即：把按钮 `Tag` 解析成 int 写进 `SettingsViewModel.Section`。**纯 XAML 事件，无 Command。** 新窗口已用 `SettingsSectionItem.SelectCommand` + `ContentSection` 替代（`SettingsWindowViewModel`），但两套映射的**索引顺序不一致**，见 §7 风险 R3。

### 0.2 右栏容器与「跨分区常驻」元素

- 外层：`ScrollViewer Grid.Column="1" Padding="32,24,32,28"` → `StackPanel Spacing="18" DataContext="{Binding Settings}"`。
- 9 个分区各自是 `StackPanel x:Name="XxxSection" Spacing="12" IsVisible="{Binding IsXxxSection}"`，**全部同时存在于可视树里，靠 `IsVisible` 切换**（不是切模板）。
- ⚠️ **`Message` 文本块是 9 个分区的公共兄弟节点**，写在 `SourcesSection` 之后、`PlaybackSection` 之前：
  ```xml
  <TextBlock Text="{Binding Message}" FontSize="12.5" Foreground="#4CC2FF" TextWrapping="Wrap" />
  ```
  它在 9 个分区里**全部常驻显示**（承载所有命令的成功/失败提示）。迁移时不能只塞进「源与订阅」卡片。
- ⚠️ **点播配置弹窗在最外层**，是根 `Panel` 的第二个子节点（与 `Grid` 同级），不是任何分区内部：
  ```xml
  <Panel IsVisible="{Binding Settings.ConfigDialogOpen}"> … </Panel>
  ```
  注意这里绑定路径是 **`Settings.ConfigDialogOpen`**（因为根 `Panel` 的 DataContext 仍是 `MainViewModel`，只有右栏 `StackPanel` 才切到 `Settings`）。新窗口把 `SettingsViewModel` 当 `Content` 挂载时，这条路径写法会变。

---

## 1. 分区 0——源与订阅（SourcesSection / `IsSourcesSection`）

### 1.1 标题
- `TextBlock Text="源与订阅" FontSize="20" FontWeight="SemiBold"`（分区大标题）
- 中段另有二级标题 `TextBlock Text="站点列表" FontSize="20" FontWeight="SemiBold"`

### 1.2 点播配置卡片（`Border` + 内嵌 `StackPanel Margin="20,4,20,4"`）

| # | 界面文案 | 控件 | 绑定 |
|---|---|---|---|
| 1 | 「点播配置（TVBox JSON）」 | `TextBlock FontSize=13.5` | 静态标题 |
| 2 | 配置 URL 值（`TextTrimming=CharacterEllipsis` `MaxWidth=420` `Foreground=#4CC2FF`） | `TextBlock`（**只读展示**） | `{Binding VodConfigUrl}` |
| 3 | 「更改配置…」 | `Button`（`#0078D4` 主按钮） | `Command="{Binding OpenConfigDialogCommand}"` |
| 4 | 「直播配置（m3u / txt）」+ 副标题「IPTV 直播源地址」 | `TextBlock` ×2 | 静态 |
| 5 | Placeholder `https://…/live.m3u` | `TextBox Width=280 Height=32` | `{Binding LiveConfigUrl}` |
| 6 | 「加载」 | `Button`（主按钮） | `Command="{Binding ApplyLiveConfigCommand}"` |

行间有 `<Separator Background="#17FFFFFF" Height="1" />`（⚠️ 硬编码遗留色，规格要求改 `StrokeBrush`）。

### 1.3 站点列表（数据列表类控件 ⭐）

- 容器：`Border`（`MinHeight=120`）→ `ItemsControl ItemsSource="{Binding Sites}" Margin="20,12"`
- 数据源：`SettingsViewModel.Sites`（`ObservableCollection<SourceInfo>`，由 `RefreshSites()` 从 `_services.Registry.Sources` 填充）
- 行模板结构（`Grid ColumnDefinitions="*,Auto" Margin="0,6"`）：
  - 左：`StackPanel Orientation=Horizontal Spacing=10`
    - `TextBlock Text="{Binding Name}" FontSize=13`
    - `Border`（badge，`Background=#264CC2FF` 圆角 5）内 `TextBlock Text="{Binding Runtime}" FontSize=10.5 Foreground=#4CC2FF`
  - 右：`StackPanel Orientation=Horizontal Spacing=6`，**5 个按钮**（全部通过 `$parent` 上升拿到 Settings 的命令，`CommandParameter="{Binding}"` 传行数据）：

| 行内按钮 | Content | 绑定（精确路径） |
|---|---|---|
| 显示/隐藏 | `显示/隐藏` | `Command="{Binding $parent[views:SettingsView].((vm:MainViewModel)DataContext).Settings.ToggleSiteHiddenCommand}"` `CommandParameter="{Binding}"` |
| 搜索开关 | `搜索开关` | `…Settings.ToggleSiteSearchCommand` |
| 换源开关 | `换源开关` | `…Settings.ToggleSiteChangeCommand` |
| 星标 | `星标` | `…Settings.ToggleSiteStarCommand` |
| 上移 | `上移` | `…Settings.MoveSiteUpCommand` |
  - ⚠️ 只有「上移」，**没有「下移」**（`MoveSiteUpCommand` 是唯一排序操作）。
  - ⚠️ 行模板里还有一个 **`IsVisible="False"` 的死节点**：`TextBlock Text="{Binding Api}"`（隐藏但仍在可视树，`MaxWidth=380`）。
- `SourceInfo` 字段（`VodBox.Core/Models.cs:75`）：`Key, Name, Runtime, Api, Ext, PlayUrl, Type, Searchable, Changeable, Categories`。行模板只用了 `Name` / `Runtime` / `Api`(隐藏)。

### 1.4 EPG 订阅行（无卡片包裹）

| 界面文案 | 控件 | 绑定 |
|---|---|---|
| Placeholder `XMLTV订阅地址` | `TextBox Width=300` | `{Binding EpgSubscriptionUrl}` |
| 「刷新节目订阅」 | `Button` | `Command="{Binding RefreshEpgSubscriptionCommand}"` |

### 1.5 直播订阅历史（数据列表类控件）

| 界面文案 | 控件 | 绑定 |
|---|---|---|
| 「查看直播订阅历史」 | `Button` | `Command="{Binding LoadLiveConfigHistoryCommand}"`（**必须先点它才填充列表**） |
| 列表项 `Name`（`Width=260` 省略号） | `TextBlock` | `{Binding Name}` |
| 「当前」标记 | `TextBlock IsVisible="{Binding Current}"` | `{Binding Current}` |
| 「切换」 | `Button` | `Command="{Binding $parent[views:SettingsView].((vm:MainViewModel)DataContext).Settings.SwitchLiveConfigCommand}"` `CommandParameter="{Binding}"` |
| 「删除」 | `Button` | `…Settings.DeleteLiveConfigCommand` `CommandParameter="{Binding}"` |

- 数据源：`SettingsViewModel.LiveConfigHistory`（`ObservableCollection<ConfigHistoryEntry>`），`DataTemplate x:DataType="vm:ConfigHistoryEntry"`。
- ⚠️ 列表**没有** `IsVisible` 门控，空态就是空边框。
- ⚠️ 与 §1.7 的 `ConfigHistory` 是**两个不同的集合 + 两套不同命令**（`Live` 与 `Vod` 各一份），迁移时不要合并。

### 1.6 「更改点播配置」弹窗（`ConfigDialogOpen`，根节点级）

外框：`Panel IsVisible="{Binding Settings.ConfigDialogOpen}"` → 遮罩 `Rectangle Fill="#88000000"` → `Border Width=560`（`ClipToBounds`）→ `Grid RowDefinitions="Auto,*,Auto"`。

**弹窗头**（`Grid ColumnDefinitions="*,Auto" Margin="20,16,16,10"`）

| 界面文案 | 控件 | 绑定 |
|---|---|---|
| `Icon.Broadcast` 24×24 + 「更改点播配置」 | `PathIcon` + `TextBlock` | 静态 |
| 关闭 X（`ToolTip.Tip="关闭"`，`Icon.Close` 18×18，`Width=28 Height=28`） | `Button` | `Command="{Binding Settings.CancelConfigDialogCommand}"` |

**弹窗体**

| # | 界面文案 | 控件 | 绑定 |
|---|---|---|---|
| 7 | 「输入 TVBox 配置地址，或从剪贴板读取」 | `TextBlock` 11.5 弱化色 | 说明性 |
| 8 | Placeholder `https://…/tvbox.json` | `TextBox Height=34` | `{Binding Settings.DialogUrl}` |
| 9 | 「粘贴」（`Icon.Clipboard` 18 + 文字） | `Button`（`CardHoverBrush` 次按钮） | `Command="{Binding Settings.PasteFromClipboardCommand}"` |
| 10 | 「历史配置」 | `TextBlock FontSize=12` | 静态分组标题 |
| 11 | 历史列表（`Border MaxHeight=170` + `ScrollViewer` + `ItemsControl ItemsSource="{Binding Settings.ConfigHistory}"`，`DataTemplate` 无 `x:DataType`） | 行：`TextBlock {Binding Name}`（`MaxWidth=300` 省略号）+ `Border`「使用中」徽章（`IsVisible="{Binding Current}"`，`#4CC2FF` 描边）+ 2 按钮 | 见下 |
| 12 | 「切换」 | `Button` | `{Binding $parent[views:SettingsView].((vm:MainViewModel)DataContext).Settings.SwitchConfigCommand}` `CommandParameter="{Binding}"` |
| 13 | 「删除」（`Foreground="#FF6B6B"` 危险色） | `Button` | `…Settings.DeleteConfigCommand` `CommandParameter="{Binding}"` |

**弹窗脚**（`Border BorderThickness="0,1,0,0" Padding="20,12"`）

| # | 界面文案 | 控件 | 绑定 |
|---|---|---|---|
| 14 | 「手机扫码推送」（`Icon.Cellphone` 18 + 文字） | `Button`（次按钮） | `Command="{Binding Settings.OpenRemoteSettingsCommand}"` |
| 15 | 「取消」 | `Button`（次按钮） | `Command="{Binding Settings.CancelConfigDialogCommand}"` |
| 16 | 「确定加载」 | `Button`（主按钮 `#0078D4`，`FontWeight=SemiBold`） | `Command="{Binding Settings.ConfirmConfigDialogCommand}"` |

⚠️ 弹窗**没有二维码 Image**，二维码在 §6「推送与遥控」分区里。

---

## 2. 分区 1——播放（PlaybackSection / `IsPlaybackSection`）

标题：`TextBlock Text="播放" FontSize="20"`（**注意：无 `FontWeight=SemiBold`，与分区 0 的标题不一致**）。

绑定目标全部是 `PlayerSettings`（= `SettingsViewModel.PlayerSettings` → `MainViewModel.Player`，类型 `PlayerViewModel`）。

| # | 界面文案 | 控件 | 绑定属性 | 范围/参数 |
|---|---|---|---|---|
| 1 | 「自动下一集」 | `CheckBox` | `{Binding PlayerSettings.AutoNext}` | bool，默认 true |
| 2 | 「不记录播放历史」 | `CheckBox` | `{Binding PlayerSettings.Incognito}` | bool，默认 false |
| 3 | 「音量」 | `TextBlock`（标签） + `Slider` | `{Binding PlayerSettings.Volume}` | `Minimum=0 Maximum=100`，无 `Increment`、**无实时数值读数** |
| 4 | 「倍速」 | `TextBlock` + `NumericUpDown` | `{Binding PlayerSettings.Rate}` | `Minimum=0.25 Maximum=4 Increment=0.25` |
| 5 | 「跳片头（秒）」 | `TextBlock` + `NumericUpDown` | `{Binding PlayerSettings.OpeningSkipSeconds}` | `Minimum=0 Maximum=300`，无 `Increment` |
| 6 | 「跳片尾（秒）」 | `TextBlock` + `NumericUpDown` | `{Binding PlayerSettings.EndingSkipSeconds}` | `Minimum=0 Maximum=300`，无 `Increment` |

**该分区无任何说明性文字。**

副作用（迁移时需知道：改这些会立刻影响正在播放的播放器）：`Volume`/`Rate` 会立刻下发引擎并弹 toast；`OpeningSkipSeconds`/`EndingSkipSeconds` 在**下次会话**生效；`AutoNext` 触发自动续集；`Incognito` 停止写历史。

---

## 3. 分区 2——弹幕（DanmakuSection / `IsDanmakuSection`）

标题：`TextBlock Text="弹幕" FontSize="20"`。绑定目标仍是 `PlayerSettings`。

| # | 界面文案 | 控件 | 绑定属性 | 范围 |
|---|---|---|---|---|
| 1 | 「显示弹幕」 | `CheckBox` | `{Binding PlayerSettings.DanmakuEnabled}` | bool，默认 **false** |
| 2 | 「透明度」 | `TextBlock` + `Slider` | `{Binding PlayerSettings.DanmakuOpacity}` | `Minimum=0.1 Maximum=1`，**无数值读数** |
| 3 | 「屏幕弹幕上限」 | `TextBlock` + `NumericUpDown` | `{Binding PlayerSettings.DanmakuLimit}` | `Minimum=5 Maximum=60` |

**说明性文字（必须搬运，原文照抄）**：
> 支持XML、JSON与gzip；弹幕随播放时间同步，缺少弹幕地址的媒体不会生成假数据。

⚠️ **不存在**「弹幕来源 / 自动匹配 / 仅文件名 / 不加载」这类设置——那是 `SettingsWindow.axaml` 骨架里的臆造项（VM 无对应属性）。

---

## 4. 分区 3——字幕（SubtitleSection / `IsSubtitleSection`）

标题：`TextBlock Text="字幕" FontSize="20"`。

| # | 界面文案 | 控件 | 绑定 | 备注 |
|---|---|---|---|---|
| 1 | 「同步偏移（秒，正值延后）」 | `TextBlock` + `NumericUpDown` | `{Binding PlayerSettings.SubtitleDelay}` | `Minimum=-120 Maximum=120 Increment=0.5` |
| 2 | 「字号」 | `TextBlock` + `NumericUpDown` | `{Binding PlayerSettings.SubtitleFontSize}` | `Minimum=12 Maximum=96` |
| 3 | **说明**：「ASSRT服务凭据仅保留在本次会话内存，不写入偏好或备份。」（`TextWrapping=Wrap`） | `TextBlock` | 静态 | ⚠️ 安全声明，迁移必须保留 |
| 4 | Placeholder `ASSRT服务凭据` | `TextBox PasswordChar="●"` | `{Binding SubtitleCredential}` | 会话内存，不持久化 |
| 5 | Placeholder `字幕搜索片名（留空使用当前播放标题）` | `TextBox` | `{Binding SubtitleQuery}` |  |
| 6 | 「搜索在线字幕」 | `Button` | `Command="{Binding SearchOnlineSubtitlesCommand}"` |  |
| 7 | 在线字幕结果（数据列表 ⭐） | `ItemsControl ItemsSource="{Binding OnlineSubtitles}"`，`DataTemplate x:DataType="core:OnlineSubtitle"`，行是 `Button Content="{Binding Title}"` | `Command="{Binding $parent[views:SettingsView].((vm:MainViewModel)DataContext).Settings.SelectOnlineSubtitleCommand}"` `CommandParameter="{Binding}"` | 数据源 `ObservableCollection<OnlineSubtitle>`（`record OnlineSubtitle(string Id, string Title)`） |
| 8 | 字幕文件结果（数据列表 ⭐） | `ItemsControl ItemsSource="{Binding SubtitleFiles}"`，`DataTemplate x:DataType="core:OnlineSubtitleFile"`，行是 `Button Content="{Binding Name}"` | `…Settings.LoadOnlineSubtitleCommand` `CommandParameter="{Binding}"` | `record OnlineSubtitleFile(string Name, string Url)` |
| 9 | **说明**：「外挂字幕也可从播放器字幕菜单加载；在线服务和真实下载仍需有效凭据验收。」（`TextWrapping=Wrap`） | `TextBlock` | 静态 | 必须搬运 |

⚠️ `SelectOnlineSubtitleCommand` 与 `LoadOnlineSubtitleCommand` 是链式的：点字幕 → 填 `SubtitleFiles` → 点文件 → 下载并注入当前播放器（要求 `Player.Visible == true`）。
⚠️ `SubtitleSearching` 属性 VM 里有，但 XAML **未绑定**（无 loading 指示）。
⚠️ **不存在**「默认字幕语言（简体中文/繁體中文/English）」「自动加载字幕」——骨架臆造项。

---

## 5. 分区 4——界面（InterfaceSection / `IsInterfaceSection`）

标题：`TextBlock Text="界面主题" FontSize="20"`（**文案与左栏「界面」不一致**）。

| # | 界面文案 | 控件 | 绑定 | 选项/参数 |
|---|---|---|---|---|
| 1 | 「界面主题」标签 + ComboBox | `ComboBox Width=180` `SelectedIndex` | `{Binding ThemeIndex}` | item 0「跟随系统」/ 1「浅色」/ 2「深色」 |
| 2 | 「海报密度」标签 + ComboBox | `ComboBox Width=180` `SelectedIndex` | `{Binding PosterDensity}` | item 0「宽松」/ 1「标准」/ 2「紧凑」⚠️ **顺序与 `ui.poster-density` 的 0/1/2 语义相反**，见风险 R6 |
| 3 | 「选择本地壁纸」 | `Button Click="OnPickWallpaper"` | **事件**（非 Command） | 见下 |
| 4 | 「清除壁纸」 | `Button` | `Command="{Binding ClearWallpaperCommand}"` |  |

**`Click="OnPickWallpaper"`（`SettingsView.axaml.cs`）做了什么**：
```csharp
var storage = TopLevel.GetTopLevel(this)?.StorageProvider;   // 依赖可视树 → 窗口
if (storage?.CanOpen != true) return;
var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions{
    Title = "选择本地壁纸", AllowMultiple = false,
    FileTypeFilter = [ new FilePickerFileType("图片"){ Patterns=["*.png","*.jpg","*.jpeg","*.webp"] } ]});
var path = files[0].TryGetLocalPath();
VM.SetWallpaper(path);       // → 校验 16 MiB / 扩展名 / 可解码，写 prefs "ui.wallpaper"，设 MainViewModel.WallpaperPath
catch → VM.Message = $"壁纸设置失败：{error.Message}";
```
⚠️ `TopLevel.GetTopLevel(this)` 是**迁移高危点**：独立窗口里 `this` 变成哪个控件决定成败。

**说明性文字（必须搬运，原文照抄）**：
> 基础页面与导航跟随主题；视频控制层保留深色以保证画面对比度。

---

## 6. 分区 5——数据（DataSection / `IsDataSection`）

标题：`TextBlock Text="数据" FontSize="20"`。

| # | 界面文案 | 控件 | 绑定 |
|---|---|---|---|
| 1 | 「导出备份」 | `Button Click="OnExportBackup"` | **事件**（非 Command） |
| 2 | 「合并恢复备份」 | `Button Click="OnRestoreBackup"` | **事件**（非 Command） |
| 3 | 「清理下载字幕缓存」 | `Button` | `Command="{Binding ClearSubtitleCacheCommand}"` |
| 4 | 「清空媒体缓存」 | `Button` | `Command="{Binding ClearMediaCacheCommand}"` |
| 5 | 「清理上传收件箱」 | `Button` | `Command="{Binding ClearMediaInboxCommand}"` |
| 6 | 「清空观看历史与已看记录」 | `Button` | `Command="{Binding ClearHistoryCommand}"` |

**`Click="OnExportBackup"`**：`SaveFilePickerAsync(Title="导出备份", SuggestedFileName="vodbox-backup.json")` → `await VM.ExportBackupAsync()` → `output.SetLength(0)` + `StreamWriter.WriteAsync(json)` → 成功 `VM.Message = "备份已导出；文件包含订阅与偏好，请妥善保管"`；失败 `VM.Message = $"导出失败：{error.Message}"`。**依赖 `TopLevel.GetTopLevel(this)`。**

**`Click="OnRestoreBackup"`**：`OpenFilePickerAsync(Title="合并备份（不会删除现有记录）", 过滤 ["*.json"])` → 逐块读入 `StringBuilder`，**超过 16 MiB 抛「备份文件过大。」** → `await VM.RestoreBackupAsync(text)`；失败 `VM.Message = $"恢复失败：{error.Message}"`。**依赖 `TopLevel.GetTopLevel(this)`。**

**该分区无任何说明性文字**（⚠️ 但 6 个按钮里有 4 个是破坏性操作且**没有二次确认弹窗**，只有 `Message` 文字反馈）。

---

## 7. 分区 6——推送与遥控（RemoteSection / `IsRemoteSection`）

标题：`TextBlock Text="推送与遥控" FontSize="20"`。

| # | 界面文案 | 控件 | 绑定 | 备注 |
|---|---|---|---|---|
| 1 | 「允许可信局域网（需要配对）」 | `CheckBox` | `{Binding LanControl}` | 控制 `StartLocalControl` 的 `allowLan` |
| 2 | 配对信息（地址 + 配对码，或「仅本机模式」） | `TextBlock TextWrapping=Wrap` | `{Binding PairingInformation}` | **展示** |
| 3 | 配对二维码 | `Image Source=… Width=220 Height=220 HorizontalAlignment=Left` | `{Binding PairingQrImage}` | ⚠️ `Avalonia.Media.Imaging.Bitmap`，**需 `Dispose()`** |
| 4 | 「推送媒体使用本机代理」 | `CheckBox` | `{Binding ProxyPushedMedia}` | 立即写 `push.use-proxy` |
| 5 | 「启动本机遥控」 | `Button` | `Command="{Binding StartLocalControlCommand}"` |  |
| 6 | 「关闭本机遥控」 | `Button` | `Command="{Binding StopLocalControlCommand}"` |  |
| 7 | 「打开上传媒体收件箱」 | `Button` | `Command="{Binding OpenMediaInboxCommand}"` | ⚠️ 会导航主窗口到 Files 页 |

**说明性文字 ×2（必须搬运，原文照抄）**：
> 上传只接受媒体类型，单文件64 MiB；收件箱最多100项、总计256 MiB。

> 默认只监听本机；局域网模式需先停服务再重启，配对码10分钟有效。请仅在可信网络使用。

⚠️ `LanControl` 的改动**不会自动重启服务**——必须「关闭本机遥控」再「启动本机遥控」才生效（文案里已声明，交互上没有引导）。

---

## 8. 分区 7——关于（AboutSection / `IsAboutSection`）

标题：`TextBlock Text="关于" FontSize="20"`。

| # | 界面文案 | 控件 | 绑定 |
|---|---|---|---|
| 1 | 版本号（`StringFormat='VodBox {0}'`，`FontSize=11.5`） | `TextBlock` | `{Binding AppVersion}` |
| 2 | 「启动时自动检查更新」 | `CheckBox` | `{Binding AutoCheckUpdates}` |
| 3 | 更新状态文本（`TextWrapping=Wrap`，初值「尚未检查更新」） | `TextBlock` | `{Binding UpdateMessage}` |
| 4 | 「检查更新」 | `Button Click="OnCheckUpdate"` | **事件** |
| 5 | 「获取更新」 | `Button Click="OnInstallUpdate"` | **事件** |
| 6 | 「取消下载」 | `Button Click="OnCancelUpdate"` | **事件** |

三个按钮在 `StackPanel Orientation="Horizontal" Spacing="8"` 内。

**`Click="OnCheckUpdate"`**：`await VM.CheckUpdateAsync()`（`UpdateBusy=true` 防重入 → `AppUpdater.CheckAsync` → 写 `UpdateMessage`）。
**`Click="OnCancelUpdate"`**：`VM.CancelUpdate()` → `_updateCancellation?.Cancel()`。
**`Click="OnInstallUpdate"`**（**含命令式建窗，迁移最麻烦的一处**）：
```csharp
if (VM.UpdateBusy || VM.AvailableUpdate is not { } offer) { VM.UpdateMessage = "请先检查更新"; return; }
if (OperatingSystem.IsLinux()) { await VM.InstallUpdateAsync(); return; }   // Linux 直接开浏览器
var parent = TopLevel.GetTopLevel(this) as Window;                          // ← 必须是 Window
if (parent is null) return;
// 用代码 new 一个确认窗口（Width=440 Height=180 CenterOwner），内容：
//   "将下载并校验 VodBox {offer.Version}，关闭应用后由独立更新器替换安装目录并重启。是否继续？"
//   按钮「确认下载并安装」（→ confirm.Close(true)）/「取消」（→ confirm.Close(false)）
if (await confirm.ShowDialog<bool>(parent)) await VM.InstallUpdateAsync();
```
⚠️ **新增了一个运行时弹窗**（标题「确认更新 VodBox」、按钮「确认下载并安装」/「取消」），它**不在任何 axaml 里**，纯代码构建。迁移时如果只搬 axaml 就会丢掉它。`TopLevel.GetTopLevel(this) as Window` 也要求调用点仍在窗口内。

**说明性文字（必须搬运，原文照抄，`FontSize=11.5`）**：
> Linux 系统包会打开下载地址或发行说明，下载后请通过系统包管理器安装；Windows/macOS 确认后会在关闭应用时替换并重启。

---

## 9. 分区 8——诊断（DiagnosticsSection / `IsDiagnosticsSection`）

标题：`TextBlock Text="诊断日志" FontSize="20"`（**文案与左栏「诊断」不一致**）。

| # | 界面文案 | 控件 | 绑定 |
|---|---|---|---|
| 1 | `Icon`? 无。说明：「播放出问题时：打开日志 → 复现一次 → 回到这里点"刷新" → 复制内容发我。日志只记录事件与地址，查询串里的密钥已脱敏。」（`FontSize=11.5 TextWrapping=Wrap`） | `TextBlock` | 静态 |
| 2 | 「启用日志记录」 | `CheckBox` | `{Binding LogEnabled}`（**手写 property，非 `[ObservableProperty]`**） |
| 3 | `StringFormat='目录：{0}'` | `TextBlock` | `{Binding LogDirectory}` |
| 4 | `StringFormat='当前文件：{0}'` | `TextBlock` | `{Binding LogFile}` |
| 5 | 「刷新」 | `Button` | `Command="{Binding RefreshLogsCommand}"` |
| 6 | 「打开日志目录」 | `Button` | `Command="{Binding OpenLogFolderCommand}"` |
| 7 | 「清空日志」 | `Button` | `Command="{Binding ClearLogsCommand}"` |
| 8 | 「显示路径」 | `Button` | `Command="{Binding CopyLogPathCommand}"`（⚠️ **名字叫「复制」，实现只是把路径写进 `DiagnosticsMessage`，不碰剪贴板**） |
| 9 | 诊断状态文本（`FontSize=11.5` 弱化色） | `TextBlock` | `{Binding DiagnosticsMessage}` |
| 10 | 日志尾部预览（`Border Background="#0AFFFFFF"` `Padding=10` 包裹） | `TextBox IsReadOnly=True AcceptsReturn=True TextWrapping=NoWrap FontFamily={DynamicResource FontMono} FontSize=11 MinHeight=240 MaxHeight=360 Background=Transparent BorderThickness=0` | `{Binding LogTail}` |

⚠️ 第 5～8 号按钮在 `StackPanel Orientation="Horizontal" Spacing="8"` 内。
⚠️ 日志区空态文案在 VM 里：`"（暂无日志。执行一次播放后刷新即可看到记录。）"`；`LogDirectory` 空态 `"（尚未初始化）"`；`LogFile` 空态 `"（本次运行还没有日志文件）"`。
⚠️ `RefreshLog()` 会在 `LogEnabled` setter 里被调用——**切到诊断区不会自动刷新**（旧代码靠 `RefreshSites`／手动点）。

---

## 10. `SettingsViewModel` 公开面（用于互相印证）

文件：`src/VodBox.Desktop/ViewModels/SettingsViewModel.cs`。

### 10.1 `[ObservableProperty]`（21 个，含生成的属性名）

| 字段 | 生成属性 | 备注 / 是否被 XAML 用到 |
|---|---|---|
| `_vodConfigUrl` | `VodConfigUrl` | ✅ §1.2（只读展示） |
| `_liveConfigUrl` | `LiveConfigUrl` | ✅ §1.2 |
| `_epgSubscriptionUrl` | `EpgSubscriptionUrl` | ✅ §1.4 |
| `_lanControl` | `LanControl` | ✅ §7 |
| `_pairingInformation` | `PairingInformation` | ✅ §7 |
| `_pairingQrImage` | `PairingQrImage` | ✅ §7 |
| `_proxyPushedMedia` | `ProxyPushedMedia` | ✅ §7 |
| `_applying` | **`Applying`** | ❌ **XAML 未用**（仅代码内 set，无 loading UI） |
| `_message` | `Message` | ✅ §0.2（跨分区常驻） |
| `_updateMessage` | `UpdateMessage` | ✅ §8 |
| `_autoCheckUpdates` | `AutoCheckUpdates` | ✅ §8 |
| `_subtitleCredential` | `SubtitleCredential` | ✅ §4 |
| `_subtitleQuery` | `SubtitleQuery` | ✅ §4 |
| `_subtitleSearching` | **`SubtitleSearching`** | ❌ **XAML 未用**（仅内部代次门控） |
| `_posterDensity` | `PosterDensity` | ✅ §5 |
| `_themeIndex` | `ThemeIndex` | ✅ §5 |
| `_section` | `Section` | ✅（经两个转换器 + 新窗口 `ContentSection`） |
| `_logTail` | `LogTail` | ✅ §9 |
| `_diagnosticsMessage` | `DiagnosticsMessage` | ✅ §9 |
| `_configDialogOpen` | `ConfigDialogOpen` | ✅ §1.6（弹窗可见性） |
| `_dialogUrl` | `DialogUrl` | ✅ §1.6 |

### 10.2 手写属性（非 `[ObservableProperty]`）

| 属性 | 类型 | 是否被 XAML 用到 |
|---|---|---|
| `LogEnabled` | `bool`（`SetProperty` + `VodBoxLog.SetEnabled` + 写 `diagnostics.log` + `RefreshLog()`） | ✅ §9 |
| `LogDirectory` | `string` | ✅ §9 |
| `LogFile` | `string` | ✅ §9 |
| **`LogPath`** | `string` | ❌ **XAML 未用**（只被 `CopyLogPath` 内部读） |
| **`AppVersion`** | `string` | ✅ §8（新窗口左栏底部卡片也用了） |
| `IsSourcesSection` … `IsDiagnosticsSection` | `bool` ×9 | ✅ 分区可见性 |
| **`PlayerSettings`** | `PlayerViewModel` | ✅ §2/§3/§4 |
| `Sites` | `ObservableCollection<SourceInfo>` | ✅ §1.3 |
| `LiveConfigHistory` | `ObservableCollection<ConfigHistoryEntry>` | ✅ §1.5 |
| `ConfigHistory` | `ObservableCollection<ConfigHistoryEntry>` | ✅ §1.6 |
| `OnlineSubtitles` | `ObservableCollection<OnlineSubtitle>` | ✅ §4 |
| `SubtitleFiles` | `ObservableCollection<OnlineSubtitleFile>` | ✅ §4 |
| **`AvailableUpdate`** | `UpdateOffer?`（`private set`） | ❌ **XAML 未用**（仅 `OnInstallUpdate` 代码后置读） |
| **`UpdateBusy`** | `bool`（`private set`） | ❌ **XAML 未用**（仅 `OnInstallUpdate` 代码后置读） |

### 10.3 `[RelayCommand]`（33 个，含生成的 Command 名）

| 方法 | 生成命令 | 可见性 | 是否被 XAML 用到 |
|---|---|---|---|
| `ToggleSiteHidden(SourceInfo)` | `ToggleSiteHiddenCommand` | private | ✅ |
| `ToggleSiteSearch(SourceInfo)` | `ToggleSiteSearchCommand` | private | ✅ |
| `ToggleSiteChange(SourceInfo)` | `ToggleSiteChangeCommand` | private | ✅ |
| `ToggleSiteStar(SourceInfo)` | `ToggleSiteStarCommand` | private | ✅ |
| `MoveSiteUp(SourceInfo)` | `MoveSiteUpCommand` | private | ✅ |
| `RefreshLogs()` | `RefreshLogsCommand` | public | ✅ |
| `OpenLogFolder()` | `OpenLogFolderCommand` | public | ✅ |
| `ClearLogs()` | `ClearLogsCommand` | public | ✅ |
| `CopyLogPath()` | `CopyLogPathCommand` | public | ✅ |
| `ApplyVodConfig()` | **`ApplyVodConfigCommand`** | public | ❌ **XAML 未绑定**（只被 `ConfirmConfigDialog` / `SwitchConfig` 内部 `await`） |
| `RefreshEpgSubscription()` | `RefreshEpgSubscriptionCommand` | private | ✅ |
| `ApplyLiveConfig()` | `ApplyLiveConfigCommand` | public | ✅ |
| `ClearHistory()` | `ClearHistoryCommand` | private | ✅ |
| `LoadLiveConfigHistory()` | `LoadLiveConfigHistoryCommand` | public | ✅ |
| `SwitchLiveConfig(ConfigHistoryEntry)` | `SwitchLiveConfigCommand` | private | ✅ |
| `DeleteLiveConfig(ConfigHistoryEntry)` | `DeleteLiveConfigCommand` | private | ✅ |
| `SearchOnlineSubtitles()` | `SearchOnlineSubtitlesCommand` | private | ✅ |
| `SelectOnlineSubtitle(OnlineSubtitle)` | `SelectOnlineSubtitleCommand` | private | ✅ |
| `LoadOnlineSubtitle(OnlineSubtitleFile)` | `LoadOnlineSubtitleCommand` | private | ✅ |
| `OpenRemoteSettings()` | `OpenRemoteSettingsCommand` | public | ✅（弹窗脚） |
| `StartLocalControl()` | `StartLocalControlCommand` | private | ✅ |
| `ClearSubtitleCache()` | `ClearSubtitleCacheCommand` | private | ✅ |
| `ClearMediaCache()` | `ClearMediaCacheCommand` | private | ✅ |
| `ClearMediaInbox()` | `ClearMediaInboxCommand` | private | ✅ |
| `ClearWallpaper()` | `ClearWallpaperCommand` | private | ✅ |
| `OpenMediaInbox()` | `OpenMediaInboxCommand` | private | ✅ |
| `StopLocalControl()` | `StopLocalControlCommand` | private | ✅ |
| `OpenConfigDialog()` | `OpenConfigDialogCommand` | public | ✅ |
| `PasteFromClipboard()` | `PasteFromClipboardCommand` | public | ✅ |
| `ConfirmConfigDialog()` | `ConfirmConfigDialogCommand` | public | ✅ |
| `CancelConfigDialog()` | `CancelConfigDialogCommand` | private | ✅（弹窗 ×2） |
| `SwitchConfig(ConfigHistoryEntry)` | `SwitchConfigCommand` | public | ✅ |
| `DeleteConfig(ConfigHistoryEntry)` | `DeleteConfigCommand` | private | ✅ |

### 10.4 公开方法（非 Command）

| 方法 | 被谁调用 |
|---|---|
| `RefreshSites()` | ⚠️ **`MainViewModel.OnPageChanged`（`Page == AppPage.Settings` 时）** —— 见风险 R1；内部也调用 `_main.UpdateSourceName()` |
| `ClearPairingPresentation()` | `MainViewModel.ShutdownAsync()`；内部 `PairingQrImage?.Dispose()` |
| `CheckUpdateAsync()` | `App.axaml.cs` 启动 20s 后（`AutoCheckUpdates` 为真时）+ `OnCheckUpdate` |
| `CancelUpdate()` | `OnCancelUpdate` |
| `InstallUpdateAsync()` | `OnInstallUpdate` |
| `RefreshLog()` | `LogEnabled` setter、`RefreshLogs`、`App.axaml.cs`（ui-smoke） |
| `LoadConfigHistoryAsync()` | `OpenConfigDialog`、`RestoreBackupAsync`、`RecordSubscriptionAsync` |
| `CancelSubtitleSearch()` | `MainViewModel.ShutdownAsync()` |
| `SetWallpaper(string)` | `OnPickWallpaper` |
| `ExportBackupAsync()` | `OnExportBackup` |
| `RestoreBackupAsync(string)` | `OnRestoreBackup` |

**未在 XAML 露面的 VM 成员汇总（死代码 / 隐藏功能候选）**：
`Applying`（属性）、`SubtitleSearching`（属性）、`LogPath`（属性）、`AvailableUpdate` / `UpdateBusy`（属性，仅代码后置读）、`ApplyVodConfigCommand`（生成的命令从未被 XAML 绑定）。
→ `LogPath` + `CopyLogPath` 是一个**半成品功能**：命令名叫「复制」但实现只把路径显示在 `DiagnosticsMessage` 里（源码注释自认：「剪贴板需窗口，这里退化为把路径显示出来」）。迁移到独立窗口后**具备了窗口条件，可以真正实现复制到剪贴板**。

---

## 11. 跨分区依赖 / 跨窗口副作用

| # | 触发点 | 影响 | 说明 |
|---|---|---|---|
| D1 | `ThemeIndex` 变更 | `Application.Current.RequestedThemeVariant`（**全局**） + `Prefs "ui.theme"` | 影响**所有窗口**，包括新设置窗口自身。同时 `App.axaml.cs` 启动时也从 `ui.theme` 读初值 |
| D2 | `PosterDensity` 变更 | `Prefs "ui.poster-density"` + **`_main.Vod.RefreshPosterDensity()`** | 立刻重排主窗口点播页海报 |
| D3 | `SetWallpaper` / `ClearWallpaper` | `Prefs "ui.wallpaper"` + **`_main.WallpaperPath`** | 主窗口背景图 |
| D4 | `ApplyVodConfig` 成功 | `_services.CurrentVodConfig` + `RefreshSites()` + **`_main.Vod.Invalidate()`** + **`await _main.Home.LoadAsync()`** | 换源后主窗口内容整体重载 |
| D5 | `ApplyLiveConfig` 成功 | `_services.CurrentLiveConfig` + **`_main.Live.ApplyConfigurationAsync`** | 直播页分组/channel 全量替换 |
| D6 | `RestoreBackupAsync` | `VodConfigUrl`/`LiveConfigUrl`/`EpgSubscriptionUrl` 重新读盘 + 两个历史列表重载 + `_main.Live.ReloadProgrammeBackupAsync()` | 一处操作扰动 3 个分区 |
| D7 | `LanControl` | 只作为 `StartLocalControl` 的 `allowLan` 参数读取 | 改动**不热生效**，需停/启服务 |
| D8 | `ProxyPushedMedia` | `StartLocalControl` 的 `onMedia` 回调里决定是否走 `_services.LocalControl.RegisterMedia(url)` | 同 D7 |
| D9 | `LoadOnlineSubtitle` | 依赖 `_main.Player.Visible` 与 `CurrentSessionId`；缓存目录 `{DataDir}/subtitles`，上限 100 项 | 设置窗口与播放窗口耦合 |
| D10 | `ClearSubtitleCache` / `ClearMediaInbox` | **`_main.Player.Visible` 为真时直接拒绝**并写 `Message` | 交互上无禁用态，只有报错 |
| D11 | `LoginEnabled`(→`LogEnabled`) | `VodBox.Core.VodBoxLog.SetEnabled` + 立即 `RefreshLog()` |  |
| D12 | `AutoCheckUpdates` | 被 **`App.axaml.cs` 启动流程**读取（`window.Opened` 里 20 秒后检查） | 设置值跨越启动周期 |

### 会导航/操作主窗口的命令（独立窗口后必须重新设计）

| 命令 | 代码 | 迁移影响 |
|---|---|---|
| `OpenRemoteSettingsCommand` | `ConfigDialogOpen=false; _main.Navigate(AppPage.Settings); Section=6;` | ⚠️ **明确跳到「内嵌设置页」的推送与遥控分区**。设置独立后，`Navigate(AppPage.Settings)` 语义失效（内嵌页可能已删除）。当前实现其实是「关弹窗 + 切分区」，如果设置窗口已打开，等价于 `Content.Section = 6` |
| `OpenMediaInboxCommand` | `_main.Files.Navigate(dir); _main.Navigate(AppPage.Files);` | 让**主窗口**跳到「文件」页并把焦点放到 `media-inbox` 目录（副作用留在主窗口，需明确定义） |
| `StartLocalControl` 的 `configure:` 回调 | `_main.Navigate(AppPage.Settings); Section=0; DialogUrl=url; ConfigDialogOpen=true; Message="收到配置地址，请在桌面确认加载";` | ⚠️ **最高危**：手机推送配置地址时，靠这段代码把用户带回内嵌设置页并弹窗。独立窗口后必须改为「打开/激活设置窗口 + 定位分区 0 + 弹窗」 |
| `MainViewModel.GoSettingsCommand` | `Navigate(AppPage.Settings)` | 由 `MainWindow.axaml:65`（侧边栏「设置」）与 `HomeView.axaml.cs:90`（无源引导）触发。**迁移后应改为开窗**，否则「设置」入口会失效 |
| `MainViewModel.OnPageChanged` | `if (value == AppPage.Settings && !_designTime) Settings.RefreshSites();` | ⚠️ **`RefreshSites()` 唯一的自动触发点**。内嵌页消失后，站点列表需在开窗时手动刷新 |

---

## 12. 风险点清单（迁移容易丢的东西）

**R1 — `RefreshSites()` 失去自动触发点（高危）**
旧逻辑靠 `MainViewModel.OnPageChanged` 在 `Page == AppPage.Settings` 时刷新站点列表。新窗口打开时不再有 `Page` 变化事件 → 站点列表可能为空或过期。**必须在 `SettingsWindow` 打开时调用 `Settings.RefreshSites()`。**

**R2 — `ConfigDialogOpen` 弹窗位置与绑定路径（高危）**
弹窗是根 `Panel` 的兄弟节点（不在分区内），路径写死为 `Settings.ConfigDialogOpen` / `Settings.DialogUrl` / `Settings.XxxCommand`。若新窗口把 `SettingsViewModel` 直接当 `DataContext`（而不是塞进 `Content`），所有 `Settings.` 前缀都要去掉；反之保留。**两种写法必须一致，否则弹窗永久不可见。**

**R3 — 分区索引两套映射不一致（高危）**
- 旧 `SettingsViewModel`：`源=0 播放=1 弹幕=2 字幕=3 界面=4 数据=5 推送=6 关于=7 诊断=8`，且注释仍写「0=源与订阅 1=播放 2=数据 3=关于」（**注释已过期**）。
- 新 `SettingsWindowViewModel`：左栏顺序 `源0 播放1 弹幕2 字幕3 界面4 数据5 推送6 诊断8 关于7`（诊断/关于**视觉顺序与索引顺序相反**）。
- **硬编码 `Section` 赋值点**：`OpenRemoteSettings` 里 `Section = 6`；`StartLocalControl.configure` 回调里 `Section = 0`；`ConfiguredSection` 转换器 `ConverterParameter=0..8`；`App.axaml.cs` ui-smoke 的 `for(var section=0;section<9;section++)`。
- 如果迁移时把「关于/诊断」重排为 7/8 的自然顺序，**上面这些魔法数字会全部错位**。

**R4 — `$parent[views:SettingsView]` 绑定路径（高危）**
站点列表、两套历史列表、字幕两个结果列表共 **11 处**使用
`{Binding $parent[views:SettingsView].((vm:MainViewModel)DataContext).Settings.XxxCommand}`。
这条路径要求：① 模板里有一个 `SettingsView` 祖先；② 该 `SettingsView.DataContext` 是 `MainViewModel`。
**如果新窗口直接承载 `SettingsViewModel`（`DataContext` 不是 `MainViewModel`、也没有 `SettingsView`），这 11 处全部静默失效（按钮点了没反应，且不报错）。** 这是最容易被忽略、且后果最隐蔽的丢失点。

**R5 — 4 个 `Click=` 事件处理器（代码后置，不在 XAML 里）**
`OnPickWallpaper` / `OnExportBackup` / `OnRestoreBackup` / `OnCheckUpdate` / `OnInstallUpdate` / `OnCancelUpdate`（实际 6 个 `Click`，其中 §5 两个、§8 三个、§4 一个）。
它们全部依赖 `TopLevel.GetTopLevel(this)`，其中 `OnInstallUpdate` 还要求 `as Window` 并用 `ShowDialog<bool>(parent)`。**只搬 axaml 会把它们整段丢掉。**

**R6 — 「海报密度」ComboBox 选项顺序与持久化语义相反**
XAML 顺序是「宽松(0) / 标准(1) / 紧凑(2)」，`OnPosterDensityChanged` 直接 `Prefs.Set("ui.poster-density", Clamp(value,0,2))`。即「宽松」被存成 0。新窗口骨架里的缩略图单选顺序是「紧凑 / 标准 / 宽松」，**如果按骨架顺序映射到 0/1/2，会把已有用户的密度设置整体反转**。迁移时必须保持索引语义不变。

**R7 — `PairingQrImage` 是需手动释放的 `Bitmap`**
`StartLocalControl` 与 `ClearPairingPresentation` 里都有 `image?.Dispose()`。窗口关闭路径若不调用 `ClearPairingPresentation()`，会泄漏 GDI/macOS 位图。`MainViewModel.ShutdownAsync()` 目前负责收尾——**新窗口独立开合后，需要在自己的关闭事件里补这一步**。

**R8 — `Message` 是跨分区常驻提示，不是某个分区的元素**
它写在 9 个分区 `StackPanel` 之外，所以任何分区的操作反馈（共 33 个命令 + 6 个事件处理器，几乎全部以 `Message = ...` 反馈）都靠它。**新窗口若不保留一个全局提示位，绝大多数操作会变成「点了没反应」。**

**R9 — 无二次确认的破坏性操作**
「清空观看历史与已看记录」「清空媒体缓存」「清理上传收件箱」「清理下载字幕缓存」「合并恢复备份」全都没有确认对话框（仅靠 `Message` 文字）。骨架里把它们标成 `Classes="danger"`，迁移是补齐确认态的机会，**但不要顺带把功能删掉**。

**R10 — 图标误用与颜色硬编码**
- 旧左栏「弹幕」「字幕」都用 `Icon.Information`（错误），规格要求 `Icon.Danmaku` / `Icon.Subtitles`（两个 key **确认存在**于 `Resources/Icons.xaml`）。
- 硬编码颜色：`#17FFFFFF`（Separator）、`#264CC2FF`（Site Runtime 徽章）、`#4CC2FF`（多处前景）、`#0078D4`（主按钮）、`#88 000000`（遮罩）、`#26FFFFFF`、`#1FFFFFFF`、`#0AFFFFFF`、`#FF6B6B`（删除按钮）。规格要求统一走 `StrokeBrush` / `AccentBrush` / `AccentStrongBrush` / `DangerBrush`。
- 旧导航高亮用的是 `SectionConverters.NavBackground`（**蓝色块 `#264CC2FF`**），明确违反新规格 §3.3「禁止蓝色高亮块」。同文件里的 `NavForeground` 转换器**从未被 XAML 使用**。

**R11 — `App.axaml.cs` 的 `--ui-smoke` 会因迁移而失败**
```csharp
foreach (var page in new[]{… AppPage.Settings}) { main.Navigate(page); window.UpdateLayout(); … }
for(var section=0; section<9; section++){ main.Settings.Section=section; window.UpdateLayout(); … }
main.Settings.RefreshLog();
if (string.IsNullOrWhiteSpace(main.Settings.LogTail)) throw new InvalidOperationException("诊断区没有读到日志内容。");
```
它强依赖「设置是 `MainWindow` 里的一页」+「`Section` 0..8 全部可渲染」。**删除内嵌 `SettingsView` 会让 ui-smoke 直接抛异常**，必须同步改造（或让 smoke 走新窗口）。

**R12 — 新窗口骨架里的臆造设置项（反向风险）**
`SettingsWindow.axaml` 目前含有 VM 里**不存在**的项：「记忆播放进度」「弹幕来源/自动匹配/仅文件名/不加载」「默认字幕语言/简体中文/繁體中文/English」「自动加载字幕」「重新生成配对码」。**迁移是「旧 → 新」的等价搬运，不是照抄骨架**；这些项要么删除，要么另开需求新增对应 VM 属性。反过来，旧页里 33 个命令 + 全部列表在新骨架里**一个都没有**（骨架是空壳）。

**R13 — 分区标题字号/文案不一致**
分区 0 标题 `FontSize=20 FontWeight=SemiBold`（「源与订阅」），分区 1/2/3 是 `FontSize=20` 无 `SemiBold`，分区 4 标题是「界面主题」（非「界面」），分区 8 是「诊断日志」（非「诊断」）。新规格 §4.1 明确要求**不再重复 20pt 大标题**（标题移到 44px 头部条）。搬运文案时注意：**左栏名 ≠ 分区内标题名**，两处都要正确落位。

---

## 13. 统计汇总（供验收对照）

| 分区 | 索引 | 可交互项 | 说明性文本 | 列表控件 |
|---|---|---|---|---|
| 源与订阅（分区内） | 0 | 10 | 1 | 2（Sites / LiveConfigHistory） |
| ↳ 其根级弹窗「更改点播配置」 | — | 8 | 1 | 1（ConfigHistory） |
| 播放 | 1 | 6 | 0 | 0 |
| 弹幕 | 2 | 3 | 1 | 0 |
| 字幕 | 3 | 9 | 2 | 2（OnlineSubtitles / SubtitleFiles） |
| 界面 | 4 | 4 | 1 | 0 |
| 数据 | 5 | 6 | 0 | 0 |
| 推送与遥控 | 6 | 7（含展示型 Image/Text） | 2 | 0 |
| 关于 | 7 | 6 | 1 | 0 |
| 诊断 | 8 | 8（含只读 TextBox） | 1 | 0 |
| **合计** | | **61** | **10** | **5** |

> 另计：左栏 9 个分区导航按钮、根级弹窗容器 1 个、纯代码构建的运行时弹窗 1 个（标题「确认更新 VodBox」，含 2 个按钮）、33 个 `[RelayCommand]`、**6 个 `Click=` 事件处理器**（`OnSelectSection` 之外）、**11 处 `$parent[views:SettingsView]` 绑定**（站点 5 + 直播历史 2 + 点播历史 2 + 在线字幕 1 + 字幕文件 1）。
> 说明：「可交互项」按控件计数（含 `CheckBox`/`Slider`/`NumericUpDown`/`ComboBox`/`TextBox`/`Button`/`Image`/只读 `TextBox`），因此比「设置语义条目数」略多（例如「音量」的 `TextBlock` 标签与 `Slider` 分开计）。
