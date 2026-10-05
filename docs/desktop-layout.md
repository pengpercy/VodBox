# 参考 FongMi/TV 的 PC 布局

按用户要求，布局参考 FongMi/TV，而非只参考其内容源接口。核对版本为 `c616c0aa3613e87529791587a9f71b78c278c991`；只读取源码，没有编译或运行 Java。

参考入口：

- [leanback 首页](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/leanback/res/layout/activity_home.xml)：顶部品牌和导航、独立内容浏览区域。
- [点播分类页](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/leanback/res/layout/activity_vod.xml)与 [mobile 点播页](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/res/layout/fragment_vod.xml)：横向分类导航、分类内容区域。
- [列表／网格切换](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/java/com/fongmi/android/tv/ui/fragment/TypeFragment.java)：根据风格选择 LinearLayoutManager 或 GridLayoutManager，列数由 Product 决定。
- [宽屏视频布局](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/res/layout-sw600dp/activity_video.xml)：独立视频区域，以及旁边的名称、信息和播放内容。

## 页面与组件职责

保留 PC 侧边导航、鼠标与键盘操作、Design.DataContext、编译绑定、扩展标题栏和 Mica／透明效果。分类改成可水平滚动的导航，播放器底部控制按可用宽度换行。

窗口最小宽度为 800 DIP。浏览与播放详情使用独立页面区域；导航到首页、点播、直播、历史、收藏或设置会回到相应浏览页，打开媒体或详情则显示播放页。返回浏览保留播放会话。播放页内部宽度至少 900 DIP 时视频与详情并列，更窄时上下排列。主窗口宽度低于 1180 DIP 时收紧侧边导航和边距。断点按 PC 内容需求确定，没有照搬 Android 的 600dp。

继续核对同一版本的 `HomeActivity`、`VodFragment`、`TypeFragment`、`VideoActivity` 和设置布局后，将页面容器、分类内容、独立播放页与设置子页分开。参考源码：

- [HomeActivity 页面管理](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/java/com/fongmi/android/tv/ui/activity/HomeActivity.java)及 [首页容器布局](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/res/layout/activity_home.xml)。
- [VodFragment 分类入口](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/java/com/fongmi/android/tv/ui/fragment/VodFragment.java)和 [TypeFragment 列表布局](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/res/layout/fragment_type.xml)；列表选片进入 VideoActivity。
- 宽屏视频布局通过 include 复用 widget、control 和 progress；播放器设置、弹幕设置另有独立 Fragment／Activity。VodBox 借鉴职责与组合方式，保留 Avalonia 的 PC 交互及常驻原生表面。

`MainWindow.axaml` 现在只组织窗口外壳、导航、页面区域和公共地址入口。`Views/` 中分别是 Home、SourceConfiguration、Library、Live、History、Favorites、Settings、Playback 和 PlaybackControls；Playback 组合 DetailSummary、EpisodeBrowser、Programme，Settings 组合 General、Playback、Danmaku 三个设置子页。共有 15 个独立视图，每个都有 Design.DataContext 和编译绑定。海报生命周期与列数计算归 LibraryView，选集事件归 EpisodeBrowserView，拖动进度和添加字幕归 PlaybackControlsView，文件选择归对应设置子页。共享主题放在 `Themes/DesktopTheme.axaml`。

目前页面共享 MainViewModel 的状态和应用服务，未声称已经拥有各自独立的 ViewModel。页面由明确类型静态组合，不依赖反射式 ViewLocator，也不会因导航重建原生播放器。

布局变化只修改列宽与可见性，不重建视频容器或原生播放器；切换视频内核也保留各自的实例。mpv 的弹幕在同一 UI 树绘制，LibVLC 使用透明附属窗口处理原生视频遮挡。

## 后续继续改造

内容浏览已改为按可用宽度计算 2–10 列的海报网格。外层虚拟化行，内层只创建当前行的海报；分页追加复用已有行，缩放重排保留 MediaCard 和选中状态。海报仍按可见性加载／取消／释放，数据模板使用编译绑定，可供设计器预览。

详情区已增加影片标题、备注、来源和可展开的滚动简介；播放线路与分集集中在下方。分集支持标题搜索、倒序和筛选数量提示，筛选不改变原始线路顺序，点击按钮才播放。批量填充结束后统一筛选，避免逐集追加时反复生成整个列表；分集控件仍虚拟化。

独立播放页已落地；全屏已隐藏窗口导航、地址、详情与分集，视频占满客户区，透明控制栏悬浮在底部（LibVLC 使用透明原生叠层），退出恢复原页面。F11／Esc 优先于控件输入处理；控制栏也可用鼠标切换全屏／窗口，并保留 0.25×–4× 倍速设置，退出保持倍速。全屏控制自动隐藏、进一步细分页面状态模型及完整三平台布局仍待完善。

随后验证 800／900／1280／1600 DIP、不同缩放率、鼠标／键盘导航及 Windows／Linux 真机。macOS 本地验证不能替代其他平台验收；没有运行 Actions。

已有 15 项独立视图预览、4 项不同宽度页面切换、内部宽窄布局与导航测试，以及 1,000 条内容的网格虚拟化、分页追加与选择重排测试；macOS x64 实际 AOT 主窗口也验证了播放中切换 900／1280 DIP、返回浏览／恢复播放页、保持原渲染表面和弹幕，未重建原生内核。整体回归 171 项，详细日志见 [播放验收](playback-testing.md)。

Debug 配置保持托管程序集与完整 XAML 资源：显式关闭 PublishAot/PublishTrimmed，启用 DebuggerSupport 和设计器所需 JsonSerializerIsReflectionEnabledByDefault。Release 指定 RID 时默认 NativeAOT/full trim，可用 DisableAOT=true 关闭。运行时业务 JSON 继续使用源码生成；所有页面保留 Design.DataContext。参考 [Downio 配置](https://github.com/pengpercy/Downio/blob/master/src/Downio/Downio.csproj)，不复制其关闭异常堆栈的选项。


## 截图反馈后的首页和配置流程修正（2026-10-05）

重新核对用户提供的首次启动、设置、配置完成三张截图，并阅读 FongMi/TV c616c0a 的 [HomeActivity](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/leanback/java/com/fongmi/android/tv/ui/activity/HomeActivity.java) 与 [SettingActivity](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/leanback/java/com/fongmi/android/tv/ui/activity/SettingActivity.java)。HomeActivity 的 getVideo 调用 homeContent，历史和功能入口独立；SettingActivity 默认聚焦点播，点播/直播各自打开配置对话框。用户截图版本与当前上游的功能按钮有区别，以截图表达的首页结构作为本轮布局依据。

原来的偏差是把首页与分类浏览合并、首次运行自动加载示例、播放源入口藏在通用设置底部。现在首次运行显示“影视”、时钟、七个功能入口、最近观看和更新推荐；没有配置时不自动加载示例、不显示分类、播放器或全局地址栏。已保存的配置仍会恢复，不清理用户数据。配置完成后标题显示当前点播源，推荐有独立海报网格；点播进入分类，分类/搜索不覆盖首页推荐。首页隐去 PC 侧栏，其他业务页保留侧栏和首页返回入口；PC 材质和标题栏继续沿用。

设置顶部改为点播、直播、播放设置和无痕模式。来源行展示配置摘要并打开独立 SourceConfigurationView，支持 URL/本地文件，成功加载才关闭对话框并返回首页，失败留在设置显示错误。两行目前编辑同一 VodBox 配置包，尚未实现 FongMi 的独立点播/直播订阅仓库；对话框明确提示整包替换。旧 TV 配置兼容仍不在本轮范围。

推荐通过 IContentProvider.GetHomeAsync 获取，默认回退到未指定分类的第一页；AppGet 的现有推荐协议直接复用，其他 provider 未因此获得新的站点推荐协议。首页最多展示 40 张推荐，按宽度分列并虚拟化行，海报复用时释放旧卡片；最近观看最多 8 条，目前使用标题按钮，无痕模式立即清空并阻止过期异步结果回填。最近观看海报卡片、壁纸、DoH/代理设置仍待继续实现；推送/投屏入口明确禁用，未伪装已有业务能力。

新增首次启动、设置配置成功/失败、分类搜索与推荐隔离、首页全屏返回测试，并将旧播放页测试显式切换到各自业务页。两个新视图均有 Design.DataContext 和编译绑定，无反射页面定位。
