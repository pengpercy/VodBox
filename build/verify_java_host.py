"""Run the delivered JRE and upstream adapter on the current native CI runner."""
from pathlib import Path
import argparse, json, os, subprocess, tempfile
from nativeverify import verify_jre


def verify(rid, component):
    component = Path(component).resolve()
    verify_jre(component / 'jre', rid)
    java = component / 'jre' / 'bin' / ('java.exe' if rid.startswith('win') else 'java')
    if not java.is_file() and rid.startswith('osx'):
        java = component / 'jre' / 'Contents' / 'Home' / 'bin' / 'java'
    subprocess.run([str(java), '-version'], check=True, timeout=20)
    cp = os.pathsep.join([str(component / 'host.jar'), str(component / 'deps' / '*')])
    with tempfile.TemporaryDirectory(prefix='delivery-smoke-', dir=component.parent) as work:
        root = Path(work)
        request = root / 'request.json'
        request.write_text(json.dumps({'jar': str(root / 'missing-plugin.jar'), 'cache': str(root / 'cache'), 'api': 'csp_Missing', 'ext': ''}))
        result = subprocess.run([str(java), '--add-opens=java.base/java.lang=ALL-UNNAMED',
            '--add-opens=java.base/sun.net.www.protocol.jar=ALL-UNNAMED', '--enable-native-access=ALL-UNNAMED',
            '-cp', cp, 'vodbox.bridge.ProcessAdapter', '--serve', str(request)], input='', text=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30)
        receipt = root / 'cache' / 'ready.json'
        if not receipt.exists():
            raise RuntimeError('Delivered adapter failed before producing its protocol envelope: ' + result.stderr[-2000:])
        envelope = json.loads(receipt.read_text())
        if not envelope.get('error') or result.returncode == 0:
            raise RuntimeError('Missing plugin must yield an explicit protocol failure')
    print(rid + ': delivered JRE + original dependency closure + adapter protocol verified (not a media playback test)')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('rid')
    parser.add_argument('component', type=Path)
    args = parser.parse_args()
    verify(args.rid, args.component)
