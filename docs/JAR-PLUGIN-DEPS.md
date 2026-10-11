# JAR 宿主依赖：上游组件、构建与分发

默认直接复用 [tvbox-silicon](https://github.com/jo32/tvbox-silicon/tree/b82a0313ceb9db652c4f1e07560a7a603eee75df) 原 JavaHost 源码与 `pom.xml`，固定提交 `b82a0313ceb9db652c4f1e07560a7a603eee75df`。上游组件许可为 **GPL-3.0-only**，宿主及 Java 协议包装按组件携带许可与对应源码分发；**不修改 VodBox 主项目许可**。

DEX、Android API 兼容、ClassLoader 与 native 执行直接使用上游实现。旧方案的自研运行时、`dex-tools/`、`sqlite-jdbc/`、`runtime/` 分组，以及“默认不分发 unidbg”的策略，不再代表新默认方案。

## 1. 构建入口与依赖来源

从仓库根目录执行：

```sh
python3 build/tvbox-host.py
# 指定完整组件输出目录：
python3 build/tvbox-host.py --output .cache/tvbox-host/JavaHost
# 兼容旧入口，直接调用同一上游构建脚本，不编译历史自研 src/：
bash src/VodBox.PluginHost/JavaHost/build.sh --output .cache/tvbox-host/JavaHost
# 旧位置参数只接受 host.jar，完整组件写到其父目录：
bash src/VodBox.PluginHost/JavaHost/build.sh .cache/tvbox-host/JavaHost/host.jar
```

输入：

- `third_party/tvbox-silicon/Runtime/JavaHost/src/`：原 JavaHost 源码。
- `third_party/tvbox-silicon/Runtime/JavaHost/pom.xml`：原依赖与构建定义。
- `third_party/tvbox-silicon/vodbox-adapter/`：VodBox Java 协议包装。
- `third_party/tvbox-silicon/UPSTREAM.json` 与 `LICENSE`：来源提交、归档 SHA-256 和 GPL 许可。

需要 **JDK 17+**。构建交付要求是脚本自动发现/下载 JDK 与 Maven。当前脚本从 `JAVA_HOME` 或 `PATH` 发现 JDK；Maven 优先使用 `PATH`，否则使用缓存或下载官方 Maven 3.9.9 并校验 SHA-512。**缺失 JDK 时的自动下载尚待主 Agent 确认实现与验收，不宣称已完成。**

Maven 使用官方 Central（`https://repo.maven.apache.org/maven2`），缓存位于工作区 `.cache/maven-repository/`。构建沿用上游 pom 解析传递依赖并复制到 `deps/`；旧 `build/dex-deps.json` 的分组锁定表不作为新宿主 Maven 闭包的版本或校验依据。该锁定表仍用于目标 RID 的 JRE 获取。

## 2. 原 pom 的依赖范围

以下为固定提交的直接依赖，实际分发还包括 Maven 解析出的传递闭包：

| 能力 | 原 pom 直接依赖 |
| --- | --- |
| Android/native 运行时 | `unidbg-android:0.9.8`、`unidbg-unicorn2:0.9.8` |
| DEX 工具 | `dex-translator:2.4.37`、`baksmali:2.5.2` |
| Android 编译接口与 JSON | `android:4.1.1.4`、`android-json:0.0.20131108.vaadin1` |
| HTTP、解析与加密 | `okhttp:4.12.0`、`gson:2.10.1`、`bcprov-jdk18on:1.78.1`、`jsoup:1.17.2`、`simple-xml:2.7.1` |
| 日志 | `slf4j-simple:2.0.13` |

不另选 Dynarmic 版本替换上游 native 后端，也不将旧分组的版本表当作该 pom 的审计结果。第三方依赖各自的许可证及 NOTICE 仍需按实际分发制品保留；不能把整个 `deps/` 宣称为“全部 Apache”。GPL 宿主组件的许可文件与对应源码必须同行。

## 3. 构建输出与打包路径

`build/tvbox-host.py` 默认输出到 `src/VodBox.PluginHost/JavaHost/`；发布时 `build/bundle.py` 将完整组件放在 `<publish>/JavaHost/`，内部路径保持一致：

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
  jre/bin/java                # Windows 为 java.exe；由 bundle.py 添加
  java-host.json              # bundle.py 的宿主哈希、目标 JRE 元数据与原生后端覆盖裁决
```

`java-host.json` 的 `nativeBackend` 字段显式记录该 RID 是否能跑原生模拟后端（`supported` + `verdict` + 实测的 `bundledJar`）。打包时 `bundle.py` 会先以 `build/native_coverage.py verify` 从 `deps/*.jar` 重新推导覆盖，若与锁定表声明不符则直接失败——不会为不可跑的 RID 写出绿色裁决。

`host.jar` 包含上游类与协议包装；`deps/*` 包含原 pom 的运行依赖；`upstream-runtime.json` 保留固定提交与来源信息；`corresponding-source/` 包含宿主源、原 pom、包装源及来源/许可文件。分发时必须同时保留这些材料，不能只复制 `host.jar`。

打包命令形式：

```sh
python3 build/bundle.py <rid> <publish目录>
# 复用预构建完整组件时，传入其 host.jar：
python3 build/bundle.py <rid> <publish目录> --java-host-jar /abs/path/JavaHost/host.jar
```

显式 `--java-host-jar` 的来源目录必须含 `deps/`、`upstream-runtime.json`、`LICENSE-tvbox-silicon` 和 `corresponding-source/`，打包器按同路径复制。`VODBOX_JAR_HOST` 仍有预构建入口，但当前实现只复制 JAR，尚不能据此宣称完整组件已随包；复用完整组件应使用显式参数并核对发布布局。`--skip-java-host` 跳过 JVM 宿主打包。

## 4. JRE 与目标 RID

Java 字节码可以用构建机 JDK 编译，随包 **JRE 必须匹配目标 RID**。`build/bundle.py` 继续使用 `build/fetch-dex-deps.py jre --rid <rid>` 获取锁定的 Temurin 资产，再通过 `build/nativeverify.py` 的 `verify_jre` 检查 `bin/java` 的 PE/ELF/Mach-O 架构；不匹配即失败。

JRE 的锁定资产、SHA-256 与发布版本保留在 `build/dex-deps.json`。Temurin 的许可为 `GPL-2.0 WITH Classpath-exception-2.0`，随包应保留运行时自身的许可材料。

JRE 有目标 RID 资产不代表上游 native 后端也支持该平台。六 RID 的宿主启动、native 库装载和真实插件行为需分别验证；本次文档更新没有运行这些检查，也不宣称各平台均已验收。

### 4.1 原生后端在各 RID 的实际资产覆盖

上游 pom 固定 `unidbg-android:0.9.8` + `unidbg-unicorn2:0.9.8`，因此 ARM64 模拟后端是 **unidbg-unicorn2** 内 `natives/**` 的原生库（不是 Dynarmic）。解包实际随包 `deps/*.jar` 实测：

| RID | `unidbg-unicorn2` natives | 原生模拟 |
| --- | --- | --- |
| `osx-x64` | `natives/osx_64/libunicorn.so` | 支持 |
| `osx-arm64` | `natives/osx_arm64/libunicorn.so` | 支持 |
| `linux-x64` | `natives/linux_64/libunicorn.so` | 支持 |
| `linux-arm64` | `natives/linux_arm64/libunicorn.so` | 支持 |
| `win-x64` | `natives/windows_64/unicorn.dll` | 支持 |
| `win-arm64` | **无 `natives/windows_arm64/**`** | **不支持** |

`win-arm64` 缺的是**原生模拟库**本身：随包 Temurin JRE 仍然存在，纯 Java 插件可运行；含原生 guard 的插件在 `win-arm64` 上必须失败为 `unsupported_native_library`，不能因为「有 JRE」或「操作方配置了 unidbg classpath」就宣称可跑。汇编辅助 `capstone/keystone/demumble` 的覆盖更窄（缺 `linux-arm64`/`win-*`），仅供诊断，不改变上表。

结论以机器可核验形式保存在 `build/dex-deps.json` 的 `nativeCoverage`，并由 `build/native_coverage.py verify --deps <JavaHost/deps>` 从实际 jar 重新推导；`build/test_native_coverage.py` 与 `build/test_bundle.py` 守住「声明不得超过实测」。运行期由 `NativeBackendCapability` 把同一结论落到当前平台（`win-arm64` 直接判定不可用）。旧 `nativePlatforms` 曾按 `unidbg-dynarmic:0.9.9` 声明覆盖——该制品**不在上游 pom 闭包内**，其 `linux_arm64` 等 natives 不构成任何覆盖声明，已更正。

## 5. 当前验收状态

真实 FTY 已成功解密 ClassLoader 并取得 Bili 实例；裸 Bili 首页失败，尚未成功播放。单 DEX、多 DEX 真实验收及完整 .NET/UI 回归由主 Agent 正在执行。组件构建、发布布局、缺失工具自动下载、目标 RID JRE 与 native 能力的结果必须分别留证，不能把加载成功写成全部完成。

`build/dex-acceptance.sh` 直接构建上游组件，核验来源/布局、仓库 fixture 的 SHA-256/大小及 baksmali 解析结果，然后设置 `VODBOX_DEX_ACCEPTANCE_HOST_JAR` 执行真实 .NET 单/多 DEX 用例。默认 Java `test.sh` 转到同一入口；`--legacy-unit` 才执行历史自研宿主局部自测，且不运行旧 native 字节码改写自测。新验收使用 Maven `deps/*`，不会通过历史 `fetch-dex-deps.py stage` 覆盖或混入 `dex-tools/`；旧锁定表仍用于上述目标 JRE 获取。

本次更新脚本与文档，未执行构建、测试或类型检查；入口接线不等于验收通过。主 Agent 执行后需记录实际结果，缺失/失败/Skip 不得标为通过。验收步骤与边界见 [DEX-ACCEPTANCE.md](DEX-ACCEPTANCE.md)。


2026-10-11 主 Agent 收尾：`VODBOX_JAR_HOST` 和 `--java-host-jar` 现在均复制完整预编译组件；对应宿主源码随实际 `build/tvbox-host.py` 与自包含目录一起交付。`dependency-sources/`、`dependency-notices/` 已纳入组件；native 清单仍明确记录外部引擎/guest SDK 精确来源 pending，不能据此宣称可正式发布。
