import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile


class DesktopPackagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        repo = Path(__file__).parents[2]
        (self.root / 'scripts').mkdir()
        for name in ['package-desktop.ps1', 'security-check.py']:
            shutil.copyfile(repo / 'scripts' / name, self.root / 'scripts' / name)
        native = self.root / 'artifacts/tdlib/windows'
        (native / 'licenses').mkdir(parents=True)
        (native / 'licenses/TDLib.txt').write_text('Synthetic test notice')
        (native / 'channelwave-build.json').write_text('{}')
        (self.root / 'docs').mkdir()
        (self.root / 'docs/desktop-installation.md').write_text('Synthetic package instructions')
        self.wrapper = self.root / 'package-test.ps1'
        self.wrapper.write_text(r"""
function dotnet {
    if ($args[0] -eq 'publish') {
        $index = [Array]::IndexOf($args, '-o')
        $output = $args[$index + 1]
        New-Item -ItemType Directory -Force $output | Out-Null
        Set-Content "$output/ChannelWave.Host.Maui.exe" 'Synthetic application'
        Set-Content "$output/tdjson.dll" 'Synthetic native library'
        if ($env:CHANNELWAVE_TEST_INJECT_SECRET -eq '1') {
            Set-Content "$output/secret.pem" $env:CHANNELWAVE_TEST_SECRET
        }
    }
}
function python {
    if ($args[0] -like '*security-check.py') {
        & $env:CHANNELWAVE_TEST_PYTHON @args
        if ($LASTEXITCODE -ne 0) { throw 'Artifact security gate rejected the package' }
    }
}
& "$PSScriptRoot/scripts/package-desktop.ps1" -Platform windows -Version 0.1.0-beta.1 -BuildNumber 7
""", encoding='utf-8')
        self.archive = self.root / 'artifacts/release-windows/ChannelWave-0.1.0-beta.1-windows-x64.zip'

    def package(self, inject_secret=False):
        pwsh = shutil.which('pwsh')
        self.assertIsNotNone(pwsh, 'PowerShell 7 is required for packaging tests')
        environment = os.environ | {
            'CHANNELWAVE_TEST_PYTHON': sys.executable,
            'CHANNELWAVE_TEST_INJECT_SECRET': '1' if inject_secret else '0',
            'CHANNELWAVE_TEST_SECRET': '-----BEGIN ' + 'PRIVATE KEY-----',
        }
        return subprocess.run([pwsh, '-NoProfile', '-NonInteractive', '-File', str(self.wrapper)],
                              cwd=self.root, env=environment, capture_output=True, text=True, timeout=60)

    def test_first_windows_publish_scans_created_output_and_packages_it(self):
        self.assertFalse((self.root / 'artifacts/publish-windows').exists())

        result = self.package()

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        with zipfile.ZipFile(self.archive) as archive:
            self.assertTrue({'ChannelWave.Host.Maui.exe', 'tdjson.dll', 'README.md',
                             'native-build.json', 'native-licenses/TDLib.txt'} <= set(archive.namelist()))

    def test_windows_publish_rejects_secrets_before_archiving(self):
        result = self.package(inject_secret=True)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn('private-key', result.stdout + result.stderr)
        self.assertFalse(self.archive.exists())
