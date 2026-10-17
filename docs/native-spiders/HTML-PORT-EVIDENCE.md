# HTML plugin port evidence (Auete / Libvio / NewCz)

Implementation is based on fixed payload Java decompilation, not on name-based template guesses. No build, test, original-plugin Oracle, or live network probe was run by the implementation subagent. Main agent owns all validation and registration.

## Auete

Source: `payload/clean/com/github/catvod/spider/Auete.java`.

- Default `https://auete.cc/`; JSON `siteUrl` override. Navbar `/type/index.html` names restricted to 电影/电视剧/综艺/动漫/其他.
- Categories `Movie`, `Tv`, `Zy`, `Dm`, `qita`; exact subtype map and filter key `0` ported from Java `N`.
- Category `/{category}/{optionalSubtype}/index{optionalPage}.html`; search `auete4so.php?searchword=...&page=...`.
- Cards `ul.threadlist li`, `h2 a[title]`, `a.pic img[src]`, `span.hdtag`. Java's `/jqp/` exclusion is preserved.
- Detail `h1.detail-title`, `div.detail-main-card` `data-pname/data-ppic` fallback; metadata labels; `div.play-card#player_list`, `div.card-header span b` and `ul.episode-list li > a`. Line name map copied from `SN`; Java's all-999 unstable sort is replaced by document order.
- Playback `var now="..."` or `var now=base64("...")`; padding before decoding; relative m3u8 uses the literal long `datas-s8...cache.haozhansou.com` origin from Java.
- **DC dependency:** `merge/DC.java:q` calls `B(url,null)`, which returns a local ProxyOrigin URL `?do=m3u8&url={encoded}`. It is not cryptographic decoding. Native output supplies the remote URL and `ProxyMedia=true` for HLS so the existing C# media proxy rewrites segment/key URLs.
- **Not equivalent:** Java DC's playlist heuristics (`G` and dependencies) remove suspected advertising segments. That behavior is not ported. Native HLS proxy handles transport/relative references, not equivalent ad filtering. Playback equivalence requires main-agent media acceptance.

## Libvio anonymous subset

Source: `/private/tmp/vodbox-protocol-read/Libvio.java` (local string-expanded decompilation).

- Default `https://www.libvio.io/`, JSON `siteUrl`; HTML `/type/{id}.html` categories.
- Category 12-slot `/show/{slots}.html`, category at 0, page at 8. Numeric filter positions match Java. No filter UI advertised until dynamic filter extraction is ported.
- Cards `div.stui-vodlist__box`, anchor `title/data-original/href`, `.pic-text`.
- Search `/index.php/ajax/suggest?mid=1&wd=...&limit=10&timestamp=...`; list JSON is a string in Java, accepts array form too; IDs `/detail/{id}.html`.
- Detail `stui-content__thumb`, `stui-content__detail h1`, `meta-item`, `span.detail-content`, `div.playlist-panel`, `div.panel-head`, `/play/...html` episode links.
- Excludes cloud/account line labels. Playback reads `var player_*` JSON; only literal HTTP(S) media whose path ends `.m3u8`/`.mp4` is returned, with Java's root Referer and Linux UA.
- **Remaining:** plugin-specific `from` parsers (`cczz`, `zxyy`, `gwj`, `lbyy`, etc.), parser-side cookies/challenges, cloud-drive inheritance, dynamic filter extraction. Non-direct media throws NotSupported; it is not labeled resolved.

## NewCz anonymous subset

Source: `/private/tmp/vodbox-protocol-read/NewCz.java`; cipher dependency `merge/C0179rS.java:yq/SN`.

- Default `https://www.4kcz.com/`; fixed category names and `category/page/{page}` from `homeContent/categoryContent`.
- Cards `.mi_ne_kd > ul > li`, first anchor href, image alt and data-original, `.hdinfo`.
- Java `merge/U7.q` advertises MAX_VALUE pages on any nonempty results. Native advertises one next page for nonempty results instead of an enormous page count.
- Search uses Java's distinct `http://czzy.xn--t75a.eu.org/czzysearch.php?wd=...`; rows `$$$`, fields `|`, columns id/title/poster/remarks. IDs `movie/{id}.html`.
- Detail `.moviedteail_tt h1`, `.dyimg img`, `ul.moviedteail_list li`, `.yp_context`, `.paly_list_btn a`, one 专线.
- Anonymous `dncry` branch extracts ciphertext/key/numeric IV and decrypts AES/CBC/PKCS7 Base64 (exact C0179rS.yq) then reads `video: {url: "..."}`. Origin and Linux UA carried to playback.
- **Remaining:** dynamic mirror discovery (`m1115/m1127`), login/session refresh, alternate parser branches (yangtu, nowwon, t0o1, iframe `var player`, `result_v2`), and Java's LAN `192.168.111.200:3000/cz` helper. These branches must be ported or classified unavailable separately. Account prompt throws NotSupported.

## Shared implementation constraints

All fetches bound to 4 MiB, use HTTP decompression, cancellation and a 20-second timeout; regex operations have a 2-second timeout. Class-body extraction balances same-name opening/closing tags to handle nested div/ul. It remains a fixed HTML template parser, not a general browser DOM: malformed markup, script text containing literal HTML-like tags, and changed site layouts can require updates. Fresh detail validation precedes playback selection. No browser JavaScript execution or credentials are embedded.

## Follow-up: durable identity and parser expansion

All three providers now expose `Supports(ext)` with conservative unknown-field rejection. Auete accepts `siteUrl/filterSpeedUp`, Libvio accepts `siteUrl`, NewCz accepts only empty `login` (nonempty credentials are not silently consumed). The app must apply Supports during registration.

Episode IDs now follow JarPlugin `flag + U+0001 + play`; resolver refreshes detail membership before extracting the play component. Auete keeps raw relative play; Libvio keeps original panel label and Java's literal host+href concatenation; NewCz keeps 专线 and original href with leading root preserved or plugin host removed for absolute href. These exact forms matter for persisted history.

Libvio now includes the Java `vid/yd.php?url=...&nid=...&id=...` parser family (`cczz/zxyy/gwj/lbyy/NBY/03yy`), optional `fetch(...)` follow-up, `urls:` and JSON URL extraction; `vr2` uses `vid/plyr/vr2.php` with `var vid` or `urls`; `ty_new1` uses `vid/ty4.php`, JSON unescaping of parseUrl/sid, and JSON POST of url/sid. Its original fixed filter JSON is now copied exactly from Java instead of leaving filters empty. The constructor's optional third delegate injects POST for main-agent fixtures.

NewCz additionally follows the iframe path: `var player` + `var rand` uses AES/CBC key `VFBTzdujpR9FWBhe`; relative decrypted url receives plugin `https://t02.cz01.org` prefix. `var url`/`result_v2.data` reverse the hex string, decode pairs and remove seven inserted center characters. The yangtu branch (returns jx=1 for external parser) and plugin LAN helpers remain unported rather than generating a false direct address.

## YCyz

`YCyzSource.cs` adds the fixed Java catalogue (`https://v.xl01.eu.cc/`), exact original category/filter data, `/s/{s}/{page}?&type=...&area=...&year=...&order=...`, `movie-card` cards, `bidisearch.php` search, info-list metadata, play-list episodes with original `自动 + U+0001 + href.substring(1)` IDs, and signed anonymous `lines` API.

Signature confirmed from Java `ME.zH` and `YCyz.yq`: input `pid + '-' + epochMilliseconds`, key is lowercase MD5 hex first 16 characters, AES/ECB/PKCS7 encrypts UTF8 input, output uppercase ciphertext hex. URL fields `url3`, `m3u8`, `m3u8_2` yield playback choices; fixed `www.bde4.cc` host is substituted as in Java. Media transport is delegated to C# proxy with ProxyMedia=true.

Remaining YCyz equivalence work: Java dynamically probes and ranks candidates through `N`, performs specialized CDN rewrites, and local `?do=m3u8&csp=YCyz` proxy rewriting. Native currently preserves all returned choices and uses C# proxy; it does not claim identical ranking, Java image-CDN rewrite, or ad filtering. Needs original Oracle and playable-media acceptance by main agent.
