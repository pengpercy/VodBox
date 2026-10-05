# 参考 FongMi/TV 的 PC 布局

按用户要求，布局参考 FongMi/TV，而非只参考其内容源接口。核对版本为 `c616c0aa3613e87529791587a9f71b78c278c991`；只读取源码，没有编译或运行 Java。

参考入口：

- [leanback 首页](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/leanback/res/layout/activity_home.xml)：顶部品牌和导航、独立内容浏览区域。
- [点播分类页](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/leanback/res/layout/activity_vod.xml)与 [mobile 点播页](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/res/layout/fragment_vod.xml)：横向分类导航、分类内容区域。
- [列表／网格切换](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/java/com/fongmi/android/tv/ui/fragment/TypeFragment.java)：根据风格选择 LinearLayoutManager 或 GridLayoutManager，列数由 Product 决定。
- [宽屏视频布局](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/mobile/res/layout-sw600dp/activity_video.xml)：独立视频区域，以及旁边的名称、信息和播放内容。

## 已落地的第一阶段

保留 PC 侧边导航、鼠标与键盘操作、Design.DataContext、编译绑定、扩展标题栏和 Mica／透明效果。分类改成可水平滚动的导航，播放器底部控制按可用宽度换行。

窗口最小宽度降至 800 DIP。宽度至少 1180 DIP 时浏览和播放详情并列；更窄时分为浏览页与播放详情页，通过顶部按钮切换。选择内容或打开媒体自动显示播放详情，返回浏览保留播放会话。断点使用 Avalonia DIP，根据 PC 内容需求确定，没有照搬 Android 的 600dp 数字。

布局变化只修改列宽与可见性，不重建视频容器或原生播放器；切换视频内核也保留各自的实例。mpv 的弹幕在同一 UI 树绘制，LibVLC 使用透明附属窗口处理原生视频遮挡。

## 后续继续改造

内容浏览已改为按可用宽度计算 2–10 列的海报网格。外层虚拟化行，内层只创建当前行的海报；分页追加复用已有行，缩放重排保留 MediaCard 和选中状态。海报仍按可见性加载／取消／释放，数据模板使用编译绑定，可供设计器预览。

接下来继续改造独立详情页、信息与播放线路／分集布局、专门的播放视图和全屏控制显示。

随后验证 800／900／1280／1600 DIP、不同缩放率、鼠标／键盘导航及 Windows／Linux 真机。macOS 本地验证不能替代其他平台验收；没有运行 Actions。

已有 4 项设计布局测试，新增 1,000 条内容的网格虚拟化、分页追加与选择重排测试；macOS x64 实际 AOT 主窗口也验证了播放中切换 900／1280 DIP、返回浏览／恢复播放页、保持原渲染表面和弹幕，未重建原生内核。整体回归 149 项，详细日志见 [播放验收](playback-testing.md)。
