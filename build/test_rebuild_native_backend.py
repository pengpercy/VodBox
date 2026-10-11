"""Regression coverage for rebuild output wiring; no native compiler is invoked."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import rebuild_native_backend as rebuild


class WindowsToolchainTests(unittest.TestCase):
    def probe(self, tools, env=None, target="x86_64-w64-mingw32"):
        with patch.dict(os.environ, dict(JAVA_HOME="fixture-jdk", **(env or {})), clear=True), \
             patch.object(rebuild.shutil, "which", side_effect=tools.get), \
             patch.object(rebuild, "_tool_version", side_effect=lambda command:
                          target if command[-1] == "-dumpmachine" else "fixture GCC"):
            return rebuild.toolchain_probe("win-x64")

    def test_unprefixed_msys2_gcc_is_supported(self):
        probe = self.probe({"gcc": "C:/msys64/mingw64/bin/gcc.exe",
                            "cmake": "C:/msys64/mingw64/bin/cmake.exe",
                            "mingw32-make": "C:/msys64/mingw64/bin/mingw32-make.exe"})
        self.assertEqual(probe["command"], ["C:/msys64/mingw64/bin/gcc.exe"])
        self.assertEqual(probe["engine_generator"], "MinGW Makefiles")
        self.assertEqual(probe["target"], "x86_64-w64-mingw32")

    def test_explicit_tools_override_path_discovery(self):
        cc = "C:/tool chain/gcc.exe"
        cmake = "C:/tool chain/cmake.exe"
        make = "C:/tool chain/ninja.exe"
        probe = self.probe({cc: cc, cmake: cmake, make: make, "gcc": "wrong-gcc"},
                           {"VODBOX_NATIVE_CC": cc, "VODBOX_ENGINE_CMAKE": cmake,
                            "VODBOX_ENGINE_MAKE": make, "VODBOX_ENGINE_GENERATOR": "Ninja"})
        self.assertEqual(probe["command"], [cc])
        self.assertEqual(probe["engine_cmake"], cmake)
        self.assertEqual(probe["engine_make"], make)
        self.assertEqual(probe["engine_generator"], "Ninja")

    def test_missing_explicit_compiler_does_not_fall_back(self):
        with self.assertRaisesRegex(rebuild.RebuildError, "compiler not found"):
            self.probe({"gcc": "other-gcc"}, {"VODBOX_NATIVE_CC": "missing-gcc"})

    def test_wrong_target_is_rejected(self):
        with self.assertRaisesRegex(rebuild.RebuildError, "expected x86_64-w64-mingw32"):
            self.probe({"gcc": "gcc"}, target="x86_64-pc-msys")

    def test_missing_make_is_rejected(self):
        with self.assertRaisesRegex(rebuild.RebuildError, "VODBOX_ENGINE_MAKE"):
            self.probe({"gcc": "gcc", "cmake": "cmake"})


class RebuildEnvironmentTests(unittest.TestCase):
    def test_build_passes_output_and_engine_flags_to_both_recipes(self):
        for rid in ("osx-x64", "osx-arm64", "win-x64"):
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                deps = root / "deps"
                deps.mkdir()
                for name in ("unidbg-unicorn2-0.9.8.jar", "unicorn-1.0.14.jar"):
                    with zipfile.ZipFile(deps / name, "w") as archive:
                        archive.writestr("Example.class", b"classes")
                        archive.writestr("natives/foreign/libunicorn.so", b"foreign")
                archive = root / "engine.tar.gz"
                archive.write_bytes(b"pinned fixture")
                engine = dict(rebuild._engines())
                engine["archive_sha256"] = hashlib.sha256(archive.read_bytes()).hexdigest()
                out = root / "custom output"
                cache = root / "engine cache"
                calls = []

                def run(command, *, env):
                    calls.append((Path(command[-1]).name, dict(env)))
                    if calls[-1][0] == "build-backend.sh":
                        library = Path(env["VODBOX_REBUILD_OUT"]) / rid / rebuild.RID_OUTPUT_NAME[rid]
                        library.parent.mkdir(parents=True)
                        library.write_bytes(b"rebuilt fixture")

                with patch.dict(os.environ, {"JAVA_HOME": str(root / "jdk"),
                                             "VODBOX_NATIVE_CC": "C:/tool chain/gcc.exe",
                        "VODBOX_NATIVE_BASH": "C:/msys/usr/bin/bash.exe",
                                             "VODBOX_ENGINE_CMAKE": "C:/tool chain/cmake.exe",
                                             "VODBOX_ENGINE_MAKE": "C:/tool chain/ninja.exe",
                                             "VODBOX_ENGINE_GENERATOR": "Ninja"}), \
                     patch.object(rebuild.shutil, "which", side_effect=lambda name: name), \
                     patch.object(rebuild, "_engines", return_value=engine), \
                     patch.object(rebuild, "engine_archive", return_value=archive), \
                     patch.object(rebuild, "_tool_version", side_effect=lambda command:
                                  "x86_64-w64-mingw32" if command[-1] == "-dumpmachine" else "fixture compiler"), \
                     patch.object(rebuild, "unicorn1_source_evidence", return_value={"archive_sha256": "fixture"}), \
                     patch.object(rebuild, "_run", side_effect=run):
                    receipt = rebuild.build(rid, deps, engine_cache=cache, out_root=out)
                self.assertEqual([name for name, _ in calls], ["fetch-engine.sh", "build-backend.sh"])
                for _, env in calls:
                    self.assertEqual(env["VODBOX_REBUILD_OUT"], str(out.resolve()))
                    self.assertEqual(env["VODBOX_REBUILD_CACHE"], str(cache.resolve()))
                    if rid == "win-x64":
                        self.assertEqual(env["VODBOX_NATIVE_CC"], "C:/tool chain/gcc.exe")
                        self.assertEqual(env["VODBOX_ENGINE_CMAKE"], "C:/tool chain/cmake.exe")
                        self.assertEqual(env["VODBOX_ENGINE_MAKE"], "C:/tool chain/ninja.exe")
                        self.assertEqual(env["VODBOX_ENGINE_GENERATOR"], "Ninja")
                    else:
                        self.assertEqual(env["VODBOX_ENGINE_CMAKE_FLAGS"], "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.6")
                if rid.startswith("osx"):
                    self.assertIn("-mmacosx-version-min=11.6", receipt["backend"]["flags"].split())
                self.assertEqual(receipt["backend"]["output_library"]["sha256"],
                                 hashlib.sha256(b"rebuilt fixture").hexdigest())
                with zipfile.ZipFile(deps / "unidbg-unicorn2-0.9.8.jar") as jar:
                    native = receipt["backend"]["jar_integration"]["native_entry"]
                    self.assertEqual(jar.read(native), b"rebuilt fixture")
                    self.assertEqual([name for name in jar.namelist() if name.startswith("natives/")], [native])
                with zipfile.ZipFile(deps / "unicorn-1.0.14.jar") as jar:
                    self.assertEqual(jar.namelist(), ["Example.class"])

    @unittest.skipIf(os.name == "nt", "recipe harness needs POSIX executables")
    def test_recipe_writes_custom_output_with_matching_macos_minimum(self):
        for rid in ("osx-x64", "osx-arm64"):
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                recipe_dir = root / "rebuild" / "backend-unicorn2"
                recipe_dir.mkdir(parents=True)
                recipe = recipe_dir / "build-backend.sh"
                shutil.copyfile(rebuild.REBUILD / recipe.name, recipe)
                native = root / "dependency-sources" / "upstream-evidence" / "native-provenance" / "release-inputs" / "backend" / "unicorn2" / "src" / "main" / "native"
                native.mkdir(parents=True)
                for name in rebuild.BRIDGE_SOURCES + rebuild.BRIDGE_HEADERS:
                    (native / name).touch()
                cache = root / "cache"
                engine = cache / "unicorn-548eda466eb9a2c69723a0f74888aa7b889b4ed9"
                (engine / "include").mkdir(parents=True)
                (engine / "build").mkdir()
                (engine / "build" / "libunicorn.a").touch()
                java = root / "jdk" / "include"
                (java / "darwin").mkdir(parents=True)
                (java / "jni.h").touch()
                (java / "darwin" / "jni_md.h").touch()
                tools = root / "tools"
                tools.mkdir()
                compiler = tools / "xcrun"
                compiler.write_text('#!/bin/bash\n[[ " $* " == *" -mmacosx-version-min=11.6 "* ]] || exit 9\nwhile [[ $# -gt 0 ]]; do\n  if [[ "$1" == "-o" ]]; then printf "fixture library" > "$2"; exit 0; fi\n  shift\ndone\nexit 8\n')
                compiler.chmod(0o755)
                env = dict(os.environ, RID=rid, JAVA_HOME=str(java.parent),
                           VODBOX_REBUILD_CACHE=str(cache), VODBOX_REBUILD_OUT="custom output",
                           PATH=str(tools) + os.pathsep + os.environ["PATH"])
                subprocess.run(["bash", str(recipe)], cwd=root, env=env, check=True,
                               capture_output=True, text=True)
                library = root / "custom output" / rid / "libunicorn.dylib"
                self.assertEqual(library.read_bytes(), b"fixture library")
                self.assertFalse((recipe_dir / "out").exists())


if __name__ == "__main__":
    unittest.main()
