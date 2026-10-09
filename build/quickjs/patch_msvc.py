"""Audited optimizer fixes for pinned quickjs-ng 3c9afc9; no warning suppression.

The source hash and exact replacements fail closed on upstream changes. Reapplication
is safe for cached builds. Other QuickJS revisions require reviewing this patch again.
"""
import hashlib
from pathlib import Path
import sys

SOURCE_SHA = 'c9dcb1de0f977c329fe69fd716d49df30552958d4abb8289faa1df37d8dc8378'

def patched(source):
    lines = source.splitlines(keepends=True)
    # All branches return/abort, or the enclosing loop has no exit. These
    # compiler-pacifying fallbacks are themselves unreachable on MSVC.
    dead = {2829:'return (JSAtomKindEnum){-1};', 7501:'return JS_UNDEFINED;',
            9760:'return false;', 22121:'return -1;', 25911:'return 0;',
            48784:'break;', 48788:'return NULL;', 54397:'return JS_UNDEFINED;',
            56339:'return JS_EXCEPTION;'}
    for number, expected in dead.items():
        if not lines[number-1].strip().startswith(expected):
            raise ValueError(f'Unexpected pinned source at line {number}')
        lines[number-1] = ''
    source = ''.join(lines)
    def replace(old, new):
        nonlocal source
        if source.count(old) != 1:
            raise ValueError(f'Expected exactly one patch site: {old}')
        source = source.replace(old, new)
    replace('JSValue stack, prepare, saved_exception;',
            'JSValue stack, prepare = JS_UNDEFINED, saved_exception;')
    replace('int64_t len, start, k, final, n, count, del_count, new_len;',
            'int64_t len, start, k, final, n, count, del_count = 0, new_len;')
    replace('    bool have_promise_hook;\n    JSValueLink link;',
            '    bool have_promise_hook;\n    JSValueLink link = {0};')
    helper = '''/* Keep the Date intermediate rounded and prevent fused operations.
 * ARM64 MSVC requires explicit ISO volatile access to avoid /volatile ambiguity.
 * The union transfers the double's bits without numeric conversion. */
static double vodbox_date_intermediate(double value)
{
#if defined(_MSC_VER) && defined(_M_ARM64)
    union { double d; __int64 i; } bits;
    volatile __int64 storage;
    bits.d = value;
    __iso_volatile_store64(&storage, bits.i);
    bits.i = __iso_volatile_load64(&storage);
    return bits.d;
#else
    volatile double rounded = value;
    return rounded;
#endif
}

'''
    anchor = 'static double set_date_fields(double fields[minimum_length(7)], int is_local) {'
    replace(anchor, helper + anchor)
    replace('    volatile double temp;  /* enforce evaluation order */\n', '')
    replace('time += (temp = m * 60000);', 'time += vodbox_date_intermediate(m * 60000);')
    replace('time += (temp = s * 1000);', 'time += vodbox_date_intermediate(s * 1000);')
    replace('tv = (temp = day * 86400000) + time;', 'tv = vodbox_date_intermediate(day * 86400000) + time;')
    return source

def apply(directory):
    path = Path(directory) / 'quickjs.c'
    marker = path.with_name('.vodbox-msvc-patch.sha256')
    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    patch_digest = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    if marker.exists() and marker.read_text() == f'{patch_digest}:{digest}':
        return
    if digest != SOURCE_SHA:
        raise ValueError('QuickJS source changed; clean the build cache and review the pinned patch')
    output = patched(raw.decode()).encode()
    path.write_bytes(output)
    marker.write_text(f'{patch_digest}:{hashlib.sha256(output).hexdigest()}')

if __name__ == '__main__':
    apply(sys.argv[1])
