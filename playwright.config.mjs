import { defineConfig } from '@playwright/test';
const browser = process.env.CHANNELWAVE_BROWSER || 'msedge';
const origin = `http://127.0.0.1:${process.env.CHANNELWAVE_TEST_PORT || '5188'}`;
export default defineConfig({
    testDir: './tests/browser', workers: 1, timeout: 60000,
    retries: 0,
    maxFailures: process.env.CI ? 1 : undefined,
    testIgnore: [
        ...(process.env.CHANNELWAVE_VERIFY_TDWEB === '1' ? [] : ['**/tdlib.spec.mjs']),
        ...(browser === 'webkit' ? [] : ['**/apple-ui.spec.mjs'])
    ],
    use: { baseURL: origin, browserName: browser === 'webkit' ? 'webkit' : 'chromium',
        channel: ['msedge', 'chrome'].includes(browser) ? browser : undefined, headless: true,
        trace: 'retain-on-failure', screenshot: 'only-on-failure', video: 'retain-on-failure' },
    webServer: process.env.CHANNELWAVE_TEST_EXTERNAL_SERVER === '1' ? undefined : { command: `dotnet run --project src/ChannelWave.Host.Web -c ${process.env.CHANNELWAVE_CONFIGURATION || 'Debug'} --no-build --no-launch-profile --urls ${origin}`, url: origin, reuseExistingServer: false, timeout: 60000 },
    reporter: [['list'], ['html', { open: 'never' }], ['junit', { outputFile: 'test-results/browser.xml' }]]
});
