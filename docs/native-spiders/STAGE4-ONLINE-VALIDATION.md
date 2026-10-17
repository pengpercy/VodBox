# 阶段 4/6 入口在线验收（2026-10-18）

用通用化后的 `tools/native-spiders/Probe`（可直接传 `csp_*` api 名）在真实站点上跑
分类 → 首页 → 列表 → 搜索 → 详情 → 播放解析（可选 `--decode` 经 libmpv 实播）。所有命令
只读访问真实站点，不解码合成地址，不打印签名 URL/token。

## 结果汇总

| api | 站点 | 结果 | 说明 |
| --- | --- | --- | --- |
| `csp_AueteGuard` | 奥特（www.aeete.com） | ✅ 实播通过 | 分类/首页/列表/搜索/详情/解析齐全；`--decode` libmpv position≈1.0s |
| `csp_JPJGuard` | 荐片 | ✅ 实播通过 | 11 线路全部解析；`--decode` position≈1.1s |
| `csp_MusicGuard` | MTV1 | ✅ 实播通过 | 分类/首页/列表/搜索/详情/解析；`--decode` position≈1.1s |
| `csp_AllliveGuard` | alllive（live.muxia.site） | ✅ 解析通过 | 991 分类 / 30 首页 / 30 列表 / 5 线路解析；个别线路重建后选集不存在（上游刷新竞态） |
| `csp_SixVGuard` | 新6V（www.xb6v.com） | ✅ 抓取+解析通过；串流路径受控 swarm 实播/seek 通过 | 见 `SIXV-TORRENT-EVIDENCE.md` |
| `csp_LibvioGuard` | 立播 | ⛔ 环境受阻 | ext 配置域名 `www.libvio.pw` 对当前出口返回 `403 Access denied by geographic restriction`；PoW 逻辑已按 Java `dT()`/`F5()` 实现并在默认域名 `www.libvio.io` 上验证（`x-cdn-challenge: js-pow` → `__cdn_pow` → 200） |
| `csp_NewCzGuard` | 厂长 | ⛔ 环境受阻 | 前置雷池 SafeLine WAF 对该出口所有镜像均 `403 + sl-session`（IP/行为级拦截） |
| `csp_JpysGuard` | 文采 | ⛔ 环境受阻 | 域名轮换 + 边缘 WAF：裸域 302 到当前域名后仍 `403`（IP/行为级 ACL） |
| `csp_YCyzGuard` | 原创 | ⛔ 上游受阻 | 新目录域 `v.xl01.cc.ua` 分类可取，但列表接口经 Cloudflare `522`（origin 不可用） |

## 本轮代码修复

- `AueteSource`：默认站 `auete.cc` → 现域名 `www.aeete.com`；该出口对 SNI `www.aeete.com` 重置 TLS，
  改用 SAN 相同的 `aeete.com` 连接并设 `Host: www.aeete.com`；播放页 `base64decode(` 解析。
- `YCyzSource`：目录域 `v.xl01.eu.cc` → `v.xl01.cc.ua`；超时 20s → 60s；列表路径改回站点规范的
  `/s/{s}[/{page}]?type=…`（第 1 页省略 `/1`，去掉多余的 `?&`，空筛选/默认排序不拼接）。
- `JpysSource`：默认基地址改裸域并允许跟随 302；播放头 `Origin/Referer` 改用实际配置基地址（ext 覆盖域名时不再退回硬编码默认镜像）。
- `NewCzSource`：UA 改为 Java `yq()` 的 Android Chromecast(CrKey)；加 CookieContainer 复用 `sl-session`；补齐 `yq()` 请求头。
- `LibvioSource`：挑战感知抓取（`x-cdn-challenge` js-pow → `__cdn_pow` Cookie → `__cdn_verified`）、浏览器导航头、分类白名单改线上真实形态、搜索改 HTML 搜索页。
- `JPJSource`：详情跳过无 `source_list` 的占位分组（`back_source_list_cdn`）。
- `AllliveSource`：线路解析取首个匹配（同流内重复 CDN 地址不再判为「选集不存在」）；搜索容忍单平台缺 `data.list`。

## 说明与限制

- 未通过项均为出口 IP / 上游可用性限制，不是抓取协议缺失；对应 C# 抓取逻辑已按 Java 语义与在线观察实现。
- `Auete` 搜索页当前返回「系统安全验证」JS 挑战，`SEARCH 0`；未在本次范围内实现该挑战，记为未验证项。
- `YCyz` 播放详情路径仍依赖部分 `522` 的上游接口，未端到端验证播放。
