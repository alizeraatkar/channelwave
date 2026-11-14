"""Release gates using the Python standard library. Never print matched secret values.

Secret detection is deliberately bounded pattern matching, not a proof that a tree
contains no secrets. All reachable history, staged blobs and nonignored working
files are checked. Dependency checks fail closed when a feed is unavailable.
"""
import argparse
import gzip
import io
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import tarfile
import urllib.parse
import urllib.request
import zipfile


PATTERNS = {
    'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----'),
    'aws-access-key': re.compile(r'\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'),
    'github-token': re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,255}|github_pat_[A-Za-z0-9_]{50,255})\b'),
    'telegram-bot-token': re.compile(r'\b\d{8,12}:[A-Za-z0-9_-]{35}\b'),
    'google-api-key': re.compile(r'\bAIza[0-9A-Za-z_-]{35}\b'),
    'npm-token': re.compile(r'\bnpm_[A-Za-z0-9]{36}\b'),
    'azure-shared-key': re.compile(r'(?i)\b(?:AccountKey|SharedAccessKey)\s*=\s*[A-Za-z0-9/+]{40,}={0,2}'),
    'jwt': re.compile(r'\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{16,}\b'),
    'telegram-app-hash': re.compile(r'(?i)["\']?(?:apiHash|api_hash)["\']?\s*[:=]\s*["\']([a-f0-9]{32})["\']'),
    'connection-password': re.compile(r'(?i)\b(?:Password|Pwd)\s*=\s*([^;\s"\']{6,})'),
}
PLACEHOLDERS = {'password', 'changeme', 'your-password', 'your-key-here', 'placeholder', 'example', 'synthetic'}
FORBIDDEN_SUFFIXES = {'.db', '.db3', '.sqlite', '.sqlite3', '.db-wal', '.db-shm', '.binlog', '.session', '.keystore', '.jks', '.pfx', '.p12', '.p8'}
MAX_ARCHIVE_BYTES = 1024 * 1024 * 1024


def command(arguments, cwd):
    result = subprocess.run(arguments, cwd=cwd, capture_output=True)
    if result.returncode:
        raise ValueError(f'{arguments[0]} {arguments[1]} failed (exit {result.returncode}); output withheld')
    return result.stdout


def find_secrets(data):
    if data.startswith((b'\xff\xfe', b'\xfe\xff')):
        # Windows configuration can be BOM-marked UTF-16, including its NUL bytes.
        text = data.decode('utf-16', errors='replace')
    elif b'\0' in data[:8192]:
        # Unmarked binary payloads remain outside this bounded text-pattern scan.
        return []
    else:
        # A legacy byte elsewhere must not hide an otherwise ASCII credential.
        text = data.decode('utf-8-sig', errors='replace')
    findings = []
    for number, line in enumerate(text.splitlines(), 1):
        for rule, pattern in PATTERNS.items():
            for match in pattern.finditer(line):
                value = match.group(1) if match.lastindex else match.group()
                if value.lower() in PLACEHOLDERS or re.fullmatch(r'([a-f0-9])\1{31}', value, re.I):
                    continue
                # Shell/template expressions are not literal connection passwords.
                if rule == 'connection-password' and (value.startswith(('$(', '${', '<', '{')) or re.fullmatch(r'\$[A-Za-z_]\w*', value)):
                    continue
                findings.append({'rule': rule, 'line': number})
    return findings


def scan_repository(repo):
    repo = Path(repo).resolve()
    if command(['git', 'rev-parse', '--is-shallow-repository'], repo).strip() == b'true':
        raise ValueError('Full history is required; fetch with fetch-depth: 0 before scanning')
    objects = command(['git', 'rev-list', '--objects', '--all'], repo).splitlines()
    indexed = command(['git', 'ls-files', '--stage', '-z'], repo).split(b'\0')
    names = {}
    for row in objects:
        oid, _, name = row.partition(b' ')
        names.setdefault(oid, name.decode('utf-8', errors='replace'))
    for row in indexed:
        if row:
            metadata, name = row.split(b'\t', 1)
            names.setdefault(metadata.split()[1], name.decode('utf-8', errors='replace'))
    findings = []
    # cat-file's byte-count protocol handles embedded newlines and avoids shell execution.
    process = subprocess.Popen(['git', 'cat-file', '--batch'], cwd=repo, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    try:
        for oid, name in names.items():
            process.stdin.write(oid + b'\n')
            process.stdin.flush()
            header = process.stdout.readline().split()
            if len(header) != 3:
                raise ValueError('Unable to inspect a reachable Git object')
            size = int(header[2])
            data = process.stdout.read(size)
            if len(data) != size or process.stdout.read(1) != b'\n':
                raise ValueError('Truncated Git object inspection')
            if header[1] == b'blob':
                if any(PurePosixPath(name).name.lower().endswith(suffix) for suffix in FORBIDDEN_SUFFIXES):
                    findings.append({'rule': 'private-runtime-or-signing-file', 'path': name, 'object': oid.decode()[:12]})
                for finding in find_secrets(data):
                    findings.append(finding | {'path': name, 'object': oid.decode()[:12]})
        process.stdin.close()
        if process.wait() != 0:
            raise ValueError('Git object inspection failed')
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        process.stdout.close()
        if not process.stdin.closed:
            process.stdin.close()
    files = command(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], repo).split(b'\0')
    for name in set(files) - {b''}:
        path = repo / name.decode('utf-8', errors='strict')
        if path.is_file() and not path.is_symlink():
            if any(path.name.lower().endswith(suffix) for suffix in FORBIDDEN_SUFFIXES):
                findings.append({'rule': 'private-runtime-or-signing-file', 'path': name.decode(), 'object': 'working-tree'})
            for finding in find_secrets(path.read_bytes()):
                findings.append(finding | {'path': name.decode(), 'object': 'working-tree'})
    return {'kind': 'bounded-secret-pattern-scan', 'objects': len(names), 'workingFiles': len(set(files) - {b''}), 'findings': findings}


def config_allowed(name, data, allow_web_config):
    leaf = PurePosixPath(name).name
    if leaf not in ('telegram.json', 'channelwave.local.json'):
        return False
    if leaf == 'channelwave.local.json' and not allow_web_config:
        raise ValueError('Developer configuration is excluded from default web output')
    try:
        value = json.loads(data)
    except (ValueError, UnicodeDecodeError):
        raise ValueError('Packaged application registration is invalid JSON') from None
    if not isinstance(value, dict) or set(value) != {'apiId', 'apiHash'} or type(value['apiId']) is not int or not 0 < value['apiId'] <= 2147483647 or not isinstance(value['apiHash'], str) or not re.fullmatch('[a-fA-F0-9]{32}', value['apiHash']):
        raise ValueError('Packaged application registration must contain only valid apiId/apiHash fields')
    return True


def scan_artifact(target, allow_web_config=False):
    findings = []
    count = 0
    budget = MAX_ARCHIVE_BYTES

    def safe_member(name):
        path = PurePosixPath(name.replace('\\', '/'))
        return not path.is_absolute() and '..' not in path.parts and not re.match(r'^[a-zA-Z]:', name)

    def inspect(name, data, depth=0):
        nonlocal count, budget
        count += 1
        budget -= len(data)
        if depth > 4 or budget < 0:
            raise ValueError('Artifact inspection budget exceeded')
        path = PurePosixPath(name.replace('\\', '/'))
        if not safe_member(name):
            findings.append({'path': name, 'rule': 'archive-path-escape'})
            return
        leaf = path.name.lower()
        if any(leaf.endswith(suffix) for suffix in FORBIDDEN_SUFFIXES) or leaf.startswith('.env') or leaf in ('td.binlog', 'profile.lock', 'signing.keychain-db'):
            findings.append({'path': name, 'rule': 'private-runtime-or-signing-file'})
            return
        if config_allowed(name, data, allow_web_config):
            return
        for finding in find_secrets(data):
            findings.append(finding | {'path': name})
        content = io.BytesIO(data)
        if zipfile.is_zipfile(content):
            with zipfile.ZipFile(content) as archive:
                for entry in archive.infolist():
                    if not safe_member(entry.filename):
                        findings.append({'path': name + '!/' + entry.filename, 'rule': 'archive-path-escape'})
                        continue
                    if entry.file_size > budget:
                        raise ValueError('Artifact inspection budget exceeded')
                    if (entry.external_attr >> 16) & 0o170000 == 0o120000:
                        payload = archive.read(entry)
                        budget -= len(payload)
                        link = payload.decode('utf-8')
                        if not safe_member(link):
                            findings.append({'path': name + '!/' + entry.filename, 'rule': 'archive-link-escape'})
                        continue
                    if entry.is_dir():
                        continue
                    inspect(name + '!/' + entry.filename, archive.read(entry), depth + 1)
        elif leaf.endswith(('.tar', '.tar.gz', '.tgz')):
            content.seek(0)
            with tarfile.open(fileobj=content, mode='r:*') as archive:
                for entry in archive:
                    if not safe_member(entry.name):
                        findings.append({'path': name + '!/' + entry.name, 'rule': 'archive-path-escape'})
                        continue
                    if entry.isfile():
                        if entry.size > budget:
                            raise ValueError('Artifact inspection budget exceeded')
                        inspect(name + '!/' + entry.name.removeprefix('./'), archive.extractfile(entry).read(), depth + 1)
                    elif entry.issym() or entry.islnk():
                        # Runtime library symlinks are allowed only within their archive directory.
                        if not safe_member(entry.linkname):
                            findings.append({'path': name + '!/' + entry.name, 'rule': 'archive-link-escape'})

    target = Path(target)
    if not target.exists():
        raise ValueError('Artifact target does not exist')
    if target.is_dir():
        for file in target.rglob('*'):
            if file.is_symlink():
                if not file.resolve().is_relative_to(target.resolve()):
                    findings.append({'path': str(file.relative_to(target)), 'rule': 'artifact-link-escape'})
                continue
            if file.is_file():
                if file.stat().st_size > budget:
                    raise ValueError('Artifact inspection budget exceeded')
                inspect(file.relative_to(target).as_posix(), file.read_bytes())
    else:
        if target.stat().st_size > budget:
            raise ValueError('Artifact inspection budget exceeded')
        inspect(target.name, target.read_bytes())
    return {'kind': 'artifact-pattern-and-path-scan', 'files': count, 'findings': findings}


def nuget_version(value):
    value = value.split('+', 1)[0].lower()
    main, _, prerelease = value.partition('-')
    numbers = tuple(int(number) for number in main.split('.'))
    if not 1 <= len(numbers) <= 4:
        raise ValueError('Unsupported NuGet version')
    labels = tuple((0, int(label)) if label.isdigit() else (1, label) for label in prerelease.split('.')) if prerelease else ()
    return numbers + (0,) * (4 - len(numbers)), not bool(prerelease), labels


def version_in_range(version, interval):
    candidate = nuget_version(version)
    interval = interval.strip()
    if not interval or interval[0] not in '[(' or interval[-1] not in '])':
        raise ValueError('Unsupported NuGet advisory range; audit cannot safely continue')
    limits = interval[1:-1].split(',')
    if len(limits) == 1:
        if interval[0] != '[' or interval[-1] != ']':
            raise ValueError('Invalid exact NuGet advisory range')
        return candidate == nuget_version(limits[0].strip())
    if len(limits) != 2:
        raise ValueError('Invalid NuGet advisory range')
    low, high = (limit.strip() for limit in limits)
    if low:
        lower = nuget_version(low)
        if candidate < lower or candidate == lower and interval[0] == '(':
            return False
    if high:
        upper = nuget_version(high)
        if candidate > upper or candidate == upper and interval[-1] == ')':
            return False
    return True


def fetch_json(url):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != 'https' or parsed.hostname != 'api.nuget.org':
        raise ValueError('Unexpected advisory feed origin')
    with urllib.request.urlopen(url, timeout=45) as response:
        data = response.read(64 * 1024 * 1024 + 1)
        encoding = response.headers.get('Content-Encoding', '').lower()
    if encoding == 'gzip':
        with gzip.GzipFile(fileobj=io.BytesIO(data)) as stream:
            data = stream.read(64 * 1024 * 1024 + 1)
    elif encoding not in ('', 'identity'):
        raise ValueError('Unsupported advisory response encoding')
    if len(data) > 64 * 1024 * 1024:
        raise ValueError('Advisory feed exceeds inspection budget')
    return json.loads(data)


def audit_dependencies(repo):
    repo = Path(repo).resolve()
    names = command(['git', 'ls-files', '-z'], repo).split(b'\0')
    packages = set()
    locks = []
    for name in names:
        path = repo / name.decode()
        if re.fullmatch(r'packages(?:\.[^.]+)*\.lock\.json', path.name):
            locks.append(path)
            for graph in json.loads(path.read_text(encoding='utf-8-sig'))['dependencies'].values():
                for package, metadata in graph.items():
                    if 'resolved' in metadata:
                        packages.add((package.lower(), metadata['resolved']))
    if not packages:
        raise ValueError('No locked NuGet packages were found')
    index = fetch_json('https://api.nuget.org/v3/vulnerabilities/index.json')
    if not isinstance(index, list) or not index:
        raise ValueError('NuGet vulnerability index is empty or invalid')
    vulnerabilities = {}
    for item in index:
        page = fetch_json(item['@id'])
        if not isinstance(page, dict):
            raise ValueError('Invalid NuGet advisory data')
        for package, advisories in page.items():
            vulnerabilities.setdefault(package.lower(), []).extend(advisories)
    findings = []
    for package, version in sorted(packages):
        for advisory in vulnerabilities.get(package, []):
            severity = advisory['severity']
            if type(severity) is not int or severity not in range(4):
                raise ValueError('Invalid NuGet advisory severity')
            if version_in_range(version, advisory['versions']):
                findings.append({'ecosystem': 'NuGet', 'package': package, 'version': version,
                                 'severity': ['low', 'moderate', 'high', 'critical'][severity], 'url': advisory['url']})
    npm_results = []
    for folder in (repo, repo / 'vendor/tdweb-build'):
        if not (folder / 'package-lock.json').is_file():
            raise ValueError('Required npm dependency lockfile is missing')
        result = subprocess.run(['npm.cmd' if sys.platform == 'win32' else 'npm', 'audit', '--package-lock-only', '--ignore-scripts', '--json'], cwd=folder, capture_output=True, text=True)
        try:
            audit = json.loads(result.stdout)
        except ValueError:
            raise ValueError('npm audit did not return a valid advisory report') from None
        if result.returncode not in (0, 1) or audit.get('error') or 'metadata' not in audit or 'vulnerabilities' not in audit:
            raise ValueError('npm advisory feed is unavailable; audit cannot pass')
        npm_results.append({'lockfile': str(folder.relative_to(repo) / 'package-lock.json'), 'counts': audit['metadata']['vulnerabilities']})
        for package, metadata in audit['vulnerabilities'].items():
            if metadata.get('severity') not in ('info', 'low', 'moderate', 'high', 'critical'):
                raise ValueError('Invalid npm advisory severity')
            findings.append({'ecosystem': 'npm', 'lockfile': str(folder.relative_to(repo) / 'package-lock.json'), 'package': package, 'severity': metadata['severity']})
    return {'kind': 'current-advisory-scan', 'nugetLockfiles': len(locks), 'nugetPackages': len(packages), 'npm': npm_results, 'findings': findings}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('check', choices=['secrets', 'artifacts', 'dependencies'])
    parser.add_argument('target', nargs='?', default='.')
    parser.add_argument('--allow-web-config', action='store_true', help='Allow only a sanitized apiId/apiHash browser registration')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    try:
        result = scan_repository(args.target) if args.check == 'secrets' else scan_artifact(args.target, args.allow_web_config) if args.check == 'artifacts' else audit_dependencies(args.target)
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8', newline='\n')
        print(json.dumps(result, indent=2))
        rejected = result['findings'] if args.check != 'dependencies' else [finding for finding in result['findings'] if finding['severity'] in ('high', 'critical')]
        return 1 if rejected else 0
    except (ValueError, KeyError, OSError, RuntimeError, UnicodeError, zipfile.BadZipFile, tarfile.TarError) as error:
        # Exception messages from parsers/network clients may include arbitrary content.
        print(f'Security {args.check} check unavailable or invalid ({type(error).__name__}); no pass recorded.', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
