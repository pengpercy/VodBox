#!/usr/bin/env python3
"""Count exact Spider entry names in saved TV configurations without executing plugins."""

import argparse
import base64
import collections
import datetime
import gzip
import hashlib
import json
import pathlib
import urllib.parse


def json_without_comments(text):
    output = []
    position = 0
    quoted = False
    escaped = False
    while position < len(text):
        character = text[position]
        if quoted:
            output.append(character)
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                quoted = False
            position += 1
        elif character == '"':
            quoted = True
            output.append(character)
            position += 1
        elif text.startswith("//", position):
            end = text.find("\n", position)
            position = len(text) if end < 0 else end
        elif text.startswith("/*", position):
            end = text.find("*/", position + 2)
            if end < 0:
                raise ValueError("Unterminated JSON block comment")
            output.append(" ")
            position = end + 2
        elif character == ",":
            next_position = position + 1
            while next_position < len(text):
                if text[next_position].isspace():
                    next_position += 1
                elif text.startswith("//", next_position):
                    end = text.find("\n", next_position)
                    next_position = len(text) if end < 0 else end
                elif text.startswith("/*", next_position):
                    end = text.find("*/", next_position + 2)
                    if end < 0:
                        raise ValueError("Unterminated JSON block comment")
                    next_position = end + 2
                else:
                    break
            if next_position == len(text) or text[next_position] not in "]}":
                output.append(character)
            position += 1
        else:
            output.append(character)
            position += 1
    return "".join(output)


def read_configuration(location):
    original = pathlib.Path(location).read_bytes()
    if len(original) > 8 * 1024 * 1024:
        raise ValueError("Configuration exceeds 8 MiB")
    payload = original
    if payload.startswith(b"\x1f\x8b"):
        import io
        with gzip.GzipFile(fileobj=io.BytesIO(payload)) as stream:
            payload = stream.read(8 * 1024 * 1024 + 1)
    if payload.startswith(b"\xff\xd8"):
        end = payload.rfind(b"\xff\xd9")
        suffix = payload[end + 2:]
        if end < 0 or b"**" not in suffix:
            raise ValueError("JPEG has no supported trailing configuration")
        payload = base64.b64decode(suffix.split(b"**", 1)[1], validate=True)
    if len(payload) > 8 * 1024 * 1024:
        raise ValueError("Decoded configuration exceeds 8 MiB")
    configuration = json.loads(json_without_comments(payload.decode("utf-8-sig")))
    if not isinstance(configuration.get("sites"), list):
        raise ValueError("Configuration has no sites array")
    return configuration, hashlib.sha256(original).hexdigest()


def inventory(inputs):
    configurations = []
    plugins = {}
    for name, location in inputs:
        configuration, digest = read_configuration(location)
        counts = collections.Counter()
        for index, site in enumerate(configuration["sites"], 1):
            api = site.get("api", "")
            if not isinstance(api, str):
                raise ValueError("Site api must be a string")
            if api.startswith("csp_"):
                plugin, kind = api, "java-spider"
            elif urllib.parse.urlparse(api).path.endswith(".js"):
                plugin = "script:" + pathlib.PurePosixPath(urllib.parse.urlparse(api).path).name
                kind = "javascript"
            else:
                plugin, kind = api or "(missing-api)", "other"
            counts[kind] += 1
            entry = plugins.setdefault(plugin, {"plugin": plugin, "kind": kind, "count": 0, "bySource": {}, "sites": [], "jarVariants": []})
            jar = (site.get("jar") or configuration.get("spider") or "") if kind == "java-spider" else ""
            variant = hashlib.sha256(jar.encode("utf-8")).hexdigest()[:12] if isinstance(jar, str) and jar else None
            entry["count"] += 1
            entry["bySource"][name] = entry["bySource"].get(name, 0) + 1
            entry["sites"].append({"source": name, "index": index, "key": site.get("key", ""), "name": site.get("name", ""), "jarVariant": variant})
            if variant is not None and variant not in entry["jarVariants"]:
                entry["jarVariants"].append(variant)
        configurations.append({"name": name, "sha256": digest, "sites": len(configuration["sites"]), "liveSources": len(configuration.get("lives", [])), "kinds": dict(counts)})
    ordered = sorted(plugins.values(), key=lambda item: (-item["count"], -len(item["bySource"]), item["plugin"]))
    return {"capturedAt": datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).isoformat(timespec="seconds"), "countingUnit": "site-entry", "grouping": "Java: exact api name, Guard variants not merged; JavaScript: engine filename", "sources": configurations, "totalSites": sum(item["sites"] for item in configurations), "uniquePlugins": len(ordered), "plugins": ordered}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", action="append", required=True, metavar="LABEL=FILE")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    inputs = [value.split("=", 1) for value in args.source]
    if any(len(item) != 2 for item in inputs) or len({item[0] for item in inputs}) != len(inputs):
        parser.error("Each source needs a unique LABEL=FILE")
    report = inventory(inputs)
    pathlib.Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"totalSites": report["totalSites"], "uniquePlugins": report["uniquePlugins"], "sources": report["sources"], "mostUsed": [{key: item[key] for key in ("plugin", "count", "bySource", "jarVariants")} for item in report["plugins"][:12]]}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
