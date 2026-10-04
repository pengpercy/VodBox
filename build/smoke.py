"""Exercise actual AOT executables and bundled workers, independent of a GUI session."""
import argparse
import json
import os
from pathlib import Path
import subprocess

def smoke(rid, directory):
    windows = rid.startswith("win")
    exe = ".exe" if windows else ""
    environment = dict(os.environ)
    if rid.startswith("linux"):
        environment["LD_LIBRARY_PATH"] = str(directory / "native/vlc/lib") + (":" + environment["LD_LIBRARY_PATH"] if environment.get("LD_LIBRARY_PATH") else "")
    result = subprocess.run([str(directory / ("VodBox" + exe)), "--diagnostics", "--native"],
                            env=environment, capture_output=True, text=True, encoding="utf-8", timeout=60)
    print(result.stdout, end="")
    if result.returncode:
        raise RuntimeError(result.stderr)
    mac_resources = directory.parent / "Resources/vodbox"
    assets = mac_resources if rid.startswith("osx") and mac_resources.is_dir() else directory
    host = directory.parent / "Helpers/plugin-host" if assets == mac_resources else directory / "plugin-host"
    examples = assets / "examples"
    workers = [
        ([host / ("VodBox.PluginHost" + exe), examples / "demo.js"], "QuickJS"),
        ([assets / "runtimes/python" / ("python.exe" if windows else "bin/python3"), "-B", assets / "plugins/python/worker.py", examples / "demo.py"], "Python"),
        ([assets / "runtimes/node" / ("node.exe" if windows else "bin/node"), assets / "plugins/node/worker.mjs", examples / "demo.mjs"], "Node")
    ]
    operations = [("init", {"mediaUri": "https://example.com/sample.mp4"}), ("categories", {}),
                  ("items", {}), ("search", {"query": "missing"}), ("detail", {"mediaId": "sample"}),
                  ("resolvePlayback", {"mediaId": "sample", "episodeId": "main"})]
    requests = "".join(json.dumps({"apiVersion": 1, "requestId": index + 1, "sourceId": "smoke", "method": method, "params": params}) + "\n"
                       for index, (method, params) in enumerate(operations))
    for command, name in workers:
        result = subprocess.run([str(x) for x in command], input=requests, env=environment,
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        if result.returncode:
            raise RuntimeError(f"{name}: {result.stderr}")
        responses = [json.loads(line) for line in result.stdout.splitlines()]
        if len(responses) != len(operations): raise ValueError(f"{name}: wrong reply count")
        for index, response in enumerate(responses):
            if response.get("error") or response["requestId"] != index + 1 or response["apiVersion"] != 1:
                raise ValueError(f"{name}: invalid response {response}")
        if responses[3]["result"]["items"] != [] or responses[-1]["result"]["uri"] != "https://example.com/sample.mp4":
            raise ValueError(f"{name}: wrong search or resolve result")
        print(name + " bundled worker: OK")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("rid"); parser.add_argument("directory", type=Path)
    args = parser.parse_args(); smoke(args.rid, args.directory.resolve())
