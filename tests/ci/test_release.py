import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('release', Path(__file__).parents[2] / 'scripts/release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
SHA, OTHER = 'a' * 40, 'b' * 40


class FakeGitHub:
    def __init__(self):
        self.refs = [{'ref': 'refs/tags/v0.1.0-beta.3', 'object': {'type': 'commit', 'sha': OTHER}}]
        self.tags, self.assets, self.blobs = {}, {}, {}
        self.release, self.status, self.fail_upload = None, 'identical', False
        self.fail_name = None
        self.writes = []

    def request(self, method, path, body=None, binary=False):
        if method != 'GET': self.writes.append((method, path))
        if path.startswith('/compare/'): return {'status': self.status}
        if path.startswith('/git/matching-refs/'): return self.refs
        if path.startswith('/git/ref/tags/'):
            return next((v for v in self.refs if v['ref'] == 'refs/tags/' + path.split('/tags/')[1]), None)
        if path == '/git/tags':
            key = str(len(self.tags) + 1); self.tags[key] = body
            return {'sha': key}
        if path.startswith('/git/tags/'):
            value = self.tags[path.rsplit('/', 1)[1]]
            return value | {'object': {'sha': value['object']}}
        if path == '/git/refs':
            if any(v['ref'] == body['ref'] for v in self.refs): raise RuntimeError('Tag collision')
            self.refs.append({'ref': body['ref'], 'object': {'type': 'tag', 'sha': body['sha']}})
            return body
        if path.startswith('/releases/tags/'): return self.release
        if path == '/releases/generate-notes': return {'body': 'Changes'}
        if path == '/releases':
            self.release = body | {'id': 1, 'upload_url': 'https://uploads.github.com/upload{?name}', 'html_url': 'https://example.test/release'}
            return self.release
        if path.startswith('https://uploads.github.com/'):
            if self.fail_upload:
                self.fail_upload = False
                raise RuntimeError('Synthetic interrupted upload')
            name = release.urllib.parse.parse_qs(release.urllib.parse.urlsplit(path).query)['name'][0]
            if name == self.fail_name:
                self.fail_name = None
                raise RuntimeError('Synthetic interrupted upload')
            self.assets[name] = {'name': name, 'state': 'uploaded', 'size': len(body), 'digest': 'sha256:' + release.hashlib.sha256(body).hexdigest()}
            self.blobs[name] = body
            return self.assets[name]
        if method == 'PATCH' and path == '/releases/1':
            self.release.update(body)
            return self.release
        raise AssertionError((method, path))

    def pages(self, path):
        return list(self.assets.values()) if path.endswith('/assets') else [self.release] if self.release else []

    def download_json(self, asset):
        data = self.blobs[asset['name']]
        return json.loads(data), data


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.config = release.read_config()
        for name, value in self.config['platforms'].items():
            value['enabled'] = name == 'android'
            if not value['enabled']: value.setdefault('reason', 'Disabled in synthetic test')
        self.api = FakeGitHub()
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)

    def reserve(self, sha=SHA):
        return release.reservation(self.api, self.config, sha, '42')

    def packages(self, reserved):
        (self.folder / f"ChannelWave-{reserved['version']}-android-arm64.apk").write_bytes(b'synthetic-apk')
        (self.folder / 'TDLib-Android-notices.zip').write_bytes(b'synthetic-notices')
        release.package_manifest('android', self.folder, reserved)

    def test_next_beta_and_android_upgrade_number(self):
        value = self.reserve()
        self.assertEqual(value['tag'], 'v0.1.0-beta.4'); self.assertEqual(value['buildNumber'], 7)

    def test_rerun_reuses_reservation_without_writes(self):
        value = self.reserve(); self.api.writes.clear()
        self.assertEqual(self.reserve(), value); self.assertEqual(self.api.writes, [])

    def test_new_commit_increments_reserved_not_only_published_version(self):
        self.reserve(); self.assertEqual(self.reserve(OTHER)['tag'], 'v0.1.0-beta.5')

    def test_new_base_version_keeps_global_build_monotonic(self):
        self.reserve(); self.config['baseVersion'] = '0.2.0'; value = self.reserve(OTHER)
        self.assertEqual(value['tag'], 'v0.2.0-beta.1'); self.assertEqual(value['buildNumber'], 8)

    def test_recovery_requires_matching_existing_tag(self):
        with self.assertRaisesRegex(ValueError, 'existing pipeline'):
            release.reservation(self.api, self.config, SHA, '43', 'v0.1.0-beta.4')
        value = self.reserve()
        self.assertEqual(release.reservation(self.api, self.config, SHA, '43', value['tag']), value)
        with self.assertRaisesRegex(ValueError, 'does not match'):
            release.reservation(self.api, self.config, SHA, '43', 'v0.1.0-beta.5')

    def test_non_main_commit_and_invalid_sha_are_rejected(self):
        for status in ('diverged', 'behind'):
            self.api.status = status
            with self.assertRaises(ValueError): self.reserve()
        with self.assertRaises(ValueError): self.reserve('main')

    def test_configuration_cannot_change_within_reservation(self):
        self.reserve(); self.config['platforms']['ios']['enabled'] = True
        with self.assertRaisesRegex(ValueError, 'reserved platform'): self.reserve()

    def test_wrong_tag_commit_is_rejected(self):
        value = self.reserve(); self.api.tags['1']['object'] = OTHER
        with self.assertRaisesRegex(ValueError, 'metadata'): self.reserve()
        with self.assertRaisesRegex(ValueError, 'changed'): release.publish(self.api, self.folder, value)

    def test_build_number_limit_is_enforced(self):
        self.config['minimumBuildNumber'] = 2100000000
        with self.assertRaisesRegex(ValueError, 'exhausted'): self.reserve()

    def test_complete_artifacts_publish_after_server_digest_verification(self):
        value = self.reserve(); self.packages(value); release.publish(self.api, self.folder, value)
        self.assertFalse(self.api.release['draft']); self.assertTrue(self.api.release['prerelease'])
        self.assertIn('SHA256SUMS', self.api.assets); self.assertIn('Pending', self.api.release['body'])

    def test_published_release_rerun_is_a_noop(self):
        value = self.reserve(); self.packages(value); release.publish(self.api, self.folder, value); self.api.writes.clear()
        release.publish(self.api, self.folder, value); self.assertEqual(self.api.writes, [])

    def test_missing_platform_blocks_publication(self):
        value = self.reserve()
        with self.assertRaisesRegex(ValueError, 'Missing required platform'): release.publish(self.api, self.folder, value)
        self.assertIsNone(self.api.release)

    def test_missing_required_package_blocks_publication(self):
        value = self.reserve(); (self.folder / 'junk.txt').write_text('not a package')
        release.package_manifest('android', self.folder, value)
        with self.assertRaisesRegex(ValueError, 'Missing required package'): release.collect(self.folder, value)

    def test_tampered_package_blocks_publication(self):
        value = self.reserve(); self.packages(value); next(self.folder.glob('*.apk')).write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'integrity'): release.publish(self.api, self.folder, value)

    def test_foreign_commit_manifest_blocks_publication(self):
        value = self.reserve(); self.packages(value); path = self.folder / 'android-manifest.json'
        data = json.loads(path.read_text()); data['commit'] = OTHER; path.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'another release'): release.collect(self.folder, value)

    def test_unmanifested_file_blocks_publication(self):
        value = self.reserve(); self.packages(value); (self.folder / 'session.db').write_bytes(b'private')
        with self.assertRaisesRegex(ValueError, 'Unmanifested'): release.collect(self.folder, value)

    def test_interrupted_upload_resumes_same_draft(self):
        value = self.reserve(); self.packages(value); self.api.fail_upload = True
        with self.assertRaisesRegex(RuntimeError, 'interrupted'): release.publish(self.api, self.folder, value)
        self.assertTrue(self.api.release['draft']); release.publish(self.api, self.folder, value)
        self.assertFalse(self.api.release['draft'])

    def test_changed_asset_is_never_overwritten(self):
        value = self.reserve(); self.packages(value); release.publish(self.api, self.folder, value)
        next(iter(self.api.assets.values()))['digest'] = 'sha256:wrong'
        with self.assertRaisesRegex(ValueError, 'refusing overwrite'): release.publish(self.api, self.folder, value)

    def test_disabled_platform_cannot_be_packaged(self):
        value = self.reserve(); self.packages(value)
        with self.assertRaisesRegex(ValueError, 'disabled'): release.package_manifest('ios', self.folder, value)

    def test_completed_pipeline_can_verify_without_rebuilding(self):
        value = self.reserve(); self.packages(value); release.publish(self.api, self.folder, value); self.api.writes.clear()
        self.assertEqual(release.verify_published(self.api, value), 'https://example.test/release')
        self.assertEqual(self.api.writes, [])

    def test_completed_pipeline_rejects_missing_asset(self):
        value = self.reserve(); self.packages(value); release.publish(self.api, self.folder, value)
        del self.api.assets['TDLib-Android-notices.zip']
        with self.assertRaisesRegex(ValueError, 'assets differ'): release.verify_published(self.api, value)

    def test_completed_pipeline_rejects_foreign_reservation(self):
        value = self.reserve(); self.packages(value); release.publish(self.api, self.folder, value)
        with self.assertRaisesRegex(ValueError, 'another reservation'): release.verify_published(self.api, dict(value, commit=OTHER))

    def test_partial_upload_resumes_after_canonical_tag_round_trip(self):
        value = self.reserve(); self.packages(value); self.api.fail_name = 'SHA256SUMS'
        with self.assertRaisesRegex(RuntimeError, 'interrupted'): release.publish(self.api, self.folder, value)
        self.assertIn('release-manifest.json', self.api.assets)
        release.publish(self.api, self.folder, self.reserve())
        self.assertFalse(self.api.release['draft'])


if __name__ == '__main__': unittest.main()
