let client, clientRef, database, releaseProfile, startup, telegramClosed = false, profileOwnership;
let offlineWriteGeneration = 0;
const canceledTransfers = new Set();
export function cancelTransfer(operation) { canceledTransfers.add(operation); }
export function cancelOfflineSave() { ++offlineWriteGeneration; }
export function cancelExport() { ++exportGeneration; }
let exportGeneration = 0;
export function cancelMediaWork() { ++exportGeneration; cancelOfflineSave(); canceledTransfers.clear(); tagFiles.clear(); }
const tagFiles = new Map();
const sourceMediaVersions = new Map();
function sourceMediaVersion(account, chat) { return sourceMediaVersions.get(`${account}:${chat}`) ?? 0; }
let orderedUpdates = Promise.resolve();
const audio = new Audio();
audio.preload = 'auto';
let currentUrl, cancelAudioLoad;
let pendingLibraryWrites = 0;
window.addEventListener('channelwave-before-update', event => {
    if (cancelAudioLoad || currentUrl && !audio.ended || tagFiles.size > 0 || pendingLibraryWrites > 0)
        event.preventDefault();
});
const streams = new Map();
let streamWorker;
let streamGeneration = 0;
const streamIdleTimeout = 45000;
function audioStream(url) { return streams.get(url?.split('/__channelwave_audio/')[1]); }
function streamFailure(stream, error) {
    stream.error ??= typeof error.message === 'string' ? error.message : 'Telegram could not supply audio. Check your connection and retry Stream.';
    stream.onError?.(new Error(stream.error));
}
async function streamRequest(stream, query) {
    if (stream.cancellation.signal.aborted) throw new Error('This stream has stopped.');
    let timer, abort;
    try {
        return await Promise.race([checked(query), new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error('Telegram is taking too long to send audio. Check your connection and retry Stream.')), streamIdleTimeout);
            abort = () => reject(new Error('This stream has stopped.'));
            stream.cancellation.signal.addEventListener('abort', abort, { once: true });
        })]);
    } finally { clearTimeout(timer); stream.cancellation.signal.removeEventListener('abort', abort); }
}
async function checked(query) {
    const response = await client.send(query);
    if (response['@type'] === 'error') throw new Error(typeof response.message === 'string' ? response.message : 'Telegram audio request failed.');
    return response;
}
navigator.serviceWorker?.addEventListener('message', event => {
    if (event.data?.type !== 'channelwave-audio-range' || !event.ports[0]) return;
    const { token, offset, count } = event.data, port = event.ports[0], stream = streams.get(token);
    (async () => {
        if (!stream || !client) throw new Error('This stream has stopped.');
        if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(count) || offset < 0 || count < 0 || count > 65536 || offset + count > stream.size) throw new Error('Invalid audio range.');
        if (count === 0) { port.postMessage({ total: stream.size, mime: stream.mime }); return; }
        // Serialize ranges for a file: TDLib changes its active download offset per request.
        const task = stream.tail.then(async () => {
            if (!streams.has(token)) throw new Error('This stream has stopped.');
            await streamRequest(stream, { '@type': 'downloadFile', file_id: stream.id, priority: 32, offset, limit: count, synchronous: true });
            if (!streams.has(token)) throw new Error('This stream has stopped.');
            const part = await streamRequest(stream, { '@type': 'readFile', file_id: stream.id, offset, count });
            if (!(part.data instanceof Blob) || part.data.size !== count) throw new Error('Telegram returned an incomplete audio chunk.');
            const bytes = await part.data.arrayBuffer();
            stream.lastProgress = Date.now(); stream.received += bytes.byteLength;
            port.postMessage({ bytes }, [bytes]);
        });
        stream.tail = task.catch(() => {}); await task;
    })().catch(error => {
        if (stream && streams.has(token)) streamFailure(stream, error);
        port.postMessage({ error: stream?.error ?? 'This stream has stopped.' });
    }).finally(() => port.close());
});
async function ensureStreamWorker() {
    if (!navigator.serviceWorker) throw new Error('Streaming requires a browser with service workers over HTTPS or localhost.');
    streamWorker ??= (async () => {
        await navigator.serviceWorker.register('./audio-worker.js', { scope: './', updateViaCache: 'none' });
        await navigator.serviceWorker.ready;
        if (!navigator.serviceWorker.controller) await new Promise((resolve, reject) => {
            const timer = setTimeout(() => { navigator.serviceWorker.removeEventListener('controllerchange', changed); reject(new Error('Reload this page to enable streaming.')); }, 10000);
            function changed() { clearTimeout(timer); navigator.serviceWorker.removeEventListener('controllerchange', changed); resolve(); }
            navigator.serviceWorker.addEventListener('controllerchange', changed);
            if (navigator.serviceWorker.controller) changed();
        });
    })();
    try { await streamWorker; } catch (error) { streamWorker = null; throw error; }
}
export async function streamFile(id, size, mime = 'application/octet-stream') {
    if (!client) throw new Error('Connect Telegram first.');
    if (!Number.isSafeInteger(size) || size <= 0) throw new Error('This recording has no known file size. Use Download instead.');
    const mine = streamGeneration;
    await ensureStreamWorker();
    if (mine !== streamGeneration) throw new Error('Stream canceled.');
    const token = crypto.randomUUID();
    streams.set(token, { id, size, mime: /^audio\/[a-z0-9.+-]+$/i.test(mime) ? mime : 'application/octet-stream', tail: Promise.resolve(),
        cancellation: new AbortController(), received: 0, lastProgress: Date.now() });
    return { location: new URL(`./__channelwave_audio/${token}`, location.href).href, isBrowserUrl: true, bytes: size };
}
function releaseStream(url) {
    const token = url?.split('/__channelwave_audio/')[1], stream = streams.get(token);
    if (stream) {
        streams.delete(token);
        stream.cancellation.abort();
        checked({ '@type': 'cancelDownloadFile', file_id: stream.id, only_if_pending: false }).catch(() => {});
    }
}
export async function exportFile(id, name, size, operation = null) {
    try { return await exportFileCore(id, name, size, operation); }
    finally { if (operation) canceledTransfers.delete(operation); }
}
async function exportFileCore(id, name, size, operation) {
    if (!client) throw new Error('Connect Telegram first.');
    const generation = exportGeneration;
    const part = await checked({ '@type': 'readFile', file_id: id, offset: 0, count: size });
    if (canceledTransfers.has(operation) || generation !== exportGeneration || !client) throw new Error('Download canceled.');
    if (!(part.data instanceof Blob) || part.data.size === 0 || size > 0 && part.data.size !== size)
        throw new Error(`Telegram supplied ${part.data?.size ?? 0} of ${size || 'unknown'} audio bytes. Retry Download after the file finishes saving.`);
    const url = URL.createObjectURL(part.data), anchor = document.createElement('a');
    anchor.href = url; anchor.download = name.replace(/[\\/\u0000-\u001f]/g, '_') || 'audio';
    try {
        if (canceledTransfers.has(operation) || generation !== exportGeneration) throw new Error('Download canceled.');
        document.body.append(anchor); anchor.click();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
    } catch (error) { URL.revokeObjectURL(url); throw error; }
    finally { anchor.remove(); }
}
export async function validateExportFile(id, size) {
    const part = await checked({ '@type': 'readFile', file_id: id, offset: 0, count: size });
    if (!(part.data instanceof Blob) || part.data.size <= 0 || size > 0 && part.data.size !== size)
        throw new Error(`Telegram supplied ${part.data?.size ?? 0} of ${size || 'unknown'} audio bytes. Retry Download after the file finishes saving.`);
}

async function ownProfile() {
    if (releaseProfile) return;
    if (!navigator.locks) throw new Error('This browser cannot safely own the Telegram profile. Use a supported browser with Web Locks over HTTPS.');
    profileOwnership ??= new Promise((resolve, reject) => {
        navigator.locks.request('channelwave-profile-v1', { ifAvailable: true }, async lock => {
            if (!lock) { reject(new Error('ChannelWave is already open in another tab. Close that tab, then reload here.')); return; }
            await new Promise(release => { releaseProfile = release; resolve(); });
        }).catch(reject);
    });
    try { await profileOwnership; } catch (e) { profileOwnership = null; throw e; }
}

export async function hasSavedTelegramSession() {
    // Inspect the existing database list without creating a new Telegram profile.
    // This is the pinned tdweb database filesystem, also erased during sign-out.
    if (typeof indexedDB.databases !== 'function') return false;
    return (await indexedDB.databases()).some(database => database.name === '/channelwave-telegram/dbfs');
}

export async function startTelegram(reference) {
    if (client) return;
    if (startup) return startup;
    startup = (async () => {
        await ownProfile();
        const manifest = await fetch('./tdweb/channelwave-build.json', { cache: 'no-store' });
        if (!manifest.ok) throw new Error('The pinned Telegram browser build is missing. Run scripts/build-tdweb.ps1 and install its assets before connecting.');
        const build = await manifest.json();
        if (build.revision !== 'c15d3f5a5de6e3ba5839822c451152e5e18bb700' || build.wrapperPatch !== 2) throw new Error('Telegram browser assets do not match the pinned schema and security wrapper configuration. Rebuild with scripts/build-tdweb.ps1.');
        if (!globalThis.tdweb) await new Promise((resolve, reject) => {
            const script = document.createElement('script'); script.src = './tdweb/tdweb.js';
            script.onload = resolve; script.onerror = () => reject(new Error('Unable to load TDLib browser assets. Check WASM/worker paths.'));
            document.head.append(script);
        });
        const TdClient = globalThis.tdweb?.default ?? globalThis.tdweb;
        if (typeof TdClient !== 'function') throw new Error('The tdweb bundle does not expose TdClient.');
        clientRef = reference;
        client = new TdClient({ instanceName: 'channelwave-telegram', useDatabase: true, isBackground: false,
            logVerbosityLevel: 0, jsLogVerbosityLevel: 'error', onUpdate: update => {
                if (update['@type'] === 'updateAuthorizationState' && update.authorization_state['@type'] === 'authorizationStateClosed') telegramClosed = true;
                // File bytes stay inside tdweb; only metadata crosses the .NET boundary.
                const json = JSON.stringify(update, (key, value) => key === 'arr' || value instanceof Blob || value instanceof ArrayBuffer ? undefined : value);
                orderedUpdates = orderedUpdates.then(() => clientRef?.invokeMethodAsync('OnUpdate', json)).catch(() => {
                    clearAudio(); clientRef = null;
                    document.dispatchEvent(new CustomEvent('channelwave-fatal', { detail: 'The Telegram update bridge stopped. Reload ChannelWave.' }));
                });
            }});
    })();
    try { await startup; } finally { startup = null; }
}
export async function sendTelegram(json) {
    if (!client) throw new Error('Initialize Telegram first.');
    try {
        const response = await client.send(JSON.parse(json));
        return JSON.stringify(response, (key, value) => key === 'arr' || value instanceof Blob || value instanceof ArrayBuffer ? undefined : value);
    } catch (e) { return JSON.stringify({ '@type': 'error', code: e.code ?? 500, message: typeof e.message === 'string' ? e.message : 'Telegram request failed.' }); }
}
export async function closeTelegram() {
    cancelMediaWork();
    if (client && !telegramClosed) await client.send({ '@type': 'close' });
    // Keep the profile lock until the page is closed. Native TDLib shutdown is asynchronous.
    clientRef = null;
}
export async function clearTelegramStorage() {
    cancelMediaWork();
    if (client && !telegramClosed) throw new Error('Telegram must finish closing before its local storage can be erased.');
    clearAudio();
    // These names and the FileManager handle are verified against the pinned tdweb sources.
    // Its public close API terminates the worker but leaves the main-thread Blob database open.
    if (client?.fileManager?.idb) (await client.fileManager.idb).close();
    for (const name of ['channelwave-telegram', '/channelwave-telegram/dbfs']) {
        await new Promise((resolve, reject) => {
            const request = indexedDB.deleteDatabase(name);
            request.onsuccess = resolve; request.onerror = () => reject(new Error('Telegram storage could not be erased. Clear site data before reconnecting.'));
            request.onblocked = () => reject(new Error('A tab is blocking session deletion. Close other tabs and clear site data.'));
        });
    }
    client = null; clientRef = null;
}
export async function prepareFile(fileId, size) {
    if (!client) throw new Error('Connect Telegram first.');
    const part = await checked({ '@type': 'readFile', file_id: fileId, offset: 0, count: size });
    if (!(part.data instanceof Blob) || part.data.size === 0) throw new Error('Telegram has not supplied a complete readable file.');
    return { location: URL.createObjectURL(part.data), isBrowserUrl: true, bytes: part.data.size };
}
export async function loadAudio(url) {
    if (!url.startsWith('blob:') && ![...streams.keys()].some(token => url === new URL(`./__channelwave_audio/${token}`, location.href).href)) throw new Error('Only verified Telegram Blob URLs or active streams may be played.');
    clearAudio(url); currentUrl = url; audio.src = url;
    try { await new Promise((resolve, reject) => {
        const stream = audioStream(url), started = Date.now();
        if (stream) { stream.lastProgress = started; stream.onError = finish; }
        // Network buffering is part of startup. Allow time for a cold Telegram connection
        // and large metadata tags, while bounding both inactivity and total startup time.
        const timeout = setInterval(() => {
            const idle = Date.now() - (stream?.lastProgress ?? started);
            if (idle >= (stream ? streamIdleTimeout : 15000) || Date.now() - started >= 180000)
                finish(new Error(stream && !stream.received ? 'Telegram has not sent audio yet. Check your connection and retry Stream.' : 'Audio could not start. Retry Stream or download the recording.'));
        }, 1000);
        const ok = () => finish(); const fail = () => finish(new Error(stream?.error ?? 'The browser cannot decode this recording. Try downloading it to play in another app.'));
        cancelAudioLoad = () => finish(new Error('Stream canceled.'));
        function finish(error) {
            clearInterval(timeout); cancelAudioLoad = null;
            if (stream) stream.onError = null;
            audio.removeEventListener('loadedmetadata', ok); audio.removeEventListener('error', fail);
            error ? reject(error) : resolve();
        }
        audio.addEventListener('loadedmetadata', ok, { once: true }); audio.addEventListener('error', fail, { once: true }); audio.load();
    }); } catch (error) { if (currentUrl === url) clearAudio(); throw error; }
}
export async function playAudio() {
    try { await audio.play(); }
    catch (e) { throw new Error(e.name === 'NotAllowedError' ? 'Playback needs your permission. Press Resume to start listening.' : audioStream(currentUrl)?.error ?? 'Playback failed. Retry or choose another recording.'); }
}
export function pauseAudio() { audio.pause(); }
export function releaseUnusedAudio(url) { if (url !== currentUrl) { releaseStream(url); if (url.startsWith('blob:')) URL.revokeObjectURL(url); } }
export async function seekAudio(seconds) {
    if (!Number.isFinite(seconds)) return;
    const target = Math.max(0, Math.min(seconds, audio.duration || seconds));
    if (Math.abs(audio.currentTime - target) < 0.01) return;
    await new Promise((resolve, reject) => {
        const finish = error => { clearTimeout(timer); audio.removeEventListener('seeked', ready); audio.removeEventListener('error', failed); error ? reject(error) : resolve(); };
        const ready = () => finish(); const failed = () => finish(new Error('The recording could not seek to the saved position.'));
        const timer = setTimeout(() => finish(new Error('Seeking timed out. Retry playback.')), 15000);
        audio.addEventListener('seeked', ready); audio.addEventListener('error', failed);
        audio.currentTime = target;
    });
}
export function setAudioMetadata(title, artist, album, artwork) {
    if (!navigator.mediaSession || typeof MediaMetadata !== 'function') return;
    // Each update replaces the whole object, including an empty artwork list for the fallback.
    try {
        navigator.mediaSession.metadata = new MediaMetadata({ title, artist, album,
            artwork: artwork ? [{ src: artwork }] : [] });
    } catch { navigator.mediaSession.metadata = null; }
}
export function clearAudio(preserveUrl) {
    audioObserver?.abort();
    if (navigator.mediaSession) for (const action of ['play', 'pause', 'stop', 'nexttrack', 'previoustrack', 'seekto']) {
        try { navigator.mediaSession.setActionHandler(action, null); } catch { }
    }
    ++streamGeneration;
    cancelAudioLoad?.();
    audio.pause(); audio.removeAttribute('src'); audio.load();
    for (const token of streams.keys()) {
        const url = new URL(`./__channelwave_audio/${token}`, location.href).href;
        if (url !== preserveUrl) releaseStream(url);
    }
    if (currentUrl?.startsWith('blob:')) URL.revokeObjectURL(currentUrl);
    currentUrl = null;
    if (navigator.mediaSession) navigator.mediaSession.metadata = null;
}
export function audioState() {
    return { position: Number.isFinite(audio.currentTime) ? audio.currentTime : 0, duration: Number.isFinite(audio.duration) ? audio.duration : 0,
        buffered: audio.buffered.length ? audio.buffered.end(audio.buffered.length - 1) : 0, paused: audio.paused, ended: audio.ended, volume: audio.volume,
        error: audioStream(currentUrl)?.error ?? (audio.error ? 'Audio playback stopped. Check your connection, then retry Stream.' : null),
        buffering: !audio.paused && audio.readyState < 3, seeking: audio.seeking, muted: audio.muted, rate: audio.playbackRate };
}
export function volumeAudio(value) { audio.volume = Math.max(0, Math.min(1, value)); }

const defaultOutput = { id: '', name: 'System default', remote: false };
let chosenOutput = defaultOutput;

export async function getAudioOutputs() {
    const media = navigator.mediaDevices;
    if (!globalThis.isSecureContext || !media || typeof audio.setSinkId !== 'function' ||
        document.featurePolicy?.allowsFeature('speaker-selection') === false) {
        return { active: defaultOutput, devices: [], help: 'Change the output in your device’s sound settings.' };
    }
    const devices = (await media.enumerateDevices()).filter(device =>
        device.kind === 'audiooutput' && device.deviceId && device.deviceId !== 'default' && device.deviceId !== 'communications');
    return {
        active: chosenOutput,
        devices: [defaultOutput, ...devices.map(device => ({
            id: device.deviceId, name: device.label || 'Audio device', remote: false
        }))],
        picker: typeof media.selectAudioOutput === 'function' ? 'Choose another output' : null,
        help: 'For other speakers or headphones, use your device’s sound settings. Casting Telegram tracks is available in supported native apps.'
    };
}

export async function selectAudioOutput(id) {
    if (id === '' && !audio.sinkId) return null;
    if (typeof audio.setSinkId !== 'function') return 'This browser uses the system audio output.';
    try {
        const generation = streamGeneration;
        const device = id === null ? await navigator.mediaDevices.selectAudioOutput() : null;
        if (generation !== streamGeneration) return null;
        const target = device?.deviceId ?? id;
        await audio.setSinkId(target);
        const label = device?.label ?? (await navigator.mediaDevices.enumerateDevices()).find(item => item.deviceId === target)?.label;
        chosenOutput = target ? { id: target, name: label || 'Audio device', remote: false } : defaultOutput;
        return null;
    } catch (error) {
        if (error.name === 'AbortError') return null;
        if (error.name === 'NotAllowedError') return 'No output was selected. Check browser permissions or use your device’s sound settings.';
        return 'The audio output is unavailable. Refresh the list and try again.';
    }
}

let audioObserver;
export function observeAudio(reference, playbackId) {
    audioObserver?.abort(); audioObserver = new AbortController();
    const observedUrl = currentUrl;
    const send = (action, value = 0) => reference.invokeMethodAsync('OnAudioSignal', playbackId, action, value).catch(() => {});
    navigator.mediaDevices?.addEventListener('devicechange', async () => {
        const selected = chosenOutput;
        if (!selected.id) return;
        const devices = await navigator.mediaDevices.enumerateDevices().catch(() => null);
        if (!devices || chosenOutput !== selected || currentUrl !== observedUrl ||
            devices.some(device => device.kind === 'audiooutput' && device.deviceId === selected.id)) return;
        audio.pause();
        await selectAudioOutput('');
        await send('output-lost');
    }, { signal: audioObserver.signal });
    audio.addEventListener('seeking', () => send('seeking'), { signal: audioObserver.signal });
    audio.addEventListener('ended', () => { if (audio.ended && currentUrl === observedUrl) send('ended'); }, { signal: audioObserver.signal });
    audio.addEventListener('error', () => send(!audioStream(currentUrl)?.error && [3, 4].includes(audio.error?.code) ? 'decode-error' : 'error'), { signal: audioObserver.signal });
    if (!navigator.mediaSession) return;
    for (const action of ['play', 'pause', 'stop', 'nexttrack', 'previoustrack', 'seekto']) {
        try { navigator.mediaSession.setActionHandler(action, details => send(action, details.seekTime ?? 0)); } catch { /* Capability differs by browser. */ }
    }
}

export async function openLibrary() {
    await ownProfile();
    if (database) return;
    database = await new Promise((resolve, reject) => {
        const request = indexedDB.open('channelwave-library', 4);
        request.onupgradeneeded = event => {
            const db = request.result;
            if (event.oldVersion < 1) {
            const tracks = db.createObjectStore('tracks', { keyPath: ['origin.accountId', 'identity'] });
            tracks.createIndex('account', 'origin.accountId'); tracks.createIndex('accountId', ['origin.accountId', 'id'], { unique: true });
            const sources = db.createObjectStore('sources', { keyPath: ['accountId', 'chatId'] }); sources.createIndex('account', 'accountId');
            const state = db.createObjectStore('state', { keyPath: ['account', 'key'] }); state.createIndex('account', 'account');
            }
            if (event.oldVersion < 2) for (const name of ['offlineTracks', 'offlineAudio']) {
                const store = db.createObjectStore(name, { keyPath: ['account', 'id'] });
                store.createIndex('account', 'account');
            }
            if (event.oldVersion < 3) {
                const listens = db.createObjectStore('listens', { keyPath: ['account', 'key'] });
                listens.createIndex('account', 'account');
                listens.createIndex('timeline', ['account', 'lastActive', 'key']);
                listens.createIndex('source', ['account', 'track.chat', 'lastActive', 'key']);
                const buckets = db.createObjectStore('listenBuckets', { keyPath: ['account', 'id'] });
                buckets.createIndex('account', 'account');
                buckets.createIndex('listen', ['account', 'listenId']);
            }
            if (event.oldVersion < 4) {
                const tracks = request.transaction.objectStore('tracks');
                tracks.createIndex('uniqueMedia', ['origin.accountId', 'media.uniqueId', 'media.size']);
                tracks.createIndex('remoteMedia', ['origin.accountId', 'media.remoteId', 'media.size']);
            }
        };
        request.onsuccess = () => { request.result.onversionchange = () => { request.result.close(); database = null; }; resolve(request.result); };
        request.onerror = () => reject(new Error('Local storage is unavailable. Check private-browsing or storage settings.'));
        request.onblocked = () => reject(new Error('A previous tab is blocking the library upgrade. Close it and reload.'));
    });
}
function done(tx) {
    ++pendingLibraryWrites;
    return new Promise((resolve, reject) => {
        let finished = false;
        function finish(error) {
            if (finished) return;
            finished = true; --pendingLibraryWrites;
            error ? reject(error) : resolve();
        }
        tx.oncomplete = () => finish();
        tx.onabort = tx.onerror = () => finish(new Error('Library write failed. Storage may be full or unavailable.'));
    });
}
function result(request) { return new Promise((resolve, reject) => { request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); }); }
export async function savePage(sourceJson, tracksJson, unavailable = [], allUnavailable = false, reconcile = false) {
    await openLibrary(); const source = JSON.parse(sourceJson), tracks = JSON.parse(tracksJson);
    const updated = [];
    const tx = database.transaction(['tracks', 'sources', 'offlineTracks', 'state'], 'readwrite'); const completed = done(tx), store = tx.objectStore('tracks');
    const baselineStore = tx.objectStore('state');
    if (!await result(baselineStore.get([source.accountId, 'library-added-baseline-v1']))) baselineStore.put({ account: source.accountId, key: 'library-added-baseline-v1', value: JSON.stringify(new Date().toISOString()) });
    const indexedAt = new Date().toISOString();
    const previousSource = reconcile ? await result(tx.objectStore('sources').get([source.accountId, source.chatId])) : null;
    if (reconcile && !previousSource) { await completed; return; }
    const renamed = previousSource && previousSource.name !== source.name;
    if (allUnavailable || unavailable.length || renamed) {
        const removed = new Set(unavailable), prefix = `${source.accountId}:${source.chatId}:`;
        const request = store.openCursor(IDBKeyRange.bound([source.accountId, prefix], [source.accountId, prefix + '\uffff']));
        request.onsuccess = () => {
            const cursor = request.result;
            if (!cursor) return;
            if (allUnavailable || removed.has(cursor.value.origin.messageId) || renamed) {
                const track = { ...cursor.value, available: cursor.value.available !== false && !allUnavailable && !removed.has(cursor.value.origin.messageId),
                    origin: { ...cursor.value.origin, sourceName: source.name } };
                track.searchText = normalizeMetadata(`${track.title} ${track.performer} ${source.name}`);
                cursor.update(track); updateOfflineLabels(tx, track); updated.push(track);
            }
            cursor.continue();
        };
    }
    for (const track of tracks) {
        if (track.origin.accountId !== source.accountId || track.origin.chatId !== source.chatId) { tx.abort(); throw new Error('Cross-account/source write rejected.'); }
        const get = store.get([source.accountId, track.identity]);
        get.onsuccess = () => {
            const previous = get.result;
            const sameMedia = previous && sameMetadataMedia(previous, track);
            const merged = localLabels(previous ? { ...track, id: previous.id, favorite: previous.favorite, addedAt: previous.addedAt ?? null,
                available: reconcile ? track.available : previous.available !== false && track.available !== false,
                origin: { ...track.origin, message: mergeMessageInfo(previous.origin.message, track.origin.message) },
                publishedAt: Date.parse(track.publishedAt) > 0 ? track.publishedAt : previous.publishedAt,
                embeddedMetadata: sameMedia && !track.origin.protected ? previous.embeddedMetadata : null,
                metadataScan: sameMedia && previous.origin.protected === track.origin.protected ? previous.metadataScan : null } : { ...track, addedAt: indexedAt }, previous?.titleOverride ?? null, previous?.performerOverride ?? null, true);
            merged.artwork = !track.origin.protected && previous?.artwork?.mediaKey === artworkMediaKey(track) ? previous.artwork : null;
            store.put(merged); updateOfflineLabels(tx, merged);
            updated.push(merged);
        };
    }
    tx.objectStore('sources').put(source); await completed;
    cacheSearchTracks(source.accountId, updated);
}
function mergeMessageInfo(previous, incoming) {
    if (!incoming) return previous ?? null;
    if (!previous) return incoming;
    const senderSnapshot = incoming.isChannelPost != null || incoming.senderType != null;
    const sameSender = !senderSnapshot || incoming.senderType === previous.senderType && incoming.senderId === previous.senderId;
    return { ...incoming,
        isChannelPost: incoming.isChannelPost ?? previous.isChannelPost,
        senderType: senderSnapshot ? incoming.senderType : previous.senderType, senderId: senderSnapshot ? incoming.senderId : previous.senderId,
        senderName: incoming.senderResolved ? incoming.senderName : incoming.senderName ?? (sameSender ? previous.senderName : null),
        senderResolved: !!incoming.senderResolved || sameSender && !!previous.senderResolved,
        authorSignature: incoming.isChannelPost != null ? incoming.authorSignature : incoming.authorSignature ?? previous.authorSignature,
        link: incoming.linkResolved ? incoming.link : incoming.link ?? previous.link,
        linkResolved: !!incoming.linkResolved || !!previous.linkResolved };
}
export async function setMessageInfo(account, id, chat, message, infoJson, publishedAt) {
    await openLibrary(); const info = JSON.parse(infoJson);
    const tx = database.transaction(['tracks', 'offlineTracks'], 'readwrite'), completed = done(tx), store = tx.objectStore('tracks');
    const request = store.index('accountId').get([account, id]); let updated = null;
    request.onsuccess = () => {
        const current = request.result;
        if (!current || current.origin.chatId !== chat || current.origin.messageId !== message) return;
        updated = { ...current, origin: { ...current.origin, message: mergeMessageInfo(current.origin.message, info) },
            publishedAt: Date.parse(publishedAt) > 0 ? publishedAt : current.publishedAt };
        store.put(updated); updateOfflineLabels(tx, updated);
    };
    await completed; if (updated) cacheSearchTracks(account, [updated]); return updated ? JSON.stringify(updated) : null;
}
function normalizeMetadata(value) {
    // Lower each code point to match .NET invariant casing (including Greek sigma).
    return Array.from(value.normalize('NFD'), c => c.toLowerCase()).join('').replace(/\p{Mn}/gu, '').replace(/[يى]/g, 'ی').replace(/ك/g, 'ک').replace(/[\u200c\u200d]/g, ' ').replace(/ـ/g, ' ').replace(/\p{White_Space}+/gu, ' ').replace(/^ | $/g, '');
}
function localLabels(track, title, performer, sourceUpdate = false) {
    // Without overrides, retain the source's serialized labels/search key exactly.
    // Older/minimal rows may contain no media or original-label fields yet.
    if (sourceUpdate && title === null && performer === null && !track.embeddedMetadata) return { ...track, titleOverride: null, performerOverride: null };
    const fileTitle = (track.media?.fileName ?? '').replace(/\\/g, '/').split('/').pop().replace(/\.[^.]*$/, '');
    const originalTitle = track.originalTitle ?? track.title ?? '', originalPerformer = track.originalPerformer ?? track.performer ?? '';
    const updated = { ...track, titleOverride: title, performerOverride: performer,
        title: title ?? (track.embeddedMetadata?.title?.trim() || (sourceUpdate ? track.title : originalTitle.trim() || fileTitle || 'Unknown title')),
        performer: performer ?? (track.embeddedMetadata?.artist?.trim() || (sourceUpdate ? track.performer : originalPerformer.trim() ? originalPerformer : 'Unknown artist')) };
    updated.searchText = normalizeMetadata(`${updated.title} ${updated.performer} ${updated.origin.sourceName ?? ''}`);
    return updated;
}
function updateOfflineLabels(tx, track) {
    const store = tx.objectStore('offlineTracks'), request = store.get([track.origin.accountId, track.id]);
    request.onsuccess = () => {
        if (request.result) store.put({ ...request.result, mediaKey: request.result.mediaKey ?? artworkMediaKey(request.result.track), track });
    };
}
export async function setLocalMetadata(account, id, title, performer) {
    await openLibrary();
    for (const value of [title, performer])
        if (value !== null && (typeof value !== 'string' || !value.trim() || value.length > 1000 || /[\u0000-\u001f\u007f-\u009f]/u.test(value))) throw new Error('Enter a valid local label.');
    const tx = database.transaction(['tracks', 'offlineTracks'], 'readwrite'), completed = done(tx);
    const store = tx.objectStore('tracks'), request = store.index('accountId').get([account, id]); let updated;
    request.onsuccess = () => {
        if (!request.result) { tx.abort(); return; }
        updated = localLabels(request.result, title?.trim() ?? null, performer?.trim() ?? null);
        store.put(updated); updateOfflineLabels(tx, updated);
    };
    await completed; cacheSearchTracks(account, [updated]); return JSON.stringify(updated);
}
function artworkMediaKey(track) { return `${track.media?.uniqueId || track.media?.remoteId || ''}:${track.media?.size ?? 0}`; }
function sharedMetadataKey(track) {
    if (!(track.media?.size > 0)) return null;
    if (track.media.uniqueId) return 'unique:' + artworkMediaKey(track);
    return track.media.remoteId ? 'remote:' + artworkMediaKey(track) : null;
}
function sameMetadataMedia(left, right) {
    return artworkMediaKey(left) === artworkMediaKey(right) && sharedMetadataKey(left) === sharedMetadataKey(right);
}
function reusableMetadata(track, version) {
    const scan = track.metadataScan;
    return !track.origin.protected && scan?.readerVersion === version && scan.mediaKey === artworkMediaKey(track)
        && [0, 1, 2].includes(scan.status) && !wasRestricted(scan);
}
function wasRestricted(scan) { return scan.status === 2 && scan.reason === 'This source restricts saving.'; }
async function metadataMatches(store, track) {
    const { accountId } = track.origin;
    const { uniqueId, remoteId, size } = track.media;
    const shared = sharedMetadataKey(track);
    const rows = shared
        ? await result(store.index(uniqueId ? 'uniqueMedia' : 'remoteMedia').getAll([accountId, uniqueId || remoteId, size]))
        : [await result(store.index('accountId').get([accountId, track.id]))];
    return rows.filter(row => row && !row.origin.protected && sameMetadataMedia(track, row));
}
export async function findEmbeddedMetadata(trackJson, version) {
    await openLibrary();
    const track = JSON.parse(trackJson);
    if (track.origin.protected) return null;
    const matches = await metadataMatches(database.transaction('tracks').objectStore('tracks'), track);
    if (!matches.some(row => row.id === track.id)) return null;
    const cached = matches.filter(row => reusableMetadata(row, version)).sort((left, right) =>
        left.metadataScan.status - right.metadataScan.status ||
        Number(right.artwork?.kind === 0) - Number(left.artwork?.kind === 0))[0];
    if (!cached) return null;
    const artwork = cached.artwork?.kind === 0 && cached.artwork.mediaKey === artworkMediaKey(cached) ? cached.artwork : null;
    return JSON.stringify({ fields: cached.embeddedMetadata ?? null, scan: cached.metadataScan, artwork });
}
export async function saveEmbeddedMetadata(trackJson, metadataJson, version) {
    await openLibrary();
    const track = JSON.parse(trackJson), { fields, scan } = JSON.parse(metadataJson);
    if (!reusableMetadata({ ...track, metadataScan: scan }, version)) throw new Error('Invalid embedded metadata result.');
    const tx = database.transaction(['tracks', 'offlineTracks'], 'readwrite'), completed = done(tx);
    const store = tx.objectStore('tracks'), updated = [];
    const matches = await metadataMatches(store, track);
    if (matches.some(row => row.id === track.id)) {
        for (const row of matches) {
            if (JSON.stringify(row.embeddedMetadata ?? null) === JSON.stringify(fields) &&
                JSON.stringify(row.metadataScan ?? null) === JSON.stringify(scan)) continue;
            const next = localLabels({ ...row, embeddedMetadata: fields, metadataScan: scan }, row.titleOverride ?? null, row.performerOverride ?? null);
            store.put(next); updateOfflineLabels(tx, next); updated.push(next);
        }
    }
    await completed;
    if (updated.length) cacheSearchTracks(track.origin.accountId, updated);
    return JSON.stringify(updated);
}
export async function pendingMetadata(account, now, limit, version) {
    await openLibrary();
    return await new Promise((resolve, reject) => {
        const found = [], request = database.transaction('tracks').objectStore('tracks').index('account').openCursor(IDBKeyRange.only(account));
        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
            const cursor = request.result;
            if (!cursor || found.length >= limit) { resolve(JSON.stringify(found)); return; }
            const track = cursor.value, scan = track.metadataScan;
            if (!track.origin.protected && (!scan || scan.mediaKey !== artworkMediaKey(track) || scan.readerVersion !== version ||
                wasRestricted(scan) || scan.status === 3 && scan.attempts < 3 && scan.retryAt && Date.parse(scan.retryAt) <= now)) found.push(track);
            cursor.continue();
        };
    });
}
export async function setMetadataScan(account, id, mediaKey, metadataJson, scanJson) {
    await openLibrary(); const metadata = JSON.parse(metadataJson), scan = JSON.parse(scanJson);
    if (scan.mediaKey !== mediaKey || scan.readerVersion !== 1 || !Number.isInteger(scan.status) || scan.status < 0 || scan.status > 3) throw new Error('Invalid metadata scan.');
    const tx = database.transaction(['tracks', 'offlineTracks'], 'readwrite'), completed = done(tx), store = tx.objectStore('tracks');
    const request = store.index('accountId').get([account, id]); let updated = null;
    request.onsuccess = () => {
        const current = request.result;
        if (!current || artworkMediaKey(current) !== mediaKey || current.origin.protected && metadata ||
            scan.status === 3 && reusableMetadata(current, scan.readerVersion)) return;
        updated = localLabels({ ...current, embeddedMetadata: metadata, metadataScan: scan }, current.titleOverride ?? null, current.performerOverride ?? null);
        store.put(updated); updateOfflineLabels(tx, updated);
    };
    await completed; if (updated) cacheSearchTracks(account, [updated]); return updated ? JSON.stringify(updated) : null;
}
export async function retryMetadata(account) {
    await openLibrary(); const tx = database.transaction('tracks', 'readwrite'), completed = done(tx);
    const request = tx.objectStore('tracks').index('account').openCursor(IDBKeyRange.only(account));
    request.onsuccess = () => {
        const cursor = request.result;
        if (cursor) { if (cursor.value.metadataScan?.status === 3) cursor.update({ ...cursor.value, metadataScan: null }); cursor.continue(); }
    };
    await completed;
}
export async function setArtwork(account, id, artworkJson) {
    await openLibrary(); const artwork = JSON.parse(artworkJson);
    if (!Number.isInteger(artwork.kind) || artwork.kind < 0 || artwork.kind > 2 || typeof artwork.data !== 'string' || !artwork.data.length || artwork.data.length > 699052) throw new Error('Invalid artwork thumbnail.');
    const tx = database.transaction(['tracks', 'offlineTracks'], 'readwrite'), completed = done(tx), store = tx.objectStore('tracks');
    const request = store.index('accountId').get([account, id]); let updated;
    request.onsuccess = () => {
        const current = request.result;
        if (!current || current.origin.protected || artwork.mediaKey !== artworkMediaKey(current)) { tx.abort(); return; }
        if (current.artwork?.mediaKey === artwork.mediaKey && current.artwork.kind < artwork.kind) { updated = current; return; }
        updated = { ...current, artwork: { ...artwork, cachedAt: Date.now() } }; store.put(updated); updateOfflineLabels(tx, updated);
        const images = [], cursorRequest = store.index('account').openCursor(IDBKeyRange.only(account));
        cursorRequest.onsuccess = () => {
            const cursor = cursorRequest.result;
            if (cursor) { if (cursor.value.artwork) images.push(cursor.value); cursor.continue(); return; }
            let bytes = images.reduce((sum, track) => sum + track.artwork.data.length * 3 / 4, 0), count = images.length;
            for (const old of images.filter(track => track.id !== id).sort((a,b) => a.artwork.cachedAt - b.artwork.cachedAt)) {
                if (count <= 64 && bytes <= 16 * 1024 * 1024) break;
                bytes -= old.artwork.data.length * 3 / 4; count--;
                const evicted = { ...old, artwork: null }; store.put(evicted); updateOfflineLabels(tx, evicted);
            }
        };
    };
    await completed; return JSON.stringify(updated);
}

// Opaque handles retain Blobs in JS; .NET receives only bounded tag ranges.
export async function openTagFile(account, id, fileId, size, mediaKey) {
    const generation = exportGeneration;
    await openLibrary();
    const tx = database.transaction(['tracks', 'offlineAudio', 'offlineTracks'], 'readonly');
    const current = await result(tx.objectStore('tracks').index('accountId').get([account, id]));
    if (!current || current.origin.protected || mediaKey && artworkMediaKey(current) !== mediaKey) throw new Error('This recording is unavailable or restricts saving.');
    const sourceVersion = sourceMediaVersion(account, current.origin.chatId);
    let blob;
    if (fileId === null) {
        const [offline, pin] = await Promise.all([result(tx.objectStore('offlineAudio').get([account, id])),
            result(tx.objectStore('offlineTracks').get([account, id]))]);
        if (!offline || !(offline.blob instanceof Blob) || offline.blob.size !== offline.size || !pin ||
            (pin.mediaKey ?? artworkMediaKey(pin.track)) !== artworkMediaKey(current)) throw new Error('This offline copy is missing, outdated or incomplete.');
        blob = offline.blob;
    } else {
        if (!client) throw new Error('Connect Telegram first.');
        const part = await checked({ '@type': 'readFile', file_id: fileId, offset: 0, count: size }); blob = part.data;
        if (!(blob instanceof Blob) || !blob.size || size > 0 && blob.size !== size) throw new Error('The downloaded file is incomplete.');
    }
    if (generation !== exportGeneration || sourceVersion !== sourceMediaVersion(account, current.origin.chatId)) throw new Error('Tag reading canceled.');
    const token = crypto.randomUUID(); tagFiles.set(token, { account, id, chat: current.origin.chatId, sourceVersion, blob, length: blob.size, generation, mediaKey: artworkMediaKey(current) });
    return { token, length: blob.size };
}
function tagFile(token, account) {
    const file = tagFiles.get(token);
    if (!file || file.account !== account || file.generation !== exportGeneration || file.sourceVersion !== sourceMediaVersion(account, file.chat)) throw new Error('This tag editor has expired. Read tags again.');
    return file;
}
export async function readTagRange(token, account, offset, count) {
    const file = tagFile(token, account);
    if (!Number.isSafeInteger(offset) || !Number.isInteger(count) || offset < 0 || count < 0 || count > 32 * 1024 * 1024 || offset + count > file.length) throw new Error('Invalid metadata range.');
    const blob = file.blob ? file.blob.slice(offset, offset + count) : (await checked({ '@type': 'readFile', file_id: file.fileId, offset, count })).data;
    if (!(blob instanceof Blob) || blob.size !== count) throw new Error('Artwork bytes are not cached yet.');
    const bytes = new Uint8Array(await blob.arrayBuffer());
    tagFile(token, account); return bytes;
}
export function closeTagFile(token) { tagFiles.delete(token); }
export async function openOfflineMetadata(account, id, mediaKey) {
    await openLibrary();
    const tx = database.transaction(['tracks', 'offlineAudio', 'offlineTracks']), store = tx.objectStore('tracks');
    const [track, audio, pin] = await Promise.all([result(store.index('accountId').get([account, id])),
        result(tx.objectStore('offlineAudio').get([account, id])), result(tx.objectStore('offlineTracks').get([account, id]))]);
    if (!track || track.origin.protected || artworkMediaKey(track) !== mediaKey || !audio?.blob || audio.blob.size !== audio.size ||
        !pin || (pin.mediaKey ?? artworkMediaKey(pin.track)) !== mediaKey) return null;
    const token = crypto.randomUUID(); tagFiles.set(token, { account, id, chat: track.origin.chatId, sourceVersion: sourceMediaVersion(account, track.origin.chatId), blob: audio.blob, length: audio.blob.size, generation: exportGeneration });
    return { token, length: audio.blob.size };
}
export async function readMetadataRange(account, id, mediaKey, fileId, offset, count) {
    const generation = exportGeneration;
    await openLibrary();
    const track = await result(database.transaction('tracks').objectStore('tracks').index('accountId').get([account, id]));
    if (!track || track.origin.protected || artworkMediaKey(track) !== mediaKey || !Number.isSafeInteger(offset) || !Number.isInteger(count) || count < 0 || count > 1024 * 1024 || offset < 0 || offset + count > track.media.size) throw new Error('Metadata range is no longer available.');
    const sourceVersion = sourceMediaVersion(account, track.origin.chatId);
    const part = await checked({ '@type': 'readFile', file_id: fileId, offset, count });
    if (!(part.data instanceof Blob) || part.data.size !== count) throw new Error('Incomplete metadata range.');
    const bytes = new Uint8Array(await part.data.arrayBuffer());
    if (generation !== exportGeneration || sourceVersion !== sourceMediaVersion(account, track.origin.chatId)) throw new Error('Metadata reading canceled.');
    return bytes;
}
export async function openCachedTagFile(account, id, fileId, size) {
    const generation = exportGeneration; await openLibrary();
    const current = await result(database.transaction('tracks').objectStore('tracks').index('accountId').get([account, id]));
    if (!current || current.origin.protected || !Number.isSafeInteger(size) || size <= 0 || generation !== exportGeneration) throw new Error('Artwork is unavailable.');
    const token = crypto.randomUUID(); tagFiles.set(token, { account, id, chat: current.origin.chatId, sourceVersion: sourceMediaVersion(account, current.origin.chatId), fileId, length: size, generation });
    return { token, length: size };
}
export async function readArtworkFile(fileId, size) {
    if (!client || size <= 0 || size > 512 * 1024) throw new Error('Unsupported artwork size.');
    const generation = exportGeneration, part = await checked({ '@type': 'readFile', file_id: fileId, offset: 0, count: size });
    if (generation !== exportGeneration || !(part.data instanceof Blob) || part.data.size !== size) throw new Error('Artwork is incomplete.');
    return new Uint8Array(await part.data.arrayBuffer());
}
export async function exportTagCopy(token, account, prefixLength, prefix, replaceLegacy, legacy, name) {
    const file = tagFile(token, account);
    if (!file.blob) throw new Error('A passive artwork reader cannot export audio.');
    const current = await result(database.transaction('tracks').objectStore('tracks').index('accountId').get([account, file.id]));
    if (!current || current.origin.protected || artworkMediaKey(current) !== file.mediaKey) throw new Error('This recording is unavailable or restricts saving.');
    const end = file.blob.size - (replaceLegacy ? 128 : 0);
    if (!Number.isSafeInteger(prefixLength) || prefixLength < 0 || prefixLength > end || prefix.length > 32 * 1024 * 1024 || replaceLegacy && legacy?.length !== 128) throw new Error('Invalid tag boundaries.');
    const parts = [prefix, file.blob.slice(prefixLength, end)];
    if (replaceLegacy) parts.push(legacy);
    const blob = new Blob(parts, { type: 'audio/mpeg' }), url = URL.createObjectURL(blob), anchor = document.createElement('a');
    anchor.href = url; anchor.download = name.replace(/[\\/\u0000-\u001f]/g, '_') || 'tagged.mp3';
    try {
        tagFile(token, account); document.body.append(anchor); anchor.click();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
    } catch (error) { URL.revokeObjectURL(url); throw error; }
    finally { anchor.remove(); }
}
export async function searchLibrary(account, query, offset, limit) {
    await openLibrary(); const store = database.transaction('tracks').objectStore('tracks');
    return await new Promise((resolve, reject) => {
        const found = []; let skipped = 0;
        const request = store.index('account').openCursor(IDBKeyRange.only(account));
        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
            const cursor = request.result;
            if (!cursor || found.length >= limit) { resolve(JSON.stringify(found)); return; }
            if (cursor.value.searchText.includes(query)) { if (skipped++ >= offset) found.push(cursor.value); }
            cursor.continue();
        };
    });
}

const searchCaches = new Map(), searchVersions = new Map();
function searchProjection(track, baseline = null) {
    return { id: track.id, identity: track.identity, chat: track.origin.chatId,
        title: normalizeMetadata(track.title ?? ''), artist: normalizeMetadata(track.performer ?? ''),
        source: normalizeMetadata(track.origin.sourceName ?? ''), duration: track.duration,
        publishedAt: Date.parse(track.publishedAt), favorite: track.favorite,
        addedAt: Date.parse(track.addedAt ?? baseline) || 0, album: normalizeMetadata(track.embeddedMetadata?.album ?? ''),
        artistKey: discoveryArtist(track), albumKey: discoveryAlbum(track), artistLabel: track.performer,
        albumLabel: (track.embeddedMetadata?.album ?? '').trim(),
        disc: discoveryNumber(track.embeddedMetadata?.disc), number: discoveryNumber(track.embeddedMetadata?.track) };
}
function cacheSearchTracks(account, tracks) {
    searchVersions.set(account, (searchVersions.get(account) ?? 0) + 1);
    const cache = searchCaches.get(account);
    if (cache) searchCaches.delete(account);
}
export function clearSearchCache() { searchCaches.clear(); searchVersions.clear(); }
async function searchSnapshot(account) {
    for (;;) {
        if (searchCaches.has(account)) return [...searchCaches.get(account).values()];
        const version = searchVersions.get(account) ?? 0, rows = new Map();
        const baseline = await ensureLibraryBaseline(account);
        let last = null;
        for (;;) {
            // Bounded primary-key batches avoid thousands of cursor round trips
            // and release full records (including any artwork) after projection.
            const range = IDBKeyRange.bound(last === null ? [account] : [account, last], [account, []], last !== null);
            const batch = await result(database.transaction('tracks').objectStore('tracks').getAll(range, 1000));
            for (const track of batch) rows.set(track.id, searchProjection(track, baseline));
            if (batch.length < 1000) break;
            last = batch.at(-1).identity;
        }
        if (version !== (searchVersions.get(account) ?? 0)) continue;
        searchCaches.set(account, rows);
        return [...rows.values()];
    }
}
export async function searchLibraryAdvanced(account, requestJson) {
    await openLibrary();
    const request = JSON.parse(requestJson), criteria = request.criteria;
    if (!criteria.likedOnly && !criteria.savedOnly && !request.groups && request.order == null && request.artistKey == null && request.albumKey == null && request.offlineIds == null && !request.idsOnly && !request.favorites && request.chat === null && request.playlist === null
        && !request.terms.length && !request.titleTerms.length && !request.artistTerms.length
        && criteria.minDuration === null && criteria.maxDuration === null
        && !criteria.publishedFrom && !criteria.publishedBefore) {
        // Compound primary-key order matches shared ordinal Identity order. A
        // cursor advance skips earlier pages without transferring their records.
        const store = database.transaction('tracks').objectStore('tracks');
        return await new Promise((resolve, reject) => {
            const found = [], cursorRequest = store.openCursor(IDBKeyRange.bound([account], [account, []]));
            let advanced = false;
            cursorRequest.onerror = () => reject(cursorRequest.error);
            cursorRequest.onsuccess = () => {
                const cursor = cursorRequest.result;
                if (!cursor) { resolve(JSON.stringify(found)); return; }
                if (!advanced && request.offset > 0) { advanced = true; cursor.advance(request.offset); return; }
                found.push(cursor.value);
                if (found.length >= request.limit) resolve(JSON.stringify(found));
                else cursor.continue();
            };
        });
    }
    const from = criteria.publishedFrom ? Date.parse(criteria.publishedFrom) : null;
    const before = criteria.publishedBefore ? Date.parse(criteria.publishedBefore) : null;
    const order = request.playlist === null ? null : new Map([...new Set(request.playlist)].map((id, index) => [id, index]));
    const snapshot = await searchSnapshot(account), matches = [];
    const offline = request.offlineIds == null ? (criteria.savedOnly ? new Set() : null) : new Set(request.offlineIds);
    for (let i = 0; i < snapshot.length; i++) {
        const row = snapshot[i];
        if ((!(request.favorites || criteria.likedOnly) || row.favorite) && (request.chat === null || row.chat === request.chat)
            && (order === null || order.has(row.id)) && (offline === null || offline.has(row.id))
            && (request.artistKey == null || row.artistKey === request.artistKey) && (request.albumKey == null || row.albumKey === request.albumKey)
            && request.terms.every(term => row.title.includes(term) || row.artist.includes(term) || row.source.includes(term) || row.album.includes(term))
            && request.titleTerms.every(term => row.title.includes(term)) && request.artistTerms.every(term => row.artist.includes(term))
            && (criteria.minDuration === null || row.duration >= criteria.minDuration)
            && (criteria.maxDuration === null || row.duration <= criteria.maxDuration)
            && (from === null || row.publishedAt >= from) && (before === null || row.publishedAt < before)) matches.push(row);
        if (i > 0 && i % 4096 === 0) await new Promise(resolve => setTimeout(resolve, 0));
    }
    // Ordinal comparison matches the C# adapters; localeCompare would not.
    const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
    const generalOrder = request.order ?? (order === null ? null : ({ 4: 0, 5: 1, 6: 3, 7: 5, 8: 7, 9: 8, 10: 9 })[request.sort]);
    matches.sort((a, b) => {
        if (generalOrder != null) {
            const tie = compare(a.identity, b.identity);
            switch (generalOrder) {
                case 0: return b.addedAt - a.addedAt || tie;
                case 1: return (a.addedAt === 0) - (b.addedAt === 0) || a.addedAt - b.addedAt || tie;
                case 2: return compare(a.title, b.title) || tie;
                case 3: return compare(b.title, a.title) || tie;
                case 4: return compare(a.artist, b.artist) || compare(a.title, b.title) || tie;
                case 5: return compare(b.artist, a.artist) || compare(a.title, b.title) || tie;
                case 6: return b.publishedAt - a.publishedAt || tie;
                case 7: return a.publishedAt - b.publishedAt || tie;
                case 8: return (a.duration <= 0) - (b.duration <= 0) || a.duration - b.duration || tie;
                case 9: return (a.duration <= 0) - (b.duration <= 0) || b.duration - a.duration || tie;
                case 10: return (a.disc || (a.number ? 1 : 2147483647)) - (b.disc || (b.number ? 1 : 2147483647))
                    || (a.number || 2147483647) - (b.number || 2147483647) || compare(a.title, b.title) || tie;
            }
        }
        if (order === null) return compare(a.identity, b.identity);
        const custom = order.get(a.id) - order.get(b.id);
        switch (request.sort) {
            case 1: return compare(a.title, b.title) || custom;
            case 2: return compare(a.artist, b.artist) || compare(a.title, b.title) || custom;
            case 3: return b.publishedAt - a.publishedAt || custom;
            default: return custom;
        }
    });
    if (request.groups) {
        const groups = new Map();
        // Identity chooses representative labels/artwork identically to Core.
        for (const row of [...matches].sort((a, b) => compare(a.identity, b.identity))) {
            const album = request.groups === 'albums', key = album ? row.albumKey : row.artistKey;
            if (key == null) continue;
            const group = groups.get(key);
            if (group) group.count++;
            else groups.set(key, { key, label: album ? row.albumLabel : row.artistLabel,
                artist: album ? row.artistLabel : '', count: 1, artworkTrack: row.id });
        }
        const labelTerms = (request.groupText || '').match(/"[^"]*"|[^\s"]+/g)?.map(term => normalizeMetadata(term.replaceAll('"', ''))).filter(Boolean) || [];
        return JSON.stringify([...groups.values()].filter(group => labelTerms.every(term => normalizeMetadata(group.label).includes(term))).sort((a, b) => compare(normalizeMetadata(a.label), normalizeMetadata(b.label)) || compare(a.key, b.key))
            .slice(request.offset, request.offset + request.limit));
    }
    const selected = matches.slice(request.offset, request.offset + request.limit);
    if (request.idsOnly) return JSON.stringify(selected.map(row => row.id));
    const found = [];
    for (let i = 0; i < selected.length; i += 500) {
        const index = database.transaction('tracks').objectStore('tracks').index('accountId');
        const batch = await Promise.all(selected.slice(i, i + 500).map(row => result(index.get([account, row.id]))));
        found.push(...batch.filter(Boolean));
    }
    return JSON.stringify(found);
}
export async function readTrack(account, id) {
    await openLibrary();
    const track = await result(database.transaction('tracks').objectStore('tracks').index('accountId').get([account, id]));
    return track ? JSON.stringify(track) : null;
}
export async function sourceMessageIds(account, chat, before) {
    await openLibrary();
    const rows = await searchSnapshot(account);
    return rows.filter(row => row.chat === chat).map(row => Number(row.identity.split(':').at(-1)))
        .filter(id => !before || id < before).sort((a, b) => b - a).slice(0, 100);
}
export async function readSources(account) { await openLibrary(); return JSON.stringify(await result(database.transaction('sources').objectStore('sources').index('account').getAll(account))); }
export async function readSource(account, chat) { await openLibrary(); const source = await result(database.transaction('sources').objectStore('sources').get([account, chat])); return source ? JSON.stringify(source) : null; }
export async function removeSource(account, chat) {
    // The UI cancels transfers belonging to this source before removal. Global
    // invalidation here would also cancel exports and saves from other channels.
    sourceMediaVersions.set(`${account}:${chat}`, sourceMediaVersion(account, chat) + 1);
    for (const [token, file] of tagFiles) if (file.account === account && file.chat === chat) tagFiles.delete(token);
    await openLibrary();
    const tx = database.transaction(['tracks', 'sources', 'state', 'offlineTracks', 'offlineAudio'], 'readwrite'), completed = done(tx);
    const removed = [], prefix = `${account}:${chat}:`;
    const request = tx.objectStore('tracks').openCursor(IDBKeyRange.bound([account, prefix], [account, prefix + '\uffff']));
    tx.objectStore('sources').delete([account, chat]);
    request.onsuccess = () => {
        const cursor = request.result;
        if (cursor) {
            removed.push(cursor.value.id);
            for (const name of ['offlineTracks', 'offlineAudio']) tx.objectStore(name).delete([account, cursor.value.id]);
            cursor.delete(); cursor.continue(); return;
        }
        const store = tx.objectStore('state'), playlistRequest = store.get([account, 'playlist']);
        playlistRequest.onsuccess = () => {
            if (!playlistRequest.result) return;
            try {
                const row = playlistRequest.result, playlist = JSON.parse(row.value), ids = new Set(removed);
                const key = Object.hasOwn(playlist, 'Tracks') ? 'Tracks' : 'tracks';
                playlist[key] = playlist[key].filter(id => !ids.has(id));
                store.put({ ...row, value: JSON.stringify(playlist) });
            } catch { tx.abort(); }
        };
        const collectionsRequest = store.get([account, 'playlists']);
        collectionsRequest.onsuccess = () => {
            if (!collectionsRequest.result) return;
            try {
                const row = collectionsRequest.result, playlists = JSON.parse(row.value), ids = new Set(removed);
                for (const playlist of playlists) {
                    const key = Object.hasOwn(playlist, 'Tracks') ? 'Tracks' : 'tracks';
                    playlist[key] = [...new Set(playlist[key])].filter(id => !ids.has(id));
                }
                store.put({ ...row, value: JSON.stringify(playlists) });
            } catch { tx.abort(); }
        };
    };
    await completed;
    cacheSearchTracks(account, []);
    const cached = searchCaches.get(account);
    if (cached) for (const id of removed) cached.delete(id);
    return JSON.stringify(removed);
}
export async function favorite(account, id, value) {
    await openLibrary(); const tx = database.transaction(['tracks', 'offlineTracks'], 'readwrite'), completed = done(tx), store = tx.objectStore('tracks');
    const request = store.index('accountId').get([account, id]); let updated;
    request.onsuccess = () => {
        if (!request.result) { tx.abort(); return; }
        updated = { ...request.result, favorite: value }; store.put(updated);
        const offline = tx.objectStore('offlineTracks'), saved = offline.get([account, id]);
        saved.onsuccess = () => { if (saved.result) offline.put({ ...saved.result, track: { ...saved.result.track, favorite: value } }); };
    }; await completed; cacheSearchTracks(account, [updated]);
}
export async function saveState(account, key, value) { await openLibrary(); const tx = database.transaction('state','readwrite'), completed = done(tx); tx.objectStore('state').put({account,key,value}); await completed; }
function listeningTransaction(stores) {
    try { return database.transaction(stores, 'readwrite', { durability: 'strict' }); }
    catch (error) { if (!(error instanceof TypeError)) throw error; return database.transaction(stores, 'readwrite'); }
}
async function listeningControl(tx, account) {
    const row = await result(tx.objectStore('state').get([account, 'listening-v1']));
    return row ? JSON.parse(row.value) : { revision: 0, generation: 0, preferences: null, player: null, previousPlayer: null };
}
export async function readListeningState(account) {
    await openLibrary(); return JSON.stringify(await listeningControl(database.transaction('state'), account));
}
export async function commitListening(json) {
    await openLibrary(); const { commit, buckets } = JSON.parse(json), account = commit.account;
    if (!(account > 0) || commit.events.some(e => e.account !== account || e.track.account !== account)) throw new Error('Cross-account listening write rejected.');
    const tx = listeningTransaction(['state', 'listens', 'listenBuckets']), completed = done(tx);
    try {
        const previous = await listeningControl(tx, account);
        if (previous.revision !== commit.expectedRevision || previous.generation !== commit.generation) throw new Error('Listening storage changed. Reload before writing again.');
        for (const listen of commit.events) {
            tx.objectStore('listens').put({ ...listen, key: listen.id.replaceAll('-', ''), search: normalizeMetadata(`${listen.track.title} ${listen.track.artist} ${listen.track.source}`) });
            const request = tx.objectStore('listenBuckets').index('listen').openCursor(IDBKeyRange.only([account, listen.id]));
            request.onsuccess = () => { const cursor = request.result; if (cursor) { cursor.delete(); cursor.continue(); } else {
                for (const bucket of buckets.filter(b => b.listenId === listen.id)) tx.objectStore('listenBuckets').put(bucket);
            } };
        }
        const next = { ...previous, revision: previous.revision + 1, preferences: commit.preferences,
            player: commit.forgetPlayer ? null : commit.player, previousPlayer: commit.forgetPlayer ? null : previous.player };
        tx.objectStore('state').put({ account, key: 'listening-v1', value: JSON.stringify(next) });
        await completed; return next.revision;
    } catch (error) { try { tx.abort(); } catch {} await completed.catch(() => {}); throw error; }
}
export async function readListen(account, id) {
    await openLibrary(); const row = await result(database.transaction('listens').objectStore('listens').get([account, id]));
    return row ? JSON.stringify(row) : null;
}
export async function listeningHistory(account, json) {
    await openLibrary(); const query = JSON.parse(json), limit = Math.max(1, Math.min(100, query.limit)), rows = [];
    const tx = database.transaction('listens'), bySource = query.source != null;
    const index = tx.objectStore('listens').index(bySource ? 'source' : 'timeline');
    const prefix = bySource ? [account, query.source] : [account];
    const lower = [...prefix, query.from, ''];
    const upper = [...prefix, query.before?.time ?? Number.MAX_VALUE, query.before?.id ?? '\uffff'];
    const tokens = query.terms ?? (query.search ?? '').match(/"[^"]*"|[^\s"]+/g)?.map(token => normalizeMetadata(token.replaceAll('"', ''))).filter(Boolean) ?? [];
    if (lower[lower.length - 2] > upper[upper.length - 2]) return JSON.stringify({ items: [], next: null });
    await new Promise((resolve, reject) => {
        const request = index.openCursor(IDBKeyRange.bound(lower, upper, false, !!query.before), 'prev');
        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
            const cursor = request.result; if (!cursor || rows.length > limit) { resolve(); return; }
            const row = cursor.value;
            if (row.started < query.to && (query.track == null || row.track.id === query.track)
                && (query.qualified == null || (row.qualifiedAt != null) === query.qualified)
                && tokens.every(token => row.search.includes(token))) rows.push(row);
            cursor.continue();
        };
    });
    const page = rows.slice(0, limit), last = page.at(-1);
    return JSON.stringify({ items: page, next: rows.length > limit ? { time: last.lastActive, id: last.key } : null });
}
export async function listeningBuckets(account, from, to, after) {
    await openLibrary(); const rows = [], store = database.transaction('listenBuckets').objectStore('listenBuckets');
    if (to <= from) return '[]';
    const first = after ?? `${String(from).padStart(16, '0')}:`;
    const range = IDBKeyRange.bound([account, first], [account, `${String(to).padStart(16, '0')}:`], !!after, true);
    await new Promise((resolve, reject) => {
        const request = store.openCursor(range);
        request.onerror = () => reject(request.error);
        request.onsuccess = () => { const cursor = request.result; if (!cursor || rows.length === 500) { resolve(); return; } rows.push(cursor.value); cursor.continue(); };
    });
    return JSON.stringify(rows);
}
export async function deleteListening(account, id, olderThan) {
    await openLibrary(); const tx = listeningTransaction(['state', 'listens', 'listenBuckets']), completed = done(tx);
    try {
        const state = await listeningControl(tx, account);
        const request = tx.objectStore('listens').index('account').openCursor(IDBKeyRange.only(account));
        request.onsuccess = () => {
            const cursor = request.result; if (!cursor) return;
            const row = cursor.value;
            if ((id == null || row.key === id) && (olderThan == null || row.lastActive < olderThan)) {
                cursor.delete();
                const buckets = tx.objectStore('listenBuckets').index('listen').openCursor(IDBKeyRange.only([account, row.id]));
                buckets.onsuccess = () => { const bucket = buckets.result; if (bucket) { bucket.delete(); bucket.continue(); } };
            }
            cursor.continue();
        };
        // Invalidate every pending old-generation commit. The coordinator writes the
        // suppressed active token back before playback recording can resume.
        state.revision++; state.generation++;
        state.previousPlayer = null;
        if (olderThan == null && state.player) {
            const snapshot = JSON.parse(state.player.json);
            if (id == null || snapshot.activeListen?.replaceAll('-', '') === id) {
                // Keep playback data, but never restore a deleted listening attempt.
                state.suppressedListen = snapshot.activeListen;
                // The live coordinator immediately saves a new checksummed snapshot.
            }
        }
        tx.objectStore('state').put({ account, key: 'listening-v1', value: JSON.stringify(state) });
        await completed; return JSON.stringify(state);
    } catch (error) { try { tx.abort(); } catch {} await completed.catch(() => {}); throw error; }
}
export async function readState(account, key) { await openLibrary(); return (await result(database.transaction('state').objectStore('state').get([account,key])))?.value ?? null; }
export async function clearAccount(account) {
    cancelMediaWork();
    await openLibrary(); const tx = database.transaction(['tracks','sources','state','offlineTracks','offlineAudio','listens','listenBuckets'],'readwrite'), completed = done(tx);
    for (const name of ['tracks','sources','state','offlineTracks','offlineAudio','listens','listenBuckets']) {
        const request = tx.objectStore(name).index('account').openCursor(IDBKeyRange.only(account));
        request.onsuccess = () => { const cursor = request.result; if (cursor) { cursor.delete(); cursor.continue(); } };
    }
    const resume = tx.objectStore('state').get([0, 'last-listening-account-v1']);
    resume.onsuccess = () => {
        if (resume.result?.value === String(account)) tx.objectStore('state').delete([0, 'last-listening-account-v1']);
    };
    await completed;
    cacheSearchTracks(account, []); searchCaches.delete(account);
}

// App-owned pins are independent of TDLib's evictable transfer cache. Keep metadata
// separate so opening a collection never reads every audio Blob into memory.
export async function offlineTracks(account) {
    await openLibrary();
    const rows = await result(database.transaction('offlineTracks').objectStore('offlineTracks').getAll());
    // A disconnected launch opens only the most recently saved account's collection.
    account ??= rows.sort((a, b) => b.savedAt - a.savedAt)[0]?.account;
    return JSON.stringify(rows.filter(row => row.account === account).map(row => row.track));
}
export async function offlineCopies(account) {
    const tracks = JSON.parse(await offlineTracks(account));
    const tx = database.transaction(['offlineTracks', 'offlineAudio']);
    const rows = await Promise.all(tracks.map(async track => {
        const key = [track.origin.accountId, track.id];
        const [saved, audio] = await Promise.all([result(tx.objectStore('offlineTracks').get(key)), result(tx.objectStore('offlineAudio').get(key))]);
        return { track, mediaKey: saved.mediaKey ?? artworkMediaKey(track), bytes: audio?.size ?? 0, hash: '',
            savedAt: new Date(saved.savedAt).toISOString(), health: !audio ? 1 : !(audio.blob instanceof Blob) || audio.blob.size !== audio.size || audio.size <= 0 ? 2 : 0 };
    }));
    return JSON.stringify(rows);
}
export async function storageUsage(account) {
    await openLibrary();
    const tx = database.transaction(['offlineAudio', 'tracks', 'sources', 'state']);
    const [audioRows, tracks, sources, states] = await Promise.all([
        result(tx.objectStore('offlineAudio').index('account').getAll(account)),
        result(tx.objectStore('tracks').index('account').getAll(account)),
        result(tx.objectStore('sources').index('account').getAll(account)),
        result(tx.objectStore('state').index('account').getAll(account))
    ]);
    const offlineBytes = audioRows.reduce((sum, row) => sum + (row.blob?.size ?? 0), 0);
    const cacheRow = states.find(row => row.key === 'media-cache:v1');
    let temporaryBytes = 0;
    if (cacheRow) for (const entry of JSON.parse(cacheRow.value)) {
        // Runtime file IDs are not durable: query the source message to measure loaded bytes.
        if (!client) continue;
        const track = entry.Track;
        try {
            const message = await checked({ '@type': 'getMessage', chat_id: track.Origin.ChatId, message_id: track.Origin.MessageId });
            temporaryBytes += message.content?.audio?.audio?.local?.downloaded_size ?? 0;
        } catch { /* A disconnected measurement is incomplete and remains explicitly estimated. */ }
    }
    const estimate = await navigator.storage?.estimate?.().catch(() => null);
    return { offlineBytes, temporaryBytes, otherBytes: new Blob([JSON.stringify({ tracks, sources, states })]).size,
        total: 0, siteUsage: estimate?.usage ?? null, siteQuota: estimate?.quota ?? null, estimated: true, protectedBytes: 0 };
}
export async function saveOffline(trackJson, fileId, size, operation = null) {
    try { return await saveOfflineCore(trackJson, fileId, size, operation); }
    finally { if (operation) canceledTransfers.delete(operation); }
}
async function saveOfflineCore(trackJson, fileId, size, operation) {
    const generation = offlineWriteGeneration;
    const track = JSON.parse(trackJson);
    const sourceVersion = sourceMediaVersion(track.origin.accountId, track.origin.chatId);
    if (track.origin.protected) throw new Error('This source restricts saving.');
    if (!client) throw new Error('Connect Telegram to save this song offline.');
    const part = await checked({ '@type': 'readFile', file_id: fileId, offset: 0, count: size });
    if (!(part.data instanceof Blob) || !part.data.size || size > 0 && part.data.size !== size)
        throw new Error(`Telegram supplied ${part.data?.size ?? 0} of ${size || 'unknown'} audio bytes. Retry saving offline.`);
    const registration = await window.channelwaveOfflineReady;
    if (!registration?.active) throw new Error('The app could not prepare offline startup. Reconnect and reload, then try saving again.');
    await openLibrary();
    if (canceledTransfers.has(operation) || generation !== offlineWriteGeneration || sourceVersion !== sourceMediaVersion(track.origin.accountId, track.origin.chatId)) throw new Error('Saving offline was canceled.');
    const tx = database.transaction(['tracks', 'offlineTracks', 'offlineAudio'], 'readwrite'), completed = done(tx);
    const key = { account: track.origin.accountId, id: track.id };
    // The download can outlive favorite/metadata edits. Snapshot the current row in
    // the same transaction as the audio so the offline collection never regresses it.
    const current = tx.objectStore('tracks').index('accountId').get([key.account, key.id]);
    current.onsuccess = () => {
        if (!current.result || current.result.origin.chatId !== track.origin.chatId) { tx.abort(); return; }
        if (artworkMediaKey(current.result) !== artworkMediaKey(track) || current.result.origin.protected) { tx.abort(); return; }
        tx.objectStore('offlineTracks').put({ ...key, track: current.result, mediaKey: artworkMediaKey(current.result), savedAt: Date.now() });
        tx.objectStore('offlineAudio').put({ ...key, blob: part.data, size: part.data.size });
    };
    await completed;
    // Best effort: browsers may still evict data, especially in private mode.
    navigator.storage?.persist?.().catch(() => {});
}
export async function openOffline(account, id, mediaKey = null) {
    await openLibrary();
    const saved = await result(database.transaction('offlineTracks').objectStore('offlineTracks').get([account, id]));
    if (!saved || saved.track.origin.protected || mediaKey && (saved.mediaKey ?? artworkMediaKey(saved.track)) !== mediaKey) return null;
    const row = await result(database.transaction('offlineAudio').objectStore('offlineAudio').get([account, id]));
    if (!row) return null;
    if (!(row.blob instanceof Blob) || !row.size || row.blob.size !== row.size)
        throw new Error('This offline copy is incomplete. Remove it and save the song again while connected.');
    return { location: URL.createObjectURL(row.blob), isBrowserUrl: true, bytes: row.blob.size };
}
export async function removeOffline(account, id) {
    await openLibrary();
    const tx = database.transaction(['offlineTracks', 'offlineAudio'], 'readwrite'), completed = done(tx);
    for (const name of ['offlineTracks', 'offlineAudio']) tx.objectStore(name).delete([account, id]);
    await completed;
}

function discoveryArtist(track) {
    if (![track.performerOverride, track.embeddedMetadata?.artist, track.originalPerformer].some(value => value?.trim())) return null;
    return normalizeMetadata(track.performer ?? '') || null;
}
function discoveryAlbum(track) {
    const artist = discoveryArtist(track), album = normalizeMetadata(track.embeddedMetadata?.album ?? '');
    return artist && album ? `${artist.length}:${artist}${album}` : null;
}
function discoveryNumber(value) { const part = (value ?? '').split('/')[0].trim(); return /^\+?\d+$/.test(part) && Number(part) <= 2147483647 ? Number(part) : 0; }
export async function ensureLibraryBaseline(account) {
    await openLibrary(); const tx = database.transaction('state', 'readwrite'), completed = done(tx), store = tx.objectStore('state');
    let baseline = (await result(store.get([account, 'library-added-baseline-v1'])))?.value;
    if (!baseline) { baseline = JSON.stringify(new Date().toISOString()); store.put({ account, key: 'library-added-baseline-v1', value: baseline }); }
    await completed; return JSON.parse(baseline);
}
export async function recentListeningTracks(account, limit = 6, offset = 0) {
    await openLibrary(); const found = new Map();
    await new Promise((resolve, reject) => {
        const request = database.transaction('listens').objectStore('listens').index('timeline')
            .openCursor(IDBKeyRange.bound([account], [account, []]), 'prev');
        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
            const cursor = request.result;
            if (!cursor) { resolve(); return; }
            const listen = cursor.value;
            if (found.size >= offset + limit && listen.lastActive < [...found.values()].at(-1).lastActive) { resolve(); return; }
            if (listen.activeMilliseconds > 0 && !found.has(listen.track.id)) found.set(listen.track.id, { trackId: listen.track.id, lastActive: listen.lastActive, reference: listen.track });
            cursor.continue();
        };
    });
    return JSON.stringify([...found.values()].sort((a,b) => b.lastActive - a.lastActive || (a.trackId < b.trackId ? -1 : 1)).slice(offset, offset + limit));
}

export async function libraryOverview(account) {
    await openLibrary();
    const rows = await searchSnapshot(account);
    return JSON.stringify({ tracks: rows.length, artists: new Set(rows.map(r => r.artistKey).filter(Boolean)).size,
        albums: new Set(rows.map(r => r.albumKey).filter(Boolean)).size });
}
