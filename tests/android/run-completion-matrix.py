"""Run the EOF regression matrix serially on one isolated Android emulator."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--output', default='artifacts/android-completion/after')
args = parser.parse_args()
output = Path(args.output)
output.mkdir(parents=True, exist_ok=True)
cases = [(scenario, screen) for scenario in ['missing', 'all'] for screen in ['foreground', 'background', 'off']]
cases += [(scenario, 'off') for scenario in ['off', 'one', 'shuffle', 'slow', 'mp3', 'resume', 'tiny', 'offline', 'download']]
results = []
for scenario, screen in cases:
    run = subprocess.run([sys.executable, str(Path(__file__).with_name('run-completion-checks.py')),
                          '--scenario', scenario, '--screen', screen, '--output', str(output)], text=True, capture_output=True, timeout=120)
    report = (output / (scenario + '-' + screen + '.txt')).read_text(encoding='utf-8') if (output / (scenario + '-' + screen + '.txt')).exists() else ''
    starts = re.search(r'RESULT: PASS starts=(\d+)', report)
    result = {'scenario': scenario, 'screen': screen, 'passed': run.returncode == 0 and starts is not None,
              'starts': int(starts[1]) if starts else None}
    results.append(result)
    (output / 'summary.json').write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result), flush=True)
    if not result['passed']:
        print(run.stdout[-4000:] + run.stderr[-2000:], flush=True)
        raise SystemExit(1)
