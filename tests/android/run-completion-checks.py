"""Run real MediaPlayer EOF scenarios on the isolated emulator fixture."""
import argparse
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('--scenario', choices=['all', 'off', 'one', 'shuffle', 'slow', 'mp3', 'resume', 'tiny', 'offline', 'download', 'missing'], default='all')
parser.add_argument('--screen', choices=['foreground', 'background', 'off'], default='foreground')
parser.add_argument('--output', default='artifacts/android-completion/baseline')
args = parser.parse_args()
adb = 'C:/Program Files (x86)/Android/android-sdk/platform-tools/adb.exe'
app = 'dev.channelwave.app.performance'
output = Path(args.output)
output.mkdir(parents=True, exist_ok=True)

def shell(*command):
    result = subprocess.run([adb, '-s', 'emulator-5554', 'shell', *command], text=True, capture_output=True, timeout=15)
    return result.stdout.strip()

shell('am', 'force-stop', app)
shell('input', 'keyevent', 'KEYCODE_WAKEUP')
shell('wm', 'dismiss-keyguard')
shell('run-as', app, 'rm', '-f', 'cache/completion-result.txt', 'cache/completion-go')
shell('am', 'start', '-n', app + '/dev.channelwave.app.CompletionTests', '--es', 'scenario', args.scenario)
report = ''
released = switched = False
deadline = time.monotonic() + 100
try:
    while time.monotonic() < deadline:
        report = shell('run-as', app, 'cat', 'cache/completion-result.txt')
        if 'READY: SCREEN' in report and not released:
            shell('run-as', app, 'touch', 'cache/completion-go')
            released = True
        if 'playing=True preparing=False' in report and not switched:
            if args.screen == 'background': shell('input', 'keyevent', 'KEYCODE_HOME')
            if args.screen == 'off': shell('input', 'keyevent', 'KEYCODE_SLEEP')
            if args.screen == 'off':
                power = shell('dumpsys', 'power')
                for _ in range(15):
                    if 'mWakefulness=Asleep' in power: break
                    time.sleep(0.2)
                    power = shell('dumpsys', 'power')
                (output / (args.scenario + '-power.txt')).write_text(power, encoding='utf-8')
                if 'mWakefulness=Asleep' not in power:
                    raise RuntimeError('Emulator display did not enter sleep')
            switched = True
        if 'RESULT:' in report:
            break
        time.sleep(0.15)
    (output / (args.scenario + '-' + args.screen + '.txt')).write_text(report, encoding='utf-8')
    pid = shell('pidof', app)
    if pid.isdigit():
        try:
            log = subprocess.run([adb, '-s', 'emulator-5554', 'logcat', '-d', '--pid=' + pid, '-t', '800'], text=True, capture_output=True, timeout=8)
            text = log.stdout
        except subprocess.TimeoutExpired as error:
            text = (error.stdout or b'').decode('utf-8', errors='replace') if isinstance(error.stdout, bytes) else error.stdout or ''
        (output / (args.scenario + '-' + args.screen + '-logcat.txt')).write_text(text, encoding='utf-8')
    print(report)
    if not switched or 'RESULT: PASS' not in report:
        raise SystemExit('Completion scenario failed: ' + args.scenario + '/' + args.screen)
finally:
    shell('input', 'keyevent', 'KEYCODE_WAKEUP')
    shell('wm', 'dismiss-keyguard')
    shell('am', 'force-stop', app)
