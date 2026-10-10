# VodBox 独立设置窗口 — 设计规格（v1）

> 作者：Designer (Alma) · 状态：待 Developer 实现
> 目标：把「设置」从 MainWindow 内嵌页面改为**独立顶层窗口**，两栏布局（左分区导航 / 右卡片式内容）。
> 本文件只描述设计规格，不含生产代码。所有画刷/圆角/图标**必须复用现有资源**，见 `src/VodBox.Desktop/Resources/`。

---

## 0. 一句话原则

左栏的选中态**必须与主窗口侧边菜单逐像素一致**（半透明浅底 + 提亮文字，**不是**蓝色高亮块）。
参考截图（Snappy）只用来决定**右栏卡片式布局**，**不要**照抄它的高亮颜色。

---

## 1. 窗口规格

| 项 | 规格 | 理由 |
|---|---|---|
| 类型 | 独立顶层 `Window`（非 `Owner` 模态），已有实例则激活而不是开第二个 | 与 `PlayerWindow` 同级的独立窗口先例 |
| 默认尺寸 | **940 × 680** | 216 导航 + 内容 723，容纳 180px 右侧控件不挤压 |
| 最小尺寸 | **MinWidth 760 / MinHeight 560** | 760 时内容列仍有 487px 可用宽；560 时卡片区仍可见 2 组 |
| 可缩放 | 是（默认 `CanResize=true`），不设 `SizeToContent` | 长列表设置需要拉伸 |
| 标题 | `设置 — VodBox` | 任务栏/Dock 可区分；纯「设置」也可接受 |
| Icon | `/Assets/Icons/linux/256/vodbox.png` | 与 MainWindow 一致 |
| 材质 | `TransparencyLevelHint="Mica,AcrylicBlur,Blur"`，`TransparencyBackgroundFallback="{DynamicResource PageBackgroundBrush}"`，`Background="Transparent"` | 与 MainWindow/PlayerWindow 一致 |
| 客户区扩展 | `ExtendClientAreaToDecorationsHint="True"`，`ExtendClientAreaTitleBarHeightHint="44"`，`WindowDecorations="Full"` | 与主窗口统一 44px 自绘标题条 |
| 主题 | **不要**写 `RequestedThemeVariant`，跟随 App/用户设置 | `界面` 分区有「跟随系统/浅色/深色」，硬编码 Dark 会让该设置失效 |
| 字号基线 | 窗口级 `FontSize="13"` | 与 MainWindow 一致 |
| 打开位置 | `WindowStartupLocation="CenterOwner"`（由 MainWindow 打开） | 视觉上归属主窗口，但仍是独立窗口 |
| 快捷键 | `Esc` 关闭窗口；`Ctrl/Cmd+W` 亦可（可选） | 独立窗口的惯例 |

### 窗口按钮平台差异（必须处理）

标题条是 44px 自绘拖拽区，平台控件占位不同：

- **macOS**：红黄绿在**左上**，压在**左栏导航列**上方 → 左栏顶部 44px 设 `chrome:WindowDecorationProperties.ElementRole="TitleBar"`，`Margin="{OnPlatform macOS='90,0,0,0', Default='0'}"`（与 `MainWindow` 的 `SidebarTitleBar` 同款写法）。**导航列表从 y=44 开始**，避免与红黄绿重叠。
- **Windows / Linux**：按钮在**右上**，压在**右栏头部条**上 → 头部条 `Margin="{OnPlatform Default='0,0,140,0', macOS='0'}"`，右侧留 140px 空白不放大可点元素。
- 头部条整条标记为 `ElementRole="TitleBar"`（可拖拽）；条内任何交互元素（如后续加的按钮）必须显式标 `ElementRole="User"`。

---

## 2. 整体布局

```
Panel                                   ← 材质底
├─ Rectangle  Fill=PageBackgroundBrush  Opacity=0.40  Width=216  AlignLeft   ← 左栏着色（照抄 MainWindow.SidebarBackdrop）
├─ Rectangle  Fill=PageBackgroundBrush  Grid.Column=1                         ← 右栏不透明底（照抄 PageBackdrop）
├─ Grid ColumnDefinitions="216,*" RowDefinitions="44,*"
│   ├─ [0,0] Border  DragArea  ElementRole=TitleBar  Margin=OnPlatform macOS '90,0,0,0'
│   ├─ [0,1] Border  头部条    ElementRole=TitleBar  Margin=OnPlatform Default '0,0,140,0'
│   │        └─ TextBlock 当前分区标题 · Padding=28,0,28,0 · 垂直居中 · 左对齐
│   ├─ [1,0] ScrollViewer 左栏导航（Padding 0,4,0,0）
│   ├─ [1,1] ScrollViewer 右栏内容（Padding 28,20,28,32）
│   └─ Border  Grid.Column=0 · Width=1 · HorizontalAlignment=Right · Background=StrokeBrush · RowSpan=2   ← 竖向分隔线
└─ Border 左栏底部版本卡片（Dock 到左栏底部，不随导航滚动）
```

具体数值：

| 元素 | 值 |
|---|---|
| 左栏宽 | **216**（与主窗口侧边栏完全一致） |
| 竖向分隔线 | 1px，`StrokeBrush`，贯穿整个客户区（含 44px 标题条），位于左栏右缘 |
| 左栏着色 | `PageBackgroundBrush` @ `Opacity=0.40`，让 Mica/Acrylic 透出（同主窗口侧边栏） |
| 右栏底色 | `PageBackgroundBrush` 不透明，**不**透材质（与主窗口内容区一致，保证表格/文字可读） |
| 头部条高 | 44 |
| 右栏内边距 | `28,20,28,32` |
| 内容列最大宽 | **720**，`HorizontalAlignment="Left"`（窗口拉宽时内容不无限拉伸） |

---

## 3. 左栏导航规格

### 3.1 强烈建议：直接复用 `Views/NavItemView.axaml`

需求 4（高亮对齐主窗口）最省事、最不会走样的做法是**复用现有 `NavItemView` 控件**，只把 `IsSelected`/`Command` 换成设置分区。这样高亮、悬停、字号、图标尺寸天然一致，无需新颜色令牌、无需新转换器。

若因数据绑定需要自己实现，则必须逐项复刻下表数值。

### 3.2 导航项尺寸（与 `NavItemView` 完全一致）

| 属性 | 值 |
|---|---|
| 外层 `Border`（胶囊） | `Margin="8,1"` · `Padding="10,9"` · `CornerRadius="{DynamicResource AppControlCornerRadius}"` (6) |
| 图标 | `PathIcon` **18×18** |
| 图标与文字间距 | **12** |
| 文字 | `FontSize` **13.5** |
| 项高 | 约 **36px**（9+18+9） |
| 光标 | `Cursor="Hand"` |
| 圆角说明 | 实现里绑的是 `AppControlCornerRadius` = **6**（不是 7）。以 **token** 为准，不要写死 7 |

### 3.3 状态画刷（必须精确复制 `NavItemView.UpdateVisual()` 的逻辑）

| 状态 | 背景 | 前景（文字+图标） |
|---|---|---|
| 选中 · 深色 | `#1FFFFFFF` | `#FFFFFF`（不透明白） |
| 选中 · 浅色 | `#14000000` | `#202020` |
| 未选中 · 深色 | `Transparent` | `#C7FFFFFF`（≈78%） |
| 未选中 · 浅色 | `Transparent` | `#C7202020` |
| 悬停 | `Transparent` → 可选 `CardHoverBrush` | 同未选中 |

**明确禁止**：蓝色高亮块、左侧蓝色竖条指示器（主窗口已移除，设置窗口也不要加）、参考截图里的蓝色选中背景。
浅色/深色通过 `ActualThemeVariant` 判定，主题切换时实时刷新（`NavItemView` 已处理，复用即得）。

### 3.4 分组间距

导航项之间 `Margin="8,1"` → 视觉间距 2px。分组之间**额外插入 12px 空隙**（用一个 `Height="12"` 的占位或 `Margin="0,12,0,0"` 加在被分组首项上），得到 ≈14px 的组间留白。
组与组之间**不加分组标题文字**（保持左栏干净，与主窗口一致）。

### 3.5 左栏底部版本/状态卡片

固定在左栏底部（不参与导航滚动）：
- `Border` · `Margin="8,8,8,10"` · `Padding="12,10"` · `CornerRadius="{DynamicResource AppControlCornerRadius}"` (6) · `Background="{DynamicResource CardBrush}"` · `BorderBrush="{DynamicResource StrokeBrush}"` · `BorderThickness="1"`
- 内容（`StackPanel Spacing="6"`）：
  - 行 1：`Grid ColumnDefinitions="Auto,*,Auto"` — `TextBlock`「VodBox」`FontSize=12.5 FontWeight=SemiBold Foreground=TextPrimaryBrush`；右侧 `STABLE` 徽章 = `Border Padding="5,1" CornerRadius="4" Background=CardHoverBrush` 内 `TextBlock FontSize=9.5 Foreground=TextTertiaryBrush`
  - 行 2：`TextBlock` `版本 {AppVersion}` `FontSize=11 Foreground=TextTertiaryBrush`（`AppVersion` 已有，来自 `SettingsViewModel`）
- **注意**：`STABLE` 只是静态文案（当前代码里没有更新通道概念）。**不要**为此新建假的渠道判断逻辑；若不想显示可直接省略该徽章。
- 主窗口侧边栏底部已有的「HTTP 服务 · 未启用」**不在这里重复**。

---

## 4. 右栏内容规格

### 4.1 顶部头部条

- 高度 44，与标题条同一 `Grid.Row=0`，整条 `ElementRole="TitleBar"`。
- 内容：**只有当前分区标题**，`FontSize=15.5` · `FontWeight=SemiBold` · `Foreground="{DynamicResource TextPrimaryBrush}"` · 左对齐 · `Padding="28,0,28,0"` · 垂直居中。
- **不需要返回/前进箭头。** 判断与理由：
  1. 这是独立顶层窗口，没有可回退的导航栈——箭头会承诺一个不存在的「上一页」；
  2. 分区切换在左栏永远一键可达，返回箭头是冗余的第二条路径；
  3. 主窗口交互语言里没有前进/后退概念，加入会破坏一致性。
- 也**不需要**自绘关闭按钮（Windows 有原生三键，macOS 有红黄绿）。
- **不再重复**一个 `FontSize=20` 的分区大标题（现 `SettingsView` 有），标题已在头部条里。

### 4.2 分组标题

| 属性 | 值 |
|---|---|
| 字号 | **12** |
| 字重 | `SemiBold` |
| 颜色 | `{DynamicResource TextTertiaryBrush}` |
| 位置 | 卡片上方，`Margin="4,0,0,8"` |
| 与上一张卡片的距离 | **24**（第一个分组标题距离头部条 0，由 ScrollViewer 顶部 padding 20 提供） |
| 说明文字（可选） | 分组/分区引导句 `FontSize=11.5` · `TextTertiaryBrush` · `TextWrapping=Wrap` · `Margin="4,0,0,10"` |

### 4.3 卡片容器

| 属性 | 值 |
|---|---|
| 圆角 | `{DynamicResource AppPanelCornerRadius}` (**8**) |
| 背景 | `{DynamicResource CardBrush}`（比 `PageBackgroundBrush` 略亮一档，深色下约 #2C2C2C） |
| 边框 | `BorderThickness="1"` · `BorderBrush="{DynamicResource StrokeBrush}"`（很淡，深色 9% 白） |
| 内边距 | **0**（间距由每行自己承担，分隔线才能通到卡片边缘） |
| 裁剪 | `ClipToBounds="True"`（保证首/末行不溢出圆角） |
| 分组间距 | 同组内多个卡片间距 **8**；卡片到下一个分组标题 **24** |
| 列宽 | 卡片 `Stretch` 到内容列（最大 720） |

### 4.4 设置行

行结构：`Grid ColumnDefinitions="Auto,*,Auto"`，`Padding="16,12"`，`MinHeight=60`（双行行）/ `48`（单行行）。

| 元素 | 规格 |
|---|---|
| 左侧图标（可选） | `PathIcon` **18×18** · `Foreground="{DynamicResource TextSecondaryBrush}"` · `Margin="0,0,14,0"` · 垂直居中。**仅用于身份性行**（如「点播配置」「直播配置」），纯开关行不加图标以减噪 |
| 标题 | `FontSize=13.5` · `TextPrimaryBrush` · 单行不换行 |
| 副标题 | `FontSize=11.5` · `TextTertiaryBrush` · 与标题间距 **3** · 允许换行（`TextWrapping=Wrap`），`MaxWidth=420` |
| 副标题若是可复制值/链接 | `Foreground="{DynamicResource AccentBrush}"`（#4CC2FF）+ `TextTrimming="CharacterEllipsis"` |
| 右侧控件列 | `Auto` 宽 · 垂直居中 · 与文字列间距 **24**（`Margin="24,0,0,0"`） |
| 行间分隔线 | 1px · 颜色 `{DynamicResource StrokeBrush}` · **左侧缩进 46px**（16 padding + 18 图标 + 12 gap）· 右侧 0 · 末行不加。用 `Border Height=1 Margin="46,0,0,0"`，**不要**用硬编码 `#17FFFFFF`（旧代码遗留），统一走 token |

### 4.5 右侧控件形态规格

以下 6 种形态都有现成实现可参考（`SettingsView.axaml`），统一规格如下：

**1）主按钮（Primary）**
`Background="#0078D4"`(= `AccentStrongBrush`) · `Foreground="White"` · `BorderThickness=0` · `CornerRadius="{DynamicResource AppControlCornerRadius}"` · `Padding="14,7"` · `FontSize=12.5` · `FontWeight=SemiBold` · `MinHeight=32` · `Cursor=Hand`
（全局 `ShapeStyles.axaml` 已给 Button 设圆角，写不写 `CornerRadius` 都行，保持显式更稳）

**2）次按钮（Secondary）**
`Background="{DynamicResource CardHoverBrush}"` · `Foreground="{DynamicResource TextSecondaryBrush}"` · `BorderThickness=0` · `CornerRadius=AppControlCornerRadius` · `Padding="14,7"` · `FontSize=12.5` · `MinHeight=32` · `Cursor=Hand`

**3）危险/破坏性按钮（Destructive，用于清空/删除）**
`Background=Transparent` · `Foreground="{DynamicResource DangerBrush}"`(#FF6B6B) · `Padding="12,6"` · `FontSize=12.5` · `BorderThickness=0`

**4）开关 Switch**
用 `ToggleSwitch`，**不重写模板**：`OnContent=""` · `OffContent=""` · `MinWidth=44` · `VerticalAlignment=Center`。
若 Fluent 默认强调色与品牌色不一致，先在 `ThemeDictionaries` 里覆写 `ToggleSwitchFillOn` / `ToggleSwitchFillOnPointerOver` 为 `AccentStrongBrush`；**若资源名与实际主题不匹配，以实际模板为准，允许保持默认外观**——不要为改色重写整个控件模板。

**5）下拉 ComboBox**
`Width=180` · `Height=32` · `FontSize=12.5` · 圆角由全局样式提供（`AppControlCornerRadius`）；项模板沿用默认（含上下箭头）。
若选项较多（如站点列表）可放宽到 `Width=280`。

**6）滑杆 + 数值 Slider**
- `Slider` 加 **class `md`**（`App.axaml` 里已定义：4px 圆头轨道、14px 圆 thumb、`#4CC2FF` 主题色），`Width=180` · `VerticalAlignment=Center`。
- 右侧数值用 `TextBlock`：`FontSize=12` · `Foreground="{DynamicResource TextSecondaryBrush}"` · `MinWidth=44` · `TextAlignment=Right` · 带单位（`80%` / `1.0×` / `+0.5s`）；数值区与滑杆间距 12。
- 组合宽度 180 + 12 + 44 = **236**，需 ≤ 内容列可用宽（760 窗口下为 487，安全）。
- 数值必须与滑杆同步显示（拖动即更新），**不要**只留滑杆无读数。

**7）缩略图单选（Thumbnail Radio）**
用于「海报密度」等视觉偏好：`RadioButton`，`GroupName` 分组，内容为一个 `Border`：
- 常态：`Width=96 Height=60` · `CornerRadius="{DynamicResource AppControlCornerRadius}"` · `BorderThickness=1` · `BorderBrush="{DynamicResource StrokeBrush}"` · `Background="{DynamicResource CardBrush}"`
- 选中：`BorderBrush="{DynamicResource AccentBrush}"`(#4CC2FF) · `BorderThickness=2`（**仅此处允许蓝色**——它是「选中值」语义，不是导航高亮）
- 选项之间 `Spacing=10`；下方配 `FontSize=11.5 TextTertiaryBrush` 的小标签

### 4.6 滚动行为

- **两栏各自独立滚动**：左栏导航用 `ScrollViewer`（`VerticalScrollBarVisibility="Auto"`），右栏内容用 `ScrollViewer`。
- **滚动条位置**：用默认位置。右栏滚动条贴在**内容区右缘**（`Grid.Column=1` 内），不跨分隔线；左栏滚动条贴在左栏右缘（分隔线左侧）。
- **底部版本卡片不随滚动**：用 `Grid RowDefinitions="*,Auto"` 把卡片钉在左栏底部；只有导航列表滚动。
- 内容不足一屏时：卡片保持自然高度、顶部对齐，**不拉伸**（避免 `VerticalAlignment=Stretch`）。
- 内容较长时应保留头部条常驻（头部条在 `Grid.Row=0`，天然不滚动）。

---

## 5. 分区清单（左栏顺序 + 图标）

按用户要求覆盖 9 个分区，分 3 组（组间 12px 留白）。**所有图标 key 均已确认存在于 `Resources/Icons.xaml`**。

| # | 分区 | 图标 key | 说明 |
|---|---|---|---|
| **组 A — 内容与播放** | | | |
| 1 | 源与订阅 | `Icon.Broadcast` | 已用于现实现，语义贴合订阅源 |
| 2 | 播放 | `Icon.Play` | |
| 3 | 弹幕 | `Icon.Danmaku` | **现实现误用 `Icon.Information`，必须改** |
| 4 | 字幕 | `Icon.Subtitles` | **现实现误用 `Icon.Information`，必须改** |
| **组 B — 应用与设备** | | | |
| 5 | 界面 | `Icon.ViewGrid` | 主题/密度/壁纸 |
| 6 | 数据 | `Icon.ContentSave` | 备份/缓存/历史 |
| 7 | 推送与遥控 | `Icon.Cellphone` | 扫码推送 / 局域网遥控 |
| **组 C — 支持与信息** | | | |
| 8 | 诊断 | `Icon.History` | 日志与排障 |
| 9 | 关于 | `Icon.Information` | 版本与更新 |

**图标缺口说明（不要新增 key）：**
- 「诊断」没有日志/终端类图标 → **建议复用 `Icon.History`**（历史/记录语义最近）；次选 `Icon.Refresh`（刷新日志）。二者已在库中。
- 「播放」若想区别于媒体内容，可复用 `Icon.Speed`（倍速/播放参数语义），但默认仍推荐 `Icon.Play`。
- 现有 `Icon.Sparkles`（AI/花火）、`Icon.Fire`（热榜）、`Icon.Puzzle`（插件）、`Icon.Incognito`（隐私/无痕）暂未用上；`Icon.Incognito` 可用作「播放」里「不记录历史」的行图标。

---

## 6. 响应式 / 边界情况

| 场景 | 处理 |
|---|---|
| 窗口缩到最小（760×560） | 左栏**固定 216 不收缩**；右栏内容按需压缩。内容列此时可用宽 ≈ 487，控件 236 宽仍安全。**不**做折叠/抽屉式导航（窄到 760 以下已被 MinWidth 拦住） |
| 窗口放大（>1000） | 内容列封顶 720 并左对齐，右侧留白；卡片不被拉成一行超宽（避免「标题在最左、开关在最右」的阅读断裂） |
| 标题过长 | 行标题 `TextTrimming="CharacterEllipsis"`；副标题允许换行最多 2 行，超出继续换行（`TextWrapping=Wrap` + `MaxWidth=420`） |
| 路径/URL | 单行 `TextTrimming="CharacterEllipsis"` + `ToolTip.Tip` 显示完整值（不要用换行撑高行） |
| 内容不足一屏 | 卡片顶部对齐、自然高度，**不拉伸**；右栏底部保留 32px 内边距 |
| 高度不足（560） | 右栏滚动；左栏导航若也不足则独立滚动，版本卡片始终可见 |
| 分组为空 | 该分组标题 + 空卡片都不渲染（`IsVisible` 绑定），不显示空壳卡片 |
| 主题切换 | 左栏复用 `NavItemView` 自动跟随；右栏全部走 `DynamicResource`，禁止硬编码 `#1FFFFFFF` / `#4CC2FF` 等（`#0078D4` 主按钮、`#4CC2FF` 强调色、`#FF6B6B` 危险色已在 App 级定义，可用现有 key `AccentStrongBrush` / `AccentBrush` / `DangerBrush`） |
| 重复打开设置 | MainWindow 侧记录已开实例：已开则 `Activate()`，不叠第二个窗口 |
| Mica 不可用平台 | 材质回退到 `AcrylicBlur`/`Blur`，再回退 `PageBackgroundBrush`；左栏 40% 着色在任何回退下都可读 |

---

## 7. 验收清单（Developer 自查）

- [ ] 左栏选中项 = 半透明浅底 + 提亮文字（`#1FFFFFFF` / `#14000000`），**无**蓝色块、**无**竖条
- [ ] 左栏 216 宽、项 `Margin=8,1` / `Padding=10,9` / 圆角 token(6) / 图标 18 / 字号 13.5
- [ ] 头部条 44 高，含当前分区标题（15.5 SemiBold），**无**返回箭头，**无**重复的 20pt 分区标题
- [ ] macOS 左栏顶部让出 90px 给红黄绿；Windows 头部条右侧让出 140px
- [ ] 卡片圆角 8（`AppPanelCornerRadius`）、背景 `CardBrush`、边框 `StrokeBrush`
- [ ] 行分隔线用 `StrokeBrush` 且左缩进 46
- [ ] 6 种控件形态按 §4.5 数值实现；缩略图单选是唯一允许蓝色的控件态
- [ ] 窗口**未**硬编码 `RequestedThemeVariant`（主题设置仍生效）
- [ ] 底部版本卡片固定不滚动；`STABLE` 徽章为静态文案
- [ ] 内容列 MaxWidth 720 左对齐；窗口 760×560 下无控件重叠
