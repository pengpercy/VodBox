# 合成媒体解码样片

这些 6 秒纯色画面及 440 Hz 音频是 VodBox 回归测试自行生成的素材，按 CC0 1.0 提供；不含第三方影视内容。用于真实验证 libmpv 的 H.264/AAC、HEVC/AAC、VP9/Opus、AV1/Opus 软件解码和 seek。它们仅用于构建测试，不复制进安装包。

生成输入：`-f lavfi -i color=c=blue:s=64x64:r=10:d=6 -f lavfi -i sine=frequency=440:sample_rate=16000:duration=6`。

编码选项分别为 `-c:v libx264 -preset ultrafast -c:a aac -b:a 16k`、`-c:v libx265 -preset ultrafast -x265-params pools=none:frame-threads=1:log-level=error -c:a aac -b:a 16k`、`-c:v libvpx-vp9 -threads 1 -c:a libopus -b:a 16k`、`-c:v libaom-av1 -cpu-used 8 -threads 1 -c:a libopus -b:a 16k`；MP4 加 `-movflags +faststart`，VP9/AV1 使用 WebM。H.264 样片画面尺寸为 32×32，其余为 64×64。
