# 协议验证工具

从仓库根目录执行。Probe 调用实际 C# provider，经真实 HTTP 访问 fixture 或在线服务；不会启动 Java。默认只验到播放解析，`--decode` 另经现有媒体代理和无窗口 mpv 验证进度超过 1 秒。返回非零表示该轮未通过；没有解码证据不能声称媒体可播。

```sh
dotnet build tools/native-spiders/Probe/Probe.csproj -c Release -p:TreatWarningsAsErrors=true
postman simulation run postman/simulations/fty-apptt.sim.yaml --port 4911 --output ndjson
# 在另一个终端运行：
dotnet tools/native-spiders/Probe/bin/Release/net10.0/Probe.dll apptt http://127.0.0.1:4911
```

LiveGz 使用 `postman/simulations/fty-livegz.sim.yaml`、端口 4912，探针参数为 `livegz http://127.0.0.1:4912`。

AppSx 使用 `postman mock run postman/mocks/fty-appsx/config.yaml --port 4913 --output ndjson`，探针参数为 `appsx http://127.0.0.1:4913`。CLI 1.74.0 的 simulation 路由名不支持 `api.php`，所以保留原始路径的单 mock 模式用于此协议，不为工具改动生产请求路径。

这些 mock 为冻结插件协议衍生的合成数据，独立原插件输出在 `tests/VodBox.Tests/fixtures/fty-*-oracle.json`；不是线上响应，也没有为第三方服务发布共享 mock。Postman 的 request 日志可核对实际执行路径，原始日志不提交。

在线复核从固定 `fty-in.bmp` 读取源定义：

```sh
dotnet tools/native-spiders/Probe/bin/Release/net10.0/Probe.dll apptt
VODBOX_MPV_LIB=/absolute/path/to/libmpv.dylib dotnet tools/native-spiders/Probe/bin/Release/net10.0/Probe.dll livegz --decode
```

`appsx` 在线模式依次验证剧圈和咕咕。fixture 模式只接受回环 HTTP origin，拒绝解码合成媒体地址。探针日志不打印签名媒体 URL、token 或响应正文。

`ProtocolOracle.java` 是可选的原插件分析工具，依赖已有 JavaHost 的 JDK 21 环境（仅此分析工具例外，运行时和默认测试不调用它）；普通 Java 项目仍使用全局约定的 Java 8。
