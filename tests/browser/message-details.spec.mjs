import { openRoot } from './fixtures/telegram.mjs';
import { test, expect } from '@playwright/test';
import { installTelegramFixture, openLibrary, clickTrackAction, connectFixture, addFixtureSource } from './fixtures/telegram.mjs';

const modulePath = '/_content/ChannelWave.Platform.Browser/channelwave.js';
const stored = page => page.evaluate(async path => JSON.parse(await (await import(path)).searchLibrary(700, '', 0, 100)), modulePath);

test.afterEach(async ({ page }, info) => {
    if (info.status === info.expectedStatus) return;
    await info.attach('synthetic-message-state', { contentType: 'application/json', body: JSON.stringify({
        tracks: await stored(page).catch(() => []),
        requests: await page.evaluate(() => ({ links: globalThis.messageLinkRequests, senders: globalThis.senderRequests })).catch(() => null)
    }, null, 2) });
});

async function setup(page, { kind = 'group', isPublic = false, canLink = true, width = 1440 } = {}) {
    await page.setViewportSize({ width, height: 900 });
    await installTelegramFixture(page);
    await page.addInitScript(({ kind, isPublic, canLink }) => {
        const originalSend = tdweb.default.prototype.send;
        globalThis.messageLinkRequests = [];
        globalThis.senderRequests = [];
        globalThis.fixtureMessageFailure = 0;
        globalThis.fixtureMessageHeld = false;
        const decorate = (message, id = 1048576) => {
            message.id = id; message.chat_id = -100123;
            message.is_channel_post = kind === 'signed' || kind === 'unsigned';
            message.sender_id = kind === 'group' ? { '@type': 'messageSenderUser', user_id: 42 }
                : { '@type': 'messageSenderChat', chat_id: -100123 };
            message.author_signature = kind === 'signed' ? (id === 1048576 ? 'Mina · Guest curator' : 'Admin B') : '';
            message.edit_date = 1800000000;
            message.forward_info = { date: 1600000000, origin: { '@type': 'messageOriginUser', sender_user_id: 999 } };
            if (kind === 'missing') {
                delete message.date; delete message.is_channel_post; delete message.sender_id; delete message.author_signature;
            }
            return message;
        };
        tdweb.default.prototype.send = async function (query) {
            const type = query['@type'];
            if (type === 'getUser') {
                senderRequests.push(query.user_id);
                return { '@type': 'user', id: query.user_id, first_name: 'سارا', last_name: 'Ahmadi' };
            }
            if (type === 'getMessageProperties') return { '@type': 'messageProperties', can_get_link: canLink };
            if (type === 'getMessageLink') {
                messageLinkRequests.push(query);
                return { '@type': 'messageLink', is_public: isPublic,
                    link: isPublic ? 'https://t.me/channelwave_fixture/123?single' : 'https://t.me/c/1234567890/123?single' };
            }
            if (type === 'getMessage') {
                if (fixtureMessageHeld) await new Promise(resolve => { globalThis.releaseMessageDetails = resolve; });
                if (fixtureMessageFailure) throw { code: fixtureMessageFailure, message: 'Synthetic message failure' };
            }
            const result = await originalSend.call(this, query);
            if (type === 'getChat' && kind === 'group') result.type.is_channel = false;
            if (type === 'getMessage') return decorate(result, query.message_id);
            if (type === 'searchChatMessages') {
                result.next_from_message_id = 0;
                result.messages = kind === 'signed'
                    ? [decorate(result.messages[0]), decorate(result.messages[1], 2097152)]
                    : [decorate(result.messages[0])];
            }
            return result;
        };
    }, { kind, isPublic, canLink });
    await page.goto('/'); await connectFixture(page);
    await addFixtureSource(page);
    await openLibrary(page);
    await expect(page.locator('.track')).toHaveCount(kind === 'signed' ? 2 : 1);
}

async function details(page, index = 0) {
    await clickTrackAction(page.locator('.track').nth(index), 'Edit details');
    const dialog = page.getByRole('dialog', { name: 'Edit details', exact: true });
    const region = dialog.getByRole('region', { name: 'Telegram message', exact: true });
    await expect(region).toBeVisible();
    await expect(region).toHaveAttribute('aria-busy', 'false');
    return { dialog, region };
}

for (const width of [1440, 390]) {
    test(`group attribution, posting date and private message action render consistently at ${width}px`, async ({ page }) => {
        await setup(page, { width });
        const before = (await stored(page))[0];
        expect(before.origin.message.senderName).toBe('سارا Ahmadi');
        expect(before.publishedAt).toMatch(/^2023-11-14T22:13:20/);
        const { dialog, region } = await details(page);
        await expect(region).toContainText('Sender');
        await expect(region).toContainText('سارا Ahmadi');
        await expect(region.locator('time')).toHaveAttribute('datetime', /^2023-11-14T22:13:20/);
        await expect(region).toContainText('Requires access to this Telegram chat.');
        const link = region.getByRole('link', { name: 'Open in Telegram' });
        await expect(link).toHaveAttribute('href', 'https://t.me/c/1234567890/123?single');
        await expect(link).toHaveAttribute('target', '_blank');
        await expect(link).toHaveAttribute('rel', /noopener/);
        const request = await page.evaluate(() => messageLinkRequests[0]);
        expect(request).toMatchObject({ chat_id: -100123, message_id: 1048576, for_album: false, in_message_thread: false,
            media_timestamp: 0, checklist_task_id: 0, poll_option_id: '' });
        expect(await page.evaluate(() => senderRequests)).toEqual([42, 42]);
        expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await dialog.screenshot({ path: `test-results/message-details-${width}.png` });
    });
}

test('signed channel posts retain different authors and unsigned posts retain a date and working link', async ({ page }) => {
    await setup(page, { kind: 'signed', isPublic: true });
    expect((await stored(page)).map(t => t.origin.message.authorSignature).sort()).toEqual(['Admin B', 'Mina · Guest curator']);
    const { dialog, region } = await details(page);
    await expect(region).toContainText('Author');
    await expect(region).toContainText('Mina · Guest curator');
    await dialog.getByRole('button', { name: 'Close details' }).click();
    const second = await details(page, 1);
    await expect(second.region).toContainText('Admin B');
    await expect(second.region.getByRole('link', { name: 'Open in Telegram' })).toHaveAttribute('href', 'https://t.me/channelwave_fixture/123?single');
    await expect(second.region).not.toContainText('Requires access');
    expect(await page.evaluate(() => senderRequests)).toEqual([]);
});

test('unsigned channel author stays unknown while its posting date and public link remain available', async ({ page }) => {
    await setup(page, { kind: 'unsigned', isPublic: true, width: 390 });
    const { region } = await details(page);
    await expect(region).toContainText('Author');
    await expect(region).toContainText('Not provided by Telegram');
    await expect(region.locator('time')).toBeVisible();
    const link = region.getByRole('link', { name: 'Open in Telegram' });
    await expect(link).toHaveAttribute('href', 'https://t.me/channelwave_fixture/123?single');
    await expect(region).not.toContainText('Requires access');
    await page.context().route('https://t.me/**', route => route.fulfill({ contentType: 'text/html', body: '<title>Synthetic Telegram message</title>' }));
    const popupPromise = page.waitForEvent('popup');
    await link.click();
    const popup = await popupPromise;
    await expect(popup).toHaveURL('https://t.me/channelwave_fixture/123?single');
    expect(await popup.evaluate(() => window.opener)).toBeNull();
    await popup.close();
});

test('missing metadata shows explicit fallbacks and unavailable message links stay disabled', async ({ page }) => {
    await setup(page, { kind: 'missing', canLink: false });
    const { region } = await details(page);
    await expect(region).toContainText('Unknown sender');
    await expect(region).toContainText('Unknown posting date');
    await expect(region.getByRole('button', { name: 'Open in Telegram' })).toBeDisabled();
    await expect(region).toContainText('Message link unavailable.');
    expect(await page.evaluate(() => messageLinkRequests)).toEqual([]);
});

test('legacy tracks enrich on details and IndexedDB retains data through local corrections, reindexing and reopening', async ({ page }) => {
    await setup(page, { kind: 'group' });
    await page.evaluate(async path => {
        const lib = await import(path), track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        // Simulate a row written by the previous app version, before attribution existed.
        await new Promise((resolve, reject) => {
            const request = indexedDB.open('channelwave-library');
            request.onerror = () => reject(request.error);
            request.onsuccess = () => {
                const db = request.result, tx = db.transaction('tracks', 'readwrite');
                delete track.origin.message; tx.objectStore('tracks').put(track);
                tx.oncomplete = () => { db.close(); resolve(); }; tx.onerror = () => reject(tx.error);
            };
        });
        await lib.favorite(700, track.id, true); await lib.setLocalMetadata(700, track.id, 'My title', null);
    }, modulePath);
    await page.reload();
    expect((await stored(page))[0].origin.message).toBeUndefined();
    await openRoot(page, 'Profile');
    await connectFixture(page);
    await openLibrary(page); await expect(page.locator('.track')).toHaveCount(1);
    const { dialog, region } = await details(page);
    await expect(region).toContainText('سارا Ahmadi');
    await dialog.getByRole('button', { name: 'Close details' }).click();
    const enriched = (await stored(page))[0];
    expect(enriched.title).toBe('My title'); expect(enriched.favorite).toBe(true);
    const result = await page.evaluate(async path => {
        const lib = await import(path), track = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        const source = JSON.parse(await lib.readSources(700))[0];
        await lib.savePage(JSON.stringify(source), JSON.stringify([{ ...track, id: crypto.randomUUID(),
            origin: { ...track.origin, message: null }, publishedAt: '1970-01-01T00:00:00+00:00' }]));
        const preserved = JSON.parse(await lib.searchLibrary(700, '', 0, 100))[0];
        const info = { isChannelPost: true, senderType: 'chat', senderId: track.origin.chatId, authorSignature: null };
        const unsigned = JSON.parse(await lib.setMessageInfo(700, track.id, track.origin.chatId, track.origin.messageId,
            JSON.stringify(info), '1970-01-01T00:00:00+00:00'));
        const other = await lib.setMessageInfo(701, track.id, track.origin.chatId, track.origin.messageId, JSON.stringify(info), track.publishedAt);
        const wrong = await lib.setMessageInfo(700, track.id, track.origin.chatId, 99, JSON.stringify(info), track.publishedAt);
        await lib.removeSource(700, track.origin.chatId);
        const removed = await lib.setMessageInfo(700, track.id, track.origin.chatId, track.origin.messageId, JSON.stringify(info), track.publishedAt);
        return { preserved, unsigned, other, wrong, removed };
    }, modulePath);
    expect(result.preserved.id).toBe(enriched.id);
    expect(result.preserved.origin.message).toEqual(enriched.origin.message);
    expect(result.preserved.publishedAt).toBe(enriched.publishedAt);
    expect(result.preserved.favorite).toBe(true); expect(result.preserved.title).toBe('My title');
    expect(result.unsigned.origin.message.authorSignature).toBeNull();
    expect(result.unsigned.origin.message.link).toEqual(enriched.origin.message.link);
    expect(result.other).toBeNull(); expect(result.wrong).toBeNull(); expect(result.removed).toBeNull();
});

test('cached attribution and links survive temporary failures and access loss disables opening', async ({ page }) => {
    await setup(page);
    let { dialog, region } = await details(page);
    await expect(region.getByRole('link', { name: 'Open in Telegram' })).toBeVisible();
    await dialog.getByRole('button', { name: 'Close details' }).click();
    await page.evaluate(() => { fixtureMessageFailure = 503; });
    ({ dialog, region } = await details(page));
    await expect(region).toContainText('سارا Ahmadi');
    await expect(region.getByRole('link', { name: 'Open in Telegram' })).toBeVisible();
    await expect(region).toContainText('Cached details are shown');
    await dialog.getByRole('button', { name: 'Close details' }).click();
    await page.evaluate(() => { fixtureMessageFailure = 403; });
    ({ region } = await details(page));
    await expect(region).toContainText('سارا Ahmadi');
    await expect(region.getByRole('button', { name: 'Open in Telegram' })).toBeDisabled();
    await expect(region).toContainText('no longer has access');
    expect((await stored(page))[0].origin.message.link).toBeNull();
});

test('details enrichment updates the offline snapshot and stays available after an offline reload', async ({ page, context }) => {
    await setup(page);
    await clickTrackAction(page.locator('.track'), 'Save offline');
    await expect(page.getByRole('button', { name: 'Cancel download' })).toBeVisible();
    await page.evaluate(() => completeFixtureDownload());
    await expect(page.locator('.music-saved')).toHaveCount(1);
    const { dialog, region } = await details(page);
    await expect(region.getByRole('link', { name: 'Open in Telegram' })).toBeVisible();
    await dialog.getByRole('button', { name: 'Close details' }).click();
    const offline = await page.evaluate(async path => JSON.parse(await (await import(path)).offlineTracks(700))[0], modulePath);
    expect(offline.origin.message.senderName).toBe('سارا Ahmadi');
    expect(offline.origin.message.link.url).toBe('https://t.me/c/1234567890/123?single');
    await context.setOffline(true); await page.reload();
    await expect(page.getByRole('heading', { name: 'Offline', exact: true })).toBeVisible();
    const cached = await details(page);
    await expect(cached.region).toContainText('سارا Ahmadi');
    await expect(cached.region.locator('time')).toBeVisible();
    await expect(cached.region.getByRole('link', { name: 'Open in Telegram' })).toBeVisible();
    expect(await page.evaluate(() => fixtureCalls)).toEqual([]);
});
