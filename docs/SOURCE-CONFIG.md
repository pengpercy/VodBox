# 订阅源格式说明

VodBox 的点播源沿用 [LunaTV](https://github.com/MoonTechLab/LunaTV)（MoonTV）的配置文件格式：一个 `config.json`，里面列出 **苹果CMS V10 采集接口**、自定义分类和直播源。LunaTV 能用的配置，VodBox 基本可以直接用。

> VodBox 不内置任何播放源。本文所有地址都是 `example.com` 占位示例，请换成你自己有权使用的接口。

- [1. 快速开始](#1-快速开始)
- [2. 配置文件结构](#2-配置文件结构)
- [3. api_site：点播采集源](#3-api_site点播采集源)
- [4. custom_category：自定义分类](#4-custom_category自定义分类)
- [5. lives：直播源](#5-lives直播源)
- [6. 制作订阅链接（base58）](#6-制作订阅链接base58)
- [7. 更新订阅时如何合并](#7-更新订阅时如何合并)
- [8. 采集接口要求（给自建站点）](#8-采集接口要求给自建站点)
- [9. 常见问题](#9-常见问题)

## 1. 快速开始

1. 按下文格式写好 `config.json`；
2. 把它放到任意能通过 HTTP(S) 访问的位置（自己的服务器、对象存储、Gist raw 地址等），可选先做 [base58 编码](#6-制作订阅链接base58)；
3. 打开 VodBox → **设置 → 播放源**，填入订阅地址并拉取；也可以直接把 JSON 内容粘贴进去。
4. 拉取后可在同一页面用 **有效性检测** 搜索一个关键词，检查各源是否连通、延迟多少，一键禁用无效源。

## 2. 配置文件结构

```jsonc
{
  "cache_time": 7200,
  "api_site": {
    "example_a": {
      "name": "示例资源A",
      "api": "https://cms-a.example.com/api.php/provide/vod",
      "detail": "https://cms-a.example.com"
    },
    "example_b": {
      "name": "示例资源B",
      "api": "https://cms-b.example.com/api.php/provide/vod"
    }
  },
  "custom_category": [
    { "name": "华语", "type": "movie", "query": "华语" },
    { "name": "美剧", "type": "tv", "query": "美剧" }
  ],
  "lives": {
    "example_live": {
      "name": "示例直播",
      "url": "https://live.example.com/tv.m3u",
      "ua": "okhttp/3.15",
      "epg": "https://live.example.com/epg.xml"
    }
  }
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `cache_time` | 数字（秒） | 否 | 采集接口搜索结果的缓存时间，缺省 `7200`。 |
| `api_site` | 对象 | 三者至少一个 | 点播采集源，键为源 ID，见 [第 3 节](#3-api_site点播采集源)。 |
| `custom_category` | 数组 | 三者至少一个 | 点播页额外的分类，见 [第 4 节](#4-custom_category自定义分类)。 |
| `lives` | 对象 | 三者至少一个 | 直播源，键为源 ID，见 [第 5 节](#5-lives直播源)。 |

解析比较宽容：

- 允许 `//` / `/* */` 注释、尾逗号和 UTF-8 BOM；
- 数字和字符串可以混用（例如 `"cache_time": "7200"` 也能识别）；
- 不认识的字段会被忽略，所以 LunaTV 管理后台导出的完整配置也能直接用。

## 3. api_site：点播采集源

```jsonc
"api_site": {
  "<源ID>": {
    "name": "显示名称",
    "api": "https://cms.example.com/api.php/provide/vod",
    "detail": "https://cms.example.com"   // 可选
  }
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| 键（源ID） | 是 | 唯一标识，建议只用小写字母、数字和下划线。VodBox 用它在更新订阅时识别「同一个源」，**改键等于删掉旧源、新增一个源**。 |
| `api` | 是 | 苹果CMS V10 JSON 接口地址，通常以 `/api.php/provide/vod` 结尾。地址里可以自带查询参数，VodBox 会用 `&` 追加。 |
| `name` | 否 | 界面显示名；为空时显示键名。 |
| `detail` | 否 | 站点网页根地址。只在接口拿不到播放地址时使用：VodBox 会去抓 `<detail>/index.php/vod/detail/id/<影片ID>.html`，从页面里提取 `.m3u8` 地址。普通源不要填。 |

`api` 或键为空的条目会被跳过。

**VodBox 会发出的请求**（`{api}` 为上面的地址）：

| 用途 | 请求 |
| --- | --- |
| 搜索 | `{api}?ac=videolist&wd=<关键词>`，翻页追加 `&pg=2`、`&pg=3`… |
| 详情 | `{api}?ac=videolist&ids=<影片ID>` |

搜索时，每个源默认最多拉 5 页（可在设置里调整），单页超时 8 秒；详情请求超时 10 秒。

## 4. custom_category：自定义分类

点播页顶部的分类标签，按标签或关键词展示片单，和采集源无关。

```json
"custom_category": [
  { "name": "华语", "type": "movie", "query": "华语" },
  { "type": "tv", "query": "日剧" },
  { "name": "哈利波特", "type": "movie", "query": "哈利波特" }
]
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `query` | 是 | 分类的标签或关键词。 |
| `type` | 否 | `movie`（电影）或 `tv`（剧集）；其他值按 `movie` 处理。 |
| `name` | 否 | 显示名；省略时用 `query`。 |

同一分类由 `type + query` 唯一确定。常见可用标签：

- **movie**：热门、最新、经典、豆瓣高分、冷门佳片、华语、欧美、韩国、日本、动作、喜剧、爱情、科幻、悬疑、恐怖、治愈
- **tv**：热门、美剧、英剧、韩剧、日剧、国产剧、港剧、日本动画、综艺、纪录片

其他关键词（比如某个系列名）也可以用。

## 5. lives：直播源

```jsonc
"lives": {
  "<源ID>": {
    "name": "显示名称",
    "url": "https://live.example.com/tv.m3u",
    "ua": "okhttp/3.15",                       // 可选
    "epg": "https://live.example.com/epg.xml"  // 可选
  }
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| 键（源ID） | 是 | 唯一标识，规则同 `api_site`。 |
| `url` | 是 | 播放列表地址，支持 M3U 和 TXT 两种格式（见下）。 |
| `name` | 否 | 显示名；为空时显示键名。 |
| `ua` | 否 | 请求该直播源时使用的 User-Agent。 |
| `epg` | 否 | XMLTV 节目单地址。 |

### M3U 格式

```m3u
#EXTM3U
#EXTINF:-1 tvg-id="CCTV1" tvg-logo="https://img.example.com/cctv1.png" group-title="央视",CCTV-1 综合
https://live.example.com/cctv1.m3u8
#EXTINF:-1 tvg-id="demo" group-title="卫视",示例卫视
#EXTVLCOPT:http-user-agent=Mozilla/5.0
#EXTVLCOPT:http-referrer=https://live.example.com/
https://live.example.com/demo.m3u8
```

- `group-title` → 分组，`tvg-logo` → 台标，`tvg-id` → 用于匹配节目单；
- 也支持 `#EXTGRP:分组名`；
- 单个频道的请求头：`#EXTVLCOPT:http-user-agent=` / `http-referrer=`，或 Kodi 的 `#KODIPROP:inputstream.adaptive.stream_headers=`。目前只取 User-Agent 和 Referer。

### TXT 格式

```text
央视,#genre#
CCTV-1 综合,https://live.example.com/cctv1.m3u8
CCTV-2 财经,https://live.example.com/cctv2.m3u8$https://backup.example.com/cctv2.m3u8
卫视,#genre#
示例卫视,https://live.example.com/demo.m3u8
```

- `分组名,#genre#` 开始一个新分组，后续频道都归入它；
- `频道名,地址` 为一个频道；多个地址用 `$` 分隔，会成为该频道的多条线路；
- 以 `//` 或 `;` 开头的行视为注释；
- 分组名写成 `分组名_密码` 时，该分组需要输入密码才能查看，界面只显示 `分组名`。

`url` 也可以直接指向另一份 LunaTV 配置 JSON，VodBox 会展开其中的 `lives`。

## 6. 制作订阅链接（base58）

订阅地址的内容可以是 **原始 JSON**，也可以是 **整个 JSON 做 base58 编码后的文本**（LunaTV 管理后台的订阅格式），VodBox 会自动识别。base58 使用比特币字母表：

```text
123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz
```

用 Python 编码（无需第三方库）：

```python
# encode_config.py  用法：python3 encode_config.py config.json > config.txt
import sys

ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"
data = open(sys.argv[1], "rb").read()
n = int.from_bytes(data, "big")
out = ""
while n > 0:
    n, r = divmod(n, 58)
    out = ALPHABET[r] + out
out = "1" * (len(data) - len(data.lstrip(b"\0"))) + out
print(out)
```

把生成的 `config.txt` 放到可访问的地址上，就得到了一个订阅链接。

> base58 只是编码，**不是加密**，任何人拿到链接都能解出原文。不想公开的源不要放进公开的订阅链接。

## 7. 更新订阅时如何合并

重新拉取订阅时（手动，或开启自动更新后最多每小时检查一次），VodBox 按 LunaTV 的 refineConfig 规则合并：

- 订阅里有、本地也有（同键）：更新名称和地址，**保留你设置的启用/禁用状态和排序**；
- 订阅里新增的：追加到列表末尾；
- 订阅里已删除的：本地不会删掉，转为「自定义」源保留；
- 你在 VodBox 里手动删除的订阅源：之后刷新订阅也不会加回（可用「恢复已删除」找回）；
- `cache_time` 大于 0 时覆盖本地缓存时间。

手动添加的自定义源与订阅互不影响。

## 8. 采集接口要求（给自建站点）

VodBox 支持标准的苹果CMS V10 JSON 接口。返回示例（只列出用到的字段）：

```json
{
  "code": 1,
  "page": 1,
  "pagecount": 3,
  "list": [
    {
      "vod_id": 1001,
      "vod_name": "示例影片",
      "vod_pic": "https://img.example.com/poster/1001.jpg",
      "vod_remarks": "更新至第2集",
      "vod_year": "2024",
      "vod_area": "大陆",
      "type_name": "国产剧",
      "vod_class": "剧情,悬疑",
      "vod_director": "导演甲",
      "vod_actor": "演员甲,演员乙",
      "vod_content": "<p>剧情简介……</p>",
      "vod_douban_id": "1234567",
      "vod_play_from": "线路A$$$线路B",
      "vod_play_url": "第1集$https://media.example.com/a/1.m3u8#第2集$https://media.example.com/a/2.m3u8$$$第1集$https://media.example.com/b/1.mp4#第2集$https://media.example.com/b/2.mp4"
    }
  ]
}
```

**播放地址格式**（重点）：

- `vod_play_from` 与 `vod_play_url` 都用 `$$$` 分隔线路，两边一一对应；
- 每条线路内用 `#` 分隔集，每集为 `集名$地址`；没写集名时显示为「第N集」；
- 只接受 `http://` / `https://` 地址。

**线路取舍**（与 LunaTV 的差异）：mpv 能播 `.m3u8` 以外的格式，所以 VodBox 保留所有 HTTP(S) 线路：

- 直链媒体线路（`.m3u8` `.mp4` `.flv` `.mkv` `.ts` `.mpd` 等）排在最前；
- 整条都是网页播放页的线路（`.html` / `.php` 分享页、`/share/`、云播 `/play/短码` 等）会被丢弃，因为播放器打不开网页；
- 一部影片没有任何可播线路时不会出现在搜索结果里；
- `vod_play_url` 为空时，会尝试从简介里找 `.m3u8` 地址作为兜底。

**其他细节**：

- `pagecount` 决定搜索要翻几页；
- `vod_year` 里提取四位年份，聚合搜索按「标题 + 年份 + 电影/剧集」合并不同源的同一部影片，年份写准有助于合并；
- `type_name` 命中成人类分类（如「伦理片」「福利」等）的结果默认会被过滤，可在设置里关闭；
- 接口应返回 UTF-8（也兼容 GBK 等常见编码），单次响应不超过 12 MB；
- 返回 403 的源会被暂时跳过，空结果不缓存。

## 9. 常见问题

**拉取订阅报「既不是 JSON 也不是 base58 编码的 JSON」**
订阅地址返回的可能是网页（比如网盘分享页），而不是文件内容。请换成能直接下载原文的 raw 地址。

**报「配置文件中没有 api_site / lives / custom_category」**
JSON 能解析，但三个字段都为空或拼写错误。注意是 `api_site`，不是 `api_sites`/`sites`。

**有效性检测显示「无结果」**
接口连通了，但没搜到含检测关键词的影片。换一个更常见的关键词再测；一直无结果的源可能只收录特定类型的片源。

**TVBox / FongMi 的配置能用吗？**
不能。VodBox 不支持 jar/js 爬虫、type 3/4 源和解析接口，只认苹果CMS V10 JSON 接口。可以从那类配置里挑出 `type: 1`、`api` 指向 `/api.php/provide/vod` 的站点，按本文格式改写。
