#!/usr/bin/env python3
"""Build the pinned upstream GPL Java host and the small VodBox IPC adapter."""
from pathlib import Path
import argparse, hashlib, json, os, shutil, subprocess, tarfile, urllib.request

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / 'third_party/tvbox-silicon'

def maven():
    existing = shutil.which('mvn')
    if existing: return existing
    version = '3.9.9'
    folder = ROOT / '.cache' / 'maven'
    executable = folder / f'apache-maven-{version}' / 'bin' / 'mvn'
    if executable.exists(): return str(executable)
    folder.mkdir(parents=True, exist_ok=True)
    url = f'https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/{version}/apache-maven-{version}-bin.tar.gz'
    archive = folder / 'maven.tar.gz'
    data = urllib.request.urlopen(url, timeout=60).read()
    expected = urllib.request.urlopen(url + '.sha512', timeout=30).read().decode().split()[0]
    if hashlib.sha512(data).hexdigest() != expected: raise RuntimeError('Maven checksum mismatch')
    archive.write_bytes(data)
    with tarfile.open(archive) as tar: tar.extractall(folder, filter='data')
    archive.unlink()
    return str(executable)

def source_archives():
    root = SOURCE / 'dependency-sources'
    for entry in json.loads((root / 'download-archives.json').read_text()):
        archive = root / 'archives' / entry['file']
        archive.parent.mkdir(parents=True, exist_ok=True)
        if not archive.exists():
            partial = archive.with_suffix(archive.suffix + '.partial')
            with urllib.request.urlopen(entry['url'], timeout=180) as response, partial.open('wb') as output:
                shutil.copyfileobj(response, output)
            partial.replace(archive)
        with archive.open('rb') as stream:
            actual = hashlib.file_digest(stream, 'sha256').hexdigest()
        if actual != entry['sha256']:
            raise RuntimeError('Corresponding source archive checksum mismatch: ' + entry['file'])

def build(output):
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    source_archives()
    source = SOURCE / 'Runtime/JavaHost'
    repository = ROOT / '.cache/maven-repository'
    repository.mkdir(parents=True, exist_ok=True)
    settings = ROOT / '.cache/tvbox-maven-settings.xml'
    settings.write_text('<settings><mirrors><mirror><id>vodbox-central</id><mirrorOf>*</mirrorOf><url>https://repo.maven.apache.org/maven2</url></mirror></mirrors></settings>')
    subprocess.run([maven(), '-B', '-s', str(settings), '-gs', str(settings), f'-Dmaven.repo.local={repository}', '-f', str(source / 'pom.xml'),
                    'package', 'dependency:copy-dependencies', '-DskipTests'], check=True)
    classes = ROOT / '.cache/tvbox-adapter-classes'
    classes.mkdir(parents=True, exist_ok=True)
    deps = source / 'target/dependency'
    upstream = source / 'target/classes'
    cp = os.pathsep.join([str(upstream), str(deps / '*')])
    java = Path(os.environ['JAVA_HOME']) / 'bin' if os.environ.get('JAVA_HOME') else None
    javac = str(java / 'javac') if java else 'javac'
    jar = str(java / 'jar') if java else 'jar'
    subprocess.run([javac, '--release', '17', '-encoding', 'UTF-8', '-cp', cp, '-d', str(classes),
                    str(SOURCE / 'vodbox-adapter/src/vodbox/bridge/ProcessAdapter.java')], check=True)
    subprocess.run([jar, '--create', '--file', str(output / 'host.jar'), '-C', str(upstream), '.', '-C', str(classes), '.'], check=True)
    shutil.copytree(deps, output / 'deps', dirs_exist_ok=True)
    shutil.copy2(SOURCE / 'UPSTREAM.json', output / 'upstream-runtime.json')
    shutil.copy2(SOURCE / 'LICENSE', output / 'LICENSE-tvbox-silicon')
    # Keep the corresponding host + adapter source beside the distributed component.
    shutil.copytree(source / 'src', output / 'corresponding-source/src', dirs_exist_ok=True)
    shutil.copytree(SOURCE / 'vodbox-adapter', output / 'corresponding-source/vodbox-adapter', dirs_exist_ok=True)
    shutil.copy2(source / 'pom.xml', output / 'corresponding-source/pom.xml')
    shutil.copy2(SOURCE / 'UPSTREAM.json', output / 'corresponding-source/UPSTREAM.json')
    shutil.copy2(SOURCE / 'LICENSE', output / 'corresponding-source/LICENSE')
    # Include a self-contained source tree and the script that actually produces this component.
    corresponding = output / 'corresponding-source'
    rebuild = corresponding / 'third_party/tvbox-silicon'
    shutil.copytree(source / 'src', rebuild / 'Runtime/JavaHost/src', dirs_exist_ok=True)
    shutil.copy2(source / 'pom.xml', rebuild / 'Runtime/JavaHost/pom.xml')
    shutil.copytree(SOURCE / 'vodbox-adapter', rebuild / 'vodbox-adapter', dirs_exist_ok=True)
    for name in ('UPSTREAM.json', 'LICENSE'):
        shutil.copy2(SOURCE / name, rebuild / name)
    for name in ('dependency-sources', 'dependency-notices', 'rebuild'):
        if (SOURCE / name).is_dir(): shutil.copytree(SOURCE / name, rebuild / name, dirs_exist_ok=True)
    (corresponding / 'build').mkdir(exist_ok=True)
    shutil.copy2(Path(__file__), corresponding / 'build/tvbox-host.py')
    (corresponding / 'README-build.txt').write_text('Rebuild with JDK 17+: python3 build/tvbox-host.py --output build/component\nOriginal upstream sources and adapter are in third_party/tvbox-silicon.\nNative dependency provenance limitations are documented in dependency-sources/native-source-manifest.json.\n')
    shutil.rmtree(classes)
    print('Built upstream tvbox-silicon host:', output / 'host.jar', flush=True)

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', default=str(ROOT / 'src/VodBox.PluginHost/JavaHost'))
    args = parser.parse_args()
    build(args.output)
