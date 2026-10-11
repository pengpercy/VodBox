"""生成**真实**的 DEX fixture（不是占位字节），供 DEX→JVM 转换的真实验收使用。

背景：C# 侧的离线单测用假转换器注入成功/失败，证明不了「真能转换 DEX」。要证明这一点，
必须有一个**真 DEX** 喂给真实转换引擎（dex2jar）+ 宿主 `--convert` 子命令。本脚本按 DEX 格式
（little-endian：header / string_ids / type_ids / proto_ids / method_ids / class_defs / data + map_list）
独立编码一个最小但合法的 DEX；类里带真实方法体（构造器 invoke-direct、const-string、return），
因此 dex2jar 必须真的做字节码翻译才能得到可加载的 `.class`。

本脚本只**生成/解析文件**，不编译、不运行测试、不跑 Java；产物写在指定工作区目录。

用法：
  python3 build/dex_fixture.py --out tests/VodBox.Tests/fixtures/dex
  python3 build/dex_fixture.py --out DIR --verify-only     # 只解析已有产物做结构自检

产物：
  sample-single.dex      单 DEX（类 dev.vodbox.fixture.Sample）
  sample-multidex.zip    classes.dex(Sample) + classes2.dex(Second) 的多 DEX 归档（含一个非 DEX 条目）
  fingerprint.json       各产物 sha256 / 大小 / 类名，供验收断言与缓存键对照
"""
import argparse
import hashlib
import json
import struct
import sys
import zipfile
import zlib
from pathlib import Path

DEX_MAGIC = b"dex\n"
DEX_VERSION = b"035"
HEADER_SIZE = 0x70
ENDIAN_TAG = 0x12345678

ACC_PUBLIC = 0x1
ACC_SUPER = 0x20
ACC_STATIC = 0x8
ACC_CONSTRUCTOR = 0x10000

OP_RETURN_VOID = 0x0E
OP_RETURN = 0x0F
OP_RETURN_OBJECT = 0x11
OP_CONST_16 = 0x13
OP_CONST_STRING = 0x1A
OP_INVOKE_DIRECT = 0x70

SECOND_CLASS = "dev.vodbox.fixture.Second"
SAMPLE_CLASS = "dev.vodbox.fixture.Sample"


def uleb128(value: int) -> bytes:
    if value < 0:
        raise ValueError("uleb128 不接受负数")
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def read_uleb(data: bytes, cursor: int) -> tuple[int, int]:
    result = 0
    shift = 0
    while True:
        byte = data[cursor]
        cursor += 1
        result |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return result, cursor
        shift += 7


class Dex:
    """按 DEX 格式编码一个含单类的最小合法文件。"""

    def __init__(self, class_name: str, string_value: str):
        self.class_name = class_name
        self.descriptor = "L" + class_name.replace(".", "/") + ";"
        self.string_value = string_value
        self.source_file = class_name.rsplit(".", 1)[-1] + ".java"
        # string_ids 必须按字符串值升序（Android 约定）。集合去重后排序即可。
        self.strings = sorted(set([
            "<init>", "()V", "answer", "()I", "greet", "()Ljava/lang/String;",
            "I", "L", "L" + class_name.replace(".", "/") + ";",
            "Ljava/lang/Object;", "Ljava/lang/String;", "V",
            self.source_file, string_value,
        ]))
        self.sidx = {text: index for index, text in enumerate(self.strings)}

    # ---- 有序表（type/proto/method 各自按规范键升序） ----

    def type_table(self) -> tuple[list[int], dict[str, int]]:
        descriptors = ["I", self.descriptor, "Ljava/lang/Object;", "Ljava/lang/String;", "V"]
        order = sorted(descriptors, key=lambda name: self.sidx[name])
        ids = [self.sidx[name] for name in order]
        return ids, {name: ids.index(self.sidx[name]) for name in descriptors}

    def proto_table(self, type_index: dict[str, int]) -> list[tuple[int, int]]:
        # (return_type, 短名字符)；按 return_type_idx 升序。
        protos = [("I", "()I"), ("Ljava/lang/String;", "()Ljava/lang/String;"), ("V", "()V")]
        return sorted((type_index[return_type], self.sidx[shorty]) for return_type, shorty in protos)

    def method_table(self, type_index: dict[str, int], proto_index: dict[str, int]) -> list[tuple[int, int, int]]:
        rows = [
            (type_index[self.descriptor], self.sidx["<init>"], proto_index["()V"]),
            (type_index[self.descriptor], self.sidx["answer"], proto_index["()I"]),
            (type_index[self.descriptor], self.sidx["greet"], proto_index["()Ljava/lang/String;"]),
            (type_index["Ljava/lang/Object;"], self.sidx["<init>"], proto_index["()V"]),
        ]
        return sorted(rows)

    def build(self) -> bytes:
        type_ids, type_index = self.type_table()
        proto_rows = self.proto_table(type_index)
        proto_index = {"()I": 0, "()Ljava/lang/String;": 1, "()V": 2}
        method_rows = self.method_table(type_index, proto_index)

        def method_index_of(descriptor: str, name: str, proto: int) -> int:
            for index, (class_idx, name_idx, proto_idx) in enumerate(method_rows):
                if class_idx == type_index[descriptor] and name_idx == self.sidx[name] and proto_idx == proto:
                    return index
            raise AssertionError("方法未在 method_ids 中")

        sample_init = method_index_of(self.descriptor, "<init>", proto_index["()V"])
        sample_answer = method_index_of(self.descriptor, "answer", proto_index["()I"])
        sample_greet = method_index_of(self.descriptor, "greet", proto_index["()Ljava/lang/String;"])
        object_init = method_index_of("Ljava/lang/Object;", "<init>", proto_index["()V"])

        # 方法体（DEX 指令，单位为 16-bit code unit）。
        init_insns = struct.pack("<3H", (1 << 12) | OP_INVOKE_DIRECT, object_init, 0x0000)
        init_insns += struct.pack("<H", OP_RETURN_VOID)
        answer_insns = struct.pack("<HH", OP_CONST_16, 42) + struct.pack("<H", OP_RETURN)
        greet_insns = struct.pack("<HH", OP_CONST_STRING, self.sidx[self.string_value]) + struct.pack("<H", OP_RETURN_OBJECT)
        # (method_index, registers_size, ins_size, outs_size, insns)
        codes = [
            (sample_init, 1, 1, 1, init_insns),
            (sample_answer, 1, 0, 0, answer_insns),
            (sample_greet, 1, 0, 0, greet_insns),
        ]

        # ---- 布局 ----
        cursor = HEADER_SIZE
        string_ids_off = cursor
        cursor += 4 * len(self.strings)
        type_ids_off = cursor
        cursor += 4 * len(type_ids)
        proto_off = cursor
        cursor += 12 * len(proto_rows)
        method_ids_off = cursor
        cursor += 8 * len(method_rows)
        class_defs_off = cursor
        cursor += 32
        data_off = cursor

        data = bytearray()
        # string_data_item
        string_data_off = data_off
        string_offsets = []
        for text in self.strings:
            string_offsets.append(data_off + len(data))
            encoded = text.encode("utf-8")
            data += uleb128(len(text))
            data += encoded
            data += b"\x00"
        # code_item（4 字节对齐）
        code_off: dict[int, int] = {}
        for method, registers, ins, outs, insns in codes:
            while (data_off + len(data)) % 4:
                data += b"\x00"
            code_off[method] = data_off + len(data)
            data += struct.pack("<4H2I", registers, ins, outs, 0, 0, len(insns) // 2)
            data += insns
        # class_data_item
        while (data_off + len(data)) % 4:
            data += b"\x00"
        class_data_off = data_off + len(data)
        data += uleb128(0)  # static fields
        data += uleb128(0)  # instance fields
        data += uleb128(3)  # direct methods
        data += uleb128(0)  # virtual methods
        previous = 0
        for method, access in ((sample_init, ACC_PUBLIC | ACC_CONSTRUCTOR),
                               (sample_answer, ACC_PUBLIC | ACC_STATIC),
                               (sample_greet, ACC_PUBLIC | ACC_STATIC)):
            data += uleb128(method - previous)
            data += uleb128(access)
            data += uleb128(code_off[method])
            previous = method
        # map_list（自身条目也要在表里；按 offset 升序）
        map_off = data_off + len(data)
        entries = [
            (0x0000, 1, 0),
            (0x0001, len(self.strings), string_ids_off),
            (0x0002, len(type_ids), type_ids_off),
            (0x0003, len(proto_rows), proto_off),
            (0x0005, len(method_rows), method_ids_off),
            (0x0006, 1, class_defs_off),
            (0x2002, len(self.strings), string_data_off),
            (0x2000, 1, class_data_off),
            (0x2001, len(codes), min(code_off.values())),
            (0x1000, 1, map_off),
        ]
        entries.sort(key=lambda item: item[2])
        data += struct.pack("<I", len(entries))
        for kind, size, offset in entries:
            data += struct.pack("<HHII", kind, 0, size, offset)

        file_size = data_off + len(data)
        header = bytearray(HEADER_SIZE)
        header[0:8] = DEX_MAGIC + DEX_VERSION + b"\x00"
        struct.pack_into("<I", header, 32, file_size)
        struct.pack_into("<I", header, 36, HEADER_SIZE)
        struct.pack_into("<I", header, 40, ENDIAN_TAG)
        struct.pack_into("<I", header, 52, map_off)
        struct.pack_into("<I", header, 56, len(self.strings))
        struct.pack_into("<I", header, 60, string_ids_off)
        struct.pack_into("<I", header, 64, len(type_ids))
        struct.pack_into("<I", header, 68, type_ids_off)
        struct.pack_into("<I", header, 72, len(proto_rows))
        struct.pack_into("<I", header, 76, proto_off)
        struct.pack_into("<I", header, 88, len(method_rows))
        struct.pack_into("<I", header, 92, method_ids_off)
        struct.pack_into("<I", header, 96, 1)
        struct.pack_into("<I", header, 100, class_defs_off)
        struct.pack_into("<I", header, 104, file_size - data_off)
        struct.pack_into("<I", header, 108, data_off)

        for offset in string_offsets:
            header += struct.pack("<I", offset)
        for descriptor_index in type_ids:
            header += struct.pack("<I", descriptor_index)
        for return_type, shorty in proto_rows:
            header += struct.pack("<3I", shorty, return_type, 0)
        for class_idx, name_idx, proto_idx in method_rows:
            header += struct.pack("<HHI", class_idx, proto_idx, name_idx)
        header += struct.pack("<8I",
                              type_index[self.descriptor], ACC_PUBLIC | ACC_SUPER, type_index["Ljava/lang/Object;"],
                              0, self.sidx[self.source_file], 0, class_data_off, 0)

        image = bytes(header) + bytes(data)
        assert len(image) == file_size, (len(image), file_size)
        return seal(image)

    # ---- header 尾部：signature(20B, offset 12) + checksum(u4, offset 8) ----

    @staticmethod
    def verify(image: bytes) -> dict:
        return parse(image)


def seal(image: bytes) -> bytes:
    body = bytearray(image)
    body[12:32] = hashlib.sha1(body[32:]).digest()
    struct.pack_into("<I", body, 8, zlib.adler32(body[12:]) & 0xFFFFFFFF)
    return bytes(body)


# ---------------------------------------------------------------- 独立解析自检

def parse(image: bytes) -> dict:
    """不依赖转换引擎地再解析一遍，确认结构自洽（uleb128、偏移、map_list、类数据）。"""
    if image[0:4] != DEX_MAGIC or image[7] != 0:
        raise ValueError("magic 不符")
    version = image[4:7].decode()
    file_size, header_size, endian = struct.unpack_from("<3I", image, 32)
    if file_size != len(image):
        raise ValueError(f"file_size {file_size} != {len(image)}")
    if header_size != HEADER_SIZE or endian != ENDIAN_TAG:
        raise ValueError("header_size/endian_tag 不符")
    if image[12:32] != hashlib.sha1(image[32:]).digest():
        raise ValueError("signature 不符")
    if struct.unpack_from("<I", image, 8)[0] != zlib.adler32(image[12:]) & 0xFFFFFFFF:
        raise ValueError("checksum 不符")

    string_count, string_off = struct.unpack_from("<2I", image, 56)
    type_count, type_off = struct.unpack_from("<2I", image, 64)
    proto_count, proto_off = struct.unpack_from("<2I", image, 72)
    field_count, field_off = struct.unpack_from("<2I", image, 80)
    method_count, method_off = struct.unpack_from("<2I", image, 88)
    class_count, class_off = struct.unpack_from("<2I", image, 96)
    data_size, data_off = struct.unpack_from("<2I", image, 104)
    map_off = struct.unpack_from("<I", image, 52)[0]
    if field_count or field_off:
        raise ValueError("fixture 不应有 field_ids")
    if data_off + data_size != file_size:
        raise ValueError("data_off+data_size != file_size")

    strings = []
    for index in range(string_count):
        cursor = struct.unpack_from("<I", image, string_off + 4 * index)[0]
        size, cursor = read_uleb(image, cursor)
        strings.append(image[cursor:cursor + size].decode("utf-8"))
    if strings != sorted(strings):
        raise ValueError("string_ids 未按值升序")

    type_ids = [struct.unpack_from("<I", image, type_off + 4 * index)[0] for index in range(type_count)]
    if type_ids != sorted(type_ids):
        raise ValueError("type_ids 未按 descriptor 的 string 索引升序")
    descriptors = [strings[index] for index in type_ids]

    protos = []
    for index in range(proto_count):
        shorty, return_type, parameters = struct.unpack_from("<3I", image, proto_off + 12 * index)
        if parameters:
            raise ValueError("fixture 的 proto 不应有参数")
        protos.append((strings[shorty], strings[type_ids[return_type]]))
    return_indexes = [struct.unpack_from("<I", image, proto_off + 12 * index + 4)[0] for index in range(proto_count)]
    if return_indexes != sorted(return_indexes):
        raise ValueError("proto_ids 未按 return_type 升序")

    methods = []
    for index in range(method_count):
        class_idx, proto_idx, name_idx = struct.unpack_from("<HHI", image, method_off + 8 * index)
        methods.append((class_idx, name_idx, proto_idx, strings[name_idx]))
    if [tuple(row[:3]) for row in methods] != sorted(tuple(row[:3]) for row in methods):
        raise ValueError("method_ids 未排序")

    class_idx, access, super_idx, interfaces, source, annotations, class_data, values = \
        struct.unpack_from("<8I", image, class_off)
    if interfaces or annotations or values:
        raise ValueError("fixture 不应有 interfaces/annotations/static values")

    cursor = class_data
    static_fields, cursor = read_uleb(image, cursor)
    instance_fields, cursor = read_uleb(image, cursor)
    direct_count, cursor = read_uleb(image, cursor)
    virtual_count, cursor = read_uleb(image, cursor)
    if static_fields or instance_fields or virtual_count:
        raise ValueError("fixture 只应有 direct methods")
    decoded = []
    method_index = 0
    for _ in range(direct_count):
        diff, cursor = read_uleb(image, cursor)
        flags, cursor = read_uleb(image, cursor)
        code, cursor = read_uleb(image, cursor)
        method_index += diff
        registers, ins, outs, tries, debug, units = struct.unpack_from("<4H2I", image, code)
        if tries or debug:
            raise ValueError("fixture 不应有 tries/debug_info")
        decoded.append({"name": methods[method_index][3], "access": flags,
                        "registers": registers, "ins": ins, "units": units})

    map_size = struct.unpack_from("<I", image, map_off)[0]
    map_entries = [struct.unpack_from("<HHII", image, map_off + 4 + 12 * index) for index in range(map_size)]
    offsets = [entry[3] for entry in map_entries]  # (type, unused, size, offset)
    if offsets != sorted(offsets):
        raise ValueError("map_list 未按 offset 升序")
    if not map_entries or map_entries[0][0] != 0x0000 or map_entries[0][3] != 0:
        raise ValueError("map_list 首项应为 header")

    return {
        "version": version,
        "class": descriptors[class_idx],
        "super": descriptors[super_idx],
        "sourceFile": strings[source],
        "strings": string_count,
        "types": type_count,
        "protos": proto_count,
        "methods": method_count,
        "mapEntries": map_size,
        "decodedMethods": decoded,
    }


# ---------------------------------------------------------------- 产物

def write_fixtures(output: Path) -> dict:
    output.mkdir(parents=True, exist_ok=True)
    single = Dex(SAMPLE_CLASS, "vodbox-dex-fixture").build()
    (output / "sample-single.dex").write_bytes(single)

    second = Dex(SECOND_CLASS, "vodbox-dex-second").build()
    multidex_path = output / "sample-multidex.zip"
    with zipfile.ZipFile(multidex_path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("classes.dex", single)
        archive.writestr("classes2.dex", second)
        archive.writestr("res/raw/keep.txt", b"not-a-dex\n")

    fingerprint = {
        "generator": "build/dex_fixture.py",
        "single": {"file": "sample-single.dex", "class": SAMPLE_CLASS,
                   "sha256": hashlib.sha256(single).hexdigest(), "size": len(single)},
        "multidex": {"file": "sample-multidex.zip", "classes": [SAMPLE_CLASS, SECOND_CLASS],
                     "sha256": hashlib.sha256(multidex_path.read_bytes()).hexdigest(),
                     "size": multidex_path.stat().st_size},
    }
    (output / "fingerprint.json").write_text(json.dumps(fingerprint, indent=2) + "\n")
    return fingerprint


def verify(output: Path) -> int:
    failures = 0
    for name in ("sample-single.dex",):
        path = output / name
        if not path.is_file():
            print(f"  缺少 {path}")
            failures += 1
            continue
        summary = parse(path.read_bytes())
        print(f"  {name}: {summary['class']} extends {summary['super']} "
              f"methods={[m['name'] for m in summary['decodedMethods']]}")
    archive_path = output / "sample-multidex.zip"
    if archive_path.is_file():
        with zipfile.ZipFile(archive_path) as archive:
            for name in sorted(entry for entry in archive.namelist() if entry.endswith(".dex")):
                summary = parse(archive.read(name))
                print(f"  {archive_path.name}!{name}: {summary['class']}")
    else:
        print(f"  缺少 {archive_path}")
        failures += 1
    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description="生成真实 DEX fixture（不编译、不运行 Java）")
    parser.add_argument("--out", required=True, type=Path, help="输出目录（工作区内）")
    parser.add_argument("--verify-only", action="store_true", help="只解析已有产物")
    args = parser.parse_args()
    if not args.verify_only:
        fingerprint = write_fixtures(args.out)
        print(f"生成 DEX fixture 到 {args.out}")
        for key, value in fingerprint.items():
            if isinstance(value, dict):
                print(f"  {key}: {value['file']} sha256={value['sha256'][:16]}… size={value['size']}")
    failures = verify(args.out)
    if failures:
        print(f"自检失败：{failures} 项", file=sys.stderr)
        return 1
    print("结构自检通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
