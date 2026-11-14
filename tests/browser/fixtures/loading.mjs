// Test-only gates around the real browser adapter. Shipping code has no test hooks.
export async function installLoadingGates(page, initialHolds = []) {
    await page.addInitScript(initial => {
        const holds = initial;
        globalThis.fixtureSearchRequests = [];
        globalThis.fixtureSearchTimings = [];
        globalThis.fixturePageRequests = [];
        globalThis.fixtureSessionProbes = 0;
        globalThis.fixtureHoldOperation = (name, query = null, stage = 'before', pageOffset = null) => holds.push({ name, query, stage, pageOffset });
        globalThis.fixtureOperationPending = name => holds.filter(h => h.name === name && h.release).length;
        globalThis.fixtureReleaseOperation = (name, fail = false) => {
            const index = holds.findIndex(h => h.name === name && h.release);
            if (index < 0) throw new Error(`No pending ${name}`);
            const [hold] = holds.splice(index, 1);
            fail ? hold.reject(new Error('Synthetic loading failure. Try again.')) : hold.release();
        };
        globalThis.fixtureWaitForOperation = async (name, args, stage) => {
            if (name === 'searchLibrary' && stage === 'before') fixtureSearchRequests.push(args[1]);
            if (name === 'searchLibrary') fixtureSearchTimings.push({ stage, query: args[1], at: performance.now() });
            const hold = holds.find(h => h.name === name && h.stage === stage && !h.release && (h.query === null || args[1] === h.query)
                && (h.pageOffset === null || h.pageOffset === undefined || args[2] === h.pageOffset));
            if (hold) await new Promise((resolve, reject) => { hold.release = resolve; hold.reject = reject; });
        };
    }, initialHolds);
    await page.route('**/_content/ChannelWave.Platform.Browser/channelwave.js', async route => {
        const response = await route.fetch();
        let body = await response.text();
        for (const name of ['readState', 'readSources', 'readSource', 'searchLibrary', 'searchLibraryAdvanced', 'offlineTracks', 'favorite', 'loadAudio', 'clearAccount', 'readArtworkFile', 'hasSavedTelegramSession']) {
            const operation = name === 'searchLibraryAdvanced' ? 'searchLibrary' : name;
            body = body.replace(`export async function ${name}(`, `async function loadingOriginal_${name}(`);
            body += `\nexport async function ${name}(...args) {
                if ('${name}' === 'searchLibraryAdvanced') globalThis.fixturePageRequests.push({ account: args[0], ...JSON.parse(args[1]) });
                const gateArgs = '${name}' === 'searchLibraryAdvanced' ? [args[0], JSON.parse(args[1]).criteria.text, JSON.parse(args[1]).offset] : args;
                await globalThis.fixtureWaitForOperation('${operation}', gateArgs, 'before');
                const value = await loadingOriginal_${name}(...args);
                await globalThis.fixtureWaitForOperation('${operation}', gateArgs, 'after');
                if ('${name}' === 'hasSavedTelegramSession') globalThis.fixtureSessionProbes++;
                return value;
            }\n`;
        }
        await route.fulfill({ response, body });
    });
}
