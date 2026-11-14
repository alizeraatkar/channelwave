"""Check shipped Linux libraries, recording only the runtime's optional tracing exception."""
import argparse
import json
from pathlib import Path
import re
import subprocess


def classify(relative_path, output):
    missing = re.findall(r'^\s*(\S+)\s+=>\s+not found\s*$', output, re.MULTILINE)
    # The portable .NET runtime deliberately tolerates dlopen failure for this
    # optional LTTng 2.12 provider. Ubuntu 24.04 provides the incompatible .so.1.
    # https://github.com/dotnet/runtime/blob/v10.0.6/src/coreclr/pal/src/misc/tracepointprovider.cpp
    optional = relative_path == 'libcoreclrtraceptprovider.so' and missing == ['liblttng-ust.so.0']
    if missing and not optional:
        raise ValueError(f'Unresolved Linux native dependencies: {relative_path}: {missing}')
    return {'file': relative_path, 'optionalMissing': missing if optional else [], 'ldd': output}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    root = args.folder.resolve()
    files = sorted(path for path in root.rglob('*') if path.is_file() and re.search(r'\.so(?:\..*)?$', path.name))
    if not files or not (root / 'libtdjson.so').is_file():
        raise ValueError('Missing shipped Linux TDLib library')
    results = []
    for path in files:
        checked = subprocess.run(['ldd', str(path)], capture_output=True, text=True, check=True)
        result = classify(path.relative_to(root).as_posix(), checked.stdout)
        results.append(result)
        print(json.dumps({k: result[k] for k in ('file', 'optionalMissing')}))
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({'result': 'pass', 'libraries': results}, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
