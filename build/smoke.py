"""AOT 发布产物的冒烟验证：运行程序带 --diagnostics 参数自检 libmpv 加载与播放器状态。"""
import argparse
import subprocess
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("rid")
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    exe = "VodBox.exe" if args.rid.startswith("win") else "VodBox"
    app = args.directory / exe
    if not app.exists():
        # macOS app bundle 形态
        app = args.directory.parent / "MacOS" / "VodBox"
    result = subprocess.run([str(app), "--diagnostics"], capture_output=True, text=True,
                            encoding="utf-8", timeout=60)
    print(result.stdout, end="")
    if result.returncode:
        raise RuntimeError(result.stderr)


if __name__ == "__main__":
    main()
