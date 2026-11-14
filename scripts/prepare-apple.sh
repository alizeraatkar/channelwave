#!/usr/bin/env bash
set -euo pipefail
: "${APPLE_CERTIFICATES_BASE64:?Missing Apple signing certificate archive}"
: "${APPLE_CERTIFICATES_PASSWORD:?Missing certificate password}"
: "${APPLE_PROFILES_BASE64:?Missing provisioning archive}"
: "${ASC_KEY_BASE64:?Missing App Store Connect private key}"
: "${ASC_KEY_ID:?Missing App Store Connect key ID}"
: "${ASC_ISSUER_ID:?Missing App Store Connect issuer ID}"
mkdir -p "$RUNNER_TEMP/channelwave-apple" "$HOME/Library/MobileDevice/Provisioning Profiles" "$HOME/.appstoreconnect/private_keys"
python3 - <<'PY'
import os, base64, pathlib, zipfile
root=pathlib.Path(os.environ['RUNNER_TEMP'])/'channelwave-apple'
for key,name in [('APPLE_CERTIFICATES_BASE64','signing.p12'),('APPLE_PROFILES_BASE64','profiles.zip'),('ASC_KEY_BASE64','AuthKey.p8')]:
 p=root/name; p.write_bytes(base64.b64decode(''.join(os.environ[key].split()),validate=True));p.chmod(0o600)
with zipfile.ZipFile(root/'profiles.zip') as archive:
 imported=[]
 for entry in archive.infolist():
  if not entry.is_dir():
   name=pathlib.Path(entry.filename).name
   if not name.endswith(('.mobileprovision','.provisionprofile')): raise ValueError('Unexpected provisioning file')
   (pathlib.Path.home()/'Library/MobileDevice/Provisioning Profiles'/name).write_bytes(archive.read(entry))
   imported.append(name)
 (root/'imported-profiles.json').write_text(__import__('json').dumps(imported))
PY
security create-keychain -p "$APPLE_TEMP_KEYCHAIN_PASSWORD" "$RUNNER_TEMP/channelwave-apple/signing.keychain-db"
security set-keychain-settings -lut 21600 "$RUNNER_TEMP/channelwave-apple/signing.keychain-db"
security unlock-keychain -p "$APPLE_TEMP_KEYCHAIN_PASSWORD" "$RUNNER_TEMP/channelwave-apple/signing.keychain-db"
security import "$RUNNER_TEMP/channelwave-apple/signing.p12" -k "$RUNNER_TEMP/channelwave-apple/signing.keychain-db" -P "$APPLE_CERTIFICATES_PASSWORD" -T /usr/bin/codesign -T /usr/bin/security
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$APPLE_TEMP_KEYCHAIN_PASSWORD" "$RUNNER_TEMP/channelwave-apple/signing.keychain-db" >/dev/null
security list-keychains -d user -s "$RUNNER_TEMP/channelwave-apple/signing.keychain-db" "$HOME/Library/Keychains/login.keychain-db"
cp "$RUNNER_TEMP/channelwave-apple/AuthKey.p8" "$HOME/.appstoreconnect/private_keys/AuthKey_${ASC_KEY_ID}.p8"
