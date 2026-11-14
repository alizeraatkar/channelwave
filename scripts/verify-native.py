"""Validate native cache provenance before a cache hit can reach packaging."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('folder', type=Path)
parser.add_argument('platform', choices=['android', 'linux', 'windows', 'ios', 'maccatalyst'])
args = parser.parse_args()
root = args.folder.resolve()
metadata = json.loads((root / 'channelwave-build.json').read_text(encoding='utf-8-sig'))
revision = json.loads(Path('vendor/tdlib.lock.json').read_text())['revision']
assert metadata['revision'] == revision, 'TDLib revision mismatch'
checked = set()
for line in (root / 'SHA256SUMS').read_text(encoding='utf-8-sig').splitlines():
    digest, name = line.split(maxsplit=1)
    path = (root / name.lstrip('*')).resolve()
    assert path.is_relative_to(root), 'Checksum path escapes native output'
    with path.open('rb') as stream:
        assert hashlib.file_digest(stream, 'sha256').hexdigest() == digest.lower(), f'Native checksum mismatch: {name}'
    checked.add(path)
assert any((root / 'licenses').iterdir()), 'Missing native notices'
if args.platform in ('android', 'linux'):
    file = root / ('arm64-v8a/libtdjson.so' if args.platform == 'android' else 'lib/libtdjson.so')
    header = file.read_bytes()[:64]
    assert file.resolve() in checked, 'Native payload is not covered by checksums'
    assert header[:5] == b'\x7fELF\x02', 'Expected 64-bit ELF'
    assert struct.unpack_from('<H', header, 18)[0] == (183 if args.platform == 'android' else 62), 'Wrong ELF architecture'
    if args.platform == 'android':
        assert metadata['abi'] == 'arm64-v8a'
        assert metadata['ndk'] == '28.2.13676358' and metadata['openssl'] == '3.5.8' and metadata['androidApi'] == 29
        data = file.read_bytes()
        offset = struct.unpack_from('<Q', header, 32)[0]
        size, count = struct.unpack_from('<HH', header, 54)
        loads = [offset + i * size for i in range(count) if struct.unpack_from('<I', data, offset + i * size)[0] == 1]
        assert loads and all(struct.unpack_from('<Q', data, start + 48)[0] >= 16384 for start in loads), 'Android ELF must support 16 KiB pages'
elif args.platform == 'windows':
    assert (root / 'tdjson.dll').resolve() in checked
    assert metadata['generator'] == json.loads(Path('.github/release.json').read_text())['toolchain']['windowsGenerator']
    data = (root / 'tdjson.dll').read_bytes()
    offset = struct.unpack_from('<I', data, 0x3c)[0]
    assert data[:2] == b'MZ' and data[offset:offset + 4] == b'PE\0\0', 'Expected PE image'
    assert struct.unpack_from('<H', data, offset + 4)[0] == 0x8664, 'Expected x64 DLL'
else:
    assert metadata['platform'] == args.platform and metadata['architecture'] == 'arm64'
    assert (root / 'libtdjson-combined.a').read_bytes()[:8] == b'!<arch>\n', 'Expected static archive'
    assert (root / 'libtdjson-combined.a').resolve() in checked
    assert metadata['xcode'] == json.loads(Path('.github/release.json').read_text())['toolchain']['xcode']
    # Cache hits must verify the archive itself, not only its declared metadata.
    subprocess.run(['lipo', str(root / 'libtdjson-combined.a'), '-verify_arch', 'arm64'], check=True)
print(f'Verified {args.platform}: {revision}')
