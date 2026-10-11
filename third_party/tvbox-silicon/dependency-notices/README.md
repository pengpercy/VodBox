# 原 Maven runtime 依赖许可资料

`runtime-dependency-index.json` 为当前 59 个 runtime JAR 保存 SHA-256、原始缓存 POM、父 POM 链、有效 license/SCM 声明与原 JAR LICENSE/NOTICE 路径。各 artifact 目录保存原文，`upstream/` 来自 dependency-sources 中的固定 release/tag 档案。

JAR 内存在的 META-INF LICENSE/NOTICE 均已保留，另保留 okhttp publicsuffix NOTICE 与 xmlParserAPIs 的 license 目录。没有内嵌文本的其他宽松依赖保留 Maven license/SCM 原始索引；不把索引存在描述成已下载所有许可证。BouncyCastle 额外保存实际 JAR 内 LICENSE.class 和 UTF8 常量池中的许可字符串（无类执行），并保留 r1rv78v1 固定提交的 LICENSE.html。

unidbg 的根 LICENSE 放在 unidbg-unicorn2-0.9.8/upstream/LICENSE，api/android 的索引指向该原文。该 Java 工程许可不能自动覆盖所有嵌入的第三方 native 文件；完整 native 源码/许可闭包状态见 dependency-sources/native-source-manifest.json。

demumble Maven GPL V2 与源仓库 Apache/LLVM 声明存在差异，资料并存，不推定许可替换。JNA 双许可与随附 libffi 文本并存，不作许可选用决策。未更改项目许可。

SHA256SUMS 排除自身，列出本目录所有已保存文件的 SHA-256。

新增 `unidbg-android-0.9.8/NATIVE-LICENSE-STATUS.md` 与 `upstream-reference/`：xHook 历史候选 MIT/BSD 文本、Dobby/Whale 当前参考 Apache 文本。仅为研究参考，不代表嵌入历史二进制许可/版本闭合，不能覆盖 SDK/HookZz 或静态 runtime。
