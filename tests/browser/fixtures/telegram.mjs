import fs from 'node:fs/promises';
import { expect } from '@playwright/test';
export async function installTelegramFixture(page, playback = 'normal', { automaticMetadata = false } = {}) {
    // Fixture-only client drives the actual C# indexing/serialization and IndexedDB adapter.
    // It never signs into Telegram or reads the developer configuration.
    await page.route('**/channelwave.local.json', route => route.fulfill({ json: { apiId: 1, apiHash: '0'.repeat(32) } }));
    await page.route('**/tdweb/channelwave-build.json', route => route.fulfill({ json: { revision: 'c15d3f5a5de6e3ba5839822c451152e5e18bb700', wrapperPatch: 2 } }));
    // Existing focused playback/artwork fixtures keep enrichment explicitly paused.
    // Metadata tests opt into the production default and exercise its network work.
    if (!automaticMetadata) await page.addInitScript(() => {
        const open = indexedDB.open.bind(indexedDB);
        indexedDB.open = (...args) => {
            const request = open(...args);
            if (args[0] === 'channelwave-library') request.addEventListener('success', () => {
                const db = request.result;
                if (!db.objectStoreNames.contains('state')) return;
                const tx = db.transaction('state', 'readwrite'), store = tx.objectStore('state');
                const saved = store.get([700, 'automatic-metadata-v1']);
                saved.onsuccess = () => { if (!saved.result) store.put({ account: 700, key: 'automatic-metadata-v1', value: 'false' }); };
            });
            return request;
        };
    });
    const encoded = ['progressive', 'tags'].includes(playback) ? (await fs.readFile(new URL('./stream-tone.mp3', import.meta.url))).toString('base64') : null;
    await page.addInitScript(({ playback, encoded }) => {
        const syncFixture = playback.startsWith('sync');
        const mixedChats = {
            '-100123': { title: 'Public channel', type: { '@type': 'chatTypeSupergroup', supergroup_id: 123, is_channel: true } },
            '-100124': { title: 'Private channel', type: { '@type': 'chatTypeSupergroup', supergroup_id: 124, is_channel: true } },
            '-123': { title: 'Listening group', type: { '@type': 'chatTypeBasicGroup', basic_group_id: 123 } },
            '-100127': { title: 'Music supergroup', type: { '@type': 'chatTypeSupergroup', supergroup_id: 127, is_channel: false } },
            701: { title: 'Private conversation', type: { '@type': 'chatTypePrivate', user_id: 701 } },
            702: { title: 'Music bot', type: { '@type': 'chatTypePrivate', user_id: 702 } },
            700: { title: 'Saved Messages', type: { '@type': 'chatTypePrivate', user_id: 700 } },
            703: { title: 'Secret conversation', type: { '@type': 'chatTypeSecret', secret_chat_id: 1, user_id: 701 } },
            704: { title: 'Unknown chat type', type: { '@type': 'chatTypeFuture' } }
        };
        let buffer = new ArrayBuffer(44 + 40000 * 2); const data = new DataView(buffer);
        const text = (offset, value) => [...value].forEach((v, i) => data.setUint8(offset + i, v.charCodeAt(0)));
        text(0, 'RIFF'); data.setUint32(4, buffer.byteLength - 8, true); text(8, 'WAVE'); text(12, 'fmt '); data.setUint32(16, 16, true);
        data.setUint16(20, 1, true); data.setUint16(22, 1, true); data.setUint32(24, 8000, true); data.setUint32(28, 16000, true);
        data.setUint16(32, 2, true); data.setUint16(34, 16, true); text(36, 'data'); data.setUint32(40, 80000, true);
        for (let i = 0; i < 40000; i++) data.setInt16(44 + i * 2, Math.sin(i * 2 * Math.PI * 440 / 8000) * 1500, true);
        if (encoded) buffer = Uint8Array.from(atob(encoded), c => c.charCodeAt(0)).buffer;
        const mime = encoded ? 'audio/mpeg' : 'audio/wav';
        const file = complete => ({ '@type': 'file', id: 1, size: buffer.byteLength, expected_size: buffer.byteLength,
            local: { is_downloading_completed: complete, is_downloading_active: !complete, downloaded_size: complete ? buffer.byteLength : 0 },
            remote: { id: 'fixture', unique_id: 'fixture-unique' } });
        const message = () => ({ '@type': 'message', id: 1, date: 1700000000, can_be_saved: playback !== 'protected',
            content: { '@type': 'messageAudio', audio: { title: 'Café 🎵', performer: 'كِي آهنگ\u200cمن', duration: encoded ? 30 : 5,
                file_name: encoded ? 'fixture.mp3' : 'fixture.wav', mime_type: mime, audio: file(false),
                ...(globalThis.fixtureArtwork ? { album_cover_thumbnail: { file: { ...file(true), id: 9, size: fixtureArtwork.length } } } : {}) } } });
        const realPlay = HTMLMediaElement.prototype.play;
        let denied = false;
        HTMLMediaElement.prototype.play = function () {
            globalThis.fixtureAudio = this;
            if (playback === 'denied' && !denied) { denied = true; return Promise.reject(new DOMException('Fixture autoplay denial', 'NotAllowedError')); }
            return realPlay.call(this);
        };
        globalThis.fixtureCalls = [];
        globalThis.tdweb = { default: class FixtureClient {
            constructor(options) { this.options = options; globalThis.fixtureClient = this; }
            authorize(state) { this.options.onUpdate({ '@type': 'updateAuthorizationState', authorization_state: state }); return state; }
            async send(query) {
                globalThis.fixtureCalls.push(query['@type']);
                await globalThis.fixtureWaitForOperation?.(`telegram:${query['@type']}`, [query.chat_id, query.message_id], 'before');
                switch (query['@type']) {
                    case 'getOption': return { '@type': 'optionValueString', value: query.name === 'version' ? '1.8.68' : 'c15d3f5a5de6e3ba5839822c451152e5e18bb700' };
                    case 'getAuthorizationState':
                        if (playback === 'auth') return this.authorize({ '@type': 'authorizationStateWaitPhoneNumber' });
                        this.options.onUpdate({ '@type': 'updateAuthorizationState', authorization_state: { '@type': 'authorizationStateReady' } });
                        return { '@type': 'authorizationStateReady' };
                    case 'setAuthenticationPhoneNumber': return this.authorize({ '@type': 'authorizationStateWaitCode', code_info: { type: { '@type': 'authenticationCodeTypeTelegramMessage' }, timeout: 0 } });
                    case 'checkAuthenticationCode': return this.authorize({ '@type': 'authorizationStateWaitPassword' });
                    case 'checkAuthenticationPassword': return this.authorize({ '@type': 'authorizationStateReady' });
                    case 'getMe': return { '@type': 'user', id: globalThis.fixtureAccountId ?? 700 };
                    case 'getChats':
                        if (globalThis.fixtureHoldDiscovery) await new Promise(resolve => { globalThis.releaseFixtureDiscovery = resolve; });
                        if (globalThis.fixtureDiscoveryFailure) throw { code: 500, message: 'Synthetic discovery failure' };
                        if (playback === 'sync-mixed') return { '@type': 'chats', chat_ids: query.chat_list['@type'] === 'chatListMain'
                            ? [-100123, -123, 701, 702, 700, 703] : [-100124, -100127, 701, 704] };
                        return { '@type': 'chats', chat_ids: playback === 'picker' || syncFixture ? [-100123, -100124, -100125, -100126, -100123] : [-100123] };
                    case 'loadChats': throw { code: 404, message: 'End of synthetic chat list' };
                    case 'getChat': return playback === 'sync-mixed' ? { '@type': 'chat', id: query.chat_id, ...mixedChats[query.chat_id] }
                        : { '@type': 'chat', id: query.chat_id, title: ({ '-100123': ':)', '-100124': 'Café Sessions', '-100125': 'آهنگ‌های من 🎵', '-100126': 'A very long channel title with live recordings and late night listening sessions from around the world' })[query.chat_id], type: { '@type': 'chatTypeSupergroup', is_channel: true } };
                    case 'searchChatMessages':
                        if (globalThis.fixtureMessages) {
                            const messages = Object.values(fixtureMessages).filter(m => m && m.content['@type'] === 'messageAudio' && (!query.from_message_id || m.id < query.from_message_id)).sort((a, b) => b.id - a.id);
                            return { '@type': 'foundChatMessages', next_from_message_id: messages.length > 100 ? messages[99].id : 0, messages: messages.slice(0, 100) };
                        }
                        if (syncFixture) {
                            globalThis.fixturePages ??= [];
                            globalThis.fixturePages.push({ chat: query.chat_id, cursor: query.from_message_id });
                            if (playback === 'sync-failure' && query.chat_id === -100125) throw { code: 403, message: 'Synthetic inaccessible channel' };
                            if (globalThis.fixtureRateLimit) { globalThis.fixtureRateLimit = false; throw { code: 429, message: 'Too Many Requests: retry after 1' }; }
                        }
                        if (globalThis.fixtureIndexFailure) throw { code: 500, message: 'Synthetic indexing failure' };
                        if (globalThis.fixtureHoldIndex) await new Promise(resolve => { globalThis.releaseFixtureIndex = resolve; (globalThis.releaseFixtureIndexes ??= []).push(resolve); });
                        if (playback === 'sync-mixed' && query.chat_id === 702) return { '@type': 'foundChatMessages', next_from_message_id: 0, messages: [] };
                        return { '@type': 'foundChatMessages', next_from_message_id: (playback === 'picker' || syncFixture) && !query.from_message_id ? 10 : 0, messages: [message(), message()] };
                    case 'getMessage':
                        if (globalThis.fixtureMessages) {
                            if (!fixtureMessages[query.message_id]) throw { code: 404, message: 'Message not found' };
                            return fixtureMessages[query.message_id];
                        }
                        return message();
                    case 'getMessages': return { '@type': 'messages', total_count: query.message_ids.length,
                        messages: query.message_ids.map(id => globalThis.fixtureMessages ? fixtureMessages[id] ?? null : ({ ...message(), id, chat_id: query.chat_id })) };
                    case 'openChat': case 'closeChat': return { '@type': 'ok' };
                    case 'getMessageProperties': return { '@type': 'messageProperties', can_get_link: false };
                    case 'getFileDownloadedPrefixSize': return { '@type': 'fileDownloadedPrefixSize', size: Math.max(0, buffer.byteLength - query.offset) };
                    case 'downloadFile':
                        if (playback === 'failed') throw { code: 403, message: 'Synthetic download failure' };
                        if (globalThis.fixtureStreamFailure) { globalThis.fixtureStreamFailure = false; throw { code: 500, message: 'Synthetic stream transfer failure' }; }
                        globalThis.fixtureDownloads ??= [];
                        globalThis.fixtureDownloads.push({ offset: query.offset, limit: query.limit, synchronous: query.synchronous });
                        if (query.synchronous) {
                            if (globalThis.fixtureHoldFirstStream && !query.offset) await new Promise(resolve => { globalThis.releaseFixtureFirstStream = resolve; });
                            if (globalThis.fixtureHoldStream && query.offset > 0) await new Promise(resolve => { globalThis.releaseFixtureStream = resolve; });
                            return file(true);
                        }
                        globalThis.completeFixtureDownload = () => this.options.onUpdate({ '@type': 'updateFile', file: file(true) });
                        return file(false);
                    case 'readFile': return { '@type': 'filePart', data: query.file_id === 9 && globalThis.fixtureArtwork
                        ? new Blob([fixtureArtwork], { type: 'image/png' })
                        : new Blob([buffer], { type: mime }).slice(query.offset ?? 0, query.count ? (query.offset ?? 0) + query.count : buffer.byteLength) };
                    case 'cancelDownloadFile': return { '@type': 'ok' };
                    case 'deleteFile':
                        (globalThis.fixtureDeletedFiles ??= []).push(query.file_id);
                        return { '@type': 'ok' };
                    case 'logOut':
                        this.options.onUpdate({ '@type': 'updateAuthorizationState', authorization_state: { '@type': 'authorizationStateClosed' } });
                        return { '@type': 'ok' };
                    default: throw new Error(`Unexpected fixture request: ${query['@type']}`);
                }
            }
        } };
    }, { playback, encoded });
}


export async function openLibrary(page) {
  await openRoot(page, 'Library');
  await page.getByRole('button', { name: /^Tracks \d/ }).click();
}
export async function setConnectionState(page, state) {
    await page.evaluate(state => fixtureClient.options.onUpdate({
        '@type': 'updateConnectionState', state: { '@type': `connectionState${state}` }
    }), state);
}

export async function observeActivityStatus(page) {
    await page.evaluate(() => {
        globalThis.fixtureActivityChanges = [];
        const status = document.querySelector('.telegram-activity');
        new MutationObserver(() => fixtureActivityChanges.push({ text: status.textContent.trim(), at: performance.now() }))
            .observe(status, { childList: true, subtree: true, characterData: true });
    });
}

export async function openRoot(page, name) {
  const nav = page.getByRole('navigation', { name: page.viewportSize().width > 760 ? 'Main navigation' : 'Mobile navigation' });
  await expect(async () => {
    if (!await nav.isVisible()) await page.getByRole('button', { name: 'Go back', exact: true }).click({ timeout: 1500 });
    else await nav.getByRole('button', { name, exact: true }).click({ timeout: 1500 });
    await expect(nav.getByRole('button', { name, exact: true })).toHaveAttribute('aria-current', 'page', { timeout: 1500 });
    await expect(page.locator('.app-shell')).toHaveClass(/root-page/, { timeout: 1500 });
  }).toPass({ timeout: 20000 });
}
export async function connectFixture(page) {
    await page.evaluate(() => window.channelwaveOfflineReady);
    // A cold shell installation can briefly hold input while activating its worker.
    await expect(async () => {
        const connect = page.getByRole('button', { name: 'Connect account', exact: true });
        if (await connect.isVisible()) await connect.click();
        await expect(page.locator('.sidebar-bottom')).toContainText('Connected to Telegram', { timeout: 1500 });
    }).toPass({ timeout: 20000 });
}
export async function addFixtureSource(page, name = ':)') {
    // Account/discovery renders can briefly leave a stale enabled action while
    // startup still owns input. Wait for the persisted selection before navigating.
    await expect(async () => {
        const add = page.getByRole('button', { name: `Add to library: ${name}`, exact: true });
        if (await add.isVisible() && await add.isEnabled()) await add.click();
        await expect(page.getByRole('button', { name: `Remove from library: ${name}`, exact: true })).toBeEnabled({ timeout: 1500 });
    }).toPass({ timeout: 20000 });
}
export async function openChannels(page) {
  await openRoot(page, 'Library');
  await page.getByRole('button', { name: /^Sources \d/ }).click();
}
export async function openCategory(page, name) {
  await openRoot(page, 'Library');
  await page.locator('.library-tiles').first().getByRole('button', { name: new RegExp('^' + name + '( |$)') }).click();
}

// Exercise the visible responsive UI rather than hidden desktop controls.
export async function clickTrackAction(scope, name) {
    const action = scope.getByRole('button', { name, exact: true });
    const more = scope.getByRole('button', { name: 'More actions', exact: true }).first();
    await more.waitFor({ state: 'visible' });
    if (!await action.isVisible()) await more.click();
    await action.click();
    if (name === 'Download' || name === 'Save offline')
        await expect.poll(() => (typeof scope.page === 'function' ? scope.page() : scope).evaluate(() =>
            typeof globalThis.completeFixtureDownload === 'function' || !!document.querySelector('.music-saved,.music-error[role="alert"]')
            || [...document.querySelectorAll('.music-feedback,.music-transfer [role="status"]')].some(el => /Your file is ready|Waiting for playback/.test(el.textContent)))).toBe(true);
}
// A prepared export deliberately waits for a second, explicit save action.
export async function savePreparedExport(page, { returnToLibrary = true } = {}) {
    await openDownloads(page);
    await expect(page.getByRole('button', { name: 'Save file', exact: true })).toBeVisible();
    await page.evaluate(() => {
        globalThis.fixtureAutoCompleteExports = true;
        if (globalThis.fixtureExportCompletionWrapper) return;
        globalThis.fixtureExportCompletionWrapper = true;
        const send = tdweb.default.prototype.send;
        tdweb.default.prototype.send = async function (query) {
            const response = await send.call(this, query);
            if (query['@type'] === 'downloadFile' && !query.synchronous && globalThis.fixtureAutoCompleteExports) {
                globalThis.fixtureAutoCompleteExports = false;
                setTimeout(() => globalThis.completeFixtureDownload?.(), 0);
            }
            return response;
        };
    });
    const completed = page.getByRole('button', { name: /^Completed/ });
    const count = Number((await completed.innerText()).match(/\d+/)?.[0] ?? 0);
    await page.getByRole('button', { name: 'Save file', exact: true }).click();
    await expect(completed).toContainText(String(count + 1));
    await page.evaluate(() => globalThis.fixtureAutoCompleteExports = false);
    if (returnToLibrary) await openLibrary(page);
}

export async function openDownloads(page) {
    await openRoot(page, 'Settings');
    await page.getByRole('button', { name: 'Offline downloads', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Downloads', exact: true })).toBeVisible();
}
export async function selectChannel(scope, value) {
    await scope.getByLabel('Source', { exact: true }).click();
    await scope.getByRole('dialog', { name: 'Choose a source', exact: true }).locator(`input[type=radio][value="${value}"]`).check();
}

// Phone transport keeps Pause/Resume in the dock and Stop in the expanded menu.
export async function stopPlayback(page) {
    const player = page.getByRole('region', { name: 'Now playing', exact: true });
    const stop = player.getByRole('button', { name: 'Stop playback', exact: true });
    if (await stop.isVisible()) { await stop.click(); return; }
    await player.getByRole('button', { name: 'Open full-screen player', exact: true }).click();
    const expanded = page.getByRole('dialog', { name: 'Music player', exact: true });
    await expanded.getByRole('button', { name: 'More actions', exact: true }).click();
    await page.getByRole('dialog', { name: 'Song actions', exact: true }).getByRole('button', { name: 'Stop playback', exact: true }).click();
}
