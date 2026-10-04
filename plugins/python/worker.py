"""VodBox apiVersion=1 worker. stdout is exclusively NDJSON; plugin prints go to stderr."""
import asyncio
import contextlib
import importlib.util
import inspect
import json
import sys
import traceback

# Windows redirected pipes may otherwise use an ANSI code page, breaking Chinese metadata.
sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")

spec = importlib.util.spec_from_file_location("vodbox_provider", sys.argv[1])
provider = importlib.util.module_from_spec(spec)
with contextlib.redirect_stdout(sys.stderr):
    spec.loader.exec_module(provider)

for line in sys.stdin:
    request_id = 0
    try:
        request = json.loads(line)
        request_id = request["requestId"]
        if request["apiVersion"] != 1:
            raise ValueError("Unsupported apiVersion")
        with contextlib.redirect_stdout(sys.stderr):
            result = getattr(provider, request["method"])(request["params"])
            if inspect.isawaitable(result):
                result = asyncio.run(result)
        response = {"apiVersion": 1, "requestId": request_id, "result": result if result is not None else {}}
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        response = {"apiVersion": 1, "requestId": request_id, "error": str(error)}
    print(json.dumps(response, ensure_ascii=False), flush=True)
