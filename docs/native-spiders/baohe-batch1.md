# 宝盒（宝盒接口.top）插件首批原生迁移记录

日期：2026-10-18；分支：`main`，基线 `c83f707`。目标源：`https://宝盒接口.top`（Punycode `https://xn--6orr3pi6g9uu.top/`）。

## 输入与工件

- 运行配置快照：[baohe-current.json](baohe-current.json)，本轮只读抓取 50 个站点条目、34 个不同的 `api` 值。
- 全局 spider：`https://raw.giteeusercontent.com/KYSY1/dyy/raw/master/bh0822.jar`，SHA-256
  `2ff484674c512cc9ea6e9d52f54e7154de113e7aee83230e493259655f71b3ec`（1.2 MB）。
- 反编译：`jadx` 输出 238 个类（含 56 个失败项，均为资源/合成类）。
- 去混淆：该 JAR 用同一个加载器对字符串常量做 `hex → XOR(key=yRrGmJ)` 变换（`merge/EQb.d`）。
  还原脚本与产物保留在分析目录（`bh/tools/deobf.py`、`bh/deobf/`），本轮按去混淆后的源码逐行对照。
- REA/Ghidra：本批 5 个源都是纯 Java 实现，播放地址由字符串拼接或 HTTP JSON 得出，**没有** native `.so`
  校验代码，因此不需要 REA 反汇编；`bh0822.jar` 中带 native 变体的入口（AppTe/AppYd 等）留给后续批次。
  大型 payload 与凭据不进仓库，本文件只记录结论与可复现路径。

## 本轮迁移的 5 个入口

| api | 站点 | ext 形态 | 类文件 | C# 实现 |
|---|---|---|---|---|
| `csp_Wwys` | 旺旺影视 | HTTP(S) 基地址 | `Wwys.java` | `WwysSource.cs` |
| `csp_AppV6` | 麦田 | HTTP(S) 接口根 | `AppV6.java` | `AppV6Source.cs` |
| `csp_TingBook` | 六月有声 | 基地址或 `{site|url}` JSON | `TingBook.java` | `TingBookSource.cs` |
| `csp_HeMa` | 盒马短剧 | 空或功能位 JSON | `HeMa.java` | `HeMaSource.cs` |
| `csp_Feiyu` | 飞鱼 | 空 | `Feiyu.java` | `FeiyuSource.cs` |

注册表：`src/VodBox.Infrastructure/NativeSpiders.cs` 的 `Entries`，均带 ext 闸门，只有形态被接受的定义才提升为原生，
未通过的配置仍走既有提示路径，不扩大支持范围。

## 协议要点（取自反编译源码）

### Wwys（HTML）

- 基地址 = ext（去掉结尾 `/`）；分类来自 `#topnav` 第一条 `ul` 的 `li a`，id 取 href 按 `-` 切分的第 4 段
  （`/vod-type-id-1-pg-1.html` → `1`）。
- 列表：`/vod-list-id-{id}-pg-{page}-order--by-time-class-0-year-0-letter--area--lang-.html`；分页总数取自
  `pagego('…',N)`。
- 详情：`h1.title > a[title]`、`div.desc_item` 第 1/2/3 行的 `font`/`a`、`.page-hd img[src]`、
  `.detail-con > p`（`简介：` 前缀替换在空白折叠后执行，对带 `&nbsp;` 的实际文本不会命中，与原插件一致）。
- 线路：`div.numList`，`id` 含 `m3u8` 记为「量子有广」，否则「高清无广」；同名线路按 `LinkedHashMap` 语义覆盖；
  每条线路内 `Collections.reverse`（最新一集在前）。
- 播放：解析 `mac_from`/`mac_url`，按 `src`/`num` 定位直链；随后按原插件链路请求 `/player/{line}.js` 取
  `src="…'` 前缀拼上直链，再取播放器页 `"url":"…"`。

### AppV6（JSON）

- `{ext}/v6/nav` 出分类与 `type_extend`；`class/area/year/lang` 映射为 类型/地区/年份/语言 筛选（仅多于一个取值时出现）。
- 列表 `{ext}/v6/video?pg=&tid=[&class=][&area=][&lang=]&order=time&token=&`；首页 `{ext}/v6/index_video`
  按原插件语义取循环里最后一次赋值的分组；搜索 `{ext}/v6/search?pg=1&tid=0&text=`。
- 详情 `{ext}/v6/video_detail?id=`：`vod_url_with_player` 的每个 `parse_api` 前缀拼到分集地址前。
- 播放：地址含 `?url=`/`?key=` 时先取 JSON 的 `url`，否则必须是 `m3u8/mp4/mkv`。

### TingBook（HTML）

- 16 个固定分类；列表 `/category/{id}/2/{page}.html`，卡片 `a[href^=/book/]` + `h2`（含 `|` 时按原逻辑拆成
  标题与备注）、`img[src]`；详情 `/book/{id}.html` 在 `System Error` 时重试 3 次，并按 `?page=N` 翻页（上限 200）。
- 章节：`a.list-item[href^=/play/]`，标题取 `title` 属性、正文、再退化为「章节」。
- 播放：`/pc/index/getchapterurl/bookId/{b}/chapterId/{c}/timestamp/{秒}.html`，`jsjm` 或 `N*N*N` 形式的 `src`
  按码位还原并补 `//` 前缀；接口失败时退回拼接的 `/play/…html` 地址。

### HeMa（转发层 JSON）

- 所有业务请求包成 `{apiId,url,body,config}` POST 到 `https://www.tangsan.fun/hema.php`，`url` 为
  `https://freevideo.zqqds.cn/free-video-portal/portal/{apiId}`；响应取 `data`（对象/数组序列化，标量取原文）。
- 四个 apiId：`1125` 列表、`1131` 详情、`1139` 播放、`1802` 搜索；详情按 `bookId` 命中对象并递归收集章节
  （`chapterId`/`chapterName`/`duration`/`videoId`/`playUrl` 任一存在才算章节）。
- 播放地址用原插件的容错顺序取：命中章节的 `content` 直链 → `mp4SwitchUrl` → 全树首个媒体直链 → 字段名兜底。

### Feiyu（签名 JSON）

- 基地址固定 `https://4kyszx.top`；`init` 忽略 ext。
- 签名：`key = HMAC-SHA256(salt=cms_device_salt_v1_2024cms_app_sign_key_v1_2024_secure, msg=deviceId=f45a775875e2e004adbcea78e3312218)`；
  请求签名 `HMAC-SHA256(key, "GET\n{path}\n{params}\n{ts}\n{nonce}\n2.6.8+1")`，header 含
  `x-signature/x-nonce/x-timestamp/x-device-id/x-app-version/x-platform` 与 `host: 4kyszx.top`。
  参数编码为 Java `URLEncoder`（空格 `+`，保留 `- _ . *`）。
- 接口：`/api/app/categories`、`/api/app/ranking/list`、`/api/app/categories/{id}/videos`、
  `/api/app/videos/{id}`、`/api/app/videos/search`。
- 线路按质量权重排序（`4k`100 / `藍光|2k`90 / `高清|hd`80 / `m3u8|採集|資源|zy`10 / 其它 50）；分集 id 为
  `{parseApi}||{url}`，`parseApi` 非空时不直接交给引擎，而是映射为解析链（`uri=url, ParseEndpoint=parseApi`）。

## 与原插件的差异（均为显式取舍）

1. `Wwys`：播放器脚本链（`/player/{line}.js` → 播放器页 `"url"`）失败时回退到 `mac_url` 里的直链。原插件在此
   直接返回空地址，等于整站不可播；回退只在已取得的直链是 HTTP(S) 时生效。
2. `Wwys`：首页卡片限定「`/vod-detail-id-N.html` + `div.pic`」，排除导航项与轮播项；原插件的
   `section.mod:nth-child(3)` 依赖模板层级，跨模板容易漏抓。
3. `Wwys`：搜索页选择器沿用原插件（`#data_list > li`），但站点当前模板文件缺失，服务端直接返回
   `缺少文件：…/vod_search.html`，原生实现按同一 URL 形态请求并返回空列表。
4. `TingBook`：列表页数用「本页是否满 24 条」推断下一页（原插件返回的是条目数而非页数）。
5. `Feiyu`：`parse=1` 分支映射为 `PlaybackRequest.ParseEndpoint`（解析地址）而不是把地址直接交给引擎。

## 在线观测（2026-10-18，本机出口）

- `Wwys https://vip.wwgz.cn:5200`：首页/分类/详情/播放页均 HTTP 200；`/player/monday.js` 取到
  `https://api.wwgz.cn:520/player/?url=` 前缀，拼接后播放器页返回 `"url":"…index.m3u8"`，与 `mac_url` 直链一致。
- `AppV6 http://172.247.227.203:25321/mtys.php`：`/v6/nav` HTTP 200（6.4 KB JSON）。
- `TingBook https://app.365ting.com`：分类页/详情页 HTTP 200；`/pc/index/getchapterurl/...` 与 `/play/*.html`
  均返回 500 `System Error`，属站点侧故障，原生实现按原协议实现并保留回退。
- `Feiyu https://4kyszx.top`：根路径与 `/api/app/categories` 均 HTTP 502（nginx），站点后端不可用，未取得在线数据。
- `HeMa` 转发层 `https://www.tangsan.fun/hema.php` 本轮未发起真实业务请求，避免对第三方转发层产生副作用。

以上只描述本轮观测，不构成「已实播」结论。

## 覆盖变化

按 `baohe-current.json` 的 34 个不同 `api` 值统计：本批新增 5 个原生映射（`csp_Wwys`、`csp_AppV6`、
`csp_TingBook`、`csp_HeMa`、`csp_Feiyu`），原生覆盖从 14 升到 19。仍未迁移：

`csp_AppDrama`、`csp_AppTe`、`csp_AppXY`、`csp_AppYd`、`csp_Czsapp`、`csp_Djuu`、`csp_Douban`、`csp_FengYe`、
`csp_Gz360`、`csp_Jianpian`、`csp_KuKuMusic`、`csp_Kugou`、`csp_PianKu8`、`csp_SaoHuo`、`csp_Tounimabi`、
`csp_YXZB`、`csp_YunZhenXiang`、`csp_XPath`（条件覆盖）、`csp_XBPQ`（条件覆盖），以及 `儿童` 的
`drpy2.min.js + 兔小贝.js` 入口。

## 验证

- 主测试 `tests/VodBox.Tests`：607/607 通过，含本批新增 `NativeBaoheSourceTests` 11 项。
- 预览测试 `tests/VodBox.PreviewTests`：265/265 通过。
- 两套均为 Release / `TreatWarningsAsErrors=true`。
- `NativeBaoheSourceTests` 断言固定样本下的选择器结果、签名向量、URL 形态与播放地址回退，不依赖联网。
