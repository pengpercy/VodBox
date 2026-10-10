# 播放窗口「加载中 / 缓冲中」视觉反馈 — 设计规格（v1）

> 作者：Designer (Alma) · 状态：待 Developer 实现
> 范围：为 `PlayerWindow` / `PlayerOverlay` 补一层**状态驱动的可见反馈**，解决「开窗到出画之间纯黑一片」与「播放中途卡顿无提示」。
> 本文只描述设计规格，**不含生产代码**。所有画刷 / 圆角 / 图标 **必须复用现有资源**（`src/VodBox.Desktop/Resources/`）。
> 事实依据：`docs/LIVE-PLAYBACK-STATE-FLOW.md`（只读调查）+ `src/VodBox.Desktop/Views/PlayerOverlay.axaml(.cs)` + `src/VodBox.Desktop/ViewModels/PlayerViewModel.cs`。

---

## 0. 一句话原则

**首次加载要「显眼、有主体」，中途缓冲要「克制、别抢戏」。**
两者共用同一条「居中提示带」（落在控制条那一格的中间行），但**外形、尺寸、出现时机完全不同**：加载期是一块居中的文字+进度条组合（黑屏上唯一主体），缓冲期是右下角之外的一个小胶囊（复用现有中央 toast 的几何），并**延迟 400ms 才出现**。

---

## 1. 设计前提（来自代码事实，实现时不要与之冲突）

| 事实 | 来源 | 对设计的影响 |
|---|---|---|
| 开窗第一刻 `State` 已是 `Resolving`（`Visible=true` 与 `State=Resolving` 同步块） | `PlayerViewModel.cs:305,307` | 加载提示**不需要延迟**，窗口一出现就该显示 |
| 首帧序列真实为 `Resolving → Loading → Playing`，**起始阶段不会出现 `Buffering`**（`_loaded==false` 时 `paused-for-cache` 被丢弃） | `MpvEngine.cs:357`；状态流文档 §2.2 | 加载提示只需处理 `Resolving`/`Loading`，**不需要 Buffering 分支** |
| `State==Playing` 早于「第一帧真正上屏」（`file-loaded` 早于 `OnOpenGlRender`，VM 拿不到首帧信号） | 状态流文档 §1.5 | 隐藏加载提示必须留**淡出时长**当缓冲期，不能硬切 |
| 加载期底部控制条被强制常显（`UpdateControls` 在非 `Playing` 时保持显示） | `PlayerOverlay.axaml.cs:224` | 提示**绝不能压住底部面板**；顶部标题栏同期也可见 |
| 播放中途卡顿**会**切到 `Buffering` 且 `State` 能通知到视图，但当前视图无任何 `State` 绑定 | 状态流文档 §2.3 | `Buffering` 是可用信号，需新增绑定 |
| `Error` 目前**没有任何 View 绑定** | 同上 §7.2 | 失败态顺带把它接出来 |
| 播放器窗口强制 `RequestedThemeVariant="Dark"` | `PlayerWindow.axaml:8` | 所有主题画刷只会解析到**暗色值**，配色可按下表直接写 |
| 项目已有 5 处 `ProgressBar IsIndeterminate="True"` 作为加载指示惯例（3–4px 细条） | `HomeView.axaml:172,221`、`VodView.axaml:146`、`LiveView.axaml:162`、`HistoryView.axaml:22`、`FavoritesView.axaml:68` | **优先复用该惯例**，不引入 spinner 图标（`Icons.xaml` 里没有语义合适的加载图标） |

**暗色主题下的实际色值**（`Theme.axaml` Dark + `App.axaml`）：

| 资源 key | 暗色值 | 用途 |
|---|---|---|
| `AccentBrush` | `#4CC2FF` | 加载进度条前景 |
| `TextPrimaryBrush` | `#FFFFFF` | 加载主文案 |
| `TextSecondaryBrush` | `#C7FFFFFF` | 缓冲文案 / 失败错误正文 |
| `TextTertiaryBrush` | `#85FFFFFF` | 次级信息（标题/来源）、提示语 |
| `DangerBrush` | `#FF6B6B` | 失败态图标 |
| （字面量）`#CC000000` | — | 缓冲胶囊底色；**与现有中央 toast 完全一致**（`PlayerOverlay.axaml:119`） |

圆角：胶囊/面板用 `AppPanelCornerRadius`（8）；进度条圆角用 `2`（与现有 `Slider` 轨道一致，见 `App.axaml` 的 `Track RepeatButton CornerRadius`）。

---

## 2. 三种状态的视觉方案

### 2.1 首次加载 —— `Resolving` / `Loading`（黑屏期，显眼）

**外形：** 居中竖排，一条**不确定进度条**在上、两行文字在下。这是黑屏上唯一的视觉主体。

```
            ┌──────────────────────────────┐
            │   ▓▓▓▓▓░░░░░░░░░░░░▓▓▓▓▓      │   ← IsIndeterminate ProgressBar（160×4，AccentBrush）
            │                              │
            │        正在连接频道…          │   ← 主文案 13px TextPrimaryBrush
            │          CCTV-5              │   ← 次级 11.5px TextTertiaryBrush
            └──────────────────────────────┘
                        （窗口垂直居中）
```

| 项 | 规格 |
|---|---|
| 容器位置 | 根 `Panel` 直接子元素，自带 `Grid RowDefinitions="Auto,*,Auto"`，内容放 **`Grid.Row="1"`**（中间行）。**不要放进**已有那个 `IsVisible="{Binding Visible}"` 的控制层 `Grid` |
| 对齐 | `HorizontalAlignment="Center" VerticalAlignment="Center"`，`StackPanel Spacing="12"` |
| 进度条 | `ProgressBar IsIndeterminate="True" Width="160" Height="4" CornerRadius="2" Foreground="{DynamicResource AccentBrush}" Background="#33FFFFFF" HorizontalAlignment="Center"` |
| 主文案 | `FontSize="13" Foreground="{DynamicResource TextPrimaryBrush}"`，`TextAlignment=Center` |
| 次级文案 | `FontSize="11.5" Foreground="{DynamicResource TextTertiaryBrush}"`，`TextTrimming="CharacterEllipsis"`，`MaxWidth` 绑窗口宽 60%（或固定 320），防止超长频道名撑破布局 |
| 入画动画 | **无**。窗口出现时提示已在位、`Opacity=1`。任何淡入都会让开窗瞬间又多一次黑闪 |
| 命中测试 | 整个块 `IsHitTestVisible="False"` —— 保证双击全屏、拖拽窗口这些根 `Panel` 手势仍可用 |

**主文案（`Resolving` / `Loading` 分别取）：**

| 状态 | 文案 | 说明 |
|---|---|---|
| `Resolving` | `正在获取播放地址…` | 直播此态几乎瞬时（`Direct`，无 JSON 解析），一闪而过；点播会真调源站 |
| `Loading` + 直播 | `正在连接频道…` | 与 `LiveView.axaml:163` 的「正在加载频道…」同语感 |
| `Loading` + 点播 | `正在加载视频…` | 与 `VodView.axaml:147`「正在加载第 N 页…」同语感 |

**次级文案：** 直接绑 `{Binding Title}`（直播=频道名，点播=媒体名）。它确认「加载的是我点的那个」，同时与顶栏 14px SemiBold 标题形成主次而不打架。

> **直播/点播文案区分需要一个小改动**：`PlayerViewModel` 目前**没有**公开 `IsLive`（`_current` 是私有字段）。见 §7 的第 1 条。若 Developer 决定不加该属性，则三个状态统一用 `正在加载…`（仍可接受，只是少了「连接频道」的语感）。

---

### 2.2 播放中途缓冲 —— `Buffering`（已有画面，克制）

**外形：直接复用现有「中央 toast」的几何**（`PlayerOverlay.axaml:118-127`）——同一个黑色半透明胶囊、同一圆角、同一个居中带。让「缓冲」与「倍速/跳片头提示」在视觉上同属一个家族，用户不用学第二套语言。

```
                    ┌────────────────────────┐
                    │  ▓▓▓▓░░░░   正在缓冲…   │   ← 小进度条 40×3 + 文案 12px
                    └────────────────────────┘
                     （居中；Background #CC000000，圆角 8，Padding 14,8）
```

| 项 | 规格 |
|---|---|
| 容器 | `Border Background="#CC000000" CornerRadius="{DynamicResource AppPanelCornerRadius}" Padding="14,8" HorizontalAlignment="Center" VerticalAlignment="Center"`，放**中间行**（与控制层 toast 同格） |
| 内层 | `StackPanel Orientation="Horizontal" Spacing="8" TextElement.Foreground="White"` |
| 进度条 | `ProgressBar IsIndeterminate="True" Width="40" Height="3" CornerRadius="2" VerticalAlignment="Center"` |
| 文案 | `正在缓冲…`，`FontSize="12"` |
| 出现动画 | `Opacity 0→1`，`Duration="0:0:0.15"`，`CubicEaseOut` |
| 消失动画 | `Opacity 1→0`，`Duration="0:0:0.15"`，完毕再 `IsVisible=false` |
| 命中测试 | `IsHitTestVisible="False"` |

**为什么不做全屏遮罩 / 不居中放大的 spinner：** 画面还在（多半是最后一帧或继续在动），遮罩等于把「正在努力播放」误报成「已经断掉」。小胶囊只占屏幕中央一小块，眼睛不必离开画面就能读到。

---

### 2.3 失败态 —— `Failed`

按任务要求给出建议（**不在本次核心范围**，可与加载态共用同一条居中带，因此实现成本几乎为零）。

**共用加载块容器，三选一渲染**（加载块 / 失败块 互斥显示），失败块内容：

```
                 ┌───────────────────────────┐
                 │            ⓘ              │   ← Icon.Information，危险色着色
                 │      无法播放：{原因}        │   ← 绑 {Binding Error}，12.5px TextSecondaryBrush，换行
                 │  请稍后重试，或切换频道/线路。  │   ← 11.5px TextTertiaryBrush
                 └───────────────────────────┘
```

| 项 | 规格 |
|---|---|
| 图标 | **复用 `Icon.Information`**（`Icons.xaml` 中唯一可用的提示类图标；没有专用「错误/警告」图标）。`PathIcon Width="22" Height="22" Foreground="{DynamicResource DangerBrush}"` |
| 主文案 | `{Binding Error}`，`FontSize="12.5" Foreground="{DynamicResource TextSecondaryBrush}" TextWrapping="Wrap" MaxWidth="360" TextAlignment="Center"`（`Error` 已是 `无法播放：{message}` 形态，直接透传） |
| 提示语 | `请稍后重试，或切换频道/线路。`，`FontSize="11.5" Foreground="{DynamicResource TextTertiaryBrush}"` |
| 「重试」按钮 | **本轮不加**。`PlayerViewModel` 没有重试命令；直播已有 `LiveViewModel.HandlePlaybackFailure` 自动换线并把 `StatusMessage` 打到主窗口。等有 `ReplayCommand` 再加按钮，避免设计出无法接线的控件 |
| 与加载态的关系 | 复用同一个 `Grid.Row="1"` 居中带，仅切换内容。`IsLoading` 与 `IsFailed` 互斥，不会同框 |

---

## 3. 状态切换的时序与过渡

```
开窗 ──▶ Resolving ──▶ Loading ─────────────▶ Playing ──────────▶ Buffering(≥400ms) ──▶ Playing
          │              │                      │                      │                 │
        （已在位）   （只换主文案）      （整块 250ms 淡出后隐藏）   （150ms 淡入）   （150ms 淡出）
```

| 过渡 | 做法 | 时长 | 理由 |
|---|---|---|---|
| 窗口出现 → `Resolving` | 提示**直接就在**位，`Opacity=1`，无入场动画 | 0 | 避免开窗瞬间多一次黑闪 |
| `Resolving` → `Loading` | 容器不卸载：进度条、次级文案原样保留，**只替换主文案文字**，文字**即时切换**（不做交叉淡入） | 0 | 两者视觉骨架完全相同，任何过渡都是闪烁源；即时换字最稳 |
| `Loading` → `Playing` | 整个加载块 `Opacity 1→0`，完成后 `IsVisible=false` | **250ms**，`CubicEaseOut` | `Playing` 早于首帧上屏（§1），250ms 淡出兼作首帧宽限期，露出的是渐显的画面而不是硬切黑 |
| `Playing` → `Buffering` | 先等 **400ms**（§4），确认是持续卡顿后，胶囊 `Opacity 0→1` | 150ms | 见 §4 |
| `Buffering` → `Playing` | 胶囊 `Opacity 1→0`，完成后 `IsVisible=false` | 150ms，**无延迟** | 画面恢复即撤提示，不留残影 |
| 任意 → `Failed` | 加载块内容换成失败块（无过渡，或与加载块之间 150ms 淡入淡出） | 0–150ms | 需要立即看到原因 |

**不闪烁的三条硬约束（给 Developer）：**
1. `Resolving` 与 `Loading` **共用同一个容器实例**，不要用两个 `IsVisible` 不同的块拼接——否则会看到进度条闪一下重来。
2. **过渡绑 `Opacity`，不要绑 `IsVisible`**（`IsVisible` 是布尔，切换即硬闪）。
3. 隐藏用 `Opacity→0` 后**再**落 `IsVisible=false`（靠 `Transitions` + 一个短的 code-behind 收尾，或直接用 Avalonia 的 `IsVisible` 绑定 + `Transitions` 组合）；不要只把它缩到透明却留在命中测试里。

---

## 4. `Buffering` 的克制方案（明确数值）

| 参数 | 建议值 | 理由 |
|---|---|---|
| **显示延迟** | **400ms** | mpv 在分片边界会短暂置 `_buffering`，几百毫秒内的抖动人眼无法判定为「卡住」。<300ms 会频繁闪出提示、比不提示更烦；>700ms 用户已经开始怀疑「是不是死了」。400ms 落在「察觉卡顿」之前、又避开所有无感抖动 |
| **最短显示时长** | **600ms**（一旦显示） | 防止「出现 80ms 立刻消失」的闪烁。若在 600ms 内又回到 `Playing`，保持胶囊到 600ms 再淡出；若期间再次 `Buffering`，**不重置**计时、保持显示 |
| **隐藏延迟** | **0ms** | 恢复播放立即撤，不留残影 |
| 淡入/淡出 | 150ms | 与 toast / 抽屉（`PlayerWindow` 里 `0.22s CubicEaseOut` 的过渡惯例）同族 |
| 首次加载是否也要延迟 | **否**（立即显示） | 开窗即黑，任何延迟都是新增的纯黑期（§1 事实：窗口出现即 `Resolving`） |
| 全屏 vs 小窗差异 | 无 | 胶囊固定尺寸；在小窗（480×270）它仍只剩中央一小块，不挤占画面 |

**伪时序（给实现当验收标尺）：**

```
t=0     Playing → Buffering            （不显示）
t=400   仍 Buffering                    → 显示胶囊（淡入 150ms）
t=550   胶囊完全可见
t=700   Buffering → Playing             → 立即淡出（150ms）
t=850   胶囊隐藏

t=0     Playing → Buffering            （不显示）
t=120   Buffering → Playing             → 从未显示，无闪烁
```

---

## 5. 边界情况

| 场景 | 决策 |
|---|---|
| **全屏 vs 小窗（480×270）** | 字号**不缩放**（12–13px 在 480 与 4K 都清晰）。进度条**默认固定 160×4**——在 480 宽下仍有 320px 余量，不挤压。*可选打磨*：按窗口宽做一次自适应 `Width = clamp(窗口宽×0.25, 120, 320)`，用与 `PlayerLayoutConverters.PlaylistDrawerWidth` 同款的 `IMultiValueConverter` 实现；不实现也可交付 |
| **遮挡底部控制条** | 不会。提示放在 `Grid.Row="1"`（中间弹性行），控制条在 `Row="2"` 的 `Auto` 行、顶栏在 `Row="0"`。**禁止**用窗口绝对居中（`VerticalAlignment=Center` 直接挂根 `Panel`）——那会在矮窗里压到底部面板 |
| **开窗时控制层可能隐藏** | 加载提示的容器**不放在** `IsVisible="{Binding Visible}"` 的控制层 `Grid` 内，而是根 `Panel` 的独立子元素，只由状态驱动可见性。因此「控制层自动隐藏」与提示**零冲突**；反过来，此时提示是屏幕上唯一的元素，正好补足答案。（注：按 `PlayerOverlay.axaml.cs:224`，加载期控制条其实被强制常显，但**设计不依赖**这一行为） |
| **直播 vs 点播文案** | 区分，见 §2.1 表（`正在连接频道…` / `正在加载视频…`）。需要 `PlayerViewModel` 暴露 `IsLive`（§7） |
| **音频 / 纯音频内容** | **不特殊处理**：加载块照常显示（音频也要解析与缓冲），用同一套 `正在…` 文案。⚠️ 若 Developer 采用 §7 的「等首帧才隐藏」增强，**必须识别无视频轨并跳过该等待**，否则纯音频会永久停在加载提示 |
| **弹幕** | `Danmaku` 在新会话已被清空（`PlayerViewModel.cs:304`），无冲突。层级上加载块放在 `DanmakuCanvas` **之上**，读得清 |
| **迷你窗 / PiP（`CompactMode`）** | 提示尺寸不变。胶囊居中在极小的 PiP 窗里也成立；加载块在极小窗里可能偏大——可接受，不做额外分支 |
| **播放列表抽屉展开时** | 抽屉从右滑入覆盖视频；提示居中，两者不重叠。无需联动 |

---

## 6. 无障碍 / 「减少动画」降级

**现状约束（重要）：** `Resources/Skeleton.axaml:4` 明确写了「应用没有『减少动画』偏好可供沿用，无条件动画无法按偏好关闭」。所以本方案**不能假定**已有该开关。

**方案：**

1. **语义不依赖动画。** 文案（`正在连接频道…` / `正在缓冲…`）本身就完整表达了状态；进度条只是辅助。即便动画被系统/用户关掉，信息不丢——这是最低要求，本次必须满足。
2. **提供一个可关闭的偏好（建议本轮一并加）。** 新增全局偏好 `ui.reduce-motion`（bool，默认 `false`），加到「设置 → 界面」或「播放设置」。开启后：
   - 加载块：`ProgressBar` 的 `IsVisible=false`；改为在主文案下加一条**静态**细线（`Border Height="2" Width="160" Background="#33FFFFFF" CornerRadius="2"`），配合文案照常显示。
   - 缓冲胶囊：同上，隐藏进度条，只留 `正在缓冲…` 文字。
   - 所有淡入淡出过渡 `Duration="0:0:0"`（瞬切）。
3. **若本轮不加该偏好**，则接受默认动画，但**只保留 §3 里那几处短淡入淡出 + 一条不确定进度条**，不引入任何多余循环闪烁（与 `Skeleton.axaml` 的既有取舍保持一致）。
4. 对比度：白/近白文字压 `#CC000000` 或纯黑，对比度远超 4.5:1，满足 WCAG AA。
5. `AccentBrush #4CC2FF` 在 `#202020`/黑底上对比良好，且已是全应用的强调色，不做新色。

---

## 7. 给 Developer 的实现要点清单

### 7.1 需要新增的 ViewModel 支撑（`PlayerViewModel.cs`）

| 新增 | 定义 | 通知时机 |
|---|---|---|
| `bool IsLive` | `_current?.IsLive ?? false` | 在 `PlayResolvedAsync` 里 `_current = request;` 之后（`PlayerViewModel.cs:342` 附近）`OnPropertyChanged(nameof(IsLive))` |
| `bool IsLoading` | `State is PlaybackState.Resolving or PlaybackState.Loading` | 在 `partial void OnStateChanged`（`PlayerViewModel.cs:129`）里，紧跟现有 `OnPropertyChanged(nameof(IsPlaying))` 补一行 |
| `bool IsBuffering` | `State == PlaybackState.Buffering` | 同上 |
| `bool IsFailed` | `State == PlaybackState.Failed` | 同上 |
| `bool BufferingHintVisible` | 由 §4 的延迟/最短显示逻辑驱动的最终可见性 | 用一个 `DispatcherTimer`（可仿 `FlashToast`，`PlayerViewModel.cs:466`）实现 400ms 延迟与 600ms 最短显示；在 `State` 变为 `Buffering`/`Playing` 时驱动 |

> 把计时逻辑放 VM（而非 code-behind）是为了可测——现有假 `Engine` 能 `Emit(PlaybackState.Buffering)`，直接断言 `BufferingHintVisible`（见 `docs/LIVE-PLAYBACK-STATE-FLOW.md` §6）。

### 7.2 XAML 结构（`PlayerOverlay.axaml`）

- 在根 `Panel` 内、`<v:DanmakuLayer .../>` **之后**加入一个 `Grid`（自带 `RowDefinitions="Auto,*,Auto"`），作为两条提示的宿主：
  - `Grid.Row="1"`：加载块（§2.1）↔ 失败块（§2.3），由 `IsLoading` / `IsFailed` 互斥切换。
  - `Grid.Row="1"`：缓冲胶囊（§2.2），由 `BufferingHintVisible` 控制。
  - 加载块与缓冲胶囊**不会同时出现**（`Buffering` 只在 `_loaded` 之后），但仍建议用 `IsLoading`/`BufferingHintVisible` 两个独立 `IsVisible`，避免互相牵制。
- 复用现有 `Border` toast 的字面量背景 `#CC000000`、`AppPanelCornerRadius`；进度条按 §2 的尺寸。
- 过渡统一用 `Transitions` + `<OpacityTransition Property="Opacity" Duration="0:0:0.15" Easing="CubicEaseOut"/>`（模仿 `PlaylistDrawer` 的 `TransformOperationsTransition` 写法）。

### 7.3 Avalonia 原语选择

| 需求 | 选择 | 不要用 |
|---|---|---|
| 加载指示 | `ProgressBar IsIndeterminate="True"`（复用全应用既有惯例，5 处先例） | ❌ 不要新增 spinner 图标——`Icons.xaml` 没有加载类图标；❌ `PathIcon + RotateTransform` 自绘会与项目现有加载语言分叉 |
| 淡入淡出 | `Transitions` + `OpacityTransition` | `Animation` 关键帧（过重，且更难停） |
| 出现/消失 | `IsVisible` 绑状态 + `Opacity` 过渡 | 只改 `Opacity` 而不管 `IsVisible`（会残留命中区域、白跑动画） |
| 失败图标 | `PathIcon Data="{DynamicResource Icon.Information}"` | 假设可新增 `Icon.Warning`（不存在） |

### 7.4 必须注意的坑

1. **动画在窗口不可见时要停。** 用 `IsVisible=false`（而不是 `Opacity=0`）来隐藏进度条/胶囊，未挂载或不渲染的控件不会继续跑不确定动画。窗口 `Close` 后 `PlayerOverlay.OnDetached`（`PlayerOverlay.axaml.cs:198`）已会 `_controlsTimer.Stop()`，新加的计时器也要在那里停，并复位 `BufferingHintVisible=false`。
2. **不要动已有的控制层 `Grid`。** 提示是新增的兄弟节点；改控制层会牵连 `UpdateControls` / `PlayerLayout` 的最小宽度算式。
3. **`Resolving`/`Loading` 共用一个容器**，只换主文案文字（§3 硬约束 1）。
4. **加载块 `IsHitTestVisible="False"`**，否则会吃掉根 `Panel` 的双击全屏 / 拖拽（`PlayerOverlay.OnVideoPointerPressed`）。缓冲胶囊同。
5. **加载块的次级文案要限制宽度**（`MaxWidth` + `TextTrimming`），频道名可能很长。
6. `Error` 可能为 `null`（理论上 `Failed` 前必写，但引擎路径建议加 `StringConverters.IsNullOrEmpty` 兜底）。
7. 隐藏加载块时**不要**在 `Playing` 的第一时间硬切——保留 §3 的 250ms 淡出，它同时兜住「`Playing` 早于首帧」的窗口期。
8. 若要把「等首帧」做扎实（可选增强）：`MpvVideoSurface.RenderedFrames`（`MpvVideoSurface.cs:17,48`）已在计数但**未暴露给 VM**；把 `RenderedFrames>0` 作为隐藏条件。**前提**：必须能区分「无视频轨（纯音频）」，否则纯音频永不出帧 → 永久加载提示。不满足就退回 250ms 淡出方案。

### 7.5 需要绑定的属性一览

| 绑定 | 来源 | 用在 |
|---|---|---|
| `IsLoading` | 新增（派生） | 加载块 `IsVisible` |
| `IsLive` | 新增 | 加载主文案（`正在连接频道…` / `正在加载视频…`） |
| `Title` | 已有（`PlayerViewModel.cs:145`） | 加载块次级文案 |
| `BufferingHintVisible` | 新增（计时派生） | 缓冲胶囊 `IsVisible`/`Opacity` |
| `IsFailed` | 新增（派生） | 失败块 `IsVisible` |
| `Error` | 已有（`:171`，当前无绑定） | 失败块主文案 |
| `Visible` | 已有 | 现有控制层，不改 |

---

## 8. 文案总表（中文，界面统一口径）

| 场景 | 文案 | 字号 / 颜色 |
|---|---|---|
| `Resolving` | `正在获取播放地址…` | 13 / `TextPrimaryBrush` |
| `Loading` + 直播 | `正在连接频道…` | 13 / `TextPrimaryBrush` |
| `Loading` + 点播 | `正在加载视频…` | 13 / `TextPrimaryBrush` |
| 加载块次级 | `{Title}`（频道名 / 媒体名） | 11.5 / `TextTertiaryBrush` |
| `Buffering` | `正在缓冲…` | 12 / 白（在 `#CC000000` 胶囊上） |
| `Failed` 正文 | `{Error}`（即 `无法播放：{原因}`） | 12.5 / `TextSecondaryBrush` |
| `Failed` 提示 | `请稍后重试，或切换频道/线路。` | 11.5 / `TextTertiaryBrush` |

风格：与既有 `正在加载频道…`、`正在加载第 N 页…` 保持一致——**「正在 + 动词 + 对象 + …」**，全角省略号，不用感叹号。

---

## 9. 明确不做（避免范围蔓延）

- 不加圆环 spinner、不新增图标资源、不新增颜色。
- 不为起始加载做 `Buffering` 分支（那是死路径）。
- 不加「重试」按钮（无对应命令）。
- 不改底部控制条的外观与自动隐藏逻辑（只保证提示不遮挡）。
- 不在全屏/小窗之间切换动画策略（尺寸固定，差异可忽略）。
固定，差异可忽略）。
