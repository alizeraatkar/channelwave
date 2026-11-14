import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest


class NativeIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.repo = Path(__file__).parents[2]
        (self.folder / 'licenses').mkdir()
        (self.folder / 'licenses/TDLib.txt').write_text('Synthetic test notice')
        policy = json.loads((self.repo / '.github/release.json').read_text())
        revision = json.loads((self.repo / 'vendor/tdlib.lock.json').read_text())['revision']
        self.metadata = {'revision': revision, 'generator': policy['toolchain']['windowsGenerator']}
        payload = bytearray(128)
        payload[:2] = b'MZ'
        struct.pack_into('<I', payload, 0x3c, 64)
        payload[64:68] = b'PE\0\0'
        struct.pack_into('<H', payload, 68, 0x8664)
        self.payload = payload
        self.write()

    def write(self):
        (self.folder / 'tdjson.dll').write_bytes(self.payload)
        (self.folder / 'channelwave-build.json').write_text(json.dumps(self.metadata))
        (self.folder / 'SHA256SUMS').write_text(hashlib.sha256(self.payload).hexdigest() + '  tdjson.dll\n')

    def verify(self):
        return subprocess.run([sys.executable, str(self.repo / 'scripts/verify-native.py'), str(self.folder), 'windows'],
                              cwd=self.repo, capture_output=True, text=True)

    def test_expected_cached_payload_passes(self):
        checked = self.verify()
        self.assertEqual(checked.returncode, 0, checked.stderr)

    def test_corrupted_cache_payload_fails(self):
        (self.folder / 'tdjson.dll').write_bytes(b'corrupted')
        self.assertNotEqual(self.verify().returncode, 0)

    def test_wrong_native_revision_fails(self):
        self.metadata['revision'] = '0' * 40
        self.write()
        self.assertNotEqual(self.verify().returncode, 0)

    def test_wrong_architecture_fails_even_with_valid_checksum(self):
        struct.pack_into('<H', self.payload, 68, 0xaa64)
        self.write()
        self.assertNotEqual(self.verify().returncode, 0)

    def test_incompatible_toolchain_metadata_fails(self):
        self.metadata['generator'] = 'wrong toolchain'
        self.write()
        self.assertNotEqual(self.verify().returncode, 0)

    def test_payload_missing_from_checksums_fails(self):
        (self.folder / 'SHA256SUMS').write_text('')
        self.assertNotEqual(self.verify().returncode, 0)

    def test_checksum_path_escape_fails(self):
        (self.folder / 'SHA256SUMS').write_text('0' * 64 + '  ../outside.dll\n')
        self.assertNotEqual(self.verify().returncode, 0)
