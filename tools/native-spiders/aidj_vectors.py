"""Optional offline oracle: evaluate pure calculations extracted from AiDj's embedded Python.

Usage: python3 aidj_vectors.py /path/to/extracted.py output.json
No imports, Spider class, HTTP server, or request functions are executed from the payload.
"""
import ast
import base64
import binascii
import bisect
import hashlib
import inspect
import json
import lzma
import pathlib
import random
import re
import struct
import sys
import threading
import time
import types
import typing
import __future__
from collections.abc import Mapping
from html.parser import HTMLParser
from urllib.parse import urlencode, quote, urlparse

source = pathlib.Path(sys.argv[1]).read_text()
tree = ast.parse(source)
nodes = [node for node in tree.body if isinstance(node, (ast.FunctionDef, ast.ClassDef, ast.Assign, ast.AnnAssign, ast.For))
         and not (isinstance(node, ast.ClassDef) and node.name in ("Spider", "_StreamSession", "_StreamHandler"))]
scope = dict(globals(), Any=typing.Any, AES_BACKEND="none", hashes=None)
exec(compile(ast.Module(body=nodes, type_ignores=[]), "aidj-pure-reference", "exec", flags=__future__.annotations.compiler_flag), scope)
result = {"sourceSha256": hashlib.sha256(source.encode()).hexdigest(), "sm3": [], "gorgon": [], "helios": [], "hashF13": [], "aesV3": [], "sign": []}
for value in ("", "abc", "中文", "abcd" * 40):
    result["sm3"].append({"input": value, "output": scope["SM3"](value.encode()).digest().hex()})
for body in ("", '{"中文":"一","ids":[1,2]}'):
    for timestamp in (1792260000, 1792260001):
        result["gorgon"].append({"body": body, "query": "a=1&b=%E4%B8%AD", "timestamp": timestamp, "random": 1234,
            "output": scope["_encrypt_gorgon"](body, "a=1&b=%E4%B8%AD", timestamp, 1234, "application/json; charset=UTF-8")})
for timestamp in range(1792260000, 1792260004):
    result["helios"].append({"timestamp": timestamp, "random": 123456, "output": scope["_encrypt_helios"](timestamp, 123456)})
    raw = bytes(range(256)) * 2
    key, iv = bytes(range(16)), bytes(range(16, 32))
    result["aesV3"].append({"input": raw.hex(), "key": key.hex(), "iv": iv.hex(), "timestamp": timestamp,
        "output": scope["AES_V3"](key, timestamp).encrypt(raw, iv).hex()})
for index in range(24):
    timestamp = 1792260000 + index
    query_hash = scope["SM3"](("a=" + str(index)).encode()).digest()
    body_hash = hashlib.md5(str(index).encode()).digest()
    result["hashF13"].append({"querySm3": query_hash.hex(), "bodyMd5": body_hash.hex(), "timestamp": timestamp,
        "output": scope["hash_f13"](query_hash, body_hash, timestamp.to_bytes(4, "little"), timestamp).hex()})
for body in ("", '{"中文":"一","ids":[1,2]}'):
    parameters = {"device_id": "initial", "aid": "8662", "version_name": "initial", "keyword": "中文 * ~"}
    devices = {"device_id": "123456789012345678", "version_name": "7.1.3.32", "device_model": "fixture", "device_brand": "fixture", "os_version": "12", "ua": "fixture-agent", "x_tt_dt": ""}
    values = iter((1234, 123456, 0x12345678, 111, 11000, 0xABCDEF01, 0x01234567))
    scope["random"] = types.SimpleNamespace(randint=lambda low, high: next(values))
    scope["time"] = types.SimpleNamespace(time=lambda: 1792260000.125)
    headers = {"Content-Type": "application/json; charset=UTF-8", "User-Agent": "original"}
    signed_headers, url = scope["_core_sixgod"]("https://api.example/model", parameters, devices, body, headers)
    result["sign"].append({"endpoint": "https://api.example/model", "parameters": parameters, "devices": devices, "body": body, "headers": headers, "url": url, "signedHeaders": signed_headers})
pathlib.Path(sys.argv[2]).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
print("wrote independent pure calculation vectors")
