// Updates replace only the public app shell. IndexedDB, saved audio and Telegram
// sessions are never cleared. A page participates only after this script loads.
(() => {
    if (window.channelwaveOfflineReady) return;
    let heldUntil = 0, lastInteraction = Date.now(), reloading = false;
    let controller = navigator.serviceWorker?.controller;
    function idle() {
        if (Date.now() - lastInteraction < 1500 || document.readyState !== 'complete') return false;
        if (document.querySelector('dialog[open], .playlist-editor, .music-transfer, [aria-busy="true"], [data-update-busy="true"]')) return false;
        if (document.activeElement?.matches('input, textarea, [contenteditable="true"]')) return false;
        return window.dispatchEvent(new Event('channelwave-before-update', { cancelable: true }));
    }
    navigator.serviceWorker?.addEventListener('message', event => {
        if (event.data?.type === 'channelwave-update-release') { heldUntil = 0; return; }
        if (event.data?.type !== 'channelwave-update-ready' || !event.ports[0]) return;
        const ready = idle();
        // First installation claims this page without reloading it. Only an
        // existing controller needs to hold actions while its replacement starts.
        const current = navigator.serviceWorker.controller;
        if (ready && event.data.hold && current && event.source && event.source !== current)
            heldUntil = Date.now() + 5000;
        event.ports[0].postMessage({ ready }); event.ports[0].close();
    });
    // Hold new user actions briefly between the final readiness vote and reload.
    // A lease releases the hold if activation fails or another tab vetoes it.
    for (const type of ['pointerdown', 'keydown', 'click', 'beforeinput', 'submit']) {
        document.addEventListener(type, event => {
            if (Date.now() < heldUntil) { event.preventDefault(); event.stopImmediatePropagation(); }
            else lastInteraction = Date.now();
        }, true);
    }
    navigator.serviceWorker?.addEventListener('controllerchange', () => {
        const next = navigator.serviceWorker.controller;
        heldUntil = 0;
        if (controller && next && next !== controller && !reloading) {
            reloading = true; location.reload();
        }
        controller = next;
    });
    window.channelwaveOfflineReady = (async () => {
        if (!navigator.serviceWorker) return null;
        try {
            const registration = await navigator.serviceWorker.register('./audio-worker.js', { updateViaCache: 'none' });
            const activate = () => {
                if (navigator.serviceWorker.controller && registration.waiting && idle())
                    registration.waiting.postMessage({ type: 'channelwave-activate-update' });
            };
            const watch = worker => {
                if (!worker) return;
                worker.addEventListener('statechange', () => { if (worker.state === 'installed') activate(); });
            };
            registration.addEventListener('updatefound', () => watch(registration.installing));
            watch(registration.installing);
            let checking;
            const check = () => checking ??= registration.update().catch(() => {}).finally(() => { checking = null; activate(); });
            window.addEventListener('online', check);
            window.addEventListener('focus', check);
            document.addEventListener('visibilitychange', () => { if (!document.hidden) check(); });
            setInterval(check, 60000);
            setInterval(activate, 5000);
            if (registration.active) check();
            activate();
            // Do not wait on an update while the active shell supports offline saves.
            if (registration.active) return registration;
            const worker = registration.installing || registration.waiting;
            if (worker && worker.state !== 'activated') await new Promise((resolve, reject) => {
                const timer = setTimeout(() => finish(new Error('Offline setup timed out.')), 60000);
                function finish(error) {
                    clearTimeout(timer); worker.removeEventListener('statechange', changed);
                    error ? reject(error) : resolve();
                }
                function changed() {
                    if (worker.state === 'activated') finish();
                    if (worker.state === 'redundant') finish(new Error('Offline setup failed.'));
                }
                worker.addEventListener('statechange', changed); changed();
            });
            return registration.active ? registration : null;
        } catch { return null; }
    })();
})();
