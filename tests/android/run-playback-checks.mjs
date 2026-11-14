import { _android } from 'playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

// Use the isolated fixture so tests cannot replace the user's current playback.
const app = 'dev.channelwave.app.performance';
const serial = process.env.CHANNELWAVE_EMULATOR || 'emulator-5554';
const output = process.argv[2] || 'artifacts/android-playback';
if (!/^emulator-\d+$/.test(serial)) throw new Error('Choose an Android emulator.');
const devices = await _android.devices({ omitDriverInstall: true });
const device = devices.find(d => d.serial() === serial);
if (!device) { await Promise.all(devices.map(d => d.close())); throw new Error('Emulator unavailable.'); }
const shell = async command => (await device.shell(command)).toString().trim();
let report = '', foregroundLaunch = false;
try {
    await mkdir(output, { recursive: true });
    const component = (await shell(`cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER ${app}`)).split(/\r?\n/).at(-1);
    if (!/^dev\.channelwave\.app\.performance\/[\w.$]+$/.test(component)) throw new Error('Install the isolated fixture with ChannelWavePlaybackTest=true.');
    await shell(`am force-stop ${app}`);
    await shell(`run-as ${app} rm -f cache/channelwave-playback-test-result.txt`);
    await shell('input keyevent KEYCODE_WAKEUP'); await shell('wm dismiss-keyguard');
    await shell(`am start -n ${app}/dev.channelwave.app.PlaybackTests`);
    const deadline = Date.now() + 180000;
    while (Date.now() < deadline) {
        report = await shell(`run-as ${app} cat cache/channelwave-playback-test-result.txt`);
        if (report.includes('RESULT:')) break;
        if (report.includes('Waiting for foreground user launch after activity destruction')
            && !report.includes('Background playback and recreated player UI retain the same song') && !foregroundLaunch) {
            // Android 16 can block an activity's own background launch. An ADB
            // launch models the user reopening the app, without a BAL exemption.
            await shell(`am start -n ${component}`);
            foregroundLaunch = true;
        }
        await new Promise(resolve => setTimeout(resolve, 200));
    }
    await writeFile(join(output, 'result.txt'), report);
    await writeFile(join(output, 'run.json'), JSON.stringify({ serial, foregroundLaunch, passed: report.includes('RESULT: PASS') }, null, 2));
    if (!report.includes('RESULT: PASS')) throw new Error(report || 'Playback runner did not produce a report.');
    console.log(report);
} finally {
    await shell(`am force-stop ${app}`).catch(() => {});
    await Promise.all(devices.map(d => d.close()));
}
