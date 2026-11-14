"""Verify locked restores cannot silently use SDK-bundled package variants."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class RestoreSourceTests(unittest.TestCase):
    def test_sdk_library_packs_are_excluded_but_explicit_override_enables_them(self):
        repository = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary).resolve()
            command = ['dotnet', 'msbuild', 'src/ChannelWave.Core/ChannelWave.Core.csproj',
                       '-nologo', '-getProperty:RestoreAdditionalProjectSources',
                       '-p:_WorkloadLibraryPacksFolder=' + str(folder)]
            environment = os.environ | {'DOTNET_NOLOGO': 'true', 'DOTNET_CLI_TELEMETRY_OPTOUT': 'true'}
            def sources(extra):
                output = subprocess.check_output(command + extra, cwd=repository,
                                                 env=environment, text=True, timeout=60).strip()
                return [Path(value).resolve() for value in output.split(';') if value.strip()]
            self.assertNotIn(folder, sources([]))
            self.assertIn(folder, sources(['-p:DisableImplicitLibraryPacksFolder=false']))
