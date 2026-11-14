import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('linux_dependencies', Path(__file__).parents[2] / 'scripts/verify-linux-dependencies.py')
dependencies = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dependencies)


class LinuxDependencyTests(unittest.TestCase):
    def test_optional_runtime_tracing_is_recorded(self):
        value = dependencies.classify('libcoreclrtraceptprovider.so', '\tliblttng-ust.so.0 => not found\n')
        self.assertEqual(value['optionalMissing'], ['liblttng-ust.so.0'])

    def test_missing_tdlib_dependency_is_fatal(self):
        with self.assertRaises(ValueError):
            dependencies.classify('libtdjson.so', '\tlibssl.so.3 => not found\n')

    def test_exception_does_not_hide_other_missing_runtime_libraries(self):
        with self.assertRaises(ValueError):
            dependencies.classify('libcoreclrtraceptprovider.so', 'liblttng-ust.so.0 => not found\nlibstdc++.so.6 => not found\n')

    def test_exception_cannot_apply_to_another_shipped_library(self):
        with self.assertRaises(ValueError):
            dependencies.classify('libtdjson.so', 'liblttng-ust.so.0 => not found\n')

    def test_resolved_dependencies_pass(self):
        self.assertEqual(dependencies.classify('libtdjson.so', 'libssl.so.3 => /lib/libssl.so.3 (0x123)\n')['optionalMissing'], [])
