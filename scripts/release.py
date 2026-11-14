"""Same-commit beta reservations and idempotent publication. No third-party packages."""
import argparse
import hashlib
import json
import importlib.util
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.parse
import urllib.request


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        redirected = super().redirect_request(request, response, code, message, headers, url)
        if urllib.parse.urlsplit(request.full_url).netloc != urllib.parse.urlsplit(url).netloc:
            redirected.remove_header('Authorization')
        return redirected


class GitHub:
    def __init__(self):
        self.base = 'https://api.github.com/repos/' + os.environ['GITHUB_REPOSITORY']
        self.opener = urllib.request.build_opener(SafeRedirect())

    def request(self, method, path, body=None, binary=False):
        url = path if path.startswith('https://') else self.base + path
        if urllib.parse.urlsplit(url).hostname not in ('api.github.com', 'uploads.github.com'):
            raise ValueError('Unexpected GitHub API host')
        data = body if binary else json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(url, data=data, method=method, headers={
            'Authorization': 'Bearer ' + os.environ['GH_TOKEN'],
            'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
            'Content-Type': 'application/octet-stream' if binary else 'application/json',
            'User-Agent': 'ChannelWave-release',
        })
        try:
            with self.opener.open(request, timeout=120) as response:
                result = response.read()
                return json.loads(result) if result else None
        except urllib.error.HTTPError as error:
            if error.code == 404 and method == 'GET':
                return None
            # Do not print API responses: upload errors can contain credentialed URLs.
            raise RuntimeError(f'GitHub {method} failed: HTTP {error.code}') from None

    def pages(self, path):
        result = []
        for page in range(1, 1001):
            rows = self.request('GET', path + ('&' if '?' in path else '?') + f'per_page=100&page={page}') or []
            result.extend(rows)
            if len(rows) < 100:
                return result
        raise RuntimeError('GitHub pagination limit exceeded')

    def download_json(self, asset):
        request = urllib.request.Request(self.base + '/releases/assets/' + str(asset['id']), headers={
            'Authorization': 'Bearer ' + os.environ['GH_TOKEN'], 'Accept': 'application/octet-stream',
            'User-Agent': 'ChannelWave-release', 'X-GitHub-Api-Version': '2022-11-28'})
        with self.opener.open(request, timeout=120) as response:
            data = response.read(1024 * 1024 + 1)
        if len(data) > 1024 * 1024 or 'sha256:' + hashlib.sha256(data).hexdigest() != asset.get('digest'):
            raise ValueError('Published manifest integrity check failed')
        return json.loads(data), data


def read_config():
    config = json.loads(Path('.github/release.json').read_text())
    if not re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)', config['baseVersion']) or config['channel'] != 'beta':
        raise ValueError('Invalid beta configuration')
    if not any(value['enabled'] for value in config['platforms'].values()):
        raise ValueError('At least one release platform must be enabled')
    for value in config['platforms'].values():
        if not isinstance(value['enabled'], bool):
            raise ValueError('Platform enabled flags must be JSON booleans')
        if not value['enabled'] and not value.get('reason'):
            raise ValueError('Disabled platforms require a visible reason')
    return config


def ensure_main(api, sha):
    if not re.fullmatch('[a-f0-9]{40}', sha):
        raise ValueError('An exact commit SHA is required')
    comparison = api.request('GET', '/compare/' + sha + '...main')
    if not comparison or comparison['status'] not in ('ahead', 'identical'):
        raise ValueError('Release commit must belong to main history')


def reservation(api, config, sha, run_id, recover_tag=None):
    ensure_main(api, sha)
    prefix = 'v' + config['baseVersion'] + '-beta.'
    refs = api.request('GET', '/git/matching-refs/tags/v') or []
    sequence, build = 0, config['minimumBuildNumber']
    existing = []
    for ref in refs:
        tag = ref['ref'].removeprefix('refs/tags/')
        if re.fullmatch(re.escape(prefix) + r'[1-9]\d*', tag):
            sequence = max(sequence, int(tag[len(prefix):]))
        if ref['object']['type'] != 'tag':
            continue  # Legacy tags are never mistaken for pipeline reservations.
        obj = api.request('GET', '/git/tags/' + ref['object']['sha'])
        try:
            metadata = json.loads(obj['message'])
        except (ValueError, TypeError):
            continue
        if metadata.get('schema') != 1 or metadata.get('application') != 'ChannelWave':
            continue
        if metadata['commit'] != obj['object']['sha'] or metadata['tag'] != tag:
            raise ValueError('Reservation tag metadata does not match its commit')
        build = max(build, metadata['buildNumber'])
        if metadata['commit'] == sha:
            existing.append(metadata)
    if len(existing) > 1:
        raise ValueError('Multiple reservations for one commit require operator investigation')
    if existing:
        result = existing[0]
        if recover_tag and result['tag'] != recover_tag:
            raise ValueError('Recovery tag does not match the reserved commit')
        # Configuration is immutable for the lifetime of a reservation.
        if result['platforms'] != config['platforms'] or result['toolchain'] != config['toolchain'] or result['baseVersion'] != config['baseVersion']:
            raise ValueError('Recovery must use the reserved platform configuration')
        return result
    if recover_tag:
        raise ValueError('Recovery requires an existing pipeline reservation')
    if not 0 < build + 1 <= 2100000000:
        raise ValueError('Android build number exhausted')
    tag = prefix + str(sequence + 1)
    result = {'schema': 1, 'application': 'ChannelWave', 'tag': tag, 'version': tag[1:],
              'baseVersion': config['baseVersion'], 'buildNumber': build + 1, 'commit': sha,
              'reservationRun': str(run_id), 'platforms': config['platforms'], 'toolchain': config['toolchain']}
    annotated = api.request('POST', '/git/tags', {'tag': tag, 'message': json.dumps(result, sort_keys=True),
                                                'object': sha, 'type': 'commit'})
    # Atomic ref creation rejects collisions. No force pushes, tag moves or version reuse.
    api.request('POST', '/git/refs', {'ref': 'refs/tags/' + tag, 'sha': annotated['sha']})
    return result


def file_hash(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def package_manifest(platform, folder, reserved):
    if not reserved['platforms'][platform]['enabled']:
        raise ValueError('Cannot package a disabled platform')
    files = sorted(path for path in Path(folder).iterdir() if path.name != f'{platform}-manifest.json')
    if not files or any(not path.is_file() for path in files):
        raise ValueError('Package output must contain files only')
    spec = importlib.util.spec_from_file_location('security_check', Path(__file__).with_name('security-check.py'))
    security = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(security)
    if security.scan_artifact(folder)['findings']:
        raise ValueError('Release assets contain forbidden private files, secret patterns or unsafe archive paths')
    manifest = {'schema': 1, 'platform': platform, 'commit': reserved['commit'],
                'version': reserved['version'], 'buildNumber': reserved['buildNumber'],
                'assets': [{'name': path.name, 'size': path.stat().st_size, 'sha256': file_hash(path)} for path in files]}
    Path(folder, f'{platform}-manifest.json').write_text(json.dumps(manifest, indent=2, sort_keys=True) + '\n', newline='\n')


def collect(folder, reserved):
    root = Path(folder)
    manifests = sorted(path for path in root.glob('*-manifest.json') if path.name != 'release-manifest.json')
    enabled = {key for key, value in reserved['platforms'].items() if value['enabled']}
    actual = set()
    assets = {}
    for path in manifests:
        manifest = json.loads(path.read_text())
        platform = manifest['platform']
        if platform in actual or platform not in enabled:
            raise ValueError('Unexpected or duplicate platform manifest')
        actual.add(platform)
        for key in ('commit', 'version', 'buildNumber'):
            if manifest[key] != reserved[key]:
                raise ValueError('Package belongs to another release')
        for asset in manifest['assets']:
            name = asset['name']
            if Path(name).name != name or '\\' in name or name in assets:
                raise ValueError('Invalid or duplicate asset name')
            target = root / name
            if not target.is_file() or target.stat().st_size != asset['size'] or file_hash(target) != asset['sha256']:
                raise ValueError('Package integrity verification failed: ' + name)
            assets[name] = asset
        expected_packages = {
            'android': [f"ChannelWave-{reserved['version']}-android-arm64.apk", 'TDLib-Android-notices.zip'],
            'windows': [f"ChannelWave-{reserved['version']}-windows-x64.zip"],
            'linux': [f"ChannelWave-{reserved['version']}-linux-x64.deb", f"ChannelWave-{reserved['version']}-linux-x64.tar.gz"],
            'macos': [f"ChannelWave-{reserved['version']}-macos-arm64.dmg", 'TDLib-macos-notices.tar.gz'],
            'ios': [f"TestFlight-{reserved['version']}.json", 'TDLib-ios-notices.tar.gz'],
        }
        names = {value['name'] for value in manifest['assets'] if value['size'] > 0}
        if not set(expected_packages[platform]) <= names:
            raise ValueError('Missing required package files for ' + platform)
        assets[path.name] = {'name': path.name, 'size': path.stat().st_size, 'sha256': file_hash(path)}
    if actual != enabled:
        raise ValueError('Missing required platform artifacts: ' + ', '.join(sorted(enabled - actual)))
    expected = set(assets)
    extras = {path.name for path in root.iterdir()} - expected - {'release-manifest.json', 'SHA256SUMS'}
    if extras:
        raise ValueError('Unmanifested files in release output: ' + ', '.join(sorted(extras)))
    release = reserved | {'assets': list(assets.values())}
    (root / 'release-manifest.json').write_text(json.dumps(release, indent=2, sort_keys=True) + '\n', newline='\n')
    assets['release-manifest.json'] = {'name': 'release-manifest.json', 'size': (root / 'release-manifest.json').stat().st_size,
                                       'sha256': file_hash(root / 'release-manifest.json')}
    (root / 'SHA256SUMS').write_text(''.join(f"{asset['sha256']}  {asset['name']}\n" for asset in sorted(assets.values(), key=lambda value: value['name'])), newline='\n')
    assets['SHA256SUMS'] = {'name': 'SHA256SUMS', 'size': (root / 'SHA256SUMS').stat().st_size, 'sha256': file_hash(root / 'SHA256SUMS')}
    return assets


def publish(api, folder, reserved):
    ensure_main(api, reserved['commit'])
    ref = api.request('GET', '/git/ref/tags/' + reserved['tag'])
    if not ref or ref['object']['type'] != 'tag':
        raise ValueError('Missing annotated reservation')
    annotation = api.request('GET', '/git/tags/' + ref['object']['sha'])
    if annotation['object']['sha'] != reserved['commit'] or json.loads(annotation['message']) != reserved:
        raise ValueError('Reservation changed before publication')
    assets = collect(folder, reserved)
    release = api.request('GET', '/releases/tags/' + reserved['tag'])
    if not release:
        # Drafts are not consistently returned by /releases/tags before publication.
        drafts = [value for value in api.pages('/releases') if value['tag_name'] == reserved['tag']]
        if len(drafts) > 1:
            raise ValueError('Multiple draft releases require operator investigation')
        release = drafts[0] if drafts else None
    pending = '\n'.join(f"- {key}: {value['reason']}" for key, value in reserved['platforms'].items() if not value['enabled'])
    notes = (f"Commit: `{reserved['commit']}`\n\nAutomated beta; build {reserved['buildNumber']}. "
             'Windows is an unsigned portable build. See SHA256SUMS and release-manifest.json.\n\n'
             + ('Pending platforms:\n' + pending + '\n\n' if pending else '')
             + 'iOS delivery, when enabled, is through internal TestFlight.\n')
    if not release:
        generated = api.request('POST', '/releases/generate-notes', {'tag_name': reserved['tag'], 'target_commitish': reserved['commit']})
        release = api.request('POST', '/releases', {'tag_name': reserved['tag'], 'target_commitish': reserved['commit'],
            'name': 'ChannelWave ' + reserved['version'], 'body': notes + '\n' + generated['body'], 'draft': True, 'prerelease': True})
    uploaded = {value['name']: value for value in api.pages(f"/releases/{release['id']}/assets")}
    if set(uploaded) - set(assets):
        raise ValueError('Unexpected assets already exist on this release')
    for name, asset in assets.items():
        existing = uploaded.get(name)
        if existing:
            if existing.get('state') != 'uploaded' or existing.get('size') != asset['size'] or existing.get('digest') != 'sha256:' + asset['sha256']:
                raise ValueError('Existing release asset differs; refusing overwrite: ' + name)
        elif not release['draft']:
            raise ValueError('Published releases cannot be amended')
        else:
            api.request('POST', release['upload_url'].split('{')[0] + '?name=' + urllib.parse.quote(name),
                        (Path(folder) / name).read_bytes(), binary=True)
    # Re-read server digests after all uploads, including resumed partial drafts.
    uploaded = {value['name']: value for value in api.pages(f"/releases/{release['id']}/assets")}
    if set(uploaded) != set(assets) or any(uploaded[name].get('digest') != 'sha256:' + value['sha256'] for name, value in assets.items()):
        raise ValueError('Server asset digest verification failed')
    if release['draft']:
        api.request('PATCH', f"/releases/{release['id']}", {'draft': False, 'prerelease': True, 'make_latest': 'false'})
    return release['html_url']


def verify_published(api, reserved):
    value = api.request('GET', '/releases/tags/' + reserved['tag'])
    if not value or value['draft'] or not value['prerelease']:
        raise ValueError('Expected an existing published beta')
    assets = api.pages(f"/releases/{value['id']}/assets")
    indexed = {asset['name']: asset for asset in assets}
    if len(indexed) != len(assets) or 'release-manifest.json' not in indexed:
        raise ValueError('Missing or duplicate published manifest')
    manifest, data = api.download_json(indexed['release-manifest.json'])
    if any(manifest.get(key) != item for key, item in reserved.items()):
        raise ValueError('Published release belongs to another reservation')
    expected = {asset['name']: asset for asset in manifest['assets']}
    required = {key + '-manifest.json' for key, item in reserved['platforms'].items() if item['enabled']}
    if not required <= set(expected):
        raise ValueError('Published beta is missing platform manifests')
    expected['release-manifest.json'] = {'sha256': hashlib.sha256(data).hexdigest(), 'size': len(data)}
    checksums = ''.join(f"{asset['sha256']}  {name}\n" for name, asset in sorted(expected.items())).encode()
    expected['SHA256SUMS'] = {'sha256': hashlib.sha256(checksums).hexdigest(), 'size': len(checksums)}
    if set(indexed) != set(expected) or any(indexed[name].get('state') != 'uploaded' or indexed[name].get('size') != item['size']
            or indexed[name].get('digest') != 'sha256:' + item['sha256'] for name, item in expected.items()):
        raise ValueError('Published assets differ from the reserved manifest')
    return value['html_url']


def outputs(values):
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            for key, value in values.items():
                output.write(f'{key}={value}\n')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['config', 'resolve', 'reserve', 'package', 'publish', 'verify-published'])
    parser.add_argument('--sha', default=os.environ.get('GITHUB_SHA'))
    parser.add_argument('--recover-tag')
    parser.add_argument('--reservation', default='artifacts/reservation.json')
    parser.add_argument('--folder', default='artifacts/release')
    parser.add_argument('--platform')
    args = parser.parse_args()
    if args.command == 'config':
        config = read_config()
        outputs({key: str(value['enabled']).lower() for key, value in config['platforms'].items()})
        print(json.dumps(config, indent=2))
    elif args.command == 'resolve':
        sha = args.sha
        if args.recover_tag:
            if not re.fullmatch(r'v\d+\.\d+\.\d+-beta\.[1-9]\d*', args.recover_tag):
                raise ValueError('Invalid recovery tag')
            api = GitHub()
            ref = api.request('GET', '/git/ref/tags/' + args.recover_tag)
            if not ref or ref['object']['type'] != 'tag':
                raise ValueError('Recovery requires a pipeline reservation')
            annotation = api.request('GET', '/git/tags/' + ref['object']['sha'])
            metadata = json.loads(annotation['message'])
            sha = annotation['object']['sha']
            if metadata.get('application') != 'ChannelWave' or metadata.get('commit') != sha or metadata.get('tag') != args.recover_tag:
                raise ValueError('Invalid reservation metadata')
            ensure_main(api, sha)
        outputs({'sha': sha})
    elif args.command == 'reserve':
        value = reservation(GitHub(), read_config(), args.sha, os.environ['GITHUB_RUN_ID'], args.recover_tag)
        target = Path(args.reservation); target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n', newline='\n')
        outputs({'version': value['version'], 'base-version': value['baseVersion'], 'build-number': value['buildNumber'], 'tag': value['tag']})
        published = GitHub().request('GET', '/releases/tags/' + value['tag'])
        outputs({'published': str(bool(published and not published['draft'])).lower()})
        print(f"Reserved {value['tag']} (build {value['buildNumber']}) at {value['commit']}")
    else:
        reserved = json.loads(Path(args.reservation).read_text())
        if args.command == 'package':
            package_manifest(args.platform, args.folder, reserved)
        elif args.command == 'publish':
            print(publish(GitHub(), args.folder, reserved))
        else:
            print(verify_published(GitHub(), reserved))


if __name__ == '__main__':
    main()
