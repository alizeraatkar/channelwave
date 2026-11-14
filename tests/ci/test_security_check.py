import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch, Mock
import zipfile


SPEC = importlib.util.spec_from_file_location('security_check', Path(__file__).parents[2] / 'scripts/security-check.py')
security = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(security)


class SecretScanTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git('init', '--quiet')
        self.git('config', 'user.email', 'synthetic@example.invalid')
        self.git('config', 'user.name', 'Synthetic test')

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), *args], stderr=subprocess.DEVNULL)

    def secret(self):
        return 'gh' + 'p_' + 'Abcde0123456789' * 3

    def commit(self):
        self.git('add', '.')
        self.git('commit', '--quiet', '-m', 'Synthetic fixture')

    def test_removed_historical_secret_is_found_without_exposing_value(self):
        (self.root / 'old.txt').write_text(self.secret())
        self.commit()
        (self.root / 'old.txt').unlink()
        self.commit()
        report = security.scan_repository(self.root)
        self.assertTrue(any(f['rule'] == 'github-token' and f['object'] != 'working-tree' for f in report['findings']))
        self.assertNotIn(self.secret(), json.dumps(report))

    def test_staged_secret_is_found_even_after_working_file_is_replaced(self):
        (self.root / 'config.txt').write_text(self.secret())
        self.git('add', 'config.txt')
        (self.root / 'config.txt').write_text('safe')
        self.assertTrue(security.scan_repository(self.root)['findings'])

    def test_binary_session_or_signing_file_in_history_is_rejected(self):
        (self.root / 'accidental.keystore').write_bytes(b'\0\x01\x02synthetic')
        self.commit()
        (self.root / 'accidental.keystore').unlink()
        self.commit()
        self.assertTrue(any(f['rule'] == 'private-runtime-or-signing-file' for f in security.scan_repository(self.root)['findings']))

    def test_nonignored_new_files_are_scanned_and_local_ignored_credentials_are_excluded(self):
        (self.root / '.gitignore').write_text('private.local.json\n')
        (self.root / 'private.local.json').write_text(self.secret())
        (self.root / 'new.txt').write_text(self.secret())
        report = security.scan_repository(self.root)
        self.assertEqual({f['path'] for f in report['findings']}, {'new.txt'})

    def test_shallow_checkout_cannot_pass_history_scan(self):
        with patch.object(security, 'command', return_value=b'true\n'):
            with self.assertRaises(ValueError):
                security.scan_repository(self.root)

    def test_placeholder_and_expression_passwords_are_not_literal_secrets(self):
        data = ('Password' + '=changeme;Pwd' + '=$generated;apiHash="' + '0' * 32 + '"').encode()
        self.assertEqual(security.find_secrets(data), [])

    def test_private_key_and_connection_password_patterns_are_redacted(self):
        data = ('-----BEGIN ' + 'PRIVATE KEY-----\nPassword' + '=ReallySensitiveValue;').encode()
        report = security.find_secrets(data)
        self.assertEqual({f['rule'] for f in report}, {'private-key', 'connection-password'})
        self.assertNotIn('ReallySensitiveValue', json.dumps(report))

    def test_literal_password_with_shell_punctuation_is_not_skipped(self):
        for value in ('Sensitive$123', 'Sensitive(value)', 'Sensitive{value}', 'Sensitive<value>'):
            with self.subTest(value=value):
                report = security.find_secrets(('Password' + '=' + value + ';').encode())
                self.assertEqual([finding['rule'] for finding in report], ['connection-password'])
                self.assertNotIn(value, json.dumps(report))

    def test_bom_marked_unicode_secret_files_are_scanned_and_redacted(self):
        text = 'Synthetic fixture\n' + self.secret()
        for encoding, bom in [('utf-16-le', b'\xff\xfe'), ('utf-16-be', b'\xfe\xff'), ('utf-8', b'\xef\xbb\xbf')]:
            with self.subTest(encoding=encoding):
                (self.root / 'unicode.config').write_bytes(bom + text.encode(encoding))
                report = security.scan_repository(self.root)
                self.assertTrue(any(f['rule'] == 'github-token' and f['line'] == 2 for f in report['findings']))
                self.assertNotIn(self.secret(), json.dumps(report))

    def test_invalid_utf8_byte_cannot_hide_ascii_secret_in_file(self):
        (self.root / 'legacy.config').write_bytes(b'Legacy CP1252 \x96 text\n' + self.secret().encode('ascii'))
        self.commit()
        report = security.scan_repository(self.root)
        self.assertTrue(any(f['rule'] == 'github-token' and f['line'] == 2 for f in report['findings']))
        self.assertNotIn(self.secret(), json.dumps(report))

    def test_unmarked_binary_payload_is_explicitly_outside_text_pattern_scan(self):
        self.assertEqual(security.find_secrets(b'\0binary\n' + self.secret().encode()), [])


class ArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def registration(self, **extra):
        return json.dumps({'apiId': 123, 'apiHash': '0123456789abcdef' * 2} | extra).encode()

    def test_default_web_output_cannot_contain_local_registration(self):
        (self.root / 'channelwave.local.json').write_bytes(self.registration())
        with self.assertRaises(ValueError):
            security.scan_artifact(self.root)

    def test_explicit_web_registration_is_sanitized_and_not_reported_as_session_secret(self):
        (self.root / 'channelwave.local.json').write_bytes(self.registration())
        self.assertEqual(security.scan_artifact(self.root, True)['findings'], [])

    def test_packaged_registration_cannot_contain_proxy_or_session_fields(self):
        (self.root / 'telegram.json').write_bytes(self.registration(session='must-never-ship'))
        with self.assertRaises(ValueError):
            security.scan_artifact(self.root)

    def test_nested_apk_session_and_signing_files_are_rejected(self):
        nested = io.BytesIO()
        with zipfile.ZipFile(nested, 'w') as archive:
            archive.writestr('assets/telegram.json', self.registration())
            archive.writestr('data/session.db', b'synthetic database')
            archive.writestr('signing/private.pfx', b'synthetic signing key')
        (self.root / 'app.apk').write_bytes(nested.getvalue())
        findings = security.scan_artifact(self.root)['findings']
        self.assertEqual(len(findings), 2)
        self.assertTrue(all(f['rule'] == 'private-runtime-or-signing-file' for f in findings))

    def test_archive_traversal_fails_without_extracting_anything(self):
        with zipfile.ZipFile(self.root / 'bad.zip', 'w') as archive:
            archive.writestr('../outside.txt', 'synthetic')
        self.assertEqual(security.scan_artifact(self.root)['findings'][0]['rule'], 'archive-path-escape')
        self.assertFalse((self.root.parent / 'outside.txt').exists())

    def test_absolute_and_windows_drive_archive_paths_are_rejected(self):
        with zipfile.ZipFile(self.root / 'bad.zip', 'w') as archive:
            archive.writestr('/absolute.txt', 'synthetic')
            archive.writestr('C:/absolute.txt', 'synthetic')
        self.assertEqual([f['rule'] for f in security.scan_artifact(self.root)['findings']], ['archive-path-escape'] * 2)

    def test_zip_symlink_cannot_escape_archive(self):
        with zipfile.ZipFile(self.root / 'bad.zip', 'w') as archive:
            entry = zipfile.ZipInfo('link')
            entry.external_attr = 0o120777 << 16
            archive.writestr(entry, '../private')
        self.assertEqual(security.scan_artifact(self.root)['findings'][0]['rule'], 'archive-link-escape')

    def test_zip_directory_symlink_cannot_skip_escape_check(self):
        with zipfile.ZipFile(self.root / 'bad.zip', 'w') as archive:
            entry = zipfile.ZipInfo('directory-link/')
            entry.external_attr = 0o120777 << 16
            archive.writestr(entry, '../private')
        self.assertEqual(security.scan_artifact(self.root)['findings'][0]['rule'], 'archive-link-escape')

    def test_tar_symlink_cannot_escape_archive(self):
        with tarfile.open(self.root / 'bad.tar.gz', 'w:gz') as archive:
            entry = tarfile.TarInfo('link')
            entry.type = tarfile.SYMTYPE
            entry.linkname = '/private'
            archive.addfile(entry)
        self.assertEqual(security.scan_artifact(self.root)['findings'][0]['rule'], 'archive-link-escape')

    def test_exceeded_artifact_budget_fails_closed(self):
        (self.root / 'file').write_bytes(b'123456')
        with patch.object(security, 'MAX_ARCHIVE_BYTES', 5):
            with self.assertRaises(ValueError):
                security.scan_artifact(self.root)


class DependencyTests(unittest.TestCase):
    def test_nuget_ranges_respect_inclusive_exclusive_and_normalized_versions(self):
        for version, interval, expected in [
            ('1.2', '[1.2.0]', True), ('1.2.0', '(,1.2.0)', False),
            ('1.2.0', '(,1.2.0]', True), ('1.2.0', '[1.2,2)', True),
            ('2.0.0', '[1.2,2)', False), ('2.0.0-preview.2', '[1,2)', True),
            ('2.0.0', '[2.0.0-preview.2,)', True), ('1.2.1+build', '[1.2.1]', True),
            ('1.2.0-beta.10', '(1.2.0-beta.2,1.2.0)', True),
        ]:
            with self.subTest(version=version, interval=interval):
                self.assertEqual(security.version_in_range(version, interval), expected)

    def test_unrecognized_advisory_range_cannot_pass(self):
        with self.assertRaises(ValueError):
            security.version_in_range('1.2.3', '1.*')

    def test_untrusted_feed_origin_is_rejected_before_network_access(self):
        with self.assertRaises(ValueError):
            security.fetch_json('https://example.invalid/advisories')

    def audit_fixture(self, npm_report=None, fetch_failure=None):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        (root / 'vendor/tdweb-build').mkdir(parents=True)
        (root / 'package-lock.json').write_text('{}')
        (root / 'vendor/tdweb-build/package-lock.json').write_text('{}')
        (root / 'packages.android-release.lock.json').write_text(json.dumps({'dependencies': {'net10.0-android': {'Affected': {'resolved': '1.2.3'}}}}))
        feed = [{'@id': 'https://api.nuget.org/v3-vulnerabilities/synthetic/base.json'}]
        page = {'affected': [{'severity': 3, 'versions': '[1.0,2.0)', 'url': 'https://example.invalid/synthetic-advisory'}]}
        npm = npm_report if npm_report is not None else {'metadata': {'vulnerabilities': {'total': 0}}, 'vulnerabilities': {}}
        result = Mock(returncode=0, stdout=json.dumps(npm))
        with patch.object(security, 'command', return_value=b'packages.android-release.lock.json\0'), \
                patch.object(security, 'fetch_json', side_effect=fetch_failure if fetch_failure else [feed, page]), \
                patch.object(security.subprocess, 'run', return_value=result):
            return security.audit_dependencies(root)

    def test_platform_specific_locked_critical_package_is_not_missed(self):
        report = self.audit_fixture()
        self.assertEqual(report['nugetLockfiles'], 1)
        self.assertEqual(report['findings'][0]['package'], 'affected')
        self.assertEqual(report['findings'][0]['severity'], 'critical')

    def test_nuget_feed_failure_cannot_be_treated_as_clean(self):
        with self.assertRaises(OSError):
            self.audit_fixture(fetch_failure=OSError('Synthetic offline feed'))

    def test_npm_error_response_cannot_be_treated_as_clean(self):
        with self.assertRaises(ValueError):
            self.audit_fixture({'error': {'summary': 'Synthetic offline npm'}})

    def test_unrecognized_npm_severity_cannot_pass(self):
        with self.assertRaises(ValueError):
            self.audit_fixture({'metadata': {'vulnerabilities': {'total': 1}}, 'vulnerabilities': {'bad': {'severity': 'unknown'}}})

    def test_cli_rejects_high_dependency_findings(self):
        with patch.object(security.sys, 'argv', ['security-check.py', 'dependencies']), \
                patch.object(security, 'audit_dependencies', return_value={'findings': [{'severity': 'high'}]}), \
                patch('sys.stdout', new_callable=io.StringIO):
            self.assertEqual(security.main(), 1)

    def test_cli_feed_failure_is_distinct_from_clean_and_redacts_exception_text(self):
        with patch.object(security.sys, 'argv', ['security-check.py', 'dependencies']), \
                patch.object(security, 'audit_dependencies', side_effect=OSError('Sensitive arbitrary error content')), \
                patch('sys.stderr', new_callable=io.StringIO) as output:
            self.assertEqual(security.main(), 2)
            self.assertNotIn('Sensitive arbitrary error content', output.getvalue())


if __name__ == '__main__':
    unittest.main()
