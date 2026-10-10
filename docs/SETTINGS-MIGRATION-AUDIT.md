# VodBox 设置迁移审计报告

> 作者：Researcher (Alma) · 类型：**只读审计**
> 基准：`docs/SETTINGS-ITEM-INVENTORY.md` + 旧 `src/VodBox.Desktop/Views/SettingsView.axaml(.cs)`
> 对象：`Views/SettingsWindow.axaml(.cs)` + `Views/Settings/{Sources,Playback,InterfaceData,RemoteDiagnostics,About}SettingsView.axaml(.cs)`

## 审计快照

审计期间新实现文件**正在被并发编辑**（`SettingsWindow.axaml` 的 mtime 从 02:14 变到 02:29，行数从 193 变为 280）。
下面所有结论固定在一个**冻结快照**上（探测到 20s 内无改动后取样）：

| 文件 | md5 |
|---|---|
| `Views/SettingsWindow.axaml` | `21ad947574af028b874422014f52a0ba`（280 行） |
| `Views/Settings/SourcesSettingsView.axaml` | `de37d6c64ccdbe3b9ad2d50ff8a6f697` |
| `Views/Settings/PlaybackSettingsView.axaml` | `8a4b3f38e6ca525fa057b0b5f086a027` |
| `Views/Settings/InterfaceDataSettingsView.axaml` | `fd91401863a0693aa3909a5b05d62a8b` |
| `Views/Settings/RemoteDiagnosticsSettingsView.axaml` | `d9350bc6a8c408166b789ce8a9fe4032` |
| `Views/Settings/AboutSettingsView.axaml` | `bc9b0dc7cff0753a376916ecb16bc723` |

> ⚠️ 若实现方在本报告之后又改了文件，请以 md5 为准复核。

---

## 0. 结论先行

- **设置项本身：零缺失。** 清单 §13 的 **61 个可交互设置项全部在新实现里找到对应控件**，且绑定名与旧实现**逐一一致**（自动 diff：旧 XAML 里出现的命令名 `OLD-not-NEW: []`，绑定路径除结构性差异外无丢失）。
- **10 段「必须逐字搬运」的说明性文字：10/10 原文在位**（脚本逐条精确匹配，0 条缺失）。
- **6 个 `Click=` 代码后置处理器：6/6 全部迁移**，`OnSelectSection` 被 `SelectCommand` 机制取代。
- **危险绑定扫描：11 处 `$parent` 全部指向真实存在的祖先，无一悬空。**
- **但发现 1 个真实回归（高危）**：跨分区常驻的 `Message` 提示位只剩「源与订阅」一个分区在显示 → 数据/推送/字幕/界面等分区的操作反馈**运行时不可见**。
- 另有 1 个中危行为缺口（`OpenRemoteSettings` 新建窗口时左栏选中不同步）与若干低危「改写」。

---

## 1. 逐项核对（三态）

### 1.1 分区 3——源与订阅（10 项）

| # | 项 | 状态 | 新文件位置 |
|---|---|---|---|
| 1 | 点播配置 URL 只读展示 | ✅ | `SourcesSettingsView.axaml:24` (`{Binding VodConfigUrl}`) |
| 2 | 「更改配置…」按钮 | ✅ | `SourcesSettingsView.axaml:27` (`OpenConfigDialogCommand`) |
| 3 | 直播配置 TextBox | ✅ | `SourcesSettingsView.axaml:45` (`LiveConfigUrl`) |
| 4 | 「加载」按钮 | ✅ | `SourcesSettingsView.axaml:48` (`ApplyLiveConfigCommand`) |
| 5-9 | 站点列表 5 按钮（显示/隐藏·搜索开关·换源开关·星标·上移） | ✅ | `SourcesSettingsView.axaml:75-88`，命令经 `$parent[UserControl]` |
| 10 | EPG 订阅 TextBox + 「刷新节目订阅」 | ✅ | `SourcesSettingsView.axaml:120-126` |
| 11 | 「查看直播订阅历史」按钮 | ✅ | `SourcesSettingsView.axaml:140` (`LoadLiveConfigHistoryCommand`) |
| 12-13 | 直播历史 行（切换/删除） | ✅ | `SourcesSettingsView.axaml:161/164` |
| — | 死节点 `TextBlock Text="{Binding Api}" IsVisible="False"` | ⚠️ 有意删除 | 旧实现即为 `IsVisible=False` 的死代码，非设置项，未搬运属合理 |

### 1.2 根级弹窗「更改点播配置」（8 项）——✅ 全部迁移

位置：`SettingsWindow.axaml:195-273`，整体经 **`Content.` 前缀**绑定 `SettingsViewModel`（路径写法与旧「根 `Panel` 仍是 `MainViewModel`、用 `Settings.` 前缀」等价，语义一致）。

| 项 | 状态 | 位置 |
|---|---|---|
| 弹窗可见性 | ✅ | `SettingsWindow.axaml:195` (`{Binding Content.ConfigDialogOpen}`) |
| 关闭 X | ✅ | `:206` (`Content.CancelConfigDialogCommand`) |
| 说明「输入 TVBox 配置地址…」 | ✅ | `:211` |
| DialogUrl TextBox | ✅ | `:216` (`Content.DialogUrl`) |
| 「粘贴」 | ✅ | `:218` (`Content.PasteFromClipboardCommand`) |
| 历史列表 ConfigHistory + 「使用中」徽章 | ✅ | `:231-240` |
| 「切换」/「删除」 | ✅ | `:243`/`:247`（`$parent[views:SettingsWindow]…Content.SwitchConfigCommand` / `DeleteConfigCommand`） |
| 「手机扫码推送」/「取消」/「确定加载」 | ✅ | `:260`/`:268`/`:270` |

### 1.3 分区 1 播放（6 项）——✅ 全部迁移
`PlaybackSettingsView.axaml`（`IsPlayback` 段，:14-113）：`AutoNext` ✅ · `Incognito` ✅ · `PlayerSettings.Volume` ✅ · `PlayerSettings.Rate` ✅ · `PlayerSettings.OpeningSkipSeconds` ✅ · `PlayerSettings.EndingSkipSeconds` ✅。
> ➕ 增强（非丢失）：音量新增实时数值读数（`:66`）——旧实现"无数值读数"。

### 1.4 分区 2 弹幕（3 项 + 1 文案）——✅ 全部迁移
`PlaybackSettingsView.axaml`（`IsDanmaku` 段）: `DanmakuEnabled` ✅ · `DanmakuOpacity` ✅ · `DanmakuLimit` ✅ · 说明文字 ✅（`:170`）。

### 1.5 分区 3 字幕（9 项 + 2 文案）——✅ 全部迁移
`PlaybackSettingsView.axaml`（`IsSubtitles` 段）: `SubtitleDelay` ✅ · `SubtitleFontSize` ✅ · ASSRT 安全声明 ✅ · `SubtitleCredential` ✅ · `SubtitleQuery` ✅ · `SearchOnlineSubtitlesCommand` ✅ · `OnlineSubtitles` 列表 ✅ · `SubtitleFiles` 列表 ✅ · 结尾说明 ✅。
两条链条命令 `SelectOnlineSubtitleCommand`/`LoadOnlineSubtitleCommand` 经 `$parent[settings:PlaybackSettingsView]` 正确解析 ✅。

### 1.6 分区 4 界面（4 项 + 1 文案）——✅ 全部迁移
`InterfaceDataSettingsView.axaml`（`IsInterface` 段）: `ThemeIndex` ✅ · `PosterDensity` ✅ · `OnPickWallpaper` ✅ · `ClearWallpaperCommand` ✅ · 说明 ✅。
> ⚠️ **R6 风险已规避**：新 ComboBox 选项顺序仍为「宽松/标准/紧凑」（:63-67），与 `ui.poster-density` 的 0/1/2 语义一致，未反转。

### 1.7 分区 5 数据（6 项）——✅ 全部迁移
`InterfaceDataSettingsView.axaml`（`IsData` 段）: `OnExportBackup` ✅ · `OnRestoreBackup` ✅ · `ClearSubtitleCacheCommand` ✅ · `ClearMediaCacheCommand` ✅ · `ClearMediaInboxCommand` ✅ · `ClearHistoryCommand` ✅。

### 1.8 分区 6 推送与遥控（7 项 + 2 文案）——✅ 全部迁移
`RemoteDiagnosticsSettingsView.axaml`（`IsRemote` 段）: `LanControl` ⚠️控件改 `ToggleSwitch` ✅绑定不变 · `PairingInformation` ✅ · `PairingQrImage` ✅ · `ProxyPushedMedia` ⚠️`ToggleSwitch` · `StartLocalControlCommand` ✅ · `StopLocalControlCommand` ✅ · `OpenMediaInboxCommand` ✅ · 两段说明 ✅。

### 1.9 分区 7 关于（6 项 + 1 文案）——✅ 全部迁移
`AboutSettingsView.axaml`: `AppVersion` ✅ · `AutoCheckUpdates` ✅ · `UpdateMessage` ✅ · `OnCheckUpdate`/`OnInstallUpdate`/`OnCancelUpdate` ✅ · Linux 说明 ✅。
> 纯代码构建的运行时确认弹窗（`new Window{Title="确认更新 VodBox"}`）**逐字保留**在 `AboutSettingsView.axaml.cs:19-38`。

### 1.10 分区 8 诊断（8 项 + 1 文案）——✅ 全部迁移
`RemoteDiagnosticsSettingsView.axaml`（`IsDiagnostics` 段）: 引导文案 ✅ · `LogEnabled` ⚠️`ToggleSwitch` · `LogDirectory` ✅ · `LogFile` ✅ · `RefreshLogsCommand` ✅ · `OpenLogFolderCommand` ✅ · `ClearLogsCommand` ✅ · `CopyLogPathCommand` ✅ · `DiagnosticsMessage` ✅ · `LogTail` 只读预览（保留等宽字体/半透明底/240-360 高度）✅。

### 1.11 导航壳（9 按钮）——✅ 迁移并修正
`SettingsWindow.axaml:135-156` 用 `NavItemView` + `SelectCommand`；**图标误用已修正**为 `Icon.Danmaku` / `Icon.Subtitles`（旧实现两处皆为 `Icon.Information`）。9 个 label 文案全部在位。

---

## 2. 绑定名核对

**结论：未发现任何绑定名被写错。**

自动 diff（旧 `SettingsView.axaml` 86 条绑定路径 vs 新实现 101 条，剥离 `$parent`/`Settings.`/`Content.` 前缀后比较）：

```
OLD-not-NEW:  Api, Section, Settings, Is{Sources..Diagnostics}Section
NEW-not-OLD:  {九个分区}.IsSelected / .SelectCommand, Content, CurrentTitle,
              IsPlayback/IsDanmaku/IsSubtitles/IsInterface/IsData/IsRemote/IsDiagnostics
```

- 命令名 diff：`OLD-not-NEW: []`（**旧实现引用的每个 `*Command` 在新实现中都存在同名绑定**）。
- `Section` / `IsXxxSection` 的消失是**结构性改写**（旧的 VM 布尔可见性开关 → 新的控件级 `DirectProperty` + `$parent`），非绑定错误；`IsXxxSection` 属性在 VM 中仍保留。
- `Api` 是旧实现里 `IsVisible=False` 的死节点，删除合理。

逐项抽验关键项，绑定名全部一致：`PlayerSettings.{AutoNext,Incognito,Volume,Rate,OpeningSkipSeconds,EndingSkipSeconds,DanmakuEnabled,DanmakuOpacity,DanmakuLimit,SubtitleDelay,SubtitleFontSize}`、`ThemeIndex`、`PosterDensity`、`VodConfigUrl`、`LiveConfigUrl`、`EpgSubscriptionUrl`、`LanControl`、`ProxyPushedMedia`、`PairingInformation`、`PairingQrImage`、`AutoCheckUpdates`、`UpdateMessage`、`LogEnabled`、`DiagnosticsMessage`、`LogTail`。

**唯一文案级差异（非绑定名）：**
| 项 | 旧 | 新 | 判定 |
|---|---|---|---|
| 日志目录 | `{Binding LogDirectory, StringFormat='目录：{0}'}` | `{Binding LogDirectory}` + 行标题「日志目录」 | ⚠️ 改写（语义等价） |
| 当前文件 | `{Binding LogFile, StringFormat='当前文件：{0}'}` | `{Binding LogFile}` + 行标题「当前文件」 | ⚠️ 改写（语义等价） |

---

## 3. 说明性文字核对（清单第 3 条，10 段）

脚本对 10 段「必须逐字搬运」文案做精确子串匹配：**10/10 全部命中，无丢失、无改动**。

| # | 文案（节选） | 新位置 |
|---|---|---|
| 1 | 支持XML、JSON与gzip；… | `PlaybackSettingsView.axaml:170` |
| 2 | ASSRT服务凭据仅保留在本次会话内存… | `PlaybackSettingsView.axaml:213` |
| 3 | 外挂字幕也可从播放器字幕菜单加载… | `PlaybackSettingsView.axaml:283` |
| 4 | 基础页面与导航跟随主题… | `InterfaceDataSettingsView.axaml:46` |
| 5 | 上传只接受媒体类型，单文件64 MiB… | `RemoteDiagnosticsSettingsView.axaml:79` |
| 6 | 默认只监听本机；局域网模式需先停服务再重启… | `RemoteDiagnosticsSettingsView.axaml:81` |
| 7 | Linux 系统包会打开下载地址或发行说明… | `AboutSettingsView.axaml:57` |
| 8 | 播放出问题时：打开日志 → 复现一次… | `RemoteDiagnosticsSettingsView.axaml:94` |
| 9 | 输入 TVBox 配置地址，或从剪贴板读取 | `SettingsWindow.axaml:211` |
| 10 | IPTV 直播源地址 | `SourcesSettingsView.axaml:44` |

---

## 4. 危险绑定扫描（`$parent[`）

新实现共 **11 处功能性 `$parent`**（另有多处是注释文本）。逐条判定祖先类型是否存在：

| 位置 | 引用祖先 | 祖先是否真实存在 | 判定 |
|---|---|---|---|
| `SourcesSettingsView.axaml:75,78,81,84,87,161,164` | `$parent[UserControl]` | 是（最近的 UserControl = `SourcesSettingsView` 自身，DataContext = `Content` = `SettingsViewModel`） | ✅ 可解析 |
| `PlaybackSettingsView.axaml:260,277` | `$parent[settings:PlaybackSettingsView]` | 是（自身） | ✅ |
| `PlaybackSettingsView.axaml:15,114,180` | `$parent[settings:PlaybackSettingsView].Is{Playback,Danmaku,Subtitles}` | 是（DirectProperty 已在 code-behind 注册） | ✅ |
| `InterfaceDataSettingsView.axaml:13,70` | `$parent[settings:InterfaceDataSettingsView].Is{Interface,Data}` | 是 | ✅ |
| `RemoteDiagnosticsSettingsView.axaml:14,91` | `$parent[settings:RemoteDiagnosticsSettingsView].Is{Remote,Diagnostics}` | 是 | ✅ |
| `SettingsWindow.axaml:176-184`（9 处） | `$parent[Window].((vm:SettingsWindowViewModel)DataContext)` | 是（根为 `Window`，DataContext = `SettingsWindowViewModel`） | ✅ |
| `SettingsWindow.axaml:243,247` | `$parent[views:SettingsWindow].((vm:SettingsWindowViewModel)DataContext).Content.…` | 是（`views:SettingsWindow` 就是根窗口；`Content` 已指向 `SettingsViewModel`） | ✅ |

**结论：无一处引用不存在的祖先。** 旧实现里那 11 处 `$parent[views:SettingsView]` 已在迁移中**全部正确改基**（站点 5 + 直播历史 2 + 在线字幕 1 + 字幕文件 1 → 改为分区控件；点播历史 2 → 改为 `$parent[views:SettingsWindow]`），未出现「编译通过但运行期点了没反应」的静默失效。

附带核查：新实现用到的**全部 `StaticResource Icon.*` 键在 `Resources/Icons.xaml` 中都存在**（含新引入的 `Icon.Danmaku`/`Icon.Subtitles`/`Icon.Television`/`Icon.Filmstrip`/`Icon.Speed`/`Icon.Playlist`/`Icon.Incognito` 等），无悬空资源键。

---

## 5. 代码后置处理器核对

| 旧处理器（`SettingsView.axaml.cs`） | 新位置 | 状态 |
|---|---|---|
| `OnCheckUpdate` | `Views/Settings/AboutSettingsView.axaml.cs:15` | ✅ |
| `OnInstallUpdate`（含纯代码确认弹窗） | `Views/Settings/AboutSettingsView.axaml.cs:19` | ✅ 逐字保留 |
| `OnCancelUpdate` | `Views/Settings/AboutSettingsView.axaml.cs:17` | ✅ |
| `OnPickWallpaper` | `Views/Settings/InterfaceDataSettingsView.axaml.cs:49` | ✅ |
| `OnExportBackup` | `Views/Settings/InterfaceDataSettingsView.axaml.cs:60` | ✅ |
| `OnRestoreBackup` | `Views/Settings/InterfaceDataSettingsView.axaml.cs:78` | ✅ |
| `OnSelectSection` | 被 `SettingsWindowViewModel.Select` / `SettingsSectionItem.SelectCommand` + `NavItemView` 取代 | ⚠️ 机制改写 |

**无丢失的处理器。** 对应 XAML 的 `Click=` 也都在位（`AboutSettingsView.axaml:47-49`、`InterfaceDataSettingsView.axaml:59/82/94`）。

---

## 6. 汇总统计

| 维度 | 清单总数 | ✅ 已迁移 | ❌ 缺失 | ⚠️ 改写/合并 |
|---|---|---|---|---|
| 可交互设置项（清单 §13） | 61 | **61** | **0** | 0 |
| 左栏分区导航按钮 | 9 | 9 | 0 | 0 |
| 必须逐字搬运的说明文字 | 10 | 10 | 0 | 0 |
| 数据列表类控件 | 5 | 5 | 0 | 0 |
| 代码后置 `Click=` 处理器 | 6 | 6 | 0 | 0（`OnSelectSection` 机制改写） |
| `$parent` 关键绑定 | 11 | 11（改基后全部可解析） | 0 | 0 |

**净结论：本次迁移在「设置项 / 绑定名 / 文案 / 处理器」四个维度上是零丢失的。真正的问题不在「东西少了」，而在「跨分区反馈位收窄」和一处「导航同步缺口」。**

---

## 7. 问题清单（按严重程度排序）

### 🔴 P0 — `Message` 跨分区常驻性丢失（真实回归，清单 R8 预警命中）
- **现象**：旧实现把 `{Binding Message}` 放在 9 个分区 `StackPanel` **之外**，是跨分区常驻提示位，承载 33 个命令 + 6 个事件处理器的成功/失败反馈。新实现里 `{Binding Message}` **只出现在 `SourcesSettingsView.axaml:178` 一处**；`SettingsWindow.axaml` / `PlaybackSettingsView` / `InterfaceDataSettingsView` / `RemoteDiagnosticsSettingsView` / `AboutSettingsView` **均无 `Message` 绑定**。
- **后果**：在「数据」「推送与遥控」「字幕」「界面」分区触发以下操作时，`Message` 被 VM 赋值但**界面上看不到任何反馈**——用户会以为按钮点了没反应：
  - `ClearSubtitleCache` / `ClearMediaCache` / `ClearMediaInbox` / `ClearHistory`（数据分区，`SettingsViewModel.cs:506-531`）
  - `StartLocalControl` / `StopLocalControl`（推送分区）
  - `SearchOnlineSubtitles` / `SelectOnlineSubtitle` / `LoadOnlineSubtitle`（字幕分区，`:390-450`）
  - `SetWallpaper` / `ClearWallpaper`（界面分区，`:541`）
  - `ExportBackup` / `RestoreBackup`（数据分区，事件处理器写 `VM.Message`）
- **定位依据**：`grep -rn '{Binding Message}' Views/` → 仅 `SourcesSettingsView.axaml:178` 与旧文件。
- **修复方向**：在 `SettingsWindow.axaml` 右栏底部（或分区控件之外的常驻条）加一个绑定 `Content.Message` 的提示位；这正是旧结构里「与 9 个分区并列的公共兄弟节点」所要保住的。

### 🟠 P1 — `OpenRemoteSettings` 新建窗口时左栏选中不同步（导航缺口）
- **现象**：`SettingsViewModel.OpenRemoteSettings()` = `ConfigDialogOpen=false; _main.OpenSettings(); Section=6;`（`SettingsViewModel.cs:458-459`）。窗口若**未打开**，`MainWindow.ShowSettingsWindow()` 走新建分支（`MainWindow.axaml.cs:180`），`new SettingsWindow(main.Settings)` 只设置 `Content`，**不调用 `SelectSection`**；`SyncSelectionFrom` 仅在「窗口已存在」分支被调用（`:175-179`）。
- **后果**：右栏内容可见性由 `SettingsWindowViewModel.{X}.IsSelected` 驱动，而该选中态停留在默认的「源与订阅」；即便随后把 `Settings.Section=6`，窗口也不会响应（无 `Section` 变更订阅）。即「手机扫码推送」在窗口尚未打开时，**不会真正落到『推送与遥控』**。
- **定位依据**：`SettingsWindow.axaml.cs:21`（构造仅设 `Content`）、`MainWindow.axaml.cs:171-185`、`SettingsWindow.axaml:176-184`（可见性由 `IsSelected` 驱动）。
- **修复方向**：新建分支或在 `Content` setter 里补一次 `SelectSection(settings.Section)`。

### 🟡 P2 — 控件类型改写（设计规格所致，非丢失）
| 项 | 旧 | 新 | 影响 |
|---|---|---|---|
| `LanControl` | `CheckBox` | `ToggleSwitch` | 绑定名不变，语义一致 |
| `ProxyPushedMedia` | `CheckBox` | `ToggleSwitch` | 同上 |
| `LogEnabled` | `CheckBox` | `ToggleSwitch` | 同上 |
| 分区可见性 | VM 布尔 `IsXxxSection` | 控件 `DirectProperty` + `$parent` | 结构改写，功能等价 |

### 🟢 P3 — 文案/结构微调（等价改写，可不处理）
- `LogDirectory`/`LogFile` 丢掉 `StringFormat='目录：{0}'`/`'当前文件：{0}'` 前缀，改由行标题承载（信息等价）。
- 旧死节点 `TextBlock Text="{Binding Api}" IsVisible="False"` 未搬运（本就不显示）。

### 🟢 P4 — 遗留风险（迁移未加剧，建议知悉）
- **R7 `PairingQrImage` 释放**：`ClearPairingPresentation()` 仅由 `MainViewModel.ShutdownAsync`（`MainViewModel.cs:117`）调用；独立窗口的 `Closed` 路径未补 `Dispose`。窗口独立开合后，位图释放时机仍依赖应用级关闭。
- **诊断空态文案**：`LogDirectory`/`LogFile`/`LogTail` 的 VM 空态串（如「（尚未初始化）」）未变，新实现直接透传，行为一致 ✅。

### ✅ 已确认被正确处理的高危项（供对照）
- **R1（站点列表自动刷新）已修复**：`MainViewModel.OpenSettings()` 在 `!designTime` 时调用 `Settings.RefreshSites()`（`MainViewModel.cs:181-184`），补上了旧 `OnPageChanged` 的触发点。
- **R2（弹窗绑定前缀）已正确处理**：弹窗挂在 `SettingsWindow` 根 `Panel`，统一 `Content.` 前缀，前后一致。
- **R3（分区索引）已对齐**：`SettingsWindowViewModel` 显式把「诊断=8 / 关于=7」映射回 VM 语义（`SettingsWindowViewModel.cs:67-68`），未按视觉顺序错位。
- **R4（`$parent[views:SettingsView]` 静默失效）已全部消解**（见 §4）。
- **R6（海报密度顺序反转）已规避**（见 §1.6）。
- **R10（图标误用）已修正**：左栏「弹幕/字幕」改用 `Icon.Danmaku`/`Icon.Subtitles`。
- **R12（骨架臆造项）未泄漏**：新实现中不存在「记忆播放进度」「弹幕来源」「默认字幕语言」等 VM 无对应属性的臆造项。
