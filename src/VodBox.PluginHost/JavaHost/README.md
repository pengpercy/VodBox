# VodBox JavaHost：复用 tvbox-silicon

默认宿主直接复用 [tvbox-silicon](https://github.com/jo32/tvbox-silicon/tree/b82a0313ceb9db652c4f1e07560a7a603eee75df) 在提交 `b82a0313ceb9db652c4f1e07560a7a603eee75df` 的原 JavaHost 源码与 `pom.xml`。DEX 转换、Android 兼容、ClassLoader 与 native 执行由上游运行时承担，不再维护另一套自研实现。VodBox 保留 Java 协议包装，将桌面端 JSON IPC 接到上游会话。

## 构建

从仓库根目录执行：

```sh
python3 build/tvbox-host.py
# 可指定独立输出目录：
python3 build/tvbox-host.py --output .cache/tvbox-host/JavaHost
```

构建需要 JDK 17+ 和 Maven，直接使用 `third_party/tvbox-silicon/Runtime/JavaHost/{src,pom.xml}`，并编译 `third_party/tvbox-silicon/vodbox-adapter/`。依赖从官方 Maven Central 解析到工作区缓存。

脚本自动发现/下载 JDK 与 Maven 是交付要求。当前脚本从 `JAVA_HOME` 或 `PATH` 发现 JDK，自动发现 Maven，缺失 Maven 时下载官方发行并核对 SHA-512；缺失 JDK 时的自动下载仍待主 Agent 确认实现与验收。旧 `build.sh`/`test.sh` 对应历史自研宿主，不能作为新默认运行时的构建或验收入口。

## 运行与协议

类 Unix 的启动与转换入口如下；Windows classpath 分隔符为 `;`：

```sh
java -cp 'JavaHost/host.jar:JavaHost/deps/*' vodbox.bridge.ProcessAdapter --serve request.json
java -cp 'JavaHost/host.jar:JavaHost/deps/*' vodbox.bridge.ProcessAdapter --convert request.json
```

`--serve` 读取桌面端会话请求，调用上游初始化与插件会话。就绪状态写入 `<cache>/ready.json`；随后从 stdin 逐行接收 `{"id","params"}`，将响应原子写入 `<cache>/responses/<id>.json`。插件 stdout 转向 stderr，避免污染协议。首页、分类、搜索、详情和播放调用由上游会话执行。

`--convert` 接收 `input`、`output`、`result` 路径，将转换委托给上游 `dalvik.system.DexClassLoader`，包装层只输出产物与结果信封。转换后的类能否链接、插件方法能否成功、播放结果能否播放，都需要独立验收。

## 分发与许可

构建输出必须作为完整组件分发：

```text
JavaHost/
  host.jar
  deps/*.jar
  upstream-runtime.json
  LICENSE-tvbox-silicon
  corresponding-source/
    src/
    pom.xml
    vodbox-adapter/
    UPSTREAM.json
    LICENSE
```

上游组件及协议包装使用 **GPL-3.0-only**，随组件保留 GPL 许可与对应源码；第三方 Maven 依赖的许可/NOTICE 按实际制品保留。**VodBox 主项目许可不修改。** 不再使用旧文档中“独立实现、无上游/GPL 代码”或“默认排除 unidbg”的描述。

`build/bundle.py` 将上述内容以同路径打包到 `<publish>/JavaHost/`，并添加目标 RID 的 `jre/` 与 `java-host.json`。复用预构建组件时使用 `--java-host-jar` 指向完整组件目录中的 JAR，并保留所有相邻材料；`VODBOX_JAR_HOST` 与 `--java-host-jar` 都走同一 `stage_prebuilt_host`，复制 `deps/`、`upstream-runtime.json`、`LICENSE-tvbox-silicon` 与 `corresponding-source/`，且拒绝 `deps/` 为空的组件（否则宿主加载不到上游类）。

随包 JRE 继续按目标 RID 获取并验证 PE/ELF/Mach-O 架构；构建机的 Java 不能替代目标 JRE。**JRE 架构匹配不能证明上游 native 后端在该平台可用**：上游 pom 固定 `unidbg-unicorn2:0.9.8`，其 `natives/` 缺 `windows_arm64/**`，故 `win-arm64` 上含原生 guard 的插件必须失败为 `unsupported_native_library`。覆盖结论见 `build/dex-deps.json` 的 `nativeCoverage`，由 `build/native_coverage.py verify` 实测校验，并写入 `java-host.json` 的 `nativeBackend`。

## 验收状态

真实 FTY 已成功解密 ClassLoader 并取得 Bili 实例，但裸 Bili 首页调用失败，尚未成功播放。主 Agent 正在执行单 DEX、多 DEX 真实验收和完整 .NET/UI 回归；当前不能标为全部完成。本次仅改文档，未运行构建、测试或类型检查。

详细约定见 [依赖与打包](../../../docs/JAR-PLUGIN-DEPS.md) 和 [真实验收](../../../docs/DEX-ACCEPTANCE.md)。
