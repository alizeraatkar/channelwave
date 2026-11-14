"""Fail early if repository policy and the selected build tools drift."""
import argparse
import json
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--node', action='store_true')
args = parser.parse_args()
policy = json.loads(Path('.github/release.json').read_text())['toolchain']
sdk = json.loads(Path('global.json').read_text())['sdk']
actual = {'sdk': subprocess.check_output(['dotnet', '--version'], text=True).strip()}
assert sdk['version'] == policy['sdk'] == actual['sdk'] and sdk['rollForward'] == 'disable', 'SDK policy drift'
if args.node:
    actual['node'] = subprocess.check_output(['node', '--version'], text=True).strip().removeprefix('v')
    assert actual['node'] == policy['node'], 'Node policy drift'
print(json.dumps(actual, sort_keys=True))
