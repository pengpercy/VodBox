# 原 Maven runtime native 源码资料

总状态 **pending**，不能据此宣称完整 corresponding source 交付闭包已完成。主索引：`native-source-manifest.json`；所有实际 native 文件：`runtime-native-binary-index.json`；来源证据与逐项比对：`upstream-evidence/`；固定提交源档案：`archives/`。

已保存并核对：

- unicorn 1.0.14：release commit `15cad005ed027685e5e3da429eb84325fba55f8a`，6/6 native 字节匹配，412 个 C/C++ 文件。
- keystone 0.9.7：release commit `c0bfeb3ea47923dee2b530257ec1989f0fbc4201`，5/5 native 字节匹配，472 个 C/C++ 文件。
- demumble 1.0.4：release commit `cb6f349ba21f54e01936a06ea8e826caa1f4a702`，4/4 native 字节匹配，57 个 C/C++ 文件。POM GPL V2 与仓库 Apache/LLVM 许可声明不一致，保留双方资料，待澄清。
- unidbg-unicorn2 0.9.8：tag `v0.9.8` → `c4ced05858a7878f3c68f6df465ae02fa59f6759`，5/5 native 字节匹配；外部 Unicorn2 静态库源码 commit 未固定，**源码闭包 pending**。不可拿 unicorn 1.0.14 的引擎档案替代。
- JNA 5.10.0：tag `5.10.0` → `8230fd9c023246e331601690a2afb82f37e6d4b6`，23/23 native 字节匹配；档案含 native/libffi 源码，Apache/LGPL 与 libffi 文本均保留。

前三项 release POM 与缓存 POM 字节一致。发布源档案包含原始 native 二进制，并不证明每个平台二进制由同一提交的源码、无本地修改生成；各平台构建 provenance 未验证，manifest 明确保留 pending。保存了 SCM commits/tags/tree API 证据；这些档案均无 gitlink 子模块，unicorn2 的外部引擎引用仍不在档案内。

四个 Maven source.jar 均无 native C/C++ 源码，只是 Java 包装层资料，不能当作 native 完整源码。

unidbg-android 的 34 个 guest SDK/hook native 文件已单独列入索引，精确 Android/第三方源版本与许可来源仍 pending。capstone 保留缓存 POM BSD 许可与 SCM 索引。

SHA256SUMS 覆盖本目录所有资料（排除该文件自身）；dependency-notices/SHA256SUMS 覆盖许可目录。源码压缩档案不展开到主项目，不修改运行代码或主许可。本轮未运行 build/test/typecheck。

## Native provenance follow-up

详见 `upstream-evidence/native-provenance/REPORT.md`：34/34 Android guest 库与 release 档案字节对应已确认（sdk19/sdk23，并非 API 34）；Unicorn2 外部 engine commit 仍未确证。xHook v1.1.12 有历史升级证据但之后二进制再次修改。新增 xHook 历史候选及 Dobby/Whale 当前参考完整源码档案与独立许可文本，**全部不冒充最终二进制对应源码**。主清单保留 pending，并记录逐二进制 fail-closed 发布关卡建议；未实现主 build 关卡。
