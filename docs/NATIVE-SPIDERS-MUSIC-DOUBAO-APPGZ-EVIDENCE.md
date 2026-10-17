# Music / Doubao / Appgz 原生迁移证据

仅静态读取固定 payload 展开字符串的 Java 源码及 merge 辅助类。子 Agent 未运行 build/test/typecheck/Oracle。主 Agent 负责注册、编译、独立向量及真实播放验收。

## Music

新增 MusicSource.cs。默认 `http://www.4c44.com`，ext 直接覆盖网页 origin。UA 为 Windows WOW64 Chrome/86，Referer 网页 origin。

- home 分类选择器 `div.pull-right > div > ul > li > a`，ID 为 href 去 `.html`，名字 title；C# 采用 pull-right 下 href `.html` 的链接。
- category URL `origin + category + '/' + page + '.html'`；列表选择器 `div.lksinger_list > ul > li`、`div.singer_list > ul > li`、`div.video_list > ul > li`、`.ilingku_fl > li`，name a 优先，否则 a；封面 img.src。
- Java 搜索通过扫码或弹窗输入，生成 `/so/{keyword}.html` 的聚合项，然后 detail 展开歌曲。C# SearchAsync 直接返回该聚合页，不再走 Android UI。
- detail `.play_list h1` 取逗号前标题、`.play_list > ul > li`：`.name a` 生成歌曲，`.mv a` 生成 `-MTV`；固定线路“易听”，持久 ID `易听\u0001{href}`。
- GET 同时有 `csrf_token` 和 `human_check` 时，读取 input[name=csrf_token].value，POST origin `{csrf_token,human_check:on}`，用响应 cookie 重试。Java 持久 cookie 到 Y_sjyy；C# HttpClient CookieContainer 保存 provider 生命期会话，不向账号请求授权。
- 音频 ID 为末段去 `.html`，radio 页面先正则 `"music","(.*?)"` 提 ID；POST `http://www.4c44.com/js/play.php` form `id,type:music,key:{yyyyMMddHHmm}4c44`，返回 `{url,title,pic,lrc}`。
- **真实 Java 默认域名绑定**：SN 在字段初始化时取 `this.yq + /js/play.php`；init(ext) 仅更改 yq，不重建 SN。因此 C# 保留播放 API 默认域名。
- URL 包含 mp4 时返回固定 `http://www.4c44.com/data/down.php?ac=vplay&id={id}&q=1080`，没有额外网页解析。

待主 Agent 验收：HTML 固定样本层级、csrf cookie 流程、分钟 key 时区、音频及 MTV。C# 当前未挂载 Java Android 字幕代理生成的 LRC/ASS 动画歌词；播放媒体 URL 已覆盖。Java detail 为首歌曲额外读取 play_left img，C# 当前读取聚合页 play_left，因此无聚合页封面时可能缺图。

## Doubao

新增 DoubaoSource.cs。默认 `https://www.doubaozhibo.com`；固定 UA Chrome/122、Accept application/json、Accept-Language zh-CN。

- `GET /api/v1/schedules/public/local` → `data.days[].live[]`，比赛含 id、teamA/teamB、teamAImage、teamAscore/teamBscore、matchTime、league、status、playbackId。直播列表排除已有正 playbackId 的项，按状态和比赛时间排序；媒体 ID `live###{id}`。
- 四分类 live、replay_football、replay_basketball、replay_all。
- `GET /api/v1/playbacks?page={page}&pageSize=20&dataType={football|basketball|all}` → data `{list,page,pageSize,total}`；每条 `{schedule,playback}`，媒体 ID `replay###{playback.id}`。
- live detail 在 days 的 live 和 playback 两数组找 schedule.id，信号 `signals:[{name,playId}]`；线路“直播线路”。
- replay detail 按 Java 上限扫描 all 前 5 页，找 playback.id，播放 `playback.lines:[{title,proxyUrl}]`，`/` 相对 proxyUrl 加 origin；线路“回放线路”。
- 持久 Episode.Id 为线路名 `\u0001` 原始 playId/proxyUrl。
- 原始 play 字符串以 http 开头即直接返回；否则 GET `/play/{playId}`，将文本 `\u002F` 解成 `/`，正则抽 http(s) m3u8，优先含 rtmpcdn/live./pul-/gkykp 的地址。
- Java 抽不到地址会进入浏览器嗅探；C# 明确报缺少 m3u8，未将 HTML 当作媒体交给播放器。
- Java SN/tF 为一分钟缓存；C# 为正确刷新短时播放参数直接请求公开接口，暂未做缓存。Java 未实现 search，C# 返回空结果。

## Appgz

新增 AppGzCrypto.cs 和 AppGzSource.cs。**与 GuaziTY 不同协议**。Java Appgz 只有 init(Context)，无 ext 覆盖；C# Supports 只接收空或 null ext。默认固定 `https://api.gjjwgp3n.com`。

HTTP header 静态状态机展开结果：

- User-Agent okhttp/3.12.0
- Version 2603002
- PackageName com.r3ac892153.q1ed27199a.wf930fc6bf20260401
- Ver 和 api-ver 都是 3.0.4.0
- code GZ0001
- Referer https://api.gjjwgp3n.com

### 加密信封

Java Appgz.N / tF 和 merge.Xu：AES key/iv 为 init 随机16位 alnum 再大写，整个实例共用。AES-CBC/PKCS7(16-byte) 加密 UTF8 JSON，request_key 为**大写 hex**。RSA keys 包含 JSON `{iv,key}`，用 Java 内嵌 X509 公钥 PKCS1 v1.5、117-byte 分块加密、Base64 NO_WRAP。响应 data.keys 用 Java 内嵌 PKCS8 私钥按128-byte分块解密；响应 data.response_key 去反斜线、hex 解码后用响应 keys 的 key/iv AES 解密，得到真正 JSON。

form 字段 token、token_id 空、phone_type 1、time 秒、phone_model、keys、request_key、signature、app_id 1、ad_version 1。signature 是以下串 UTF8 MD5 **大写**：

`token_id=,token={token},phone_type=1,request_key={UPPERHEX},app_id=1,time={seconds},keys={RSA_BASE64}*&zvdvdvddbfikkkumtmdwqppp?|4Y!s!2br`

C# 所有 JSON 使用 Utf8JsonWriter，不依赖反射 JSON 序列化。Wrap 可注入 RSA 加密函数，Unwrap 可注入 RSA 解密函数，便于主 Agent 创建独立固定向量。

### 匿名设备

Java old_key 是 UTDID：18字节 = int32be 秒、int32be 随机数、byte3、byte0、int32be JavaHash(androidId)、int32be JavaHash(Base64(HMACSHA1(前14字节)))，HMAC key `d6fc3a4a06adbde89223bvefedc24fecde188aaa9161`，整体 Base64 无换行。

new_key 为设备 androidId、serial、Build 指纹 UUID 组合的 SHA1 大写。桌面无 Android Settings/Build，C# 采用持久随机 androidId 和固定 vodbox/desktop 指纹（serial 空），沿同一 SHA1 组合算法生成；这属于**明确的设备字段替代**，不是伪称 Android 真机 identity。token/new_key/old_key 存在 AppPaths.DataDirectory/appgz-device.json；构造不落盘，第一次实际请求才注册。

认证 `POST /App/Authentication/Device/signIn` `{old_key,new_key}`；空 token 后 `signUp` 追加 phone_type:1、code 空；再失败重试 signIn，与 Java 尝试顺序一致。认证请求 form token 空，正文也加密。

### 内容及播放

- `/App/IndexList/index` `{pid:"1"}`，响应 list[] 找 type 包含“热门推荐”，子 list[] `{vod_id,c_name,c_pic,new_continue}`。
- `/App/IndexList/indexList` `{area,sub,year,pageSize:"30",sort:"d_id",page,tid}`；空 area/year 为字符串“0”，sub 电影5、剧12、综艺22、其余30；响应 list[]。
- `/App/Index/findMoreVod` `{keywords,order_val:"1"}`，Java无分页。
- `/App/IndexPlay/playInfo` `{token_id:"",vod_id,mobile_time:seconds,token}` → vodInfo。
- `/App/Resource/Vurl/show` `{vurl_cloud_id:"2",vod_d_id}` → list[] `{title,play:{1080:{param},720:{param},480:{param}}}`；依序选择首个非空，固定“专线播放”。保留 `专线播放\u0001{param}` 的持久身份。
- `/App/Resource/VurlDetail/showOne` `{domain_type,vod_id,type,resolution,vurl_id}`，从 param 的 vod_d_id/vurl_id/domain_type/resolution/type 拆出；返回 url，UA Lavf/57.83.100。
- Java 将路径以 `/hls/index.m3u8` 结尾的地址视为防盗链账号失败，重做设备登录后重试；C# 也刷新认证并重取地址。

主 Agent 必须验证：独立 RSA/AES 解封向量、canonical signature、UTDID 字节顺序和 JavaHash、匿名设备服务端接受情况、所有内容数据结构及真实播放。Java DC.B m3u8本地代理清理路径未迁入 provider（由现有播放器/MediaProxy处理），可能还需针对真实坏片段扩展。当前不要把静态实现写成“已实播通过”。
