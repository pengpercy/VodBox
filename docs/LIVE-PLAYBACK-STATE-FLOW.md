# 直播播放状态时序（只读调查事实文档）

> 范围：首页「最近观看」点一个直播频道 → 独立播放窗口打开 → 出第一帧画面，之间的完整 `PlaybackState` 时序。
> 方法：纯静态阅读源码。**未 build / 未 test / 未 typecheck**。每一条结论都指向文件与行号。
> 约定：**【代码事实】**= 直接读到的代码行为；**【推断】**= 基于代码的推理，未运行验证。行号以调查时工作区文件为准。

---

## 0. 一图速览（真实时序）

```
用户点「最近观看」里的直播频道
  HomeViewModel.Resume (HomeViewModel.cs:239)
    → MainViewModel.ResumeEntry(entry)            (MainViewModel.cs:131)
        → LiveViewModel.TryPlayByUri(entry.MediaId) (LiveViewModel.cs:500)
            → LiveViewModel.PlayChannel(channel)     (LiveViewModel.cs:459)
                → PlayerViewModel.Play(request)       (PlayerViewModel.cs:294)
                    → PlayerViewModel.PlayResolvedAsync (PlayerViewModel.cs:297)
   ── UI 线程同步段（一个 RunOnUiAsync 内）──         (PlayerViewModel.cs:302-307)
        _intent++ ; _current=null ; Visible=true ; Error=null
        State = Resolving                             (PlayerViewModel.cs:307)
   ── MainWindow 观察 Visible/Title 变化 ──
        SyncPlayerWindow() → new PlayerWindow().Show() (MainWindow.axaml.cs:187-204)
        → PlayerOverlay 入树：装渲染面、NotifyVideoSurfaceReady (PlayerOverlay.axaml.cs:200-247)
   ── 引擎 OpenAsync（跨线程，异步）──              (MpvEngine.cs:130)
        waitForVideoSurface: 等渲染面 ≤8s            (MpvEngine.cs:143-162)
        设 user-agent/volume/speed/loadfile         (MpvEngine.cs:166-188)
        SetSnapshot(Loading)                         (MpvEngine.cs:169)   ← State=Loading
   ── mpv 事件泵 100ms 轮询 ──                      (MpvEngine.cs:250-303)
        file-loaded 事件 → State = Playing           (MpvEngine.cs:322-330)
        → StateChanged 事件 → PlayerViewModel.State = Playing (PlayerViewModel.cs:215)
```

**从窗口出现到出第一帧，PlayerViewModel 上真实会经过的状态：**
`(旧值) → Resolving → Loading → Playing`。**没有 `Buffering`**（见第 2 节：起始阶段 `Buffering` 是死路径）。
窗口显示（`Visible=true`）与 `Resolving` 在**同一个同步块**里发生（PlayerViewModel.cs:305、307），所以在窗口出现的第一刻，`State` 已经是 `Resolving`。

---

## 1. 直播完整状态链路（逐环节）

### 1.1 点击入口 → PlayerViewModel.Play
| # | 环节 | 文件:行 | 动作 | 设置的状态 |
|---|------|---------|------|-----------|
| 1 | 历史卡片点击 | `HomeViewModel.cs:239` `Resume(HistoryEntry)` | 转调 `_main.ResumeEntry(entry)` | — |
| 2 | 分流 | `MainViewModel.cs:131` `ResumeEntry` | `case "live"` → `Live.TryPlayByUri(entry.MediaId)` | — |
| 3 | 按地址找频道 | `LiveViewModel.cs:500` `TryPlayByUri` | `Groups.SelectMany(...).FirstOrDefault(uri==mediaId)`；找不到则按 `Loading` 给提示 | — |
| 4 | 起播 | `LiveViewModel.cs:459` `PlayChannel` | 写日志、`SetCurrent(channel)`、`_lineIndex=0`，构造 `PlaybackRequest{IsLive=true, SourceKey="live"}` 交给 `_main.Player.Play(...)` | — |
| 5 | 入口 | `PlayerViewModel.cs:294` `Play(request)` | `PlayResolvedAsync(_ => Task.FromResult(request))` | — |

**【代码事实】** `TryPlayByUri` 用的是**频道地址（URI）**匹配历史 `MediaId`，不是频道名（LiveViewModel.cs:500-503）。历史里直播的 `MediaId` 就是 `channel.Uris[0]`（LiveViewModel.cs:481）。

### 1.2 PlayResolvedAsync 的 UI 同步段（状态第一次跃迁）
`PlayerViewModel.cs:297 PlayResolvedAsync`：

- `PlayerViewModel.cs:302` `await _main.RunOnUiAsync(() => {...})` —— 整段在 UI 线程跑。
- `PlayerViewModel.cs:303` `intent = ++_intent;`（新会话号）
- `PlayerViewModel.cs:304` `_current = null; VideoAspectRatio = null;`
- `PlayerViewModel.cs:305` `Visible = true;` ← **这是播放窗口出现的信号**
- `PlayerViewModel.cs:306` `Error = null;`
- `PlayerViewModel.cs:307` `State = PlaybackState.Resolving;`
- `PlayerViewModel.cs:308-...` `open = _coordinator.OpenAsync(async ct => {...})`（异步，不在此阻塞；`Play` 里是 `_ =` 即发即忘，`PlayerViewModel.cs:294`）

**【代码事实】** `Visible=true` 与 `State=Resolving` 在同一同步代码块，顺序固定：先 `Visible`（305）后 `State`（307）。
**【推断】** 窗口创建（订阅 `Visible`）与 `State=Resolving` 几乎同一帧发生，因此用户看到窗口时 `State` 已是 `Resolving`——对「加载提示」而言，**窗口一出现就可以立刻显示提示**，不存在「窗口已现但状态还是旧的 Idle」的窗口期。

### 1.3 窗口创建（谁让窗口出现）
- `MainWindow.axaml.cs:154` `OnPlayerVisibilityChanged`：当 `Player.Visible` 或 `Player.Title` 变化 → `SyncPlayerWindow()`。
- `MainWindow.axaml.cs:187` `SyncPlayerWindow`：`Visible==false` 则不建；已存在则提到最前；否则 `new PlayerWindow{DataContext=main}` + `player.Show()`（`MainWindow.axaml.cs:199-203`）。
- 窗口 `PlayerWindow.axaml` 内容只有一个 `<v:PlayerOverlay x:Name="PlayerView"/>`（`PlayerWindow.axaml:12`）。
- `PlayerOverlay` 入树（`OnAttached`，`PlayerOverlay.axaml.cs:125`）：`TryInstallSurface()`（`:224`）装 `MpvVideoSurface`，表面 Ready 后 `OnSurfaceReady`（`:241`）调 `App.Services.Player.NotifyVideoSurfaceReady()`（`:244`）。

**【推断】** 从此到 `file-loaded` 之间，`VideoHost` 只是一个空 `Panel`（`PlayerOverlay.axaml:76`），黑底来自它下面的 `<Rectangle Fill="#000000"/>`（`PlayerOverlay.axaml:74`）与窗口 `TransparencyBackgroundFallback="Black"`（`PlayerWindow.axaml:9`）。

### 1.4 引擎 OpenAsync（第二次跃迁：Loading）
`MpvEngine.cs:130 OpenAsync(PlaybackRequest, sessionId, token)`：

1. `MpvEngine.cs:131-134` 记录 `open` 事件日志。
2. `MpvEngine.cs:135-142` 校验 URI scheme 与请求头（Cookie 等非 UA/Referer 直接 throw）。
3. **【关键】`MpvEngine.cs:143-162` `if (_waitForVideoSurface)`**：先 `Initialize()`，再 `await _videoSurfaceReady.Task.WaitAsync(8秒)`。
   - GUI 引擎就是用这个构造的：`AppServices.cs:31` `Player = new MpvEngine(waitForVideoSurface: true);`
   - 超时 8 秒会抛 `TimeoutException`（MpvEngine.cs:153-157）→ 冒泡到 `PlayResolvedAsync` 的 catch → `Failed`。
4. `MpvEngine.cs:163-...` `ExecuteAsync`（信号量串行）：`client.Command("stop")` 清空旧文件（MpvEngine.cs:165-166）。
5. `MpvEngine.cs:167` 复位 `_session/_loaded/_paused/_buffering`。
6. **`MpvEngine.cs:169` `SetSnapshot(new PlaybackSnapshot(PlaybackState.Loading, 0, 0, false));`** ← **State = Loading**。
7. `MpvEngine.cs:170-188` 设置 UA/Referer/pause/volume/speed/aspect，然后 `loadfile uri replace`。
8. `MpvEngine.cs:189` 记录「已提交 loadfile」。

**【代码事实】** `Loadfile` 提交后 OpenAsync 就返回了；**真正的画面要等事件泵**。

### 1.5 事件泵 → Playing（第三次跃迁：出画面）
`MpvEngine.cs:250 PumpAsync`：`PeriodicTimer(100ms)` 轮询 `PollEvent()`，逐个 `Process`。

- `MpvEngine.cs:313-316` `Process` 里 `item.Id==8`（`MPV_EVENT_FILE_LOADED`）：
  - `_loaded=true; RefreshTracks();`
  - 若 `_startPosition>0` 先 seek（直播 `StartPositionMs=0`，不 seek）
  - `return snapshot with { State = CurrentState(), Error = null };`（`MpvEngine.cs:330`）
  - `CurrentState()` = `_paused ? Paused : _buffering ? Buffering : Playing`（`MpvEngine.cs:360`）。刚加载时 `_paused=_buffering=false` → **`Playing`**。
- `SetSnapshot`（`MpvEngine.cs:392`）写快照并 `StateChanged?.Invoke(...)`。
- `PlayerViewModel.cs:203 engine.StateChanged` 订阅回调：`_main.RunOnUi(...)` → `State = evt.Snapshot.State`（`PlayerViewModel.cs:215`），同时刷新 `Position/Duration/Error/VideoAspectRatio`（216-219）。

**【代码事实】** `file-loaded` 是**唯一的「Loading→Playing」跃迁点**。在此之前 `State` 一直是 `Loading`。
**【推断，重要】** `file-loaded`（网络 demuxer 已就绪）**早于**第一帧真正上屏。真正的画面由 `MpvVideoSurface.OnOpenGlRender`（`MpvVideoSurface.cs:41-48`）渲染，`RenderedFrames` 计数（`MpvVideoSurface.cs:17,48`）**没有暴露给 ViewModel**。所以：**`State==Playing` 不代表已经在出画面，而「到第一帧」这段黑窗期在 ViewModel 侧完全没有状态标记**。这正是本次要补的提示要覆盖的区间。

### 1.6 每个状态的持续时间由什么决定
| 状态 | 从哪来 | 持续到 | 时长决定因素 |
|------|--------|--------|--------------|
| `Resolving` | PlayerViewModel.cs:307 | 引擎 `Loading` 事件回填（PlayerViewModel.cs:215） | 解析链路 + 等渲染面 + 等首次加载。直播 `Resolution` 默认 `Direct`（LiveViewModel 组装的 request 未设 `Resolution`，默认 0 = `Direct`，见 `Models.cs:148`），故 **无 JSON 解析步骤**；**【推断/待确认】** `PlayChannel` 是**同步**调用（LiveViewModel.cs:498 `_main.Player.Play(...)` 是 `void`），所以几乎立刻进入 `Resolving`。 |
| `Loading` | MpvEngine.cs:169 | `file-loaded` 事件（MpvEngine.cs:330） | **= 等渲染面握手(≤8s) + mpv 打开直播流 + 首个 file-loaded**。直播是网络流，这段通常就是用户抱怨的「黑窗等待」。 |
| `Playing` | 同上 | Ended/Failed/Paused/Buffering | 播放中 |

**【推断】** 「等一会儿才出画面」的黑窗期 ≈ `Loading` 的全部时长，可能还含 `Playing` 之后到真正首帧的少量时间（见 1.5 的推断）。

---

## 2. `Buffering` 在直播场景会不会出现？——起始阶段不会（死路径）

### 2.1 `_buffering` 谁设、什么条件
- 字段：`MpvEngine.cs:19` `private bool _loaded, _paused, _buffering;`
- 观察注册：`MpvEngine.cs:99` `client.Observe("paused-for-cache", 5, MpvPropertyFormat.Flag);`
- 赋值：`MpvEngine.cs:354` `case "paused-for-cache" when item.PropertyFlag is { } buffering: _buffering = buffering; break;`
- 使用：`MpvEngine.cs:360` `CurrentState() => _paused ? Paused : _buffering ? Buffering : Playing;`
- 仅在 `_loaded == true` 时才可能通过 `CurrentState()` 生效：`MpvEngine.cs:357` `return _loaded ? snapshot with { State = CurrentState() } : snapshot;`

### 2.2 起始加载阶段：`Buffering` 是死路径（明确结论）
**【代码事实】** 在 `file-loaded` 之前 `_loaded == false`（`MpvEngine.cs:167` 复位，`MpvEngine.cs:313` 才置真）。而 `paused-for-cache` property-change 走 `MpvEngine.cs:357` 的 `_loaded ? ... : snapshot` 分支——`_loaded` 为 false 时**直接返回未改动的 snapshot（State 保持 `Loading`）**。因此**在等帧阶段，即使 mpv 报了 `paused-for-cache`，state 也停在 `Loading`，绝不会变成 `Buffering`。**

**结论（明确）：在「开播 → 首帧」这个加载窗口内，`Buffering` 永远不出现。** 设计**不需要**为起始黑窗做 `Buffering` 状态；起始只用 `Resolving` + `Loading` 两个状态。

### 2.3 播放中途卡顿：`Buffering` 会出现，但**视图层收不到通知**（代码事实）
**【代码事实】** `file-loaded` 之后 `_loaded==true`，若 mpv 把 `paused-for-cache` 置真（网络卡顿），下一次 property-change 会走到 `MpvEngine.cs:357` → `CurrentState()` 返回 `Buffering`（`MpvEngine.cs:360`）→ `SetSnapshot` 触发 `StateChanged`。所以**播放中途卡顿确实会切到 `Buffering`**。

**但有两个会「吞掉」该通知的过滤点：**
1. `MpvEngine.cs:354` 的 `paused-for-cache` case 只有 `break`（354 行），**不 return**，随后落到 `MpvEngine.cs:357` 用 `CurrentState()` 生成新快照——所以**只有当 `paused-for-cache` 的 property-change 事件本身到达时**才会被观测到。若 mpv 是通过其他事件让 `_buffering` 变化（例如 property 值已在别处变过），则可能不触发。**【推断】** 具体触发频率依赖 mpv 事件细节，未运行验证。
2. **View 侧订阅在 `PlayerOverlay.axaml.cs:200 OnAttached`**（`DataContextChanged`/`AttachedToVisualTree`）。**【代码事实】** 播放中途 `State` 变化（含 `Buffering`）只在 `PlayerViewModel` 的 `StateChanged` 回调里改 `State`（PlayerViewModel.cs:215），而 ViewModel 的 `PropertyChanged` 是全程在线的；`State` 是 `[ObservableProperty]`（`PlayerViewModel.cs:144`），会正常通知。

**综合结论：**
- **起始黑窗（本次需求）**：`Buffering` 是**死路径**——只会有 `Resolving → Loading → Playing`。**设计起始提示不需要 Buffering 分支。**
- **播放中途卡顿**：`Buffering` 理论上会出现且 `State` 能通知到视图，但**当前没有任何 UI 绑定 `State`**（见第 3 节），所以「能收到通知」只停留在 ViewModel/`PropertyChanged` 层，**视图层目前看不到**。
- **【推断】** 若只做「起始加载提示」，可完全不依赖 `Buffering`；若以后想做「播放中缓冲提示」，`Buffering` 是可用状态，但需新增绑定。

---

## 3. 等帧期间窗口显示了什么——**空白的黑窗，没有任何状态驱动提示**

**【代码事实】** 逐项检查 `PlayerOverlay.axaml`：

- 背景：`PlayerOverlay.axaml:74` `<Rectangle Fill="#000000"/>` + `:76` `<Panel x:Name="VideoHost"/>`（mpv 画面最终挂在这里）+ `:77` 弹幕层。
- 控制层整体可见性由 `Visible` 驱动：`PlayerOverlay.axaml:80` `<Grid ... IsVisible="{Binding Visible}">`。
- **全文没有任何** `State` / `Loading` / `Resolving` / `Buffering` / `Error` 的绑定（grep 结果：`PlayerOverlay.axaml` 里 `State/Error/Buffering/Resolving/Loading/Skeleton` 命中数 = 0；唯一命中的是 `IsPlaying`，用于播放/暂停按钮图标，`PlayerOverlay.axaml:182-183`）。
- 唯一的「状态感」元素是 **中央 toast**（`PlayerOverlay.axaml:118-127`，`IsVisible="{Binding ShowToast}"`），但它只服务倍速/跳片头提示（`ShowToast/ToastText`，`PlayerViewModel.cs:175-176`），**不用于加载**。
- 没有转圈/进度/占位/spinner/skeleton。

**【代码事实】** `PlayerOverlay.axaml.cs` 里与「可见性/状态」相关的逻辑：
- `OnPlayerChanged`（`PlayerOverlay.axaml.cs:117`）：只在 `VideoAspectRatio` 变化时 `FitWindowToVideo()`（`:119`），在 `Visible==false` 时 `RestorePlayerWindow()`（`:120`），**没有对 `State` 做任何事**。
- `UpdateControls`（`PlayerOverlay.axaml.cs:214`）：`!vm.Visible || (!_pointerOutside && vm.State != PlaybackState.Playing)` 时**保持控制条常显**（`:224`）。也就是说：**在 `Resolving`/`Loading` 期间，底部控制条（时间 00:00、进度、播放键）是可见的**——这是等帧期间屏幕上**唯一**的视觉元素，且它长得像「已就绪的待播放状态」，与「正在加载」不符。

**【代码事实】** `PlayerWindow.axaml.cs`：无任何状态/加载相关 UI 逻辑；只有键盘、Closing 里设置 `main.Player.Error`（`PlayerWindow.axaml.cs:42`）。

**结论：等帧期间窗口 = 纯黑背景 + 底部常显控制条（标题、00:00 进度、播放键）。没有任何「加载中/缓冲中」反馈。** 与用户反馈一致。

---

## 4. `PlayerViewModel` 状态/错误/标题相关公开面

以下为视图层**可以绑定**的属性（`ObservableObject`，`[ObservableProperty]` 会生成 `Xxx` 通知属性）：

| 属性 | 定义位置 | 含义 | 视图可绑定 |
|------|----------|------|-----------|
| `State` (`PlaybackState`) | `PlayerViewModel.cs:144` | 当前播放状态（Idle/Resolving/Loading/Playing/Paused/Buffering/Ended/Failed） | ✅ `[ObservableProperty]` |
| `Title` | `:145` | 顶栏标题（直播=频道名，两处都设 `request.Title`；见 LiveViewModel.cs:475/494 与 PlayerViewModel.cs:342） | ✅ |
| `Subtitle` | `:173` | 顶栏副标题（集数 · 站源；直播为 `request.SourceName`=「直播」） | ✅ |
| `Error` (`string?`) | `:171` | 错误文案（失败时为 `无法播放：{message}`，PlayerViewModel.cs:368） | ✅ 但**当前无任何 View 绑定** |
| `Visible` | `:170` | 播放窗口是否应存在（`MainWindow` 据此 `Show/Close`） | ✅（被 MainWindow、Overlay 订阅） |
| `VideoAspectRatio` (`double?`) | `:31`/`_videoAspectRatio` | 视频显示比例；变化时 Overlay 缩窗适配 | ✅ |
| `IsPlaying`（只读计算属性） | `:128` | `State is Playing or Buffering`（注意：**`Loading/Resolving` 时为 false**） | ✅（仅用于播放/暂停图标，`:182-183`） |
| `Position` / `Duration` | `:146-147` | 进度（直播 Duration 常为 0） | ✅ |
| `SeekPositionSeconds` | `:148` | 拖动条值 | ✅ |
| `ControlsVisible` | `:126` | 控制条显隐 | ✅ |
| `ShowToast` / `ToastText` | `:175-176` | 中央 toast 显隐/文案 | ✅ |
| `IsMuted`（只读） | `:24` | `Volume==0` | ✅ |
| `Volume` / `Rate` | `:168-169` | 音量/倍速 | ✅ |
| `AlwaysOnTop` / `CompactMode` | `:124-125` | 置顶/迷你窗 | ✅ |
| `AspectRatio` (`double?`, `get` only) | `:23` | 用户选择的目标比例（非视频本身） | ✅ |
| `Playlist` / `PlaylistIndex` / `HasPreviousEpisode` / `HasNextEpisode` | `:90`/`_playlistIndex`/`:113-114` | 播放列表 | ✅ |
| `CurrentSessionId`（只读） | `:411` | `_intent`，当前会话号（Async 操作防串号用） | ✅ |
| `IsSeeking`（只读） | `:127` | 是否正在拖动 | ✅ |
| `Danmaku` / `DanmakuEnabled` / `DanmakuOpacity` / `DanmakuLimit` | `:50`/`:67` 等 | 弹幕 | ✅ |
| `OpeningSkipSeconds` / `EndingSkipSeconds` / `Incognito` / `AutoNext` / `PreferredParser` / `SubtitleDelay` / `SubtitleFontSize` | 多处 | 偏好 | ✅ |

**给设计的要点：** 若要做「加载中」提示，最直接可绑的是 **`State`**（`Resolving`/`Loading` 即加载期），其次可加一个只在 `Resolving|Loading` 为 true 的**只读计算属性**（如 `IsLoading`），模式与现有 `IsPlaying`（`:128` + `OnStateChanged` 里 `OnPropertyChanged(nameof(IsPlaying))`，`:129-130`）一致。

---

## 5. 通知机制（改 `State` 后视图怎么知道）

- **机制**：`PlayerViewModel : ObservableObject`（`PlayerViewModel.cs:9`）。`State` 是 `[ObservableProperty] private PlaybackState _state`（`:144`），会生成 `public PlaybackState State {get;set;}` + `PropertyChanged` 通知。**结论：View 可以直接 `{Binding State}`。**
- **附带通知**：`partial void OnStateChanged(PlaybackState value)`（`:129`）里除了打日志，还会 `OnPropertyChanged(nameof(IsPlaying))`（`:130`）。所以如果新增 `IsLoading` 之类的派生属性，应在同一处补 `OnPropertyChanged`。
- **谁在改 `State`**：
  1. 引擎事件回调：`PlayerViewModel.cs:203` `engine.StateChanged += ...` → `_main.RunOnUi(...)`（`:206`）→ `State = evt.Snapshot.State`（`:215`）。
  2. `PlayResolvedAsync`：`:307` 设 `Resolving`；错误分支 `:369` 设 `Failed`；`Close` `:490` 设 `Idle`。
- **跨线程处理**（关键，决定 Developer 能不能直接绑定）：
  - `MainViewModel.RunOnUi(Action)`（`MainViewModel.cs:155`）：`uidispatcher==null` 或已在 UI 线程则直接执行，否则 `uidispatcher.Post`（**即发即忘**）。
  - `MainViewModel.RunOnUiAsync(Action)`（`:161`）：`InvokeAsync`（**等待完成**）。
  - `uidispatcher` 由 `MainWindow.OnDataContextChanged` 注入：`vm.AttachDispatcher(Dispatcher.UIThread)`（`MainWindow.axaml.cs:113`）。`MainViewModel.AttachDispatcher`（`:169`）。
  - **结论**：`State` 的所有写入都经 `RunOnUi`/`RunOnUiAsync` 回到 UI 线程（引擎回调来自 mpv 事件泵线程 `MpvEngine.cs:256 Task.Run(PumpAsync)`）。**View 直接 `{Binding State}` 是安全的，无需在 View 侧再套 Dispatcher。** 注意 `RunOnUi` 是 `Post`（异步），所以从触发到 UI 更新有极小的延迟（**推断**：对加载提示无影响）。
  - 测试佐证：`ViewModelRaceTests.BackgroundEntryPointsAndCompletionsUpdateOnlyOnUiThread`（`ViewModelRaceTests.cs` 内）断言所有 `PropertyChanged` 都在 UI 线程。

---

## 6. 可测试入口点（给主 Agent / Evaluator 写无头测试）

### 6.1 已有测试基建（可直接复用）
- **假引擎 `Engine : IPlaybackEngine`**：`tests/VodBox.PreviewTests/ViewModelRaceTests.cs:3068` 起（`private sealed class Engine`）。
  - `Opened`（`:3070`）记录每次 `(Request, Session)`；`EmitCurrent(state, pos, dur)`（`:3085`）/ `Emit(session, state, pos, dur)`（`:3086`）**手动投递状态**。
  - `OpenAsync` 默认在 `Open` 委托完成后 `Emit(sessionId, Playing, 0, 0)`（`ViewModelRaceTests.cs` 内，`Engine.OpenAsync`）。
  - `Open`（`:3070`）是可替换委托 → **可用来卡住「Loading 期」**。
- **`Context`**（`:2996`）：装配 `AppServices` + `MainViewModel` + `PlayerViewModel(Engine, Store, Main)` + `AttachDispatcher(Dispatcher.UIThread)`；`Main.Player` 被替换为使用假 `Engine` 的实例（`:3013-3015`）。`Configured` 用 `new MainViewModel(_services, true, key=>..., Store)`。
- **`LoadingVisualTests.cs`**：演示了「骨架可见/消失」渲染断言模式（`Border.Classes.Contains("skeleton")` + `window.CaptureRenderedFrame()`）。
- `AppServices.CreateDesignTime()`（`AppServices.cs:37`）/ `MpvEngine.DesignDisabled()`（`MpvEngine.cs:35`）：永不加载 libmpv 的空引擎，适合纯 VM 测试。

### 6.2 如何伪造「加载中 → 播放中」时序（建议写法）
**目标**：验证提示在 `Resolving/Loading` 出现、在 `Playing` 消失。
```csharp
using var context = new Context();
var vm = context.Main.Player;
var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
context.Engine.Open = _ => gate.Task;          // 卡住引擎，停在「开播中」
var play = vm.PlayResolvedAsync(_ => Task.FromResult(
    new PlaybackRequest { Uri = "https://live.example/x", Title = "频道", SourceKey = "live", IsLive = true }));

// 此刻 State 应为 Resolving（窗口已 Visible）
Assert.Equal(PlaybackState.Resolving, vm.State);
// 【推断】假引擎 OpenAsync 只在 Open 完成回调后才 Emit Playing，
//          所以「Loading」在纯假引擎下不会被自然触发，需要手动补一步：
context.Engine.EmitCurrent(PlaybackState.Loading, 0, 0);
Assert.Equal(PlaybackState.Loading, vm.State);        // 提示应出现
gate.SetResult();                                      // 放行 → 引擎 Emit(Playing)
await Done(play);
Dispatcher.UIThread.RunJobs();
Assert.Equal(PlaybackState.Playing, vm.State);         // 提示应消失
```
**注意（代码事实）：**
- 假 `Engine.OpenAsync`（`ViewModelRaceTests.cs` 内）**不会**自己发 `Loading`——它只在 `Open` 完成后发 `Playing`。所以要测 `Loading` 期，必须像上面那样**手动 `Emit(Loading)`**，或给 `Engine` 加一个 `Loading` 闸门。
- **更贴近真实的替代**：直接用 `MpvEngineTests` 的假 `IMpvClient`（`tests/VodBox.Tests/MpvEngineTests.cs:179 Client`）驱动 `MpvEngine`，`Client.Command("loadfile",...)` 会注入 `MPV_EVENT_FILE_LOADED(8)`（`MpvEngineTests.cs:195-201`）→ 引擎自然从 `Loading` 变 `Playing`。可在此基础上面向 `MpvEngine.Snapshot.State` 断言时序（`Loading` → `Playing`）。
- 直播场景用例模板已存在：`ViewModelRaceTests.Live_LineSwitchCyclesAndKeepsStableChannelHistoryKey`（`ViewModelRaceTests.cs` 内）演示了 `live.PlayChannel(channel)` + `context.Engine.Opened[^1].Request.IsLive` 的断言方式，可照抄来断言「点击 → Resolving」。
- **【推断】** 现有 `Engine`/`Context` **没有**任何「Loading 事件」的内建触发点，也**没有** `Buffering` 用例；如要覆盖 `Buffering`，需手动 `Emit(PlaybackState.Buffering, ...)`。

### 6.3 关键断言数据点（无头）
- `vm.Visible == true`（`:305`）与 `vm.State == Resolving`（`:307`）同时成立。
- `vm.Error == null` 在起播时被清（`:306`）。
- 引擎侧：`context.Engine.Opened[^1].Request.IsLive == true`、`SourceKey == "live"`、`Uri == channel.Uris[0]`。

---

## 7. 失败路径

### 7.1 `Failed` 谁设置
两条独立来源：
1. **引擎层**：`MpvEngine.Process` 的 `MPV_EVENT_END_FILE`（id=7）：
   - `MpvEngine.cs:334` 前置 stop 特判（见下）；
   - `MpvEngine.cs:338` `State = EndReason==0 ? Ended : EndReason==4 ? Failed : Idle`；
   - `MpvEngine.cs:339` `Error = EndReason==4 ? $"mpv 无法播放此媒体（错误 {EndError}）。" : null`。
   - 另：`loadfile` 直接抛异常时 `MpvEngine.cs:313` 设 `Failed + Error=ex.Message`；事件泵异常 `MpvEngine.cs:296` 也设 `Failed`。
2. **VM 层**：`PlayResolvedAsync` catch（`PlayerViewModel.cs:359-371`）：`Error = $"无法播放：{error.Message}"`（`:368`）→ `State = Failed`（`:369`）→ 调 `_main.Live.HandlePlaybackFailure(_current, intent)`（`:370`）。
   - **顺序刻意**：先写 `Error` 再切 `State`（注释见 `:367`），因为 `OnStateChanged` 会读 `Error`。

### 7.2 `Error` 文案怎么产生
- 引擎：`mpv 无法播放此媒体（错误 {EndError}）。`（`MpvEngine.cs:339`）或 `InnerException.Message`。
- VM 包装：`无法播放：{error.Message}`（`:368`）。
- UI 呈现：**【代码事实】** `Error` **没有被任何 View 绑定**（`PlayerOverlay.axaml` / `PlayerWindow.axaml` grep 无 `Error` 绑定）。失败时用户**看不到原因**（只有日志 `VodBoxLog.Error`，`:366`）。

### 7.3 直播全线路失败（`LiveViewModel.HandlePlaybackFailure`）
`LiveViewModel.cs:17-31`：
- 守卫：`_lastFailedIntent==intent` 去重、`SourceKey=="live" && IsLive`、有 `CurrentChannel`、频道未锁定、`request.MediaId==channel.Uris[0] && request.Uri==channel.Uris[_lineIndex]`（`:20`）。
- 计数 `++_failedLines`（`:25`）：
  - **≥ 线路数** → 记错误日志，`_main.StatusMessage="该频道全部线路播放失败，请手动重试或切换频道"`，返回（`:27-28`）。
  - **否则** → 自动换线 `_lineIndex=(_lineIndex+1)%Uris.Count`，`StatusMessage="直播线路失败，尝试线路 N"`，`PlayCurrentLine(channel)` 重新 `Play`（`:30-31`）。
- 触发点：引擎自动失败（`PlayerViewModel.cs:370`）与 `PlayResolvedAsync` catch（同样 `:370`）都会调它。
- **【推断】** 全线路失败时，播放窗口可能仍开着（`Visible` 未复位），但 `State=Failed` + 无画面 + 无 UI 提示 → 用户看到的是「黑窗且再无反应」。这进一步支持「需要可见的失败/加载提示」。

---

## 8. 给设计的结论摘要

1. **起始黑窗真实状态序列 = `Resolving` → `Loading` → `Playing`**（窗口一出现即 `Resolving`，PlayerViewModel.cs:305/307；`Loading` 在 MpvEngine.cs:169；`Playing` 在 file-loaded，MpvEngine.cs:330）。
2. **`Buffering` 在起始加载期是死路径**（`_loaded==false` 时 `paused-for-cache` 被丢弃，MpvEngine.cs:357）→ **加载提示不需要 Buffering 分支**。播放中途卡顿会有 `Buffering` 且能到 VM，但当前视图无绑定。
3. **视图层目前什么都看不到**：`PlayerOverlay.axaml` 无任何状态驱动提示，只有黑底 + 常显控制条（`UpdateControls`，PlayerOverlay.axaml.cs:224）。`Error` 也无人绑定。
4. **建议只绑 `State`（或新增 `IsLoading = State is Resolving or Loading`）**，通知机制现成（`ObservableObject` + `OnStateChanged` 模式，PlayerViewModel.cs:129-130/144），跨线程已由 `RunOnUi` 处理，View 直接绑定安全。
5. **可测**：用 `ViewModelRaceTests` 的假 `Engine`/`Context` 手动 `Emit(Loading)`/`Emit(Playing)` 验证提示出现与消失；或直接用 `MpvEngineTests` 的假 `IMpvClient` 走真实 `MpvEngine` 时序。
��。
序。
 时序。
