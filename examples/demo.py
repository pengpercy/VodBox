options = {}
card = {"id": "sample", "title": "Python 插件示例", "remarks": "独立进程 NDJSON"}

def init(value):
    global options
    options = value
    return {}

def categories(args):
    return [{"id": "demo", "name": "协议示例"}]

def items(args):
    return {"items": [card], "nextCursor": None}

def search(args):
    return {"items": [card] if (args.get("query") or "").lower() in card["title"].lower() else []}

def detail(args):
    return {"item": card, "description": "在配置 options.mediaUri 中填写自己的媒体地址。", "playbackLines": [{"id": "main", "name": "主线路", "episodes": [{"id": "main", "title": "播放"}]}]}

def resolvePlayback(args):
    if not options.get("mediaUri"):
        raise ValueError("请先配置 options.mediaUri")
    return {"uri": options["mediaUri"], "title": card["title"]}
