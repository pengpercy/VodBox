# Jpys / Alllive Java → C# 迁移证据

来源：固定 payload 解混淆阅读稿 `/private/tmp/vodbox-protocol-read/{Jpys,Alllive}.java`，原 `merge/{ME,zE}.java` 来自对应 payload/clean；仅静态阅读，未运行原插件、未运行任何 build/test。

## Jpys

新增 `JpysSource.cs`，工厂需要主 Agent 注册 `csp_Jpys`（真实配置名称应以清单为准）。ext 非空时直接覆盖默认 `https://www.ghw9zwp5.com`，不是 FTY 密文。设备标识 Java `jpDeviceId` SharedPreferences，全局持久 UUID；C# 对应数据目录 `jpDeviceId` 文件，构造测试可注入固定设备号、毫秒时钟和 HTTP 委托。

签名：`SHA1(lowerhex(MD5(UTF8(canonical + "&key=cb808529bae6b6be45ecfab29a4889bc&t=" + milliseconds))))`，canonical 空时不加开头 `&`。SHA1 输出小写。headers 为 `sign`、`T`、`deviceid`。

- home：`/api/mw-movie/anonymous/home/hotSearch`，canonical 空，`data[]`。
- category：`video/list?type1={type}&pageNum={page}&area={area}&year={year}`，canonical **area、pageNum、type1、year** 顺序，`data.list[]`。`by` 原插件读取但未用于请求。
- search：`video/searchByWord?keyword={URLEncoder(keyword)}&pageNum=1&pageSize=8`，签名用未编码关键词；Java 固定只取第一页；`data.result.list[]`，按标题包含关键词筛选。
- detail：`video/detail?id={id}`，canonical `id={id}`，`data` 对象；`episodeList[].{name,nid}` 原始播放值 `mediaId@nid`，持久 Episode.Id 为 `爱电影\u0001mediaId@nid`，LineId 为 `爱电影`，一条“爱电影”线路；简介优先 `vodBlurb`，否则 `vodContent`。
- player：`v2/video/episode/url?clientType=3&id={id}&nid={nid}`，canonical 同序；返回 `data.list[0].url`，请求和播放 UA 均为 Java Chrome/148 Linux aarch64 UA；播放 Origin/Referer 固定默认域名，原插件即使 ext 覆盖也如此。

**detail 请求的真实异常**：阅读稿 553–554 行 `String strM1008 = this.SN; map.put(strM1008,strM1008)`。因此 C# detail 请求保留 `header[deviceId]=deviceId`，没有未经证据擅自补 `deviceid`。主 Agent 应用独立 Java Oracle 捕获确认，并决定是否以明确兼容选项修复真实服务端问题。

ME.G 在 `&` 分隔每项上执行 contains；`vodClass` 包含“其它”“伦理”“情色”“剧情,爱情”任意项会被过滤。C# 同样保留。Java home 固定分类 1 电影、2 电视剧、4 动漫、3 综艺，以及 2026 固定过滤年份，未按机器年份推进。

## Alllive

新增 `AllliveSource.cs` 和 `FtyLzString.cs`，尚需主 Agent 工厂注册真实 `csp_Alllive` 名称。

- init `this.yq = ME.KJ(ext)`；ME.KJ 调 HideUtils.decrypt。C# 使用既有 FtyExtCodec；允许明文 HTTP(S) ext 便于独立模拟。
- `M(url)` 设置 `sec-fetch-site: same-origin`，取 HTTP 文本并 `replaceAll(" ","")` 后 `zE.N`，再 JSONObject。
- `zE` 固定字母表 `ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=`（原 clean 第 29 行）；起始 mask=32（573 行）；字典初始3项、2-bit 初始字符宽度选择、8/16-bit UTF16 literal、3-bit 后继码宽、动态 enlargeIn，确认是 LZ-string decompressFromBase64。C# 有压缩/解压大小及字典数量上限。
- `/api/{huya|douyu}/getCategories`：`data[]` 是父组 `{id,name,list:[{cid,name,pic}]}`。Java 用文件夹卡片导航。Core 无文件夹模型，因此 C# 将父子展开到 Category（`platform_cid`），标签保留平台、父组、子类。
- `/api/{platform}/getCategoryRooms?id={cid}&pid=1&page={page}`：`data.list[]`，房间 `{roomId,title,nickname,cover}`，媒体 ID `platform_roomId`。
- `/api/bilibili/getRecommendRooms?page={page}`：同房间格式。C# 首页直接用该公开推荐。
- 房间列表可返回 `data.cookie`；Java 保存 SN 并在后续 list URL 附加解码 cookie。C# 保存每 provider 的 cookie，URL 编码传输值以避免参数注入。
- `/api/{platform}/searchRooms?page={page}&kw={keyword}`：Java 借 Android UI 输入关键词；C# 直接使用用户搜索词，覆盖 huya/douyu。
- `/api/{platform}/getRoomDetail?id={roomId}`：`data` 含 title/online/siteId/nickname/cover/url/category 和 `stream:[{name,lines:[{name,url}]}]`；原 player 直接地址，`//` 前加 `https:`。C# 播放时刷新详情并按线路/地址索引取新 URL。

### 虎牙 info 签名

有 `data.info` 时，原 Java init POST `https://udblgn.huya.com/web/anonymousLogin`，body `{"appId":5002,"byPass":3,"context":"","version":"2.4","data":{}}` 取 `data.uid`。C# 延迟到真正需要虎牙签名时请求 UID。

info `{name,code}`；code 查询参数含 fm/ctype/fs：

- `fmPrefix = UTF8(Base64Decode(fm)).split('_')[0]`
- `wsTime = lowerhex(milliseconds/1000 + 21600)`
- `seqid = milliseconds*1000 + uid`
- `hash = MD5(seqid + "|" + ctype + "|" + ctype)`
- `wsSecret = MD5(fmPrefix + "_" + uid + "_" + info.name + "_" + hash + "_" + wsTime)`
- 每个流 URL 后附 `&wsSecret=...&wsTime=...&seqid=...&ctype=...&ver=1&fs=...&t={ctype}&u={uid}`。
- `ME.tF` 原函数直接 `return zH(str)`，即小写 MD5。

### 验收待主 Agent 执行

注册入口；编译/完整回归；Oracle 独立捕获 Jpys 各端点签名及 detail 异常键、Alllive LZ 输入输出向量（含中文、空串、padding）；模拟多分组分类和多条流；虎牙 UID/签名固定时钟向量；真实公开端点与播放解码。未迁移 Android 搜索扫码弹窗及 TV remote 控制服务（桌面直接搜索替代），未增加账号或网盘能力。
