"""Write only application registration credentials; never account/session data."""
import json
import os
from pathlib import Path
import re
import sys

api_id, api_hash = os.environ.get('TELEGRAM_API_ID', ''), os.environ.get('TELEGRAM_API_HASH', '')
if not re.fullmatch(r'[1-9]\d*', api_id) or int(api_id) > 2147483647 or not re.fullmatch(r'[a-fA-F0-9]{32}', api_hash):
    raise SystemExit('Valid TELEGRAM_API_ID and TELEGRAM_API_HASH secrets are required')
target = Path(sys.argv[1])
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps({'apiId': int(api_id), 'apiHash': api_hash}) + '\n')
target.chmod(0o600)
