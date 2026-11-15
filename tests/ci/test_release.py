import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import subprocess

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


class VersionAutomationTests(unittest.TestCase):
    setUp = ReleaseTests.setUp
    reserve = ReleaseTests.reserve
    packages = ReleaseTests.packages
    def test_conventional_changes_choose_semantic_base(self):
        for messages, expected in [(['fix: resume playback'], '1.2.4'),
                                   (['docs: setup'], '1.2.4'),
                                   (['Unstructured commit'], '1.2.4'),
                                   (['feat(audio): add casting', 'fix: cleanup'], '1.3.0'),
                                   (['feat(api)!: remove old settings'], '2.0.0'),
                                   (['refactor: storage\n\nBREAKING CHANGE: schema removed'], '2.0.0')]:
            with self.subTest(messages=messages):
                self.assertEqual(release.next_base('1.2.3', messages), expected)

    def test_stable_requires_a_published_beta(self):
        with self.assertRaisesRegex(ValueError, 'tested beta'):
            release.reservation(self.api, self.config, SHA, '42', channel='stable')
        self.reserve()
        with self.assertRaisesRegex(ValueError, 'published release'):
            release.reservation(self.api, self.config, SHA, '42', channel='stable')

    def test_stable_gets_new_build_and_reruns_are_idempotent(self):
        beta = self.reserve(); self.packages(beta); release.publish(self.api, self.folder, beta)
        stable = release.reservation(self.api, self.config, SHA, '43', channel='stable')
        self.assertEqual(stable['tag'], 'v0.1.0')
        self.assertEqual(stable['buildNumber'], beta['buildNumber'] + 1)
        self.api.writes.clear()
        self.assertEqual(release.reservation(self.api, self.config, SHA, '44', channel='stable'), stable)
        self.assertEqual(self.reserve(), beta)
        self.assertEqual(release.reservation(self.api, self.config, SHA, '44', stable['tag']), stable)
        self.assertEqual(self.api.writes, [])

    def test_stable_publish_is_not_a_prerelease_and_is_latest(self):
        value = self.reserve()
        value = value | {'tag': 'v0.1.0', 'version': '0.1.0'}
        obj = self.api.request('POST', '/git/tags', {'tag': value['tag'], 'message': json.dumps(value), 'object': SHA})
        self.api.request('POST', '/git/refs', {'ref': 'refs/tags/' + value['tag'], 'sha': obj['sha']})
        self.packages(value)
        release.publish(self.api, self.folder, value)
        self.assertFalse(self.api.release['prerelease'])
        self.assertEqual(self.api.release['make_latest'], 'true')
        self.assertEqual(release.verify_published(self.api, value), self.api.release['html_url'])
        self.api.release['prerelease'] = True
        with self.assertRaisesRegex(ValueError, 'reserved channel'):
            release.verify_published(self.api, value)

    def test_after_stable_pushes_advance_automatically_and_keep_build_counter(self):
        self.api.release = {'tag_name': 'v0.1.0', 'draft': False, 'prerelease': False}
        with patch.object(release, 'commit_messages', return_value=['fix: audio']):
            value = self.reserve()
        self.assertEqual(value['tag'], 'v0.1.1-beta.1')
        # Rerunning after another stable publication must retain its original reservation.
        self.api.release['tag_name'] = 'v0.2.0'
        with patch.object(release, 'commit_messages', side_effect=AssertionError('rerun must not replan')):
            self.assertEqual(self.reserve(), value)
        with patch.object(release, 'commit_messages', return_value=['feat: casting']):
            newer = self.reserve(OTHER)
        self.assertEqual(newer['tag'], 'v0.3.0-beta.1')
        self.assertGreater(newer['buildNumber'], value['buildNumber'])

    def test_old_stable_cannot_replace_newer_published_version(self):
        beta = self.reserve(); self.packages(beta); release.publish(self.api, self.folder, beta)
        original_pages = self.api.pages
        with patch.object(self.api, 'pages', side_effect=lambda path: original_pages(path) +
                          ([{'tag_name': 'v0.2.0', 'draft': False, 'prerelease': False}] if path == '/releases' else [])):
            with self.assertRaisesRegex(ValueError, 'increase the published'):
                release.reservation(self.api, self.config, SHA, '43', channel='stable')

    def test_real_git_history_is_bounded_by_stable_tag(self):
        with tempfile.TemporaryDirectory() as directory:
            def git(*args):
                return subprocess.check_output(['git', '-C', directory, *args], text=True).strip()
            git('init', '-q'); git('config', 'user.name', 'Test'); git('config', 'user.email', 'test@example.test')
            git('commit', '--allow-empty', '-qm', 'feat: old feature'); git('tag', 'v0.1.0')
            git('commit', '--allow-empty', '-qm', 'fix: new fix')
            sha = git('rev-parse', 'HEAD')
            real_run = subprocess.run
            with patch.object(release.subprocess, 'run') as run:
                # Run the real reader in the temporary repository.
                run.side_effect = lambda command, **kwargs: real_run(command, cwd=directory, **kwargs)
                self.assertEqual(release.commit_messages('v0.1.0', sha), ['fix: new fix'])

    def test_existing_legacy_beta_reservation_is_still_recoverable(self):
        value = self.reserve()
        value.pop('versionFloor')
        self.api.tags['1']['message'] = json.dumps(value)
        self.assertEqual(self.reserve(), value)

    def test_draft_stable_does_not_start_a_new_cycle(self):
        self.api.release = {'tag_name': 'v0.1.0', 'draft': True, 'prerelease': False}
        with patch.object(release, 'commit_messages', side_effect=AssertionError('draft is not stable')):
            self.assertEqual(self.reserve()['tag'], 'v0.1.0-beta.4')

    def test_publish_rejects_an_existing_release_with_the_wrong_channel(self):
        value = self.reserve(); self.packages(value); release.publish(self.api, self.folder, value)
        self.api.release['prerelease'] = False
        with self.assertRaisesRegex(ValueError, 'channel differs'):
            release.publish(self.api, self.folder, value)
