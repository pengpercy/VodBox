# 完整媒体内核发行

开发中的下一版：Windows x64/arm64、Linux x64/arm64 与 macOS 一样，发行包必须包含 libmpv 和媒体依赖。缺失库、架构错误或实际解码失败会阻断构建；不再以“用户安装系统 libmpv”作为成功路径。已发布的 0.2.2 尚不包含这些 Windows/Linux 变更。

## Windows

- 固定 `edde746/mpv-build` 的内容寻址资产 `442b5d101e4c`，libmpv 0.41.0 / FFmpeg 8.0.1；媒体编解码库静态整合到 `libmpv-2.dll`。
- 该构建源自 mpv 官方安装页推荐的 shinchiro 构建链，固定源码、补丁与构建配置；完整版本清单保存在 `build/licenses/mpv-build-versions.json`。
- DLL 静态导入 `vulkan-1.dll`。随包构建并提供 Khronos Vulkan Loader 1.4.341；Loader 和 Headers 的源码归档均固定 SHA256，使用静态 MSVC CRT，不要求用户安装 Vulkan SDK。
- 每个 DLL 检查 PE 机器类型与导入闭包。Windows 系统 DLL、系统 CRT API 集和设备驱动由操作系统提供。
- 完整源码入口和构建配方：<https://github.com/edde746/mpv-build/tree/690a755f66c21240515bb2ef84f86a9187652856>。

## Linux

- 固定内容寻址资产 `47a8d9f36f50`，其 Linux 平台覆盖配置实际为 libmpv **0.40.0 / FFmpeg 7.1**，不是其他平台的版本；不隐藏此差异。
- 将包内 libmpv 和 shaderc 的 SO/SONAME 链复制到 `lib/`，再从匹配架构的 Ubuntu 24.04 runner 递归收集所有非 glibc 运行库，包含字体、字幕、TLS、音频、图形加载器等。FFmpeg、libass、libplacebo 的媒体部分已静态整合。
- 保留 glibc/ELF 加载器作为宿主基础；GPU 驱动与桌面音频服务也是宿主设施。不需要额外安装系统 mpv、FFmpeg 或第三方媒体库。
- 每个库设置 `$ORIGIN` 搜索路径；主程序设置 `$ORIGIN:$ORIGIN/lib`。启动器另设置随包路径。直接启动可执行文件与桌面菜单启动均使用随包媒体库。
- DEB/RPM 不再声明 `libmpv2` 系统包依赖。
- **运行基线改为 Ubuntu 24.04 或兼容环境**：第三方内核本身需要 glibc 2.38，随包系统依赖的实际需求也会记录在 `media-dependencies.json`。Ubuntu 22.04 不作为该新包的支持基线。glibc 不强行随包拷贝。
- 发行库的 Debian 包名、版本、SHA256、glibc 符号需求及各自版权声明一起保存，供追溯和重新构建使用。

## 验证与符号

六个 RID 均在原生 runner 用发行目录的**绝对 libmpv 路径**解码 H.264/AAC、HEVC/AAC、VP9/Opus、AV1/Opus 样片，并检查视频参数、持续播放与精确 seek。不能回退到系统 libmpv 来通过此门禁。

`.dSYM`、`.pdb`、`.dbg` 仍单独归档。完整媒体库不因体积被删除。

GPL 媒体构建的版本、许可证及对应源码入口随包存入 `licenses/media/`；Linux 动态组件的版权声明随包存入 `licenses/linux/`。源码配方包含上游精确版本、下载地址/校验值、补丁及重建驱动。发行前仍须核实实际平台运行结果，不把本机的结构检查当作 Windows 运行验证。
