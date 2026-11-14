"""Check package ID, signer and monotonic versionCode against published APKs."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.request

from release import GitHub, SafeRedirect

apk = Path(sys.argv[1])
tools = Path(sys.argv[2])
expected = int(os.environ['RELEASE_BUILD_NUMBER'])


def metadata(path):
    signature = subprocess.check_output([str(tools / 'apksigner'), 'verify', '--print-certs', str(path)], text=True)
    certs = sorted(re.findall(r'certificate SHA-256 digest: ([a-fA-F0-9]+)', signature))
    badging = subprocess.check_output([str(tools / 'aapt2'), 'dump', 'badging', str(path)], text=True)
    package = re.search(r"package: name='([^']+)' versionCode='(\d+)'", badging)
    if not package or not certs:
        raise ValueError('Missing APK signing/package metadata')
    return {'packageId': package[1], 'versionCode': int(package[2]), 'signerSha256': certs}


current = metadata(apk)
assert current['packageId'] == 'dev.channelwave.app' and current['versionCode'] == expected
api = GitHub()
checked = []
with tempfile.TemporaryDirectory() as temporary:
    opener = urllib.request.build_opener(SafeRedirect())
    for item in api.pages('/releases'):
        if item['draft']:
            continue
        if item['tag_name'] == 'v' + os.environ['RELEASE_VERSION']:
            continue
        for asset in item['assets']:
            if not asset['name'].endswith('-android-arm64.apk'):
                continue
            request = urllib.request.Request(api.base + '/releases/assets/' + str(asset['id']), headers={
                'Authorization': 'Bearer ' + os.environ['GH_TOKEN'], 'Accept': 'application/octet-stream',
                'User-Agent': 'ChannelWave-Android-upgrade-check', 'X-GitHub-Api-Version': '2022-11-28'})
            path = Path(temporary) / 'previous.apk'
            with opener.open(request, timeout=120) as response, path.open('wb') as output:
                import shutil
                shutil.copyfileobj(response, output)
            previous = metadata(path)
            assert previous['packageId'] == current['packageId'], 'Application ID changed'
            assert previous['signerSha256'] == current['signerSha256'], 'Existing installations require the original signing key'
            assert previous['versionCode'] < current['versionCode'], 'Android versionCode must exceed every published APK'
            checked.append({'tag': item['tag_name'], 'versionCode': previous['versionCode']})
(apk.parent / 'android-upgrade-verification.json').write_text(json.dumps(current | {'previousReleases': checked,
    'scope': 'Package identity/signature/version compatibility; does not prove device data migration'}, indent=2) + '\n')
