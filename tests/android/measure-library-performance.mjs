import { _android as android } from 'playwright';
import { expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const app = 'dev.channelwave.app.performance';
const serial = process.env.CHANNELWAVE_EMULATOR || 'emulator-5554';
const output = process.argv[2] || 'artifacts/android-library-performance';
const runs = Number(process.env.CHANNELWAVE_PERF_RUNS || 5);
if (!/^emulator-\d+$/.test(serial) || !Number.isInteger(runs) || runs < 1) {
    throw new Error('Select an emulator serial and a positive run count.');
}

const devices = await android.devices({ omitDriverInstall: true });
const device = devices.find(candidate => candidate.serial() === serial);
if (!device) {
    await Promise.all(devices.map(candidate => candidate.close()));
    throw new Error(`ADB did not report ${serial}.`);
}
const shell = async command => (await device.shell(command)).toString().trim();

function frameStats(raw) {
    const frames = raw.split('---PROFILEDATA---').filter(section => section.trimStart().startsWith('Flags,')).flatMap(section => {
        const [header, ...rows] = section.trim().split(/\r?\n/);
        const columns = header.split(',');
        const deadline = columns.indexOf('FrameDeadline');
        const completed = columns.indexOf('FrameCompleted');
        if (deadline < 0 || completed < 0) return [];
        return rows.map(row => row.split(',').map(Number)).filter(row => row[0] === 0
            && row[deadline] > 0 && row[completed] > 0 && row[completed] < Number.MAX_SAFE_INTEGER)
            .map(row => (row[completed] - row[deadline]) / 1e6);
    });
    if (!frames.length) throw new Error('Android returned no completed frames with deadlines.');
    frames.sort((a, b) => a - b);
    return { sampledFrames: frames.length, missedDeadlines: frames.filter(value => value > 0).length,
        p95OverrunMs: frames[Math.ceil(frames.length * .95) - 1] };
}

async function captureSearch(page, field, query) {
    await page.evaluate(query => {
        const main = document.querySelector('#main-content');
        const afterPaint = callback => requestAnimationFrame(() => requestAnimationFrame(callback));
        const timing = { keyPaintMs: [], queryPaintMs: null };
        window.channelwaveSearchTiming = timing;
        const inputEvents = new AbortController();
        main.addEventListener('keydown', event => {
            if (!event.target.matches('#music-search') || event.key.length !== 1) return;
            afterPaint(() => timing.keyPaintMs.push(performance.now() - event.timeStamp));
        }, { signal: inputEvents.signal });
        main.addEventListener('submit', event => {
            if (!event.target.matches('.music-search')) return;
            inputEvents.abort();
            const started = event.timeStamp;
            timing.submitted = true;
            performance.mark('channelwave-query-submitted');
            const painted = () => {
                const results = document.querySelector('.music-results');
                const title = results?.querySelector('.music-track-title > strong')?.textContent?.toLowerCase();
                if (results?.getAttribute('aria-busy') !== 'false' || !title?.startsWith(query)) return;
                observer.disconnect();
                afterPaint(() => {
                    timing.queryPaintMs = performance.now() - started;
                    performance.mark('channelwave-query-painted');
                });
            };
            const observer = new MutationObserver(painted);
            observer.observe(main, { subtree: true, childList: true, attributes: true, characterData: true });
            painted();
        }, { signal: inputEvents.signal });
    }, query);
    await field.fill('');
    await field.pressSequentially(query.slice(0, 4), { delay: 15 });
    await field.evaluate((input, value) => {
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.form.requestSubmit();
    }, query);
    try { await page.waitForFunction(() => window.channelwaveSearchTiming.queryPaintMs !== null); }
    catch (error) {
        const state = await page.evaluate(() => ({
            timing: window.channelwaveSearchTiming,
            value: document.querySelector('#music-search')?.value,
            busy: document.querySelector('.music-results')?.getAttribute('aria-busy'),
            title: document.querySelector('.music-track-title > strong')?.textContent
        }));
        throw new Error(`Search for ${query} did not reach a painted result: ${JSON.stringify(state)}`, { cause: error });
    }
    return page.evaluate(() => {
        const { keyPaintMs, queryPaintMs } = window.channelwaveSearchTiming;
        return { keyPaintMs, queryPaintMs };
    });
}

async function startFixture(component, withMetadata) {
    await shell(`am force-stop ${app}`);
    const launch = await shell(`am start -W -n ${component} --ez channelwave.performance.metadata ${withMetadata}`);
    if (!launch.includes('Status: ok')) throw new Error(launch);
    const view = await device.webView({ pkg: app }, { timeout: 150_000 });
    const page = await view.page();
    page.setDefaultTimeout(30_000);
    await page.locator('#main-content').waitFor();
    return page;
}

async function runTrial(page, withMetadata, trial) {
    const main = page.locator('#main-content');
    const back = page.getByRole('button', { name: 'Go back', exact: true });
    const library = page.getByRole('navigation', { name: 'Mobile navigation' }).getByRole('button', { name: 'Library', exact: true });
    const tracksTile = page.locator('.library-landing .library-tiles').first().getByRole('button', { name: /^Tracks(?: |$)/ });
    await expect(async () => {
        if (!await library.isVisible()) await back.click({ timeout: 2500 });
        else if (!await tracksTile.isVisible()) await library.click({ timeout: 2500 });
        await expect(tracksTile).toBeVisible({ timeout: 2500 });
    }).toPass({ timeout: 30_000 });
    await tracksTile.click();
    await page.waitForFunction(() => document.querySelector('.virtual-tracks')?.dataset.loadedCount === '100');

    const cdp = await page.context().newCDPSession(page);
    const trace = [];
    cdp.on('Tracing.dataCollected', event => trace.push(...event.value));
    let traceComplete;
    cdp.once('Tracing.tracingComplete', () => traceComplete?.());
    await cdp.send('Tracing.start', { categories: 'devtools.timeline,blink.user_timing', transferMode: 'ReportEvents' });
    const frameSamples = [];
    const base = `${withMetadata ? 'metadata' : 'idle'}-${trial}`;
    for (let expected = 200; expected <= 300; expected += 100) {
        await shell(`dumpsys gfxinfo ${app} reset`);
        await main.evaluate(async panel => {
            performance.mark('channelwave-scroll-start');
            for (let frame = 0; frame < 36; frame++) {
                await new Promise(resolve => requestAnimationFrame(resolve));
                panel.scrollTop += 130;
            }
            panel.scrollTop = panel.scrollHeight;
            performance.mark('channelwave-scroll-end');
        });
        await page.waitForFunction(expected => Number(document.querySelector('.virtual-tracks')?.dataset.loadedCount) >= expected, expected);
        const raw = await shell(`dumpsys gfxinfo ${app} framestats`);
        await writeFile(join(output, `${base}-frames-${expected}.txt`), raw);
        frameSamples.push(frameStats(raw));
    }
    let traceTimeout;
    const finishedTrace = new Promise((resolve, reject) => {
        traceComplete = resolve;
        traceTimeout = setTimeout(() => reject(new Error('WebView tracing did not finish.')), 15_000);
    });
    try { await Promise.all([cdp.send('Tracing.end'), finishedTrace]); }
    finally { clearTimeout(traceTimeout); }
    await cdp.detach();
    await writeFile(join(output, `${base}-webview.json`), JSON.stringify({ traceEvents: trace }));

    const searchButton = page.getByRole('button', { name: 'Open search', exact: true });
    const searchHeading = page.getByRole('heading', { name: 'Find your next listen.', exact: true });
    const field = page.getByRole('searchbox', { name: 'Search your indexed library' });
    await searchButton.click();
    await searchHeading.waitFor();
    const first = await captureSearch(page, field, 'performance song 09871');
    const warm = await captureSearch(page, field, 'performance song 07654');

    const pid = await shell(`pidof ${app}`);
    if (!/^\d+$/.test(pid)) throw new Error(`Unexpected fixture process ID: ${pid}`);
    const log = await shell(`logcat -d --pid=${pid} -v epoch -s ChannelWavePerf:I`);
    await writeFile(join(output, `${base}-managed.txt`), log);
    if (withMetadata && !log.includes('Refresh after background changes')) {
        throw new Error('The metadata trial did not observe a background refresh.');
    }
    return { trial, withMetadata, frameSamples, first, warm };
}

try {
    await mkdir(output, { recursive: true });
    const component = (await shell(`cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER ${app}`)).split(/\r?\n/).at(-1);
    if (!/^dev\.channelwave\.app\.performance\/[\w.$]+$/.test(component)) throw new Error(`Install the fixture APK first: ${component}`);
    const samples = [];
    for (const withMetadata of [false, true]) {
        const page = await startFixture(component, withMetadata);
        for (let trial = 1; trial <= runs; trial++) {
            const sample = await runTrial(page, withMetadata, trial);
            samples.push(sample);
            await writeFile(join(output, 'samples.json'), JSON.stringify(samples, null, 2));
            console.log(JSON.stringify(sample));
        }
    }
} finally {
    await shell(`am force-stop ${app}`);
    await Promise.all(devices.map(candidate => candidate.close()));
}
