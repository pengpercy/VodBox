# 饭太硬未导入入口：原生适配批次

2026-10-07 承接前一会话的 42 项未导入统计。原订阅共 48 个点播条目，本批新增五个原生 Provider，并修复明星 MV 的分类容量问题。真实导入最终为 12 个点播站点、6 个直播订阅、36 项未适配说明。导入映射与播放验证分别统计；不能将余下 Guard 入口统称为已兼容。

## 实现范围

- 兔小贝：固定公开 MIP 分类、30 项分页、站内搜索、单视频详情、每次播放刷新 HTML5 直接视频。搜索允许最多三次同源显式重定向，拒绝跳往其他站点。
- 虎牙：六个公开分类、分页、搜索分页、实时房间详情、多 CDN 线路、匿名 FLV 签名刷新、直播身份。离线房间明确失败。
- 斗鱼：八个公开分类、分页、POST 搜索、当前 `vike_pageContext` 房间数据、直播身份。播放经现有隔离 Chrome/Edge/Chromium 嗅探和请求头代理；没有实现原 Guard 或斗鱼签名脚本引擎。
- 急救教学：有来医生八类公开课程目录、按课程标题搜索、详情、每次刷新 HTML5 直接视频。搜索范围为急救目录，不是全站医学搜索。
- 荐影预告片：6huo 公开目录分页、关键词搜索、影片详情、多组预告视频及静态 ckplayer 媒体声明。只解析静态字符串，不执行页面脚本。暂无预告或不是该声明格式的条目明确失败；不将预告片当作正片。
- 明星 MV：原分类文件有 129 项，此前导入层和 BilibiliProvider 的 100 项上限使整个条目被丢弃。两处同步调整为 500 项；登录配置分类仍排除，仅转换公开关键词分类。

五个 Provider 都使用异步请求、四路请求上限、20 秒请求期限、2 MiB 内容限制和取消。只识别饭太硬已核对的三个 drpy 规则路径及两个明确的 Spider 名称，不下载或执行 JAR/规则脚本，不代表通用 drpy/Guard 兼容。原生配置见 [public-sites.json](../examples/public-sites.json)。

公开协议参考：[兔小贝页面](https://www.tuxiaobei.com/play/1556)、[虎牙目录](https://www.huya.com/)、[斗鱼房间页](https://m.douyu.com/593392)、[有来医生急救目录](https://m.youlai.cn/jijiu)、[6huo 预告片目录](https://www.6huo.com/movlist/____1)。另只读核对 [FirstAid 固定版本](https://github.com/5q68fs6b86-netizen/CatVodSpider-maintainable/blob/3ac7e6ea46339e6cfdf94dec4ff8e2e573638802/app/src/main/java/com/github/catvod/spider/FirstAid.java) 与 [YGP 固定版本](https://github.com/5q68fs6b86-netizen/CatVodSpider-maintainable/blob/3ac7e6ea46339e6cfdf94dec4ff8e2e573638802/app/src/main/java/com/github/catvod/spider/YGP.java) 的接口字段；没有运行 Java。

## 已验证

本地 HTTP fixture 验证分类、分页、搜索、详情、身份校验、离线拒绝、每次媒体地址刷新、取消/释放、同源重定向、旧规则自动映射、129 项 Bilibili 分类，以及静态媒体解析。完整回归 209 项通过。

macOS x64 真实接口：兔小贝 4 类、30 项/下一页 30 项、1 条搜索；虎牙 6 类、120 项/下一页 120 项、5 条搜索、2 条线路；斗鱼 8 类、8 项/下一页 8 项、1 条搜索；急救教学 8 类、首类 19 项、1 条搜索；荐影 1 类、7 项/下一页 7 项、7 条搜索和 1 组预告。

严格 macOS Native AOT 诊断产物发布成功，将编译/AOT 警告视为错误。该产物真实 LibVLC H.264/VideoToolbox 解码：兔小贝、虎牙、急救教学和荐影均观察到实际视频/音频解码块；直播采用解码统计验收，不依赖直播流的时间轴位置。JIT 与 AOT 的斗鱼目录/搜索/详情及浏览器媒体地址获取通过，经应用实际 PlaybackResolutionService 与回环请求头代理，JIT 斗鱼 HLS 播放确认解码 58 个视频块、184 个音频块；AOT 的该代理播放链路尚待验证。上述不是桌面画面、多平台或长时播放验收，未运行 Actions。

本机复现日志：`/private/tmp/vodbox-public-sites-tests-final.log`、`/private/tmp/vodbox-public-sites-import-final.log`、`/private/tmp/vodbox-public-sites-decode-integrated.log`、`/private/tmp/vodbox-public-sites-aot-build.log`、`/private/tmp/vodbox-public-sites-native-aot.log`。临时原始配置、签名和返回数据不入库。

## 仍未完成的 36 项

- 加密应用/接口：光影、奶酪（T4）、瓜子、糯米、文采、热播、视界（App99Guard）、剧圈、咕咕（AppSxGuard）、AI 短漫剧。现有 AppGet/App99 协议不能直接套用加密 ext；文采公开同类参考入口本次 TLS 连接重置，尚无真实验收。
- 内容网站/音乐/音频：易听、原创、厂长、立播、比特、荐片、奥特、Dm84、有声小说。需核对当前入口发现、页面/API 和播放协议，不能仅按名称注册别名。
- 云盘/磁力：我的云盘、玩偶哥哥、聚剧、新6V、盘搜、易搜、盘她、盘他、抠抠、优汐。需要各站搜索协议及网盘会话、分享解析或磁力播放能力；当前没有实施这些链路。
- 体育/聚合直播：八八、多多、吃瓜、一直播。尚未核对当前节目、房间和播放协议。
- 辅助：豆豆片单、手机推送、广告提醒。它们并非普通点播站点，需要单独定义桌面交互。

要使用新增导入映射，需重新保存原订阅；此前持久化的离线配置快照不会被静默替换。剩余项目仍在导入说明中显示，整个 42 项任务尚未完成。
